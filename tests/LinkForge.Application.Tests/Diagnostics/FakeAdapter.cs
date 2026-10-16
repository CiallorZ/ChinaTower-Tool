using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Application.Tests.Diagnostics;

/// <summary>
/// 测试专用诊断适配器。
/// </summary>
/// <remarks>
/// <para>
/// <b>刻意放在测试项目而不是产品源码里。</b>
/// 计划中曾打算在 <c>LinkForge.Application</c> 下建一个 <c>FakeAdapter.cs</c>，
/// 但那意味着一个“假装有功能”的类型会随产品程序集一起分发：
/// 只要有人把它注册进 DI，工作台就会出现一条永远不会真实失败的任务。
/// 把测试替身留在测试项目，是让“生产路径不含样例数据”成为编译期事实，
/// 而不是靠代码评审记得别注册它（DEV_STANDARDS §6.6）。
/// </para>
/// <para>
/// 每个步骤的行为由构造函数传入的委托决定，因此同一个类能表达
/// 通过 / 失败 / 未知 / 抛异常 / 永久挂起等全部情形。
/// </para>
/// </remarks>
internal sealed class FakeAdapter : IDiagnosticAdapter
{
    private readonly Func<DiagnosticStepContext, CancellationToken, Task<DiagnosticStepResult>> _behavior;

    /// <summary>创建测试适配器。</summary>
    /// <param name="behavior">步骤行为。</param>
    /// <param name="steps">步骤计划；null 时使用一个默认步骤。</param>
    public FakeAdapter(
        Func<DiagnosticStepContext, CancellationToken, Task<DiagnosticStepResult>> behavior,
        IReadOnlyList<DiagnosticStepPlan>? steps = null)
    {
        _behavior = behavior;
        Steps = steps ?? [new DiagnosticStepPlan("s1", "步骤 1")];
    }

    /// <summary>步骤计划。</summary>
    public IReadOnlyList<DiagnosticStepPlan> Steps { get; set; }

    /// <summary>实际被执行的步骤标识（用于断言“未执行的步骤真的没被执行”）。</summary>
    public List<string> ExecutedSteps { get; } = [];

    /// <inheritdoc />
    public string Id => "test.fake";

    /// <inheritdoc />
    public string DisplayName => "测试适配器";

    /// <inheritdoc />
    public Version ApiVersion => new(1, 0);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Capabilities => ["test"];

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticStepPlan> Plan(DiagnosticRequest request) => Steps;

    /// <inheritdoc />
    public Task<DiagnosticStepResult> ExecuteStepAsync(
        DiagnosticStepContext context,
        IProgress<DiagnosticProgress> progress,
        CancellationToken cancellationToken)
    {
        ExecutedSteps.Add(context.StepId);
        return _behavior(context, cancellationToken);
    }

    /// <summary>构造一个“立即通过”的行为。</summary>
    public static Func<DiagnosticStepContext, CancellationToken, Task<DiagnosticStepResult>> Passed() =>
        (context, _) => Task.FromResult(DiagnosticStepResults.Passed(
            context.StepId, context.Name, "通过", context.StartedAt, TimeSpan.Zero));

    /// <summary>构造一个“立即失败”的行为。</summary>
    public static Func<DiagnosticStepContext, CancellationToken, Task<DiagnosticStepResult>> Failed() =>
        (context, _) => Task.FromResult(DiagnosticStepResults.Failed(
            context.StepId, context.Name, "失败", context.StartedAt, TimeSpan.Zero));

    /// <summary>构造一个“永久挂起直到被取消”的行为（用于验证超时与取消）。</summary>
    public static Func<DiagnosticStepContext, CancellationToken, Task<DiagnosticStepResult>> Hangs() =>
        async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("不应到达这里。");
        };
}

/// <summary>
/// 同步转发、可枚举的进度接收器。
/// </summary>
/// <remarks>
/// 不用 <c>Progress&lt;T&gt;</c>：它会异步投递，导致断言时进度可能还没到达，
/// 测试要么变得不稳定，要么只能加 <c>Task.Delay</c> 之类的脆弱等待。
/// </remarks>
internal sealed class RecordingProgress : IProgress<DiagnosticProgress>
{
    /// <summary>收到的全部进度事件（按到达顺序）。</summary>
    public List<DiagnosticProgress> Events { get; } = [];

    /// <inheritdoc />
    public void Report(DiagnosticProgress value) => Events.Add(value);
}