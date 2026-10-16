using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LinkForge.App.Services;

/// <summary>
/// 一个外部依赖的探测结果。
/// </summary>
/// <param name="Name">依赖名称（如 "Npcap"）。</param>
/// <param name="IsAvailable">是否可用。</param>
/// <param name="Detail">可读说明（版本号或"未检测到"的具体依据）。</param>
public readonly record struct DependencyProbe(string Name, bool IsAvailable, string Detail);

/// <summary>
/// 当前运行环境的真实信息（应用版本、运行时、操作系统、外部依赖）。
/// </summary>
/// <remarks>
/// <para>
/// 本服务存在的目的：工作台与"依赖缺失"状态需要真实数据。
/// UI 规范 §4.1 明确禁止"没有真实数据时编造数值"，
/// 因此这里只报告<b>能实际探测到</b>的事实，探测不到就如实说明"未检测到"。
/// </para>
/// <para>
/// 所有探测都是只读且轻量的（读注册表/文件系统、查 PATH），
/// 不启动长任务、不发起网络请求，因此可以在 UI 线程同步调用。
/// 将来若加入耗时探测（如端口扫描），必须改为异步并支持取消。
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
    /// 本机实测为 Windows 11 build 26300。
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
    /// 探测外部依赖的可用性。
    /// </summary>
    /// <returns>各依赖的探测结果；顺序即界面展示顺序。</returns>
    public IReadOnlyList<DependencyProbe> ProbeDependencies() =>
    [
        ProbeNpcap(),
        ProbeExecutable("FFmpeg", "ffmpeg.exe", "--version"),
        ProbeExecutable("TShark", "tshark.exe", "-v")
    ];

    /// <summary>
    /// 探测 Npcap（抓包驱动）。
    /// </summary>
    /// <remarks>
    /// 判定依据是驱动目录下的 <c>wpcap.dll</c> 是否存在 ——
    /// 这是 Npcap 安装后的实际落盘位置。
    /// 不读注册表，因为不同安装方式（静默安装/升级）留下的注册表键不一致，
    /// 而文件是否存在是唯一可靠的判据。
    /// </remarks>
    private static DependencyProbe ProbeNpcap()
    {
        const string name = "Npcap";

        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var candidate = Path.Combine(system32, "Npcap", "wpcap.dll");

            return File.Exists(candidate)
                ? new DependencyProbe(name, true, "已安装（抓包可用）")
                : new DependencyProbe(name, false, "未检测到，抓包相关步骤将不可用");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new DependencyProbe(name, false, "检测失败，无法确认是否可用");
        }
    }

    /// <summary>
    /// 在 PATH 中探测命令行工具。
    /// </summary>
    /// <param name="displayName">展示名称。</param>
    /// <param name="executable">可执行文件名。</param>
    /// <param name="versionArgument">获取版本的参数。</param>
    /// <remarks>
    /// 只读取版本输出，不执行任何用户输入 ——
    /// 符合 ADR-009（外部进程调用一律用参数数组、禁止 shell 拼接）。
    /// 超时 3 秒，避免某个卡住的工具拖住启动。
    /// </remarks>
    private static DependencyProbe ProbeExecutable(string displayName, string executable, string versionArgument)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add(versionArgument);

            if (!process.Start())
            {
                return new DependencyProbe(displayName, false, "未检测到");
            }

            // 只读第一行作为版本标识，避免大输出拖慢启动。
            var firstLine = process.StandardOutput.ReadLine();

            if (!process.WaitForExit(3000))
            {
                TryKill(process);
                return new DependencyProbe(displayName, true, "已检测到（版本查询超时）");
            }

            return new DependencyProbe(
                displayName,
                true,
                string.IsNullOrWhiteSpace(firstLine) ? "已检测到" : firstLine.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 找不到可执行文件时 Process.Start 抛 Win32Exception，
            // 这是"未安装"的正常路径，不是错误。
            return new DependencyProbe(displayName, false, "未检测到");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程可能已自行退出；清理失败不影响探测结果。
        }
    }
}