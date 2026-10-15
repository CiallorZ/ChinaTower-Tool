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

    /// <summary>
    /// 使用 WPF 应用程序资源作为根字典创建实例。
    /// </summary>
    /// <param name="root">通常为 <see cref="Application.Current"/> 的 <c>Resources</c>。</param>
    public ThemeManager(ResourceDictionary root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
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
        var resolved = Resolve(preference);
        ApplyResolved(resolved);
        return resolved;
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
