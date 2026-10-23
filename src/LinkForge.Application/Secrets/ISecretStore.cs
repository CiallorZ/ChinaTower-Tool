using LinkForge.Domain.Secrets;

namespace LinkForge.Application.Secrets;

/// <summary>
/// 一条秘密引用的存储状态。
/// </summary>
public enum SecretEntryState
{
    /// <summary>尚未保存。</summary>
    Missing = 0,

    /// <summary>已保存且可被当前 Windows 用户解密。</summary>
    Available = 1,

    /// <summary>文件存在，但内容损坏、权限不足或不属于当前 Windows 用户。</summary>
    Unreadable = 2
}

/// <summary>
/// 秘密条目的安全状态描述；不包含秘密值。
/// </summary>
public sealed record SecretEntryStatus(
    SecretEntryState State,
    DateTimeOffset? UpdatedAtUtc,
    string Message)
{
    /// <summary>未保存。</summary>
    public static SecretEntryStatus Missing { get; } = new(
        SecretEntryState.Missing,
        null,
        "未保存到当前 Windows 用户的凭据库。");

    /// <summary>已保存且可读取。</summary>
    public static SecretEntryStatus Available(DateTimeOffset updatedAtUtc) => new(
        SecretEntryState.Available,
        updatedAtUtc,
        "已保存到当前 Windows 用户的凭据库。");
}

/// <summary>
/// 凭据库操作失败。消息必须可安全显示，不得包含秘密明文。
/// </summary>
public sealed class SecretStoreException : Exception
{
    /// <summary>创建凭据库异常。</summary>
    public SecretStoreException(string message)
        : base(message)
    {
    }

    /// <summary>创建带内部异常的凭据库异常。</summary>
    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 秘密存储契约。
/// </summary>
/// <remarks>
/// 业务层只持有 <see cref="SecretReference"/>。只有真正需要发起认证的组件
/// 才可显式调用 <see cref="ReadAsync"/>；复制、历史、日志、导出和报告不得读取秘密值。
/// </remarks>
public interface ISecretStore
{
    /// <summary>读取条目的安全状态，不返回秘密值。</summary>
    Task<SecretEntryStatus> GetStatusAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>保存或覆盖一条秘密。</summary>
    Task SaveAsync(
        SecretReference reference,
        string secretValue,
        CancellationToken cancellationToken = default);

    /// <summary>读取秘密值；不存在时返回 null。</summary>
    Task<string?> ReadAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>删除秘密；原本不存在时返回 false。</summary>
    Task<bool> DeleteAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);
}
