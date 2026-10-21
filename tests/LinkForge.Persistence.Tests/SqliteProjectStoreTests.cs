using LinkForge.Application.Storage;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;
using LinkForge.Persistence;

namespace LinkForge.Persistence.Tests;

/// <summary>
/// 项目存储（LF-004）。
/// </summary>
public class SqliteProjectStoreTests
{
    [Fact]
    public async Task Upsert_should_round_trip_all_project_fields()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();
        var store = new SqliteProjectStore(database.Factory);

        var created = await store.UpsertAsync(new Project
        {
            Id = "prj-1",
            Name = "东门联调",
            Description = "东门 12 台摄像机联调",
            DefaultTimeoutSeconds = 45,
            CreatedAt = DateTimeOffset.Parse("2026-10-01T08:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture)
        });

        var loaded = await store.FindAsync("prj-1");

        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("东门联调");
        loaded.Description.ShouldBe("东门 12 台摄像机联调");
        loaded.DefaultTimeoutSeconds.ShouldBe(45);
        loaded.SchemaVersion.ShouldBe(Project.CurrentSchemaVersion);
        loaded.UpdatedAt.ShouldBe(created.UpdatedAt);
        created.UpdatedAt.ShouldNotBeNull("存储层负责填修改时间，界面据此排序。");
    }

    [Fact]
    public async Task Upsert_should_update_the_existing_row_instead_of_violating_the_key()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();
        var store = new SqliteProjectStore(database.Factory);

        var first = await store.UpsertAsync(NewProject("prj-1", "改造前"));

        var second = await store.UpsertAsync(first with { Name = "改造后", Description = "已改造" });

        second.Name.ShouldBe("改造后");
        (await store.ListAsync()).Count.ShouldBe(1, "同一 Id 必须是更新，而不是插入第二行。");
    }

    [Fact]
    public async Task Duplicate_project_name_should_fail_with_a_readable_message()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();
        var store = new SqliteProjectStore(database.Factory);

        await store.UpsertAsync(NewProject("prj-1", "重名项目"));

        var exception = await Should.ThrowAsync<StorageException>(
            () => store.UpsertAsync(NewProject("prj-2", "重名项目")));

        exception.Message.ShouldContain("重名项目");
        exception.Message.ShouldNotContain("UNIQUE", Case.Insensitive, "不要把数据库错误原文抛给用户。");
        exception.Message.ShouldNotContain("Sqlite", Case.Insensitive);
    }

    [Fact]
    public async Task List_should_order_by_most_recent_update()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var clock = new FakeTimeProvider(DateTimeOffset.Parse(
            "2026-10-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture));

        var store = new SqliteProjectStore(database.Factory, clock);

        await store.UpsertAsync(NewProject("prj-old", "较早"));
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.UpsertAsync(NewProject("prj-new", "较晚"));
        clock.Advance(TimeSpan.FromMinutes(5));

        // 重新保存"较早"的项目，它应当排到最前。
        await store.UpsertAsync(NewProject("prj-old", "较早"));

        var order = (await store.ListAsync()).Select(static p => p.Id).ToList();

        order.ShouldBe(["prj-old", "prj-new"]);
    }

    [Fact]
    public async Task Delete_should_cascade_to_assets_and_environments()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projects = new SqliteProjectStore(database.Factory);
        var assets = new SqliteAssetStore(database.Factory);
        var environments = new SqliteEnvironmentStore(database.Factory);

        var project = await projects.UpsertAsync(NewProject("prj-1", "待删除"));

        await assets.UpsertAsync(new Asset
        {
            Id = "asset-1",
            ProjectId = project.Id,
            Name = "摄像机",
            Host = "10.0.0.8"
        });

        await environments.UpsertAsync(new EnvironmentProfile
        {
            Id = "env-1",
            ProjectId = project.Id,
            Name = "测试环境",
            IsActive = true
        });

        (await projects.DeleteAsync(project.Id)).ShouldBeTrue();

        // 留下孤儿资产会让用户在切换项目后看到不属于任何项目的设备。
        (await assets.ListAsync(project.Id)).ShouldBeEmpty();
        (await environments.ListAsync(project.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_should_report_false_for_unknown_project()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();
        var store = new SqliteProjectStore(database.Factory);

        (await store.DeleteAsync("不存在的项目")).ShouldBeFalse();
    }

    [Fact]
    public async Task Find_should_return_null_for_unknown_project()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();
        var store = new SqliteProjectStore(database.Factory);

        (await store.FindAsync("不存在")).ShouldBeNull();
    }

    private static Project NewProject(string id, string name) => new()
    {
        Id = id,
        Name = name,
        CreatedAt = DateTimeOffset.UnixEpoch
    };
}
