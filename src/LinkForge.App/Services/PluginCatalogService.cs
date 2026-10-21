using System.IO;
using LinkForge.Plugin.Hosting;

namespace LinkForge.App.Services;

/// <summary>插件目录扫描边界。</summary>
public interface IPluginCatalogService
{
    /// <summary>插件根目录。</summary>
    string PluginsDirectory { get; }

    /// <summary>异步扫描插件目录；不启动插件进程。</summary>
    Task<IReadOnlyList<PluginDescriptor>> ScanAsync(CancellationToken cancellationToken);
}

/// <summary>默认插件目录扫描服务。</summary>
/// <remarks>
/// 插件放在 <c>%LOCALAPPDATA%\LinkForge\plugins</c>。扫描本身由宿主库执行，
/// 此处只负责定位目录，并把同步目录枚举/哈希计算移到线程池，避免阻塞 UI。
/// </remarks>
public sealed class PluginCatalogService : IPluginCatalogService
{
    private readonly PluginCatalogScanner _scanner;

    /// <summary>创建扫描服务。</summary>
    public PluginCatalogService(PluginCatalogScanner? scanner = null)
    {
        _scanner = scanner ?? new PluginCatalogScanner();
        PluginsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinkForge",
            "plugins");
    }

    /// <inheritdoc />
    public string PluginsDirectory { get; }

    /// <inheritdoc />
    public Task<IReadOnlyList<PluginDescriptor>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () => _scanner.ScanAsync(PluginsDirectory, cancellationToken),
            cancellationToken);
}
