namespace LinkForge.Application.Assets;

/// <summary>
/// 资产凭据引用键的校验与归一化（LF-004，ADR-008）。
/// </summary>
/// <remarks>
/// <para>
/// 资产只保存秘密的<b>引用键</b>（如 <c>asset.cam-01.password</c>），
/// 不保存密码本身。引用键是“去哪里取密码”的指针，因此它本身不是秘密。
/// </para>
/// <para>
/// 键的字符集刻意收紧为<b>白名单</b>：字母、数字与 <c>.</c> <c>_</c> <c>-</c> <c>:</c>。
/// 真实密码几乎必然带有符号或空格，而引用键不需要它们 ——
/// 这样“用户把密码直接贴进凭据栏”会在保存前被拦下，
/// 而不是被当成引用键存进数据库、再随 CSV 导出出去。
/// </para>
/// <para>
/// 校验逻辑集中在此处，CSV 导入与界面表单共用同一份实现：
/// 两处各写一份的典型后果是“导入拦住了、界面没拦住”。
/// </para>
/// </remarks>
public static class AssetCredentialReference
{
    /// <summary>引用键长度上限。</summary>
    public const int MaxKeyLength = AssetCsvLimits.CredentialKeyLength;

    /// <summary>
    /// 判断文本是否是合法的秘密引用键。
    /// </summary>
    /// <param name="key">待校验文本；null 或空白视为“未配置凭据”，合法。</param>
    /// <returns>是否合法。</returns>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            // 空 = 不配置凭据，不是错误。
            return true;
        }

        var trimmed = key.Trim();

        if (trimmed.Length > MaxKeyLength)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// 归一化引用键：去首尾空白；空白视为未配置，返回 null。
    /// </summary>
    /// <param name="key">原始文本。</param>
    /// <returns>归一化后的键；未配置时返回 null。</returns>
    public static string? Normalize(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    /// <summary>
    /// 用户可读的拒绝理由。
    /// </summary>
    /// <returns>说明文本。</returns>
    public static string InvalidReason() =>
        $"凭据只能填写引用键（最长 {MaxKeyLength} 个字符，仅限字母、数字与 . _ - :），"
        + "例如 asset.cam-01.password。请勿直接粘贴密码；实际密码应存放在凭据存储中。";
}
