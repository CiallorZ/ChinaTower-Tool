using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Wpf.Ui.Appearance;

namespace LinkForge.App.Services;

/// <summary>
/// 用户可见的主题偏好。
/// </summary>
public enum LfThemePreference
{
    /// <summary>跟随 Windows 系统主题（默认，见 ADR-014）。</summary>
    FollowSystem,

    /// <summary>强制浅色（设计基准）。</summary>
    Light,

    /// <summary>强制深色。</summary>
    Dark
}

/// <summary>
/// 实际生效的主题。
/// </summary>
public enum LfResolvedTheme
{
    Light,
    Dark
}

/// <summary>
/// 设计令牌主题切换服务。
/// </summary>
/// <remarks>
/// 职责边界（阶段 01）：
/// <list type="bullet">
///   <item>把 <see cref="LfThemePreference"/> 解析为实际主题（含"跟随系统"）。</item>
///   <item>替换 <c>Themes/ThemeResources.xaml</c> 中的调色板字典，使所有 <c>Lf*</c> 令牌立即生效。</item>
/// </list>
/// 偏好持久化、跟随系统时的实时监听由阶段 03 的 <c>AppSettingsStore</c> / 壳层负责；
/// 本类不读写磁盘、不修改 ViewModel。
/// </remarks>
public sealed class ThemeManager
{
    /// <summary>
    /// 调色板字典的源路径片段，用于在运行时定位并替换当前调色板。
    /// 必须与 <c>Themes/ThemeResources.xaml</c> 中的 Source 保持一致。
    /// </summary>
    public const string PalettePathPrefix = "Themes/Tokens/Palette.";

    private const string LightPaletteSource = "pack://application:,,,/LinkForge.App;component/Themes/Tokens/Palette.Light.xaml";
    private const string DarkPaletteSource = "pack://application:,,,/LinkForge.App;component/Themes/Tokens/Palette.Dark.xaml";

    private readonly ResourceDictionary _root;
    private readonly AppSettingsStore? _settingsStore;

    /// <summary>
    /// 防止"同步 WPF-UI 主题 -> 触发 Changed -> 再次同步"的递归。
    /// </summary>
    private bool _isSyncingWithWpfUi;

    /// <summary>
    /// 使用 WPF 应用程序资源作为根字典创建实例。
    /// </summary>
    /// <param name="root">通常为 <see cref="Application.Current"/> 的 <c>Resources</c>。</param>
    /// <param name="settingsStore">
    /// 设置持久化；为 null 时不读写磁盘（单元测试与设计时使用）。
    /// </param>
    public ThemeManager(ResourceDictionary root, AppSettingsStore? settingsStore = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        _settingsStore = settingsStore;
    }

    /// <summary>当前用户偏好（与 <see cref="CurrentTheme"/> 的区别：后者是解析后的实际主题）。</summary>
    public LfThemePreference Preference { get; private set; } = LfThemePreference.FollowSystem;

    /// <summary>
    /// 从设置文件读取偏好并应用；无设置文件时使用默认值（跟随系统）。
    /// </summary>
    /// <returns>实际生效的主题。</returns>
    public LfResolvedTheme ApplySavedPreference()
    {
        var settings = _settingsStore?.Load();
        var preference = settings?.ThemePreference ?? LfThemePreference.FollowSystem;

        return Apply(preference);
    }

    /// <summary>
    /// 持久化当前偏好。
    /// </summary>
    /// <remarks>
    /// 只保存主题偏好，其余字段沿用已存设置 ——
    /// 否则切换主题会把导航折叠状态、面板高度一并重置，
    /// 那是用户会立刻察觉的数据丢失。
    /// </remarks>
    public void PersistPreference()
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        settings.ThemePreference = Preference;
        _settingsStore.Save(settings);
    }

    /// <summary>当前生效的主题。</summary>
    public LfResolvedTheme CurrentTheme { get; private set; } = LfResolvedTheme.Light;

    /// <summary>
    /// 把用户偏好解析为实际主题。
    /// </summary>
    /// <param name="preference">用户偏好；<see cref="LfThemePreference.FollowSystem"/> 时读取系统主题。</param>
    /// <returns>实际应生效的主题。</returns>
    public static LfResolvedTheme Resolve(LfThemePreference preference) => preference switch
    {
        LfThemePreference.Light => LfResolvedTheme.Light,
        LfThemePreference.Dark => LfResolvedTheme.Dark,
        LfThemePreference.FollowSystem => GetSystemTheme(),
        _ => LfResolvedTheme.Light
    };

    /// <summary>
    /// 读取当前 Windows 应用主题；读取失败时回退浅色（设计基准）。
    /// </summary>
    /// <remarks>
    /// 只有明确的深色系统主题才映射为深色，其余情况（Light、Unknown、高对比度变体）
    /// 一律回退浅色设计基准——避免把无法判定的系统状态渲染成"意外的深色"。
    /// </remarks>
    public static LfResolvedTheme GetSystemTheme()
    {
        try
        {
            return ApplicationThemeManager.GetSystemTheme() switch
            {
                SystemTheme.Dark => LfResolvedTheme.Dark,
                SystemTheme.HCBlack => LfResolvedTheme.Dark,
                _ => LfResolvedTheme.Light
            };
        }
        catch (Exception)
        {
            // 系统主题不可读（注册表受限、非交互会话）时不得让应用启动失败。
            return LfResolvedTheme.Light;
        }
    }

    /// <summary>
    /// 应用主题偏好：同步 WPF-UI 控件主题与 LinkForge 设计令牌。
    /// </summary>
    /// <param name="preference">用户偏好。</param>
    /// <returns>实际生效的主题。</returns>
    public LfResolvedTheme Apply(LfThemePreference preference)
    {
        Preference = preference;

        var resolved = Resolve(preference);
        ApplyResolved(resolved);
        return resolved;
    }

    /// <summary>
    /// WPF-UI 自行改动了应用主题（典型场景：系统主题变化，或 <c>SystemThemeWatcher</c> 首次注册）
    /// 之后，把 LinkForge 设计令牌拉回用户偏好。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么必须有这一步：<c>SystemThemeWatcher.Watch</c> 在注册第一个窗口时会调用
    /// <c>ApplicationThemeManager.ApplySystemTheme</c>，系统主题变化时也会再次调用。
    /// 它只切换 WPF-UI 自己的字典，<b>不会</b>替换 LinkForge 的 <c>Palette.*.xaml</c>。
    /// 结果就是控件库那半边变了、<c>Lf*</c> 令牌那半边没变 —— 界面变成"半深半浅"，
    /// 与 ADR-017 要修的缺陷同源，只是触发路径不同。
    /// </para>
    /// <para>
    /// 用 <see cref="ApplyResolved"/> 而不是 <see cref="Apply"/>：后者会改写
    /// <see cref="Preference"/>，把"跟随系统"意外固化成当时的系统主题。
    /// </para>
    /// <para>
    /// 终止性：若当前偏好是显式浅色/深色，本方法会把 WPF-UI 主题改回去，
    /// 而 <c>ApplicationThemeManager.Apply</c> 只在字典确实发生变化时才抛 <c>Changed</c>，
    /// 第二次进入时偏好与实际一致、不再产生事件；<see cref="_isSyncingWithWpfUi"/> 是
    /// 额外的硬保险，确保任何版本的控件库都不会把这里变成死循环。
    /// </para>
    /// </remarks>
    public void SyncWithWpfUiTheme()
    {
        if (_isSyncingWithWpfUi)
        {
            return;
        }

        _isSyncingWithWpfUi = true;

        try
        {
            ApplyResolved(Resolve(Preference));
        }
        finally
        {
            _isSyncingWithWpfUi = false;
        }
    }

    /// <summary>
    /// 应用已解析的主题，供系统主题变更事件直接调用。
    /// </summary>
    public void ApplyResolved(LfResolvedTheme theme)
    {
        ApplicationThemeManager.Apply(
            theme == LfResolvedTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light);

        ReplacePalette(theme);
        CurrentTheme = theme;
    }

    private void ReplacePalette(LfResolvedTheme theme)
    {
        var target = theme == LfResolvedTheme.Dark ? DarkPaletteSource : LightPaletteSource;
        var palette = FindPaletteDictionary(_root)
            ?? throw new InvalidOperationException(
                $"未在应用资源中找到调色板字典（路径片段 \"{PalettePathPrefix}\"）。" +
                "请确认 App.xaml 已合并 Themes/ThemeResources.xaml。");

        palette.Source = new Uri(target, UriKind.Absolute);
    }

    private static ResourceDictionary? FindPaletteDictionary(ResourceDictionary dictionary)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (IsPalette(merged))
            {
                return merged;
            }

            var nested = FindPaletteDictionary(merged);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Source 在运行时可能为 null 或非 file/pack URI，任何失败都只表示'不是调色板'。")]
    private static bool IsPalette(ResourceDictionary dictionary)
    {
        try
        {
            var source = dictionary.Source?.OriginalString;
            if (string.IsNullOrEmpty(source))
            {
                return false;
            }

            // pack URI 使用正斜杠，而测试或诊断代码可能传入 Windows 反斜杠路径，
            // 两种写法都必须能识别，否则主题切换会静默失效。
            var normalized = source.Replace('\\', '/');

            return normalized.Contains(PalettePathPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
