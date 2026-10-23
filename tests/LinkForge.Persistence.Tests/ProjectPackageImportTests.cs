using LinkForge.Application.Projects;
using LinkForge.Application.Storage;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;
using LinkForge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence.Tests;

/// <summary>真实 SQLite 上的项目包原子导入（LF-015）。</summary>
public class ProjectPackageImportTests
{
    [Fact]
    public async Task Import_should_write_the_whole_package_and_allow_reading_it_back()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var importer = new SqliteProjectPackageImporter(database.Factory);
        var package = CreatePackage("project-001", "东门视频项目", "asset-001");

        var result = await importer.ImportAsync(package);

        result.ProjectId.ShouldBe("project-001");
        result.EnvironmentCount.ShouldBe(1);
        result.AssetCount.ShouldBe(1);

        var projects = new SqliteProjectStore(database.Factory);
        var environments = new SqliteEnvironmentStore(database.Factory);
        var assets = new SqliteAssetStore(database.Factory);

        (await projects.FindAsync("project-001")).ShouldNotBeNull();
        (await environments.ListAsync("project-001")).ShouldHaveSingleItem();
        var loaded = (await assets.ListAsync("project-001")).ShouldHaveSingleItem();
        loaded.CredentialReference.ShouldBe("asset.cam-01.password");
        loaded.Tags.ShouldContain("园区");
    }

    [Fact]
    public async Task Id_conflict_should_reject_the_entire_package_without_partial_rows()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projects = new SqliteProjectStore(database.Factory);
        var assets = new SqliteAssetStore(database.Factory);
        await projects.UpsertAsync(new Project
        {
            Id = "existing-project",
            Name = "已有项目",
            CreatedAt = DateTimeOffset.Now
        });
        await assets.UpsertAsync(new Asset
        {
            Id = "conflicting-asset",
            ProjectId = "existing-project",
            Name = "已有资产",
            Host = "127.0.0.1"
        });

        var importer = new SqliteProjectPackageImporter(database.Factory);
        var package = CreatePackage("new-project", "新项目", "conflicting-asset");

        var exception = await Should.ThrowAsync<StorageException>(() =>
            importer.ImportAsync(package));

        exception.Message.ShouldContain("资产 ID");
        (await projects.FindAsync("new-project")).ShouldBeNull("冲突时必须回滚整包。");
        (await assets.FindAsync("asset-001")).ShouldBeNull();
    }

    [Fact]
    public async Task Duplicate_project_name_should_reject_the_entire_package()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projects = new SqliteProjectStore(database.Factory);
        await projects.UpsertAsync(new Project
        {
            Id = "existing-project",
            Name = "同名项目",
            CreatedAt = DateTimeOffset.Now
        });

        var importer = new SqliteProjectPackageImporter(database.Factory);
        var package = CreatePackage("new-project", "同名项目", "asset-001");

        var exception = await Should.ThrowAsync<StorageException>(() =>
            importer.ImportAsync(package));

        exception.Message.ShouldContain("已存在名为");
        (await projects.FindAsync("new-project")).ShouldBeNull();
    }

    [Fact]
    public async Task Database_failure_during_asset_insert_should_roll_back_project_and_environment()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        await using (var context = database.Factory.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER fail_asset_insert
                BEFORE INSERT ON assets
                BEGIN
                    SELECT RAISE(ABORT, 'forced asset failure');
                END;
                """);
        }

        var importer = new SqliteProjectPackageImporter(database.Factory);

        await Should.ThrowAsync<DbUpdateException>(() =>
            importer.ImportAsync(CreatePackage("project-rollback", "回滚项目", "asset-rollback")));

        await using var verification = database.Factory.CreateContext();
        (await verification.Projects.CountAsync()).ShouldBe(0);
        (await verification.Environments.CountAsync()).ShouldBe(0);
        (await verification.Assets.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Cancellation_before_insert_should_write_nothing()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var importer = new SqliteProjectPackageImporter(database.Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            importer.ImportAsync(
                CreatePackage("project-cancel", "取消项目", "asset-cancel"),
                cancellation.Token));

        await using var verification = database.Factory.CreateContext();
        (await verification.Projects.CountAsync()).ShouldBe(0);
        (await verification.Environments.CountAsync()).ShouldBe(0);
        (await verification.Assets.CountAsync()).ShouldBe(0);
    }

    private static ProjectPackage CreatePackage(
        string projectId,
        string projectName,
        string assetId) =>
        new()
        {
            Project = new Project
            {
                Id = projectId,
                Name = projectName,
                CreatedAt = DateTimeOffset.Now,
                DefaultTimeoutSeconds = 10
            },
            Environments =
            [
                new EnvironmentProfile
                {
                    Id = $"env-{projectId}",
                    ProjectId = projectId,
                    Name = "现场环境",
                    Variables = new Dictionary<string, string> { ["REGION"] = "south" },
                    IsActive = true
                }
            ],
            Assets =
            [
                new Asset
                {
                    Id = assetId,
                    ProjectId = projectId,
                    Name = "东门球机",
                    Host = "10.0.0.8",
                    Port = 554,
                    Kind = AssetKind.Camera,
                    Vendor = "海康",
                    Tags = ["园区"],
                    CredentialReference = "asset.cam-01.password"
                }
            ]
        };
}
