using System.Windows;
using LinkForge.Adapters.Http;
using LinkForge.Adapters.Mqtt;
using LinkForge.Adapters.Network;
using LinkForge.Adapters.Video;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.App.Views;
using LinkForge.Application.Diagnostics;
using LinkForge.Application.Environment;
using LinkForge.Application.Assets;
using LinkForge.Application.Projects;
using LinkForge.Application.Storage;
using LinkForge.Plugin.Abstractions;
using LinkForge.Persistence;
using LinkForge.Reporting.Reports;
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

    /// <summary>工作台视图模型；由 DI 容器提供（启动后加载真实数据）。</summary>
    public DashboardViewModel Dashboard { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = BuildServiceProvider();

        ThemeManager = _services.GetRequiredService<ThemeManager>();
        Dashboard = _services.GetRequiredService<DashboardViewModel>();

        // ADR-014：默认跟随系统主题；偏好从设置文件读取（阶段 03 起持久化）。
        //
        // 验收开关（StartupThemeVariableName）优先于已保存偏好：
        // 它存在的唯一目的是让截图脚本拿到干净的浅/深色启动画面。
        var startupOverride = ResolveStartupPreference();
        ThemeManager.Apply(startupOverride ?? ThemeManager.Preference);

        var shell = _services.GetRequiredService<ShellWindow>();
        shell.Show();

        // 窗口已显示后再加载：依赖探测会启动外部进程（ffmpeg --version 等），
        // 放在 Show 之前会让用户看到一段没有内容的空白窗口。
        // 不 await 也不阻塞：数据到达时由绑定自动刷新，首屏显示加载态。
        _ = Dashboard.LoadAsync();
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
    /// 注册的服务都是当前真正被消费的：注册一堆没人用的服务会掩盖真实的依赖关系，
    /// 也会让"这个模块到底依赖什么"失去意义。
    /// </remarks>
    /// <remarks>
    /// 可见性为 internal 而不是 private：容器装配是最容易漏注册的地方，
    /// 而漏注册的依赖只会在用户点到那个页面时才炸。
    /// 把它开放给测试，就能让"整张依赖图能否解析"成为一条自动检查。
    ///
    /// 刻意做成 static：测试宿主里已经存在一个 WPF <c>Application</c> 实例，
    /// 再 new 一个 App 会抛"不能创建多个 Application 实例"。
    /// 装配本身不依赖实例状态，因此静态方法更合适。
    /// </remarks>
    internal static ServiceProvider BuildServiceProvider()
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

        // 环境信息：单例，只读取进程自身信息（同步且必然为真）。
        services.AddSingleton(static provider =>
            new AppEnvironment(provider.GetRequiredService<AppSettingsStore>().FilePath));

        // 脱敏：Application 层定义契约，这里注入唯一实现（ADR-004）。
        // 注册为 ITextRedactor 而不是具体类型，
        // 是为了让"忘记注入真实实现"在解析期就失败，而不是在上线后泄漏明文。
        services.AddSingleton<ITextRedactor, SecretTextRedactor>();

        // 依赖探测：单例，无状态（每次探测现取）。
        services.AddSingleton(static _ => new DependencyCatalog());

        // 任务历史：文件级实现（LF-004 换 SQLite 时本接口不变）。
        // 注册为接口而不是具体类型：消费方（DashboardViewModel）依赖的是契约，
        // 若只注册具体类型，解析期就会报"无法解析 IRunHistoryStore"——
        // 真实启动验证抓到过这个缺陷。
        services.AddSingleton<IRunHistoryStore>(static provider =>
            new JsonRunHistoryStore(
                provider.GetRequiredService<AppEnvironment>().SettingsDirectory
                ?? throw new InvalidOperationException("无法确定数据目录，任务历史不可用。")));

        // 任务编排器：单例，无状态（取消/超时按次创建）。
        services.AddSingleton(static provider =>
            new DiagnosticTaskRunner(provider.GetRequiredService<ITextRedactor>()));

        // 网络探测：系统 API 与 tracert 的真实实现；测试通过接口替换系统边界。
        services.AddSingleton<INetworkProbe, WindowsNetworkProbe>();
        services.AddSingleton<NetworkDiagnosticAdapter>();
        services.AddSingleton<INetworkResultExporter, NetworkResultExporter>();

        // HTTP 传输与 API 历史：真实系统边界；UI 不直接依赖 HttpClient。
        services.AddSingleton<IHttpApiTransport, HttpApiTransport>();
        services.AddSingleton<IApiRequestHistoryStore>(static provider =>
            new JsonApiRequestHistoryStore(
                provider.GetRequiredService<AppEnvironment>().SettingsDirectory
                ?? throw new InvalidOperationException("无法确定数据目录，API 历史不可用。")));
        services.AddSingleton<IApiResultExporter, ApiResultExporter>();

        // MQTT 传输：真实 MQTTnet 客户端；UI 只依赖 IMqttTransport。
        // 注册为单例是因为页面需要保持同一条长连接与消息流，
        // 每次导航都新建客户端会让用户“切走页面就掉线”。
        services.AddSingleton<IMqttTransport, MqttTransport>();
        services.AddSingleton<IMqttResultExporter, MqttResultExporter>();

        // RTSP 信令：只做标准 RTSP/1.0 OPTIONS/DESCRIBE/SDP，不接入媒体解码。
        services.AddSingleton<IRtspProbe, WindowsRtspProbe>();
        services.AddSingleton<RtspDiagnosticAdapter>();
        services.AddSingleton<IRtspResultExporter, RtspResultExporter>();

        // 报告：应用层只依赖 IReportGenerator，HTML 模板与扫描规则集中在 Reporting 项目。
        services.AddSingleton<IReportGenerator>(static provider =>
            new ReportGenerator(provider.GetRequiredService<ITextRedactor>()));
        services.AddSingleton<IReportFileExporter, ReportFileExporter>();

        // 开发者工具：纯离线转换库不访问网络/文件；剪贴板是唯一系统边界，抽象后视图模型可测。
        services.AddSingleton<IDeveloperTransformExecutor, DeveloperTransformExecutor>();
        services.AddSingleton<IDeveloperResultClipboard, DeveloperResultClipboard>();

        // 插件宿主：清单校验与进程外执行在 Plugin.Hosting 中实现，
        // 这里只注入"扫描目录"与"运行插件"两个边界，页面不直接接触子进程 API。
        services.AddSingleton<IPluginCatalogService>(static _ => new PluginCatalogService());
        services.AddSingleton<IPluginRunExecutor, PluginRunExecutor>();
        services.AddSingleton<IPluginResultExporter, PluginResultExporter>();

        // 本地持久化（LF-004）：SQLite + EF Core 迁移。
        //
        // 上下文工厂是单例（它只持有 DbContextOptions，无状态），
        // 三个存储各自是单例（每次操作新建上下文，自身无状态）。
        // 页面只依赖 IProjectStore / IAssetStore / IEnvironmentStore 三个契约，
        // 因此换成别的存储实现时不需要改页面。
        services.AddSingleton(static _ => new SqliteContextFactory(LinkForgeDatabase.ResolveDefaultPath()));
        services.AddSingleton<IStorageInitializer>(static provider =>
            provider.GetRequiredService<SqliteContextFactory>());
        services.AddSingleton<IProjectStore>(static provider =>
            new SqliteProjectStore(provider.GetRequiredService<SqliteContextFactory>()));
        services.AddSingleton<IAssetStore>(static provider =>
            new SqliteAssetStore(provider.GetRequiredService<SqliteContextFactory>()));
        services.AddSingleton<IEnvironmentStore>(static provider =>
            new SqliteEnvironmentStore(provider.GetRequiredService<SqliteContextFactory>()));

        // CSV 导入导出：编解码在 Application，文件对话框在 App 层。
        services.AddSingleton<IAssetCsvFileService, AssetCsvFileService>();
        services.AddSingleton<AssetCsvImportService>();
        services.AddSingleton<AssetCsvExportService>();

        // 自检适配器：以适配器契约注册，
        // 之后接入海康 / 大华等进程外适配器时，调用方（工作台）无需改动。
        services.AddSingleton<IDiagnosticAdapter>(static provider =>
            new EnvironmentSelfCheckAdapter(
                provider.GetRequiredService<AppEnvironment>().SettingsDirectory
                ?? throw new InvalidOperationException("无法确定数据目录，自检证据无法落盘。"),
                provider.GetRequiredService<ITextRedactor>()));

        // 视图模型
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<NetworkDiagnosticsViewModel>();
        services.AddSingleton<ApiWorkbenchViewModel>();
        services.AddSingleton<MqttWorkbenchViewModel>();
        services.AddSingleton<RtspDiagnosticsViewModel>();
        services.AddSingleton<ReportPageViewModel>();
        services.AddSingleton<DeveloperToolsViewModel>();
        services.AddSingleton<PluginManagerViewModel>();
        services.AddSingleton<AssetCatalogViewModel>();

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
