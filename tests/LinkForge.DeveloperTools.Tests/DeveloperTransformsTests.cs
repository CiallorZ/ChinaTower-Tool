using System.Text;
using LinkForge.DeveloperTools;

namespace LinkForge.DeveloperTools.Tests;

public class DeveloperTransformsTests
{
    [Fact]
    public void Json_format_should_preserve_unicode_and_nested_structure()
    {
        var result = DeveloperTransforms.Execute(
            DeveloperToolKind.JsonFormat,
            """{"设备":"摄像机","位置":{"机房":"A-01"},"通道":2}""");

        result.Succeeded.ShouldBeTrue();
        result.Output.ShouldContain("\"设备\": \"摄像机\"");
        result.Output.ShouldContain("\"机房\": \"A-01\"");
        result.Output.ShouldContain(Environment.NewLine);
    }

    [Fact]
    public void Json_minify_should_remove_whitespace()
    {
        var result = DeveloperTransforms.Execute(
            DeveloperToolKind.JsonMinify,
            """
            {
              "a": 1,
              "b": [2, 3]
            }
            """);

        result.Succeeded.ShouldBeTrue();
        result.Output.ShouldBe("""{"a":1,"b":[2,3]}""");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{"a":1 /* comment */}""")]
    public void Invalid_json_should_return_a_readable_failure(string input)
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.JsonFormat, input);

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("JSON");
    }

    [Fact]
    public void Json_failure_should_include_line_and_byte_position()
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.JsonFormat, "{\n  \"a\": }");

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("第 2 行");
        result.Message.ShouldContain("字节");
    }

    [Fact]
    public void Json_depth_limit_should_reject_excessive_nesting()
    {
        var input = new string('[', 130) + new string(']', 130);

        var result = DeveloperTransforms.Execute(DeveloperToolKind.JsonFormat, input);

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("JSON");
    }

    [Fact]
    public void Url_encode_should_handle_chinese_spaces_and_astral_unicode()
    {
        var result = DeveloperTransforms.Execute(
            DeveloperToolKind.UrlEncode,
            "设备 名称 😀");

        result.Succeeded.ShouldBeTrue();
        result.Output.ShouldBe("%E8%AE%BE%E5%A4%87%20%E5%90%8D%E7%A7%B0%20%F0%9F%98%80");
    }

    [Fact]
    public void Url_encode_should_return_failure_for_unpaired_surrogate()
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.UrlEncode, "\uD800");

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("代理字符");
    }

    [Fact]
    public void Url_decode_should_treat_plus_as_space()
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.UrlDecode, "a+b%20c");

        result.Succeeded.ShouldBeTrue();
        result.Output.ShouldBe("a b c");
    }

    [Theory]
    [InlineData("%")]
    [InlineData("%A")]
    [InlineData("%ZZ")]
    public void Url_decode_should_reject_invalid_percent_sequences(string input)
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.UrlDecode, input);

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("十六进制");
    }

    [Fact]
    public void Base64_decode_should_accept_standard_and_whitespace_input()
    {
        var standard = Convert.ToBase64String(Encoding.UTF8.GetBytes("LinkForge 设备"));

        var result = DeveloperTransforms.Execute(
            DeveloperToolKind.Base64Decode,
            standard.Insert(4, Environment.NewLine));

        result.Succeeded.ShouldBeTrue();
        result.Output.ShouldBe("LinkForge 设备");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("@@@=")]
    [InlineData("A===")]
    public void Base64_decode_should_reject_invalid_input(string input)
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.Base64Decode, input);

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("Base64");
    }

    [Fact]
    public void Base64_decode_should_show_hex_with_warning_for_non_utf8()
    {
        var input = Convert.ToBase64String([0xFF, 0xFE, 0x00]);

        var result = DeveloperTransforms.Execute(DeveloperToolKind.Base64Decode, input);

        result.Succeeded.ShouldBeTrue();
        result.IsWarning.ShouldBeTrue();
        result.Output.ShouldBe("FFFE00");
        result.Message.ShouldContain("十六进制");
    }

    [Theory]
    [InlineData("0", "1970-01-01 00:00:00")]
    [InlineData("-1", "1969-12-31 23:59:59")]
    [InlineData("253402300799", "9999-12-31 23:59:59")]
    [InlineData("253402300799999", "9999-12-31 23:59:59")]
    public void Timestamp_to_time_should_cover_boundaries(string input, string expectedUtc)
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.TimestampToTime, input);

        result.Succeeded.ShouldBeTrue();
        result.Output.ShouldContain($"UTC：{expectedUtc}");
    }

    [Theory]
    [InlineData("253402300800")]
    [InlineData("253402300800000")]
    public void Timestamp_to_time_should_reject_out_of_range_values(string input)
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.TimestampToTime, input);

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("范围");
    }

    [Fact]
    public void Time_to_timestamp_should_return_seconds_and_milliseconds()
    {
        var result = DeveloperTransforms.Execute(
            DeveloperToolKind.TimeToTimestamp,
            "2026-10-21T08:30:00+08:00");

        result.Succeeded.ShouldBeTrue();
        result.Output.ShouldContain("秒：");
        result.Output.ShouldContain("毫秒：");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1000")]
    public void Uuid_generate_should_honor_count_boundaries(string input)
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.UuidGenerate, input);
        var expected = string.IsNullOrEmpty(input) ? 1 : int.Parse(input);

        result.Succeeded.ShouldBeTrue();
        var lines = result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(expected);
        foreach (var line in lines)
        {
            Guid.TryParseExact(line, "D", out var parsed).ShouldBeTrue();
            parsed.ShouldNotBe(Guid.Empty);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Uuid_generate_should_reject_invalid_count(string input)
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.UuidGenerate, input);

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("1–1000");
    }

    [Fact]
    public void Uuid_generate_should_respect_cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            DeveloperTransforms.Execute(DeveloperToolKind.UuidGenerate, "1000", source.Token));
    }

    [Fact]
    public void Jwt_decode_should_show_header_payload_and_unverified_warning()
    {
        var header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url("""{"sub":"camera-01","name":"前门"}""");

        var result = DeveloperTransforms.Execute(
            DeveloperToolKind.JwtDecode,
            $"{header}.{payload}.signature");

        result.Succeeded.ShouldBeTrue();
        result.IsWarning.ShouldBeTrue();
        result.Output.ShouldContain("\"alg\": \"HS256\"");
        result.Output.ShouldContain("\"sub\": \"camera-01\"");
        result.Output.ShouldContain("未验证");
        result.Message.ShouldContain("未验证签名");
    }

    [Fact]
    public void Jwt_decode_should_accept_empty_signature_but_still_warn()
    {
        var header = Base64Url("""{"alg":"none"}""");
        var payload = Base64Url("""{"sub":"test"}""");

        var result = DeveloperTransforms.Execute(
            DeveloperToolKind.JwtDecode,
            $"{header}.{payload}.");

        result.Succeeded.ShouldBeTrue();
        result.IsWarning.ShouldBeTrue();
        result.Output.ShouldContain("长度 0 字符");
    }

    [Theory]
    [InlineData("only-one-part")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    public void Jwt_decode_should_reject_wrong_segment_count(string input)
    {
        var result = DeveloperTransforms.Execute(DeveloperToolKind.JwtDecode, input);

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("三段");
    }

    [Fact]
    public void Jwt_decode_should_reject_invalid_base64url_or_json()
    {
        var invalidBase64 = DeveloperTransforms.Execute(DeveloperToolKind.JwtDecode, "@@@.e30.sig");
        var invalidJson = DeveloperTransforms.Execute(
            DeveloperToolKind.JwtDecode,
            $"{Base64Url("not-json")}.{Base64Url("""{"a":1}""")}.sig");

        invalidBase64.Succeeded.ShouldBeFalse();
        invalidJson.Succeeded.ShouldBeFalse();
        invalidBase64.Message.ShouldContain("Base64URL");
        invalidJson.Message.ShouldContain("Base64URL");
    }

    [Fact]
    public void Input_at_the_byte_limit_should_be_accepted()
    {
        var input = new string('a', DeveloperTransforms.MaxInputBytes);

        var result = DeveloperTransforms.Execute(DeveloperToolKind.UrlEncode, input);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Input_one_byte_over_the_limit_should_fail()
    {
        var input = new string('a', DeveloperTransforms.MaxInputBytes + 1);

        var result = DeveloperTransforms.Execute(DeveloperToolKind.UrlEncode, input);

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain("1024 KiB");
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
