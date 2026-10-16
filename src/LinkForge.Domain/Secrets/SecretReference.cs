namespace LinkForge.Domain.Secrets;

/// <summary>
/// 秘密存储提供方。
/// </summary>
/// <remarks>依据 ADR-008：秘密不进项目文件，只进 Windows DPAPI 或凭据管理器。</remarks>
public enum SecretProvider
{
    /// <summary>Windows DPAPI（CurrentUser 作用域）。</summary>
    DpapiCurrentUser = 0,

    /// <summary>Windows 凭据管理器。</summary>
    WindowsCredentialManager = 1
}

/// <summary>
/// 对一条秘密的<b>引用</b>（不含明文）。
/// </summary>
/// <param name="Key">引用键，例如 <c>asset.cam-01.password</c>。</param>
/// <param name="Provider">存储提供方。</param>
/// <param name="Kind">秘密种类（密码 / Token / 私钥）。</param>
/// <param name="Description">用途说明（面向用户）。</param>
/// <remarks>
/// <para>
/// 依据 ADR-008 与 PRD §6.3。这是"项目导出默认不含秘密"的实现基础：
/// 项目文件里出现的永远是引用，而不是值。
/// </para>
/// <para>
/// 本类型刻意<b>没有</b> <c>Value</c> 属性，也没有隐式转换：
/// 想拿到明文必须显式调用秘密存储服务，无法顺手 <c>.Value</c> 就泄漏。
/// 自动化测试会断言本类型不含任何承载明文的成员。
/// </para>
/// </remarks>
public readonly record struct SecretReference(
    string Key,
    SecretProvider Provider,
    SecretKind Kind,
    string? Description = null)
{
    /// <summary>是否为有效引用（键非空）。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Key);
}

/// <summary>秘密种类。</summary>
public enum SecretKind
{
    /// <summary>未指定。</summary>
    Unknown = 0,

    /// <summary>密码。</summary>
    Password = 1,

    /// <summary>访问令牌 / API Key。</summary>
    Token = 2,

    /// <summary>私钥。</summary>
    PrivateKey = 3,

    /// <summary>客户端证书口令。</summary>
    CertificatePassphrase = 4
}
