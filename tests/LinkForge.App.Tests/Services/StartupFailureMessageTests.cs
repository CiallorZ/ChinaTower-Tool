using System.IO;
using LinkForge.App;

namespace LinkForge.App.Tests.Services;

/// <summary>
/// 启动失败提示必须可读且默认脱敏。
/// </summary>
public class StartupFailureMessageTests
{
    [Fact]
    public void Failure_message_should_include_inner_exception_chain()
    {
        var exception = new InvalidOperationException(
            "外层失败",
            new IOException("本地数据库被占用"));

        var message = App.BuildStartupFailureMessage(exception);

        message.ShouldContain(nameof(InvalidOperationException));
        message.ShouldContain("外层失败");
        message.ShouldContain(nameof(IOException));
        message.ShouldContain("本地数据库被占用");
    }

    [Fact]
    public void Failure_message_should_redact_credentials()
    {
        var exception = new InvalidOperationException(
            "password=hunter2 Authorization: Bearer abc.def.ghi");

        var message = App.BuildStartupFailureMessage(exception);

        message.ShouldNotContain("hunter2");
        message.ShouldNotContain("abc.def.ghi");
        message.ShouldContain("***");
    }

    [Fact]
    public void Failure_message_should_reject_null_exception()
    {
        Should.Throw<ArgumentNullException>(() => App.BuildStartupFailureMessage(null!));
    }
}
