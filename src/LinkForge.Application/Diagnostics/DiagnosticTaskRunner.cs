using LinkForge.Domain.Diagnostics;
using LinkForge.Domain.Environment;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Application.Diagnostics;

/// <summary>
/// 诊断任务编排器（LF-003）。
/// </summary>
/// <remarks>
/// <para>
/// 这是全应用<b>唯一</b>执行诊断步骤的地方。把取消、超时、依赖跳过、异常映射
/// 集中在一处，是为了让这些跨模块规则只实现一遍：
/// 若让每个适配器自己处理超时，二十个适配器就会有二十种“超时算失败还是算超时”的口径。
/// </para>
/// <para>
/// 三条状态红线（ADR-016、ADR-019）：
/// <list type="number">
///   <item>未执行的步骤标记 <see cref="DiagnosticStatus.Skipped"/> 并写明原因，绝不标为 Failed。</item>
///   <item>用户取消与超时是两种不同结论（Cancelled / TimedOut），不得合并。</item>
///   <item>编排器不猜结论：适配器返回什么状态就记录什么状态。</item>
/// </list>
/// </para>
/// <para>
/// 线程模型：本类内的等待全部是异步的，唯一的同步工作是内存中的列表操作，不涉及 I/O。
/// 进度通过 <see cref="IProgress{T}"/> 发出，由调用方决定回到哪个线程
/// （WPF 场景由 <c>Progress&lt;T&gt;</c> 自动回到 UI 线程）。
/// </para>
/// </remarks>
public sealed class DiagnosticTaskRunner
{
    private readonly TimeProvider _timeProvider;
    private readonly ITextRedactor _redactor;

    /// <summary>
    /// 创建编排器。
    /// </summary>
    /// <param name="redactor">
    /// 脱敏实现，不得为 null：任务结论会进入日志、历史与报告，
    /// 允许为 null 就等于允许某条路径忘掉脱敏。
    /// </param>
    /// <param name="timeProvider">时间源；null 时使用系统时钟（测试可注入假时钟）。</param>
    public DiagnosticTaskRunner(ITextRedactor redactor, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(redactor);

        _redactor = redactor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 执行一次诊断任务。
    /// </summary>
    /// <param name="adapter">诊断适配器。</param>
    /// <param name="request">诊断请求。</param>
    /// <param name="dependencies">当前环境的依赖探测结果（用于依赖跳过判定）。</param>
    /// <param name="options">执行选项。</param>
    /// <param name="progress">进度上报器；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务结果；永不为 null。</returns>
    /// <remarks>
    /// 本方法不把业务失败表达为异常：适配器抛出的异常会被映射为步骤的
    /// <see cref="DiagnosticStatus.Failed"/> 并附可读原因。
    /// 只有参数为 null 这类调用错误才会抛出。
    /// </remarks>
    public async Task<DiagnosticRunResult> RunAsync(
        IDiagnosticAdapter adapter,
        DiagnosticRequest request,
        IReadOnlyCollection<DependencyProbe> dependencies,
        DiagnosticRunOptions options,
        IProgress<DiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(options);

        options.Validated();

        var startedAt = _timeProvider.GetUtcNow();
        var plans = adapter.Plan(request) ?? [];
        var results = new List<DiagnosticStepResult>(plans.Count);
        var unavailable = dependencies.Where(static d => !d.IsAvailable).ToArray();
        var wasCancelled = false;

        using var overallCts = options.OverallTimeout is { } overall
            ? new CancellationTokenSource(overall, _timeProvider)
            : null;

        // 把“用户取消”与“任务总超时”合并成一个令牌：步骤内部只需响应一个信号，
        // 但两者在结果里必须区分，因此分别记住来源。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            overallCts?.Token ?? CancellationToken.None);

        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];

            if (linked.IsCancellationRequested)
            {
                var cancelledByUser = cancellationToken.IsCancellationRequested;

                if (cancelledByUser)
                {
                    wasCancelled = true;
                }

                // 剩余步骤全部记为“未执行”，并说明原因。
                // 不能记 Failed：它们确实没有被执行，也不代表有问题。
                for (var rest = index; rest < plans.Count; rest++)
                {
                    results.Add(DiagnosticStepResults.Skipped(
                        plans[rest].StepId,
                        plans[rest].Name,
                        cancelledByUser
                            ? "任务已被取消，该步骤未执行"
                            : "任务已超过总超时，该步骤未执行"));
                }

                break;
            }

            var blocked = FindBlockingReason(plan, results, unavailable);

            if (blocked is not null)
            {
                results.Add(DiagnosticStepResults.Skipped(plan.StepId, plan.Name, blocked));
                Report(progress, request.RunId, plan.StepId, DiagnosticStepPhase.Finished, _redactor.Redact(blocked) ?? blocked, startedAt);
                continue;
            }

            var stepResult = await ExecuteStepAsync(
                    adapter, request, plan, options, progress, linked.Token, cancellationToken, startedAt)
                .ConfigureAwait(false);

            if (stepResult.Status == DiagnosticStatus.Cancelled)
            {
                wasCancelled = true;
            }

            results.Add(stepResult);
        }

        return new DiagnosticRunResult
        {
            RunId = request.RunId,
            AdapterId = adapter.Id,
            DisplayName = adapter.DisplayName,
            TargetLabel = request.TargetLabel,
            StartedAt = startedAt,
            EndedAt = _timeProvider.GetUtcNow(),
            Steps = results,
            WasCancelled = wasCancelled
        };
    }

    /// <summary>
    /// 判断步骤是否必须跳过，并给出原因。
    /// </summary>
    /// <param name="plan">步骤计划。</param>
    /// <param name="completed">已完成的步骤结论。</param>
    /// <param name="unavailable">不可用的依赖。</param>
    /// <returns>跳过原因；不需要跳过时返回 null。</returns>
    /// <remarks>
    /// 顺序有意为之：先判依赖、再判前置步骤。
    /// 依赖缺失是环境问题（用户需要装东西），前置失败是任务本身的问题
    /// （用户需要看前面的结论）；先报环境问题更贴近用户的下一步动作。
    /// </remarks>
    private static string? FindBlockingReason(
        DiagnosticStepPlan plan,
        IReadOnlyList<DiagnosticStepResult> completed,
        IReadOnlyList<DependencyProbe> unavailable)
    {
        foreach (var dependencyId in plan.Dependencies)
        {
            foreach (var probe in unavailable)
            {
                if (string.Equals(probe.Id, dependencyId, StringComparison.OrdinalIgnoreCase))
                {
                    return $"缺少 {probe.Name}（{probe.Detail}）";
                }
            }
        }

        foreach (var prerequisiteId in plan.Prerequisites)
        {
            var prerequisite = completed.FirstOrDefault(
                step => string.Equals(step.StepId, prerequisiteId, StringComparison.Ordinal));

            // 前置步骤不存在（计划写错）时同样跳过并说明：
            // 静默执行会让“依赖顺序写错”这类缺陷永远查不出来。
            if (prerequisite is null)
            {
                return $"前置步骤 {prerequisiteId} 不存在，无法确认执行条件";
            }

            if (prerequisite.Status is not (DiagnosticStatus.Passed or DiagnosticStatus.Warning))
            {
                return $"前置步骤“{prerequisite.Name}”未通过（{DescribeStatus(prerequisite.Status)}）";
            }
        }

        return null;
    }

    /// <summary>
    /// 执行单个步骤，并把所有异常与取消映射为结论。
    /// </summary>
    private async Task<DiagnosticStepResult> ExecuteStepAsync(
        IDiagnosticAdapter adapter,
        DiagnosticRequest request,
        DiagnosticStepPlan plan,
        DiagnosticRunOptions options,
        IProgress<DiagnosticProgress>? progress,
        CancellationToken linkedToken,
        CancellationToken userToken,
        DateTimeOffset runStartedAt)
    {
        var timeout = plan.Timeout ?? request.DefaultStepTimeout ?? options.DefaultStepTimeout;
        var startedAt = _timeProvider.GetUtcNow();

        Report(progress, request.RunId, plan.StepId, DiagnosticStepPhase.Starting, $"开始执行：{plan.Name}", runStartedAt);

        // 单步超时与外部取消各占一个来源，之后靠 IsCancellationRequested 区分。
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken, timeoutCts.Token);

        var context = new DiagnosticStepContext(
            request.RunId,
            plan.StepId,
            plan.Name,
            request.TargetLabel,
            request.ParameterMap,
            startedAt);

        // 用同步转发的包装器而不是 Progress<T>：
        // Progress<T> 会异步投递到构造时捕获的上下文，导致进度消息乱序、
        // 且“Finished”事件可能晚于任务结束才到达 UI。
        // 这里只补 RunId 并同步转发，线程归属仍由调用方注入的 IProgress 决定。
        var stepProgress = progress is null
            ? null
            : new StepProgressRelay(progress, request.RunId, plan.StepId, runStartedAt);

        try
        {
            var result = await adapter
                .ExecuteStepAsync(context, stepProgress!, stepCts.Token)
                .ConfigureAwait(false);

            // 必须先脱敏再上报进度：进度事件会立刻进入 UI、日志面板与任务历史，
            // 若先 Report 后 Redact，明文在报告之前就已经出去了。
            var redacted = Redact(result, startedAt, _timeProvider.GetUtcNow() - startedAt);

            Report(
                progress,
                request.RunId,
                plan.StepId,
                DiagnosticStepPhase.Finished,
                redacted.Summary,
                runStartedAt);

            return redacted;
        }
        catch (OperationCanceledException)
        {
            var duration = _timeProvider.GetUtcNow() - startedAt;

            // 区分取消来源：用户主动取消（外部令牌）vs 超时（单步或任务总超时）。
            // 两者都必须与 Failed 区分开（DEV_STANDARDS §2.4）。
            DiagnosticStepResult conclusion;

            if (userToken.IsCancellationRequested)
            {
                conclusion = DiagnosticStepResults.Cancelled(plan.StepId, plan.Name, startedAt, duration);
            }
            else if (timeoutCts.IsCancellationRequested)
            {
                conclusion = DiagnosticStepResults.TimedOut(
                    plan.StepId, plan.Name, timeout, startedAt, duration);
            }
            else
            {
                // 剩下的可能只有“任务总超时”：总超时小于单步超时时会走到这里。
                // 用单独的文案说明，避免用户去调单步超时却解决不了问题。
                conclusion = DiagnosticStepResults.TimedOut(
                    plan.StepId,
                    plan.Name,
                    options.OverallTimeout ?? timeout,
                    startedAt,
                    duration,
                    "任务总超时已用尽，请减少步骤数量、提高总超时或缩小诊断范围后重试。",
                    timeoutScope: "任务总");
            }

            Report(progress, request.RunId, plan.StepId, DiagnosticStepPhase.Finished, conclusion.Summary, runStartedAt);

            return conclusion;
        }
        catch (Exception ex)
        {
            var duration = _timeProvider.GetUtcNow() - startedAt;
            var conclusion = DiagnosticStepResults.FromException(plan.StepId, plan.Name, ex, startedAt, duration);

            Report(progress, request.RunId, plan.StepId, DiagnosticStepPhase.Finished, conclusion.Summary, runStartedAt);

            return conclusion;
        }
    }

    /// <summary>
    /// 对适配器返回的结论做最后一道脱敏与耗时校正。
    /// </summary>
    /// <remarks>
    /// 即使适配器已经自己脱敏过，这里仍然再走一遍：脱敏是幂等的（已由测试锁定），
    /// 而漏脱敏的代价不可逆。同时用编排器的时钟重算耗时，
    /// 避免适配器自行计时导致同一任务内的时间基准不一致。
    /// </remarks>
    private DiagnosticStepResult Redact(
        DiagnosticStepResult result,
        DateTimeOffset startedAt,
        TimeSpan duration) =>
        result with
        {
            Name = _redactor.Redact(result.Name) ?? string.Empty,
            Summary = _redactor.Redact(result.Summary) ?? string.Empty,
            NextStepSuggestion = _redactor.Redact(result.NextStepSuggestion),
            SkipReason = _redactor.Redact(result.SkipReason),
            Facts = result.FactItems
                .Select(fact => new DiagnosticFact(
                    _redactor.Redact(fact.Label) ?? string.Empty,
                    _redactor.Redact(fact.Value) ?? string.Empty,
                    fact.Status))
                .ToArray(),
            RawOutput = _redactor.Redact(result.RawOutput),
            StartedAt = result.StartedAt ?? startedAt,
            Duration = duration
        };

    /// <summary>把步骤状态转成面向用户的中文短语（仅用于跳过原因）。</summary>
    private static string DescribeStatus(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Passed => "通过",
        DiagnosticStatus.Warning => "警告",
        DiagnosticStatus.Failed => "失败",
        DiagnosticStatus.Skipped => "已跳过",
        DiagnosticStatus.Unknown => "未判定",
        DiagnosticStatus.Cancelled => "已取消",
        DiagnosticStatus.TimedOut => "超时",
        DiagnosticStatus.Running => "执行中",
        _ => "未开始"
    };

    private static void Report(
        IProgress<DiagnosticProgress>? progress,
        string runId,
        string stepId,
        DiagnosticStepPhase phase,
        string message,
        DateTimeOffset runStartedAt)
    {
        if (progress is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;

        progress.Report(new DiagnosticProgress(
            runId,
            stepId,
            phase,
            message,
            now,
            now - runStartedAt));
    }
}
