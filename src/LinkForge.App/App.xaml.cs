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
    /// 设计令牌主题服务；阶段 03 将由 DI 容器统一提供，此处先按最小实现构造。
    /// </summary>
    public ThemeManager ThemeManager { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ThemeManager = new ThemeManager(Resources);

        // ADR-014：默认跟随系统主题。偏好持久化在阶段 03 接入。
        ThemeManager.Apply(LfThemePreference.FollowSystem);
    }
}
