using LinkForge.Application.Projects;
using LinkForge.Application.Storage;
using LinkForge.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence;

/// <summary>
/// 项目存储的 SQLite 实现（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 所有方法都直接等待 EF 的异步 API，不做 <c>Task.Run</c> 包装：
/// SQLite 驱动本身就是异步 I/O，再套一层线程池只是多一次线程切换。
/// </para>
/// <para>
/// 每次操作使用独立上下文，因此本类型可以被多个页面／任务并发调用。
/// </para>
/// </remarks>
public sealed class SqliteProjectStore : IProjectStore
{
    private readonly SqliteContextFactory _factory;
    private readonly TimeProvider _timeProvider;

    /// <summary>创建项目存储。</summary>
    /// <param name="factory">上下文工厂。</param>
    /// <param name="timeProvider">时间源；null 时使用系统时钟。</param>
    public SqliteProjectStore(SqliteContextFactory factory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _factory.CreateContext();

        var projects = await context.Projects
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 排序在客户端完成：SQLite 提供程序拒绝在 ORDER BY 里使用 DateTimeOffset
        // （混用不同 UTC 偏移的时间戳，文本比较得不到正确顺序），
        // 而项目的实数规模是"每个用户几十个"，在内存里排序的代价可以忽略。
        // 若将来项目数量达到需要数据库排序的量级，应改为写入一列 UTC ticks 排序列，
        // 而不是把时间戳降级成不保留偏移的字符串。
        return projects
            .OrderByDescending(static p => p.UpdatedAt ?? p.CreatedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<Project?> FindAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var context = _factory.CreateContext();

        return await context.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Project> UpsertAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        // 修改时间与 schema 版本由存储层决定：调用方自己写时间会导致
        // "刚保存的项目排在列表底部"这类难以解释的现象。
        var persisted = project with
        {
            UpdatedAt = _timeProvider.GetUtcNow(),
            SchemaVersion = Project.CurrentSchemaVersion
        };

        await using var context = _factory.CreateContext();

        var existing = await context.Projects
            .FirstOrDefaultAsync(p => p.Id == persisted.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.Projects.Add(persisted);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(persisted);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqliteStoreHelper.IsUniqueViolation(ex))
        {
            throw new StorageException(
                $"已存在名为“{persisted.Name}”的项目。请换一个名称，或直接打开已有项目。",
                ex);
        }

        return persisted;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var context = _factory.CreateContext();

        var existing = await context.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return false;
        }

        context.Projects.Remove(existing);

        // 资产与环境由数据库外键级联删除（见 LinkForgeDbContext 的 OnDelete 配置）。
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
