using LinkForge.App.Presentation;

namespace LinkForge.App.Tests.Presentation;

/// <summary>
/// 敏感信息脱敏测试（UI 规范 §9、ADR-004）。
/// </summary>
/// <remarks>
/// 这些用例取自真实排障场景中的常见格式（RTSP URL、MQTT 连接串、HTTP 头、JSON 请求体、
/// 各类令牌）。每个用例都断言"原文中的秘密值不再出现"，
/// 而不是只断言"出现了 ***"——后者会在脱敏不完整时仍然通过。
/// </remarks>
public class SecretRedactorTests
{
    [Fact]
    public void Rtsp_url_password_should_be_redacted_but_username_and_host_kept()
    {
        const string input = "rtsp://admin:Passw0rd!23@10.20.30.40:554/Streaming/Channels/101";

        var output = SecretRedactor.Redact(input);

        output.ShouldNotContain("Passw0rd!23");
        output.ShouldContain("admin");
        output.ShouldContain("10.20.30.40:554");
        output.ShouldContain("***@");
    }

    [Theory]
    [InlineData("http://user:secret123@example.com/api")]
    [InlineData("https://user:secret123@example.com/api")]
    [InlineData("mqtt://user:secret123@broker.local:1883")]
    [InlineData("ftp://user:secret123@files.local")]
    public void Url_userinfo_should_be_redacted_for_all_schemes(string input)
    {
        var output = SecretRedactor.Redact(input);

        output.ShouldNotContain("secret123");
        output.ShouldContain("user");
    }

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("password: hunter2")]
    [InlineData("pwd=hunter2")]
    [InlineData("passwd=hunter2")]
    [InlineData("PASSWORD=hunter2")]
    public void Key_value_password_forms_should_be_redacted(string input)
    {
        SecretRedactor.Redact(input).ShouldNotContain("hunter2");
    }

    [Theory]
    [InlineData("token=abc123def456")]
    [InlineData("access_token=abc123def456")]
    [InlineData("refresh_token=abc123def456")]
    [InlineData("api_key=abc123def456")]
    [InlineData("apikey=abc123def456")]
    [InlineData("app_secret=abc123def456")]
    [InlineData("client_secret=abc123def456")]
    [InlineData("secret=abc123def456")]
    [InlineData("signature=abc123def456")]
    [InlineData("session_id=abc123def456")]
    public void Key_value_token_forms_should_be_redacted(string input)
    {
        SecretRedactor.Redact(input).ShouldNotContain("abc123def456");
    }

    [Fact]
    public void Json_body_should_be_redacted_without_breaking_structure()
    {
        const string input =
            """{"deviceId":"cam-01","password":"P@ssw0rd","token":"tok_9f8e7d","channel":1}""";

        var output = SecretRedactor.Redact(input);

        output.ShouldNotContain("P@ssw0rd");
        output.ShouldNotContain("tok_9f8e7d");
        // 结构与非敏感字段必须保留，否则排障时无法理解请求。
        output.ShouldContain("deviceId");
        output.ShouldContain("cam-01");
        output.ShouldContain("channel");
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payloadpart.signaturepart")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==")]
    [InlineData("authorization: bearer sometokenvalue123")]
    public void Authorization_headers_should_be_redacted(string input)
    {
        var output = SecretRedactor.Redact(input);

        output.ShouldNotContain("eyJhbGciOiJIUzI1NiJ9");
        output.ShouldNotContain("dXNlcjpwYXNzd29yZA==");
        output.ShouldNotContain("sometokenvalue123");
    }

    [Fact]
    public void Jwt_without_key_name_should_still_be_redacted()
    {
        const string input =
            "sent header eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk to server";

        var output = SecretRedactor.Redact(input);

        output.ShouldNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");
        output.ShouldNotContain("dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk");
    }

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    public void Known_token_prefixes_should_be_redacted(string token)
    {
        SecretRedactor.Redact($"key is {token} here").ShouldNotContain(token);
    }

    [Fact]
    public void Query_string_should_be_redacted()
    {
        const string input =
            "GET /api/devices?access_token=abcdef123456&channel=1&app_key=keyvalue99";

        var output = SecretRedactor.Redact(input);

        output.ShouldNotContain("abcdef123456");
        output.ShouldNotContain("keyvalue99");
        output.ShouldContain("channel=1");
    }

    [Fact]
    public void Cookie_header_should_be_redacted()
    {
        const string input = "Cookie: sessionid=abc123xyz; theme=dark";

        var output = SecretRedactor.Redact(input);

        output.ShouldNotContain("abc123xyz");
        output.ShouldContain("theme=dark");
    }

    [Fact]
    public void Non_sensitive_text_should_be_unchanged()
    {
        const string input =
            "连接超时：10.20.30.40:1883 无响应，已重试 3 次，耗时 5002 ms。";

        SecretRedactor.Redact(input).ShouldBe(input);
    }

    [Fact]
    public void Non_sensitive_key_values_should_be_unchanged()
    {
        // 防止过度脱敏把排障必需信息也抹掉。
        const string input = "host=10.20.30.40 port=1883 channel=1 deviceId=cam-01 timeout=5000";

        var output = SecretRedactor.Redact(input);

        output.ShouldContain("10.20.30.40");
        output.ShouldContain("1883");
        output.ShouldContain("cam-01");
        output.ShouldContain("5000");
    }

    [Fact]
    public void Redact_should_be_idempotent()
    {
        const string input = "rtsp://admin:secret@10.0.0.1:554/live password=another";

        var once = SecretRedactor.Redact(input);
        var twice = SecretRedactor.Redact(once);

        twice.ShouldBe(once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_should_pass_through(string? input)
    {
        SecretRedactor.Redact(input).ShouldBe(input);
    }

    [Fact]
    public void Multiple_secrets_in_one_line_should_all_be_redacted()
    {
        const string input =
            "rtsp://admin:pass1@10.0.0.1/live | Authorization: Bearer tok123456789 | api_key=key987654321";

        var output = SecretRedactor.Redact(input);

        output.ShouldNotContain("pass1");
        output.ShouldNotContain("tok123456789");
        output.ShouldNotContain("key987654321");
    }

    [Fact]
    public void Mask_constant_should_not_look_like_a_real_value()
    {
        // 掩码本身必须一眼可辨，避免被误认为真实值。
        SecretRedactor.Mask.ShouldBe("***");
    }
}
