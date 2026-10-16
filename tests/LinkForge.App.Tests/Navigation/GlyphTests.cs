using System.IO;
using System.Reflection;
using System.Windows.Media;
using LinkForge.App.Navigation;
using LinkForge.App.Tests.DesignTokens;

namespace LinkForge.App.Tests.Navigation;

/// <summary>
/// 图标字形守卫：写入的码位必须在系统字体里真实存在。
/// </summary>
/// <remarks>
/// <para>
/// 为什么值得单独写一类测试：码位写错（或抄了另一套字体表的码位）<b>不会报任何错</b>，
/// 编译通过、运行不崩，只是界面上出现一个空心方框。
/// 这种缺陷通常要到人工走查才被发现，而那时它已经散布在多个页面里。
/// </para>
/// <para>
/// 验证方式是直接读字体文件的 cmap（<see cref="GlyphTypeface.CharacterToGlyphMap"/>），
/// 而不是"渲染出来看着像就行" —— 后者在自动化测试里无法判定。
/// </para>
/// <para>
/// 特别地，本用例会证明 WPF-UI 的 <c>SymbolRegular</c> 码位<b>不可用</b>：
/// 它对应控件库自带的 Fluent 图标字体，与 Windows 自带的
/// Segoe Fluent Icons / Segoe MDL2 Assets 是两套不同码表。
/// 见 <c>Navigation/LfGlyph.cs</c> 的说明。
/// </para>
/// </remarks>
public sealed class GlyphTests
{
    /// <summary>
    /// Windows 自带图标字体文件（Segoe Fluent Icons 与 Windows 10 的回退字体）。
    /// </summary>
    private static readonly string[] SystemIconFontFiles =
    [
        @"C:\Windows\Fonts\SegoeIcons.ttf",
        @"C:\Windows\Fonts\segmdl2.ttf"
    ];

    /// <summary>
    /// 不是字形、而是字体族名的常量，需从字形断言中排除。
    /// </summary>
    /// <remarks>
    /// <see cref="LfGlyph.FontFamily"/> 与字形常量同为 <c>public const string</c>，
    /// 反射遍历时会被一并取到。它天然是多字符（字体族回退列表），
    /// 因此必须显式排除，否则会得到"字体族名不是单个图标字符"的假失败。
    /// 该常量的回退链由 <c>Icon_font_family_token_should_prefer_fluent_and_fall_back_to_mdl2</c> 单独验证。
    /// </remarks>
    private static readonly HashSet<string> NonGlyphConstantNames =
        new(StringComparer.Ordinal) { nameof(LfGlyph.FontFamily) };

    [Fact]
    public void Both_system_icon_fonts_should_be_present_on_this_machine()
    {
        // 缺字体时后续用例会"因为字体不存在而跳过"，从而静默失去保护。
        // 因此先显式断言字体存在（Windows 10 自带 segmdl2，Windows 11 两者都有）。
        foreach (var file in SystemIconFontFiles)
        {
            File.Exists(file).ShouldBeTrue(
                $"未找到系统图标字体 {file}。图标令牌的验证依赖它，请确认运行环境。");
        }
    }

    [Fact]
    public void Every_navigation_glyph_should_exist_in_both_system_icon_fonts()
    {
        // 必须两种字体都有：Windows 11 用 Segoe Fluent Icons，
        // Windows 10 只有 Segoe MDL2 Assets。只验证一种会让另一个平台出现方框。
        var navigationGlyphs = NavigationCatalog.Enumerate()
            .Select(node => (Name: node.Id, Glyph: node.Glyph))
            .ToArray();

        navigationGlyphs.ShouldNotBeEmpty();

        var missing = FindMissing(navigationGlyphs);

        missing.ShouldBeEmpty(
            "以下导航图标码位在系统图标字体中不存在，会渲染成空心方框：" +
            string.Join("; ", missing) +
            "。请改用 Navigation/LfGlyph.cs 中已登记的字形，并确认码位来自系统字体而不是 WPF-UI 的 SymbolRegular。");
    }

    [Fact]
    public void Every_declared_glyph_token_should_exist_in_both_system_icon_fonts()
    {
        // 不只测导航用到的：LfGlyph 是"字形令牌表"，
        // 表里存在一个坏码位，后续页面一用就出方框，而那时不会有人想到是这里的问题。
        var tokens = typeof(LfGlyph)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Where(field => !NonGlyphConstantNames.Contains(field.Name))
            .Select(field => (Name: field.Name, Glyph: (string)field.GetRawConstantValue()!))
            .ToArray();

        tokens.ShouldNotBeEmpty("LfGlyph 未暴露任何字形令牌。");

        var missing = FindMissing(tokens);

        missing.ShouldBeEmpty(
            "LfGlyph 中存在系统字体不支持的码位（一旦被页面使用就是方框）：" +
            string.Join("; ", missing));
    }

    [Fact]
    public void Glyph_tokens_should_be_single_characters()
    {
        // 图标是单字符字形；写成多字符（例如误写成字符串名字）在 TextBlock 里
        // 会渲染成一串方框，且不会报错。
        foreach (var field in typeof(LfGlyph).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral
                || field.FieldType != typeof(string)
                || NonGlyphConstantNames.Contains(field.Name))
            {
                continue;
            }

            var value = (string)field.GetRawConstantValue()!;

            value.Length.ShouldBe(
                1,
                $"LfGlyph.{field.Name} 必须是单个字符（图标字体中的私用区码位）。");

            // 私用区（Private Use Area）：图标字体把字形放在这里，
            // 落在普通 ASCII 范围说明多半写成了字母或数字。
            ((int)value[0]).ShouldBeGreaterThanOrEqualTo(
                0xE000,
                $"LfGlyph.{field.Name} 不在 Unicode 私用区，图标字体中不会渲染出图标。");
        }
    }

    [Fact]
    public void WpfUi_symbol_regular_codepoints_should_be_rejected_by_system_fonts()
    {
        // 这条用例锁定"为什么不用 SymbolRegular"这一决策依据（见 LfGlyph.cs 注释）。
        // 如果将来某个 WPF-UI 版本把它对齐到了系统字体，这里会失败，
        // 那时可以重新评估是否直接复用 SymbolRegular —— 失败本身是有价值的信息。
        const int symbolRegularHome = 0xF481;

        var supported = StaRunner.Run(() => SystemIconFontsSupporting(symbolRegularHome));

        supported.ShouldBeEmpty(
            "WPF-UI SymbolRegular.Home24 (U+F481) 意外地被系统图标字体支持了。" +
            "这意味着 LfGlyph.cs 中“两套码表不通用”的结论需要重新评估。");
    }

    [Fact]
    public void Icon_font_family_token_should_prefer_fluent_and_fall_back_to_mdl2()
    {
        // 回退链写错（例如漏掉 MDL2）在 Windows 11 上完全正常，
        // 只有 Windows 10 才会表现为"全部图标变方框"。
        LfGlyph.FontFamily.ShouldContain("Segoe Fluent Icons");
        LfGlyph.FontFamily.ShouldContain("Segoe MDL2 Assets");

        // 顺序有意义：Fluent 在前，MDL2 作为回退。
        LfGlyph.FontFamily.IndexOf("Segoe Fluent Icons", StringComparison.Ordinal)
            .ShouldBeLessThan(LfGlyph.FontFamily.IndexOf("Segoe MDL2 Assets", StringComparison.Ordinal));
    }

    private static List<string> FindMissing(IEnumerable<(string Name, string Glyph)> glyphs)
    {
        var missing = new List<string>();

        StaRunner.Run(() =>
        {
            var fonts = SystemIconFontFiles
                .Select(file => (File: Path.GetFileName(file), Map: new GlyphTypeface(new Uri(file)).CharacterToGlyphMap))
                .ToArray();

            foreach (var (name, glyph) in glyphs)
            {
                var code = glyph[0];

                foreach (var (file, map) in fonts)
                {
                    if (!map.ContainsKey(code))
                    {
                        missing.Add($"{name}=U+{code:X4} 在 {file} 中缺失");
                    }
                }
            }

            return 0;
        });

        return missing;
    }

    private static List<string> SystemIconFontsSupporting(int codePoint)
    {
        var result = new List<string>();

        foreach (var file in SystemIconFontFiles)
        {
            var map = new GlyphTypeface(new Uri(file)).CharacterToGlyphMap;

            if (map.ContainsKey(codePoint))
            {
                result.Add(Path.GetFileName(file));
            }
        }

        return result;
    }
}
