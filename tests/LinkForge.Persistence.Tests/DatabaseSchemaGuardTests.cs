using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence.Tests;

/// <summary>
/// 数据库结构守卫（LF-004）。
/// </summary>
/// <remarks>
/// 这些断言保护的是<b>结构性安全属性</b>，而不是某个功能：
/// 只要有人往表里加一列 <c>Password</c>，"项目文件/数据库不含明文凭据"
/// 就从结构保证退化成约定，而这类回退在代码评审里很难被发现。
/// </remarks>
public class DatabaseSchemaGuardTests
{
    private static readonly string[] SecretColumnNames =
    [
        "password", "passwd", "secret", "apikey", "api_key", "token", "privatekey", "pwd"
    ];

    [Theory]
    [InlineData("projects")]
    [InlineData("assets")]
    [InlineData("environments")]
    public async Task Tables_should_not_contain_a_secret_column(string table)
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var columns = await database.ReadColumnsAsync(table);

        columns.ShouldNotBeEmpty($"{table} 表应当存在。");

        var offenders = columns
            .Where(c => SecretColumnNames.Contains(
                c.Name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant()))
            .Select(static c => c.Name)
            .ToList();

        offenders.ShouldBeEmpty(
            $"{table} 表不得出现承载明文凭据的列（实际：{string.Join(", ", offenders)}）；凭据只允许以引用键形式存在。");
    }

    [Fact]
    public async Task Assets_should_store_credentials_only_as_a_reference_key()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        var columns = await database.ReadColumnsAsync("assets");
        var credential = columns.Single(static c => c.Name == "CredentialReference");

        // 可空：绝大多数资产不需要凭据。
        credential.NotNull.ShouldBeFalse();

        // 存引用键而不是值：列名本身也要说清楚这一点。
        credential.Type.ShouldBe("TEXT");
    }

    [Fact]
    public async Task Assets_should_keep_a_foreign_key_to_projects_with_cascade()
    {
        using var database = new TemporaryDatabase();
        await database.InitializeAsync();

        await using var context = database.Factory.CreateContext();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        await context.Database.OpenConnectionAsync();

        command.CommandText = "PRAGMA foreign_key_list(assets)";

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            // 列顺序：id, seq, table, from, to, on_update, on_delete, match
            rows.Add($"{reader.GetString(2)}|{reader.GetString(3)}|{reader.GetString(6)}");
        }

        rows.ShouldContain("projects|ProjectId|CASCADE");
    }
}
