using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.App.Services;

namespace LinkForge.App.ViewModels;

/// <summary>
/// 一条"环境事实"（工作台展示用）。
/// </summary>
/// <param name="Label">项目名称。</param>
/// <param name="Value">真实取值。</param>
public readonly record struct EnvironmentFact(string Label, string Value);

/// <summary>
/// 工作台视图模型。
/// </summary>
/// <remarks>
/// <para>
/// 依据 UI 规范 §4.1 与 DEV_STANDARDS §6.6：
/// <b>工作台只显示真实数据，没有真实数据时显示空状态，不编造数值。</b>
/// </para>
/// <para>
/// 因此阶段 03 的工作台只呈现三类<b>可验证</b>内容：
/// </para>
/// <list type="number">
///   <item>当前运行环境（版本、运行时、系统、架构）——直接读取，必然为真。</item>
///   <item>外部依赖探测结果——真实探测，缺失就如实标注"未检测到"。</item>
///   <item>项目与任务——尚无持久化，因此明确显示空状态而不是示例数据。</item>
/// </list>
/// <para>
/// "快捷动作"只列出功能树中已实现的模块。当前阶段除工作台自身外均未实现，
/// 因此快捷动作区显示"尚无可用动作"而不是放一排点了没反应的按钮。
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppEnvironment _environment;

    /// <summary>
    /// 创建工作台视图模型。
    /// </summary>
    /// <param name="environment">环境信息读取器。</param>
    public DashboardViewModel(AppEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;

        EnvironmentFacts =
        [
            new("应用版本", _environment.ApplicationVersion),
            new("运行时", _environment.RuntimeDescription),
            new("操作系统", _environment.WindowsVersionName),
            new("进程架构", _environment.ProcessArchitecture),
            new("配置位置", _environment.SettingsFilePath ?? "未知")
        ];

        Dependencies = new ObservableCollection<DependencyProbe>(_environment.ProbeDependencies());

        MissingDependencies = new ObservableCollection<DependencyProbe>(
            Dependencies.Where(static d => !d.IsAvailable));
    }

    /// <summary>运行环境事实。</summary>
    public IReadOnlyList<EnvironmentFact> EnvironmentFacts { get; }

    /// <summary>外部依赖探测结果（全部）。</summary>
    public ObservableCollection<DependencyProbe> Dependencies { get; }

    /// <summary>不可用的依赖（用于依赖缺失提示区）。</summary>
    public ObservableCollection<DependencyProbe> MissingDependencies { get; }

    /// <summary>是否存在缺失依赖。</summary>
    public bool HasMissingDependencies => MissingDependencies.Count > 0;

    /// <summary>是否已有项目（阶段 03 尚无持久化，恒为 false）。</summary>
    /// <remarks>
    /// 保留为属性而非直接写死 false：阶段 04 接入持久化后，
    /// 这里只需改为读取真实仓储，页面与空状态判断无需改动。
    /// </remarks>
    public bool HasProjects => false;

    /// <summary>是否已有任务记录（阶段 03 尚无持久化，恒为 false）。</summary>
    public bool HasRuns => false;

    /// <summary>空状态文案：说明为什么是空的，以及下一步该做什么。</summary>
    public string ProjectsEmptyDescription =>
        "尚未创建项目。项目管理功能将在后续阶段交付，届时可在此新建或导入项目。";

    /// <inheritdoc cref="ProjectsEmptyDescription"/>
    public string RunsEmptyDescription =>
        "尚无诊断任务记录。接入调试与网络诊断功能交付后，任务结果会汇总到这里。";

    /// <summary>
    /// 刷新依赖探测。
    /// </summary>
    /// <remarks>
    /// 用户可能在应用运行期间安装了 Npcap 或 FFmpeg，
    /// 因此需要提供手动刷新入口，而不是要求重启应用。
    /// 探测是只读且轻量的（查文件、查 PATH、读版本号），
    /// 不发起网络请求、不启动长任务，因此可以同步执行；
    /// 将来若加入耗时探测（如端口扫描），必须改为异步并支持取消。
    /// </remarks>
    [RelayCommand]
    public void RefreshDependencies()
    {
        Dependencies.Clear();

        foreach (var probe in _environment.ProbeDependencies())
        {
            Dependencies.Add(probe);
        }

        MissingDependencies.Clear();

        foreach (var probe in Dependencies.Where(static d => !d.IsAvailable))
        {
            MissingDependencies.Add(probe);
        }

        OnPropertyChanged(nameof(HasMissingDependencies));
    }
}
