namespace LinkForge.App.Navigation;

/// <summary>
/// 图标字形令牌（Segoe Fluent Icons / Segoe MDL2 Assets 码位）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接用 <c>Wpf.Ui.Controls.SymbolRegular</c>：
/// 该枚举的码位对应 WPF-UI 自带的 Fluent 图标字体，
/// 与 Windows 自带的 Segoe Fluent Icons / Segoe MDL2 Assets 是两套不同的码表。
/// 本机实测（GlyphTypeface.CharacterToGlyphMap）确认二者不通用 ——
/// 例如 <c>SymbolRegular.Home24 = 0xF481</c>，而系统字体中该码位无字形，
/// 直接使用会渲染成方框。
/// </para>
/// <para>
/// 因此统一使用 Windows 10/11 自带字体的码位，不引入额外字体依赖。
/// 每个码位都已用 <see cref="System.Windows.Media.GlyphTypeface"/> 在本机两种字体上验证存在；
/// 测试 <c>GlyphTests</c> 会对其中"导航与状态必需"的子集重复该验证，
/// 防止后续新增图标时写入一个不存在的码位（那种错误在界面上只表现为一个空心方框，很难察觉）。
/// </para>
/// </remarks>
public static class LfGlyph
{
    // ===== 一级导航 =====
    public const string Dashboard = "\uE80F";        // Home
    public const string Assets = "\uE7F4";           // DeviceLaptopNoPic
    public const string Connect = "\uE99A";          // PlugConnected
    public const string Network = "\uE968";          // NetworkTower / 通用网络
    public const string Diagnostics = "\uE9D9";      // Flowchart / Diagnostic
    public const string Security = "\uEA18";         // Shield
    public const string Developer = "\uEC7A";        // DeveloperTools / Toolbox
    public const string Reports = "\uE8A5";          // Document
    public const string Settings = "\uE713";         // Settings

    // ===== 二级导航 =====
    public const string Video = "\uE722";            // Camera / Video
    public const string Iot = "\uE839";              // Ethernet
    public const string Api = "\uE943";              // Code

    // ===== 通用操作 =====
    public const string Search = "\uE721";
    public const string Save = "\uE74E";
    public const string Filter = "\uE71C";
    public const string Refresh = "\uE8E5";
    public const string Add = "\uE710";
    public const string Delete = "\uE74D";
    public const string OpenFile = "\uE8C8";
    public const string Play = "\uE768";
    public const string Stop = "\uE71A";
    public const string Task = "\uE9D5";
    public const string History = "\uE81C";
    public const string Wrench = "\uE90F";
    public const string Bug = "\uEBE8";
    public const string Certificate = "\uEB95";
    public const string Database = "\uE8D7";
    public const string Server = "\uE950";
    public const string Folder = "\uE8B7";
    public const string Contact = "\uE77B";

    // ===== 面板与主题 =====
    public const string Palette = "\uE790";
    public const string Keyboard = "\uE765";
    public const string Link = "\uE8DE";
    public const string AllApps = "\uE7C4";
    public const string Library = "\uE8F1";

    /// <summary>图标字体族；Fluent 优先，Windows 10 回退到 MDL2。</summary>
    /// <remarks>
    /// Windows 10 不带 Segoe Fluent Icons，只带 Segoe MDL2 Assets；
    /// 两者在本项目使用的全部码位上均已实测一致，因此回退是安全的。
    /// </remarks>
    public const string FontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";
}