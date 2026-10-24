using LinkForge.Application.Storage;
using LinkForge.Domain.Projects;
using LinkForge.Persistence;

namespace LinkForge.Persistence.Tests;

/// <summary>
/// 数据库定位、初始化与损坏恢复（LF-004）。
/// </summary>
public class DatabaseInitializationTests
{
    [Fact]
    public async Task First_initialization_should_create_the_database_file()
    {
        using var database = new TemporaryDatabase();

        var result = await database.InitializeAsync();

        result.Outcome.ShouldBe(DatabaseInitializationOutcome.Created);
        File.Exists(database.DatabasePath).ShouldBeTrue("首次初始化必须真的落盘，而不是只建目录。");
    }

    [Fact]
    public async Task Second_initialization_should_report_already_current()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var result = await database.InitializeAsync();

        result.Outcome.ShouldBe(DatabaseInitializationOutcome.AlreadyCurrent);
    }

    [Fact]
    public async Task Zero_byte_database_should_initialize_into_a_usable_schema()
    {
        using var database = new TemporaryDatabase();

        // 用户目录里可能残留一个 0 字节的 linkforge.db：
        // 文件存在但尚未写入 SQLite 头。启动时必须把它当作空库迁移，
        // 而不是在工作台查询时抛 "no such table"。
        await File.WriteAllBytesAsync(database.DatabasePath, []);

        var result = await database.InitializeAsync();

        result.Outcome.ShouldBe(DatabaseInitializationOutcome.Upgraded);

        var projectStore = new SqliteProjectStore(database.Factory);
        var saved = await projectStore.UpsertAsync(NewProject("零点数据库"));

        (await projectStore.FindAsync(saved.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Initialization_should_keep_existing_data()
    {
        // "旧版本数据库打开"的核心风险不是结构，而是"打开之后数据还在不在"。
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projectStore = new SqliteProjectStore(database.Factory);
        var saved = await projectStore.UpsertAsync(NewProject("现场项目"));

        await database.InitializeAsync();

        var reloaded = await projectStore.FindAsync(saved.Id);
        reloaded.ShouldNotBeNull();
        reloaded.Name.ShouldBe("现场项目");
    }

    [Fact]
    public async Task Corrupt_file_should_be_quarantined_and_database_rebuilt()
    {
        using var database = new TemporaryDatabase();

        // 写一个"不是数据库"的文件：SQLite 打开时会报 NOTADB，
        // 这正是同步盘截断或误覆盖之后现场机器的真实形态。
        await File.WriteAllTextAsync(database.DatabasePath, "这不是一个 SQLite 数据库");

        var result = await database.InitializeAsync();

        result.Outcome.ShouldBe(DatabaseInitializationOutcome.RecoveredFromCorruption);
        result.Note.ShouldNotBeNullOrWhiteSpace("必须如实告诉用户原文件被隔离到哪里。");

        // 重建后的库必须可用。
        var projectStore = new SqliteProjectStore(database.Factory);
        var saved = await projectStore.UpsertAsync(NewProject("重建后"));
        (await projectStore.FindAsync(saved.Id)).ShouldNotBeNull();

        // 坏文件必须被保留下来，而不是被删掉。
        var quarantined = Directory
            .EnumerateFiles(database.DirectoryPath, "*.corrupt-*")
            .ToList();

        quarantined.ShouldNotBeEmpty("隔离的意义是保留现场，不是丢弃。");
    }

    [Fact]
    public async Task Quarantine_should_move_the_database_plus_wal_and_shm_files()
    {
        using var database = new TemporaryDatabase();

        await File.WriteAllTextAsync(database.DatabasePath, "坏文件");
        await File.WriteAllTextAsync(database.DatabasePath + "-wal", "坏日志");
        await File.WriteAllTextAsync(database.DatabasePath + "-shm", "坏共享内存");

        var stamp = DateTimeOffset.Parse(
            "2026-10-22T09:30:00+08:00",
            System.Globalization.CultureInfo.InvariantCulture);

        var name = LinkForgeDatabase.QuarantineFiles(database.DatabasePath, stamp);

        name.ShouldBe("linkforge.db.corrupt-20261022-093000");

        // 只挪主文件会让新库继承旧的预写日志，表现为"刚重建完又损坏一次"。
        File.Exists(database.DatabasePath + "-wal").ShouldBeFalse();
        File.Exists(database.DatabasePath + "-shm").ShouldBeFalse();
        File.Exists(Path.Combine(database.DirectoryPath, name)).ShouldBeTrue();
        File.Exists(Path.Combine(database.DirectoryPath, name + "-wal")).ShouldBeTrue();
        File.Exists(Path.Combine(database.DirectoryPath, name + "-shm")).ShouldBeTrue();
    }

    [Fact]
    public async Task Quarantine_should_fail_loudly_when_the_file_cannot_be_moved()
    {
        using var database = new TemporaryDatabase();
        await File.WriteAllTextAsync(database.DatabasePath, "坏文件");

        // 独占打开：模拟"另一个实例正占着数据库"。
        await using var hold = new FileStream(
            database.DatabasePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var exception = await Should.ThrowAsync<StorageException>(
            () => Task.Run(() => LinkForgeDatabase.QuarantineFiles(
                database.DatabasePath,
                DateTimeOffset.UnixEpoch)));

        // 假装恢复成功会让用户以为数据被重建了，而实际上坏文件还在原地。
        exception.Message.ShouldContain("占用");
    }

    [Fact]
    public void Default_path_should_live_under_local_app_data()
    {
        var path = LinkForgeDatabase.ResolveDefaultPath();

        path.ShouldEndWith("linkforge.db");
        path.ShouldContain("LinkForge");
        path.ShouldNotContain("Roaming", Case.Insensitive, "用户级数据放 Local 而不是 Roaming：不需要跟着漫游配置文件同步。");
    }

    private static Project NewProject(string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        CreatedAt = DateTimeOffset.UnixEpoch
    };
}
