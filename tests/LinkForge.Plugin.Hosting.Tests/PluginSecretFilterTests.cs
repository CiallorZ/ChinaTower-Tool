namespace LinkForge.Plugin.Hosting.Tests;

/// <summary>
/// 宿主对插件返回文本的秘密过滤。
/// </summary>
/// <remarks>
/// 参数白名单只保证"宿主不把秘密发给插件"，不保证"插件不返回秘密"。
/// 本类锁定宿主侧这最后一道边界：恶意或有缺陷的插件把凭据塞进摘要/事实/原始输出时，
/// 不得原样进入日志、历史、复制或报告。
/// </remarks>
public class PluginSecretFilterTests
{
    [Fact]
    public void Key_value_assignments_should_be_masked()
    {
        PluginSecretFilter.Redact("登录成功 password=SuperSecret123 已建立会话")!
            .ShouldNotContain("SuperSecret123");
        PluginSecretFilter.Redact("connect with passwd: SuperSecret123; retry")!
            .ShouldNotContain("SuperSecret123");
    }

    [Fact]
    public void Json_quoted_secret_values_should_be_masked()
    {
        // JSON 形态（键名带引号）是插件最常见的返回结构，必须命中。
        PluginSecretFilter.Redact("""{"token":"SuperSecret123","ok":true}""")!
            .ShouldNotContain("SuperSecret123");
    }

    [Fact]
    public void Authorization_scheme_name_should_not_hide_the_real_token()
    {
        // 把 "Bearer" 当值遮蔽、留下真令牌，是最危险的假脱敏。
        var output = PluginSecretFilter.Redact("Authorization = Bearer abcdefghijklmnop")!;

        output.ShouldNotContain("abcdefghijklmnop");
        output.ShouldContain("Authorization");
    }

    [Fact]
    public void Token_shapes_without_a_key_should_be_masked()
    {
        PluginSecretFilter.Redact("use sk-abcdefghij123456 to call the api")!
            .ShouldNotContain("sk-abcdefghij123456");
        PluginSecretFilter.Redact("key AKIAABCDEFGHIJKLMNOP for s3")!
            .ShouldNotContain("AKIAABCDEFGHIJKLMNOP");
    }

    [Fact]
    public void Quoted_json_values_should_be_masked_without_breaking_structure()
    {
        const string input = """{"user":"admin","password":"P@ss w0rd","channel":1}""";

        var output = PluginSecretFilter.Redact(input)!;

        output.ShouldNotContain("P@ss w0rd");

        // 保留键名与结构：脱敏不能把整行变成不可读的噪声，否则排障信息也一起丢了。
        output.ShouldContain("\"user\"");
        output.ShouldContain("\"password\"");
        output.ShouldContain("\"channel\":1");

        // 引号必须成对保留，否则产出非法 JSON，整份导出文件被写坏。
        output.ShouldContain("\"password\":\"***\"");
        Should.NotThrow(() => System.Text.Json.JsonDocument.Parse(output));
    }

    [Fact]
    public void Secret_ref_is_a_reference_key_and_must_survive()
    {
        // secretRef 是宿主有意下发给插件的引用键，本身不是秘密；
        // 若被规则误伤，插件就收不到任何可用参数，这是功能性缺陷。
        const string input = "capabilityProbe=vendor.probe secretRef=cam-01-admin";

        PluginSecretFilter.Redact(input).ShouldBe(input);
    }

    [Fact]
    public void Benign_text_should_pass_through_unchanged()
    {
        const string input = "连接超时：10.20.30.40:1883 无响应，已重试 3 次，耗时 5002 ms。";

        PluginSecretFilter.Redact(input).ShouldBe(input);
    }

    [Fact]
    public void Filtering_should_be_idempotent()
    {
        const string input = "handshake ok password=Sup3rS3cret host=10.0.0.8";

        var once = PluginSecretFilter.Redact(input);
        var twice = PluginSecretFilter.Redact(once);

        twice.ShouldBe(once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_should_pass_through(string? input)
    {
        PluginSecretFilter.Redact(input).ShouldBe(input);
    }

    [Fact]
    public void Short_token_like_prefixes_should_not_be_swallowed()
    {
        // "sk-" 只有 3 个字符的后缀不是凭据；过度匹配会破坏正常日志可读性。
        const string input = "socket sk-x is a shorthand in this sentence";

        PluginSecretFilter.Redact(input).ShouldBe(input);
    }
}
