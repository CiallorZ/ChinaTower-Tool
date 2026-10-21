using LinkForge.Application.Storage;
using LinkForge.Domain.Projects;
using LinkForge.Persistence;

namespace LinkForge.Persistence.Tests;

/// <summary>
/// 环境配置存储（LF-004）。
/// </summary>
public class SqliteEnvironmentStoreTests
{
    [Fact]
    public async Task Upsert_should_round_trip_variables_and_proxy()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteEnvironmentStore(database.Factory);

        await store.UpsertAsync(new EnvironmentProfile
        {
            Id = "env-1",
            ProjectId = "prj-1",
            Name = "测试环境",
            Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = "10.0.0.10",
                ["ApiBase"] = "https://api.test.local"
            },
            ProxyUrl = "http://127.0.0.1:7897",
            DefaultTimeoutSeconds = 30,
            IsActive = true
        });

        var loaded = (await store.ListAsync("prj-1")).Single();

        loaded.Name.ShouldBe("测试环境");
        loaded.Variables["Host"].ShouldBe("10.0.0.10");
        loaded.Variables.Count.ShouldBe(2);
        loaded.ProxyUrl.ShouldBe("http://127.0.0.1:7897");
        loaded.DefaultTimeoutSeconds.ShouldBe(30);
        loaded.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Changing_variables_should_be_persisted()
    {
        // 与标签同理：字典属性依赖自定义比较器，否则改动不会被写入。
        using var database = await NewDatabaseAsync();
        var store = new SqliteEnvironmentStore(database.Factory);

        var profile = new EnvironmentProfile
        {
            Id = "env-1",
            ProjectId = "prj-1",
            Name = "测试环境",
            Variables = new Dictionary<string, string> { ["A"] = "1" }
        };

        await store.UpsertAsync(profile);
        await store.UpsertAsync(profile with
        {
            Variables = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }
        });

        (await store.ListAsync("prj-1")).Single().Variables.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Duplicate_environment_name_should_fail_with_a_readable_message()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteEnvironmentStore(database.Factory);

        await store.UpsertAsync(NewProfile("env-1", "生产环境"));

        var exception = await Should.ThrowAsync<StorageException>(
            () => store.UpsertAsync(NewProfile("env-2", "生产环境")));

        exception.Message.ShouldContain("生产环境");
        exception.Message.ShouldNotContain("UNIQUE", Case.Insensitive);
    }

    [Fact]
    public async Task SetActive_should_leave_exactly_one_active_environment()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteEnvironmentStore(database.Factory);

        await store.UpsertAsync(NewProfile("env-1", "开发环境") with { IsActive = true });
        await store.UpsertAsync(NewProfile("env-2", "生产环境"));

        (await store.SetActiveAsync("prj-1", "env-2")).ShouldBeTrue();

        var profiles = await store.ListAsync("prj-1");

        // 两个环境同时激活会让"当前用的是哪套变量"无法回答。
        profiles.Count(static p => p.IsActive).ShouldBe(1);
        profiles.Single(static p => p.IsActive).Id.ShouldBe("env-2");
    }

    [Fact]
    public async Task SetActive_should_report_false_for_unknown_environment()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteEnvironmentStore(database.Factory);

        (await store.SetActiveAsync("prj-1", "不存在")).ShouldBeFalse();
    }

    [Fact]
    public async Task SetActive_should_not_touch_other_projects()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteEnvironmentStore(database.Factory);

        await store.UpsertAsync(NewProfile("env-1", "环境") with { IsActive = true });
        await store.UpsertAsync(new EnvironmentProfile
        {
            Id = "env-other",
            ProjectId = "prj-2",
            Name = "环境",
            IsActive = true
        });

        await store.SetActiveAsync("prj-1", "env-1");

        (await store.ListAsync("prj-2")).Single().IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_should_report_false_for_unknown_environment()
    {
        using var database = await NewDatabaseAsync();
        var store = new SqliteEnvironmentStore(database.Factory);

        (await store.DeleteAsync("不存在")).ShouldBeFalse();
    }

    private static EnvironmentProfile NewProfile(string id, string name) => new()
    {
        Id = id,
        ProjectId = "prj-1",
        Name = name
    };

    private static async Task<TemporaryDatabase> NewDatabaseAsync()
    {
        var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var projects = new SqliteProjectStore(database.Factory);

        foreach (var (id, name) in new[] { ("prj-1", "默认项目"), ("prj-2", "第二项目") })
        {
            await projects.UpsertAsync(new Project
            {
                Id = id,
                Name = name,
                CreatedAt = DateTimeOffset.UnixEpoch
            });
        }

        return database;
    }
}
