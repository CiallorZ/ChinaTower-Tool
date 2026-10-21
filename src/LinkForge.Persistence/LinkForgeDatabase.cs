using System.Data.Common;
using LinkForge.Application.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence;

/// <summary>
/// SQLite 数据库的定位、打开与结构初始化（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// <b>损坏不得让应用无法启动。</b>数据库文件可能是上一版写的、被同步盘截断的、
/// 或被用户手工改过的。遇到"文件损坏"必须隔离现场（改名保留）后重建，
/// 并且<b>如实告知用户</b>原数据已隔离 —— 静默重建会让用户以为数据凭空消失。
/// </para>
/// <para>
/// 结构升级走 EF Core 迁移（ADR-003），因此"旧版本数据库打开"是可测的：
/// 先建到旧迁移，再用新版打开，断言数据仍在且新列已补齐。
/// </para>
/// </remarks>
public static class LinkForgeDatabase
{
    /// <summary>数据库文件名。</summary>
    public const string DefaultFileName = "linkforge.db";

    /// <summary>
    /// 默认数据库路径：<c>%LOCALAPPDATA%\LinkForge\linkforge.db</c>。
    /// </summary>
    /// <remarks>
    /// 用户级目录，不需要管理员权限；与 <c>settings.json</c>、
    /// <c>run-history.json</c> 同目录，便于"整个目录一起拷贝到现场机器"。
    /// </remarks>
    public static string ResolveDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LinkForge",
        DefaultFileName);

    /// <summary>
    /// 构造上下文选项。
    /// </summary>
    /// <param name="databasePath">数据库文件路径。</param>
    /// <returns>选项。</returns>
    public static DbContextOptions<LinkForgeDbContext> BuildOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        return new DbContextOptionsBuilder<LinkForgeDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
    }

    /// <summary>
    /// 打开一个上下文。
    /// </summary>
    /// <param name="databasePath">数据库文件路径。</param>
    /// <returns>上下文；调用方负责释放。</returns>
    public static LinkForgeDbContext Open(string databasePath) =>
        new(BuildOptions(databasePath));

    /// <summary>
    /// 确保目录存在并应用迁移。
    /// </summary>
    /// <param name="databasePath">数据库文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>初始化结果。</returns>
    /// <remarks>
    /// 只有在"文件损坏"时才重建；其它异常（磁盘满、无权限、文件被占用）直接抛出，
    /// 由调用方显示可读错误 —— 把"磁盘满了"当成"数据损坏"去重建是破坏性行为。
    /// </remarks>
    public static async Task<DatabaseInitializationResult> InitializeAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(databasePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existedBefore = File.Exists(databasePath);

        try
        {
            await using var context = Open(databasePath);

            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)
                .ConfigureAwait(false)).ToList();

            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            var outcome = !existedBefore
                ? DatabaseInitializationOutcome.Created
                : pending.Count > 0
                    ? DatabaseInitializationOutcome.Upgraded
                    : DatabaseInitializationOutcome.AlreadyCurrent;

            return new DatabaseInitializationResult(outcome, databasePath, Note: null);
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            var quarantineName = Quarantine(databasePath);

            await using var context = Open(databasePath);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            return new DatabaseInitializationResult(
                DatabaseInitializationOutcome.RecoveredFromCorruption,
                databasePath,
                quarantineName);
        }
    }

    /// <summary>
    /// 判断异常是否代表"文件不是可用的数据库"。
    /// </summary>
    /// <remarks>
    /// 只认 SQLite 的 CORRUPT / NOTADB 两个错误码。
    /// 权限、磁盘满、占用等一律不在此列 —— 那些情况下重建数据库会丢掉用户数据，
    /// 而用户真正需要的是看到"文件被占用/磁盘空间不足"。
    /// </remarks>
    private static bool IsCorruption(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is SqliteException sqlite
                && sqlite.SqliteErrorCode is 11 or 26)
            {
                return true;
            }

            if (exception is DbException dbException
                && dbException.Message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (exception.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("file is encrypted or is not a database", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }

    /// <summary>
    /// 把损坏的数据库文件改名保留，返回新的文件名（供如实告知用户）。
    /// </summary>
    /// <param name="databasePath">数据库文件路径。</param>
    /// <param name="stamp">用于生成隔离文件名的时刻。</param>
    /// <returns>隔离后的主文件名。</returns>
    /// <remarks>
    /// <para>
    /// WAL 与 SHM 必须一起挪走：只挪主文件会让新库继承旧的预写日志，
    /// 结果是"刚重建完就又损坏一次"。
    /// </para>
    /// <para>
    /// 挪文件之前必须先 <see cref="SqliteConnection.ClearAllPools"/>：
    /// Microsoft.Data.Sqlite 默认启用连接池，池里的连接仍然持有文件句柄，
    /// 于是 <c>File.Move</c> 会因共享冲突失败，隔离失败后重试又撞回同一个坏文件。
    /// 这一条是本地测试真实抓到的（LF-004）。
    /// </para>
    /// <para>
    /// 隔离失败时不假装恢复成功，而是抛可读错误：把"文件被其它进程占用"
    /// 说清楚，比抛一个 SQLite 英文错误有用得多。
    /// </para>
    /// <para>
    /// 本方法公开是为了能被直接测试 —— SQLite 在打开损坏库时往往会自行清理
    /// <c>-wal</c>/<c>-shm</c>，因此"三个文件一起挪走"这条逻辑无法透过
    /// 恢复流程的端到端用例观察到。
    /// </para>
    /// </remarks>
    public static string QuarantineFiles(string databasePath, DateTimeOffset stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        SqliteConnection.ClearAllPools();

        var stampText = stamp.ToString(
            "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture);

        var quarantined = $"{databasePath}.corrupt-{stampText}";
        var movedAny = false;

        foreach (var (suffix, targetSuffix) in new[]
                 {
                     (string.Empty, string.Empty),
                     ("-wal", "-wal"),
                     ("-shm", "-shm")
                 })
        {
            var source = databasePath + suffix;

            if (!File.Exists(source))
            {
                continue;
            }

            try
            {
                File.Move(source, quarantined + targetSuffix, overwrite: true);
                movedAny = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件挪不动时继续尝试其余文件，最后统一判断是否完成了隔离。
            }
        }

        if (!movedAny)
        {
            throw new StorageException(
                "本地数据库文件已损坏，且当前无法隔离它（文件可能被另一个 LinkForge 实例或其它程序占用）。"
                + "请关闭其它实例后重启本程序。");
        }

        return Path.GetFileName(quarantined);
    }

    /// <summary>隔离损坏的数据库文件（使用当前时间生成隔离名）。</summary>
    /// <param name="databasePath">数据库文件路径。</param>
    /// <returns>隔离后的主文件名。</returns>
    private static string Quarantine(string databasePath) =>
        QuarantineFiles(databasePath, DateTimeOffset.Now);
}
