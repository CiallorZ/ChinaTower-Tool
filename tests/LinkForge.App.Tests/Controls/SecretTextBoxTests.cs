using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkForge.App.Controls;
using LinkForge.App.Tests.DesignTokens;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 凭据控件安全契约测试（UI 规范 §9、ADR-004、ADR-008）。
/// </summary>
/// <remarks>
/// 这些用例守住三个真实泄露途径：
/// <list type="number">
///   <item>明文进入可视化树 —— UI 自动化、屏幕阅读器、调试转储都能读到。</item>
///   <item>随手 Ctrl+C 把凭据带进剪贴板 —— 随后被粘进聊天或日志。</item>
///   <item>按长度回显 —— 泄露"密码是 8 位还是 20 位"。</item>
/// </list>
/// 这三个都是"功能看起来正常、但凭据已经泄露"的缺陷，必须靠测试锁定。
/// </remarks>
public class SecretTextBoxTests
{
    private const string SecretValue = "P@ssw0rd!ForCamera01";

    [Fact]
    public void Plaintext_should_never_appear_in_the_visual_tree()
    {
        var leaked = StaRunner.Run(() =>
        {
            var box = CreateSecretBox();
            Enter(box, SecretValue);

            return CollectTextInVisualTree(box);
        });

        // 用 Contains 再断言，而不是 ShouldNotContain：
        // Shouldly 的 ShouldNotContain 对 string 有 Expression 重载，
        // 传普通字符串会被误解析为字符筛选器表达式。
        leaked.Contains(SecretValue, StringComparison.Ordinal).ShouldBeFalse(
            "明文出现在可视化树中。UI 自动化、屏幕阅读器与内存转储都能读到它。");
    }

    [Fact]
    public void Copy_should_be_blocked_by_default()
    {
        // 默认必须阻断。用户必须显式开启"允许复制"，才知道自己在做什么。
        StaRunner.Run(() =>
        {
            var box = CreateSecretBox();
            Enter(box, SecretValue);

            return box.CanCopy;
        }).ShouldBeFalse();
    }

    [Fact]
    public void Copy_keystroke_should_be_handled_when_copy_is_disabled()
    {
        // 判断规则刻意做成纯函数：合成 KeyEventArgs 需要真实 PresentationSource，
        // 无窗口的测试环境构造不出来，直接模拟按键会永远失败。
        SecretTextBox.IsCopyKeystroke(Key.C, ModifierKeys.Control).ShouldBeTrue();
        SecretTextBox.IsCopyKeystroke(Key.Insert, ModifierKeys.Control).ShouldBeTrue();
    }

    [Theory]
    [InlineData(Key.C, ModifierKeys.None)]
    [InlineData(Key.X, ModifierKeys.Control)]
    [InlineData(Key.V, ModifierKeys.Control)]
    [InlineData(Key.A, ModifierKeys.Control)]
    public void Non_copy_keystrokes_should_not_be_intercepted(Key key, ModifierKeys modifiers)
    {
        // 反面同样重要：误伤 Ctrl+X/Ctrl+V/Ctrl+A 会破坏常规编辑操作，
        // 而漏掉单独的 C 键会让用户无法输入含字母 C 的密码。
        SecretTextBox.IsCopyKeystroke(key, modifiers).ShouldBeFalse();
    }

    [Fact]
    public void Copy_command_should_not_be_executable_by_default()
    {
        var canExecute = StaRunner.Run(() =>
        {
            var box = CreateSecretBox();
            Enter(box, SecretValue);

            var inner = FindPasswordBox(box);

            return ApplicationCommands.Copy.CanExecute(null, inner);
        });

        canExecute.ShouldBeFalse("默认状态下复制命令必须不可执行。");
    }

    [Fact]
    public void Redacted_preview_should_be_fixed_length()
    {
        // 按真实长度回显会泄露密码长度。固定长度是刻意的。
        var (shortPreview, longPreview) = StaRunner.Run(() =>
        {
            var shortBox = CreateSecretBox();
            Enter(shortBox, "ab");

            var longBox = CreateSecretBox();
            Enter(longBox, new string('x', 40));

            return (shortBox.GetRedactedPreview(), longBox.GetRedactedPreview());
        });

        shortPreview.ShouldBe(longPreview);
        shortPreview.ShouldNotBeEmpty();
    }

    [Fact]
    public void Empty_secret_should_report_no_secret_and_empty_preview()
    {
        var (hasSecret, preview) = StaRunner.Run(() =>
        {
            var box = CreateSecretBox();
            return (box.HasSecret, box.GetRedactedPreview());
        });

        hasSecret.ShouldBeFalse();
        preview.ShouldBeEmpty();
    }

    [Fact]
    public void Revealed_state_should_default_to_false()
    {
        // 明文显示必须是用户主动操作的结果，默认绝不能是明文。
        StaRunner.Run(() => CreateSecretBox().IsRevealed).ShouldBeFalse();
    }

    [Fact]
    public void Clear_should_discard_the_secret()
    {
        var (hasSecret, preview) = StaRunner.Run(() =>
        {
            var box = CreateSecretBox();
            Enter(box, SecretValue);
            box.ClearSecret();

            return (box.HasSecret, box.GetRedactedPreview());
        });

        hasSecret.ShouldBeFalse();
        preview.ShouldBeEmpty();
    }

    [Fact]
    public void Reveal_plain_text_should_return_what_was_entered()
    {
        // 这是唯一明文出口，必须能正确工作，否则连接功能根本无法使用。
        var revealed = StaRunner.Run(() =>
        {
            var box = CreateSecretBox();
            Enter(box, SecretValue);
            return box.RevealPlainText();
        });

        revealed.ShouldBe(SecretValue);
    }

    [Fact]
    public void Secret_assigned_before_template_should_sync_to_password_box()
    {
        var (hasSecret, password) = StaRunner.Run(() =>
        {
            var box = new SecretTextBox
            {
                Label = "设备密码",
                Secret = SecretValue
            };
            ThemedHost.Host(box);

            return (box.HasSecret, box.PasswordBoxPart?.Password);
        });

        hasSecret.ShouldBeTrue();
        password.ShouldBe(SecretValue);
    }

    [Fact]
    public void Clear_command_should_not_be_executable_when_empty()
    {
        var canExecute = StaRunner.Run(() =>
            SecretTextBox.ClearCommand.CanExecute(null, CreateSecretBox()));

        canExecute.ShouldBeFalse("没有凭据时清空命令应不可用，避免用户点了没反应。");
    }

    private static SecretTextBox CreateSecretBox()
    {
        var box = new SecretTextBox { Label = "设备密码" };
        ThemedHost.Host(box);

        return box;
    }

    /// <summary>
    /// 模拟用户录入口令。
    /// </summary>
    private static void Enter(SecretTextBox box, string value)
    {
        var inner = FindPasswordBox(box);
        inner.ShouldNotBeNull();
        inner!.Password = value;
    }

    private static Wpf.Ui.Controls.PasswordBox? FindPasswordBox(SecretTextBox box) =>
        box.PasswordBoxPart;

    /// <summary>
    /// 遍历可视化树收集所有文本内容。
    /// </summary>
    private static string CollectTextInVisualTree(DependencyObject root)
    {
        var collected = new System.Text.StringBuilder();

        Walk(root);

        return collected.ToString();

        void Walk(DependencyObject node)
        {
            if (node is TextBlock textBlock)
            {
                collected.Append(textBlock.Text).Append('|');
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
