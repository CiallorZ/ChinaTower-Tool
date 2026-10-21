using System.IO;
using LinkForge.Application.Assets;
using LinkForge.Domain.Assets;

namespace LinkForge.App.Services;

/// <summary>
/// 资产 CSV 的导出流程（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 导出只写<b>引用键</b>，不写任何秘密值：资产模型里本来就没有秘密字段，
/// 因此这一层不需要"过滤密码"，只需要保证不把别的东西塞进去。
/// </para>
/// <para>
/// 导出范围刻意限定为"当前筛选后的可见资产"：
/// 用户在界面上筛选出 3 台设备再导出，得到的就是这 3 台，
/// 而不是整个项目 —— 后者会让"我导出了什么"变得无法回答。
/// </para>
/// </remarks>
public sealed class AssetCsvExportService
{
    /// <summary>
    /// 把资产渲染为 CSV 字节。
    /// </summary>
    /// <param name="assets">资产集合。</param>
    /// <returns>UTF-8 带 BOM 的 CSV 字节。</returns>
    public byte[] Build(IReadOnlyList<Asset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        return AssetCsvCodec.Write(assets);
    }

    /// <summary>
    /// 生成默认导出文件名。
    /// </summary>
    /// <param name="projectName">项目名称。</param>
    /// <param name="timestamp">时间戳。</param>
    /// <returns>文件名。</returns>
    /// <remarks>
    /// 项目名可能被用户写成含路径分隔符的文本，直接拼进文件名会写到别处去；
    /// 因此替换掉文件名里的非法字符。
    /// </remarks>
    public string BuildFileName(string? projectName, DateTimeOffset timestamp)
    {
        var safeName = SanitizeFileName(projectName);
        var stamp = timestamp.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        return string.IsNullOrWhiteSpace(safeName)
            ? $"linkforge-assets-{stamp}.csv"
            : $"linkforge-assets-{safeName}-{stamp}.csv";
    }

    /// <summary>
    /// 去掉文件名中的非法字符。
    /// </summary>
    /// <param name="value">原始文本。</param>
    /// <returns>可用于文件名的文本。</returns>
    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var ch in value.Trim())
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}
