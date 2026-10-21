namespace LinkForge.Application.Assets;

/// <summary>
/// 资产标签的文本形态（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 界面用"分号分隔的一行文本"编辑标签，CSV 也用同一形态，因此解析规则必须只有一处。
/// 两个入口各写一份的典型后果是：界面里用逗号分隔的标签在 CSV 里被当成两个字段。
/// </para>
/// <para>
/// 接受 <c>;</c>、<c>|</c>、<c>、</c> 与 <c>,</c> 四种分隔符：现场表格里这四种写法都常见，
/// 而标签本身极少包含它们。
/// </para>
/// </remarks>
public static class AssetTagText
{
    /// <summary>解析标签文本：切分、去空白、去空项、按序去重（忽略大小写）。</summary>
    /// <param name="raw">原始文本。</param>
    /// <returns>归一化后的标签；无内容时返回空列表。</returns>
    public static IReadOnlyList<string> Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var separators = new[] { ';', '|', '、', ',' };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();

        foreach (var part in raw.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = part.Trim();

            if (tag.Length > 0 && seen.Add(tag))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    /// <summary>把标签渲染成可编辑文本。</summary>
    /// <param name="tags">标签。</param>
    /// <returns>分号分隔的文本。</returns>
    public static string Format(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        return string.Join("; ", tags);
    }
}
