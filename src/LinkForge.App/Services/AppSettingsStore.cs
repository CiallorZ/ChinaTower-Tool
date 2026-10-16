using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkForge.App.Services;

/// <summary>
/// 应用设置的文件级持久化。
/// </summary>
/// <remarks>
/// <para>
/// 存储位置：<c>%LOCALAPPDATA%\LinkForge\settings.json</c>（用户级，不需要管理员权限）。
/// </para>
/// <para>
/// 三条实现约束：
/// </para>
/// <list type="number">
///   <item>
///     <b>损坏文件不得让应用无法启动。</b>JSON 解析失败或字段越界时回退到默认值，
///     并把坏文件改名保留（<c>settings.json.corrupt-{timestamp}</c>），
///     便于事后排查，而不是直接丢弃用户的配置。
///   </item>
///   <item>
///     <b>写入必须是原子的。</b>先写临时文件再替换，
///     避免断电/崩溃时留下半截 JSON —— 那会让下次启动丢配置。
///   </item>
///   <item>
///     <b>越界值必须被钳制。</b>手改配置文件可能写入负高度或超大值，
///     直接采用会让界面布局错乱。
///   </item>
/// </list>
/// </remarks>
public sealed class AppSettingsStore
{
    /// <summary>日志面板高度的允许区间（像素）。</summary>
    /// <remarks>
    /// 下限 96：低于此值连一行日志都显示不全，拖到那么小没有意义。
    /// 上限 640：再高就挤占了主内容区，用户应当改用最大化而不是继续拖。
    /// </remarks>
    public const double MinLogPanelHeight = 96;

    /// <inheritdoc cref="MinLogPanelHeight"/>
    public const double MaxLogPanelHeight = 640;

    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,

        // 枚举写成字符串：数字在配置文件里无法自解释，
        // 人工排查"主题为什么不对"时看不出 0/1/2 的含义。
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    /// <summary>
    /// 使用默认位置（<c>%LOCALAPPDATA%\LinkForge\settings.json</c>）创建设置存储。
    /// </summary>
    public AppSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinkForge",
            FileName))
    {
    }

    /// <summary>
    /// 使用指定文件路径创建设置存储（供测试注入临时目录）。
    /// </summary>
    /// <param name="filePath">设置文件完整路径。</param>
    public AppSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>设置文件完整路径（诊断与测试用）。</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 读取设置；文件不存在或内容损坏时返回默认值。
    /// </summary>
    /// <returns>已钳制的设置对象；永不为 null。</returns>
    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);

            if (settings is null)
            {
                QuarantineCorruptFile();
                return new AppSettings();
            }

            return Normalize(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 设置文件损坏不应阻断启动：保留现场并回退默认值。
            QuarantineCorruptFile();
            return new AppSettings();
        }
    }

    /// <summary>
    /// 保存设置（原子写入）。
    /// </summary>
    /// <param name="settings">待保存的设置。</param>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = Normalize(settings);
        normalized.SchemaVersion = AppSettings.CurrentSchemaVersion;

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _filePath + ".tmp";

        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, SerializerOptions));

        // File.Move(overwrite) 在 NTFS 上是替换语义，不会留下半写文件。
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// 把越界值钳制到合法区间。
    /// </summary>
    /// <remarks>
    /// 只钳制"有明确物理含义"的数值。
    /// 主题偏好枚举若取到未定义值，回退到"跟随系统"而不是抛异常 ——
    /// 一个手改坏的字段不该让整个应用起不来。
    /// </remarks>
    private static AppSettings Normalize(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.ThemePreference))
        {
            settings.ThemePreference = LfThemePreference.FollowSystem;
        }

        settings.LogPanelHeight = Math.Clamp(
            settings.LogPanelHeight,
            MinLogPanelHeight,
            MaxLogPanelHeight);

        return settings;
    }

    /// <summary>
    /// 把损坏的设置文件改名保留，供事后排查。
    /// </summary>
    private void QuarantineCorruptFile()
    {
        try
        {
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(_filePath, $"{_filePath}.corrupt-{stamp}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 连改名都失败（文件被占用等）时只能放弃保留，
            // 但不能因此让调用方拿到异常 —— 启动流程不能被设置文件拖垮。
        }
    }
}