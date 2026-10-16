namespace LinkForge.Domain.Environment;

/// <summary>
/// 一个外部依赖的真实探测结果。
/// </summary>
/// <param name="Id">稳定标识（如 <c>npcap</c>）；用于声明步骤依赖，不随文案变化。</param>
/// <param name="Name">展示名称（如 "Npcap"）。</param>
/// <param name="IsAvailable">是否可用。</param>
/// <param name="Detail">可读说明（版本号，或"未检测到"的具体依据）。</param>
/// <remarks>
/// <para>
/// 放在 Domain 而不是 UI 层：同一个探测结果被三处消费 ——
/// 工作台的"依赖状态"、诊断步骤的"依赖跳过"判定、证据文件的内容。
/// 若各层各建一个类型，就会出现"工作台显示已安装、诊断却跳过"的矛盾。
/// </para>
/// <para>
/// 探测本身（读文件、查 PATH、跑 <c>--version</c>）属于 I/O，在
/// <c>LinkForge.Application.Diagnostics.DependencyCatalog</c> 中实现；
/// 本类型只是事实的载体。
/// </para>
/// </remarks>
public readonly record struct DependencyProbe(string Id, string Name, bool IsAvailable, string Detail)
{
    /// <summary>界面用的一句话状态，例如 "已安装（抓包可用）"。</summary>
    public string StatusText => IsAvailable ? Detail : Detail;
}
