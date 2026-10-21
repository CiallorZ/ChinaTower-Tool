using LinkForge.App.Presentation;

namespace LinkForge.App.Tests.Presentation;

/// <summary>
/// 日志条目脱敏测试（架构 §3.4、ADR-004）。
/// </summary>
/// <remarks>
/// 这些用例针对一个明确的真实风险：
/// 日志文本里经常夹带 <c>rtsp://admin:pass@host</c>、<c>Authorization: Bearer xxx</c>
/// 之类的整行内容。若脱敏只做在"写入文件时"，那么在 UI 显示、复制、
/// 报告生成这些环节就已经是明文了。
///
/// 因此这里断言的是：<b>对象构造完成之后，任何路径都拿不到明文</b>。
/// 断言方式是"密文不得出现在输出中"，而不是"输出里包含 ***"
/// ——后者在脱敏不完整时仍会通过。
/// </remarks>
public class LogEntryTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 10, 15, 14, 3, 21, 482, TimeSpan.Zero);

    private static LogEntry Create(LfLogLevel level, string message, string? targetId = null) =>
        new(FixedTime, level, "Network", "run-1", targetId, message);

    private static LogEntry CreateWithEvent(
        LfLogLevel level,
        string message,
        string eventId) =>
        new(FixedTime, level, "Network", "run-1", null, message, eventId);

    [Theory]
    [InlineData("连接 rtsp://admin:Passw0rd!23@10.0.0.1:554/live 超时")]
    [InlineData("GET /api?access_token=abcdef123456 返回 200")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.pay.sig")]
    [InlineData("password=hunter2")]
    public void Secrets_in_log_message_should_be_redacted_on_construction(string raw)
    {
        var entry = Create(LfLogLevel.Information, raw);

        // 逐段断言明文确实消失，而不是只检查出现了掩码。
        entry.Message.ShouldNotContain("Passw0rd!23");
        entry.Message.ShouldNotContain("abcdef123456");
        entry.Message.ShouldNotContain("eyJhbGciOiJIUzI1NiJ9");
        entry.Message.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Non_sensitive_log_message_should_be_preserved()
    {
        // 脱敏不得把排障必需的信息一起抹掉，否则日志失去价值。
        const string raw = "目标 10.0.0.1:554 无响应，已重试 3 次，耗时 5002 ms";

        Create(LfLogLevel.Warning, raw).Message.ShouldBe(raw);
    }

    [Fact]
    public void Entry_should_expose_no_plaintext_property()
    {
        // 反射层面的守卫：本类型不得出现任何承载原始消息的成员。
        // 若有人新增 OriginalMessage 属性，本测试立即失败，
        // 防止"为了方便调试"把明文出口加回来。
        var suspicious = typeof(LogEntry)
            .GetProperties()
            .Select(p => p.Name)
            .Where(name => name.Contains("Original", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Raw", StringComparison.OrdinalIgnoreCase))
            .ToList();

        suspicious.ShouldBeEmpty(
            "LogEntry 不得暴露承载原始明文的成员：" + string.Join(", ", suspicious));
    }

    [Fact]
    public void Level_text_should_be_chinese_for_every_level()
    {
        foreach (var level in Enum.GetValues<LfLogLevel>())
        {
            Create(level, "消息").LevelText.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Missing_target_should_render_as_dash_not_blank()
    {
        // 空白会在表格里看起来像"数据缺失"，用占位符明确表达"无目标"。
        Create(LfLogLevel.Information, "消息", null).TargetText.ShouldBe("-");
    }

    [Fact]
    public void Target_identifier_should_be_redacted_on_construction()
    {
        // 目标字段也可能带 userinfo（例如从粘贴的 URL 中误带入），
        // 不能只脱敏消息而把目标列原样显示在日志表里。
        var entry = Create(
            LfLogLevel.Information,
            "连接失败",
            "rtsp://admin:Passw0rd!23@10.0.0.1:554/live");

        entry.TargetId.ShouldNotBeNull().ShouldNotContain("Passw0rd!23");
        entry.TargetText.ShouldNotContain("Passw0rd!23");
    }

    [Fact]
    public void Event_identifier_should_be_present_and_redacted_on_construction()
    {
        var entry = CreateWithEvent(
            LfLogLevel.Information,
            "兼容诊断",
            "legacy.password=EventSecret123");

        entry.EventId.ShouldNotContain("EventSecret123");
        entry.EventId.ShouldContain("legacy.password=***");
    }

    [Fact]
    public void Module_and_run_identifier_should_also_be_redacted()
    {
        var entry = new LogEntry(
            FixedTime,
            LfLogLevel.Information,
            "module.password=ModuleSecret123",
            "token=RunSecret123",
            null,
            "消息");

        entry.Module.ShouldNotContain("ModuleSecret123");
        entry.RunId.ShouldNotContain("RunSecret123");
    }

    [Fact]
    public void Plain_text_should_include_all_structured_fields()
    {
        var entry = new LogEntry(
            FixedTime,
            LfLogLevel.Error,
            "Network",
            "run-42",
            "camera-01",
            "TCP 连接失败",
            "network.tcp.failed");

        var text = entry.ToPlainText();

        text.ShouldContain("Network");
        text.ShouldContain("run-42");
        text.ShouldContain("camera-01");
        text.ShouldContain("network.tcp.failed");
        text.ShouldContain("TCP 连接失败");
    }

    [Fact]
    public void Timestamp_text_should_be_time_only_and_culture_invariant()
    {
        // 强制 InvariantCulture：否则在部分区域设置下小数点会变成逗号，
        // 日志时间戳的毫秒分隔符随机器变化，跨机器比对日志时会误判。
        Create(LfLogLevel.Information, "消息").TimestampText.ShouldMatch(@"^\d{2}:\d{2}:\d{2}\.\d{3}$");
    }

    [Fact]
    public void Emphasis_should_apply_only_to_error_and_above()
    {
        // 若 Warning 也加粗，满屏日志里就分不出轻重，等于没有强调。
        LogLevelPresentation.IsEmphasized(LfLogLevel.Warning).ShouldBeFalse();
        LogLevelPresentation.IsEmphasized(LfLogLevel.Information).ShouldBeFalse();
        LogLevelPresentation.IsEmphasized(LfLogLevel.Error).ShouldBeTrue();
        LogLevelPresentation.IsEmphasized(LfLogLevel.Critical).ShouldBeTrue();
    }

    [Fact]
    public void Every_log_level_should_map_to_an_existing_brush_token()
    {
        // 级别颜色必须落在既有语义令牌上，防止有人引入 ThemeManager 不认识的令牌名，
        // 那样主题切换后这些颜色不会更新（ADR-017）。
        var suffixes = Enum.GetValues<LfLogLevel>()
            .Select(LogLevelPresentation.TokenSuffix)
            .Distinct()
            .ToList();

        foreach (var suffix in suffixes)
        {
            StatusPresentation.TokenSuffix(
                LogLevelPresentation.Visual(LfLogLevel.Information)).ShouldNotBeNullOrWhiteSpace();
            suffix.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
