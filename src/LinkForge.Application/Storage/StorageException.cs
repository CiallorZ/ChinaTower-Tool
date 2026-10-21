namespace LinkForge.Application.Storage;

/// <summary>
/// 存储层的可读失败。
/// </summary>
/// <remarks>
/// <para>
/// 存储实现（SQLite、文件）会在遇到"用户能自己解决"的冲突时抛本异常，
/// 例如项目重名、环境名重复。视图模型直接展示 <see cref="Exception.Message"/>，
/// 因此消息必须是面向用户的完整句子，而不是把 <c>SqliteException</c> 的英文原文抛到界面上。
/// </para>
/// <para>
/// 与之相对：磁盘满、无权限这类环境故障<b>不</b>包装成本异常，
/// 它们由上层按"环境错误"呈现（DEV_STANDARDS §3.4 的状态区分）。
/// </para>
/// </remarks>
public sealed class StorageException : Exception
{
    /// <summary>创建可读失败。</summary>
    /// <param name="message">面向用户的说明。</param>
    public StorageException(string message)
        : base(message)
    {
    }

    /// <summary>创建可读失败并保留内部原因。</summary>
    /// <param name="message">面向用户的说明。</param>
    /// <param name="innerException">内部原因。</param>
    public StorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
