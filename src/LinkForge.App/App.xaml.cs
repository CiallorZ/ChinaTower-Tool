using System.Windows;
using LinkForge.App.Services;

namespace LinkForge.App;

/// <summary>
/// 应用入口。
/// </summary>
/// <remarks>
/// 必须写全 <see cref="System.Windows.Application"/>：本程序集引用了
/// <c>LinkForge.Application</c> 项目，裸写 <c>Application</c> 会被解析为命名空间而非类型（CS0118）。
/// </remarks>
public partial class App : System.Windows.Application
{
    /// <summary>
    /// 阶段验收截图用的启动主题环境变量名。
    /// </summary>
    /// <remarks>
    /// 设为 public 是为了让测试也能引用同一个常量：
    /// 若脚本与代码各写一份字符串，改名时必然有一处漏改。
    /// </remarks>
    public const string StartupThemeVariableName = "LINKFORGE_STARTUP_THEME";

    /// <summary>
    /// 设计令牌主题服务；阶段 03 将由 DI 容器统一提供，此处先按最小实现构造。
    /// </summary>
    public ThemeManager ThemeManager { get; internal set; } = null!;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ThemeManager = new ThemeManager(Resources);

        // ADR-014：默认跟随系统主题。偏好持久化在阶段 03 接入。
        //
        // StartupThemeVariableName 仅用于阶段验收截图：
        // 让用户不必手动点按钮就能拿到浅/深色两张干净的启动截图。
        // 不设该变量时行为完全不变（跟随系统）。
        ThemeManager.Apply(ResolveStartupPreference());
    }

    /// <summary>
    /// 解析启动主题偏好。
    /// </summary>
    /// <remarks>
    /// 非法或未知取值一律回退到"跟随系统"，而不是抛异常：
    /// 一个仅供验收的开关不应具备让应用启动失败的能力。
    /// </remarks>
    internal static LfThemePreference ResolveStartupPreference()
    {
        var value = Environment.GetEnvironmentVariable(StartupThemeVariableName);

        return value?.Trim().ToLowerInvariant() switch
        {
            "light" => LfThemePreference.Light,
            "dark" => LfThemePreference.Dark,
            _ => LfThemePreference.FollowSystem
        };
    }
}