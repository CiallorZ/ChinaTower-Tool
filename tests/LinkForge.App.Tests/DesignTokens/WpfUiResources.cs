using System.Windows;

namespace LinkForge.App.Tests.DesignTokens;

/// <summary>
/// 枚举 WPF-UI 实际提供的资源键。
/// </summary>
/// <remarks>
/// WPF-UI 把主题与控件字典以 BAML 形式嵌入程序集，键集合只能通过加载字典得到，
/// 无法用文本搜索获得。这里加载与 App.xaml 相同的字典组合，
/// 递归收集键（<see cref="ResourceDictionary.Keys"/> 不含合并字典的键）。
/// </remarks>
internal static class WpfUiResources
{
    private static readonly Lazy<HashSet<string>> KeysLazy = new(
        () => StaRunner.Run(() =>
        {
            var dictionary = new ResourceDictionary();
            dictionary.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
            {
                Theme = Wpf.Ui.Appearance.ApplicationTheme.Light
            });
            dictionary.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

            var keys = new HashSet<string>(StringComparer.Ordinal);
            Collect(dictionary, keys);
            return keys;
        }));

    internal static HashSet<string> EnumerateKeys() => KeysLazy.Value;

    private static void Collect(ResourceDictionary dictionary, HashSet<string> into)
    {
        foreach (var key in dictionary.Keys)
        {
            if (key?.ToString() is { Length: > 0 } text)
            {
                into.Add(text);
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            Collect(merged, into);
        }
    }
}
