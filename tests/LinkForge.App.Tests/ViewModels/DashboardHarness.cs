using System.IO;
using System.Security.AccessControl;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.Application.Diagnostics;
using LinkForge.Application.Environment;
using LinkForge.Application.Projects;
using LinkForge.Domain.Projects;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// 构造 <see cref="DashboardViewModel"/> 的测试夹具。
/// </summary>
/// <remarks>
/// <para>
/// 集中构造而不是在每个测试里重复六个参数：工作台的依赖是真实对象
/// （真实编排器、真实文件历史、真实自检适配器），
/// 若各测试各拼一套，会很快演化出"某个测试用的是假时钟、另一个不是"这类不一致，
/// 而这类不一致正是"测试通过但生产行为不同"的来源。
/// </para>
/// <para>
/// <b>刻意不注入假的 IDiagnosticAdapter。</b>
/// 工作台要在阶段 04 验证的就是"真实自检任务能跑通、能取消、能落历史"，
/// 换成假适配器等于把被测对象换掉，测出来的结论与生产无关。
/// </para>
/// </remarks>
internal sealed class DashboardHarness : IDisposable
{
    private readonly string _directory;

    /// <summary>
    /// 创建夹具。
    /// </summary>
    /// <param name="timeProvider">时间源；null 时使用系统时钟。</param>
    public DashboardHarness(TimeProvider? timeProvider = null)
    {
        StaleTempDirectoryCleanup.Sweep();

        _directory = Path.Combine(
            Path.GetTempPath(),
            "linkforge-dashboard-" + Guid.NewGuid().ToString("N"));

        System.IO.Directory.CreateDirectory(_directory);

        SettingsPath = Path.Combine(_directory, "settings.json");
        HistoryPath = Path.Combine(_directory, "run-history.json");

        HistoryStore = new JsonRunHistoryStore(_directory);
        Adapter = new EnvironmentSelfCheckAdapter(_directory, new SecretTextRedactor());
        Runner = new DiagnosticTaskRunner(new SecretTextRedactor(), timeProvider);
        ProjectStore = new FakeProjectStore();

        ViewModel = new DashboardViewModel(
            new AppEnvironment(SettingsPath),
            new DependencyCatalog(timeProvider),
            HistoryStore,
            ProjectStore,
            Adapter,
            Runner,
            timeProvider);
    }

    /// <summary>项目存储（工作台的"项目"区读它）。</summary>
    public FakeProjectStore ProjectStore { get; }

    /// <summary>设置文件路径（环境的"配置位置"一行显示它）。</summary>
    public string SettingsPath { get; }

    /// <summary>历史文件路径（用于断言"历史确实落盘"）。</summary>
    public string HistoryPath { get; }

    /// <summary>数据目录（证据落盘位置）。</summary>
    public string DataDirectory => _directory;

    /// <summary>历史存储（断言用）。</summary>
    public JsonRunHistoryStore HistoryStore { get; }

    /// <summary>自检适配器（断言用）。</summary>
    public IDiagnosticAdapter Adapter { get; }

    /// <summary>任务编排器。</summary>
    public DiagnosticTaskRunner Runner { get; }

    /// <summary>被测视图模型。</summary>
    public DashboardViewModel ViewModel { get; }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(_directory))
            {
                System.IO.Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不应让测试失败。
        }
    }
}
