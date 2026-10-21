using System.Globalization;
using System.Net;
using System.Text;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.Reporting.Reports;

/// <summary>
/// 把 <see cref="ReportDocument"/> 渲染为自包含的离线 HTML。
/// </summary>
/// <remarks>
/// <para>
/// <b>不使用模板引擎</b>：本文件是唯一的模板，所有外部数据都必须经过
/// <see cref="Encode"/>。这样"是否转义"只取决于一处代码，而不是某个模板文件里
/// 某个 <c>{{{ }}}</c> 与 <c>{{ }}</c> 的区别 —— 后者正是注入漏洞最常见的来源。
/// </para>
/// <para>
/// 输出不含任何 <c>script</c>、外部样式表、字体链接或远程图片：
/// 报告要能在断网、离线的现场机器上直接双击打开。
/// </para>
/// <para>
/// 报告区分三类内容，且在版式上就区分开：
/// 观测事实（步骤结论与结构化事实）、推断建议（下一步建议）、
/// 以及数据范围声明（本次执行了什么、没执行什么）。
/// 把三者混在一张表里，读者会把建议当成事实。
/// </para>
/// </remarks>
public static class ReportHtmlRenderer
{
    /// <summary>报告模板版本（报告格式变化时递增）。</summary>
    public const string TemplateVersion = "1.0";

    /// <summary>
    /// 渲染报告。
    /// </summary>
    /// <param name="document">报告内容。</param>
    /// <returns>完整 HTML 文档。</returns>
    public static string Render(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var run = document.Run;
        var builder = new StringBuilder(16 * 1024);

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-CN\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<!-- 由 LinkForge 生成；模板版本 ")
            .Append(Encode(TemplateVersion))
            .AppendLine(" -->");
        builder.Append("<title>")
            .Append(Encode(document.Title))
            .AppendLine("</title>");
        builder.AppendLine(Styles);
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<main class=\"report\">");

        AppendHeader(builder, document);
        AppendSummary(builder, document);
        AppendEnvironment(builder, document);
        AppendSteps(builder, document);
        AppendAdvice(builder, document);
        AppendScope(builder, document);
        AppendFooter(builder, document);

        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private static void AppendHeader(StringBuilder builder, ReportDocument document)
    {
        var metadata = document.Metadata;

        builder.AppendLine("<header class=\"report-header\">");
        builder.Append("<div class=\"brand\">")
            .Append(Encode(metadata.ProductName))
            .AppendLine("</div>");
        builder.Append("<h1>")
            .Append(Encode(document.Title))
            .AppendLine("</h1>");

        builder.Append("<div class=\"state ")
            .Append(StateClass(document.Run.State))
            .Append("\">")
            .Append(Encode(document.StateText))
            .AppendLine("</div>");
        builder.AppendLine("</header>");

        builder.AppendLine("<section class=\"meta-grid\">");
        AppendMeta(builder, "项目", metadata.ProjectName ?? "未指定项目");
        AppendMeta(builder, "目标", document.Run.TargetLabel);
        AppendMeta(builder, "任务类型", document.Run.DisplayName);
        AppendMeta(builder, "开始时间", document.Run.StartedAtText);
        AppendMeta(builder, "结束时间", FormatLocal(document.Run.EndedAt));
        AppendMeta(builder, "耗时", document.Run.DurationText);
        AppendMeta(builder, "任务层状态", document.StateText);
        AppendMeta(builder, "步骤统计", document.Run.StepSummaryText);
        builder.AppendLine("</section>");
    }

    private static void AppendMeta(StringBuilder builder, string label, string value)
    {
        builder.Append("<div class=\"meta-item\"><span class=\"meta-label\">")
            .Append(Encode(label))
            .Append("</span><span class=\"meta-value\">")
            .Append(Encode(value))
            .AppendLine("</span></div>");
    }

    private static void AppendSummary(StringBuilder builder, ReportDocument document)
    {
        var run = document.Run;

        builder.AppendLine("<section class=\"block\">");
        builder.AppendLine("<h2>结果概览</h2>");
        builder.AppendLine("<div class=\"counters\">");
        AppendCounter(builder, "通过", run.PassedCount, "ok");
        AppendCounter(builder, "警告", run.WarningCount, "warn");
        AppendCounter(builder, "失败", run.FailedCount, "bad");
        AppendCounter(builder, "跳过", run.SkippedCount, "skip");
        AppendCounter(builder, "未判定", run.InconclusiveCount, "unknown");
        builder.AppendLine("</div>");

        if (run.WasCancelled)
        {
            builder.AppendLine(
                "<p class=\"notice notice-warn\">本次任务被用户取消。未执行或已中止的步骤记为其他状态，"
                + "不代表该项检查未通过。</p>");
        }

        if (document.IsIncompleteState)
        {
            builder.AppendLine(
                "<p class=\"notice notice-warn\">本次结论不完整：存在未通过、未执行或未能判定的步骤。"
                + "请结合下方明细判断可用范围，不要将本报告单独作为验收依据。</p>");
        }

        builder.AppendLine("</section>");
    }

    private static void AppendCounter(StringBuilder builder, string label, int count, string kind)
    {
        var muted = count == 0 ? " muted" : string.Empty;

        builder.Append("<div class=\"counter")
            .Append(muted)
            .Append("\"><span class=\"counter-kind ")
            .Append(kind)
            .Append("\"></span><span class=\"counter-count\">")
            .Append(count.ToString(CultureInfo.InvariantCulture))
            .Append("</span><span class=\"counter-label\">")
            .Append(Encode(label))
            .AppendLine("</span></div>");
    }

    private static void AppendEnvironment(StringBuilder builder, ReportDocument document)
    {
        var facts = document.Metadata.EnvironmentFacts;

        builder.AppendLine("<section class=\"block\">");
        builder.AppendLine("<h2>生成环境</h2>");

        if (facts.Count == 0)
        {
            builder.AppendLine("<p class=\"muted-text\">未采集环境信息。</p>");
        }
        else
        {
            builder.AppendLine("<table class=\"facts\"><tbody>");

            foreach (var fact in facts)
            {
                builder.Append("<tr><th>")
                    .Append(Encode(fact.Label))
                    .Append("</th><td>")
                    .Append(Encode(fact.Value))
                    .AppendLine("</td></tr>");
            }

            builder.AppendLine("</tbody></table>");
        }

        builder.AppendLine("</section>");
    }

    private static void AppendSteps(StringBuilder builder, ReportDocument document)
    {
        builder.AppendLine("<section class=\"block\">");
        builder.AppendLine("<h2>步骤明细（观测事实）</h2>");

        if (document.Run.Steps.Count == 0)
        {
            builder.AppendLine("<p class=\"muted-text\">本次任务没有产生步骤结论。</p>");
            builder.AppendLine("</section>");
            return;
        }

        foreach (var step in document.Run.Steps)
        {
            builder.AppendLine("<article class=\"step\">");
            builder.Append("<div class=\"step-head\"><span class=\"step-name\">")
                .Append(Encode(step.Name))
                .Append("</span><span class=\"state small ")
                .Append(StateClass(step.Status))
                .Append("\">")
                .Append(Encode(StatusText(step.Status)))
                .Append("</span><span class=\"step-duration\">")
                .Append(Encode(step.DurationText))
                .AppendLine("</span></div>");

            builder.Append("<p class=\"step-summary\">")
                .Append(Encode(step.Summary))
                .AppendLine("</p>");

            // DiagnosticStepResults.Skipped 已把原因写进 Summary（"已跳过：<原因>"）。
            // 摘要里已经包含原因时不再单独输出，否则同一句话会出现两次，
            // 读者会误以为是两个不同的跳过原因。
            if (step.RequiresSkipReason
                && !string.IsNullOrWhiteSpace(step.SkipReason)
                && !step.Summary.Contains(step.SkipReason!, StringComparison.Ordinal))
            {
                builder.Append("<p class=\"notice notice-skip\">跳过原因：")
                    .Append(Encode(step.SkipReason!))
                    .AppendLine("</p>");
            }

            if (step.HasFacts)
            {
                builder.AppendLine("<table class=\"facts\"><tbody>");

                foreach (var fact in step.FactItems)
                {
                    builder.Append("<tr><th>")
                        .Append(Encode(fact.Label));

                    if (fact.Status is { } factStatus)
                    {
                        builder.Append("<span class=\"fact-status ")
                            .Append(StateClass(factStatus))
                            .Append("\">")
                            .Append(Encode(StatusText(factStatus)))
                            .Append("</span>");
                    }

                    builder.Append("</th><td>")
                        .Append(Encode(fact.Value))
                        .AppendLine("</td></tr>");
                }

                builder.AppendLine("</tbody></table>");
            }

            if (step.HasEvidence)
            {
                builder.AppendLine("<div class=\"evidence\"><h3>证据文件</h3><ul>");

                foreach (var evidence in step.EvidenceArtifacts)
                {
                    builder.Append("<li><span class=\"mono\">")
                        .Append(Encode(evidence.RelativePath))
                        .Append("</span> <span class=\"muted-text\">")
                        .Append(Encode(evidence.SizeText))
                        .Append(" · SHA-256 ")
                        .Append(Encode(evidence.ShortHash))
                        .Append(evidence.IsRedacted ? " · 已脱敏" : " · 未标记脱敏")
                        .Append("</span>");

                    if (!string.IsNullOrWhiteSpace(evidence.Description))
                    {
                        builder.Append("<div class=\"muted-text\">")
                            .Append(Encode(evidence.Description!))
                            .Append("</div>");
                    }

                    builder.AppendLine("</li>");
                }

                builder.AppendLine("</ul></div>");
            }

            if (step.HasRawOutput)
            {
                builder.AppendLine("<details class=\"raw\"><summary>原始输出（已脱敏）</summary><pre>");
                builder.Append(Encode(step.RawOutput!));
                builder.AppendLine("</pre></details>");
            }

            builder.AppendLine("</article>");
        }

        builder.AppendLine("</section>");
    }

    private static void AppendAdvice(StringBuilder builder, ReportDocument document)
    {
        var advices = document.Run.Steps
            .Where(static s => s.HasSuggestion)
            .Select(static s => (Step: s.Name, Suggestion: s.NextStepSuggestion!))
            .ToList();

        builder.AppendLine("<section class=\"block\">");
        builder.AppendLine("<h2>下一步建议（推断，非观测事实）</h2>");

        if (advices.Count == 0)
        {
            builder.AppendLine("<p class=\"muted-text\">本次任务未产生额外建议。</p>");
        }
        else
        {
            builder.AppendLine("<ul class=\"advice\">");

            foreach (var (step, suggestion) in advices)
            {
                builder.Append("<li><span class=\"advice-step\">")
                    .Append(Encode(step))
                    .Append("</span>")
                    .Append(Encode(suggestion))
                    .AppendLine("</li>");
            }

            builder.AppendLine("</ul>");
        }

        builder.AppendLine("</section>");
    }

    private static void AppendScope(StringBuilder builder, ReportDocument document)
    {
        builder.AppendLine("<section class=\"block\">");
        builder.AppendLine("<h2>数据范围与脱敏说明</h2>");
        builder.AppendLine("<ul class=\"scope\">");
        builder.Append("<li>本报告仅包含 <span class=\"mono\">")
            .Append(Encode(document.Run.DisplayName))
            .Append("</span> 一次任务的执行结果，不包含该目标在其他时间点的状态。</li>");
        builder.Append("<li>报告中的结论来自工具的实际观测；建议部分为推断，需人工复核。</li>");
        builder.Append("<li>未执行、被跳过或未能判定的步骤不会计入“通过”。</li>");
        builder.AppendLine(
            "<li>生成前已对全部文本执行敏感信息扫描；命中项会阻止生成，而不会被静默写入。</li>");
        builder.AppendLine(
            "<li>报告为自包含 HTML，不引用外部样式、脚本、字体或图片，可离线打开。</li>");

        if (document.Run.EvidenceDirectory is { Length: > 0 } directory)
        {
            builder.Append("<li>证据根目录：<span class=\"mono\">")
                .Append(Encode(directory))
                .AppendLine("</span></li>");
        }

        builder.AppendLine("</ul>");
        builder.AppendLine("</section>");
    }

    private static void AppendFooter(StringBuilder builder, ReportDocument document)
    {
        builder.AppendLine("<footer class=\"report-footer\">");
        builder.Append("<div>产品版本：")
            .Append(Encode(document.Metadata.ProductVersion))
            .Append("</div>");
        builder.Append("<div>报告生成时间：")
            .Append(Encode(FormatLocal(document.Metadata.GeneratedAt)))
            .Append("</div>");
        builder.Append("<div>运行标识：<span class=\"mono\">")
            .Append(Encode(document.Run.RunId))
            .AppendLine("</span></div>");
        builder.Append("<div>报告模板：LinkForge/")
            .Append(Encode(TemplateVersion))
            .Append("</div>");
        builder.AppendLine(
            "<div class=\"muted-text\">本报告由工具自动生成，敏感字段已脱敏；如需原始证据请联系执行人。</div>");
        builder.AppendLine("</footer>");
    }

    private static string FormatLocal(DateTimeOffset? value) =>
        value is { } timestamp
            ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "未结束";

    private static string StatusText(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Passed => "通过",
        DiagnosticStatus.Warning => "警告",
        DiagnosticStatus.Failed => "失败",
        DiagnosticStatus.Skipped => "已跳过",
        DiagnosticStatus.Unknown => "未判定",
        DiagnosticStatus.Cancelled => "已取消",
        DiagnosticStatus.TimedOut => "超时",
        DiagnosticStatus.Running => "执行中",
        DiagnosticStatus.NotRun => "未开始",
        _ => "未知"
    };

    private static string StateClass(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Passed => "s-ok",
        DiagnosticStatus.Warning => "s-warn",
        DiagnosticStatus.Failed or DiagnosticStatus.TimedOut => "s-bad",
        DiagnosticStatus.Skipped => "s-skip",
        DiagnosticStatus.Cancelled => "s-cancel",
        DiagnosticStatus.Unknown => "s-unknown",
        DiagnosticStatus.Running => "s-run",
        _ => "s-unknown"
    };

    private static string StateClass(TaskRunState state) => state switch
    {
        TaskRunState.Completed => "s-ok",
        TaskRunState.PartiallySucceeded => "s-warn",
        TaskRunState.Failed => "s-bad",
        TaskRunState.Cancelled => "s-cancel",
        TaskRunState.Inconclusive => "s-unknown",
        TaskRunState.Running or TaskRunState.Preparing => "s-run",
        _ => "s-unknown"
    };

    /// <summary>
    /// HTML 编码。
    /// </summary>
    /// <remarks>
    /// 使用 <see cref="WebUtility.HtmlEncode"/> 而不是手写替换：
    /// 手写版本几乎总是漏掉单引号或反引号，而属性值恰好可能用单引号包裹。
    /// 因此本文件的属性一律使用双引号，且所有插值都必须经过本方法。
    /// </remarks>
    private static string Encode(string? value) =>
        WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// 内联样式表。
    /// </summary>
    /// <remarks>
    /// 刻意不使用 CSS 变量或深色模式：报告需要打印，也需要在别人机器上打开。
    /// 固定浅色纸张版式 + 明确的打印规则，比"跟随主题"更可靠。
    /// </remarks>
    private const string Styles = """
<style>
  :root { color-scheme: light; }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    padding: 32px 24px;
    background: #f1f3f5;
    color: #1f2328;
    font-family: "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC",
                 "Hiragino Sans GB", "Source Han Sans SC", sans-serif;
    font-size: 14px;
    line-height: 1.65;
  }
  .report {
    max-width: 960px;
    margin: 0 auto;
    background: #ffffff;
    border: 1px solid #d8dee4;
    border-radius: 4px;
    padding: 40px 44px 32px;
  }
  .report-header { border-bottom: 3px solid #1f2328; padding-bottom: 16px; }
  .brand {
    font-size: 12px;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: #57606a;
  }
  h1 { margin: 8px 0 12px; font-size: 26px; line-height: 1.3; font-weight: 600; }
  h2 {
    margin: 0 0 12px;
    font-size: 16px;
    font-weight: 600;
    padding-bottom: 6px;
    border-bottom: 1px solid #d8dee4;
  }
  h3 { margin: 12px 0 6px; font-size: 13px; font-weight: 600; color: #57606a; }
  .state {
    display: inline-block;
    padding: 3px 12px;
    border-radius: 999px;
    font-size: 13px;
    font-weight: 600;
    border: 1px solid currentColor;
  }
  .state.small { font-size: 11px; padding: 1px 8px; font-weight: 500; }
  .s-ok { color: #1a7f37; }
  .s-warn { color: #9a6700; }
  .s-bad { color: #cf222e; }
  .s-skip { color: #57606a; }
  .s-cancel { color: #57606a; }
  .s-unknown { color: #6e7781; }
  .s-run { color: #0969da; }
  .meta-grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 6px 28px;
    margin: 20px 0 8px;
  }
  .meta-item { display: flex; gap: 10px; min-width: 0; }
  .meta-label { flex: 0 0 88px; color: #57606a; }
  .meta-value { flex: 1 1 auto; word-break: break-word; }
  .block { margin-top: 28px; }
  .counters { display: flex; flex-wrap: wrap; gap: 10px; }
  .counter {
    display: flex;
    align-items: center;
    gap: 6px;
    border: 1px solid #d8dee4;
    border-radius: 4px;
    padding: 6px 12px;
    background: #f6f8fa;
  }
  .counter.muted { opacity: 0.5; }
  .counter-kind { width: 8px; height: 8px; border-radius: 50%; background: currentColor; }
  .counter-kind.ok { color: #1a7f37; }
  .counter-kind.warn { color: #9a6700; }
  .counter-kind.bad { color: #cf222e; }
  .counter-kind.skip { color: #57606a; }
  .counter-kind.unknown { color: #6e7781; }
  .counter-count { font-weight: 600; font-variant-numeric: tabular-nums; }
  .counter-label { color: #57606a; font-size: 13px; }
  .notice {
    margin: 12px 0 0;
    padding: 10px 14px;
    border-left: 3px solid #9a6700;
    background: #fff8e5;
    border-radius: 0 3px 3px 0;
  }
  .notice-skip { border-left-color: #57606a; background: #f6f8fa; font-size: 13px; }
  .step {
    border: 1px solid #d8dee4;
    border-radius: 4px;
    padding: 14px 16px;
    margin-bottom: 10px;
    page-break-inside: avoid;
  }
  .step-head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .step-name { font-weight: 600; font-size: 15px; }
  .step-duration { color: #57606a; font-size: 12px; font-variant-numeric: tabular-nums; }
  .step-summary { margin: 8px 0 10px; word-break: break-word; }
  table.facts { width: 100%; border-collapse: collapse; font-size: 13px; }
  table.facts th, table.facts td {
    text-align: left;
    vertical-align: top;
    padding: 5px 10px;
    border-bottom: 1px solid #eaeef2;
    word-break: break-word;
  }
  table.facts th { width: 34%; font-weight: 500; color: #57606a; }
  .fact-status { margin-left: 8px; font-size: 11px; font-weight: 600; }
  .evidence ul { margin: 0; padding-left: 20px; }
  .evidence li { margin-bottom: 6px; word-break: break-all; }
  .mono { font-family: Consolas, "Cascadia Mono", "Courier New", monospace; font-size: 12px; }
  details.raw { margin-top: 10px; }
  details.raw summary { cursor: pointer; color: #0969da; font-size: 13px; }
  details.raw pre {
    margin: 8px 0 0;
    padding: 10px 12px;
    background: #f6f8fa;
    border: 1px solid #eaeef2;
    border-radius: 3px;
    overflow-x: auto;
    white-space: pre-wrap;
    word-break: break-word;
    font-family: Consolas, "Cascadia Mono", "Courier New", monospace;
    font-size: 12px;
    line-height: 1.5;
  }
  ul.advice, ul.scope { margin: 0; padding-left: 20px; }
  ul.advice li, ul.scope li { margin-bottom: 6px; word-break: break-word; }
  .advice-step {
    display: inline-block;
    margin-right: 8px;
    padding: 0 6px;
    border-radius: 3px;
    background: #eaeef2;
    color: #57606a;
    font-size: 12px;
  }
  .muted-text { color: #6e7781; font-size: 12px; }
  .report-footer {
    margin-top: 32px;
    padding-top: 14px;
    border-top: 1px solid #d8dee4;
    font-size: 12px;
    color: #57606a;
    display: grid;
    gap: 3px;
  }
  @media print {
    body { background: #ffffff; padding: 0; font-size: 11pt; }
    .report { border: none; border-radius: 0; max-width: none; padding: 0; }
    .step, .notice { break-inside: avoid; }
    details.raw { display: none; }
  }
</style>
""";
}
