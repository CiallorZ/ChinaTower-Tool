using System.Globalization;
using System.Text;
using LinkForge.Domain.Assets;

namespace LinkForge.Application.Assets;

/// <summary>
/// CSV 一行的问题描述（错误或警告）。
/// </summary>
/// <param name="LineNumber">文件中的物理行号（1 起，含表头）。</param>
/// <param name="Message">面向用户的说明。</param>
public readonly record struct AssetCsvIssue(int LineNumber, string Message)
{
    /// <summary>显示为"第 N 行：说明"。</summary>
    public override string ToString() => $"第 {LineNumber} 行：{Message}";
}

/// <summary>
/// CSV 导入结果。
/// </summary>
/// <remarks>
/// 刻意把"可导入的行"、"被拒绝的行"、"需要提醒但不影响导入的行"分开：
/// 界面据此如实区分<b>成功 / 部分成功 / 失败</b>三种状态（DEV_STANDARDS §3.4），
/// 而不是把它们都压成一个"导入完成了"。
/// </remarks>
public sealed record AssetCsvImportResult
{
    /// <summary>解析成功、可以写入存储的资产（未含 <c>UpdatedAt</c>，由存储层填写）。</summary>
    public required IReadOnlyList<Asset> Assets { get; init; }

    /// <summary>被拒绝的行。</summary>
    public required IReadOnlyList<AssetCsvIssue> Errors { get; init; }

    /// <summary>已接受但需提醒的行（例如类型无法识别、凭据被剥离）。</summary>
    public required IReadOnlyList<AssetCsvIssue> Warnings { get; init; }

    /// <summary>致命错误（编码不可解析、缺少必需列）。非空时 <see cref="Assets"/> 必为空。</summary>
    public string? FatalError { get; init; }

    /// <summary>是否因致命错误而整体失败。</summary>
    public bool HasFatalError => FatalError is not null;

    /// <summary>是否部分成功（既有可用行又有被拒绝行）。</summary>
    public bool IsPartial => !HasFatalError && Errors.Count > 0 && Assets.Count > 0;
}

/// <summary>
/// 资产 CSV 编解码（LF-004，PRD §3.2）。
/// </summary>
/// <remarks>
/// <para>
/// 纯离线转换，不访问文件系统与网络：文件对话框与落盘由 App 层的服务负责，
/// 因此本类可以在没有 UI 的环境里被完整测试。
/// </para>
/// <para>
/// <b>凭据边界（ADR-008）。</b>CSV 里只允许出现秘密的<i>引用键</i>。
/// 若 <c>host</c> 列里内嵌了 <c>user:password@host</c>（含粘贴整条 RTSP/HTTP URL 的常见情形），
/// 编解码器会<b>剥离并丢弃</b>凭据部分，只保留主机与端口，并给出明确提醒 ——
/// 明文密码不会被写进内存模型、数据库或导出文件中的任何位置。
/// </para>
/// <para>
/// <b>导出编码。</b>始终写 UTF-8 <b>带 BOM</b>：中文版 Excel 双击打开无 BOM 的 UTF-8 CSV
/// 会把中文显示成乱码，而带 BOM 的 CSV 在 Excel、WPS、文本编辑器里都能正确显示。
/// </para>
/// </remarks>
public static class AssetCsvCodec
{
    /// <summary>资产标识列（导出含此列，导入按它更新已有资产）。</summary>
    public const string IdColumn = "id";

    /// <summary>名称列（必填）。</summary>
    public const string NameColumn = "name";

    /// <summary>主机列（必填）。</summary>
    public const string HostColumn = "host";

    /// <summary>端口列。</summary>
    public const string PortColumn = "port";

    /// <summary>类型列。</summary>
    public const string KindColumn = "kind";

    /// <summary>厂商列。</summary>
    public const string VendorColumn = "vendor";

    /// <summary>标签列（多个标签用分号分隔）。</summary>
    public const string TagsColumn = "tags";

    /// <summary>凭据引用键列。</summary>
    public const string CredentialColumn = "credential";

    /// <summary>备注列。</summary>
    public const string NoteColumn = "note";

    /// <summary>导入文件大小上限（字节）。</summary>
    /// <remarks>
    /// 2 MiB 足以容纳数万条资产；设上限是因为"拖进来一个 200 MB 的文件"
    /// 会把内存和 UI 一起拖死，而这类操作在界面里无法被取消。
    /// </remarks>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>导出时的列顺序（写死顺序以保证 diff 可读）。</summary>
    private static readonly string[] ExportColumns =
    [
        IdColumn, NameColumn, HostColumn, PortColumn, KindColumn,
        VendorColumn, TagsColumn, CredentialColumn, NoteColumn
    ];

    /// <summary>
    /// 把资产导出为 UTF-8（带 BOM）的 CSV 字节。
    /// </summary>
    /// <param name="assets">资产集合。</param>
    /// <returns>可直接落盘的字节。</returns>
    public static byte[] Write(IReadOnlyList<Asset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var builder = new StringBuilder();
        WriteRow(builder, ExportColumns);

        foreach (var asset in assets)
        {
            WriteRow(builder,
            [
                asset.Id,
                asset.Name,
                asset.Host,
                asset.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                asset.Kind.ToString(),
                asset.Vendor ?? string.Empty,
                string.Join(';', asset.Tags),
                asset.CredentialReference ?? string.Empty,
                asset.Note ?? string.Empty
            ]);
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    /// <summary>
    /// 解析 CSV 字节。
    /// </summary>
    /// <param name="bytes">文件字节（允许带 BOM）。</param>
    /// <param name="projectId">这些资产归属的项目标识。</param>
    /// <returns>导入结果；永不抛异常，失败以 <see cref="AssetCsvImportResult.FatalError"/> 表达。</returns>
    public static AssetCsvImportResult Read(byte[] bytes, string projectId)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var empty = new AssetCsvImportResult
        {
            Assets = [],
            Errors = [],
            Warnings = []
        };

        if (bytes.Length == 0)
        {
            return empty with { FatalError = "文件为空，没有可导入的内容。" };
        }

        if (bytes.Length > MaxFileBytes)
        {
            return empty with
            {
                FatalError = $"文件为 {bytes.Length / 1024} KiB，超过 {MaxFileBytes / 1024} KiB 上限。请拆分后再导入。"
            };
        }

        if (!TryDecode(bytes, out var text, out var decodeError))
        {
            return empty with { FatalError = decodeError };
        }

        return ReadText(text, projectId);
    }

    /// <summary>
    /// 解析纯文本 CSV（已解码）。对外暴露是为了让测试可以直接给文本，而不必先造字节。
    /// </summary>
    /// <param name="text">CSV 文本。</param>
    /// <param name="projectId">资产归属项目。</param>
    /// <returns>导入结果。</returns>
    public static AssetCsvImportResult ReadText(string text, string projectId)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var records = ParseRecords(text);

        if (records.Count == 0)
        {
            return new AssetCsvImportResult
            {
                Assets = [],
                Errors = [],
                Warnings = [],
                FatalError = "文件为空，没有可导入的内容。"
            };
        }

        var header = records[0];
        var columns = BuildColumnMap(header.Fields, out var unknownColumns);

        if (header.Fields.All(static f => string.IsNullOrWhiteSpace(f)))
        {
            return new AssetCsvImportResult
            {
                Assets = [],
                Errors = [],
                Warnings = [],
                FatalError = "文件里没有可识别的表头（可能只有空行）。"
            };
        }

        if (!columns.TryGetValue(NameColumn, out _) || !columns.TryGetValue(HostColumn, out _))
        {
            return new AssetCsvImportResult
            {
                Assets = [],
                Errors = [],
                Warnings = [],
                FatalError = $"表头缺少必需列。需要至少包含 {NameColumn} 与 {HostColumn} 两列，实际表头：{string.Join(", ", header.Fields)}"
            };
        }

        var assets = new List<Asset>();
        var errors = new List<AssetCsvIssue>();
        var warnings = new List<AssetCsvIssue>();

        if (unknownColumns.Count > 0)
        {
            warnings.Add(new AssetCsvIssue(
                header.LineNumber,
                $"以下列未被识别，其内容不会导入：{string.Join(", ", unknownColumns)}"));
        }

        foreach (var record in records.Skip(1))
        {
            if (record.Fields.All(static f => string.IsNullOrWhiteSpace(f)))
            {
                // 末尾换行与用户手工留的空行都会产生这种记录，跳过而不是报错。
                continue;
            }

            var asset = TryBuildAsset(record, columns, projectId, warnings, errors);

            if (asset is not null)
            {
                assets.Add(asset);
            }
        }

        return new AssetCsvImportResult
        {
            Assets = assets,
            Errors = errors,
            Warnings = warnings
        };
    }

    private static Asset? TryBuildAsset(
        CsvRecord record,
        IReadOnlyDictionary<string, int> columns,
        string projectId,
        List<AssetCsvIssue> warnings,
        List<AssetCsvIssue> errors)
    {
        var name = Field(record, columns, NameColumn).Trim();
        var rawHost = Field(record, columns, HostColumn).Trim();

        if (name.Length == 0)
        {
            errors.Add(new AssetCsvIssue(record.LineNumber, $"{NameColumn} 为空，资产名称是必填项。"));
            return null;
        }

        if (name.Length > AssetCsvLimits.NameLength)
        {
            errors.Add(new AssetCsvIssue(
                record.LineNumber,
                $"资产名称超过 {AssetCsvLimits.NameLength} 字符上限。"));
            return null;
        }

        if (rawHost.Length == 0)
        {
            errors.Add(new AssetCsvIssue(record.LineNumber, $"{HostColumn} 为空，主机是必填项。"));
            return null;
        }

        if (!TryNormalizeHost(rawHost, out var host, out var hostPort, out var credentialsStripped, out var hostError))
        {
            errors.Add(new AssetCsvIssue(record.LineNumber, hostError));
            return null;
        }

        if (credentialsStripped)
        {
            warnings.Add(new AssetCsvIssue(
                record.LineNumber,
                "已丢弃内嵌在主机里的用户名与密码（不会写入任何位置）。请为该资产单独配置密码引用后再使用。"));
        }

        if (host.Length > AssetCsvLimits.HostLength)
        {
            errors.Add(new AssetCsvIssue(
                record.LineNumber,
                $"主机超过 {AssetCsvLimits.HostLength} 字符上限。"));
            return null;
        }

        int? port = hostPort;
        var rawPort = Field(record, columns, PortColumn).Trim();

        if (rawPort.Length > 0)
        {
            if (!int.TryParse(rawPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
                || parsedPort is < 1 or > 65535)
            {
                errors.Add(new AssetCsvIssue(
                    record.LineNumber,
                    $"{PortColumn} “{rawPort}” 不是 1-65535 之间的端口号。"));
                return null;
            }

            port = parsedPort;
        }

        var kindText = Field(record, columns, KindColumn).Trim();
        var kind = AssetKind.Unknown;

        if (kindText.Length > 0 && !AssetKindNames.TryParse(kindText, out kind))
        {
            warnings.Add(new AssetCsvIssue(
                record.LineNumber,
                $"无法识别的资产类型 “{kindText}”，已按“未知”导入。"));
            kind = AssetKind.Unknown;
        }

        var vendor = Truncate(
            Field(record, columns, VendorColumn).Trim(), AssetCsvLimits.VendorLength, record, warnings, "厂商");
        var note = Truncate(
            Field(record, columns, NoteColumn).Trim(), AssetCsvLimits.NoteLength, record, warnings, "备注");

        var tags = ParseTags(Field(record, columns, TagsColumn), record, warnings);

        var credentialText = Field(record, columns, CredentialColumn).Trim();

        if (credentialText.Length > 0 && !AssetCredentialReference.IsValidKey(credentialText))
        {
            // 凭据键格式非法：这是"用户可能把密码写进来了"的信号，宁可拒绝该行。
            errors.Add(new AssetCsvIssue(
                record.LineNumber,
                $"{CredentialColumn} 列只接受秘密引用键（如 asset.cam-01.password），不接受明文密码；该行已跳过。"));
            return null;
        }

        string? credential = null;

        if (credentialText.Length > 0)
        {
            credential = credentialText;

            warnings.Add(new AssetCsvIssue(
                record.LineNumber,
                $"{CredentialColumn} 只是引用键，本身不含密码；实际密码需在凭据存储中按该键配置。"));
        }

        var id = Field(record, columns, IdColumn).Trim();

        if (id.Length > 0 && !IsValidAssetId(id))
        {
            errors.Add(new AssetCsvIssue(
                record.LineNumber,
                $"{IdColumn} “{id}” 不是合法的资产标识；请留空以新建资产。"));
            return null;
        }

        return new Asset
        {
            Id = id.Length > 0 ? id : Guid.NewGuid().ToString("N"),
            ProjectId = projectId,
            Name = name,
            Host = host,
            Port = port,
            Kind = kind,
            Vendor = vendor.Length > 0 ? vendor : null,
            Tags = tags,
            CredentialReference = credential,
            Note = note.Length > 0 ? note : null
        };
    }

    /// <summary>
    /// 判断资产标识是否合法。
    /// </summary>
    /// <remarks>
    /// 与凭据引用键共用同一套白名单字符集，但长度上限更短（主键列长 64），
    /// 且不允许 <c>:</c> —— 冒号是凭据键用来分隔命名空间前缀的，
    /// 放进资产标识只会让"这个 id 从哪来"更难回答。
    /// </remarks>
    private static bool IsValidAssetId(string value)
    {
        if (value.Length is 0 or > AssetCsvLimits.AssetIdLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string Truncate(
        string value,
        int maxLength,
        CsvRecord record,
        List<AssetCsvIssue> warnings,
        string label)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        warnings.Add(new AssetCsvIssue(
            record.LineNumber,
            $"{label}超过 {maxLength} 字符，已截断。"));

        return value[..maxLength];
    }

    private static IReadOnlyList<string> ParseTags(
        string raw,
        CsvRecord record,
        List<AssetCsvIssue> warnings)
    {
        var normalized = AssetTagText.Normalize(raw);

        if (normalized.Count == 0)
        {
            return [];
        }

        var tags = new List<string>();
        var truncated = 0;

        foreach (var candidate in normalized)
        {
            if (tags.Count >= AssetCsvLimits.TagCount)
            {
                truncated++;
                continue;
            }

            var tag = candidate;

            if (tag.Length > AssetCsvLimits.TagLength)
            {
                tag = tag[..AssetCsvLimits.TagLength];
                truncated++;
            }

            tags.Add(tag);
        }

        if (truncated > 0)
        {
            warnings.Add(new AssetCsvIssue(
                record.LineNumber,
                $"标签超过 {AssetCsvLimits.TagCount} 个或单个标签超过 {AssetCsvLimits.TagLength} 字符，已截断 {truncated} 处。"));
        }

        return tags;
    }

    /// <summary>
    /// 归一化主机：从粘贴的 URL 中提取主机与端口，并剥离内嵌凭据。
    /// </summary>
    /// <param name="raw">原始文本。</param>
    /// <param name="host">归一化后的主机。</param>
    /// <param name="port">URL 中出现的端口；未出现时为 null。</param>
    /// <param name="credentialsStripped">是否丢弃过内嵌凭据。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>是否成功。</returns>
    private static bool TryNormalizeHost(
        string raw,
        out string host,
        out int? port,
        out bool credentialsStripped,
        out string error)
    {
        host = string.Empty;
        port = null;
        credentialsStripped = false;
        error = string.Empty;

        var working = raw.Trim();

        if (working.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(working, UriKind.Absolute, out var uri))
            {
                error = $"主机 “{raw}” 看起来是一条 URL，但无法解析。请填写 IP、域名，或完整的 URL。";
                return false;
            }

            credentialsStripped = !string.IsNullOrEmpty(uri.UserInfo);
            host = uri.Host;

            if (!uri.IsDefaultPort && uri.Port > 0)
            {
                port = uri.Port;
            }

            if (host.Length == 0)
            {
                error = $"从 “{raw}” 中解析不出主机名。";
                return false;
            }

            return true;
        }

        // 裸 user:pass@host 形态（SDK 日志与设备标签里很常见）。
        var atIndex = working.LastIndexOf('@');

        if (atIndex >= 0)
        {
            credentialsStripped = true;
            working = working[(atIndex + 1)..];
        }

        if (working.Length == 0)
        {
            error = $"主机 “{raw}” 去掉凭据后为空。";
            return false;
        }

        if (working.Any(char.IsWhiteSpace))
        {
            error = $"主机 “{raw}” 含空白字符，不是有效的 IP 或域名。";
            return false;
        }

        if (working.Contains('/', StringComparison.Ordinal)
            || working.Contains('\\', StringComparison.Ordinal)
            || working.Contains('?', StringComparison.Ordinal)
            || working.Contains('#', StringComparison.Ordinal))
        {
            error = $"主机 “{raw}” 含路径或查询串。请只填写主机，路径属于连接配置而不是资产。";
            return false;
        }

        // host:port 形态。IPv6 字面量带方括号，冒号不构成"主机:端口"。
        if (working.StartsWith('['))
        {
            var closing = working.IndexOf(']');

            if (closing < 0)
            {
                error = $"IPv6 主机 “{raw}” 缺少收尾的 “]”。";
                return false;
            }

            host = working[..(closing + 1)];
            var remainder = working[(closing + 1)..];

            if (remainder.Length == 0)
            {
                return true;
            }

            if (!remainder.StartsWith(':')
                || !TryParsePort(remainder[1..], out var ipv6Port, out error))
            {
                error = error.Length > 0 ? error : $"主机 “{raw}” 中的端口写法无效。";
                return false;
            }

            port = ipv6Port;
            return true;
        }

        var colon = working.LastIndexOf(':');

        if (colon >= 0 && working.IndexOf(':') == colon)
        {
            host = working[..colon];
            var portText = working[(colon + 1)..];

            if (host.Length == 0)
            {
                error = $"主机 “{raw}” 缺少冒号前的主机名。";
                return false;
            }

            if (!TryParsePort(portText, out var parsed, out error))
            {
                return false;
            }

            port = parsed;
            return true;
        }

        host = working;
        return true;
    }

    private static bool TryParsePort(string text, out int port, out string error)
    {
        error = string.Empty;
        port = 0;

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > 65535)
        {
            error = $"端口 “{text}” 不是 1-65535 之间的数字。";
            return false;
        }

        port = parsed;
        return true;
    }

    private static IReadOnlyDictionary<string, int> BuildColumnMap(
        IReadOnlyList<string> header,
        out IReadOnlyList<string> unknownColumns)
    {
        var known = new HashSet<string>(ExportColumns, StringComparer.Ordinal);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknown = new List<string>();

        for (var index = 0; index < header.Count; index++)
        {
            var normalized = NormalizeColumnName(header[index]);

            if (normalized.Length == 0)
            {
                continue;
            }

            if (known.Contains(normalized))
            {
                // 重复列只认第一次出现的那一列：后出现的一列取值来源不确定，
                // 静默覆盖会让"我明明改了第 2 个 host 列"变得无法解释。
                map.TryAdd(normalized, index);
            }
            else
            {
                unknown.Add(header[index].Trim());
            }
        }

        unknownColumns = unknown;
        return map;
    }

    /// <summary>列名归一化：去空白、去下划线、转小写。</summary>
    private static string NormalizeColumnName(string name) =>
        new string(name
            .Where(static c => !char.IsWhiteSpace(c) && c != '_')
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string Field(CsvRecord record, IReadOnlyDictionary<string, int> columns, string column) =>
        columns.TryGetValue(column, out var index) && index < record.Fields.Count
            ? record.Fields[index]
            : string.Empty;

    private static void WriteRow(StringBuilder builder, IReadOnlyList<string> fields)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(Escape(fields[index]));
        }

        // CRLF：RFC 4180 规定行结束符，也是 Excel 在 Windows 上的预期。
        builder.Append("\r\n");
    }

    private static string Escape(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var needsQuotes = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;

        if (!needsQuotes)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static bool TryDecode(byte[] bytes, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;

        var offset = 0;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            offset = 3;
        }

        try
        {
            // 严格解码：非法字节直接失败，而不是产出带 U+FFFD 的"看起来能导入"的乱码。
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, offset, bytes.Length - offset);
            return true;
        }
        catch (DecoderFallbackException)
        {
            error = "文件不是有效的 UTF-8 文本（可能是 GBK/GB18030 编码）。"
                + "请在 Excel 中另存为“CSV UTF-8（逗号分隔）”后重新导入。";
            return false;
        }
    }

    /// <summary>一条 CSV 记录及其起始物理行号。</summary>
    private readonly record struct CsvRecord(int LineNumber, List<string> Fields);

    /// <summary>
    /// RFC 4180 解析：支持引号字段、字段内换行、<c>""</c> 转义与 CRLF/LF/CR 混用。
    /// </summary>
    /// <remarks>
    /// 手写而不是用现成库：本项目对"导入的每一行发生了什么"必须可解释，
    /// 而第三方 CSV 库的容错行为（跳过坏行、静默修复）恰恰会掩盖数据问题。
    /// </remarks>
    private static List<CsvRecord> ParseRecords(string text)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        var index = 0;

        while (index < text.Length)
        {
            var ch = text[index];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index += 2;
                        continue;
                    }

                    inQuotes = false;
                    index++;
                    continue;
                }

                if (ch is '\r' or '\n')
                {
                    // 引号内的换行属于字段内容，但要计入物理行号，
                    // 否则后续"第 N 行有错"会指向错误的行。
                    //
                    // \r\n 是一次换行而不是两次：分别计数会让"第 N 行"整体偏移，
                    // 而用户是照着 Excel 的行号去改文件的（本缺陷由本地测试发现）。
                    if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        field.Append("\r\n");
                        line++;
                        index += 2;
                        continue;
                    }

                    field.Append(ch);
                    line++;
                    index++;
                    continue;
                }

                field.Append(ch);
                index++;
                continue;
            }

            switch (ch)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    index++;
                    continue;

                case '"':
                    // 未加引号字段中间出现的引号：按字面量处理，不尝试修复。
                    field.Append(ch);
                    index++;
                    continue;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    index++;
                    continue;

                case '\r' or '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(new CsvRecord(recordLine, fields));
                    fields = [];

                    if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    index++;
                    line++;
                    recordLine = line;
                    continue;

                default:
                    field.Append(ch);
                    index++;
                    continue;
            }
        }

        if (inQuotes)
        {
            // 未闭合的引号：把已读内容作为最后一条记录交出，
            // 但内容已确定不可信 —— 由调用方按"表头/字段缺失"报错。
            fields.Add(field.ToString());
            records.Add(new CsvRecord(recordLine, fields));
            return records;
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(new CsvRecord(recordLine, fields));
        }

        return records;
    }
}
