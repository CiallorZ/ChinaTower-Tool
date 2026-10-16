using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LinkForge.App.Services;

/// <summary>
/// 当前运行环境的真实信息（应用版本、运行时、操作系统、配置位置）。
/// </summary>
/// <remarks>
/// <para>
/// 本服务存在的目的：工作台需要展示"当前运行在什么环境上"，
/// 而 UI 规范 §4.1 明确禁止"没有真实数据时编造数值"。
/// 因此这里只报告<b>能实际探测到</b>的事实，探测不到就如实说明"未知"。
/// </para>
/// <para>
/// <b>职责边界：本类不做外部依赖探测。</b>
/// 依赖探测（Npcap / FFmpeg / TShark）会启动外部进程，属于阻塞 I/O，
/// 已在 <c>LinkForge.Application.Environment.DependencyCatalog</c> 中以
/// <c>async</c> + 可取消的方式实现；本类只保留"读取进程自身信息"这类必然为真的同步事实。
/// 两者此前各有一个 <c>DependencyProbe</c> 类型且字段不一致（少了稳定标识 Id），
/// 是"工作台显示已安装、诊断却跳过"这类矛盾的直接来源，因此已统一到 Domain 层。
/// </para>
/// </remarks>
public sealed class AppEnvironment
{
    private readonly string? _settingsFilePath;

    /// <summary>
    /// 创建环境信息读取器。
    /// </summary>
    /// <param name="settingsFilePath">设置文件路径（用于展示"配置存放位置"）。</param>
    public AppEnvironment(string? settingsFilePath = null)
    {
        _settingsFilePath = settingsFilePath;
    }

    /// <summary>应用版本（取自程序集信息版本）。</summary>
    public string ApplicationVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "未知";

    /// <summary>.NET 运行时描述，例如 ".NET 10.0.13"。</summary>
    public string RuntimeDescription => RuntimeInformation.FrameworkDescription;

    /// <summary>操作系统描述。</summary>
    public string OperatingSystemDescription => RuntimeInformation.OSDescription;

    /// <summary>
    /// Windows 版本的可读名称。
    /// </summary>
    /// <remarks>
    /// Windows 11 与 Windows 10 共享 10.0 主版本号，靠 build 号区分（22000 起为 Windows 11）。
    /// </remarks>
    public string WindowsVersionName
    {
        get
        {
            var version = Environment.OSVersion.Version;

            return version.Build switch
            {
                >= 22000 => $"Windows 11 (build {version.Build})",
                > 0 => $"Windows 10 (build {version.Build})",
                _ => OperatingSystemDescription
            };
        }
    }

    /// <summary>进程架构（x64 / x86 / Arm64）。</summary>
    public string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();

    /// <summary>设置文件完整路径；未知时返回 null。</summary>
    public string? SettingsFilePath => _settingsFilePath;

    /// <summary>
    /// 设置文件所在目录（即本机数据目录）；未知时返回 null。
    /// </summary>
    /// <remarks>
    /// 自检证据、任务历史等落盘位置都以该目录为根，
    /// 与"界面上显示的配置位置"必须是同一个路径 ——
    /// 否则用户按界面提示去找证据文件时会找不到。
    /// </remarks>
    public string? SettingsDirectory =>
        string.IsNullOrWhiteSpace(_settingsFilePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(_settingsFilePath));
}
