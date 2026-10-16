namespace LinkForge.Domain.Diagnostics;

/// <summary>
/// 一次诊断产出的证据文件描述。
/// </summary>
/// <remarks>
/// 依据 <c>docs/ARCHITECTURE.md</c> §5：证据文件<b>不进数据库</b>，
/// 只记录路径、哈希、大小与 MIME 类型，便于事后核对"报告里的结论对应哪份原始证据"。
///
/// 两条约束：
/// <list type="number">
///   <item>
///     大文件（抓包、视频片段）只记录元数据，不把内容读进内存。
///   </item>
///   <item>
///     证据在<b>写入前</b>已脱敏；<see cref="IsRedacted"/> 记录该事实，
///     供报告与导出决定是否需要二次处理。默认值为 true —— 默认按"已脱敏"处理，
///     忘记标记时会偏保守而不是偏泄漏。
///   </item>
/// </list>
/// </remarks>
public sealed record EvidenceArtifact
{
    /// <summary>文件名（不含目录），例如 <c>environment.txt</c>。</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// 相对路径。
    /// </summary>
    /// <remarks>
    /// 相对证据根目录而不是绝对路径：项目可能被复制到另一台机器或另一个盘符，
    /// 绝对路径会立刻失效，而相对路径仍可解析。
    /// </remarks>
    public required string RelativePath { get; init; }

    /// <summary>内容 SHA-256（小写十六进制）。</summary>
    public required string Sha256 { get; init; }

    /// <summary>字节数。</summary>
    public long SizeBytes { get; init; }

    /// <summary>MIME 类型，例如 <c>text/plain</c>。</summary>
    public string ContentType { get; init; } = "text/plain";

    /// <summary>是否已在写入前完成脱敏。</summary>
    public bool IsRedacted { get; init; } = true;

    /// <summary>证据用途说明（面向用户，一句话）。</summary>
    public string? Description { get; init; }

    /// <summary>可读大小，例如 "1.2 KB"。</summary>
    public string SizeText => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes / (1024.0 * 1024.0):0.#} MB"
    };

    /// <summary>哈希的短形式（前 12 位），用于界面展示。</summary>
    public string ShortHash => Sha256.Length > 12 ? Sha256[..12] : Sha256;
}
