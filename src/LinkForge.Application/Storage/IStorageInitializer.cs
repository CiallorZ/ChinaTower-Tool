namespace LinkForge.Application.Storage;

/// <summary>
/// 本地存储的初始化契约（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 界面需要在首次使用前确保数据库存在且结构已升级，但不应知道用的是
/// SQLite、EF Core 还是文件 —— 因此这里只暴露"路径"与"初始化"两件事。
/// </para>
/// <para>
/// 初始化必须是<b>幂等</b>的：每次进入使用存储的页面都可以安全调用，
/// 已是最新结构时不做任何改动。
/// </para>
/// </remarks>
public interface IStorageInitializer
{
    /// <summary>本地存储位置（面向用户展示，便于现场备份）。</summary>
    string Location { get; }

    /// <summary>
    /// 确保存储可用（创建、升级，必要时从损坏中恢复）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次初始化实际发生了什么。</returns>
    /// <exception cref="StorageException">存储损坏且无法隔离时。</exception>
    Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default);
}
