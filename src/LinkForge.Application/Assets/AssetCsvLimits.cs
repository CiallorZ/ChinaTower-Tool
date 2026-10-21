namespace LinkForge.Application.Assets;

/// <summary>
/// 资产字段的长度上限（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意集中成一处：CSV 解析、数据库列定义与界面校验三处必须一致。
/// 若各写一份，典型后果是"CSV 收下了 500 字符的名称，写库时才抛异常"，
/// 而那时已经没法告诉用户是哪一行的问题。
/// </para>
/// <para>
/// 这些是<b>上限</b>而不是"建议值"：主机名与厂商名按真实设备命名习惯留了充足空间，
/// 备注最宽松。
/// </para>
/// </remarks>
public static class AssetCsvLimits
{
    /// <summary>资产名称上限。</summary>
    public const int NameLength = 200;

    /// <summary>主机上限（足够容纳 IPv6 字面量与长域名）。</summary>
    public const int HostLength = 256;

    /// <summary>厂商上限。</summary>
    public const int VendorLength = 128;

    /// <summary>备注上限。</summary>
    public const int NoteLength = 1024;

    /// <summary>单条标签上限。</summary>
    public const int TagLength = 64;

    /// <summary>标签个数上限。</summary>
    public const int TagCount = 16;

    /// <summary>秘密引用键上限。</summary>
    public const int CredentialKeyLength = 128;

    /// <summary>资产标识上限。</summary>
    /// <remarks>
    /// 与数据库主键列长度（64）保持一致：
    /// 导入时若允许更长的标识，保存时会被数据库截断或报错，
    /// 而用户看到的却是"导入成功"。
    /// </remarks>
    public const int AssetIdLength = 64;

    /// <summary>标签 JSON 列的数据库长度上限。</summary>
    public const int TagsJsonLength = 2048;

    /// <summary>环境变量 JSON 列的数据库长度上限。</summary>
    public const int VariablesJsonLength = 8192;
}
