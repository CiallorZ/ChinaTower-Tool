using LinkForge.Application.Assets;
using LinkForge.Domain.Assets;

namespace LinkForge.App.Services;

/// <summary>
/// CSV 导入的落库结果（LF-004）。
/// </summary>
/// <param name="ImportedCount">实际写入数据库的条数。</param>
/// <param name="Result">解析结果（含被拒绝行与提醒）。</param>
public readonly record struct AssetCsvImportOutcome(int ImportedCount, AssetCsvImportResult Result);

/// <summary>
/// 资产 CSV 的导入流程（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 解析（<see cref="AssetCsvCodec"/>）与落库（<see cref="IAssetStore"/>）分成两段：
/// 前者是纯函数、可离线测试；后者才碰磁盘。两者在这里串起来，
/// 页面只需要面对"成功 / 部分成功 / 失败"三种结论。
/// </para>
/// <para>
/// <b>部分成功必须显式存在。</b>被拒绝的行既不能静默丢弃，
/// 也不能让整批失败 —— 用户会希望把有问题的行改好再导一次。
/// 因此导入结果同时返回成功条数与逐行问题。
/// </para>
/// </remarks>
public sealed class AssetCsvImportService
{
    private readonly IAssetStore _assetStore;

    /// <summary>创建导入服务。</summary>
    /// <param name="assetStore">资产存储。</param>
    public AssetCsvImportService(IAssetStore assetStore)
    {
        ArgumentNullException.ThrowIfNull(assetStore);

        _assetStore = assetStore;
    }

    /// <summary>
    /// 解析 CSV 字节并写入存储。
    /// </summary>
    /// <param name="bytes">文件字节。</param>
    /// <param name="projectId">目标项目。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入结果。</returns>
    public async Task<AssetCsvImportOutcome> ImportAsync(
        byte[] bytes,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var parsed = AssetCsvCodec.Read(bytes, projectId);

        if (parsed.HasFatalError || parsed.Assets.Count == 0)
        {
            // 致命错误或"一行都没解析出来"：不碰数据库，
            // 否则用户会看到"导入成功 0 条"这种既非失败也非成功的结论。
            return new AssetCsvImportOutcome(0, parsed);
        }

        var written = await _assetStore
            .UpsertRangeAsync(parsed.Assets, cancellationToken)
            .ConfigureAwait(false);

        return new AssetCsvImportOutcome(written, parsed);
    }
}
