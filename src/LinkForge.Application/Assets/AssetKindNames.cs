using LinkForge.Domain.Assets;

namespace LinkForge.Application.Assets;

/// <summary>
/// 资产类型的显示名与解析（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意放在 Application 而不是 UI：CSV 导入要靠它把中文类型名解析成枚举，
/// 界面下拉框也要用它显示同一批名称。若两边各写一份，
/// "导出成中文别名、导入却认不出来"是迟早的事。
/// </para>
/// <para>
/// 导出始终写<b>枚举名</b>（如 <c>Camera</c>），只保证机器可稳定往返；
/// 中文名只是导入时的便利别名，不作为导出格式的一部分。
/// </para>
/// </remarks>
public static class AssetKindNames
{
    /// <summary>界面与导入都使用的显示顺序。</summary>
    public static IReadOnlyList<AssetKind> All { get; } =
    [
        AssetKind.Camera,
        AssetKind.Recorder,
        AssetKind.VideoPlatform,
        AssetKind.IotDevice,
        AssetKind.Gateway,
        AssetKind.Broker,
        AssetKind.Host,
        AssetKind.Unknown
    ];

    /// <summary>
    /// 取得资产类型的中文显示名。
    /// </summary>
    /// <param name="kind">资产类型。</param>
    /// <returns>显示名；未定义取值返回"未知"。</returns>
    public static string Display(AssetKind kind) => kind switch
    {
        AssetKind.Camera => "摄像机",
        AssetKind.Recorder => "录像机",
        AssetKind.VideoPlatform => "视频平台",
        AssetKind.IotDevice => "IoT 设备",
        AssetKind.Gateway => "IoT 网关",
        AssetKind.Broker => "MQTT Broker",
        AssetKind.Host => "通用主机",
        _ => "未知"
    };

    /// <summary>
    /// 解析资产类型：接受枚举名、中文显示名，以及若干现场常用写法。
    /// </summary>
    /// <param name="text">待解析文本。</param>
    /// <param name="kind">解析结果。</param>
    /// <returns>是否解析成功。</returns>
    /// <remarks>
    /// 只在大小写与全半角空白上做归一化，不做模糊匹配：
    /// 把"摄像头1"这种写法猜成某个类型会让导入结果与用户的表格不一致。
    /// </remarks>
    public static bool TryParse(string? text, out AssetKind kind)
    {
        kind = AssetKind.Unknown;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();

        if (Enum.TryParse<AssetKind>(normalized, ignoreCase: true, out var parsed))
        {
            kind = parsed;
            return true;
        }

        foreach (var candidate in All)
        {
            var display = Display(candidate);

            if (string.Equals(normalized, display, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }

            // 现场表格里常见去掉空格的写法（"IoT设备"）。
            if (string.Equals(
                    normalized.Replace(" ", string.Empty),
                    display.Replace(" ", string.Empty),
                    StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        return Aliases.TryGetValue(normalized, out kind);
    }

    /// <summary>别名表：左为输入写法，右为资产类型。</summary>
    private static readonly Dictionary<string, AssetKind> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["camera"] = AssetKind.Camera,
        ["cctv"] = AssetKind.Camera,
        ["ipc"] = AssetKind.Camera,
        ["摄像头"] = AssetKind.Camera,
        ["球机"] = AssetKind.Camera,
        ["枪机"] = AssetKind.Camera,
        ["nvr"] = AssetKind.Recorder,
        ["dvr"] = AssetKind.Recorder,
        ["磁盘阵列"] = AssetKind.Recorder,
        ["平台"] = AssetKind.VideoPlatform,
        ["视频平台"] = AssetKind.VideoPlatform,
        ["gb28181"] = AssetKind.VideoPlatform,
        ["iot"] = AssetKind.IotDevice,
        ["传感设备"] = AssetKind.IotDevice,
        ["传感器"] = AssetKind.IotDevice,
        ["网关"] = AssetKind.Gateway,
        ["zigbee网关"] = AssetKind.Gateway,
        ["broker"] = AssetKind.Broker,
        ["mqtt"] = AssetKind.Broker,
        ["mqtt服务器"] = AssetKind.Broker,
        ["host"] = AssetKind.Host,
        ["server"] = AssetKind.Host,
        ["服务器"] = AssetKind.Host,
        ["主机"] = AssetKind.Host,
        ["unknown"] = AssetKind.Unknown,
        ["未知"] = AssetKind.Unknown
    };
}
