using System.Diagnostics;
using System.IO;
using LinkForge.Domain.Environment;

namespace LinkForge.Application.Environment;

/// <summary>
/// 外部依赖探测（真实探测，异步且可取消）。
/// </summary>
/// <remarks>
/// <para>
/// 依据 UI 规范 §4.1 与 DEV_STANDARDS §2：工作台要显示真实依赖状态，
/// 但探测过程会启动外部进程（<c>ffmpeg --version</c> 之类），属于阻塞 I/O，
/// <b>不得</b>在 UI 线程同步执行 —— 一个卡住的外部工具会直接冻住界面。
/// </para>
/// <para>
/// 因此本类的所有方法都是 <c>async</c> 并接受 <see cref="CancellationToken"/>；
/// 调用方（工作台 ViewModel）负责把结果绑回 UI。
/// </para>
/// <para>
/// 探测口径保持保守：<b>只有确认可用才报可用</b>。
/// 探测失败（权限、超时、异常）一律报不可用并说明原因，
/// 不写“可能可用”这种无法据以行动的结论。
/// </para>
/// </remarks>
public sealed class DependencyCatalog
{
    /// <summary>
    /// 已知依赖的标识常量。
    /// </summary>
    /// <remarks>
    /// 步骤计划里用字符串声明依赖，如果各处手写字符串，
    /// 改名时必然出现“计划说 npcap、探测报 npcap-driver”这类对不上的缺陷。
    /// </remarks>
    public static class Ids
    {
        /// <summary>Npcap 抓包驱动。</summary>
        public const string Npcap = "npcap";

        /// <summary>FFmpeg 媒体工具。</summary>
        public const string Ffmpeg = "ffmpeg";

        /// <summary>TShark 命令行抓包分析。</summary>
        public const string TShark = "tshark";
    }

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 创建依赖探测目录。
    /// </summary>
    /// <param name="timeProvider">时间源；null 时使用系统时钟。</param>
    public DependencyCatalog(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 探测全部已知依赖。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>各依赖的探测结果；顺序即界面展示顺序。</returns>
    /// <remarks>
    /// 串行探测而不是并行：单个探测最多几秒，且并行启动多个进程
    /// 在现场低配机器上会互相拖慢，反而更难取消。
    /// </remarks>
    public async Task<IReadOnlyList<DependencyProbe>> ProbeAllAsync(CancellationToken cancellationToken = default)
    {
        var ffmpeg = await ProbeExecutableAsync(
            Ids.Ffmpeg, "FFmpeg", "ffmpeg.exe", "--version", cancellationToken).ConfigureAwait(false);

        var tshark = await ProbeExecutableAsync(
            Ids.TShark, "TShark", "tshark.exe", "-v", cancellationToken).ConfigureAwait(false);

        return [ProbeNpcap(), ffmpeg, tshark];
    }

    /// <summary>
    /// 探测 Npcap（抓包驱动）。
    /// </summary>
    /// <returns>探测结果。</returns>
    /// <remarks>
    /// 判定依据是驱动目录下的 <c>wpcap.dll</c> 是否存在 —— 这是 Npcap 安装后的实际落盘位置。
    /// 不读注册表：不同安装方式（静默安装 / 升级）留下的注册表键不一致，
    /// 而文件是否存在是唯一可靠的判据。
    /// </remarks>
    public static DependencyProbe ProbeNpcap()
    {
        const string name = "Npcap";

        try
        {
            var system32 = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
            var candidate = Path.Combine(system32, "Npcap", "wpcap.dll");

            return File.Exists(candidate)
                ? new DependencyProbe(Ids.Npcap, name, true, "已安装（抓包可用）")
                : new DependencyProbe(Ids.Npcap, name, false, "未检测到，抓包相关步骤将不可用");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new DependencyProbe(Ids.Npcap, name, false, "检测失败，无法确认是否可用");
        }
    }

    /// <summary>
    /// 在 PATH 中探测命令行工具。
    /// </summary>
    /// <param name="id">依赖标识。</param>
    /// <param name="displayName">展示名称。</param>
    /// <param name="executable">可执行文件名。</param>
    /// <param name="versionArgument">获取版本的参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结果。</returns>
    /// <remarks>
    /// <para>
    /// 只读取版本输出，不执行任何用户输入 —— 符合 ADR-009
    /// （外部进程调用一律用参数数组、禁止 shell 拼接）。
    /// </para>
    /// <para>
    /// 超时 3 秒且可取消：某个卡住的工具不应拖住启动，
    /// 用户点了取消也不该继续等它。
    /// </para>
    /// </remarks>
    private async Task<DependencyProbe> ProbeExecutableAsync(
        string id,
        string displayName,
        string executable,
        string versionArgument,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

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
                return new DependencyProbe(id, displayName, false, "未检测到");
            }

            // 只读第一行作为版本标识，避免大输出拖慢启动。
            var firstLine = await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            return new DependencyProbe(
                id,
                displayName,
                true,
                string.IsNullOrWhiteSpace(firstLine) ? "已检测到" : firstLine.Trim());
        }
        catch (OperationCanceledException)
        {
            // 两种取消要分开说：用户取消（整体放弃）与探测超时（工具卡住）。
            // 超时仍报“已检测到”是刻意的：能启动说明工具存在，
            // 只是版本号拿不到，这比报“未安装”更接近事实。
            return cancellationToken.IsCancellationRequested
                ? new DependencyProbe(id, displayName, false, "检测被取消")
                : new DependencyProbe(id, displayName, true, "已检测到（版本查询超时）");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            // 找不到可执行文件时 Process.Start 抛 Win32Exception，
            // 这是“未安装”的正常路径，不是错误。
            return new DependencyProbe(id, displayName, false, "未检测到");
        }
    }
}