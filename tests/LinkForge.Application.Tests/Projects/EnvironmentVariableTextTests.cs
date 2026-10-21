using LinkForge.Application.Projects;

namespace LinkForge.Application.Tests.Projects;

/// <summary>
/// 环境变量文本的解析与渲染（LF-004）。
/// </summary>
public class EnvironmentVariableTextTests
{
    private static string Lines(params string[] lines) =>
        string.Join("\r\n", lines);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Blank_input_should_produce_no_variables_and_no_errors(string? raw)
    {
        var result = EnvironmentVariableText.Parse(raw);

        result.Variables.ShouldBeEmpty();
        result.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public void Comments_and_blank_lines_should_be_ignored()
    {
        var result = EnvironmentVariableText.Parse(Lines(
            "# 这是注释",
            "",
            "   ",
            "HOST=192.168.1.10",
            "# 另一条注释"));

        result.HasErrors.ShouldBeFalse();
        result.Variables.Count.ShouldBe(1);
        result.Variables["HOST"].ShouldBe("192.168.1.10");
    }

    [Fact]
    public void Value_should_keep_everything_after_the_first_equals_sign()
    {
        // 代理地址之类的值里常有等号；只按第一个切分才对。
        var result = EnvironmentVariableText.Parse("PROXY_URL=http://user:pa==ss@host:3128");

        result.HasErrors.ShouldBeFalse();
        result.Variables["PROXY_URL"].ShouldBe("http://user:pa==ss@host:3128");
    }

    [Fact]
    public void Empty_value_should_be_allowed()
    {
        var result = EnvironmentVariableText.Parse("EMPTY=");

        result.HasErrors.ShouldBeFalse();
        result.Variables.ShouldContainKey("EMPTY");
        result.Variables["EMPTY"].ShouldBe(string.Empty);
    }

    [Fact]
    public void Key_names_should_be_case_insensitive()
    {
        var result = EnvironmentVariableText.Parse(Lines("Host=1", "REGION=cn", "host=2"));

        result.HasErrors.ShouldBeTrue("HOST 与 host 应视为同一个变量。");
        result.Errors[0].Message.Contains("重复", StringComparison.Ordinal).ShouldBeTrue();
        result.Variables.Count.ShouldBe(2);
        result.Variables["host"].ShouldBe("1");
    }

    [Theory]
    [InlineData("NOEQUALS")]
    [InlineData("=novalue")]
    public void Malformed_lines_should_be_rejected_without_throwing(string line)
    {
        var result = EnvironmentVariableText.Parse(line);

        result.HasErrors.ShouldBeTrue();
        result.Variables.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("DB_PASSWORD=hunter2")]
    [InlineData("API_TOKEN=abc")]
    [InlineData("app_secret=x")]
    [InlineData("MY-PWD=x")]
    [InlineData("apiKey=x")]
    [InlineData("AWS_SECRET_ACCESS_KEY=x")]
    [InlineData("SESSION_ID=x")]
    [InlineData("Cookie=x")]
    [InlineData("SIGNATURE=x")]
    [InlineData("private_key=x")]
    public void Secret_looking_variable_names_should_be_rejected(string line)
    {
        // 环境变量会随项目导出、随报告展示；明文密码进了这里就会出现在用户没预期的地方。
        var result = EnvironmentVariableText.Parse(line);

        result.HasErrors.ShouldBeTrue($"变量名 {line.Split('=')[0]} 承载凭据语义，应当被拒绝。");
        result.Variables.ShouldBeEmpty();
        result.Errors[0].Message.Contains("秘密引用", StringComparison.Ordinal).ShouldBeTrue();
    }

    [Theory]
    [InlineData("AUTHOR=someone")]
    [InlineData("TOKENIZER=standard")]
    [InlineData("KEYSTORE_PATH=/opt/keys")]
    [InlineData("COOKIEJAR=/tmp/cj")]
    public void Ordinary_variable_names_that_merely_contain_a_fragment_should_be_accepted(
        string line)
    {
        // 守卫“只看子串就拒绝”的过度拦截：author 不承载凭据，tokenizer 也不是令牌。
        var result = EnvironmentVariableText.Parse(line);

        result.HasErrors.ShouldBeFalse($"“{line}” 并不承载凭据语义，不该被拒绝。");
    }

    [Fact]
    public void Overlong_key_should_be_rejected()
    {
        var line = new string('K', EnvironmentVariableText.KeyLength + 1) + "=v";
        var result = EnvironmentVariableText.Parse(line);

        result.HasErrors.ShouldBeTrue();
        result.Errors[0].Message.Contains(
            EnvironmentVariableText.KeyLength.ToString(), StringComparison.Ordinal)
            .ShouldBeTrue();
    }

    [Fact]
    public void Overlong_value_should_be_rejected()
    {
        var line = "BIG=" + new string('v', EnvironmentVariableText.ValueLength + 1);
        var result = EnvironmentVariableText.Parse(line);

        result.HasErrors.ShouldBeTrue();
        result.Variables.ShouldBeEmpty();
    }

    [Fact]
    public void Variable_count_should_be_capped()
    {
        var lines = Enumerable
            .Range(0, EnvironmentVariableText.Count + 1)
            .Select(index => $"K{index}=v")
            .ToArray();

        var result = EnvironmentVariableText.Parse(Lines(lines));

        result.HasErrors.ShouldBeTrue();
        result.Variables.Count.ShouldBe(EnvironmentVariableText.Count);
        result.Errors.Count.ShouldBe(1);
    }

    [Fact]
    public void Issue_line_number_should_point_at_the_physical_line()
    {
        var result = EnvironmentVariableText.Parse(Lines(
            "# 注释",
            "GOOD=1",
            "BAD_PASSWORD=x",
            "ALSO_GOOD=2"));

        result.Errors.Count.ShouldBe(1);
        result.Errors[0].LineNumber.ShouldBe(3);
        result.Errors[0].ToString().Contains("第 3 行", StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void Format_should_render_sorted_key_value_lines()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REGION"] = "cn",
            ["HOST"] = "192.168.1.10",
            ["port"] = "554"
        };

        var text = EnvironmentVariableText.Format(variables);
        var lines = text.Split("\r\n", StringSplitOptions.None);

        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("HOST=192.168.1.10");
        lines[1].ShouldBe("port=554");
        lines[2].ShouldBe("REGION=cn");
    }

    [Theory]
    [InlineData(null)]
    public void Format_of_nothing_should_be_empty(string? unused)
    {
        EnvironmentVariableText.Format(null).ShouldBe(string.Empty);
        EnvironmentVariableText.Format(
            new Dictionary<string, string>()).ShouldBe(string.Empty);
        unused.ShouldBeNull();
    }

    [Fact]
    public void Round_trip_should_be_stable()
    {
        // 界面“编辑—保存—再打开”走的就是这条路径；
        // 不稳定就意味着用户没改任何东西、重新打开却看到不同文本。
        var original = Lines("HOST=10.0.0.1", "REGION=cn", "EMPTY=");

        var first = EnvironmentVariableText.Format(EnvironmentVariableText.Parse(original).Variables);
        var second = EnvironmentVariableText.Format(EnvironmentVariableText.Parse(first).Variables);

        first.ShouldBe(second);
        EnvironmentVariableText.Parse(first).HasErrors.ShouldBeFalse();
    }
}
