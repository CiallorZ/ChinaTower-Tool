namespace LinkForge.Domain.Diagnostics;

/// <summary>
/// 一条结构化诊断事实（用于结果表格，而不是把数据塞进日志）。
/// </summary>
/// <param name="Label">事实名称，例如“第 1 跳”“解析地址”。</param>
/// <param name="Value">已脱敏的事实取值。</param>
/// <param name="Status">该事实自身的结论；纯事实项为 null。</param>
/// <remarks>
/// <para>
/// 原始输出适合排障，但不适合作为结论：用户需要先看到“哪一项成功/失败/未知”，
/// 再按需展开日志。本类型就是“结论先于原始日志”的结构化载体。
/// </para>
/// <para>
/// <see cref="Status"/> 为 null 表示这是纯事实（如目标地址），
/// 不得由 UI 擅自渲染成“待运行”或“通过”。
/// </para>
/// </remarks>
public sealed record DiagnosticFact(
    string Label,
    string Value,
    DiagnosticStatus? Status = null);
