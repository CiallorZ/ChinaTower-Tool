using LinkForge.Application.Assets;
using LinkForge.Domain.Assets;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence;

/// <summary>
/// 资产存储的 SQLite 实现（LF-004）。
/// </summary>
/// <remarks>
/// 批量写入刻意包在一个事务里：CSV 导入中途失败时，
/// 用户得到的必须是"一条都没写进去"，否则重试会产生重复资产，
/// 而"哪些进去了、哪些没进去"无法回答。
/// </remarks>
public sealed class SqliteAssetStore : IAssetStore
{
    /// <summary>
    /// 单条 SQL 语句里 <c>IN</c> 的最大参数个数。
    /// </summary>
    /// <remarks>
    /// SQLite 的参数上限（999 或 32766，取决于编译选项）不适合被当作
    /// "可以随便用"的常量，因此显式分批。500 远低于下限，批次又足够大。
    /// </remarks>
    private const int QueryBatchSize = 500;

    private readonly SqliteContextFactory _factory;
    private readonly TimeProvider _timeProvider;

    /// <summary>创建资产存储。</summary>
    /// <param name="factory">上下文工厂。</param>
    /// <param name="timeProvider">时间源；null 时使用系统时钟。</param>
    public SqliteAssetStore(SqliteContextFactory factory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Asset>> ListAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var context = _factory.CreateContext();

        return await context.Assets
            .AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .OrderBy(static a => a.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Asset?> FindAsync(string assetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        await using var context = _factory.CreateContext();

        return await context.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task UpsertAsync(Asset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        return UpsertRangeAsync([asset], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> UpsertRangeAsync(
        IReadOnlyList<Asset> assets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assets);

        if (assets.Count == 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var persisted = assets.Select(asset => asset with { UpdatedAt = now }).ToList();

        await using var context = _factory.CreateContext();
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var tracked = new Dictionary<string, Asset>(StringComparer.Ordinal);

        foreach (var batch in persisted.Chunk(QueryBatchSize))
        {
            var ids = batch.Select(static a => a.Id).ToArray();

            var found = await context.Assets
                .Where(a => ids.Contains(a.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var asset in found)
            {
                tracked[asset.Id] = asset;
            }
        }

        foreach (var asset in persisted)
        {
            if (tracked.TryGetValue(asset.Id, out var existing))
            {
                context.Entry(existing).CurrentValues.SetValues(asset);
            }
            else
            {
                context.Assets.Add(asset);
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return persisted.Count;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string assetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        await using var context = _factory.CreateContext();

        var existing = await context.Assets
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return false;
        }

        context.Assets.Remove(existing);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
