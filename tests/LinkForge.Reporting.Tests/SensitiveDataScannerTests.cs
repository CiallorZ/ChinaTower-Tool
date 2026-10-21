using LinkForge.Reporting.Reports;

namespace LinkForge.Reporting.Tests;

public class SensitiveDataScannerTests
{
    [Fact]
    public void Clean_document_should_pass_scan()
    {
        var result = SensitiveDataScanner.Scan(TestData.Document().EnumerateTexts());

        result.IsClean.ShouldBeTrue(result.Describe());
    }

    [Theory]
    [InlineData("rtsp://admin:Passw0rd@camera-01:554/live", "URL 凭据")]
    [InlineData("http://user:secret@api.example.com/v1", "URL 凭据")]
    [InlineData("Authorization: Bearer abcdef123456", "Authorization")]
    [InlineData("password=SuperSecret", "键值型口令")]
    [InlineData("api_key: 9f8e7d6c5b4a", "键值型口令")]
    [InlineData("client_secret=\"s3cr3t\"", "键值型口令")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop", "令牌")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "令牌")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz123456", "令牌")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "令牌")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "私钥")]
    public void Sensitive_pattern_should_be_detected(string text, string expectedKind)
    {
        var result = SensitiveDataScanner.Scan([("测试字段", text)]);

        result.IsClean.ShouldBeFalse();
        result.Findings.ShouldContain(f => f.Kind == expectedKind);
        result.Findings.ShouldContain(f => f.Location == "测试字段");
    }

    [Theory]
    [InlineData("rtsp://admin:***@camera-01:554/live")]
    [InlineData("Authorization: ***")]
    [InlineData("Authorization: Bearer ***")]
    [InlineData("password=***")]
    [InlineData("password=<REDACTED>")]
    public void Already_redacted_values_should_not_be_flagged(string text)
    {
        var result = SensitiveDataScanner.Scan([("字段", text)]);

        result.IsClean.ShouldBeTrue(result.Describe());
    }

    [Fact]
    public void Findings_should_not_contain_the_original_secret()
    {
        const string secret = "SuperSecretValue123";

        var result = SensitiveDataScanner.Scan([("字段", $"password={secret}")]);
        var described = result.Describe();
        var location = result.Findings[0].Location;
        var kind = result.Findings[0].Kind;

        described.ShouldNotContain(secret);
        location.ShouldNotContain(secret);
        kind.ShouldNotContain(secret);
    }

    [Fact]
    public void Scanner_should_report_each_sensitive_field_separately()
    {
        var result = SensitiveDataScanner.Scan(
        [
            ("目标", "rtsp://admin:Pass1@camera-01/live"),
            ("Header", "Authorization: Bearer token123"),
            ("备注", "正常文本")
        ]);

        result.Findings.Count.ShouldBeGreaterThanOrEqualTo(2);
        result.Findings.Select(static f => f.Location).ShouldContain("目标");
        result.Findings.Select(static f => f.Location).ShouldContain("Header");
        result.Findings.Select(static f => f.Location).ShouldNotContain("备注");
    }

    [Fact]
    public void Empty_and_whitespace_texts_should_be_ignored()
    {
        var result = SensitiveDataScanner.Scan([("空", string.Empty), ("空白", "   ")]);

        result.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void Description_should_summarise_kinds_without_values()
    {
        var result = SensitiveDataScanner.Scan(
        [
            ("A", "password=abc123"),
            ("B", "token=xyz")
        ]);

        result.Describe().ShouldContain("处疑似敏感信息");
    }
}
