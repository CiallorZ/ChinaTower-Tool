using System.Windows.Controls;

namespace LinkForge.App.Views.Pages;

/// <summary>
/// 设备与资产页（LF-004）。
/// </summary>
/// <remarks>
/// 业务逻辑一律在 <c>AssetCatalogViewModel</c> 中；本文件只保留
/// WPF 要求的 code-behind 骨架（DEV_STANDARDS §1.2）。
/// </remarks>
public partial class AssetCatalogPage : UserControl
{
    /// <summary>创建页面。</summary>
    public AssetCatalogPage()
    {
        InitializeComponent();
    }
}
