using LinkForge.Application.Storage;
using LinkForge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence.Tests;

/// <summary>
/// 测试用的临时数据库。
/// </summary>
/// <remarks>
/// <para>
/// 每个测试一个独立目录，避免测试之间通过文件互相影响。
/// 目录建在系统临时目录下，并在 <see cref="Dispose"/> 时删除 ——
/// 删除前显式校验目标仍在临时目录内，否则宁可留下文件也不误删用户目录
/// （LF-012 的插件测试夹具踩过这个坑）。
/// </para>
/// <para>
/// SQLite 会为每个连接留下 <c>-wal</c>/<c>-shm</c> 文件，
/// 因此清理必须按目录整体进行，而不是只删主文件。
/// </para>
/// </remarks>
internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _directory;

    /// <summary>创建并在临时目录中初始化数据库。</summary>
    public TemporaryDatabase()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "linkforge-persistence-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);

        DatabasePath = Path.Combine(_directory, LinkForgeDatabase.DefaultFileName);
        Factory = new SqliteContextFactory(DatabasePath);
    }

    /// <summary>数据库文件路径。</summary>
    public string DatabasePath { get; }

    /// <summary>上下文工厂（与 App 装配使用同一种类型）。</summary>
    public SqliteContextFactory Factory { get; }

    /// <summary>临时目录（用于校验隔离文件位置）。</summary>
    /// <remarks>
    /// 刻意不叫 <c>Directory</c>：那会遮蔽 <see cref="System.IO.Directory"/>，
    /// 同一文件里的 <c>Directory.Delete</c> 会被解析成实例成员而编译失败。
    /// </remarks>
    public string DirectoryPath => _directory;

    /// <summary>初始化数据库结构。</summary>
    public Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        Factory.InitializeAsync(cancellationToken);

    /// <summary>用原生 SQL 读取 <c>PRAGMA table_info</c>，用于守卫数据库结构。</summary>
    public async Task<IReadOnlyList<(string Name, string Type, bool NotNull)>> ReadColumnsAsync(
        string table,
        CancellationToken cancellationToken = default)
    {
        await using var context = Factory.CreateContext();
        await using var command = context.Database.GetDbConnection().CreateCommand();

        await context.Database.OpenConnectionAsync(cancellationToken);
        command.CommandText = $"PRAGMA table_info({table})";

        var columns = new List<(string, string, bool)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1));
        }

        return columns;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            var resolved = Path.GetFullPath(_directory);
            var tempRoot = Path.GetFullPath(Path.GetTempPath());

            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                // 只清理临时目录下的内容；不在临时目录里说明路径被改写过，直接放弃。
                return;
            }

            if (System.IO.Directory.Exists(resolved))
            {
                System.IO.Directory.Delete(resolved, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论：临时目录由操作系统回收。
        }
    }
}
