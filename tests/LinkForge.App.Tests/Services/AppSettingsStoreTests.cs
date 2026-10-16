using System.IO;
using System.Text.Json;
using LinkForge.App.Services;

namespace LinkForge.App.Tests.Services;

/// <summary>
/// 用户设置持久化测试（阶段 03：主题与面板状态必须能跨重启保持）。
/// </summary>
/// <remarks>
/// 三条被测的"启动期防线"：
/// <list type="number">
///   <item>设置文件损坏不得让应用起不来；</item>
///   <item>手改出来的越界值必须被钳制，而不是把布局搞乱；</item>
///   <item>设置文件里永远不能出现秘密。</item>
/// </list>
/// </remarks>
public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "linkforge-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不应让测试失败。
        }
    }

    [Fact]
    public void Missing_file_should_yield_defaults()
    {
        var store = new AppSettingsStore(SettingsPath);
        var settings = store.Load();

        settings.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        settings.ThemePreference.ShouldBe(LfThemePreference.FollowSystem);
        settings.IsNavigationPaneOpen.ShouldBeTrue();
        settings.IsLogPanelOpen.ShouldBeFalse();
        settings.LogPanelHeight.ShouldBe(200);
    }

    [Fact]
    public void Save_then_load_should_round_trip_every_field()
    {
        var store = new AppSettingsStore(SettingsPath);

        store.Save(new AppSettings
        {
            ThemePreference = LfThemePreference.Dark,
            IsNavigationPaneOpen = false,
            IsLogPanelOpen = true,
            LogPanelHeight = 320
        });

        var loaded = store.Load();

        loaded.ThemePreference.ShouldBe(LfThemePreference.Dark);
        loaded.IsNavigationPaneOpen.ShouldBeFalse();
        loaded.IsLogPanelOpen.ShouldBeTrue();
        loaded.LogPanelHeight.ShouldBe(320);
    }

    [Fact]
    public void Theme_preference_should_be_stored_as_a_readable_name()
    {
        // 数字枚举在配置文件里无法自解释：排查"主题为什么不对"时看不出 0/1/2。
        var store = new AppSettingsStore(SettingsPath);

        store.Save(new AppSettings { ThemePreference = LfThemePreference.Dark });

        var json = File.ReadAllText(SettingsPath);
        json.ShouldContain("\"Dark\"");
    }

    [Fact]
    public void Save_should_not_leave_a_temporary_file_behind()
    {
        // 原子写入的实现是"先写 .tmp 再替换"，若替换失败或忘记删除，
        // 目录里会长期残留半截文件，下一次写入的临时文件会与之冲突。
        var store = new AppSettingsStore(SettingsPath);

        store.Save(new AppSettings());
        store.Save(new AppSettings { ThemePreference = LfThemePreference.Light });

        Directory.GetFiles(_directory).ShouldBe([SettingsPath]);
    }

    [Theory]
    [InlineData(-1000)]
    [InlineData(0)]
    [InlineData(10)]
    public void Too_small_log_panel_height_should_be_clamped(double height)
    {
        var store = new AppSettingsStore(SettingsPath);

        store.Save(new AppSettings { LogPanelHeight = height });

        store.Load().LogPanelHeight.ShouldBe(AppSettingsStore.MinLogPanelHeight);
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(641)]
    public void Too_large_log_panel_height_should_be_clamped(double height)
    {
        var store = new AppSettingsStore(SettingsPath);

        store.Save(new AppSettings { LogPanelHeight = height });

        store.Load().LogPanelHeight.ShouldBe(AppSettingsStore.MaxLogPanelHeight);
    }

    [Fact]
    public void Out_of_range_value_written_by_hand_should_be_clamped_on_load()
    {
        // 关键场景：用户直接编辑了 JSON，Save 路径根本没被走到。
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"SchemaVersion":1,"LogPanelHeight":99999}""");

        var loaded = new AppSettingsStore(SettingsPath).Load();

        loaded.LogPanelHeight.ShouldBe(AppSettingsStore.MaxLogPanelHeight);
    }

    [Fact]
    public void Undefined_theme_value_should_fall_back_to_follow_system()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"SchemaVersion":1,"ThemePreference":"Neon"}""");

        var loaded = new AppSettingsStore(SettingsPath).Load();

        loaded.ThemePreference.ShouldBe(LfThemePreference.FollowSystem);
    }

    [Fact]
    public void Corrupt_json_should_fall_back_to_defaults_and_quarantine_the_file()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ this is not json ");

        var loaded = new AppSettingsStore(SettingsPath).Load();

        loaded.ThemePreference.ShouldBe(LfThemePreference.FollowSystem);

        // 坏文件必须被改名保留而不是直接丢弃：用户配置是排查问题的证据。
        File.Exists(SettingsPath).ShouldBeFalse("损坏的设置文件应被移走，避免下次启动继续踩同一个坑。");

        var quarantined = Directory.GetFiles(_directory, "settings.json.corrupt-*");
        quarantined.Length.ShouldBe(1, "损坏的设置文件应被改名保留以便排查。");
    }

    [Fact]
    public void Json_null_literal_should_be_treated_as_corrupt()
    {
        // "null" 是合法 JSON，反序列化后得到 null 对象而不是抛异常。
        // 若不显式判断，后续会直接 NullReferenceException。
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "null");

        var loaded = new AppSettingsStore(SettingsPath).Load();

        loaded.ShouldNotBeNull();
        loaded.ThemePreference.ShouldBe(LfThemePreference.FollowSystem);
    }

    [Fact]
    public void Save_should_stamp_the_current_schema_version()
    {
        var store = new AppSettingsStore(SettingsPath);

        store.Save(new AppSettings { SchemaVersion = 0 });

        store.Load().SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
    }

    /// <summary>
    /// 设置文件是"界面偏好"，永远不得包含秘密。
    /// </summary>
    /// <remarks>
    /// 用白名单而不是黑名单：断言"属性集合恰好是这几个"，
    /// 这样将来有人加一个 <c>ApiToken</c> 属性，测试会立刻失败；
    /// 黑名单（搜 "password" 等关键字）则会被 <c>Cred</c>、<c>Auth</c> 之类的命名绕过。
    /// </remarks>
    [Fact]
    public void Settings_should_only_contain_known_non_secret_fields()
    {
        var properties = typeof(AppSettings)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        properties.ShouldBe(
            [
                "IsLogPanelOpen",
                "IsNavigationPaneOpen",
                "LogPanelHeight",
                "SchemaVersion",
                "ThemePreference"
            ],
            "AppSettings 只允许存放界面偏好。秘密必须走 DPAPI + SecretReference（ADR-008），" +
            "不得混入本文件。");
    }

    [Fact]
    public void Persisted_json_should_not_contain_any_credential_like_key()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Save(new AppSettings());

        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));

        var keys = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        keys.ShouldNotContain(
            key => key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || key.Contains("password", StringComparison.OrdinalIgnoreCase)
                || key.Contains("token", StringComparison.OrdinalIgnoreCase)
                || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || key.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constructor_should_reject_blank_path()
    {
        Should.Throw<ArgumentException>(() => new AppSettingsStore("   "));
    }
}
