using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;

namespace LinkForge.Application.Projects;

/// <summary>
/// 可移植的项目包（LF-015）。
/// </summary>
/// <remarks>
/// 项目包只包含项目、环境和资产的<b>非秘密</b>配置。资产凭据仍然只是引用键，
/// 环境变量也必须通过现有的秘密语义校验；因此“导出包中不含明文秘密”是结构与校验
/// 共同保证的结果，而不是导出时临时过滤文本。
/// </remarks>
public sealed record ProjectPackage
{
    /// <summary>项目。</summary>
    public required Project Project { get; init; }

    /// <summary>项目下的环境。</summary>
    public required IReadOnlyList<EnvironmentProfile> Environments { get; init; }

    /// <summary>项目下的资产。</summary>
    public required IReadOnlyList<Asset> Assets { get; init; }
}

/// <summary>项目包成功导入后的真实计数。</summary>
/// <param name="ProjectId">新写入的项目标识。</param>
/// <param name="EnvironmentCount">写入的环境数量。</param>
/// <param name="AssetCount">写入的资产数量。</param>
public sealed record ProjectPackageImportResult(
    string ProjectId,
    int EnvironmentCount,
    int AssetCount);

/// <summary>
/// 项目包的原子导入契约。
/// </summary>
/// <remarks>
/// 导入不提供“部分成功”：任一 ID/名称冲突或任一记录写入失败时，整个事务回滚。
/// 这样用户重试时不会面对“哪些已经导入、哪些还没导入”无法回答的状态。
/// </remarks>
public interface IProjectPackageImporter
{
    /// <summary>
    /// 导出格式或包内容不可用时抛出的可读异常。
    /// </summary>
    /// <param name="package">已通过结构校验的项目包。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入后的真实计数。</returns>
    Task<ProjectPackageImportResult> ImportAsync(
        ProjectPackage package,
        CancellationToken cancellationToken = default);
}

/// <summary>项目包格式错误；消息可直接展示给用户。</summary>
public sealed class ProjectPackageFormatException : Exception
{
    /// <summary>创建格式错误。</summary>
    /// <param name="message">面向用户的说明。</param>
    public ProjectPackageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>创建格式错误并保留内部原因。</summary>
    /// <param name="message">面向用户的说明。</param>
    /// <param name="innerException">内部原因。</param>
    public ProjectPackageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
