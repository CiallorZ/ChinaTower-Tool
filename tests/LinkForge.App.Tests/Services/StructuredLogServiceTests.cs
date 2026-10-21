using System.Collections.ObjectModel;
using System.IO;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Application.Logging;

namespace LinkForge.App.Tests.Services;

public sealed class StructuredLogServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "linkforge-log-service-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task Published_entry_should_be_visible_and_persisted_after_redaction()
    {
        using var service = CreateService();
        var entry = Entry(
            "请求 rtsp://admin:CameraPass123@10.0.0.8/live password=NestedSecret123",
            "network.request");

        service.Publish(entry);
        await service.FlushAsync();

        service.Entries.Count.ShouldBe(1);
        service.Entries[0].Message.ShouldNotContain("CameraPass123");
        service.Entries[0].Message.ShouldNotContain("NestedSecret123");

        var content = await File.ReadAllTextAsync(Path.Combine(
            _directory,
            $"linkforge-{DateTime.Now:yyyyMMdd}.jsonl"));

        content.ShouldNotContain("CameraPass123");
        content.ShouldNotContain("NestedSecret123");
        content.ShouldContain("network.request");
    }

    [Fact]
    public async Task Attaching_a_page_log_should_mirror_existing_and_future_entries()
    {
        using var service = CreateService();
        var source = new ObservableCollection<LogEntry>
        {
            Entry("existing", "page.existing")
        };

        service.Attach(source);
        source.Add(Entry("future", "page.future"));
        await service.FlushAsync();

        service.Entries.Select(static entry => entry.EventId)
            .ShouldBe(["page.existing", "page.future"]);
    }

    [Fact]
    public async Task Storage_failure_should_be_reported_without_throwing_into_the_caller()
    {
        using var service = new StructuredLogService(new FailingStore());

        service.Publish(Entry("message", "test.write"));
        await service.FlushAsync();

        service.HasStorageError.ShouldBeTrue();
        service.StorageError!.ShouldContain("写入日志失败");
        service.Entries.Count.ShouldBe(1);
    }

    private StructuredLogService CreateService() =>
        new(new JsonLineStructuredLogStore(_directory, new SecretTextRedactor()));

    private static LogEntry Entry(string message, string eventId) =>
        new(
            DateTimeOffset.Now,
            LfLogLevel.Information,
            "Network",
            "run-1",
            "rtsp://admin:CameraPass123@10.0.0.8/live",
            message,
            eventId);

    private sealed class FailingStore : IStructuredLogStore
    {
        public string DirectoryPath => "unused";

        public string CurrentFilePath => "unused.jsonl";

        public Task AppendAsync(
            StructuredLogRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("磁盘不可写"));

        public Task<IReadOnlyList<StructuredLogRecord>> ReadRecentAsync(
            int maximumEntries,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StructuredLogRecord>>([]);
    }
}
