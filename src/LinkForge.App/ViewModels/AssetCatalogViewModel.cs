using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Application.Assets;
using LinkForge.Application.Projects;
using LinkForge.Application.Secrets;
using LinkForge.Application.Storage;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Diagnostics;
using LinkForge.Domain.Projects;
using LinkForge.Domain.Secrets;

namespace LinkForge.App.ViewModels;

/// <summary>
/// 设备与资产页（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 本页的全部事实来自本地 SQLite 数据库：项目、环境与资产都是真实记录，
/// 没有“示例项目”、没有占位资产。数据库为空时显示的是空状态与新建入口，
/// 而不是预置几台假设备。
/// </para>
/// <para>
/// <b>凭据边界。</b>资产只保存秘密的<b>引用键</b>。界面上填写凭据时，
/// 输入的是“钥匙的名字”而不是密码本身；校验使用
/// <see cref="AssetCredentialReference"/>，与 CSV 导入同一份规则。
/// </para>
/// <para>
/// 导入导出只处理当前筛选后可见的资产，结论分“成功 / 部分成功 / 失败”三种，
/// 被拒绝的 CSV 行逐行给出行号与原因，不静默丢弃。
/// </para>
/// </remarks>
public sealed partial class AssetCatalogViewModel : ObservableObject
{
    private readonly IProjectStore _projectStore;
    private readonly IAssetStore _assetStore;
    private readonly IEnvironmentStore _environmentStore;
    private readonly IStorageInitializer _storage;
    private readonly IAssetCsvFileService _fileService;
    private readonly IProjectPackageFileService _packageFileService;
    private readonly AssetCsvImportService _importer;
    private readonly AssetCsvExportService _exporter;
    private readonly ProjectPackageCodec _packageCodec;
    private readonly IProjectPackageImporter _packageImporter;
    private readonly ISecretStore _secretStore;
    private readonly SynchronizationContext? _uiContext;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _importCts;
    private CancellationTokenSource? _packageImportCts;
    private CancellationTokenSource? _packageExportCts;
    private IReadOnlyList<AssetDisplayItem> _visibleAssets = [];

    /// <summary>创建资产目录视图模型。</summary>
    public AssetCatalogViewModel(
        IProjectStore projectStore,
        IAssetStore assetStore,
        IEnvironmentStore environmentStore,
        IStorageInitializer storage,
        IAssetCsvFileService fileService,
        AssetCsvImportService importer,
        AssetCsvExportService exporter,
        IProjectPackageFileService packageFileService,
        ProjectPackageCodec packageCodec,
        IProjectPackageImporter packageImporter,
        ISecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(assetStore);
        ArgumentNullException.ThrowIfNull(environmentStore);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(packageFileService);
        ArgumentNullException.ThrowIfNull(packageCodec);
        ArgumentNullException.ThrowIfNull(packageImporter);
        ArgumentNullException.ThrowIfNull(secretStore);

        _projectStore = projectStore;
        _assetStore = assetStore;
        _environmentStore = environmentStore;
        _storage = storage;
        _fileService = fileService;
        _importer = importer;
        _exporter = exporter;
        _packageFileService = packageFileService;
        _packageCodec = packageCodec;
        _packageImporter = packageImporter;
        _secretStore = secretStore;
        _uiContext = SynchronizationContext.Current;

        Projects = [];
        Assets = [];
        AssetRows = [];
        Environments = [];
        KindOptions = AssetKindOption.BuildAll();

        // 筛选框必须显式选中"全部类型"：ComboBox 在 SelectedItem 为 null 时
        // 会显示成一个空白框，用户既看不到当前筛选状态，也无从下手改回去。
        // 顺手把表单里的类型也置为"全部类型"，两者含义不同但默认值一致。
        SelectedKindOption = KindOptions.FirstOrDefault(static option => option.Kind is null);
        AssetKind = SelectedKindOption;

        // 集合变化必须显式转成属性通知：HasAssets / IsEmptyAfterLoad 是计算属性，
        // 不会因为集合被填充而自动触发，界面会出现“列表有内容、空状态也显示”的矛盾。
        Assets.CollectionChanged += OnAssetsCollectionChanged;
    }

    private void OnAssetsCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAssets));
        OnPropertyChanged(nameof(HasVisibleAssets));
        OnPropertyChanged(nameof(VisibleAssetCount));
        OnPropertyChanged(nameof(IsEmptyAfterLoad));
        OnPropertyChanged(nameof(CanExport));
    }

    // ==================== 项目 ====================

    /// <summary>项目列表（当前用户真实存在的项目）。</summary>
    public ObservableCollection<ProjectDisplayItem> Projects { get; }

    /// <summary>当前选中的项目。</summary>
    [ObservableProperty]
    public partial ProjectDisplayItem? SelectedProject { get; set; }

    /// <summary>新项目名称。</summary>
    [ObservableProperty]
    public partial string NewProjectName { get; set; } = string.Empty;

    /// <summary>新项目描述。</summary>
    [ObservableProperty]
    public partial string NewProjectDescription { get; set; } = string.Empty;

    /// <summary>是否存在项目。</summary>
    public bool HasProjects => Projects.Count > 0;

    // ==================== 环境 ====================

    /// <summary>当前项目的环境列表。</summary>
    public ObservableCollection<EnvironmentDisplayItem> Environments { get; }

    /// <summary>当前选中的环境。</summary>
    [ObservableProperty]
    public partial EnvironmentDisplayItem? SelectedEnvironment { get; set; }

    /// <summary>新环境名称。</summary>
    [ObservableProperty]
    public partial string NewEnvironmentName { get; set; } = string.Empty;

    /// <summary>新环境的代理地址。</summary>
    [ObservableProperty]
    public partial string NewEnvironmentProxyUrl { get; set; } = string.Empty;

    /// <summary>新环境的变量文本（每行 KEY=VALUE）。</summary>
    [ObservableProperty]
    public partial string NewEnvironmentVariables { get; set; } = string.Empty;

    /// <summary>是否存在环境。</summary>
    public bool HasEnvironments => Environments.Count > 0;

    /// <summary>当前项目是否已有激活环境。</summary>
    public bool HasActiveEnvironment => Environments.Any(static item => item.IsActive);

    // ==================== 资产 ====================

    /// <summary>当前项目的全部资产（未经筛选）。</summary>
    public ObservableCollection<AssetDisplayItem> Assets { get; }

    /// <summary>
    /// 资产列表的扁平行集合（分组头 + 资产行）。
    /// </summary>
    /// <remarks>
    /// 分组只是视觉上的分隔，交互仍是同一个列表；
    /// 分组头行不可选中，因此“选中项一定是一台真实资产”是结构保证。
    /// </remarks>
    public ObservableCollection<AssetListItem> AssetRows { get; }

    /// <summary>当前选中的资产。</summary>
    [ObservableProperty]
    public partial AssetDisplayItem? SelectedAsset { get; set; }

    /// <summary>搜索关键字（匹配名称、主机、厂商与标签）。</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>类型筛选。</summary>
    [ObservableProperty]
    public partial AssetKindOption? SelectedKindOption { get; set; }

    /// <summary>是否按标签分组。</summary>
    [ObservableProperty]
    public partial bool IsGroupByTag { get; set; }

    /// <summary>可用的类型筛选项。</summary>
    public IReadOnlyList<AssetKindOption> KindOptions { get; }

    /// <summary>是否存在资产。</summary>
    public bool HasAssets => Assets.Count > 0;

    /// <summary>筛选后可见的资产数量。</summary>
    public int VisibleAssetCount => _visibleAssets.Count;

    /// <summary>
    /// 筛选后可见的资产（只读快照）。
    /// </summary>
    /// <remarks>
    /// 刻意暴露只读视图而不是让界面直接用 <see cref="Assets"/>：
    /// <see cref="Assets"/> 是"当前项目的全部资产"，
    /// 而列表、计数与导出用的都是筛选后的这一份 ——
    /// 两份集合混用会让"界面显示 3 条、导出 10 条"这类问题变得难以解释。
    /// </remarks>
    public IReadOnlyList<AssetDisplayItem> VisibleAssets => _visibleAssets;

    /// <summary>筛选后是否仍有可见资产。</summary>
    public bool HasVisibleAssets => _visibleAssets.Count > 0;

    /// <summary>是否已加载过一次（区分“尚未加载”与“加载后为空”）。</summary>
    [ObservableProperty]
    public partial bool HasLoaded { get; private set; }

    /// <summary>是否正在加载。</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>是否正在导入。</summary>
    [ObservableProperty]
    public partial bool IsImporting { get; private set; }

    /// <summary>是否正在导出。</summary>
    [ObservableProperty]
    public partial bool IsExporting { get; private set; }

    /// <summary>是否正在导入项目包。</summary>
    [ObservableProperty]
    public partial bool IsImportingPackage { get; private set; }

    /// <summary>是否正在导出项目包。</summary>
    [ObservableProperty]
    public partial bool IsExportingPackage { get; private set; }

    /// <summary>是否正在保存资产表单。</summary>
    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    /// <summary>加载失败时的说明；成功时为空。</summary>
    [ObservableProperty]
    public partial string LoadErrorMessage { get; private set; } = string.Empty;

    /// <summary>操作反馈（保存、导入、导出结果）。</summary>
    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    /// <summary>导入结果的逐行问题。</summary>
    public ObservableCollection<string> ImportIssues { get; } = [];

    /// <summary>导入结论。</summary>
    [ObservableProperty]
    public partial string ImportSummary { get; private set; } = string.Empty;

    /// <summary>导入结论是否需要用户关注。</summary>
    [ObservableProperty]
    public partial bool HasImportResult { get; private set; }

    /// <summary>导入是否部分成功。</summary>
    [ObservableProperty]
    public partial bool IsPartialImport { get; private set; }

    /// <summary>导入是否整体失败。</summary>
    [ObservableProperty]
    public partial bool IsFailedImport { get; private set; }

    /// <summary>项目包操作反馈；与 CSV 单独显示，避免两种导入结论互相覆盖。</summary>
    [ObservableProperty]
    public partial string PackageFeedback { get; private set; } = string.Empty;

    /// <summary>是否有项目包操作结论。</summary>
    [ObservableProperty]
    public partial bool HasPackageResult { get; private set; }

    /// <summary>项目包结论标签。</summary>
    [ObservableProperty]
    public partial string PackageStatusText { get; private set; } = string.Empty;

    /// <summary>项目包操作是否失败。</summary>
    [ObservableProperty]
    public partial bool IsPackageFailure { get; private set; }

    /// <summary>项目包操作是否被取消。</summary>
    [ObservableProperty]
    public partial bool IsPackageCancelled { get; private set; }

    /// <summary>项目包操作的统一状态语义。</summary>
    public TaskRunState PackageState =>
        IsImportingPackage || IsExportingPackage
            ? TaskRunState.Running
            : IsPackageCancelled
                ? TaskRunState.Cancelled
                : IsPackageFailure
                    ? TaskRunState.Failed
                    : HasPackageResult
                        ? TaskRunState.Completed
                        : TaskRunState.Pending;

    /// <summary>是否有项目包操作正在运行。</summary>
    public bool IsPackageBusy => IsImportingPackage || IsExportingPackage;

    /// <summary>
    /// 导入结论的状态文案。
    /// </summary>
    /// <remarks>
    /// 三种结论对应三种不同的状态语义（规范 §5.4）：
    /// 部分成功不是失败，整体失败也不是"部分成功"。
    /// </remarks>
    public string ImportStatusText => IsFailedImport
        ? "导入失败"
        : IsPartialImport
            ? "部分成功"
            : "导入成功";

    /// <summary>本地存储位置（告诉用户数据存在哪里）。</summary>
    public string StorageLocation => _storage.Location;

    /// <summary>
    /// 是否已加载且没有任何可见资产。
    /// </summary>
    /// <remarks>
    /// 刻意排除<b>读取失败</b>：“数据库读不到”和“数据库里确实没有资产”是两种结论，
    /// 若都显示“当前项目还没有资产”，用户会以为数据丢了，而真实原因是权限或文件占用。
    /// </remarks>
    public bool IsEmptyAfterLoad =>
        HasLoaded && !HasVisibleAssets && !IsLoading && !HasLoadFailure;

    /// <summary>是否显示“尚未加载”的初始空状态。</summary>
    public bool IsInitialEmpty => !HasLoaded && !IsLoading;

    /// <summary>加载是否失败（与“加载后为空”是两种状态）。</summary>
    public bool HasLoadFailure => LoadErrorMessage.Length > 0;

    /// <summary>是否已选中资产（决定右侧详情面板是否可见）。</summary>
    public bool HasSelectedAsset => SelectedAsset is not null;

    /// <summary>右侧是否显示“尚未选择资产”的空状态。</summary>
    public bool IsNoAssetSelected => SelectedAsset is null;

    /// <summary>当前项目是否存在（没有项目时不能导入或新增资产）。</summary>
    public bool HasCurrentProject => SelectedProject is not null;

    /// <summary>能否导入 CSV。</summary>
    public bool CanImport =>
        HasCurrentProject
        && !IsImporting
        && !IsImportingPackage
        && !IsExportingPackage
        && !IsLoading;

    /// <summary>能否导出 CSV。</summary>
    public bool CanExport =>
        HasVisibleAssets
        && !IsExporting
        && !IsImportingPackage
        && !IsExportingPackage;

    /// <summary>能否导入项目包。</summary>
    public bool CanImportProjectPackage =>
        !IsLoading
        && !IsImporting
        && !IsExporting
        && !IsImportingPackage
        && !IsExportingPackage;

    /// <summary>能否导出当前项目包。</summary>
    public bool CanExportProjectPackage =>
        HasCurrentProject
        && !IsLoading
        && !IsImporting
        && !IsExporting
        && !IsImportingPackage
        && !IsExportingPackage;

    /// <summary>能否新增资产。</summary>
    public bool CanAddAsset => HasCurrentProject && !IsSaving;

    /// <summary>能否删除当前选中的资产。</summary>
    public bool CanDeleteAsset => SelectedAsset is not null && !IsSaving;

    /// <summary>能否把选中环境设为当前环境。</summary>
    public bool CanActivateEnvironment =>
        SelectedEnvironment is { IsActive: false } && HasCurrentProject;

    /// <summary>能否从凭据库检查当前引用。</summary>
    public bool CanCheckCredential => !IsCredentialBusy && BuildSecretReference() is not null;

    /// <summary>能否保存当前凭据输入。</summary>
    public bool CanSaveCredential =>
        CanCheckCredential && !string.IsNullOrEmpty(AssetSecretValue);

    /// <summary>能否删除当前引用对应的凭据。</summary>
    public bool CanDeleteCredential => CanCheckCredential;

    // ==================== 资产表单 ====================

    /// <summary>表单资产名称。</summary>
    [ObservableProperty]
    public partial string AssetName { get; set; } = string.Empty;

    /// <summary>表单主机。</summary>
    [ObservableProperty]
    public partial string AssetHost { get; set; } = string.Empty;

    /// <summary>表单端口（可空）。</summary>
    [ObservableProperty]
    public partial string AssetPort { get; set; } = string.Empty;

    /// <summary>表单类型。</summary>
    [ObservableProperty]
    public partial AssetKindOption? AssetKind { get; set; }

    /// <summary>表单厂商。</summary>
    [ObservableProperty]
    public partial string AssetVendor { get; set; } = string.Empty;

    /// <summary>表单标签文本。</summary>
    [ObservableProperty]
    public partial string AssetTags { get; set; } = string.Empty;

    /// <summary>表单凭据引用键（不是密码本身）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCredentialCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCredentialCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCredentialCommand))]
    public partial string AssetCredentialReference { get; set; } = string.Empty;

    /// <summary>
    /// 待保存到当前用户凭据库的明文；保存尝试结束后立即清空。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCredentialCommand))]
    public partial string AssetSecretValue { get; set; } = string.Empty;

    /// <summary>凭据库操作是否正在运行。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCredentialCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCredentialCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCredentialCommand))]
    public partial bool IsCredentialBusy { get; private set; }

    /// <summary>凭据库状态，不包含秘密值。</summary>
    [ObservableProperty]
    public partial string CredentialStatusText { get; private set; } =
        "未配置凭据引用。";

    /// <summary>凭据库操作的可读反馈，不包含秘密值。</summary>
    [ObservableProperty]
    public partial string CredentialFeedback { get; private set; } = string.Empty;

    /// <summary>凭据状态是否为不可读取或操作失败。</summary>
    [ObservableProperty]
    public partial bool IsCredentialFailure { get; private set; }

    /// <summary>表单备注。</summary>
    [ObservableProperty]
    public partial string AssetNote { get; set; } = string.Empty;

    /// <summary>表单字段级错误提示。</summary>
    [ObservableProperty]
    public partial string AssetFormError { get; private set; } = string.Empty;

    /// <summary>是否正在编辑已有资产（false 表示新建）。</summary>
    [ObservableProperty]
    public partial bool IsEditingExistingAsset { get; private set; }

    // ==================== 生命周期 ====================

    partial void OnSelectedProjectChanged(ProjectDisplayItem? value)
    {
        // 切换项目时清空上一项目的资产与环境：
        // 留着旧数据会让用户以为“这些设备属于新项目”。
        Assets.Clear();
        AssetRows.Clear();
        Environments.Clear();
        SelectedAsset = null;
        SelectedEnvironment = null;
        ClearAssetForm();
        ClearImportResult();

        OnPropertyChanged(nameof(HasCurrentProject));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanAddAsset));
        OnPropertyChanged(nameof(CanActivateEnvironment));
        OnPropertyChanged(nameof(CanExportProjectPackage));

        if (value is not null)
        {
            _ = LoadAssetsAsync();
        }
    }

    partial void OnSelectedEnvironmentChanged(EnvironmentDisplayItem? value)
    {
        OnPropertyChanged(nameof(CanActivateEnvironment));
    }

    partial void OnSelectedAssetChanged(AssetDisplayItem? value)
    {
        LoadAssetIntoForm(value);
        OnPropertyChanged(nameof(HasSelectedAsset));
        OnPropertyChanged(nameof(IsNoAssetSelected));
        OnPropertyChanged(nameof(CanDeleteAsset));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnAssetCredentialReferenceChanged(string value)
    {
        // 引用键变化后，旧输入属于旧引用；不得把它静默保存到新引用下。
        AssetSecretValue = string.Empty;
        CredentialFeedback = string.Empty;
        IsCredentialFailure = false;
        CredentialStatusText = BuildSecretReference() is null
            ? "未配置有效的凭据引用。"
            : "尚未检查；点击“检查状态”验证当前 Windows 用户的凭据库。";
    }

    partial void OnSelectedKindOptionChanged(AssetKindOption? value) => ApplyFilter();

    partial void OnIsGroupByTagChanged(bool value) => ApplyFilter();

    partial void OnLoadErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasLoadFailure));
        OnPropertyChanged(nameof(IsEmptyAfterLoad));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInitialEmpty));
        OnPropertyChanged(nameof(IsEmptyAfterLoad));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanImportProjectPackage));
        OnPropertyChanged(nameof(CanExportProjectPackage));
        NotifyCommands();
    }

    partial void OnIsImportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanImportProjectPackage));
        OnPropertyChanged(nameof(CanExportProjectPackage));
        NotifyCommands();
    }

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanImportProjectPackage));
        OnPropertyChanged(nameof(CanExportProjectPackage));
        NotifyCommands();
    }

    partial void OnIsImportingPackageChanged(bool value)
    {
        OnPropertyChanged(nameof(PackageState));
        OnPropertyChanged(nameof(IsPackageBusy));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanImportProjectPackage));
        OnPropertyChanged(nameof(CanExportProjectPackage));
        NotifyCommands();
    }

    partial void OnIsExportingPackageChanged(bool value)
    {
        OnPropertyChanged(nameof(PackageState));
        OnPropertyChanged(nameof(IsPackageBusy));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanImportProjectPackage));
        OnPropertyChanged(nameof(CanExportProjectPackage));
        NotifyCommands();
    }

    partial void OnHasPackageResultChanged(bool value) => OnPropertyChanged(nameof(PackageState));

    partial void OnIsPackageFailureChanged(bool value) => OnPropertyChanged(nameof(PackageState));

    partial void OnIsPackageCancelledChanged(bool value) => OnPropertyChanged(nameof(PackageState));

    partial void OnIsSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAddAsset));
        OnPropertyChanged(nameof(CanDeleteAsset));
        NotifyCommands();
    }

    // ==================== 命令 ====================

    /// <summary>
    /// 加载项目与资产。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task LoadAsync(
        string? preferredProjectId = null,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCts = linked;

        try
        {
            await PostAsync(() =>
            {
                IsLoading = true;
                LoadErrorMessage = string.Empty;
            }).ConfigureAwait(false);

            // 先确保数据库可用：损坏库会被隔离重建，这个结论必须让用户知道。
            var initialization = await _storage.InitializeAsync(linked.Token).ConfigureAwait(false);

            var projects = await _projectStore.ListAsync(linked.Token).ConfigureAwait(false);
            var items = projects.Select(static project => ProjectDisplayItem.From(project)).ToArray();

            await PostAsync(() =>
            {
                Projects.Clear();

                foreach (var item in items)
                {
                    Projects.Add(item);
                }

                OnPropertyChanged(nameof(HasProjects));

                // 默认选中最近修改的项目：用户打开这一页通常是要继续上次的工作。
                SelectedProject = preferredProjectId is null
                    ? items.FirstOrDefault()
                    : items.FirstOrDefault(item => item.Id == preferredProjectId)
                        ?? items.FirstOrDefault();

                IsLoading = false;
                HasLoaded = true;
                ActionFeedback = initialization.Description;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() =>
            {
                IsLoading = false;
                HasLoaded = true;
                LoadErrorMessage = "加载已取消。";
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            // 存储不可用是环境问题：必须显示可读原因，而不是让页面一片空白（规范 §5.6）。
            await PostAsync(() =>
            {
                IsLoading = false;
                HasLoaded = true;
                LoadErrorMessage = $"{ex.GetType().Name} — {ex.Message}";
            }).ConfigureAwait(false);
        }
        finally
        {
            _loadCts = null;
            await PostAsync(() =>
            {
                IsLoading = false;
                NotifyCommands();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>取消加载。</summary>
    [RelayCommand]
    private void CancelLoad() => _loadCts?.Cancel();

    /// <summary>
    /// 新建项目。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [RelayCommand]
    private async Task CreateProjectAsync(CancellationToken cancellationToken)
    {
        var name = NewProjectName.Trim();

        if (name.Length == 0)
        {
            ActionFeedback = "请先填写项目名称。";
            return;
        }

        var description = NewProjectDescription.Trim();

        var project = new Project
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = description.Length > 0 ? description : null,
            CreatedAt = DateTimeOffset.Now
        };

        try
        {
            var saved = await _projectStore.UpsertAsync(project, cancellationToken).ConfigureAwait(false);

            await PostAsync(() =>
            {
                var item = ProjectDisplayItem.From(saved);
                Projects.Insert(0, item);
                OnPropertyChanged(nameof(HasProjects));
                NewProjectName = string.Empty;
                NewProjectDescription = string.Empty;
                SelectedProject = item;
                ActionFeedback = $"已创建项目“{saved.Name}”。";
            }).ConfigureAwait(false);
        }
        catch (StorageException ex)
        {
            // 重名是可由用户自行解决的问题：直接展示存储层给出的可读说明。
            await PostAsync(() => ActionFeedback = ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() => ActionFeedback = "已取消创建项目。").ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() => ActionFeedback = $"创建项目失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 删除当前选中的项目。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 项目的环境与资产由数据库外键级联删除，因此这里不需要逐个清理。
    /// </remarks>
    [RelayCommand]
    private async Task DeleteProjectAsync(CancellationToken cancellationToken)
    {
        var project = SelectedProject;

        if (project is null)
        {
            return;
        }

        try
        {
            await _projectStore.DeleteAsync(project.Id, cancellationToken).ConfigureAwait(false);

            await PostAsync(() =>
            {
                Projects.Remove(project);
                OnPropertyChanged(nameof(HasProjects));
                SelectedProject = Projects.FirstOrDefault();
                ActionFeedback = $"已删除项目“{project.Name}”，其环境与资产已一并删除。";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() => ActionFeedback = "已取消删除项目。").ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() => ActionFeedback = $"删除项目失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 新建环境。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [RelayCommand]
    private async Task CreateEnvironmentAsync(CancellationToken cancellationToken)
    {
        var project = SelectedProject;

        if (project is null)
        {
            ActionFeedback = "请先选择项目。";
            return;
        }

        var name = NewEnvironmentName.Trim();

        if (name.Length == 0)
        {
            ActionFeedback = "请先填写环境名称。";
            return;
        }

        var parsed = EnvironmentVariableText.Parse(NewEnvironmentVariables);

        if (parsed.HasErrors)
        {
            // 环境变量里有疑似凭据的条目时必须拦住：
            // 环境变量会随项目导出、随报告展示。
            ActionFeedback = "环境变量未保存：" +
                string.Join("；", parsed.Errors.Select(static issue => issue.Message));
            return;
        }

        var proxy = NewEnvironmentProxyUrl.Trim();

        var profile = new EnvironmentProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = project.Id,
            Name = name,
            Variables = parsed.Variables,
            ProxyUrl = proxy.Length > 0 ? proxy : null
        };

        try
        {
            await _environmentStore.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);

            await PostAsync(() =>
            {
                Environments.Add(EnvironmentDisplayItem.From(profile));
                SortEnvironments();
                OnPropertyChanged(nameof(HasEnvironments));
                NewEnvironmentName = string.Empty;
                NewEnvironmentProxyUrl = string.Empty;
                NewEnvironmentVariables = string.Empty;
                ActionFeedback = $"已创建环境“{profile.Name}”。";
            }).ConfigureAwait(false);
        }
        catch (StorageException ex)
        {
            await PostAsync(() => ActionFeedback = ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() => ActionFeedback = "已取消创建环境。").ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() => ActionFeedback = $"创建环境失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把选中环境设为当前环境。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [RelayCommand]
    private async Task ActivateEnvironmentAsync(CancellationToken cancellationToken)
    {
        var project = SelectedProject;
        var environment = SelectedEnvironment;

        if (project is null || environment is null)
        {
            return;
        }

        try
        {
            var switched = await _environmentStore
                .SetActiveAsync(project.Id, environment.Id, cancellationToken)
                .ConfigureAwait(false);

            await PostAsync(() =>
            {
                foreach (var item in Environments)
                {
                    item.IsActive = switched && item.Id == environment.Id;
                }

                OnPropertyChanged(nameof(HasActiveEnvironment));
                OnPropertyChanged(nameof(CanActivateEnvironment));
                ActionFeedback = switched
                    ? $"当前环境已切换为“{environment.Name}”。"
                    : "切换失败：该环境不存在或不属于当前项目。";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() => ActionFeedback = "已取消切换环境。").ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() => ActionFeedback = $"切换环境失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 保存资产表单（新增或更新）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [RelayCommand]
    private async Task SaveAssetAsync(CancellationToken cancellationToken)
    {
        var project = SelectedProject;

        if (project is null)
        {
            ActionFeedback = "请先选择项目。";
            return;
        }

        var validation = ValidateAssetForm();

        if (validation is not null)
        {
            AssetFormError = validation;
            return;
        }

        AssetFormError = string.Empty;

        var existing = SelectedAsset?.Source;
        var tags = AssetTagText.Normalize(AssetTags);
        // 刻意全限定：属性名 AssetCredentialReference 会遮蔽同名类型。
        var credential = Application.Assets.AssetCredentialReference.Normalize(AssetCredentialReference);
        var vendor = AssetVendor.Trim();
        var note = AssetNote.Trim();
        int? port = null;

        if (AssetPort.Trim().Length > 0)
        {
            port = int.Parse(AssetPort.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var asset = new Asset
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            ProjectId = project.Id,
            Name = AssetName.Trim(),
            Host = AssetHost.Trim(),
            Port = port,
            // 全限定：属性名 AssetKind 会遮蔽同名枚举。
            Kind = AssetKind?.Kind ?? Domain.Assets.AssetKind.Unknown,
            Vendor = vendor.Length > 0 ? vendor : null,
            Tags = tags,
            CredentialReference = credential,
            Note = note.Length > 0 ? note : null
        };

        await PostAsync(() => IsSaving = true).ConfigureAwait(false);

        try
        {
            await _assetStore.UpsertAsync(asset, cancellationToken).ConfigureAwait(false);

            await PostAsync(async () =>
            {
                await LoadAssetsAsync().ConfigureAwait(false);
                ClearAssetForm();
                ActionFeedback = $"已保存资产“{asset.Name}”。";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() => ActionFeedback = "已取消保存资产。").ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() => ActionFeedback = $"保存资产失败：{ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            await PostAsync(() => IsSaving = false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 删除当前选中的资产。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [RelayCommand]
    private async Task DeleteAssetAsync(CancellationToken cancellationToken)
    {
        var asset = SelectedAsset;

        if (asset is null)
        {
            return;
        }

        try
        {
            await _assetStore.DeleteAsync(asset.Id, cancellationToken).ConfigureAwait(false);

            await PostAsync(async () =>
            {
                SelectedAsset = null;
                ClearAssetForm();
                await LoadAssetsAsync().ConfigureAwait(false);
                ActionFeedback = $"已删除资产“{asset.Name}”。";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() => ActionFeedback = "已取消删除资产。").ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() => ActionFeedback = $"删除资产失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>清空资产表单（回到新建状态）。</summary>
    [RelayCommand]
    private void ResetAssetForm() => ClearAssetForm();

    /// <summary>检查当前引用键是否已有可读取的凭据。</summary>
    [RelayCommand(CanExecute = nameof(CanCheckCredential))]
    private async Task CheckCredentialAsync(CancellationToken cancellationToken)
    {
        var reference = BuildSecretReference();

        if (reference is null)
        {
            CredentialStatusText = "凭据引用键无效，无法检查。";
            IsCredentialFailure = true;
            return;
        }

        await PostAsync(() =>
        {
            IsCredentialBusy = true;
            CredentialFeedback = string.Empty;
            IsCredentialFailure = false;
        }).ConfigureAwait(false);

        try
        {
            var status = await _secretStore.GetStatusAsync(reference.Value, cancellationToken)
                .ConfigureAwait(false);

            await PostAsync(() =>
            {
                CredentialStatusText = status.State switch
                {
                    SecretEntryState.Available =>
                        $"已保存；最后更新 {status.UpdatedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}。",
                    SecretEntryState.Unreadable => status.Message,
                    _ => status.Message
                };
                IsCredentialFailure = status.State == SecretEntryState.Unreadable;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() =>
            {
                CredentialFeedback = "已取消检查凭据状态。";
                IsCredentialFailure = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSecretFailure(ex))
        {
            await PostAsync(() =>
            {
                CredentialStatusText = "凭据状态不可用。";
                CredentialFeedback = $"检查失败：{ex.Message}";
                IsCredentialFailure = true;
            }).ConfigureAwait(false);
        }
        finally
        {
            await PostAsync(() => IsCredentialBusy = false).ConfigureAwait(false);
        }
    }

    /// <summary>把当前输入保存到当前 Windows 用户的 DPAPI 凭据库。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveCredential))]
    private async Task SaveCredentialAsync(CancellationToken cancellationToken)
    {
        var reference = BuildSecretReference();

        if (reference is null)
        {
            CredentialStatusText = "请先填写有效的凭据引用键。";
            IsCredentialFailure = true;
            return;
        }

        if (string.IsNullOrEmpty(AssetSecretValue))
        {
            CredentialFeedback = "凭据值不能为空。";
            IsCredentialFailure = true;
            return;
        }

        var value = AssetSecretValue;
        await PostAsync(() =>
        {
            IsCredentialBusy = true;
            AssetSecretValue = string.Empty;
            CredentialFeedback = string.Empty;
            IsCredentialFailure = false;
        }).ConfigureAwait(false);

        try
        {
            await _secretStore.SaveAsync(reference.Value, value, cancellationToken)
                .ConfigureAwait(false);
            var status = await _secretStore.GetStatusAsync(reference.Value, cancellationToken)
                .ConfigureAwait(false);

            await PostAsync(() =>
            {
                CredentialStatusText = status.State == SecretEntryState.Available
                    ? $"已保存；最后更新 {status.UpdatedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}。"
                    : status.Message;
                CredentialFeedback = status.State == SecretEntryState.Available
                    ? "凭据已加密保存到当前 Windows 用户。项目包与导出文件不包含凭据值。"
                    : "凭据已写入，但状态复查未通过。";
                IsCredentialFailure = status.State != SecretEntryState.Available;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() =>
            {
                CredentialFeedback = "已取消保存凭据。";
                IsCredentialFailure = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSecretFailure(ex))
        {
            await PostAsync(() =>
            {
                CredentialFeedback = $"保存凭据失败：{ex.Message}";
                IsCredentialFailure = true;
            }).ConfigureAwait(false);
        }
        finally
        {
            value = string.Empty;
            await PostAsync(() => IsCredentialBusy = false).ConfigureAwait(false);
        }
    }

    /// <summary>从当前 Windows 用户的凭据库删除该引用键。</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteCredential))]
    private async Task DeleteCredentialAsync(CancellationToken cancellationToken)
    {
        var reference = BuildSecretReference();

        if (reference is null)
        {
            return;
        }

        await PostAsync(() =>
        {
            IsCredentialBusy = true;
            AssetSecretValue = string.Empty;
            CredentialFeedback = string.Empty;
            IsCredentialFailure = false;
        }).ConfigureAwait(false);

        try
        {
            var deleted = await _secretStore.DeleteAsync(reference.Value, cancellationToken)
                .ConfigureAwait(false);

            await PostAsync(() =>
            {
                CredentialStatusText = "凭据条目未保存。";
                CredentialFeedback = deleted
                    ? "已从当前 Windows 用户的凭据库删除。"
                    : "该引用键当前没有已保存的凭据。";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() =>
            {
                CredentialFeedback = "已取消删除凭据。";
                IsCredentialFailure = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSecretFailure(ex))
        {
            await PostAsync(() =>
            {
                CredentialFeedback = $"删除凭据失败：{ex.Message}";
                IsCredentialFailure = true;
            }).ConfigureAwait(false);
        }
        finally
        {
            await PostAsync(() => IsCredentialBusy = false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 从 CSV 导入资产。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [RelayCommand]
    private async Task ImportCsvAsync(CancellationToken cancellationToken)
    {
        var project = SelectedProject;

        if (project is null)
        {
            ActionFeedback = "请先选择项目，再导入 CSV。";
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _importCts = linked;

        AssetCsvImportFile? file;

        try
        {
            // 文件对话框必须在 UI 线程上弹出：ConfigureAwait(true) 保证这一点。
            file = await _fileService.PickAndReadAsync(linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() => ActionFeedback = "已取消导入。").ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() => ActionFeedback = $"读取文件失败：{ex.Message}").ConfigureAwait(false);
            return;
        }

        if (file is null)
        {
            // 用户在对话框里点了取消：不是失败，只是没开始。
            await PostAsync(() => ActionFeedback = "已取消导入。").ConfigureAwait(false);
            return;
        }

        await PostAsync(() =>
        {
            IsImporting = true;
            ClearImportResult();
        }).ConfigureAwait(false);

        try
        {
            var outcome = await _importer
                .ImportAsync(file.Value.Content, project.Id, linked.Token)
                .ConfigureAwait(false);

            await PostAsync(async () =>
            {
                await LoadAssetsAsync().ConfigureAwait(false);
                ApplyImportOutcome(outcome, file.Value.FileName);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() =>
            {
                IsImporting = false;
                ImportSummary = "导入已取消，未写入任何资产。";
                HasImportResult = true;
                IsFailedImport = true;
                ActionFeedback = "已取消导入。";
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() =>
            {
                IsImporting = false;
                ImportSummary = $"导入失败：{ex.Message}";
                HasImportResult = true;
                IsFailedImport = true;
                ActionFeedback = "导入失败。";
            }).ConfigureAwait(false);
        }
        finally
        {
            _importCts = null;
            await PostAsync(() =>
            {
                IsImporting = false;
                NotifyCommands();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>取消导入。</summary>
    [RelayCommand]
    private void CancelImport() => _importCts?.Cancel();

    /// <summary>
    /// 把当前筛选后可见的资产导出为 CSV。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [RelayCommand]
    private async Task ExportCsvAsync(CancellationToken cancellationToken)
    {
        if (!HasVisibleAssets)
        {
            ActionFeedback = "当前没有可导出的资产。";
            return;
        }

        await PostAsync(() =>
        {
            IsExporting = true;
            ActionFeedback = string.Empty;
        }).ConfigureAwait(false);

        try
        {
            // 导出范围 = 当前可见的资产：用户在界面上看到的与导出的必须一致。
            var visible = VisibleAssets.Select(static item => item.Source).ToArray();
            var bytes = _exporter.Build(visible);
            var fileName = _exporter.BuildFileName(SelectedProject?.Name, DateTimeOffset.Now);

            var written = await _fileService
                .PickAndWriteAsync(fileName, bytes, cancellationToken)
                .ConfigureAwait(true);

            await PostAsync(() =>
            {
                IsExporting = false;
                ActionFeedback = written
                    ? $"已导出 {visible.Length} 条资产（仅包含凭据引用键，不含任何密码）。"
                    : "已取消导出。";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() =>
            {
                IsExporting = false;
                ActionFeedback = "已取消导出。";
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() =>
            {
                IsExporting = false;
                ActionFeedback = $"导出失败：{ex.Message}";
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 导入一个版本化 LinkForge 项目包。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [RelayCommand(CanExecute = nameof(CanImportProjectPackage))]
    private async Task ImportProjectPackageAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _packageImportCts = linked;

        ProjectPackageImportFile? file;

        try
        {
            file = await _packageFileService.PickAndReadAsync(linked.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            await SetPackageResultAsync("已取消项目包导入。", success: false, cancelled: true)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await SetPackageResultAsync(
                $"读取项目包失败：{ex.Message}",
                success: false,
                cancelled: false).ConfigureAwait(false);
            return;
        }

        if (file is null)
        {
            await SetPackageResultAsync("已取消项目包导入。", success: false, cancelled: true)
                .ConfigureAwait(false);
            return;
        }

        await PostAsync(() =>
        {
            IsImportingPackage = true;
            HasPackageResult = false;
            IsPackageFailure = false;
            IsPackageCancelled = false;
            PackageFeedback = "正在解析并校验项目包…";
        }).ConfigureAwait(false);

        try
        {
            var package = await Task.Run(
                    () => _packageCodec.Decode(file.Value.Content),
                    linked.Token)
                .ConfigureAwait(false);

            var result = await _packageImporter
                .ImportAsync(package, linked.Token)
                .ConfigureAwait(false);

            await PostAsync(() =>
            {
                IsImportingPackage = false;
                HasPackageResult = true;
                IsPackageFailure = false;
                IsPackageCancelled = false;
                PackageStatusText = "导入成功";
                PackageFeedback =
                    $"已导入项目“{package.Project.Name}”：{result.EnvironmentCount} 个环境、"
                    + $"{result.AssetCount} 条资产。项目包不含明文凭据。";
                ActionFeedback = "项目包导入成功。";
            }).ConfigureAwait(false);

            await LoadAsync(result.ProjectId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SetPackageResultAsync(
                "项目包导入已取消；事务已回滚，未写入任何记录。",
                success: false,
                cancelled: true).ConfigureAwait(false);
        }
        catch (ProjectPackageFormatException ex)
        {
            await SetPackageResultAsync(
                $"项目包校验失败：{ex.Message}",
                success: false,
                cancelled: false).ConfigureAwait(false);
        }
        catch (StorageException ex)
        {
            await SetPackageResultAsync(
                ex.Message,
                success: false,
                cancelled: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await SetPackageResultAsync(
                $"项目包导入失败：{ex.Message}",
                success: false,
                cancelled: false).ConfigureAwait(false);
        }
        finally
        {
            _packageImportCts = null;
            await PostAsync(() =>
            {
                IsImportingPackage = false;
                NotifyCommands();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>取消项目包导入。</summary>
    [RelayCommand]
    private void CancelProjectPackageImport() => _packageImportCts?.Cancel();

    /// <summary>
    /// 把当前项目、环境和资产导出为版本化项目包。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [RelayCommand(CanExecute = nameof(CanExportProjectPackage))]
    private async Task ExportProjectPackageAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedProject;

        if (selected is null)
        {
            await SetPackageResultAsync(
                "请先选择要导出的项目。",
                success: false,
                cancelled: false).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _packageExportCts = linked;

        await PostAsync(() =>
        {
            IsExportingPackage = true;
            HasPackageResult = false;
            IsPackageFailure = false;
            IsPackageCancelled = false;
            PackageFeedback = "正在从本地数据库读取项目并生成项目包…";
        }).ConfigureAwait(false);

        try
        {
            var project = await _projectStore.FindAsync(selected.Id, linked.Token)
                .ConfigureAwait(false)
                ?? throw new StorageException("所选项目已不存在，请刷新后重试。");
            var environments = await _environmentStore.ListAsync(project.Id, linked.Token)
                .ConfigureAwait(false);
            var assets = await _assetStore.ListAsync(project.Id, linked.Token)
                .ConfigureAwait(false);

            var content = await Task.Run(
                    () => _packageCodec.Encode(project, environments, assets),
                    linked.Token)
                .ConfigureAwait(false);
            var fileName = ProjectPackageFileNames.Build(project.Name, DateTimeOffset.Now);
            var written = await _packageFileService
                .PickAndWriteAsync(fileName, content, linked.Token)
                .ConfigureAwait(true);

            await PostAsync(() =>
            {
                IsExportingPackage = false;
                HasPackageResult = true;
                IsPackageFailure = false;
                IsPackageCancelled = !written;
                PackageStatusText = written ? "导出成功" : "已取消";
                PackageFeedback = written
                    ? $"已导出项目“{project.Name}”：{environments.Count} 个环境、"
                        + $"{assets.Count} 条资产。文件只含凭据引用键，不含任何密码或令牌。"
                    : "已取消项目包导出，未写出文件。";
                ActionFeedback = written ? "项目包导出成功。" : "已取消项目包导出。";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SetPackageResultAsync(
                "项目包导出已取消，未写出文件。",
                success: false,
                cancelled: true).ConfigureAwait(false);
        }
        catch (ProjectPackageFormatException ex)
        {
            await SetPackageResultAsync(
                $"当前项目无法导出：{ex.Message}",
                success: false,
                cancelled: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await SetPackageResultAsync(
                $"项目包导出失败：{ex.Message}",
                success: false,
                cancelled: false).ConfigureAwait(false);
        }
        finally
        {
            _packageExportCts = null;
            await PostAsync(() =>
            {
                IsExportingPackage = false;
                NotifyCommands();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>取消项目包导出。</summary>
    [RelayCommand]
    private void CancelProjectPackageExport() => _packageExportCts?.Cancel();

    /// <summary>刷新当前项目的资产与环境。</summary>
    [RelayCommand]
    private void Refresh() => _ = LoadAsync();


    // ==================== 内部实现 ====================

    /// <summary>
    /// 读取当前项目的资产与环境并刷新界面。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task LoadAssetsAsync(CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;

        if (project is null)
        {
            return;
        }

        try
        {
            var assets = await _assetStore.ListAsync(project.Id, cancellationToken).ConfigureAwait(false);
            var environments = await _environmentStore
                .ListAsync(project.Id, cancellationToken)
                .ConfigureAwait(false);

            await PostAsync(() =>
            {
                Assets.Clear();

                foreach (var asset in assets)
                {
                    Assets.Add(new AssetDisplayItem(asset));
                }

                Environments.Clear();

                foreach (var environment in environments)
                {
                    Environments.Add(EnvironmentDisplayItem.From(environment));
                }

                OnPropertyChanged(nameof(HasEnvironments));
                OnPropertyChanged(nameof(HasActiveEnvironment));

                ApplyFilter();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消加载不视为失败：保留上一次的结果。
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            await PostAsync(() => LoadErrorMessage = $"读取资产失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 按关键字、类型与分组方式重新计算可见资产与列表行。
    /// </summary>
    private void ApplyFilter()
    {
        var keyword = SearchText.Trim();
        var kind = SelectedKindOption?.Kind;

        var filtered = Assets
            .Where(item => kind is null || item.Source.Kind == kind)
            .Where(item => keyword.Length == 0 || Matches(item, keyword))
            .ToArray();

        _visibleAssets = filtered;

        AssetRows.Clear();

        if (IsGroupByTag)
        {
            foreach (var group in filtered
                         .Select(static item => GroupKeyOf(item))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static tag => tag, StringComparer.OrdinalIgnoreCase))
            {
                AssetRows.Add(AssetListItem.Header(group));

                foreach (var item in filtered.Where(
                             candidate => string.Equals(
                                 GroupKeyOf(candidate),
                                 group,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    AssetRows.Add(AssetListItem.Row(item));
                }
            }
        }
        else
        {
            foreach (var item in filtered)
            {
                AssetRows.Add(AssetListItem.Row(item));
            }
        }

        OnPropertyChanged(nameof(VisibleAssetCount));
        OnPropertyChanged(nameof(HasVisibleAssets));
        OnPropertyChanged(nameof(CanExport));
    }

    /// <summary>
    /// 分组键：取第一个标签；没有标签的资产归入“未分组”。
    /// </summary>
    /// <param name="item">资产。</param>
    /// <returns>分组标题。</returns>
    private static string GroupKeyOf(AssetDisplayItem item) =>
        item.Source.Tags.Count > 0 ? item.Source.Tags[0] : "未分组";

    /// <summary>
    /// 关键字是否命中资产（名称、主机、厂商与标签）。
    /// </summary>
    /// <param name="item">资产。</param>
    /// <param name="keyword">关键字。</param>
    /// <returns>是否命中。</returns>
    private static bool Matches(AssetDisplayItem item, string keyword)
    {
        if (item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || item.Source.Host.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.Source.Vendor)
            && item.Source.Vendor.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return item.Source.Tags.Any(tag => tag.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 校验资产表单。
    /// </summary>
    /// <returns>错误说明；校验通过时返回 null。</returns>
    private string? ValidateAssetForm()
    {
        var name = AssetName.Trim();

        if (name.Length == 0)
        {
            return "请填写资产名称。";
        }

        if (name.Length > AssetCsvLimits.NameLength)
        {
            return $"资产名称超过 {AssetCsvLimits.NameLength} 字符上限。";
        }

        var host = AssetHost.Trim();

        if (host.Length == 0)
        {
            return "请填写主机（IP 或域名）。";
        }

        if (host.Length > AssetCsvLimits.HostLength)
        {
            return $"主机超过 {AssetCsvLimits.HostLength} 字符上限。";
        }

        if (host.Any(char.IsWhiteSpace))
        {
            return "主机含空白字符，不是有效的 IP 或域名。";
        }

        var portText = AssetPort.Trim();

        if (portText.Length > 0
            && (!int.TryParse(portText, out var port) || port is < 1 or > 65535))
        {
            return "端口必须是 1-65535 之间的数字。";
        }

        if (!Application.Assets.AssetCredentialReference.IsValidKey(AssetCredentialReference))
        {
            return Application.Assets.AssetCredentialReference.InvalidReason();
        }

        var tags = AssetTagText.Normalize(AssetTags);

        if (tags.Count > AssetCsvLimits.TagCount)
        {
            return $"标签超过 {AssetCsvLimits.TagCount} 个上限。";
        }

        if (tags.Any(static tag => tag.Length > AssetCsvLimits.TagLength))
        {
            return $"单个标签超过 {AssetCsvLimits.TagLength} 字符上限。";
        }

        if (AssetVendor.Trim().Length > AssetCsvLimits.VendorLength)
        {
            return $"厂商超过 {AssetCsvLimits.VendorLength} 字符上限。";
        }

        if (AssetNote.Trim().Length > AssetCsvLimits.NoteLength)
        {
            return $"备注超过 {AssetCsvLimits.NoteLength} 字符上限。";
        }

        return null;
    }

    /// <summary>
    /// 把选中的资产载入表单。
    /// </summary>
    /// <param name="asset">资产；为 null 时清空表单。</param>
    private void LoadAssetIntoForm(AssetDisplayItem? asset)
    {
        ClearAssetForm();

        if (asset is null)
        {
            return;
        }

        AssetName = asset.Name;
        AssetHost = asset.Source.Host;
        AssetPort = asset.Source.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        AssetKind = KindOptions.FirstOrDefault(option => option.Kind == asset.Source.Kind);
        AssetVendor = asset.Source.Vendor ?? string.Empty;
        AssetTags = asset.TagsText;
        AssetCredentialReference = asset.Source.CredentialReference ?? string.Empty;
        AssetNote = asset.Source.Note ?? string.Empty;
        IsEditingExistingAsset = true;
    }

    /// <summary>清空资产表单。</summary>
    private void ClearAssetForm()
    {
        AssetName = string.Empty;
        AssetHost = string.Empty;
        AssetPort = string.Empty;
        AssetKind = KindOptions.FirstOrDefault(static option => option.Kind is null);
        AssetVendor = string.Empty;
        AssetTags = string.Empty;
        AssetCredentialReference = string.Empty;
        AssetSecretValue = string.Empty;
        AssetNote = string.Empty;
        AssetFormError = string.Empty;
        CredentialStatusText = "未配置凭据引用。";
        CredentialFeedback = string.Empty;
        IsCredentialFailure = false;
        IsEditingExistingAsset = false;
    }

    /// <summary>清空导入结论。</summary>
    private void ClearImportResult()
    {
        ImportIssues.Clear();
        ImportSummary = string.Empty;
        HasImportResult = false;
        IsPartialImport = false;
        IsFailedImport = false;
    }

    /// <summary>
    /// 把导入结果转成界面结论。
    /// </summary>
    /// <param name="outcome">导入结果。</param>
    /// <param name="fileName">来源文件名。</param>
    private void ApplyImportOutcome(AssetCsvImportOutcome outcome, string fileName)
    {
        ClearImportResult();

        var result = outcome.Result;
        var fileNameText = string.IsNullOrWhiteSpace(fileName) ? "所选文件" : Path.GetFileName(fileName);

        foreach (var error in result.Errors)
        {
            ImportIssues.Add(error.ToString());
        }

        foreach (var warning in result.Warnings)
        {
            ImportIssues.Add(warning.ToString());
        }

        if (result.HasFatalError)
        {
            ImportSummary = $"导入失败：{result.FatalError}";
            HasImportResult = true;
            IsFailedImport = true;
            ActionFeedback = "导入失败。";
            return;
        }

        if (outcome.ImportedCount == 0)
        {
            ImportSummary = $"“{fileNameText}”中没有可导入的资产。";
            HasImportResult = true;
            IsFailedImport = true;
            ActionFeedback = "导入未写入任何资产。";
            return;
        }

        if (result.Errors.Count > 0)
        {
            // 部分成功：写入的写入了，被拒绝的逐行给出原因，不静默丢弃。
            ImportSummary =
                $"“{fileNameText}”部分成功：已导入 {outcome.ImportedCount} 条，" +
                $"{result.Errors.Count} 行被拒绝（下方逐行给出原因）。";
            HasImportResult = true;
            IsPartialImport = true;
            ActionFeedback = $"已导入 {outcome.ImportedCount} 条资产，另有 {result.Errors.Count} 行未导入。";
            return;
        }

        ImportSummary = $"“{fileNameText}”导入成功：共 {outcome.ImportedCount} 条资产。";
        HasImportResult = true;
        ActionFeedback = $"已导入 {outcome.ImportedCount} 条资产。";
    }

    /// <summary>把项目包操作的最终状态投递到 UI。</summary>
    private async Task SetPackageResultAsync(
        string message,
        bool success,
        bool cancelled)
    {
        await PostAsync(() =>
        {
            IsImportingPackage = false;
            IsExportingPackage = false;
            HasPackageResult = true;
            IsPackageFailure = !success && !cancelled;
            IsPackageCancelled = cancelled;
            PackageStatusText = cancelled
                ? "已取消"
                : success
                    ? "成功"
                    : "失败";
            PackageFeedback = message;
            ActionFeedback = message;
        }).ConfigureAwait(false);
    }

    /// <summary>按名称排序环境列表。</summary>
    private void SortEnvironments()
    {
        var sorted = Environments.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        for (var index = 0; index < sorted.Length; index++)
        {
            var current = Environments.IndexOf(sorted[index]);

            if (current >= 0 && current != index)
            {
                Environments.Move(current, index);
            }
        }
    }

    /// <summary>
    /// 判断异常是否属于"用户可自行处理或需要明确原因"的存储/环境失败。
    /// </summary>
    /// <param name="ex">异常。</param>
    /// <returns>是否应按可读失败呈现。</returns>
    private static bool IsStorageFailure(Exception ex) =>
        ex is StorageException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException;

    /// <summary>凭据库失败是否应按可读错误呈现。</summary>
    private static bool IsSecretFailure(Exception ex) =>
        ex is SecretStoreException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    /// <summary>
    /// 从当前表单引用键构造秘密引用；键无效时返回 null。
    /// </summary>
    private SecretReference? BuildSecretReference()
    {
        var key = Application.Assets.AssetCredentialReference.Normalize(AssetCredentialReference);

        if (key is null || !Application.Assets.AssetCredentialReference.IsValidKey(key))
        {
            return null;
        }

        return new SecretReference(
            key,
            SecretProvider.DpapiCurrentUser,
            SecretKind.Password,
            $"资产凭据 {AssetName.Trim()}".TrimEnd());
    }

    /// <summary>刷新受状态影响的命令可用性。</summary>
    private void NotifyCommands()
    {
        CreateProjectCommand.NotifyCanExecuteChanged();
        DeleteProjectCommand.NotifyCanExecuteChanged();
        CreateEnvironmentCommand.NotifyCanExecuteChanged();
        ActivateEnvironmentCommand.NotifyCanExecuteChanged();
        SaveAssetCommand.NotifyCanExecuteChanged();
        DeleteAssetCommand.NotifyCanExecuteChanged();
        ImportCsvCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ImportProjectPackageCommand.NotifyCanExecuteChanged();
        ExportProjectPackageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 把动作投递到 UI 线程执行。
    /// </summary>
    /// <param name="action">动作。</param>
    /// <returns>完成通知。</returns>
    private Task PostAsync(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _uiContext.Post(_ =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, null);

        return completion.Task;
    }

    /// <summary>
    /// 把异步动作投递到 UI 线程执行。
    /// </summary>
    /// <param name="action">动作。</param>
    /// <returns>完成通知。</returns>
    /// <remarks>
    /// 必须在 UI 线程上等待它：投递后再读集合会读到“命令完成但界面未更新”的中间态。
    /// </remarks>
    private Task PostAsync(Func<Task> action)
    {
        if (_uiContext is null)
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 刻意用同步的 Post + 内部自建 Task：Send/Post 的回调不能是 async void，
        // 否则回调里抛出的异常会变成未观察的异步异常，
        // 调用方 await 到的 completion 永远不完成，界面就会一直停在"运行中"。
        _uiContext.Post(_ =>
        {
            _ = RunAsync();

            async Task RunAsync()
            {
                try
                {
                    await action().ConfigureAwait(true);
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }
        }, null);

        return completion.Task;
    }
}
