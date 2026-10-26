using System.IO;

namespace LinkForge.App.Tests;

/// <summary>
/// 清理先前异常终止的测试进程留下的临时目录。
/// </summary>
/// <remarks>
/// <para>
/// DashboardHarness 等夹具依赖 Dispose 删除临时目录；测试进程被强杀时清理
/// 不会执行，残留会随反复运行累积。测试宿主可能在加载本程序集之前就创建过
/// 临时文件，因此不使用 ModuleInitializer，而是在夹具构造函数中显式调用：
/// 每次新建临时目录前，先清扫已知前缀且超过 1 小时未写入的目录，
/// 避免误删并行运行的新目录。
/// </para>
/// </remarks>
internal static class StaleTempDirectoryCleanup
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private static int _swept;

    /// <summary>清扫系统临时目录中过期的 LinkForge 测试目录。</summary>
    public static void Sweep()
    {
        if (Interlocked.Exchange(ref _swept, 1) == 1)
        {
            return;
        }

        try
        {
            var tempRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));

            foreach (var directory in Directory
                .EnumerateDirectories(tempRoot)
                .Where(IsManagedTestDirectory))
            {
                TryDeleteIfStale(directory, tempRoot);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清扫失败不影响测试结论：残留会由下一次运行重试。
        }
    }

    private static bool IsManagedTestDirectory(string path)
    {
        var name = Path.GetFileName(path);

        if (name.Equals("LinkForge-tests", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith("linkforge-", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("linkforge-designtime", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("linkforge-test-plugins", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteIfStale(string directory, string tempRoot)
    {
        try
        {
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(
                    tempRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var info = new DirectoryInfo(resolved);
            if (info.LastWriteTimeUtc <= DateTime.UtcNow - MaxAge)
            {
                info.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单个目录被占用时跳过，留给下一次运行清理。
        }
    }
}
