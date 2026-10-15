using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App;

/// <summary>
/// 阶段 02 基础组件验证窗口；阶段 03 将由 <c>Views/ShellWindow.xaml</c> 取代。
/// </summary>
/// <remarks>
/// 必须写全 <see cref="System.Windows.Window"/>：本程序集引用了 <c>LinkForge.Application</c> 项目，
/// 裸写 <c>Window</c> 存在被解析为命名空间的风险（与 App.xaml.cs 的 CS0118 同类问题）。
/// </remarks>
public partial class MainWindow : System.Windows.Window
{
    /// <summary>
    /// 固定时间戳。
    /// </summary>
    /// <remarks>
    /// 刻意固定而不是取当前时间：这是一个静态展示面，取实时时间会让它
    /// 看起来像真实运行记录 —— 那正是规范禁止的"用假数据伪装真实功能"。
    /// 固定值 + 文字标注，明确表达这是样例而非实测。
    /// </remarks>
    private static readonly DateTimeOffset SampleTime =
        new(2026, 10, 15, 14, 3, 21, TimeSpan.FromHours(8));

    public MainWindow()
    {
        InitializeComponent();

        LoadSampleSummary();
        LoadSampleLogs();

        // 状态文本必须等 Loaded 再更新：
        // 构造时 LINKFORGE_STARTUP_THEME 还没被 App.OnStartup 应用，
        // 此时读到的主题是"上一次的残留值"，深色启动会显示成"浅色"。
        Loaded += (_, _) => UpdateThemeStatus();
    }

    private ThemeManager? ThemeManager => (System.Windows.Application.Current as App)?.ThemeManager;

    private void OnLightThemeClick(object sender, RoutedEventArgs e) => ApplyTheme(LfThemePreference.Light);

    private void OnDarkThemeClick(object sender, RoutedEventArgs e) => ApplyTheme(LfThemePreference.Dark);

    private void OnSystemThemeClick(object sender, RoutedEventArgs e) => ApplyTheme(LfThemePreference.FollowSystem);

    private void ApplyTheme(LfThemePreference preference)
    {
        ThemeManager?.Apply(preference);
        UpdateThemeStatus();
    }

    /// <summary>
    /// 填充"部分成功"的样例结论。
    /// </summary>
    private void LoadSampleSummary()
    {
        SummaryPanel.TaskState = TaskRunState.PartiallySucceeded;
        SummaryPanel.Conclusion = "4 个步骤中 2 个通过、1 个失败、1 个未执行。";
        SummaryPanel.Note = "1 个步骤因缺少 Npcap 未执行，不计入失败。";
        SummaryPanel.Items = new ObservableCollection<ResultSummaryItem>
        {
            // 纯事实项：没有结论，因此不显示状态标签。
            new() { Label = "目标", Value = "10.20.30.40:554" },
            new() { Label = "耗时", Value = "41.2 s" },

            new() { Label = "TCP 连通性", Value = "通过", Status = DiagnosticStatus.Passed },
            new() { Label = "RTSP 握手", Value = "通过", Status = DiagnosticStatus.Passed },
            new() { Label = "鉴权", Value = "401 未授权", Status = DiagnosticStatus.Failed },
            new() { Label = "抓包取证", Value = "缺少 Npcap", Status = DiagnosticStatus.Skipped }
        };
    }

    /// <summary>
    /// 填充样例日志。
    /// </summary>
    /// <remarks>
    /// 这些日志刻意包含 RTSP URL 与 Authorization 头：
    /// 目的是在界面上直接验证脱敏确实生效 —— 若脱敏失效，
    /// 打开这个窗口就能立刻看到明文密码，而不是等产品上线后才发现。
    /// </remarks>
    private void LoadSampleLogs()
    {
        var entries = new ObservableCollection<LogEntry>
        {
            Entry(LfLogLevel.Information, "开始诊断：目标 10.20.30.40:554"),
            Entry(LfLogLevel.Debug, "构造 RTSP 地址 rtsp://admin:CameraPass123@10.20.30.40:554/Streaming/Channels/101"),
            Entry(LfLogLevel.Information, "TCP 连通性：通过（12 ms）"),
            Entry(LfLogLevel.Information, "RTSP DESCRIBE 握手：通过"),
            Entry(LfLogLevel.Warning, "鉴权返回 401，将改用 Digest 重试"),
            Entry(LfLogLevel.Debug, "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJjYW0tMDEifQ.sig-part"),
            Entry(LfLogLevel.Error, "鉴权失败：凭据被拒绝，请确认设备密码"),
            Entry(LfLogLevel.Trace, "准备启动抓包，检查 Npcap 是否可用"),
            Entry(LfLogLevel.Critical, "Npcap 未安装，抓包步骤跳过（不影响其他步骤）")
        };

        LogPanel.Entries = entries;
    }

    private static LogEntry Entry(LfLogLevel level, string message) =>
        new(SampleTime.AddMilliseconds(100), level, "Video", "sample-run", "cam-01", message);

    private void UpdateThemeStatus()
    {
        var theme = ThemeManager?.CurrentTheme ?? LfResolvedTheme.Light;

        ThemeStatusText.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"基础组件已加载 · 当前主题：{(theme == LfResolvedTheme.Dark ? "深色" : "浅色")} · 日志内容为脱敏样例，非实测记录");
    }
}
