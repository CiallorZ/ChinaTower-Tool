using System.Windows;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.App.Views;
using Microsoft.Extensions.DependencyInjection;

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

    private ServiceProvider? _services;

    /// <summary>
    /// 设计令牌主题服务；由 DI 容器提供。
    /// </summary>
    public ThemeManager ThemeManager { get; internal set; } = null!;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = BuildServiceProvider();

        ThemeManager = _services.GetRequiredService<ThemeManager>();

        // ADR-014：默认跟随系统主题；偏好从设置文件读取（阶段 03 起持久化）。
        //
        // 验收开关（StartupThemeVariableName）优先于已保存偏好：
        // 它存在的唯一目的是让截图脚本拿到干净的浅/深色启动画面。
        var startupOverride = ResolveStartupPreference();
        ThemeManager.Apply(startupOverride ?? ThemeManager.Preference);

        // 首个窗口关闭即退出：本应用是单窗口工具，
        // 保留默认 OnLastWindowClose 语义即可（测试宿主另设为 OnExplicitShutdown）。
        var shell = _services.GetRequiredService<ShellWindow>();
        shell.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 组装依赖注入容器。
    /// </summary>
    /// <remarks>
    /// 阶段 03 只注册壳层真正需要的服务；业务模块的适配器在后续阶段按需加入。
    /// 保持容器精简，避免"注册了一堆没人用的服务"掩盖真实的依赖关系。
    /// </remarks>
    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        // 设置存储：单例，内部无状态（每次操作直接读写文件）。
        services.AddSingleton(static _ => new AppSettingsStore());

        // 主题服务：单例，持有当前调色板替换目标。
        services.AddSingleton(static provider =>
        {
            var store = provider.GetRequiredService<AppSettingsStore>();

            return new ThemeManager(Current.Resources, store);
        });

        // 环境信息：单例，只读探测。
        services.AddSingleton(static provider =>
            new AppEnvironment(provider.GetRequiredService<AppSettingsStore>().FilePath));

        // 视图模型
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();

        // 视图
        services.AddSingleton<ShellWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 解析启动主题覆盖项。
    /// </summary>
    /// <remarks>
    /// 非法或未知取值返回 null（表示"不覆盖"），而不是抛异常：
    /// 一个仅供验收的开关不应具备让应用启动失败的能力。
    /// </remarks>
    internal static LfThemePreference? ResolveStartupPreference()
    {
        var value = Environment.GetEnvironmentVariable(StartupThemeVariableName);

        return value?.Trim().ToLowerInvariant() switch
        {
            "light" => LfThemePreference.Light,
            "dark" => LfThemePreference.Dark,
            "system" => LfThemePreference.FollowSystem,
            _ => null
        };
    }
}