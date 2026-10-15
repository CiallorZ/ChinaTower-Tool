using System.IO.Packaging;
using System.Windows;
using System.Windows.Media;

namespace LinkForge.App.Tests.DesignTokens;

/// <summary>
/// 以真实 WPF 解析器加载调色板。
/// </summary>
/// <remarks>
/// 使用与生产完全相同的 <c>pack://application</c> URI，而非直接读磁盘文件：
/// <list type="bullet">
///   <item>校验的是实际编译进程序集并会被应用加载的 BAML，而不是磁盘上的副本；</item>
///   <item>XAML 语法错误、资源引用错误、类型错误都会在此直接暴露。</item>
/// </list>
/// </remarks>
internal sealed class PaletteFile
{
    internal const string LightUri = "pack://application:,,,/LinkForge.App;component/Themes/Tokens/Palette.Light.xaml";
    internal const string DarkUri = "pack://application:,,,/LinkForge.App;component/Themes/Tokens/Palette.Dark.xaml";

    static PaletteFile()
    {
        // pack:// 方案由 WPF 的 PackUriHelper 静态构造器注册；测试宿主进程默认未触发，
        // 因此这里显式访问一次，否则 new Uri("pack://...") 会抛 "Invalid port specified"。
        _ = PackUriHelper.UriSchemePack;
    }

    private readonly ResourceDictionary _dictionary;

    private PaletteFile(string path)
    {
        Path = path;
        _dictionary = new ResourceDictionary
        {
            Source = new Uri(path, UriKind.Absolute)
        };
    }

    internal string Path { get; }

    internal IReadOnlyList<string> ColorKeys => KeysWithPrefix("LfColor");

    internal IReadOnlyList<string> BrushKeys => KeysWithPrefix("LfBrush");

    internal IReadOnlyList<string> AllKeys =>
        _dictionary.Keys.Cast<object>().Select(key => key.ToString() ?? string.Empty).ToList();

    internal static PaletteFile LoadLight() => new(LightUri);

    internal static PaletteFile LoadDark() => new(DarkUri);

    internal Color Color(string key)
    {
        _dictionary[key].ShouldBeOfType<Color>(
            $"令牌 {key} 在 {Path} 中必须是 Color 类型。");

        return (Color)_dictionary[key];
    }

    internal SolidColorBrush Brush(string key)
    {
        _dictionary[key].ShouldBeOfType<SolidColorBrush>(
            $"令牌 {key} 在 {Path} 中必须是 SolidColorBrush 类型。");

        return (SolidColorBrush)_dictionary[key];
    }

    /// <summary>
    /// 读取原始资源值（不做类型断言），用于校验类型本身是否符合预期。
    /// </summary>
    internal object? RawValue(string key) => _dictionary.Contains(key) ? _dictionary[key] : null;

    private IReadOnlyList<string> KeysWithPrefix(string prefix) =>
        AllKeys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
}
