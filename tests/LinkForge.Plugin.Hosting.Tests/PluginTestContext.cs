using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinkForge.Plugin.Hosting.Tests;

/// <summary>创建临时插件目录与真实清单。</summary>
internal sealed class PluginTestContext : IDisposable
{
    private readonly string _root;

    public PluginTestContext()
    {
        _root = Path.Combine(Path.GetTempPath(), $"linkforge-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
    }

    public string PluginsRoot => Path.Combine(_root, "plugins");

    /// <summary>复制夹具可执行文件并生成匹配哈希的清单。</summary>
    public string CreatePlugin(
        string directoryName = "test.sample",
        string pluginId = "test.sample",
        Version? apiVersion = null,
        string[]? architectures = null,
        string? entryPoint = null,
        bool corruptHash = false,
        bool writeManifest = true,
        string? entryPointOverride = null)
    {
        var directory = Path.Combine(PluginsRoot, directoryName);
        Directory.CreateDirectory(directory);

        var source = FindTestPluginExecutable();
        var entryName = entryPointOverride
            ?? Path.GetFileName(source);
        var entryPath = Path.Combine(directory, entryName);

        if (entryPointOverride is null)
        {
            CopyAppHostBundle(source, directory);
        }

        if (!writeManifest)
        {
            return directory;
        }

        var hash = corruptHash
            ? new string('0', 64)
            : entryPointOverride is null
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entryPath))).ToLowerInvariant()
                : new string('a', 64);

        var manifest = new
        {
            id = pluginId,
            displayName = "测试插件",
            version = "1.0.0",
            apiVersion = (apiVersion ?? new Version(1, 0)).ToString(2),
            publisher = "LinkForge Tests",
            description = "仅用于自动化测试的插件夹具。",
            capabilities = new[] { "test.echo" },
            architectures = architectures ?? ["any"],
            entryPoint = entryPoint ?? entryName,
            entryPointSha256 = hash,
            signatureStatus = "unsigned",
            steps = new[]
            {
                new
                {
                    id = "probe",
                    name = "夹具探针",
                    timeoutSeconds = 5,
                    dependencies = Array.Empty<string>(),
                    dependsOn = Array.Empty<string>()
                }
            }
        };

        File.WriteAllText(
            Path.Combine(directory, PluginHostContract.ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return directory;
    }

    /// <summary>写入一个自定义清单。</summary>
    public string WriteManifest(string directory, string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, PluginHostContract.ManifestFileName),
            json,
            new UTF8Encoding(false));
        return directory;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows 在子进程退出后可能短暂保留 apphost 文件句柄。
                // 重试，但仍不把测试清理失败伪装成产品失败；最后一次留给系统清理。
                if (attempt < 4)
                {
                    Thread.Sleep(100 * (attempt + 1));
                }
            }
        }
    }

    private static string FindTestPluginExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "LinkForge.TestPluginHost.exe"),
            Path.Combine(baseDirectory, "TestPluginHost", "LinkForge.TestPluginHost.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"未找到夹具插件可执行文件。已查找：{string.Join("；", candidates)}。请确认测试项目已构建。");
    }

    /// <summary>
    /// 复制 apphost 及其运行时文件。
    /// </summary>
    /// <remarks>
    /// 只复制 .exe 会让 apphost 在目标目录找不到同名 .dll，进程会以
    /// “application to execute does not exist”立即退出；这不是插件代码失败。
    /// </remarks>
    private static void CopyAppHostBundle(string sourceExecutable, string targetDirectory)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceExecutable)!;
        var sourceName = Path.GetFileNameWithoutExtension(sourceExecutable);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, sourceName + ".*"))
        {
            File.Copy(
                file,
                Path.Combine(targetDirectory, Path.GetFileName(file)),
                overwrite: true);
        }
    }
}
