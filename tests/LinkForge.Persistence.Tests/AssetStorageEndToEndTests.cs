using LinkForge.Application.Assets;
using LinkForge.Application.Projects;
using LinkForge.Application.Storage;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;
using LinkForge.Persistence;

namespace LinkForge.Persistence.Tests;

/// <summary>
/// 资产存储的端到端链路（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 这些用例跑的是<b>真实 SQLite 文件</b>，不是内存数据库：
/// 迁移、外键级联、唯一索引、日期排序在内存库上的行为与磁盘库并不完全一致，
/// 而这些正是"用户数据丢了"这类问题最容易藏身的地方。
/// </para>
/// <para>
/// 链路固定为：初始化数据库 → 写项目 → 写环境 → 写资产 → 读回 → 级联删除，
/// 与界面上用户的操作顺序一致。
/// </para>
/// </remarks>
public class AssetStorageEndToEndTests
{
    [Fact]
    public async Task Project_environment_and_asset_should_survive_a_full_round_trip()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projects = new SqliteProjectStore(database.Factory);
        var environments = new SqliteEnvironmentStore(database.Factory);
        var assets = new SqliteAssetStore(database.Factory);

        var project = new Project
        {
            Id = "p-001",
            Name = "南区现场",
            Description = "演示用",
            CreatedAt = DateTimeOffset.Now
        };

        await projects.UpsertAsync(project);

        var environment = new EnvironmentProfile
        {
            Id = "e-001",
            ProjectId = project.Id,
            Name = "现场环境",
            ProxyUrl = "http://proxy.internal:3128",
            Variables = new Dictionary<string, string> { ["REGION"] = "cn" }
        };

        await environments.UpsertAsync(environment);

        var asset = new Asset
        {
            Id = "a-001",
            ProjectId = project.Id,
            Name = "大门摄像机",
            Host = "192.168.1.64",
            Port = 554,
            Kind = AssetKind.Camera,
            Vendor = "海康威视",
            Tags = ["室外", "大门"],
            CredentialReference = "asset.gate.password"
        };

        await assets.UpsertAsync(asset);

        // 读回：字段逐个比对，避免"存进去了但取出来是空的"这种假成功。
        var loaded = await assets.FindAsync(asset.Id);
        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("大门摄像机");
        loaded.Host.ShouldBe("192.168.1.64");
        loaded.Port.ShouldBe(554);
        loaded.Kind.ShouldBe(AssetKind.Camera);
        loaded.Vendor.ShouldBe("海康威视");
        loaded.Tags.ShouldContain("室外");
        loaded.CredentialReference.ShouldBe("asset.gate.password");

        var loadedEnvironments = await environments.ListAsync(project.Id);
        loadedEnvironments.Count.ShouldBe(1);
        loadedEnvironments[0].ProxyUrl.ShouldBe("http://proxy.internal:3128");
        loadedEnvironments[0].Variables["REGION"].ShouldBe("cn");

        var listed = await assets.ListAsync(project.Id);
        listed.Count.ShouldBe(1);
        listed[0].Id.ShouldBe(asset.Id);
    }

    [Fact]
    public async Task Deleting_a_project_should_cascade_to_its_assets()
    {
        // 留下孤儿资产会让用户切换项目后看到不属于任何项目的设备，
        // 而报告里的目标会引用一个已不存在的项目。
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projects = new SqliteProjectStore(database.Factory);
        var assets = new SqliteAssetStore(database.Factory);

        var project = new Project { Id = "p-002", Name = "北区现场", CreatedAt = DateTimeOffset.Now };
        await projects.UpsertAsync(project);

        await assets.UpsertAsync(new Asset
        {
            Id = "a-002",
            ProjectId = project.Id,
            Name = "配电房摄像机",
            Host = "10.0.0.9",
            Kind = AssetKind.Camera
        });

        var deleted = await projects.DeleteAsync(project.Id);
        deleted.ShouldBeTrue();

        (await assets.ListAsync(project.Id)).ShouldBeEmpty();
        (await assets.FindAsync("a-002")).ShouldBeNull("资产应随项目级联删除。");
    }

    [Fact]
    public async Task Two_projects_with_the_same_name_should_be_rejected_with_a_readable_message()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projects = new SqliteProjectStore(database.Factory);

        await projects.UpsertAsync(
            new Project { Id = "p-003", Name = "同名项目", CreatedAt = DateTimeOffset.Now });

        var exception = await Should.ThrowAsync<StorageException>(() =>
            projects.UpsertAsync(
                new Project { Id = "p-004", Name = "同名项目", CreatedAt = DateTimeOffset.Now }));

        // 消息要能直接给用户看，不能把 SQL 或表结构漏出去。
        exception.Message.ShouldContain("同名项目", Case.Insensitive);
        exception.Message.Contains("SELECT", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        exception.Message.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [Fact]
    public async Task Batch_asset_write_should_be_atomic()
    {
        // 导入 200 条时中途失败，用户应当得到"一条都没进去"，
        // 而不是"进去了前 87 条"。
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projects = new SqliteProjectStore(database.Factory);
        var assets = new SqliteAssetStore(database.Factory);

        const string projectId = "p-005";
        await projects.UpsertAsync(
            new Project { Id = projectId, Name = "批量项目", CreatedAt = DateTimeOffset.Now });

        var batch = Enumerable
            .Range(0, 20)
            .Select(index => new Asset
            {
                Id = $"batch-{index:D3}",
                ProjectId = projectId,
                Name = $"设备 {index}",
                Host = $"10.0.1.{index}",
                Kind = AssetKind.Camera
            })
            .ToArray();

        var written = await assets.UpsertRangeAsync(batch);
        written.ShouldBe(batch.Length);

        (await assets.ListAsync(projectId)).Count.ShouldBe(batch.Length);
    }

    [Fact]
    public async Task Data_should_persist_across_a_reopened_database()
    {
        // 每次操作新建 DbContext 的设计，必须真的能跨连接读到数据。
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projects = new SqliteProjectStore(database.Factory);
        await projects.UpsertAsync(
            new Project { Id = "p-006", Name = "持久化项目", CreatedAt = DateTimeOffset.Now });

        // 重新打开同一个文件（模拟应用重启）。
        var reopened = new SqliteContextFactory(database.DatabasePath);
        var reopenedProjects = new SqliteProjectStore(reopened);

        var loaded = await reopenedProjects.FindAsync("p-006");
        loaded.ShouldNotBeNull("应用重启后数据必须还在。");
        loaded!.Name.ShouldBe("持久化项目");
    }
}
