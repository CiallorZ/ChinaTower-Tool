using LinkForge.Domain.Assets;

namespace LinkForge.Application.Assets;

/// <summary>
/// 资产存储契约（LF-004，PRD §3.2）。
/// </summary>
/// <remarks>
/// <para>
/// 与项目存储一致：契约在 Application，实现（SQLite）在 Persistence，调用方只依赖能力。
/// </para>
/// <para>
/// <see cref="Asset.CredentialReference"/> 只存引用键；本契约不提供写入或读取明文凭据的方法。
/// CSV 导入的凭据剥离由 <see cref="AssetCsvCodec"/> 在进入存储之前完成。
/// </para>
/// </remarks>
public interface IAssetStore
{
    /// <summary>
    /// 读取某个项目下的全部资产。
    /// </summary>
    /// <param name="projectId">项目标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>资产列表；无记录时返回空列表。</returns>
    Task<IReadOnlyList<Asset>> ListAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按标识读取单个资产。
    /// </summary>
    /// <param name="assetId">资产标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>资产；不存在时返回 null。</returns>
    Task<Asset?> FindAsync(string assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增或更新资产。
    /// </summary>
    /// <param name="asset">资产。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task UpsertAsync(Asset asset, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量新增或更新资产（CSV 导入使用）。
    /// </summary>
    /// <param name="assets">资产集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际写入的条数。</returns>
    /// <remarks>
    /// 批量写入必须在一个事务内完成：导入 200 条资产时中途失败，
    /// 用户应当得到"一条都没进去"，而不是"进去了前 87 条"。
    /// </remarks>
    Task<int> UpsertRangeAsync(IReadOnlyList<Asset> assets, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除资产。
    /// </summary>
    /// <param name="assetId">资产标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否删除了记录。</returns>
    Task<bool> DeleteAsync(string assetId, CancellationToken cancellationToken = default);
}
