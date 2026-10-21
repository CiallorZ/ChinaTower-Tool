using LinkForge.Reporting.Reports;

namespace LinkForge.Reporting.Tests;

public class ReportHtmlRendererTests
{
    [Fact]
    public void Rendered_document_should_be_self_contained_html()
    {
        var html = ReportHtmlRenderer.Render(TestData.Document());

        html.ShouldStartWith("<!DOCTYPE html>");
        html.ShouldContain("<html lang=\"zh-CN\">");
        html.ShouldContain("<meta charset=\"utf-8\">");
        html.ShouldEndWith("</html>" + Environment.NewLine);

        // 离线要求：不得引用任何远程资源或脚本。
        html.ShouldNotContain("<script", Case.Insensitive);
        html.ShouldNotContain("http://", Case.Insensitive);
        html.ShouldNotContain("https://", Case.Insensitive);
        html.ShouldNotContain("<link ");
        html.ShouldNotContain("<img ");
        html.ShouldNotContain("@import");
    }

    [Fact]
    public void Rendered_document_should_contain_all_report_sections()
    {
        var html = ReportHtmlRenderer.Render(TestData.Document());

        html.ShouldContain("结果概览");
        html.ShouldContain("生成环境");
        html.ShouldContain("步骤明细（观测事实）");
        html.ShouldContain("下一步建议（推断，非观测事实）");
        html.ShouldContain("数据范围与脱敏说明");
        html.ShouldContain("产品版本：1.0.0-test");
        html.ShouldContain("报告模板：LinkForge/1.0");
    }

    [Fact]
    public void Report_should_reflect_run_facts_without_reinterpreting_them()
    {
        var html = ReportHtmlRenderer.Render(TestData.Document());

        // 状态、计数、步骤名与摘要都必须来自真实运行结果。
        html.ShouldContain("10.0.0.8:443");
        html.ShouldContain("已完成");
        html.ShouldContain("DNS 解析");
        html.ShouldContain("解析到 10.0.0.8。");
        html.ShouldContain("evidence/tcp.txt");
        html.ShouldContain("2 个步骤：2 通过");
    }

    [Fact]
    public void Skipped_step_should_not_be_rendered_as_failure()
    {
        var html = ReportHtmlRenderer.Render(TestData.Document(TestData.MixedRun()));

        html.ShouldContain("已跳过");

        // 跳过原因已包含在摘要 "已跳过：<原因>" 中，不得再次单独渲染：
        // 同一句话出现两次会让读者以为是两个不同原因。
        var occurrences = html.Split("本阶段未实现媒体接收。").Length - 1;
        occurrences.ShouldBe(1);

        // 4 个步骤：1 通过 / 1 失败 / 1 跳过 / 1 未判定。
        html.ShouldContain("4 个步骤：1 通过 / 1 失败 / 1 跳过 / 1 未判定");
    }

    [Fact]
    public void Unknown_step_should_not_be_rendered_as_passed()
    {
        var html = ReportHtmlRenderer.Render(TestData.Document(TestData.MixedRun()));

        html.ShouldContain("未判定");
        html.ShouldContain("缺少解码器，无法判定。");
        html.ShouldNotContain("s-ok\">未判定");
    }

    [Fact]
    public void Partial_success_should_be_labelled_as_incomplete()
    {
        var html = ReportHtmlRenderer.Render(TestData.Document(TestData.MixedRun()));

        html.ShouldContain("部分成功");
        html.ShouldContain("本次结论不完整");
        html.ShouldContain("不要将本报告单独作为验收依据");
    }

    [Theory]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("\"><img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"javascript:alert(1)\"></iframe>")]
    [InlineData("</style><script>alert(1)</script>")]
    public void Injected_html_in_target_should_be_escaped(string payload)
    {
        var document = TestData.Document(target: payload);

        var html = ReportHtmlRenderer.Render(document);

        html.ShouldNotContain("<script>");
        html.ShouldNotContain("<iframe");
        html.ShouldNotContain("<img");

        // 原文被转义后，其文本内容仍会出现在文档里（这是可接受的），
        // 但不能再构成标签或事件属性。
        html.ShouldNotContain("\"><img");
        html.ShouldContain("&lt;");
    }

    [Fact]
    public void Injected_html_in_step_summary_should_be_escaped()
    {
        var run = TestData.HealthyRun() with
        {
            Steps =
            [
                new LinkForge.Domain.Diagnostics.DiagnosticStepResult(
                    "s1",
                    "<b>加粗</b>",
                    LinkForge.Domain.Diagnostics.DiagnosticStatus.Passed,
                    TestData.Start,
                    TimeSpan.FromSeconds(1),
                    "摘要 <script>alert('x')</script> 结束")
            ]
        };

        var html = ReportHtmlRenderer.Render(TestData.Document(run));

        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
        html.ShouldContain("&lt;b&gt;加粗&lt;/b&gt;");
    }

    [Fact]
    public void Chinese_content_should_be_preserved_verbatim()
    {
        var html = ReportHtmlRenderer.Render(TestData.Document());

        // 中文不得变成实体，否则打印与全文检索都会出问题。
        html.ShouldContain("网络基础诊断");
        html.ShouldContain("视联网接入验收");
        html.ShouldNotContain("&#x7F51;");
    }
}
