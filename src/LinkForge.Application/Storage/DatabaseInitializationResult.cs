namespace LinkForge.Application.Storage;

/// <summary>数据库初始化结果类型。</summary>
public enum DatabaseInitializationOutcome
{
    /// <summary>首次创建。</summary>
    Created = 0,

    /// <summary>结构升级（应用了迁移）。</summary>
    Upgraded = 1,

    /// <summary>已是最新结构，未做改动。</summary>
    AlreadyCurrent = 2,

    /// <summary>原文件损坏，已隔离并重建。</summary>
    RecoveredFromCorruption = 3
}

/// <summary>
/// 存储初始化结果。
/// </summary>
/// <param name="Outcome">本次初始化实际发生了什么。</param>
/// <param name="Location">存储位置。</param>
/// <param name="Note">面向用户的补充说明；无特别情况时为空。</param>
/// <remarks>
/// 把"发生了什么"作为返回值而不是只返回 void：界面必须能如实告诉用户
/// "这是新建的库"还是"原库损坏、已隔离重建"（DEV_STANDARDS §3.4）。
/// 静默重建会让用户以为数据凭空消失。
/// </remarks>
public readonly record struct DatabaseInitializationResult(
    DatabaseInitializationOutcome Outcome,
    string Location,
    string? Note)
{
    /// <summary>一句话说明（供状态栏或诊断信息显示）。</summary>
    public string Description => Outcome switch
    {
        DatabaseInitializationOutcome.Created => "已创建本地数据库。",
        DatabaseInitializationOutcome.Upgraded => "已应用数据库结构升级。",
        DatabaseInitializationOutcome.AlreadyCurrent => "本地数据库已是最新结构。",
        DatabaseInitializationOutcome.RecoveredFromCorruption =>
            $"原数据库文件已损坏，已隔离并重建：{Note}",
        _ => "未知状态。"
    };

    /// <summary>是否属于"需要用户知晓的非正常情况"。</summary>
    public bool RequiresAttention =>
        Outcome == DatabaseInitializationOutcome.RecoveredFromCorruption;
}
