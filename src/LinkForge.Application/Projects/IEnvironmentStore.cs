using LinkForge.Domain.Projects;

namespace LinkForge.Application.Projects;

/// <summary>
/// 环境配置存储契约（LF-004，PRD §3.1 / ADR-003）。
/// </summary>
/// <remarks>
/// <para>
/// 环境只保存<b>非秘密</b>配置：变量、代理地址、超时策略（见 <see cref="EnvironmentProfile"/>）。
/// 契约因此没有任何"读取秘密"的方法 —— 想拿密码必须走秘密存储服务。
/// </para>
/// </remarks>
public interface IEnvironmentStore
{
    /// <summary>
    /// 读取某个项目下的全部环境。
    /// </summary>
    /// <param name="projectId">项目标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>环境列表，按名称排序；无记录时返回空列表。</returns>
    Task<IReadOnlyList<EnvironmentProfile>> ListAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增或更新环境。
    /// </summary>
    /// <param name="profile">环境配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task UpsertAsync(EnvironmentProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除环境。
    /// </summary>
    /// <param name="environmentId">环境标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否删除了记录。</returns>
    Task<bool> DeleteAsync(string environmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一个环境设为该项目的当前激活环境，并取消其它环境的激活状态。
    /// </summary>
    /// <param name="projectId">项目标识。</param>
    /// <param name="environmentId">环境标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否完成切换（false 表示环境不存在或不属于该项目）。</returns>
    /// <remarks>
    /// 用一次操作而不是"逐个环境改 IsActive"：两个环境同时处于激活态会让
    /// "当前用的是哪套变量"变得无法回答。
    /// </remarks>
    Task<bool> SetActiveAsync(
        string projectId,
        string environmentId,
        CancellationToken cancellationToken = default);
}
