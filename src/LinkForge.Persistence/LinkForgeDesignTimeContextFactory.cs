using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LinkForge.Persistence;

/// <summary>
/// 设计时上下文工厂，供 <c>dotnet ef migrations</c> 使用。
/// </summary>
/// <remarks>
/// <para>
/// 迁移命令需要一个能构造出上下文的入口。指向一个<b>固定文件名的临时路径</b>，
/// 而不是真实用户数据库：设计时命令只读取模型，绝不应当碰到 <c>%LOCALAPPDATA%</c>。
/// </para>
/// <para>
/// 放在运行时代码而不是 <c>Design</c> 条件编译块里，是为了让
/// <c>LinkForge.Persistence.Tests</c> 能在需要时复用它；代价只是多了一个小类型。
/// </para>
/// </remarks>
public sealed class LinkForgeDesignTimeContextFactory : IDesignTimeDbContextFactory<LinkForgeDbContext>
{
    /// <inheritdoc />
    public LinkForgeDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "linkforge-designtime", LinkForgeDatabase.DefaultFileName);

        return LinkForgeDatabase.Open(path);
    }
}
