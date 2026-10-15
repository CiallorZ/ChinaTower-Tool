using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Presentation;

/// <summary>
/// 状态标签的视觉语义。
/// </summary>
/// <remarks>
/// 依据 UI 规范 §5.4：颜色不得作为唯一表达方式，必须配合文字与图标。
/// 因此每个视觉语义都同时定义：令牌前缀（颜色）、图标字形、以及默认文案来源。
/// </remarks>
public enum LfStatusVisual
{
    /// <summary>成功 / 通过：绿色 + 勾选。</summary>
    Success,

    /// <summary>运行中 / 进行中：蓝色 + 同步箭头。</summary>
    Running,

    /// <summary>警告 / 部分成功：琥珀色 + 警告标记。</summary>
    Warning,

    /// <summary>失败 / 危险：红色 + 叉号。</summary>
    Danger,

    /// <summary>未知 / 未检测：中性色 + 问号。</summary>
    Unknown,

    /// <summary>依赖缺失 / 环境不可用：赭色 + 断开插头（必须区别于"失败"）。</summary>
    DependencyMissing,

    /// <summary>已取消：中性偏冷 + 停止。</summary>
    Cancelled,

    /// <summary>未开始 / 待运行：中性色 + 空心圆。</summary>
    Neutral
}

/// <summary>
/// 状态到视觉语义的映射结果。
/// </summary>
/// <param name="Visual">视觉语义（决定颜色令牌）。</param>
/// <param name="IconGlyph">图标字形（Segoe Fluent Icons / Segoe MDL2 Assets 码位）。</param>
/// <param name="DisplayText">默认中文文案。</param>
/// <param name="AccessibleText">读屏与 ToolTip 使用的完整描述。</param>
public readonly record struct StatusVisualMap(
    LfStatusVisual Visual,
    string IconGlyph,
    string DisplayText,
    string AccessibleText);

/// <summary>
/// 把领域状态映射为 UI 视觉语义。
/// </summary>
/// <remarks>
/// 依据 ADR-016、ADR-019 与 UI 规范 §5.4 / §5.6。
///
/// 这是**唯一**的状态→视觉映射点：所有页面通过它渲染状态标签，
/// 从而保证 "Skipped 不渲染为 Failed"、"Unknown 不渲染为成功"、
/// "依赖缺失不伪装成失败" 等红线在全应用一致。
///
/// 映射为纯函数（不依赖 WPF），因此可穷举测试。
/// </remarks>
public static class StatusPresentation
{
    // Segoe Fluent Icons / Segoe MDL2 Assets 码位。
    private const string GlyphCheckmarkCircle = "\uE73E";      // CheckmarkCircle
    private const string GlyphArrowSync = "\uE895";            // Sync
    private const string GlyphWarning = "\uE7BA";              // Warning
    private const string GlyphErrorCircle = "\uEA39";          // ErrorBadge / 叉号圆
    private const string GlyphQuestion = "\uE9CE";             // Unknown / 问号
    private const string GlyphPlugDisconnected = "\uF384";     // PlugDisconnected
    private const string GlyphStop = "\uE71A";                 // Stop
    private const string GlyphCircleRing = "\uEA3A";           // CircleRing
    private const string GlyphClock = "\uE823";                // Recent / 时钟
    private const string GlyphCancel = "\uE711";               // Cancel
    private const string GlyphInfo = "\uE946";                 // Info

    /// <summary>
    /// 映射步骤层状态。
    /// </summary>
    /// <param name="status">步骤结论。</param>
    /// <returns>视觉语义映射。</returns>
    public static StatusVisualMap ForDiagnosticStatus(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Passed => new(
            LfStatusVisual.Success, GlyphCheckmarkCircle, "通过", "步骤通过"),

        DiagnosticStatus.Failed => new(
            LfStatusVisual.Danger, GlyphErrorCircle, "失败", "步骤失败"),

        DiagnosticStatus.Warning => new(
            LfStatusVisual.Warning, GlyphWarning, "警告", "步骤通过但需关注"),

        // 跳过必须说明原因，文案本身要提示"未执行"而非"失败"。
        DiagnosticStatus.Skipped => new(
            LfStatusVisual.Neutral, GlyphCircleRing, "已跳过", "步骤已跳过，未执行"),

        DiagnosticStatus.Unknown => new(
            LfStatusVisual.Unknown, GlyphQuestion, "未判定", "未能判定结果"),

        DiagnosticStatus.Cancelled => new(
            LfStatusVisual.Cancelled, GlyphCancel, "已取消", "步骤被取消"),

        DiagnosticStatus.TimedOut => new(
            LfStatusVisual.Danger, GlyphClock, "超时", "步骤超时"),

        DiagnosticStatus.Running => new(
            LfStatusVisual.Running, GlyphArrowSync, "执行中", "步骤正在执行"),

        DiagnosticStatus.NotRun => new(
            LfStatusVisual.Neutral, GlyphCircleRing, "未开始", "步骤尚未开始"),

        _ => new(
            LfStatusVisual.Unknown, GlyphQuestion, "未知", "未知状态")
    };

    /// <summary>
    /// 映射任务层状态。
    /// </summary>
    /// <param name="state">任务状态。</param>
    /// <returns>视觉语义映射。</returns>
    public static StatusVisualMap ForTaskRunState(TaskRunState state) => state switch
    {
        TaskRunState.Pending => new(
            LfStatusVisual.Neutral, GlyphCircleRing, "待运行", "任务尚未开始"),

        TaskRunState.Preparing => new(
            LfStatusVisual.Running, GlyphArrowSync, "准备中", "任务正在准备"),

        TaskRunState.Running => new(
            LfStatusVisual.Running, GlyphArrowSync, "执行中", "任务正在执行"),

        TaskRunState.Completed => new(
            LfStatusVisual.Success, GlyphCheckmarkCircle, "已完成", "任务全部通过"),

        // "部分成功"必须使用警告色而非失败色：它确实完成了一部分工作。
        TaskRunState.PartiallySucceeded => new(
            LfStatusVisual.Warning, GlyphWarning, "部分成功", "任务部分成功，部分步骤未通过或未执行"),

        TaskRunState.Failed => new(
            LfStatusVisual.Danger, GlyphErrorCircle, "失败", "任务失败"),

        TaskRunState.Cancelled => new(
            LfStatusVisual.Cancelled, GlyphStop, "已取消", "任务被用户取消"),

        // 不得渲染为"完成"或"失败"：没有任何确定性结论。
        TaskRunState.Inconclusive => new(
            LfStatusVisual.Unknown, GlyphQuestion, "无有效结论", "任务未产生确定性结论，请查看步骤明细"),

        _ => new(
            LfStatusVisual.Unknown, GlyphQuestion, "未知", "未知状态")
    };

    /// <summary>
    /// 依赖缺失 / 当前环境不可用。
    /// </summary>
    /// <param name="missingDependency">缺失项名称（如 "Npcap"、"海康 HCNetSDK"）。</param>
    /// <returns>视觉语义映射。</returns>
    public static StatusVisualMap ForMissingDependency(string? missingDependency = null)
    {
        var name = string.IsNullOrWhiteSpace(missingDependency) ? "所需组件" : missingDependency.Trim();

        return new StatusVisualMap(
            LfStatusVisual.DependencyMissing,
            GlyphPlugDisconnected,
            "依赖缺失",
            $"缺少 {name}，该功能当前不可用");
    }

    /// <summary>
    /// 信息提示（非状态，用于中性说明）。
    /// </summary>
    public static StatusVisualMap Informational(string text) =>
        new(LfStatusVisual.Neutral, GlyphInfo, text, text);

    /// <summary>
    /// 视觉语义对应的颜色令牌前缀，例如 <c>Success</c> 对应
    /// <c>LfBrushSuccess</c> 与 <c>LfBrushSuccessSubtle</c>。
    /// </summary>
    /// <param name="visual">视觉语义。</param>
    /// <returns>令牌名后缀。</returns>
    public static string TokenSuffix(LfStatusVisual visual) => visual switch
    {
        LfStatusVisual.Success => "Success",
        LfStatusVisual.Running => "Running",
        LfStatusVisual.Warning => "Warning",
        LfStatusVisual.Danger => "Danger",
        LfStatusVisual.Unknown => "Unknown",
        LfStatusVisual.DependencyMissing => "DependencyMissing",
        LfStatusVisual.Cancelled => "Unknown",
        LfStatusVisual.Neutral => "Unknown",
        _ => "Unknown"
    };
}
