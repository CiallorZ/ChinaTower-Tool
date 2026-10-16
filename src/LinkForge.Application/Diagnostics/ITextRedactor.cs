namespace LinkForge.Application.Diagnostics;

/// <summary>
/// 文本脱敏契约（ADR-004、DEV_STANDARDS §4）。
/// </summary>
/// <remarks>
/// <para>
/// 契约放在 Application 而不是直接调用 App 层的 <c>SecretRedactor</c>：
/// 依赖方向固定为 <c>App → Application</c>，Application 不得引用 App。
/// 由 App 在组合根注入唯一实现，从而保证“UI、磁盘、报告共用同一套正则”这条红线
/// 在编译期就成立，而不是靠约定。
/// </para>
/// <para>
/// 默认实现 <see cref="PassThroughTextRedactor"/> <b>不做任何替换</b>，
/// 仅用于测试与设计时。生产组合根必须注入真实实现，否则任务结论会带明文。
/// 因此 <see cref="DiagnosticTaskRunner"/> 不接受 null：
/// 忘记注入会在启动时立刻暴露，而不是在上线后泄漏。
/// </para>
/// </remarks>
public interface ITextRedactor
{
    /// <summary>
    /// 对文本执行脱敏。
    /// </summary>
    /// <param name="input">原始文本；null/空时原样返回。</param>
    /// <returns>脱敏后的文本。</returns>
    string? Redact(string? input);
}

/// <summary>
/// 不做替换的透传实现，仅用于测试与设计时。
/// </summary>
/// <remarks>
/// 命名刻意直白（PassThrough 而不是 NoOpRedactor）：
/// 在代码评审里看到它就该立刻意识到“这里没有脱敏”。
/// </remarks>
public sealed class PassThroughTextRedactor : ITextRedactor
{
    /// <inheritdoc />
    public string? Redact(string? input) => input;
}