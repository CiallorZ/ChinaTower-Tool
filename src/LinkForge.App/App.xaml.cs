using System.Windows;
using System.IO;
using System.Windows.Threading;
using LinkForge.Adapters.Http;
using LinkForge.Adapters.Mqtt;
using LinkForge.Adapters.Network;
using LinkForge.Adapters.Onvif;
using LinkForge.Adapters.Video;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.App.Views;
using LinkForge.Application.Diagnostics;
using LinkForge.Application.Environment;
using LinkForge.Application.Assets;
using LinkForge.Application.Logging;
using LinkForge.Application.Projects;
using LinkForge.Application.Secrets;
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
    private readonly CancellationTokenSource _lifetime = new();
    private bool _fatalErrorPresented;

    /// <summary>创建应用并安装最后一道可读错误兜底。</summary>
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

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

        try
        {
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

            // 初始化顺序不可交换：数据库迁移必须先于工作台查询。
            // 历史上 0 字节 linkforge.db 会让工作台查询先报 "no such table"，
            // 且 fire-and-forget 会把异常留成不可观察任务异常。
            _ = InitializeApplicationAsync();
        }
        catch (Exception ex)
        {
            PresentFatalStartupFailure(ex);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _lifetime.Cancel();
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _services?.Dispose();
        _lifetime.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 在窗口显示后执行异步初始化，避免 I/O 与外部进程探测阻塞 UI 线程。
    /// </summary>
    private async Task InitializeApplicationAsync()
    {
        try
        {
            var services = _services
                ?? throw new InvalidOperationException("应用服务容器尚未初始化。");
            var logs = services.GetRequiredService<IStructuredLogService>();

            // 历史日志读取失败只影响日志面板，不阻断数据库和工作台。
            try
            {
                await logs.InitializeAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                TryPublishFailure(
                    "Logging",
                    "startup.logging.failed",
                    LfLogLevel.Warning,
                    ex);
                ShowRecoverableStartupFailure(
                    "历史日志加载失败",
                    "日志面板暂不可用，但项目、资产与诊断功能会继续初始化。");
            }

            var storage = services.GetRequiredService<IStorageInitializer>();
            DatabaseInitializationResult initialization;

            try
            {
                initialization = await storage.InitializeAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                TryPublishFailure(
                    "Storage",
                    "startup.storage.failed",
                    LfLogLevel.Critical,
                    ex);

                ShowRecoverableStartupFailure(
                    "本地数据库初始化失败",
                    "项目、资产、环境与历史等依赖本地数据库的功能暂不可用。");
                return;
            }

            if (initialization.RequiresAttention)
            {
                TryPublish(
                    "Storage",
                    "startup.storage.recovered",
                    LfLogLevel.Warning,
                    initialization.Description);
            }

            // 窗口已经显示加载态；这里继续等待数据库就绪后再读取真实项目数。
            await Dashboard.LoadAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 正常退出。
        }
        catch (Exception ex)
        {
            TryPublishFailure(
                "Startup",
                "startup.initialization.failed",
                LfLogLevel.Error,
                ex);
            ShowRecoverableStartupFailure(
                "应用初始化未完全成功",
                "部分页面可能需要重新打开或重试。");
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        PresentFatalStartupFailure(e.Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        foreach (var exception in e.Exception.Flatten().InnerExceptions)
        {
            TryPublishFailure(
                "Runtime",
                "runtime.unobserved-task-exception",
                LfLogLevel.Error,
                exception);
        }

        // 已进入统一日志；观测掉异常避免进程在关闭阶段再次终止。
        e.SetObserved();
    }

    private void PresentFatalStartupFailure(Exception exception)
    {
        if (_fatalErrorPresented)
        {
            return;
        }

        _fatalErrorPresented = true;
        TryPublishFailure(
            "Startup",
            "startup.fatal",
            LfLogLevel.Critical,
            exception);

        try
        {
            MessageBox.Show(
                BuildStartupFailureMessage(exception),
                "LinkForge 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // 连错误对话框都无法创建时，退出码仍是可诊断的最后事实。
        }

        Shutdown(1);
    }

    private void ShowRecoverableStartupFailure(string title, string guidance)
    {
        var message = title
            + Environment.NewLine
            + Environment.NewLine
            + guidance
            + Environment.NewLine
            + "请查看日志面板或联系维护人员。";

        try
        {
            _ = Dispatcher.BeginInvoke(
                () => MessageBox.Show(
                    message,
                    "LinkForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning),
                DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            // 应用正在退出时不再制造新的 UI 工作。
        }
    }

    private void TryPublishFailure(
        string module,
        string eventId,
        LfLogLevel level,
        Exception exception)
    {
        TryPublish(module, eventId, level, BuildStartupFailureMessage(exception));
    }

    private void TryPublish(
        string module,
        string eventId,
        LfLogLevel level,
        string message)
    {
        try
        {
            _services?.GetService<IStructuredLogService>()?.Publish(
                new LogEntry(
                    DateTimeOffset.Now,
                    level,
                    module,
                    "startup",
                    targetId: null,
                    message,
                    eventId));
        }
        catch
        {
            // 日志服务本身不可用时，不能反过来阻断错误处理。
        }
    }

    /// <summary>
    /// 构造可展示、已脱敏的失败说明。
    /// </summary>
    internal static string BuildStartupFailureMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var lines = new List<string>
        {
            "LinkForge 遇到未处理错误。请保留以下信息用于排障："
        };

        var current = exception;
        for (var depth = 0; current is not null && depth < 4; depth++)
        {
            var message = SecretRedactor.Redact(current.Message);
            lines.Add($"{current.GetType().Name}: {message}");
            current = current.InnerException;
        }

        return string.Join(Environment.NewLine, lines);
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

        // 关于页：版本与路径读取在后台线程完成，签名状态由 WinVerifyTrust 真实返回。
        services.AddSingleton<ICodeSignatureVerifier, WindowsCodeSignatureVerifier>();
        services.AddSingleton<IAboutInfoService, WindowsAboutInfoService>();
        services.AddSingleton<IAboutActionService, AboutActionService>();

        // 脱敏：Application 层定义契约，这里注入唯一实现（ADR-004）。
        // 注册为 ITextRedactor 而不是具体类型，
        // 是为了让"忘记注入真实实现"在解析期就失败，而不是在上线后泄漏明文。
        services.AddSingleton<ITextRedactor, SecretTextRedactor>();

        // 结构化日志（LF-005）：每个页面可保留局部日志，壳层统一聚合最近记录。
        // JSONL 存储在写入前再次脱敏，磁盘与 UI 共用同一 ITextRedactor 实现。
        services.AddSingleton<IStructuredLogStore>(static provider =>
            new JsonLineStructuredLogStore(
                Path.Combine(
                    provider.GetRequiredService<AppEnvironment>().SettingsDirectory
                    ?? throw new InvalidOperationException("无法确定数据目录，日志不可用。"),
                    "logs"),
                provider.GetRequiredService<ITextRedactor>()));
        services.AddSingleton<IStructuredLogService, StructuredLogService>();
        services.AddSingleton<ILogPanelExporter, LogPanelExporter>();

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

        // ONVIF 标准接入：WS-Discovery、设备/能力/媒体 Profile 与 Stream URI。
        // 不接入厂商 SDK、媒体解码或 GB/T 28181；TLS 校验始终启用。
        services.AddSingleton<IOnvifProbe, WindowsOnvifProbe>();
        services.AddSingleton<OnvifDiagnosticAdapter>();
        services.AddSingleton<IOnvifResultExporter, OnvifResultExporter>();

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
        services.AddSingleton<IProjectPackageFileService, ProjectPackageFileService>();
        services.AddSingleton<ProjectPackageCodec>();
        services.AddSingleton<IProjectPackageImporter>(static provider =>
            new SqliteProjectPackageImporter(
                provider.GetRequiredService<SqliteContextFactory>()));

        // 凭据库：Windows DPAPI CurrentUser 密文文件。
        // 资产只保存引用键；界面、日志、项目包和导出都拿不到明文。
        services.AddSingleton<ISecretStore>(static provider =>
            new WindowsDpapiSecretStore(
                Path.Combine(
                    provider.GetRequiredService<AppEnvironment>().SettingsDirectory
                    ?? throw new InvalidOperationException("无法确定数据目录，凭据库不可用。"),
                    "secrets")));

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
        services.AddSingleton<OnvifDiagnosticsViewModel>();
        services.AddSingleton<ReportPageViewModel>();
        services.AddSingleton<DeveloperToolsViewModel>();
        services.AddSingleton<PluginManagerViewModel>();
        services.AddSingleton<AssetCatalogViewModel>();
        services.AddSingleton<AboutViewModel>();

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
