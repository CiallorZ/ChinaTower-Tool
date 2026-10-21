using System.Runtime.InteropServices;

namespace LinkForge.Plugin.Hosting;

/// <summary>
/// 扫描一个插件根目录，返回每个插件的可验证状态。
/// </summary>
/// <remarks>
/// 扫描只读文件、计算哈希，<b>不启动任何插件进程</b>。
/// 只有 <see cref="PluginDescriptor.IsLoadable"/> 为真且用户显式确认来源后，
/// 宿主才会启动入口程序。
/// </remarks>
public sealed class PluginCatalogScanner
{
    private readonly Version _supportedApiVersion;
    private readonly Architecture _processArchitecture;

    /// <summary>创建扫描器。</summary>
    /// <param name="supportedApiVersion">宿主支持版本；null 时使用契约版本。</param>
    /// <param name="processArchitecture">当前进程架构；null 时读取运行时。</param>
    public PluginCatalogScanner(
        Version? supportedApiVersion = null,
        Architecture? processArchitecture = null)
    {
        _supportedApiVersion = supportedApiVersion ?? PluginHostContract.SupportedApiVersion;
        _processArchitecture = processArchitecture ?? RuntimeInformation.ProcessArchitecture;
    }

    /// <summary>扫描插件目录。</summary>
    /// <param name="rootDirectory">插件根目录；不存在时返回空集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按目录名排序的插件状态；单个插件失败不影响其他插件。</returns>
    public async Task<IReadOnlyList<PluginDescriptor>> ScanAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var root = Path.GetFullPath(rootDirectory);

        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<PluginDescriptor>();
        var directories = Directory
            .EnumerateDirectories(root)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ScanPluginAsync(directory, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<PluginDescriptor> ScanPluginAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, PluginHostContract.ManifestFileName);

        try
        {
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Invalid(directory, manifestPath, "插件目录是重解析点，已拒绝扫描。");
            }

            if (!File.Exists(manifestPath))
            {
                return Invalid(directory, manifestPath, "目录中缺少 manifest.json。");
            }

            var read = await PluginManifestReader
                .ReadAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);

            if (!read.Succeeded)
            {
                return new PluginDescriptor(
                    directory,
                    manifestPath,
                    PluginValidationStatus.InvalidManifest,
                    read.Error!,
                    null,
                    null);
            }

            var manifest = read.Manifest!;

            if (manifest.ApiVersion != _supportedApiVersion)
            {
                return new PluginDescriptor(
                    directory,
                    manifestPath,
                    PluginValidationStatus.IncompatibleApi,
                    $"插件要求 API {manifest.ApiVersion}，宿主仅支持 {_supportedApiVersion}。",
                    manifest,
                    null);
            }

            if (!IsArchitectureSupported(manifest.Architectures))
            {
                return new PluginDescriptor(
                    directory,
                    manifestPath,
                    PluginValidationStatus.UnsupportedPlatform,
                    $"插件声明架构 {string.Join(" / ", manifest.Architectures)}，"
                    + $"当前进程为 {_processArchitecture}。",
                    manifest,
                    null);
            }

            string entryPath;
            string hash;

            try
            {
                (entryPath, hash) = await PluginManifestReader
                    .ResolveEntryPointAsync(directory, manifest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
            {
                return new PluginDescriptor(
                    directory,
                    manifestPath,
                    PluginValidationStatus.EntryPointMissing,
                    ex.Message,
                    manifest,
                    null);
            }

            if (!string.Equals(hash, manifest.EntryPointSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new PluginDescriptor(
                    directory,
                    manifestPath,
                    PluginValidationStatus.HashMismatch,
                    $"入口哈希不一致：清单 {Short(manifest.EntryPointSha256)}，实际 {Short(hash)}。",
                    manifest,
                    hash);
            }

            return new PluginDescriptor(
                directory,
                manifestPath,
                PluginValidationStatus.Compatible,
                $"入口 {Path.GetFileName(entryPath)} 校验通过。",
                manifest,
                hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return new PluginDescriptor(
                directory,
                manifestPath,
                PluginValidationStatus.ScanError,
                $"扫描失败：{ex.GetType().Name} — {ex.Message}",
                null,
                null);
        }
    }

    private bool IsArchitectureSupported(IReadOnlyList<string> architectures)
    {
        if (architectures.Contains("any", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var current = _processArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => _processArchitecture.ToString().ToLowerInvariant()
        };

        // x64 宿主可以启动 x86 子进程；x86 宿主不能启动 x64 进程。
        return architectures.Contains(current, StringComparer.OrdinalIgnoreCase)
            || (_processArchitecture == Architecture.X64
                && architectures.Contains("x86", StringComparer.OrdinalIgnoreCase));
    }

    private static PluginDescriptor Invalid(string directory, string manifestPath, string detail) =>
        new(
            directory,
            manifestPath,
            PluginValidationStatus.InvalidManifest,
            detail,
            null,
            null);

    private static string Short(string hash) =>
        hash.Length <= 12 ? hash : hash[..12] + "…";
}
