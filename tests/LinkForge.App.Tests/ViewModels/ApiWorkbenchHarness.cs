using System.IO;
using LinkForge.Adapters.Http;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>构造 API 工作台的测试夹具。</summary>
internal sealed class ApiWorkbenchHarness : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "linkforge-api-" + Guid.NewGuid().ToString("N"));

    public ApiWorkbenchHarness()
    {
        Transport = new FakeHttpApiTransport();
        HistoryStore = new FakeApiHistoryStore();
        Exporter = new FakeApiResultExporter();

        ViewModel = new ApiWorkbenchViewModel(
            Transport,
            HistoryStore,
            Exporter);
    }

    public FakeHttpApiTransport Transport { get; }

    public FakeApiHistoryStore HistoryStore { get; }

    public FakeApiResultExporter Exporter { get; }

    public ApiWorkbenchViewModel ViewModel { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>可编排的 HTTP 传输替身。</summary>
internal sealed class FakeHttpApiTransport : IHttpApiTransport
{
    public Func<HttpApiRequest, CancellationToken, Task<HttpApiResponse>>? Behavior { get; set; }

    public HttpApiRequest? LastRequest { get; private set; }

    public int CallCount { get; private set; }

    public Task<HttpApiResponse> SendAsync(
        HttpApiRequest request,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;

        return Behavior?.Invoke(request, cancellationToken)
            ?? Task.FromResult(new HttpApiResponse
            {
                Method = request.Method,
                FinalUri = new Uri(request.Url),
                StatusCode = 200,
                ReasonPhrase = "OK",
                ResponseHeaders = new Dictionary<string, string[]>
                {
                    ["Content-Type"] = ["application/json"]
                },
                Body = "{\"ok\":true}",
                Duration = TimeSpan.FromMilliseconds(120),
                IsTruncated = false,
                ResponseBytes = 11
            });
    }
}

/// <summary>内存历史存储。</summary>
internal sealed class FakeApiHistoryStore : IApiRequestHistoryStore
{
    private readonly List<ApiHistoryEntry> _entries = [];

    public IReadOnlyList<ApiHistoryEntry> Entries => _entries;

    public IReadOnlyList<ApiHistoryEntry> Load() => [.. _entries];

    public void Append(ApiHistoryEntry entry) => _entries.Insert(0, entry);

    public void Clear() => _entries.Clear();
}

/// <summary>记录复制/导出内容。</summary>
internal sealed class FakeApiResultExporter : IApiResultExporter
{
    public string? CopiedText { get; private set; }

    public string? ExportedContent { get; private set; }

    public Func<string, string, CancellationToken, Task<string?>>? ExportBehavior { get; set; }

    public void Copy(string text) => CopiedText = text;

    public Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        ExportedContent = content;

        return ExportBehavior?.Invoke(suggestedFileName, content, cancellationToken)
            ?? Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), suggestedFileName));
    }
}
