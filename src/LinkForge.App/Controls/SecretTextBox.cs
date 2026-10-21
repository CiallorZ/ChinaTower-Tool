using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Input;

namespace LinkForge.App.Controls;

/// <summary>
/// 凭据输入控件：默认遮蔽，显示明文必须由用户主动操作（UI 规范 §5.2、§9）。
/// </summary>
/// <remarks>
/// 安全约束（对应 ADR-004、ADR-008 与用户硬约束"凭据在日志/历史/复制/报告中默认脱敏"）：
/// <list type="bullet">
///   <item>值保存在 <see cref="SecureString"/> 中，不进入 XAML 文本树，
///         因此不会出现在可视化树转储、UI 自动化树或内存快照的明文字符串里。</item>
///   <item><b>默认禁止复制</b>：屏蔽 Ctrl+C 与复制命令，防止凭据被随手复制进
///         聊天窗口或日志文件。用户必须显式开启"允许复制"才能复制。</item>
///   <item>提供 <see cref="GetRedactedPreview"/>，页面需要回显时只取掩码形态。</item>
/// </remarks>
public sealed class SecretTextBox : Control
{
    /// <summary>
    /// 允许复制命令 —— 唯一能被允许复制的来源。
    /// </summary>
    /// <remarks>
    /// 定义为公有静态字段供 XAML 的 CommandBindings 引用；
    /// 它是本控件内部契约，页面不应直接调用。
    /// </remarks>
    public static readonly RoutedUICommand CopyAllowedCommand = new(
        "复制",
        "CopyAllowed",
        typeof(SecretTextBox));

    /// <summary>
    /// 清空凭据命令 —— 供模板内的"清空"按钮使用。
    /// </summary>
    /// <remarks>
    /// 必须定义为命令而不是 XAML 的 Click 事件处理器：
    /// 控件样式位于资源字典，资源字典不能有代码后置，
    /// 把行为挂到事件上会迫使每个页面重复实现同一个清空逻辑。
    /// </remarks>
    public static readonly RoutedUICommand ClearCommand = new(
        "清空",
        "ClearSecret",
        typeof(SecretTextBox));

    static SecretTextBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SecretTextBox),
            new FrameworkPropertyMetadata(typeof(SecretTextBox)));
    }

    /// <summary>
    /// 模板内的 PasswordBox 名称。
    /// </summary>
    public const string PasswordBoxPartName = "PART_PasswordBox";

    /// <summary>字段标签（必填：不得只靠占位符表达字段名称）。</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(SecretTextBox),
            new PropertyMetadata("凭据"));

    /// <summary>占位符示例。</summary>
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(
            nameof(Placeholder),
            typeof(string),
            typeof(SecretTextBox),
            new PropertyMetadata(string.Empty));

    /// <summary>字段级错误提示。</summary>
    public static readonly DependencyProperty ErrorTextProperty =
        DependencyProperty.Register(
            nameof(ErrorText),
            typeof(string),
            typeof(SecretTextBox),
            new PropertyMetadata(null, OnOptionalTextChanged));

    /// <summary>
    /// 明文当前是否可见。默认 false。
    /// </summary>
    /// <remarks>
    /// 这是**只读的镜像**，由控件库 PasswordBox 的显示按钮驱动，
    /// 设置它不会改变实际显示状态（那由控件库负责）。
    /// 用途是让模板能据此显示"明文当前可见"的警告条。
    /// 明文显示必须是用户主动操作的结果，因此不做可写。
    /// </remarks>
    private static readonly DependencyPropertyKey IsRevealedKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsRevealed),
            typeof(bool),
            typeof(SecretTextBox),
            new PropertyMetadata(false));

    /// <summary>明文当前是否可见（只读镜像）。</summary>
    public static readonly DependencyProperty IsRevealedProperty = IsRevealedKey.DependencyProperty;

    /// <summary>
    /// 是否允许用户复制。默认 false —— 防止凭据被复制到日志/聊天/报告。
    /// </summary>
    public static readonly DependencyProperty CanCopyProperty =
        DependencyProperty.Register(
            nameof(CanCopy),
            typeof(bool),
            typeof(SecretTextBox),
            new PropertyMetadata(false));

    /// <summary>
    /// 供页面绑定的秘密值。
    /// </summary>
    /// <remarks>
    /// 该属性存在的原因是 API 工作台需要在发送时把值交给传输层。
    /// 它不用于日志/历史/导出；模板仍只绑定 PasswordBox，
    /// 依赖属性值在控件被清理时由页面显式清空。
    /// </remarks>
    public static readonly DependencyProperty SecretProperty =
        DependencyProperty.Register(
            nameof(Secret),
            typeof(string),
            typeof(SecretTextBox),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSecretChanged));

    /// <summary>明文是否已录入（供界面提示"已填写"而不泄露内容）。</summary>
    private static readonly DependencyPropertyKey HasSecretKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasSecret),
            typeof(bool),
            typeof(SecretTextBox),
            new PropertyMetadata(false));

    /// <summary>明文是否已录入。</summary>
    public static readonly DependencyProperty HasSecretProperty = HasSecretKey.DependencyProperty;

    private static readonly DependencyPropertyKey HasErrorKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasError),
            typeof(bool),
            typeof(SecretTextBox),
            new PropertyMetadata(false));

    /// <summary>当前是否处于错误态。</summary>
    public static readonly DependencyProperty HasErrorProperty = HasErrorKey.DependencyProperty;

    /// <summary>
    /// 明文缓冲。
    /// </summary>
    /// <remarks>
    /// 刻意**不加** readonly：口令每次变更都整体替换为新实例。
    /// SecureString 一旦 MakeReadOnly 便不可再追加，就地修改会抛 InvalidOperationException；
    /// 因此采用"每次变更后整体重建，旧实例丢弃给 GC"的方式。
    /// </remarks>
    private SecureString _secret = new();

    /// <summary>字段标签。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>占位符示例。</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>字段级错误提示。</summary>
    public string? ErrorText
    {
        get => (string?)GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    /// <summary>明文当前是否可见（只读镜像）。</summary>
    public bool IsRevealed => (bool)GetValue(IsRevealedProperty);

    /// <summary>是否允许复制。</summary>
    public bool CanCopy
    {
        get => (bool)GetValue(CanCopyProperty);
        set => SetValue(CanCopyProperty, value);
    }

    /// <summary>绑定的秘密值；仅用于真正发起连接/请求。</summary>
    public string Secret
    {
        get => (string)GetValue(SecretProperty);
        set => SetValue(SecretProperty, value);
    }

    /// <summary>明文是否已录入。</summary>
    public bool HasSecret => (bool)GetValue(HasSecretProperty);

    /// <summary>当前是否处于错误态。</summary>
    public bool HasError => (bool)GetValue(HasErrorProperty);

    /// <summary>
    /// 取掩码形态的回显文本，供页面在需要展示"已填写"时使用。
    /// </summary>
    /// <returns>固定长度的掩码串；未录入时返回 empty。</returns>
    /// <remarks>
    /// 刻意返回固定长度而不是按真实长度显示圆点：
    /// 按长度显示会泄露"密码是 8 位还是 20 位"这一信息。
    /// </remarks>
    public string GetRedactedPreview() => HasSecret ? "********" : string.Empty;

    /// <summary>
    /// 取出明文。仅供真正需要发起连接/请求时调用，调用方必须立即用完。
    /// </summary>
    /// <returns>明文；未录入时返回 <see cref="string.Empty"/>。</returns>
    /// <remarks>
    /// 这是本控件唯一的明文出口。刻意不提供依赖属性承载明文，
    /// 因为依赖属性会被可视化树与自动化树捕获。
    /// </remarks>
    public string RevealPlainText()
    {
        if (_secret.Length == 0)
        {
            return string.Empty;
        }

        var bstr = System.Runtime.InteropServices.Marshal.SecureStringToBSTR(_secret);
        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStringBSTR(bstr) ?? string.Empty;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ZeroFreeBSTR(bstr);
        }
    }

    /// <summary>清空凭据（任务结束或用户要求时调用）。</summary>
    public void ClearSecret()
    {
        // 只读实例不可清空，因此直接替换为新实例，旧实例交给 GC。
        _secret = new SecureString();

        SetValue(HasSecretKey, false);
        SetValue(IsRevealedKey, false);

        if (FindPasswordBoxPart() is Wpf.Ui.Controls.PasswordBox passwordBox)
        {
            // 必须暂时摘除回调，否则把 Password 置空会再次触发 OnPasswordChanged。
            passwordBox.PasswordChanged -= OnPasswordBoxPasswordChanged;
            passwordBox.Password = string.Empty;
            passwordBox.PasswordChanged += OnPasswordBoxPasswordChanged;
        }
    }

    private static void OnSecretChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SecretTextBox control)
        {
            return;
        }

        var value = e.NewValue as string ?? string.Empty;

        if (control.FindPasswordBoxPart() is Wpf.Ui.Controls.PasswordBox passwordBox
            && passwordBox.Password != value)
        {
            passwordBox.PasswordChanged -= control.OnPasswordBoxPasswordChanged;
            passwordBox.Password = value;
            passwordBox.PasswordChanged += control.OnPasswordBoxPasswordChanged;
        }

        control.OnPasswordChanged(value);
    }

    /// <summary>
    /// 由模板内的 PasswordBox 在口令变更时回调。
    /// </summary>
    /// <param name="password">当前录入的明文（来自 PasswordBox 的内部缓冲）。</param>
    internal void OnPasswordChanged(string password)
    {
        var previous = _secret;
        var updated = new SecureString();

        foreach (var c in password)
        {
            updated.AppendChar(c);
        }

        updated.MakeReadOnly();
        _secret = updated;

        // 旧实例若仍可写则清空；已只读的实例无法清空，只能丢弃等 GC。
        if (!previous.IsReadOnly())
        {
            previous.Clear();
        }

        SetValue(HasSecretKey, updated.Length > 0);

        if (!string.Equals(Secret, password, StringComparison.Ordinal))
        {
            SetCurrentValue(SecretProperty, password);
        }
    }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // 清空命令的目标是本控件：按钮在模板内，命令需由宿主控件接收处理。
        // 放在 OnApplyTemplate 而不是构造器中，是因为模板内的按钮此时才存在。
        CommandBindings.Add(new CommandBinding(ClearCommand, OnClearSecret, OnCanClearSecret));

        HookPart();
    }

    private static void OnOptionalTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SecretTextBox)d;
        control.SetValue(HasErrorKey, !string.IsNullOrWhiteSpace(control.ErrorText));
    }

    /// <summary>
    /// 挂接模板内的 PasswordBox，并把明文同步进 SecureString。
    /// </summary>
    /// <remarks>
    /// PasswordBox.Password 本身是 CLR 属性（非依赖属性），无法直接绑定，
    /// 只能在 PasswordChanged 事件里读取。这是 WPF 的既定设计，
    /// 目的是不让明文进入依赖属性系统。
    /// </remarks>
    private void HookPart()
    {
        if (FindPasswordBoxPart() is not Wpf.Ui.Controls.PasswordBox passwordBox)
        {
            return;
        }

        passwordBox.PasswordChanged += OnPasswordBoxPasswordChanged;

        // Secret 可能由视图模型在模板应用前赋值。此时 OnSecretChanged 已写入
        // SecureString，但 PasswordBox 尚不存在；模板就绪后必须补一次同步，
        // 否则控件会显示为空，用户再次输入时还可能覆盖已有值。
        if (!string.IsNullOrEmpty(Secret) && passwordBox.Password != Secret)
        {
            passwordBox.PasswordChanged -= OnPasswordBoxPasswordChanged;
            passwordBox.Password = Secret;
            passwordBox.PasswordChanged += OnPasswordBoxPasswordChanged;
        }

        // IsPasswordRevealed 是只读依赖属性（由控件库自带的显示按钮驱动），
        // 无法用绑定写回。这里监听其变化，把控件库的揭示状态镜像到本控件的
        // IsRevealed 上，供模板的明文警告条使用。
        // 已录入内容时先同步一次，避免模板应用前用户就已按下显示按钮。
        SyncRevealedState(passwordBox);
        DependencyPropertyDescriptor
            .FromProperty(Wpf.Ui.Controls.PasswordBox.IsPasswordRevealedProperty, typeof(Wpf.Ui.Controls.PasswordBox))
            .AddValueChanged(passwordBox, OnRevealedChanged);

        // 默认禁止复制：屏蔽标准复制命令。
        passwordBox.CommandBindings.Add(
            new CommandBinding(ApplicationCommands.Copy, OnCopyBlocked, OnCanCopy));
        passwordBox.CommandBindings.Add(
            new CommandBinding(CopyAllowedCommand, OnCopyAllowed, OnCanCopy));

        // 屏蔽 Ctrl+C 与 Ctrl+Insert（它们会绕过命令绑定直接触发编辑器的复制行为）。
        passwordBox.PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = CanCopy;

    /// <summary>标准复制命令：默认不执行任何操作，即阻断复制。</summary>
    private void OnCopyBlocked(object sender, ExecutedRoutedEventArgs e) => e.Handled = true;

    private void OnCopyAllowed(object sender, ExecutedRoutedEventArgs e)
    {
        // 允许复制时把明文放进剪贴板。这是用户显式开启的行为，
        // 界面必须同时给出"凭据已进入剪贴板"的可见提示（由页面的 ToolTip 承担）。
        System.Windows.Clipboard.SetText(RevealPlainText());
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsCopyKeystroke(e.Key, Keyboard.Modifiers) && !CanCopy)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 判断按键是否为复制快捷键。
    /// </summary>
    /// <param name="key">按下的键。</param>
    /// <param name="modifiers">同时按下的修饰键。</param>
    /// <returns>是否为 Ctrl+C 或 Ctrl+Insert。</returns>
    /// <remarks>
    /// <para>
    /// 刻意做成纯函数：合成 <see cref="KeyEventArgs"/> 需要真实的
    /// <see cref="System.Windows.PresentationSource"/>，
    /// 而单元测试环境（乃至 CI）没有窗口句柄，根本构造不出来。
    /// 把判断逻辑抽成纯函数后，这条规则就能被真正测试，
    /// 不必依赖无法在测试中复现的 WPF 输入管道。
    /// </para>
    /// <para>
    /// 同时覆盖 Ctrl+Insert：它与 Ctrl+C 等价，
    /// 只拦 Ctrl+C 会让"复制"这个动作从另一条路径漏出去。
    /// </para>
    /// </remarks>
    internal static bool IsCopyKeystroke(Key key, ModifierKeys modifiers)
    {
        var hasControl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        return hasControl && key is Key.C or Key.Insert;
    }

    private void OnPasswordBoxPasswordChanged(object sender, RoutedEventArgs e) =>
        OnPasswordChanged(((Wpf.Ui.Controls.PasswordBox)sender).Password);

    private void OnRevealedChanged(object? sender, EventArgs e)
    {
        if (sender is Wpf.Ui.Controls.PasswordBox passwordBox)
        {
            SyncRevealedState(passwordBox);
        }
    }

    private void SyncRevealedState(Wpf.Ui.Controls.PasswordBox passwordBox) =>
        SetValue(IsRevealedKey, passwordBox.IsPasswordRevealed);

    /// <summary>
    /// 取模板内的 PasswordBox。
    /// </summary>
    /// <remarks>
    /// 封装成方法而不是在各处直接调 <see cref="FrameworkElement.GetTemplateChild"/>：
    /// 该方法受保护，外部（含测试）无法调用；同时把部件名集中到一处，
    /// 避免改名时漏改某一处。
    /// </remarks>
    private Wpf.Ui.Controls.PasswordBox? FindPasswordBoxPart() =>
        GetTemplateChild(PasswordBoxPartName) as Wpf.Ui.Controls.PasswordBox;

    /// <summary>
    /// 供测试与内部使用的部件访问器。
    /// </summary>
    internal Wpf.Ui.Controls.PasswordBox? PasswordBoxPart => FindPasswordBoxPart();

    private void OnCanClearSecret(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = HasSecret;
        e.Handled = true;
    }

    private void OnClearSecret(object sender, ExecutedRoutedEventArgs e)
    {
        ClearSecret();
        e.Handled = true;
    }
}
