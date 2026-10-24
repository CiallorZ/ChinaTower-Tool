using System.Globalization;

namespace LinkForge.Adapters.Security;

/// <summary>端口列表与范围解析。</summary>
/// <remarks>
/// 只解析用户显式输入的列表或闭区间；不提供网段、全端口或自动扩展。
/// 上限由调用方传入，避免 UI、导出与适配器对“允许扫多少端口”各有一套规则。
/// </remarks>
public static class SecurityPortParser
{
    private static readonly char[] Separators = [',', ';', '，', '；', '、', ' ', '\t', '\r', '\n'];

    /// <summary>解析端口列表。</summary>
    public static bool TryParse(
        string? text,
        int maxPorts,
        out IReadOnlyList<int> ports,
        out string? error)
    {
        ports = [];
        error = null;

        if (maxPorts <= 0)
        {
            error = "端口数量上限必须大于 0。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请输入至少一个端口，例如 80,443,554。";
            return false;
        }

        var parsed = new SortedSet<int>();

        foreach (var token in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = token.Trim();

            if (value.Length == 0)
            {
                continue;
            }

            var dash = value.IndexOf('-', StringComparison.Ordinal);

            if (dash < 0)
            {
                if (!TryParsePort(value, out var port))
                {
                    error = $"无效端口：{value}。端口必须是 1–65535 的整数。";
                    return false;
                }

                parsed.Add(port);
            }
            else
            {
                if (dash == 0 || dash == value.Length - 1)
                {
                    error = $"无效端口范围：{value}。请使用“起始-结束”格式。";
                    return false;
                }

                if (!TryParsePort(value[..dash], out var start)
                    || !TryParsePort(value[(dash + 1)..], out var end))
                {
                    error = $"无效端口范围：{value}。范围两端必须是 1–65535 的整数。";
                    return false;
                }

                if (end < start)
                {
                    error = $"无效端口范围：{value}。结束端口不能小于起始端口。";
                    return false;
                }

                for (var port = start; port <= end; port++)
                {
                    parsed.Add(port);

                    if (parsed.Count > maxPorts)
                    {
                        error = $"端口范围最多包含 {maxPorts} 个端口，请缩小检查范围。";
                        ports = [];
                        return false;
                    }
                }
            }

            if (parsed.Count > maxPorts)
            {
                error = $"端口范围最多包含 {maxPorts} 个端口，请缩小检查范围。";
                ports = [];
                return false;
            }
        }

        if (parsed.Count == 0)
        {
            error = "请输入至少一个有效端口。";
            return false;
        }

        ports = parsed.ToArray();
        return true;
    }

    /// <summary>把端口列表格式化为稳定、可复制的文本。</summary>
    public static string Format(IEnumerable<int> ports) =>
        string.Join(",", ports.Distinct().Order().Select(static port => port.ToString(CultureInfo.InvariantCulture)));

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
        && port is >= 1 and <= 65535;
}
