using System.Windows;
using LinkForge.App.Services;

namespace LinkForge.App;

/// <summary>
/// 阶段 01 设计令牌验证窗口；阶段 03 将由 <c>Views/ShellWindow.xaml</c> 取代。
/// </summary>
/// <remarks>
/// 必须写全 <see cref="System.Windows.Window"/>：本程序集引用了 <c>LinkForge.Application</c> 项目，
/// 裸写 <c>Window</c> 存在被解析为命名空间的风险（与 App.xaml.cs 的 CS0118 同类问题）。
/// </remarks>
public partial class MainWindow : System.Windows.Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateThemeStatus();
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

    private void UpdateThemeStatus()
    {
        var theme = ThemeManager?.CurrentTheme ?? LfResolvedTheme.Light;
        ThemeStatusText.Text = $"设计令牌已加载 · 当前主题：{(theme == LfResolvedTheme.Dark ? "深色" : "浅色")}";
    }
}
