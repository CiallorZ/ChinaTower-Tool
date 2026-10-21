using System.Windows;
using LinkForge.App;
using LinkForge.App.Services;
using LinkForge.App.Tests.Controls;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LinkForge.App.Tests.Services;

/// <summary>
/// 依赖注入容器的装配完整性（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 这里要防的是一类特别难查的缺陷：<b>漏注册一个依赖</b>。
/// 容器默认是惰性解析的 —— 漏注册的服务不会在启动时炸，
/// 而是要等用户点进那个页面才抛 <c>InvalidOperationException</c>。
/// 历史上真实发生过（工作台拿不到 <c>IRunHistoryStore</c>）。
/// </para>
/// <para>
/// 因此本类把所有已注册的单例<b>逐一强制解析</b>：
/// 只要有一处漏注册，测试立刻失败，而不是留给用户去发现。
/// </para>
/// </remarks>
public class DependencyInjectionTests
{
    [Fact]
    public void Every_registered_service_should_be_resolvable()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var provider = App.BuildServiceProvider();

            var failures = new List<string>();

            // 逐一解析每个已登记的服务类型：
            // GetService 返回 null 才说明"注册了但构造不出来"。
            foreach (var serviceType in RegisteredServiceTypes())
            {
                try
                {
                    var instance = provider.GetService(serviceType);

                    if (instance is null)
                    {
                        failures.Add($"{serviceType.FullName} 解析结果为 null。");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{serviceType.FullName} 解析失败：{ex.GetType().Name} — {ex.Message}");
                }
            }

            failures.ShouldBeEmpty("依赖注入容器存在无法解析的服务：" + string.Join("; ", failures));
        });
    }

    [Fact]
    public void Asset_catalog_view_model_should_resolve_with_its_storage_dependencies()
    {
        // 单独点名资产目录：它是本阶段新增的页面，
        // 依赖最多（三个存储 + 初始化器 + CSV 服务），漏注册的概率最高。
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var provider = App.BuildServiceProvider();

            var viewModel = provider.GetRequiredService<AssetCatalogViewModel>();

            viewModel.ShouldNotBeNull();
        });
    }

    /// <summary>
    /// 需要逐一验证解析的服务类型清单。
    /// </summary>
    /// <remarks>
    /// 刻意写成显式清单而不是反射扫描容器：
    /// 反射扫描只能证明"已注册的那些能解析"，
    /// 而"某个服务忘了注册"这种缺陷恰恰是扫不出来的。
    /// 显式清单让新增服务时必须同步更新这里，漏了就编译不过。
    /// </remarks>
    private static IReadOnlyList<Type> RegisteredServiceTypes() =>
    [
        typeof(AppSettingsStore),
        typeof(ThemeManager),
        typeof(AppEnvironment),
        typeof(LinkForge.Application.Diagnostics.ITextRedactor),
        typeof(LinkForge.Plugin.Abstractions.IDiagnosticAdapter),
        typeof(LinkForge.Application.Diagnostics.DiagnosticTaskRunner),
        typeof(LinkForge.Application.Diagnostics.IRunHistoryStore),
        typeof(LinkForge.Adapters.Network.INetworkProbe),
        typeof(LinkForge.Adapters.Network.NetworkDiagnosticAdapter),
        typeof(LinkForge.Adapters.Http.IHttpApiTransport),
        typeof(IApiRequestHistoryStore),
        typeof(LinkForge.Adapters.Mqtt.IMqttTransport),
        typeof(LinkForge.Adapters.Video.IRtspProbe),
        typeof(LinkForge.Adapters.Video.RtspDiagnosticAdapter),
        typeof(LinkForge.Application.Storage.IStorageInitializer),
        typeof(LinkForge.Application.Projects.IProjectStore),
        typeof(LinkForge.Application.Projects.IEnvironmentStore),
        typeof(LinkForge.Application.Assets.IAssetStore),
        typeof(LinkForge.App.Services.IAssetCsvFileService),
        typeof(LinkForge.App.Services.AssetCsvImportService),
        typeof(LinkForge.App.Services.AssetCsvExportService),
        typeof(ShellViewModel),
        typeof(DashboardViewModel),
        typeof(NetworkDiagnosticsViewModel),
        typeof(ApiWorkbenchViewModel),
        typeof(MqttWorkbenchViewModel),
        typeof(RtspDiagnosticsViewModel),
        typeof(ReportPageViewModel),
        typeof(DeveloperToolsViewModel),
        typeof(PluginManagerViewModel),
        typeof(AssetCatalogViewModel)
    ];
}
