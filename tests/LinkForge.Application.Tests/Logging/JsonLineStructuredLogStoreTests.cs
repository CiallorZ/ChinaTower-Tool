using System.Text.Json;
using LinkForge.Application.Diagnostics;
using LinkForge.Application.Logging;

namespace LinkForge.Application.Tests.Logging;

public sealed class JsonLineStructuredLogStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "linkforge-logs-" + Guid.NewGuid().ToString("N"));

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
    public async Task Append_should_write_all_required_fields_as_one_json_line()
    {
        var store = CreateStore();
        var timestamp = new DateTimeOffset(2026, 10, 22, 9, 30, 15, TimeSpan.FromHours(8));

        await store.AppendAsync(new StructuredLogRecord(
            timestamp,
            StructuredLogLevel.Warning,
            "Network",
            "run-42",
            "camera-01",
            "network.timeout",
            "TCP 连接超时"));

        var line = (await File.ReadAllLinesAsync(store.CurrentFilePath)).Single();
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        root.GetProperty("Timestamp").GetDateTimeOffset().ShouldBe(timestamp);
        root.GetProperty("Level").GetString().ShouldBe("Warning");
        root.GetProperty("Module").GetString().ShouldBe("Network");
        root.GetProperty("RunId").GetString().ShouldBe("run-42");
        root.GetProperty("TargetId").GetString().ShouldBe("camera-01");
        root.GetProperty("EventId").GetString().ShouldBe("network.timeout");
        root.GetProperty("Message").GetString().ShouldBe("TCP 连接超时");
    }

    [Theory]
    [InlineData("""{"outer":{"password":"NestedPass123","token":"nested-token"},"channel":1}""", "NestedPass123")]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "abcdefghijklmnop")]
    [InlineData("rtsp://admin:CameraPass123@10.0.0.8/live", "CameraPass123")]
    [InlineData("Cookie: session=SessionSecret123; theme=dark", "SessionSecret123")]
    public async Task Store_should_redact_nested_and_common_secret_forms_before_write(
        string raw,
        string secret)
    {
        var store = CreateStore();

        await store.AppendAsync(CreateRecord(raw));

        var content = await File.ReadAllTextAsync(store.CurrentFilePath);

        content.ShouldNotContain(secret);
        content.ShouldContain("***");
    }

    [Fact]
    public async Task Read_recent_should_skip_corrupt_lines_and_return_latest_entries()
    {
        var store = CreateStore();
        await store.AppendAsync(CreateRecord("one"));
        await File.AppendAllTextAsync(
            store.CurrentFilePath,
            "{ broken json" + System.Environment.NewLine);
        await store.AppendAsync(CreateRecord("two"));
        await store.AppendAsync(CreateRecord("three"));

        var records = await store.ReadRecentAsync(2);

        records.Count.ShouldBe(2);
        records.Select(static record => record.Message).ShouldBe(["two", "three"]);
    }

    [Fact]
    public async Task Corrupt_lines_should_not_prevent_loading_healthy_entries()
    {
        var store = CreateStore();
        Directory.CreateDirectory(store.DirectoryPath);
        await File.WriteAllTextAsync(
            store.CurrentFilePath,
            "{ not valid json " + System.Environment.NewLine + "{ also broken ");

        var records = await store.ReadRecentAsync(100);

        records.ShouldBeEmpty();
    }

    private JsonLineStructuredLogStore CreateStore() =>
        new(_directory, new SecretTestRedactor());

    private static StructuredLogRecord CreateRecord(string message) =>
        new(
            DateTimeOffset.Now,
            StructuredLogLevel.Information,
            "Test",
            "run-1",
            null,
            "test.event",
            message);

    private sealed class SecretTestRedactor : ITextRedactor
    {
        public string? Redact(string? input) =>
            input is null
                ? null
                : System.Text.RegularExpressions.Regex.Replace(
                    input,
                    @"(?i)(password|token|session)([""']?\s*[:=]\s*[""']?)[^""'\s,;&}]+",
                    "$1$2***")
                .Replace("Bearer abcdefghijklmnop", "Bearer ***", StringComparison.Ordinal)
                .Replace("admin:CameraPass123@", "admin:***@", StringComparison.Ordinal);
    }
}
