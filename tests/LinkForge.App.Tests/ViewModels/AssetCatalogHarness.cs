using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.Application.Assets;
using LinkForge.Application.Projects;
using LinkForge.Application.Secrets;
using LinkForge.Application.Storage;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;
using LinkForge.Domain.Secrets;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// 构造 <see cref="AssetCatalogViewModel"/> 的测试夹具（LF-004）。
/// </summary>
/// <remarks>
/// 替换的只有“文件对话框”与“存储实现”两个边界：
/// 视图模型、CSV 编解码、标签/凭据/环境变量的校验规则仍是生产实现。
/// 真实的 SQLite 落库由 <c>LinkForge.Persistence.Tests</c> 验证。
/// </remarks>
internal sealed class AssetCatalogHarness
{
    public AssetCatalogHarness()
    {
        Projects = new FakeProjectStore();
        Assets = new FakeAssetStore();
        Environments = new FakeEnvironmentStore();
        Storage = new FakeStorageInitializer();
        Files = new FakeAssetCsvFileService();
        PackageFiles = new FakeProjectPackageFileService();
        PackageImporter = new FakeProjectPackageImporter();
        Secrets = new FakeSecretStore();

        ViewModel = new AssetCatalogViewModel(
            Projects,
            Assets,
            Environments,
            Storage,
            Files,
            new AssetCsvImportService(Assets),
            new AssetCsvExportService(),
            PackageFiles,
            new ProjectPackageCodec(),
            PackageImporter,
            Secrets);
    }

    public FakeProjectStore Projects { get; }

    public FakeAssetStore Assets { get; }

    public FakeEnvironmentStore Environments { get; }

    public FakeStorageInitializer Storage { get; }

    public FakeAssetCsvFileService Files { get; }

    public FakeProjectPackageFileService PackageFiles { get; }

    public FakeProjectPackageImporter PackageImporter { get; }

    public FakeSecretStore Secrets { get; }

    public AssetCatalogViewModel ViewModel { get; }
}

/// <summary>内存凭据库：只用于验证视图模型状态机，不模拟 DPAPI。</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Exception? StatusFailure { get; set; }

    public Exception? SaveFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public TaskCompletionSource? StatusStarted { get; set; }

    public TaskCompletionSource? StatusRelease { get; set; }

    public int SaveCallCount { get; private set; }

    public int DeleteCallCount { get; private set; }

    public async Task<SecretEntryStatus> GetStatusAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        StatusStarted?.TrySetResult();

        if (StatusRelease is not null)
        {
            await StatusRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (StatusFailure is not null)
        {
            throw StatusFailure;
        }

        return _entries.TryGetValue(reference.Key, out var entry)
            ? SecretEntryStatus.Available(entry.UpdatedAtUtc)
            : SecretEntryStatus.Missing;
    }

    public Task SaveAsync(
        SecretReference reference,
        string secretValue,
        CancellationToken cancellationToken = default)
    {
        if (SaveFailure is not null)
        {
            return Task.FromException(SaveFailure);
        }

        SaveCallCount++;
        _entries[reference.Key] = new Entry(secretValue, DateTimeOffset.Now);
        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            _entries.TryGetValue(reference.Key, out var entry)
                ? entry.Value
                : null);

    public Task<bool> DeleteAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        if (DeleteFailure is not null)
        {
            return Task.FromException<bool>(DeleteFailure);
        }

        DeleteCallCount++;
        return Task.FromResult(_entries.Remove(reference.Key));
    }

    public string? FindValue(string key) =>
        _entries.TryGetValue(key, out var entry) ? entry.Value : null;

    private sealed record Entry(string Value, DateTimeOffset UpdatedAtUtc);
}

/// <summary>内存项目存储：行为可编排，不碰磁盘。</summary>
internal sealed class FakeProjectStore : IProjectStore
{
    public List<Project> Items { get; } = [];

    public Exception? ListFailure { get; set; }

    public Exception? UpsertFailure { get; set; }

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (ListFailure is not null)
        {
            return Task.FromException<IReadOnlyList<Project>>(ListFailure);
        }

        return Task.FromResult<IReadOnlyList<Project>>(Items.ToArray());
    }

    public Task<Project?> FindAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.FirstOrDefault(p => p.Id == projectId));

    public Task<Project> UpsertAsync(Project project, CancellationToken cancellationToken = default)
    {
        if (UpsertFailure is not null)
        {
            return Task.FromException<Project>(UpsertFailure);
        }

        var persisted = project with { UpdatedAt = DateTimeOffset.Now };
        var index = Items.FindIndex(p => p.Id == persisted.Id);

        if (index >= 0)
        {
            Items[index] = persisted;
        }
        else
        {
            Items.Add(persisted);
        }

        return Task.FromResult(persisted);
    }

    public Task<bool> DeleteAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var removed = Items.RemoveAll(p => p.Id == projectId);
        return Task.FromResult(removed > 0);
    }
}

/// <summary>内存资产存储：行为可编排，不碰磁盘。</summary>
internal sealed class FakeAssetStore : IAssetStore
{
    public List<Asset> Items { get; } = [];

    public Exception? ListFailure { get; set; }

    public int UpsertCallCount { get; private set; }

    public Task<IReadOnlyList<Asset>> ListAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (ListFailure is not null)
        {
            return Task.FromException<IReadOnlyList<Asset>>(ListFailure);
        }

        return Task.FromResult<IReadOnlyList<Asset>>(
            Items.Where(a => a.ProjectId == projectId).OrderBy(a => a.Name).ToArray());
    }

    public Task<Asset?> FindAsync(string assetId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.FirstOrDefault(a => a.Id == assetId));

    public Task UpsertAsync(Asset asset, CancellationToken cancellationToken = default) =>
        UpsertRangeAsync([asset], cancellationToken);

    public Task<int> UpsertRangeAsync(IReadOnlyList<Asset> assets, CancellationToken cancellationToken = default)
    {
        UpsertCallCount++;

        foreach (var asset in assets)
        {
            var persisted = asset with { UpdatedAt = DateTimeOffset.Now };
            var index = Items.FindIndex(a => a.Id == persisted.Id);

            if (index >= 0)
            {
                Items[index] = persisted;
            }
            else
            {
                Items.Add(persisted);
            }
        }

        return Task.FromResult(assets.Count);
    }

    public Task<bool> DeleteAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var removed = Items.RemoveAll(a => a.Id == assetId);
        return Task.FromResult(removed > 0);
    }
}

/// <summary>内存环境存储：行为可编排，不碰磁盘。</summary>
internal sealed class FakeEnvironmentStore : IEnvironmentStore
{
    public List<EnvironmentProfile> Items { get; } = [];

    public Task<IReadOnlyList<EnvironmentProfile>> ListAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EnvironmentProfile>>(
            Items.Where(e => e.ProjectId == projectId).OrderBy(e => e.Name).ToArray());

    public Task UpsertAsync(EnvironmentProfile profile, CancellationToken cancellationToken = default)
    {
        var index = Items.FindIndex(e => e.Id == profile.Id);

        if (index >= 0)
        {
            Items[index] = profile;
        }
        else
        {
            Items.Add(profile);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string environmentId, CancellationToken cancellationToken = default)
    {
        var removed = Items.RemoveAll(e => e.Id == environmentId);
        return Task.FromResult(removed > 0);
    }

    public Task<bool> SetActiveAsync(
        string projectId,
        string environmentId,
        CancellationToken cancellationToken = default)
    {
        var target = Items.FirstOrDefault(e => e.Id == environmentId && e.ProjectId == projectId);

        if (target is null)
        {
            return Task.FromResult(false);
        }

        for (var index = 0; index < Items.Count; index++)
        {
            Items[index] = Items[index] with { IsActive = Items[index].Id == environmentId };
        }

        return Task.FromResult(true);
    }
}

/// <summary>总是成功的存储初始化器。</summary>
internal sealed class FakeStorageInitializer : IStorageInitializer
{
    public Exception? Failure { get; set; }

    public DatabaseInitializationResult Result { get; set; } =
        new(DatabaseInitializationOutcome.AlreadyCurrent, @"C:\fake\linkforge.db", null);

    public int InitializeCallCount { get; private set; }

    public string Location => Result.Location;

    public Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        InitializeCallCount++;

        return Failure is null
            ? Task.FromResult(Result)
            : Task.FromException<DatabaseInitializationResult>(Failure);
    }
}

/// <summary>记录导入导出内容，不触碰剪贴板与真实文件对话框。</summary>
internal sealed class FakeAssetCsvFileService : IAssetCsvFileService
{
    public byte[]? ImportContent { get; set; }

    public string ImportFileName { get; set; } = "assets.csv";

    public byte[]? WrittenContent { get; private set; }

    public string? WrittenFileName { get; private set; }

    public bool WriteResult { get; set; } = true;

    public Exception? ImportFailure { get; set; }

    public Task<AssetCsvImportFile?> PickAndReadAsync(CancellationToken cancellationToken = default)
    {
        if (ImportFailure is not null)
        {
            return Task.FromException<AssetCsvImportFile?>(ImportFailure);
        }

        return ImportContent is null
            ? Task.FromResult<AssetCsvImportFile?>(null)
            : Task.FromResult<AssetCsvImportFile?>(new AssetCsvImportFile(ImportFileName, ImportContent));
    }

    public Task<bool> PickAndWriteAsync(
        string suggestedFileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        WrittenFileName = suggestedFileName;
        WrittenContent = content;

        return Task.FromResult(WriteResult);
    }
}

/// <summary>记录项目包读写内容，不弹出系统文件对话框。</summary>
internal sealed class FakeProjectPackageFileService : IProjectPackageFileService
{
    public ProjectPackageImportFile? ImportFile { get; set; }

    public byte[]? WrittenContent { get; private set; }

    public string? WrittenFileName { get; private set; }

    public Exception? ImportFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    public bool WriteResult { get; set; } = true;

    public TaskCompletionSource? WriteStarted { get; set; }

    public TaskCompletionSource? WriteRelease { get; set; }

    public Task<ProjectPackageImportFile?> PickAndReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (ImportFailure is not null)
        {
            return Task.FromException<ProjectPackageImportFile?>(ImportFailure);
        }

        return Task.FromResult(ImportFile);
    }

    public async Task<bool> PickAndWriteAsync(
        string suggestedFileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        WrittenFileName = suggestedFileName;
        WrittenContent = content;
        WriteStarted?.TrySetResult();

        if (WriteRelease is not null)
        {
            await WriteRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        return WriteResult;
    }
}

/// <summary>内存项目包导入器，保留最后一次解析结果供断言。</summary>
internal sealed class FakeProjectPackageImporter : IProjectPackageImporter
{
    public ProjectPackage? LastPackage { get; private set; }

    public Action<ProjectPackage>? OnImport { get; set; }

    public Exception? Failure { get; set; }

    public TaskCompletionSource? ImportStarted { get; set; }

    public TaskCompletionSource? ImportRelease { get; set; }

    public async Task<ProjectPackageImportResult> ImportAsync(
        ProjectPackage package,
        CancellationToken cancellationToken = default)
    {
        LastPackage = package;
        ImportStarted?.TrySetResult();

        if (ImportRelease is not null)
        {
            await ImportRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Failure is not null)
        {
            throw Failure;
        }

        OnImport?.Invoke(package);

        return new ProjectPackageImportResult(
            package.Project.Id,
            package.Environments.Count,
            package.Assets.Count);
    }
}

