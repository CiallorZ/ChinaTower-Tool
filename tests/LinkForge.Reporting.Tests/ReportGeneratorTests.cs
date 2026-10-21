using System.Text;
using LinkForge.Application.Diagnostics;
using LinkForge.Domain.Diagnostics;
using LinkForge.Reporting.Reports;

namespace LinkForge.Reporting.Tests;

public class ReportGeneratorTests : IDisposable
{
    private readonly string _directory;
    private readonly ReportGenerator _generator;

    public ReportGeneratorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "linkforge-report-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _generator = new ReportGenerator(new PassThroughTextRedactor());
    }

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
            // 测试清理失败不影响结论。
        }
    }

    [Fact]
    public async Task Generation_should_write_utf8_html_without_bom()
    {
        var path = Path.Combine(_directory, "report.html");

        var result = await _generator.GenerateAsync(TestData.Document(), path);

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Scan.IsClean.ShouldBeTrue();
        File.Exists(path).ShouldBeTrue();

        var bytes = await File.ReadAllBytesAsync(path);

        // UTF-8 BOM 会让部分工具把前言当正文，这里要求无 BOM。
        bytes.Length.ShouldBeGreaterThan(3);
        (bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF).ShouldBeFalse();

        var html = Encoding.UTF8.GetString(bytes);
        html.ShouldStartWith("<!DOCTYPE html>");
        html.ShouldContain("网络基础诊断");
    }

    [Fact]
    public async Task Generation_should_create_missing_directories()
    {
        var path = Path.Combine(_directory, "nested", "deeper", "report.html");

        var result = await _generator.GenerateAsync(TestData.Document(), path);

        result.Succeeded.ShouldBeTrue(result.Message);
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Sensitive_content_should_block_generation_entirely()
    {
        var run = TestData.HealthyRun() with
        {
            Steps =
            [
                new DiagnosticStepResult(
                    "leak",
                    "泄漏步骤",
                    DiagnosticStatus.Failed,
                    TestData.Start,
                    TimeSpan.FromSeconds(1),
                    "请求头 Authorization: Bearer abc123456789 被拒绝。")
            ]
        };

        var path = Path.Combine(_directory, "blocked.html");

        var result = await _generator.GenerateAsync(TestData.Document(run), path);

        result.Succeeded.ShouldBeFalse();
        result.FailureKind.ShouldBe(ReportFailureKind.SensitiveDataDetected);
        result.Message.ShouldContain("未生成");
        result.Scan.IsClean.ShouldBeFalse();

        // 关键：不得留下任何文件，包括临时文件。
        File.Exists(path).ShouldBeFalse();
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task Existing_file_should_be_replaced_atomically()
    {
        var path = Path.Combine(_directory, "replace.html");
        await File.WriteAllTextAsync(path, "旧内容");

        var result = await _generator.GenerateAsync(TestData.Document(), path);

        result.Succeeded.ShouldBeTrue(result.Message);

        var html = await File.ReadAllTextAsync(path);
        html.ShouldNotContain("旧内容");
        html.ShouldStartWith("<!DOCTYPE html>");
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task Concurrent_generation_to_same_path_should_not_leave_temporary_files()
    {
        var path = Path.Combine(_directory, "concurrent.html");

        var tasks = Enumerable
            .Range(0, 4)
            .Select(_ => _generator.GenerateAsync(TestData.Document(), path));

        var results = await Task.WhenAll(tasks);

        // 并发替换可能有个别失败（文件被占用），但不得留下半写文件。
        results.ShouldContain(r => r.Succeeded || r.FailureKind == ReportFailureKind.WriteFailed);

        if (File.Exists(path))
        {
            var html = await File.ReadAllTextAsync(path);
            html.ShouldEndWith("</html>" + Environment.NewLine);
        }

        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task Cancellation_before_write_should_not_create_file()
    {
        var path = Path.Combine(_directory, "cancelled.html");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _generator.GenerateAsync(TestData.Document(), path, cts.Token);

        result.Succeeded.ShouldBeFalse();
        result.FailureKind.ShouldBe(ReportFailureKind.Cancelled);
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task Unwritable_path_should_report_write_failure_without_throwing()
    {
        // 用非法路径触发真实 IO 失败，而不是靠 mock。
        var invalid = Path.Combine(_directory, "bad\u0000name.html");

        var result = await _generator.GenerateAsync(TestData.Document(), invalid);

        result.Succeeded.ShouldBeFalse();
        result.FailureKind.ShouldNotBeNull();
        result.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Suggest_file_name_should_be_filesystem_safe()
    {
        var name = _generator.SuggestFileName("video/rtsp:v1*test");

        name.ShouldEndWith(".html");
        name.ShouldContain("linkforge-report-");
        name.ShouldNotContain("/");
        name.ShouldNotContain(":");
        name.ShouldNotContain("*");
    }

    [Fact]
    public void Scan_should_only_read_document_texts()
    {
        var scan = _generator.Scan(TestData.Document());

        scan.IsClean.ShouldBeTrue(scan.Describe());
    }
}
