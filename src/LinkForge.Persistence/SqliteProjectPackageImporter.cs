using LinkForge.Application.Projects;
using LinkForge.Application.Storage;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence;

/// <summary>
/// 使用单个 SQLite 事务导入项目包（LF-015）。
/// </summary>
/// <remarks>
/// 所有冲突都在写入前检查；事务提交前任何异常都会整体回滚。
/// 不提供“覆盖已有项目”的快捷选项：项目 ID 与名称冲突时必须由用户显式选择新项目，
/// 避免一次误操作把现场配置替换掉。
/// </remarks>
public sealed class SqliteProjectPackageImporter : IProjectPackageImporter
{
    private const int QueryBatchSize = 500;

    private readonly SqliteContextFactory _factory;
    private readonly TimeProvider _timeProvider;

    /// <summary>创建导入器。</summary>
    /// <param name="factory">上下文工厂。</param>
    /// <param name="timeProvider">时间源；null 时使用系统时钟。</param>
    public SqliteProjectPackageImporter(
        SqliteContextFactory factory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ProjectPackageImportResult> ImportAsync(
        ProjectPackage package,
        CancellationToken cancellationToken = default)
    {
        ProjectPackageCodec.Validate(package);

        var now = _timeProvider.GetUtcNow();
        var project = package.Project with
        {
            SchemaVersion = Project.CurrentSchemaVersion,
            UpdatedAt = now
        };
        var environments = package.Environments.ToArray();
        var assets = package.Assets
            .Select(asset => asset with { UpdatedAt = now })
            .ToArray();

        await using var context = _factory.CreateContext();
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnsureNoConflictsAsync(context, project, assets, environments, cancellationToken)
            .ConfigureAwait(false);

        context.Projects.Add(project);

        if (environments.Length > 0)
        {
            context.Environments.AddRange(environments);
        }

        if (assets.Length > 0)
        {
            context.Assets.AddRange(assets);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqliteStoreHelper.IsUniqueViolation(ex))
        {
            throw new StorageException(
                "导入项目包时发生唯一约束冲突；未写入任何记录。请检查项目名称、环境名称与记录 ID。",
                ex);
        }

        return new ProjectPackageImportResult(
            project.Id,
            environments.Length,
            assets.Length);
    }

    private static async Task EnsureNoConflictsAsync(
        LinkForgeDbContext context,
        Project project,
        IReadOnlyList<Asset> assets,
        IReadOnlyList<EnvironmentProfile> environments,
        CancellationToken cancellationToken)
    {
        if (await context.Projects
                .AnyAsync(item => item.Id == project.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new StorageException(
                $"项目 ID “{project.Id}” 已存在；为避免覆盖现场配置，项目包未导入任何记录。");
        }

        if (await context.Projects
                .AnyAsync(item => item.Name == project.Name, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new StorageException(
                $"已存在名为“{project.Name}”的项目；请先在源文件中修改项目名称，项目包未导入任何记录。");
        }

        foreach (var batch in environments.Chunk(QueryBatchSize))
        {
            var ids = batch.Select(static item => item.Id).ToArray();
            var found = await context.Environments
                .Where(item => ids.Contains(item.Id))
                .Select(static item => item.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (found is not null)
            {
                throw new StorageException(
                    $"环境 ID “{found}” 已存在；项目包未导入任何记录。");
            }
        }

        foreach (var batch in assets.Chunk(QueryBatchSize))
        {
            var ids = batch.Select(static item => item.Id).ToArray();
            var found = await context.Assets
                .Where(item => ids.Contains(item.Id))
                .Select(static item => item.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (found is not null)
            {
                throw new StorageException(
                    $"资产 ID “{found}” 已存在；项目包未导入任何记录。");
            }
        }
    }
}
