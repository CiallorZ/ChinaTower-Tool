using System.Text;
using LinkForge.Application.Diagnostics;

namespace LinkForge.Reporting.Reports;

/// <summary>
/// 报告生成的失败原因分类。
/// </summary>
/// <remarks>
/// 分类存在的意义：调用方要能把"扫描不通过"与"磁盘写不进去"区分开。
/// 前者需要用户清理数据，后者需要用户换一个目录或修权限 ——
/// 都用同一句"生成失败"会让用户无从下手。
/// </remarks>
public enum ReportFailureKind
{
    /// <summary>生成前的敏感信息扫描未通过。</summary>
    SensitiveDataDetected,

    /// <summary>目标目录不可写或写入失败。</summary>
    WriteFailed,

    /// <summary>被取消。</summary>
    Cancelled
}

/// <summary>
/// 报告生成结果。
/// </summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="FilePath">生成的文件路径；失败时为 null。</param>
/// <param name="Scan">敏感信息扫描结果。</param>
/// <param name="FailureKind">失败类别。</param>
/// <param name="Message">面向用户的说明。</param>
public sealed record ReportGenerationResult(
    bool Succeeded,
    string? FilePath,
    SensitiveScanResult Scan,
    ReportFailureKind? FailureKind,
    string Message)
{
    /// <summary>构造成功结果。</summary>
    public static ReportGenerationResult Success(string filePath, SensitiveScanResult scan) =>
        new(true, filePath, scan, null, $"报告已生成：{filePath}");

    /// <summary>构造失败结果。</summary>
    public static ReportGenerationResult Failure(
        ReportFailureKind kind,
        string message,
        SensitiveScanResult scan) =>
        new(false, null, scan, kind, message);
}

/// <summary>
/// 报告生成契约。
/// </summary>
public interface IReportGenerator
{
    /// <summary>
    /// 生成离线 HTML 报告。
    /// </summary>
    /// <param name="document">报告内容。</param>
    /// <param name="destinationPath">目标文件路径（.html）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>生成结果；扫描不通过时不写文件。</returns>
    Task<ReportGenerationResult> GenerateAsync(
        ReportDocument document,
        string destinationPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 仅执行生成前的敏感信息扫描（供预览校验使用）。</summary>
    /// <param name="document">报告内容。</param>
    /// <returns>扫描结果。</returns>
    SensitiveScanResult Scan(ReportDocument document);
}

/// <summary>
/// 离线 HTML 报告生成实现。
/// </summary>
/// <remarks>
/// <para>
/// 生成流程固定为：扫描 → 渲染 → 原子写入。
/// 扫描不通过时<b>绝不写文件</b>：一份"生成了但含明文口令"的报告
/// 比没有报告更危险，因为用户会把它转发出去。
/// </para>
/// <para>
/// 写入使用临时文件 + 替换，避免中途失败留下半个 HTML ——
/// 半个 HTML 在某些浏览器里能打开，用户会以为报告完整。
/// </para>
/// <para>
/// 所有 IO 都是异步且可取消；调用方在 UI 线程上 await 不会阻塞界面。
/// </para>
/// </remarks>
public sealed class ReportGenerator : IReportGenerator
{
    private readonly ITextRedactor _redactor;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 创建报告生成器。
    /// </summary>
    /// <param name="redactor">文本脱敏器（用于渲染前的兜底脱敏）。</param>
    /// <param name="timeProvider">时间源；null 时使用系统时钟。</param>
    public ReportGenerator(ITextRedactor redactor, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(redactor);

        _redactor = redactor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public SensitiveScanResult Scan(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return SensitiveDataScanner.Scan(document.EnumerateTexts());
    }

    /// <inheritdoc />
    public async Task<ReportGenerationResult> GenerateAsync(
        ReportDocument document,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (cancellationToken.IsCancellationRequested)
        {
            // 取消是用户主动行为，用结果表达而不是抛异常：
            // 抛异常会让调用方必须为"用户点了取消"写一个 catch，
            // 而 catch 又容易顺手把真正的写入失败一起吞掉。
            return ReportGenerationResult.Failure(
                ReportFailureKind.Cancelled,
                "已取消生成报告。",
                new SensitiveScanResult([]));
        }

        var scan = Scan(document);

        if (!scan.IsClean)
        {
            return ReportGenerationResult.Failure(
                ReportFailureKind.SensitiveDataDetected,
                $"报告未生成：{scan.Describe()}。请先移除或脱敏相关内容后重试。",
                scan);
        }

        var html = ReportHtmlRenderer.Render(document);

        try
        {
            await WriteAtomicallyAsync(destinationPath, html, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ReportGenerationResult.Failure(
                ReportFailureKind.Cancelled,
                "已取消生成报告。",
                scan);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            // ArgumentException 覆盖非法字符、路径过长等"用户给了一个不合法路径"的情形：
            // 这属于可预期输入错误，必须变成可读反馈，而不是让异常冒到 UI 线程。
            return ReportGenerationResult.Failure(
                ReportFailureKind.WriteFailed,
                $"写入报告失败：{ex.GetType().Name} — {ex.Message}。请检查目标目录是否存在且可写。",
                scan);
        }

        return ReportGenerationResult.Success(destinationPath, scan);
    }

    /// <summary>
    /// 建议的报告文件名（含时间戳，避免覆盖上一份）。
    /// </summary>
    /// <param name="adapterId">适配器标识，例如 <c>network.diagnostics</c>。</param>
    public string SuggestFileName(string adapterId)
    {
        var stamp = _timeProvider.GetLocalNow().ToString(
            "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture);

        var safe = new string(
            (adapterId ?? "report")
                .Select(static c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '-')
                .ToArray());

        return $"linkforge-report-{safe}-{stamp}.html";
    }

    /// <summary>
    /// 用脱敏器兜底处理报告文本。
    /// </summary>
    /// <remarks>
    /// 保留此方法是为了让调用方能对<b>尚未</b>经过编排器的文本做一次兜底，
    /// 例如项目名称、备注等自由输入字段。
    /// </remarks>
    public string Redact(string? text) => _redactor.Redact(text) ?? string.Empty;

    private static async Task WriteAtomicallyAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = fullPath + ".tmp";

        // UTF-8 无 BOM：带 BOM 会让某些校验脚本把 <!DOCTYPE> 之前的内容当作正文。
        await File.WriteAllTextAsync(
            temporaryPath,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        try
        {
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理临时文件失败不应覆盖真正的写入异常。
        }
    }
}
