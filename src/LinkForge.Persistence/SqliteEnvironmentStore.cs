using LinkForge.Application.Projects;
using LinkForge.Application.Storage;
using LinkForge.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence;

/// <summary>
/// 环境配置存储的 SQLite 实现（LF-004）。
/// </summary>
/// <remarks>
/// 本类型只处理<b>非秘密</b>配置。变量字典里若出现凭据，那是调用方违反了
/// <see cref="EnvironmentProfile"/> 的约定；存储层不做内容嗅探 ——
/// "猜测哪个字符串像密码"既不可靠，也会给使用者一种虚假的安全感。
/// </remarks>
public sealed class SqliteEnvironmentStore : IEnvironmentStore
{
    private readonly SqliteContextFactory _factory;

    /// <summary>创建环境存储。</summary>
    /// <param name="factory">上下文工厂。</param>
    public SqliteEnvironmentStore(SqliteContextFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentProfile>> ListAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var context = _factory.CreateContext();

        return await context.Environments
            .AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .OrderBy(static e => e.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        EnvironmentProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ProjectId);

        await using var context = _factory.CreateContext();

        var existing = await context.Environments
            .FirstOrDefaultAsync(e => e.Id == profile.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.Environments.Add(profile);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(profile);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqliteStoreHelper.IsUniqueViolation(ex))
        {
            throw new StorageException(
                $"该环境下已存在名为“{profile.Name}”的环境配置。请换一个名称。",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string environmentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);

        await using var context = _factory.CreateContext();

        var existing = await context.Environments
            .FirstOrDefaultAsync(e => e.Id == environmentId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return false;
        }

        context.Environments.Remove(existing);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(
        string projectId,
        string environmentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);

        await using var context = _factory.CreateContext();
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var environments = await context.Environments
            .Where(e => e.ProjectId == projectId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (environments.All(e => e.Id != environmentId))
        {
            return false;
        }

        foreach (var environment in environments)
        {
            var shouldBeActive = environment.Id == environmentId;

            if (environment.IsActive != shouldBeActive)
            {
                context.Entry(environment).CurrentValues.SetValues(
                    environment with { IsActive = shouldBeActive });
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
