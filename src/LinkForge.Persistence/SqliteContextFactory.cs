using LinkForge.Application.Storage;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence;

/// <summary>
/// SQLite 上下文工厂（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 每次操作<b>新建一个上下文</b>：DbContext 不是线程安全的，而诊断任务与界面会并发
/// 读写存储。共用一个长生命周期上下文会出现"另一个线程正在使用此上下文"这类
/// 只在现场才偶发的中断。
/// </para>
/// <para>
/// 不用 <c>AddDbContext</c>/<c>IDbContextFactory</c>：把 DI 容器的生命周期语义
/// 引进持久化层，会让"这个存储什么时候释放"取决于容器注册方式，
/// 而这里的生命周期是明确的 —— 视图模型持有工厂，用完即释放。
/// </para>
/// </remarks>
public sealed class SqliteContextFactory : IStorageInitializer
{
    private readonly DbContextOptions<LinkForgeDbContext> _options;

    /// <summary>创建工厂。</summary>
    /// <param name="databasePath">数据库文件路径。</param>
    public SqliteContextFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = databasePath;
        _options = LinkForgeDatabase.BuildOptions(databasePath);
    }

    /// <summary>数据库文件路径（诊断用）。</summary>
    public string DatabasePath { get; }

    /// <inheritdoc />
    public string Location => DatabasePath;

    /// <summary>新建上下文；调用方负责释放。</summary>
    /// <returns>上下文。</returns>
    public LinkForgeDbContext CreateContext() => new(_options);

    /// <summary>确保目录存在并应用迁移；文件损坏时隔离并重建。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>初始化结果。</returns>
    public Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        LinkForgeDatabase.InitializeAsync(DatabasePath, cancellationToken);
}
