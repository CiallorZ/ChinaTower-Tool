using System.Windows.Controls;

namespace LinkForge.App.Views.Pages;

/// <summary>项目与报告页（LF-010）。</summary>
/// <remarks>
/// 无业务逻辑：所有状态由 <c>ReportPageViewModel</c> 持有（DEV_STANDARDS §1.2）。
/// </remarks>
public partial class ReportPage : UserControl
{
    /// <summary>创建页面。</summary>
    public ReportPage()
    {
        InitializeComponent();
    }
}
