using System.Windows.Controls;

namespace LinkForge.App.Views.Pages;

/// <summary>
/// 工作台页面。
/// </summary>
/// <remarks>
/// 无 code-behind 逻辑：所有数据与命令都来自 <c>DashboardViewModel</c>。
/// 保留本文件是为了让 XAML 能通过 <c>x:Class</c> 关联部分类，
/// 同时也是"页面不含业务逻辑"这一约束的直观体现。
/// </remarks>
public partial class DashboardPage : UserControl
{
    /// <summary>创建工作台页面。</summary>
    public DashboardPage()
    {
        InitializeComponent();
    }
}