namespace LinkForge.App.Services;

/// <summary>
/// 用户级应用设置（持久化到 <c>%LOCALAPPDATA%\LinkForge\settings.json</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意不包含任何秘密：这里只放"界面偏好"（主题、导航折叠、日志面板状态）。
/// 凭据一律走 DPAPI + SecretReference（ADR-008），不得混入本文件。
/// 测试 <c>AppSettingsStoreTests.Settings_should_not_contain_secret_fields</c>
/// 会守住这条边界，防止后续有人图方便把 Token 塞进来。
/// </para>
/// <para>
/// <see cref="SchemaVersion"/> 存在的意义：将来字段变更时可以据此迁移，
/// 而不是在读取旧文件时抛异常或静默丢字段。
/// </para>
/// </remarks>
public sealed class AppSettings
{
    /// <summary>当前设置文件的 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>设置文件 schema 版本。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>主题偏好（跟随系统 / 浅色 / 深色）。</summary>
    public LfThemePreference ThemePreference { get; set; } = LfThemePreference.FollowSystem;

    /// <summary>左侧导航是否展开（false 表示折叠为图标模式）。</summary>
    public bool IsNavigationPaneOpen { get; set; } = true;

    /// <summary>底部日志面板是否展开。</summary>
    public bool IsLogPanelOpen { get; set; }

    /// <summary>底部日志面板高度（像素）。</summary>
    /// <remarks>
    /// 用 double 而非 int：GridSplitter 拖动产生的是分数像素，
    /// 强制取整会在高 DPI 下产生可见跳动。
    /// </remarks>
    public double LogPanelHeight { get; set; } = 200;
}