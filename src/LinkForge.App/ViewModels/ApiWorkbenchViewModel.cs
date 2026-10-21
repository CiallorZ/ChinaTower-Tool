using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.Adapters.Http;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.ViewModels;

/// <summary>
/// API 工作台（LF-007）。
/// </summary>
/// <remarks>
/// 页面状态来自真实 HTTP 传输：
/// <list type="bullet">
///   <item>收到响应（含 4xx/5xx）→ <see cref="TaskRunState.Completed"/>；</item>
///   <item>传输失败/超时 → <see cref="TaskRunState.Failed"/>；</item>
///   <item>用户取消 → <see cref="TaskRunState.Cancelled"/>。</item>
/// </list>
/// 历史、日志、复制和导出只接触脱敏副本；请求凭据只存在于当前视图模型与传输层之间。
/// </remarks>
public sealed partial class ApiWorkbenchViewModel : ObservableObject
{
    private static readonly Regex VariablePattern = new(
        @"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpApiTransport _transport;
    private readonly IApiRequestHistoryStore _historyStore;
    private readonly IApiResultExporter _exporter;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _uiContext;

    private CancellationTokenSource? _runCts;
    private string _currentRunId = string.Empty;
    private HttpApiResponse? _lastResponse;

    /// <summary>创建 API 工作台。</summary>
    public ApiWorkbenchViewModel(
        IHttpApiTransport transport,
        IApiRequestHistoryStore historyStore,
        IApiResultExporter exporter,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(exporter);

        _transport = transport;
        _historyStore = historyStore;
        _exporter = exporter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _uiContext = SynchronizationContext.Current;

        Methods =
        [
            new("GET"),
            new("POST"),
            new("PUT"),
            new("PATCH"),
            new("DELETE")
        ];

        BodyKinds =
        [
            new("None", "无请求体"),
            new("Json", "JSON"),
            new("Text", "文本"),
            new("FormUrlEncoded", "表单")
        ];

        AuthModes =
        [
            new("None", "无认证"),
            new("Basic", "Basic"),
            new("Bearer", "Bearer Token"),
            new("ApiKey", "API Key")
        ];

        SelectedMethod = Methods[0];
        SelectedBodyKind = BodyKinds[0];
        SelectedAuthMode = AuthModes[0];

        QueryParameters = [];
        RequestHeaders = [];
        EnvironmentVariables = [];
        History = [];
        Logs = [];
        SummaryItems = [];
        ResponseHeaders = [];

        State = TaskRunState.Pending;
        LoadHistory();
        AddDefaultEnvironmentVariables();
    }

    /// <summary>可用 HTTP 方法。</summary>
    public IReadOnlyList<HttpMethodOption> Methods { get; }

    /// <summary>可用请求体类型。</summary>
    public IReadOnlyList<HttpBodyKindOption> BodyKinds { get; }

    /// <summary>可用认证方式。</summary>
    public IReadOnlyList<HttpAuthModeOption> AuthModes { get; }

    /// <summary>当前 HTTP 方法。</summary>
    [ObservableProperty]
    public partial HttpMethodOption SelectedMethod { get; set; }

    /// <summary>当前请求体类型。</summary>
    [ObservableProperty]
    public partial HttpBodyKindOption SelectedBodyKind { get; set; }

    /// <summary>当前认证方式。</summary>
    [ObservableProperty]
    public partial HttpAuthModeOption SelectedAuthMode { get; set; }

    /// <summary>URL（可含 {{变量}}）。</summary>
    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    /// <summary>请求体文本。</summary>
    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    /// <summary>Basic 用户名。</summary>
    [ObservableProperty]
    public partial string BasicUsername { get; set; } = string.Empty;

    /// <summary>Basic 密码。</summary>
    [ObservableProperty]
    public partial string BasicPassword { get; set; } = string.Empty;

    /// <summary>Bearer Token。</summary>
    [ObservableProperty]
    public partial string BearerToken { get; set; } = string.Empty;

    /// <summary>API Key Header 名。</summary>
    [ObservableProperty]
    public partial string ApiKeyHeaderName { get; set; } = "X-API-Key";

    /// <summary>API Key 值。</summary>
    [ObservableProperty]
    public partial string ApiKeyValue { get; set; } = string.Empty;

    /// <summary>是否跟随重定向。</summary>
    [ObservableProperty]
    public partial bool FollowRedirects { get; set; } = true;

    /// <summary>最大重定向次数文本。</summary>
    [ObservableProperty]
    public partial string MaxRedirectsText { get; set; } = "5";

    /// <summary>超时秒数文本。</summary>
    [ObservableProperty]
    public partial string TimeoutText { get; set; } = "30";

    /// <summary>URL 字段错误。</summary>
    [ObservableProperty]
    public partial string? UrlError { get; set; }

    /// <summary>请求头错误。</summary>
    [ObservableProperty]
    public partial string? HeadersError { get; set; }

    /// <summary>查询参数错误。</summary>
    [ObservableProperty]
    public partial string? QueryError { get; set; }

    /// <summary>请求体错误。</summary>
    [ObservableProperty]
    public partial string? BodyError { get; set; }

    /// <summary>认证字段错误。</summary>
    [ObservableProperty]
    public partial string? AuthError { get; set; }

    /// <summary>超时字段错误。</summary>
    [ObservableProperty]
    public partial string? TimeoutError { get; set; }

    /// <summary>重定向字段错误。</summary>
    [ObservableProperty]
    public partial string? RedirectError { get; set; }

    /// <summary>是否正在执行。</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>任务状态。</summary>
    [ObservableProperty]
    public partial TaskRunState State { get; private set; }

    /// <summary>当前阶段。</summary>
    [ObservableProperty]
    public partial string CurrentStage { get; private set; } = "尚未发送请求";

    /// <summary>已运行时间。</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = "00:00.0";

    /// <summary>一句话结论。</summary>
    [ObservableProperty]
    public partial string Conclusion { get; private set; } = string.Empty;

    /// <summary>结论补充。</summary>
    [ObservableProperty]
    public partial string ConclusionNote { get; private set; } = string.Empty;

    /// <summary>是否已有结果。</summary>
    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    /// <summary>是否有响应（含 4xx/5xx）。</summary>
    [ObservableProperty]
    public partial bool HasResponse { get; private set; }

    /// <summary>状态行。</summary>
    [ObservableProperty]
    public partial string ResponseStatusText { get; private set; } = string.Empty;

    /// <summary>响应体文本。</summary>
    [ObservableProperty]
    public partial string ResponseBody { get; private set; } = string.Empty;

    /// <summary>响应体是否截断。</summary>
    [ObservableProperty]
    public partial bool IsResponseTruncated { get; private set; }

    /// <summary>截断提示。</summary>
    [ObservableProperty]
    public partial string TruncationNotice { get; private set; } = string.Empty;

    /// <summary>传输失败的技术详情。</summary>
    [ObservableProperty]
    public partial string? FailureDetail { get; private set; }

    /// <summary>复制/导出反馈。</summary>
    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    /// <summary>查询参数。</summary>
    public ObservableCollection<ApiHeaderRow> QueryParameters { get; }

    /// <summary>请求头。</summary>
    public ObservableCollection<ApiHeaderRow> RequestHeaders { get; }

    /// <summary>环境变量。</summary>
    public ObservableCollection<ApiEnvironmentVariable> EnvironmentVariables { get; }

    /// <summary>请求历史（脱敏）。</summary>
    public ObservableCollection<ApiHistoryEntry> History { get; }

    /// <summary>日志（写入即脱敏）。</summary>
    public ObservableCollection<LogEntry> Logs { get; }

    /// <summary>结论摘要。</summary>
    public ObservableCollection<ResultSummaryItem> SummaryItems { get; }

    /// <summary>响应头。</summary>
    public ObservableCollection<ApiHeaderRow> ResponseHeaders { get; }

    /// <summary>当前方法是否通常允许请求体。</summary>
    public bool CanHaveBody => SelectedMethod.Method is "POST" or "PUT" or "PATCH" or "DELETE";

    /// <summary>是否需要 Basic 凭据。</summary>
    public bool IsBasicAuth => SelectedAuthMode.Key == "Basic";

    /// <summary>是否需要 Bearer Token。</summary>
    public bool IsBearerAuth => SelectedAuthMode.Key == "Bearer";

    /// <summary>是否需要 API Key。</summary>
    public bool IsApiKeyAuth => SelectedAuthMode.Key == "ApiKey";

    /// <summary>能否发送。</summary>
    public bool CanSend => !IsRunning;

    /// <summary>是否有历史。</summary>
    public bool HasHistory => History.Count > 0;

    partial void OnSelectedMethodChanged(HttpMethodOption value)
    {
        OnPropertyChanged(nameof(CanHaveBody));
    }

    partial void OnSelectedAuthModeChanged(HttpAuthModeOption value)
    {
        OnPropertyChanged(nameof(IsBasicAuth));
        OnPropertyChanged(nameof(IsBearerAuth));
        OnPropertyChanged(nameof(IsApiKeyAuth));
        AuthError = null;
    }

    partial void OnUrlChanged(string value)
    {
        UrlError = null;
    }

    partial void OnTimeoutTextChanged(string value) => TimeoutError = null;

    partial void OnMaxRedirectsTextChanged(string value) => RedirectError = null;

    /// <summary>发送请求。</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        if (!Validate())
        {
            return;
        }

        var startedAt = _timeProvider.GetUtcNow();
        _currentRunId = Guid.NewGuid().ToString("N");
        _lastResponse = null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = linked;

        try
        {
            IsRunning = true;
            State = TaskRunState.Preparing;
            HasResult = false;
            HasResponse = false;
            CurrentStage = "正在准备请求…";
            ElapsedText = "00:00.0";
            Conclusion = string.Empty;
            ConclusionNote = string.Empty;
            ResponseStatusText = string.Empty;
            ResponseBody = string.Empty;
            TruncationNotice = string.Empty;
            FailureDetail = null;
            ActionFeedback = string.Empty;
            SummaryItems.Clear();
            ResponseHeaders.Clear();
            Logs.Clear();
            NotifyCommands();

            var expandedUrl = ExpandVariables(Url.Trim(), out var missing);
            if (missing.Count > 0)
            {
                UrlError = $"以下变量未定义：{string.Join(", ", missing)}";
                State = TaskRunState.Pending;
                return;
            }

            var request = BuildRequest(expandedUrl);
            AppendLog(LfLogLevel.Information, $"发送 {request.Method} {RedactUrl(request.Url)}");

            var ticker = TickElapsedAsync(startedAt, linked.Token);

            try
            {
                State = TaskRunState.Running;
                CurrentStage = "正在等待响应…";

                var response = await _transport.SendAsync(request, linked.Token);
                ApplyResponse(response);
            }
            finally
            {
                linked.Cancel();
                await ticker;
            }
        }
        catch (OperationCanceledException)
        {
            State = TaskRunState.Cancelled;
            CurrentStage = "已取消";
            Conclusion = "请求已被取消，未产生完整响应。";
            ConclusionNote = "已发送的请求可能已到达服务器；取消只保证本地停止等待。";
            HasResult = true;
            AppendLog(LfLogLevel.Warning, "请求已被用户取消。");
            AddHistory(null, null, false);
        }
        catch (HttpApiTransportException ex)
        {
            State = TaskRunState.Failed;
            CurrentStage = ex.Kind == HttpApiFailureKind.Timeout ? "请求超时" : "传输失败";
            Conclusion = BuildFailureConclusion(ex);
            ConclusionNote = BuildFailureSuggestion(ex.Kind);
            FailureDetail = SecretRedactor.Redact($"{ex.GetType().Name}: {ex.Message}");
            HasResult = true;
            AppendLog(LfLogLevel.Error, Conclusion);
            AddHistory(null, null, false);
        }
        catch (Exception ex)
        {
            State = TaskRunState.Failed;
            CurrentStage = "执行失败";
            Conclusion = $"请求未能完成：{ex.GetType().Name} — {ex.Message}";
            ConclusionNote = "请检查参数、网络与目标服务状态后重试。";
            FailureDetail = SecretRedactor.Redact(ex.ToString());
            HasResult = true;
            AppendLog(LfLogLevel.Critical, Conclusion);
            AddHistory(null, null, false);
        }
        finally
        {
            IsRunning = false;
            _runCts = null;
            NotifyCommands();
        }
    }

    /// <summary>取消请求。</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>复制脱敏请求/响应摘要。</summary>
    [RelayCommand]
    private void CopySummary()
    {
        if (!HasResult)
        {
            return;
        }

        try
        {
            _exporter.Copy(BuildSummaryText());
            ActionFeedback = "已复制脱敏摘要到剪贴板。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ActionFeedback = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>导出脱敏 JSON 结果。</summary>
    [RelayCommand]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (!HasResult)
        {
            return;
        }

        try
        {
            var fileName = $"linkforge-api-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var path = await _exporter
                .ExportAsync(fileName, BuildExportJson(), cancellationToken);

            ActionFeedback = path is null ? "已取消导出。" : $"已导出：{path}";
        }
        catch (OperationCanceledException)
        {
            ActionFeedback = "已取消导出。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionFeedback = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>清空历史。</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        _historyStore.Clear();
        History.Clear();
        OnPropertyChanged(nameof(HasHistory));
        ActionFeedback = "请求历史已清空。";
    }

    /// <summary>添加一行查询参数。</summary>
    [RelayCommand]
    private void AddQueryParameter() =>
        QueryParameters.Add(new ApiHeaderRow(string.Empty, string.Empty));

    /// <summary>添加一行请求头。</summary>
    [RelayCommand]
    private void AddRequestHeader() =>
        RequestHeaders.Add(new ApiHeaderRow(string.Empty, string.Empty));

    /// <summary>添加环境变量。</summary>
    [RelayCommand]
    private void AddEnvironmentVariable() =>
        EnvironmentVariables.Add(new ApiEnvironmentVariable());

    /// <summary>校验输入。</summary>
    private bool Validate()
    {
        UrlError = null;
        HeadersError = null;
        QueryError = null;
        BodyError = null;
        AuthError = null;
        TimeoutError = null;
        RedirectError = null;

        var valid = true;
        var expandedUrl = ExpandVariables(Url.Trim(), out var missing);

        if (string.IsNullOrWhiteSpace(Url))
        {
            UrlError = "请输入 http:// 或 https:// 绝对地址。";
            valid = false;
        }
        else if (missing.Count > 0)
        {
            UrlError = $"以下变量未定义：{string.Join(", ", missing)}";
            valid = false;
        }
        else if (!Uri.TryCreate(expandedUrl, UriKind.Absolute, out var uri)
                 || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            UrlError = "URL 必须是有效的 http:// 或 https:// 绝对地址。";
            valid = false;
        }
        else if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            UrlError = "URL 中不能包含用户名或密码；请使用认证字段。";
            valid = false;
        }

        if (!int.TryParse(TimeoutText, out var timeout) || timeout is < 1 or > 300)
        {
            TimeoutError = "超时必须是 1–300 秒的整数。";
            valid = false;
        }

        if (!int.TryParse(MaxRedirectsText, out var redirects) || redirects is < 0 or > 10)
        {
            RedirectError = "最大重定向次数必须是 0–10 的整数。";
            valid = false;
        }

        if (RequestHeaders.Any(static header => string.IsNullOrWhiteSpace(header.Name)
                                                 && !string.IsNullOrWhiteSpace(header.Value)))
        {
            HeadersError = "请求头有值时必须填写名称。";
            valid = false;
        }

        if (QueryParameters.Any(static query => string.IsNullOrWhiteSpace(query.Name)
                                                && !string.IsNullOrWhiteSpace(query.Value)))
        {
            QueryError = "查询参数有值时必须填写名称。";
            valid = false;
        }

        if (SelectedBodyKind.Key == "Json"
            && !string.IsNullOrWhiteSpace(Body))
        {
            try
            {
                var expandedBody = ExpandVariables(Body, out _);
                using var document = JsonDocument.Parse(expandedBody);
            }
            catch (JsonException ex)
            {
                BodyError = $"JSON 格式无效：{ex.Message}";
                valid = false;
            }
        }

        switch (SelectedAuthMode.Key)
        {
            case "Basic" when string.IsNullOrWhiteSpace(BasicUsername):
                AuthError = "Basic 认证需要用户名。";
                valid = false;
                break;

            case "Bearer" when string.IsNullOrWhiteSpace(BearerToken):
                AuthError = "Bearer 认证需要 Token。";
                valid = false;
                break;

            case "ApiKey" when string.IsNullOrWhiteSpace(ApiKeyValue):
                AuthError = "API Key 认证需要 Key 值。";
                valid = false;
                break;
        }

        return valid;
    }

    private HttpApiRequest BuildRequest(string expandedUrl) =>
        new()
        {
            Method = SelectedMethod.Method,
            Url = expandedUrl,
            Query = QueryParameters
                .Where(static query => !string.IsNullOrWhiteSpace(query.Name))
                .GroupBy(static query => query.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    group => ExpandVariables(group.Last().Value, out _),
                    StringComparer.OrdinalIgnoreCase),
            Headers = RequestHeaders
                .Where(static header => !string.IsNullOrWhiteSpace(header.Name))
                .GroupBy(static header => header.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    group => ExpandVariables(group.Last().Value, out _),
                    StringComparer.OrdinalIgnoreCase),
            Body = CanHaveBody && SelectedBodyKind.Key != "None"
                ? ExpandVariables(Body, out _)
                : null,
            BodyKind = Enum.TryParse<HttpApiBodyKind>(SelectedBodyKind.Key, out var kind)
                ? kind
                : HttpApiBodyKind.None,
            AuthMode = Enum.TryParse<HttpApiAuthMode>(SelectedAuthMode.Key, out var auth)
                ? auth
                : HttpApiAuthMode.None,
            BasicUsername = ExpandVariables(BasicUsername, out _),
            BasicPassword = ExpandVariables(BasicPassword, out _),
            BearerToken = ExpandVariables(BearerToken, out _),
            ApiKeyHeaderName = ApiKeyHeaderName.Trim(),
            ApiKeyValue = ExpandVariables(ApiKeyValue, out _),
            FollowRedirects = FollowRedirects,
            MaxRedirects = int.TryParse(MaxRedirectsText, out var redirects) ? redirects : 5,
            Timeout = TimeSpan.FromSeconds(int.TryParse(TimeoutText, out var timeout) ? timeout : 30)
        };

    private void ApplyResponse(HttpApiResponse response)
    {
        _lastResponse = response;
        State = TaskRunState.Completed;
        HasResult = true;
        HasResponse = true;
        CurrentStage = "已收到响应";
        ElapsedText = DashboardViewModel.FormatElapsed(response.Duration);
        ResponseStatusText = response.StatusText;
        ResponseBody = SecretRedactor.Redact(response.Body) ?? string.Empty;
        IsResponseTruncated = response.IsTruncated;
        TruncationNotice = response.IsTruncated
            ? $"响应体已截断：仅保留前 {response.ResponseBytes:N0} 字节。请缩小查询范围或使用分页。"
            : string.Empty;

        Conclusion = response.IsSuccess
            ? $"请求完成，服务器返回 {response.StatusText}。"
            : $"请求已执行，但服务器返回 {response.StatusText}；这是 HTTP 层错误，不是传输失败。";
        ConclusionNote = response.IsTruncated
            ? TruncationNotice
            : "状态码、响应头与响应体来自本次真实请求。";

        SummaryItems.Clear();
        SummaryItems.Add(new ResultSummaryItem { Label = "方法", Value = response.Method });
        SummaryItems.Add(new ResultSummaryItem { Label = "状态", Value = response.StatusText });
        SummaryItems.Add(new ResultSummaryItem { Label = "耗时", Value = FormatDuration(response.Duration) });
        SummaryItems.Add(new ResultSummaryItem { Label = "响应大小", Value = $"{response.ResponseBytes:N0} 字节" });
        SummaryItems.Add(new ResultSummaryItem
        {
            Label = "结论",
            Value = response.IsSuccess ? "HTTP 成功" : "HTTP 错误响应",
            Status = response.IsSuccess ? DiagnosticStatus.Passed : DiagnosticStatus.Warning
        });

        ResponseHeaders.Clear();
        foreach (var (name, values) in response.ResponseHeaders.OrderBy(static pair => pair.Key))
        {
            ResponseHeaders.Add(new ApiHeaderRow(name, string.Join(", ", values)));
        }

        AppendLog(
            response.IsSuccess ? LfLogLevel.Information : LfLogLevel.Warning,
            $"收到 {response.StatusText}，耗时 {FormatDuration(response.Duration)}，响应 {response.ResponseBytes:N0} 字节。");

        AddHistory(response, response.Duration, response.IsSuccess);
    }

    private void AddHistory(HttpApiResponse? response, TimeSpan? duration, bool success)
    {
        var displayUrl = SecretRedactor.Redact(
            RedactUrl(ExpandVariables(Url.Trim(), out _))) ?? string.Empty;
        var entry = new ApiHistoryEntry(
            _timeProvider.GetUtcNow(),
            SelectedMethod.Method,
            displayUrl,
            response?.StatusText,
            duration,
            success);

        try
        {
            _historyStore.Append(entry);
            History.Insert(
                0,
                entry with
                {
                    DisplayUrl = SecretRedactor.Redact(entry.DisplayUrl) ?? string.Empty,
                    StatusText = SecretRedactor.Redact(entry.StatusText)
                });

            while (History.Count > 100)
            {
                History.RemoveAt(History.Count - 1);
            }

            OnPropertyChanged(nameof(HasHistory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog(LfLogLevel.Warning, $"历史写入失败：{ex.Message}");
        }
    }

    private void LoadHistory()
    {
        foreach (var entry in _historyStore.Load())
        {
            History.Add(entry);
        }
    }

    private void AddDefaultEnvironmentVariables()
    {
        EnvironmentVariables.Add(new ApiEnvironmentVariable
        {
            Name = "baseUrl",
            Value = "https://api.example.com",
            IsSecret = false
        });
    }

    private string BuildSummaryText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("LinkForge API 调试结果");
        builder.AppendLine($"请求：{SelectedMethod.Method} {RedactUrl(ExpandVariables(Url.Trim(), out _))}");
        builder.AppendLine($"状态：{DescribeState(State)}");
        builder.AppendLine($"阶段：{CurrentStage}");
        builder.AppendLine($"耗时：{ElapsedText}");
        builder.AppendLine($"结论：{Conclusion}");

        if (_lastResponse is { } response)
        {
            builder.AppendLine($"HTTP：{response.StatusText}");
            builder.AppendLine($"响应大小：{response.ResponseBytes:N0} 字节");

            if (response.IsTruncated)
            {
                builder.AppendLine(TruncationNotice);
            }
        }

        builder.AppendLine();
        builder.AppendLine("说明：请求头、查询参数、响应体与日志在进入剪贴板前已统一脱敏。");
        return builder.ToString();
    }

    private string BuildExportJson()
    {
        var document = new
        {
            product = "LinkForge",
            kind = "api-request",
            generatedAt = DateTimeOffset.Now,
            request = new
            {
                method = SelectedMethod.Method,
                url = RedactUrl(ExpandVariables(Url.Trim(), out _)),
                query = QueryParameters.Select(static q => new
                {
                    name = SecretRedactor.Redact(q.Name),
                    value = SecretRedactor.RedactNamedValue(q.Name, q.Value)
                }),
                headers = RequestHeaders.Select(static h => new
                {
                    name = SecretRedactor.Redact(h.Name),
                    value = SecretRedactor.RedactNamedValue(h.Name, h.Value)
                }),
                bodyKind = SelectedBodyKind.Key,
                body = SecretRedactor.Redact(Body)
            },
            state = State.ToString(),
            conclusion = SecretRedactor.Redact(Conclusion),
            note = SecretRedactor.Redact(ConclusionNote),
            failure = SecretRedactor.Redact(FailureDetail),
            response = _lastResponse is { } response
                ? new
                {
                    status = response.StatusCode,
                    reason = SecretRedactor.Redact(response.ReasonPhrase),
                    duration = response.Duration,
                    bytes = response.ResponseBytes,
                    truncated = response.IsTruncated,
                    headers = ResponseHeaders.Select(static h => new
                    {
                        name = SecretRedactor.Redact(h.Name),
                        value = h.DisplayValue
                    }),
                    body = SecretRedactor.Redact(response.Body)
                }
                : null,
            redactionNotice = "所有文本在导出前均已执行敏感信息脱敏；认证字段不导出。"
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private string ExpandVariables(string input, out IReadOnlyList<string> missing)
    {
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = EnvironmentVariables
            .Where(static variable => !string.IsNullOrWhiteSpace(variable.Name))
            .GroupBy(static variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last().Value,
                StringComparer.OrdinalIgnoreCase);

        var expanded = VariablePattern.Replace(
            input,
            match =>
            {
                var name = match.Groups["name"].Value;

                if (values.TryGetValue(name, out var value))
                {
                    return value;
                }

                unresolved.Add(name);
                return match.Value;
            });

        missing = [.. unresolved];
        return expanded;
    }

    private static string RedactUrl(string url)
    {
        var redacted = SecretRedactor.Redact(url) ?? string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return redacted;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };

        return SecretRedactor.Redact(builder.Uri.ToString()) ?? redacted;
    }

    private static string BuildFailureConclusion(HttpApiTransportException exception) =>
        exception.Kind switch
        {
            HttpApiFailureKind.Timeout => "请求超时，未在设定时间内收到完整响应。",
            HttpApiFailureKind.Dns => "DNS 解析失败，无法找到目标主机。",
            HttpApiFailureKind.Connection => "无法建立连接，目标服务可能未启动或被防火墙拒绝。",
            HttpApiFailureKind.Tls => "TLS 或证书验证失败；不会通过关闭证书验证来绕过。",
            HttpApiFailureKind.InvalidRequest => "请求参数无效，未发送到服务器。",
            _ => $"HTTP 传输失败：{exception.Message}"
        };

    private static string BuildFailureSuggestion(HttpApiFailureKind kind) =>
        kind switch
        {
            HttpApiFailureKind.Timeout => "提高超时时间，或检查目标服务、网络与代理延迟。",
            HttpApiFailureKind.Dns => "检查域名拼写、DNS 配置与网络连接。",
            HttpApiFailureKind.Connection => "检查目标地址、端口、服务监听与防火墙。",
            HttpApiFailureKind.Tls => "检查系统时间、证书链与主机名；不要关闭 TLS 校验。",
            HttpApiFailureKind.InvalidRequest => "修正字段错误后重试。",
            _ => "检查网络、代理、TLS 与目标服务状态后重试。"
        };

    private async Task TickElapsedAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var elapsed = _timeProvider.GetUtcNow() - startedAt;
            _ = PostAsync(() => ElapsedText = DashboardViewModel.FormatElapsed(elapsed));
        }
    }

    private void AppendLog(LfLogLevel level, string message)
    {
        var entry = new LogEntry(
            DateTimeOffset.Now,
            level,
            "API",
            _currentRunId,
            RedactUrl(ExpandVariables(Url.Trim(), out _)),
            message);

        _ = PostAsync(() => Logs.Add(entry));
    }

    private Task PostAsync(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();

        _uiContext.Post(
            _ =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            },
            null);

        return completion.Task;
    }

    private void NotifyCommands()
    {
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSend));
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds:0.0} s"
            : $"{duration.TotalMilliseconds:0} ms";

    private static string DescribeState(TaskRunState state) => state switch
    {
        TaskRunState.Completed => "已收到响应",
        TaskRunState.Failed => "传输失败",
        TaskRunState.Cancelled => "已取消",
        TaskRunState.Running => "执行中",
        TaskRunState.Preparing => "准备中",
        _ => "待发送"
    };
}
