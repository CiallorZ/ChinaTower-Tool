using LinkForge.Application.Diagnostics;

namespace LinkForge.App.Presentation;

/// <summary>
/// 把 Application 层的脱敏契约接到 App 层的唯一实现 <see cref="SecretRedactor"/>。
/// </summary>
/// <remarks>
/// <para>
/// 这个桥接类存在的唯一理由：<c>LinkForge.Application</c> 不得引用
/// <c>LinkForge.App</c>（依赖方向固定为 App → Application），
/// 因此它只能定义 <see cref="ITextRedactor"/> 接口，实现必须由组合根注入。
/// </para>
/// <para>
/// 桥接而不是另写一套正则：UI、任务结论、历史文件、证据文件必须共用同一套规则，
/// 否则会出现"界面脱敏了但历史里还是明文"这种半截脱敏 —— 那比不脱敏更危险，
/// 因为它会让人误以为已经安全。
/// </para>
/// </remarks>
public sealed class SecretTextRedactor : ITextRedactor
{
    /// <inheritdoc />
    public string? Redact(string? input) => SecretRedactor.Redact(input);
}