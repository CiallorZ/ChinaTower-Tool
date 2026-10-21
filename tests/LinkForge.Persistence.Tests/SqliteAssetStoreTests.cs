using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;
using LinkForge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence.Tests;

/// <summary>
/// 资产存储（LF-004）。
/// </summary>
public class SqliteAssetStoreTests
{
    [Fact]
    public async Task Upsert_should_round_trip_tags_and_optional_fields()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        await store.UpsertAsync(new Asset
        {
            Id = "asset-1",
            ProjectId = "prj-1",
            Name = "东门球机",
            Host = "10.0.0.8",
            Port = 8000,
            Kind = AssetKind.Camera,
            Vendor = "海康",
            Tags = ["东门", "球机", "待联调"],
            CredentialReference = "asset.cam-01.password",
            Note = "夜间补光弱"
        });

        var loaded = await store.FindAsync("asset-1");

        loaded.ShouldNotBeNull();
        loaded.Host.ShouldBe("10.0.0.8");
        loaded.Port.ShouldBe(8000);
        loaded.Kind.ShouldBe(AssetKind.Camera);
        loaded.Vendor.ShouldBe("海康");
        loaded.Tags.ShouldBe(["东门", "球机", "待联调"]);
        loaded.CredentialReference.ShouldBe("asset.cam-01.password");
        loaded.Note.ShouldBe("夜间补光弱");
        loaded.TargetText.ShouldBe("10.0.0.8:8000");
    }

    [Fact]
    public async Task Upsert_should_update_the_existing_row_instead_of_duplicating_it()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        await store.UpsertAsync(NewAsset("asset-1", "改造前"));
        await store.UpsertAsync(NewAsset("asset-1", "改造后"));

        var assets = await store.ListAsync("prj-1");

        assets.Count.ShouldBe(1);
        assets[0].Name.ShouldBe("改造后");
    }

    [Fact]
    public async Task Changing_tags_should_be_persisted()
    {
        // 集合属性有自定义比较器；没有它 EF 会认为"引用不同 = 无变化"，
        // 表现为"改了标签，重新打开却没有"。
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        var asset = NewAsset("asset-1", "改标签");
        await store.UpsertAsync(asset);
        await store.UpsertAsync(asset with { Tags = ["新标签"] });

        (await store.FindAsync("asset-1"))!.Tags.ShouldBe(["新标签"]);
    }

    [Fact]
    public async Task List_should_scope_assets_to_the_requested_project()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        await store.UpsertAsync(NewAsset("asset-1", "A", projectId: "prj-1"));
        await store.UpsertAsync(NewAsset("asset-2", "B", projectId: "prj-2"));

        var assets = await store.ListAsync("prj-1");

        assets.Count.ShouldBe(1);
        assets[0].Id.ShouldBe("asset-1");
    }

    [Fact]
    public async Task List_should_order_assets_by_name()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        await store.UpsertAsync(NewAsset("asset-1", "C"));
        await store.UpsertAsync(NewAsset("asset-2", "A"));
        await store.UpsertAsync(NewAsset("asset-3", "B"));

        var names = (await store.ListAsync("prj-1")).Select(static a => a.Name).ToList();

        names.ShouldBe(["A", "B", "C"]);
    }

    [Fact]
    public async Task Batch_upsert_should_write_nothing_when_one_row_is_rejected()
    {
        // 真实性在于：数据库里确实有一条会导致失败的记录被触发，
        // 而不是"EF 在保存前就拦下了"。
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        await CreateFailingTriggerAsync(database);

        var batch = new[]
        {
            NewAsset("asset-good", "应该回滚"),
            NewAsset("asset-boom", "BOOM")
        };

        await Should.ThrowAsync<Exception>(() => store.UpsertRangeAsync(batch));

        // 部分写入比整体失败更糟：用户重试会产生无法解释的重复资产。
        (await store.ListAsync("prj-1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Batch_upsert_should_write_all_rows_when_everything_is_valid()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        var written = await store.UpsertRangeAsync(
        [
            NewAsset("asset-1", "A"),
            NewAsset("asset-2", "B"),
            NewAsset("asset-3", "C")
        ]);

        written.ShouldBe(3);
        (await store.ListAsync("prj-1")).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Batch_upsert_should_also_update_assets_that_already_exist()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        await store.UpsertAsync(NewAsset("asset-1", "旧名称"));
        await store.UpsertRangeAsync([NewAsset("asset-1", "新名称"), NewAsset("asset-2", "新增")]);

        var assets = await store.ListAsync("prj-1");

        assets.Count.ShouldBe(2);
        assets.Single(static a => a.Id == "asset-1").Name.ShouldBe("新名称");
    }

    [Fact]
    public async Task Batch_upsert_of_nothing_should_be_a_no_op()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        (await store.UpsertRangeAsync([])).ShouldBe(0);
    }

    [Fact]
    public async Task Delete_should_report_false_for_unknown_asset()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteAssetStore(database.Factory);

        (await store.DeleteAsync("不存在")).ShouldBeFalse();
    }

    private static async Task<TemporaryDatabase> NewDatabaseAsync()
    {
        var database = new TemporaryDatabase();
        await database.InitializeAsync();

        // 资产有外键指向项目：先建一个项目，行才写得进去。
        var projects = new SqliteProjectStore(database.Factory);

        await projects.UpsertAsync(new Project
        {
            Id = "prj-1",
            Name = "默认项目",
            CreatedAt = DateTimeOffset.UnixEpoch
        });

        await projects.UpsertAsync(new Project
        {
            Id = "prj-2",
            Name = "第二项目",
            CreatedAt = DateTimeOffset.UnixEpoch
        });

        return database;
    }

    private static Asset NewAsset(string id, string name, string projectId = "prj-1") => new()
    {
        Id = id,
        ProjectId = projectId,
        Name = name,
        Host = "10.0.0.1",
        Kind = AssetKind.Camera
    };

    /// <summary>建一个针对特定名称"爆炸"的触发器，用来验证批量写入的事务性。</summary>
    private static async Task CreateFailingTriggerAsync(TemporaryDatabase database)
    {
        await using var context = database.Factory.CreateContext();

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER assets_boom BEFORE INSERT ON assets
            WHEN NEW.Name = 'BOOM'
            BEGIN
                SELECT RAISE(ABORT, '注入的写入失败');
            END;
            """);
    }
}
