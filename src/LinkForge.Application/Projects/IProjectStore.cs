using LinkForge.Domain.Projects;

namespace LinkForge.Application.Projects;

/// <summary>
/// 项目存储契约（LF-004，PRD §3.1）。
/// </summary>
/// <remarks>
/// <para>
/// 契约放在 Application 而不是 Persistence：调用方（视图模型）依赖的是<b>能力</b>，
/// 换存储实现（SQLite → 其它）不应改动任何调用方。这与 <c>IRunHistoryStore</c> 的处理一致。
/// </para>
/// <para>
/// 所有方法都是异步且接受取消令牌：磁盘 I/O 绝不允许在 UI 线程同步等待（DEV_STANDARDS §1.3）。
/// </para>
/// <para>
/// <b>本契约不提供任何写入明文凭据的入口。</b><see cref="Project"/> 与
/// <c>Asset</c> 都只携带秘密引用键，因此"项目数据里没有明文密码"是结构保证（ADR-008）。
/// </para>
/// </remarks>
public interface IProjectStore
{
    /// <summary>
    /// 读取全部项目，按最后修改时间倒序（未修改过的按创建时间）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>项目列表；存储为空时返回空列表，不抛异常。</returns>
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按标识读取单个项目。
    /// </summary>
    /// <param name="projectId">项目标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>项目；不存在时返回 null。</returns>
    Task<Project?> FindAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增或更新项目（按 <see cref="Project.Id"/> 判定）。
    /// </summary>
    /// <param name="project">项目。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入后的项目（<c>UpdatedAt</c> 已刷新）。</returns>
    /// <remarks>
    /// 返回写入结果而不是 <c>void</c>：界面需要立刻显示新的"最后修改时间"，
    /// 若由界面自行猜时间会与实际落盘值不一致。
    /// </remarks>
    Task<Project> UpsertAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除项目，并级联删除其环境与资产。
    /// </summary>
    /// <param name="projectId">项目标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否删除了记录（false 表示原本就不存在）。</returns>
    /// <remarks>
    /// 级联删除是刻意的：留下"孤儿资产"会让用户在切换项目后看到不属于任何项目的设备，
    /// 而报告中的目标又会引用一个已不存在的项目。
    /// </remarks>
    Task<bool> DeleteAsync(string projectId, CancellationToken cancellationToken = default);
}
