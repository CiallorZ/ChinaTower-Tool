using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkForge.Application.Assets;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;

namespace LinkForge.Application.Projects;

/// <summary>
/// 版本化项目包 JSON 编解码（LF-015）。
/// </summary>
/// <remarks>
/// <para>
/// 读取是严格的：未知字段、重复字段、未知 schema、未知枚举值、超限集合、重复 ID、
/// 跨项目引用以及任何看起来承载秘密的字段名都会整体拒绝。项目包是一个可交换文件，
/// 静默接受未知内容会让“这份文件到底改了什么”无法回答。
/// </para>
/// <para>
/// 写入是确定性的：环境按名称、变量按键名输出，资产按名称输出。
/// 同一份数据导出后应得到可比较的文本，便于人工审查和版本控制。
/// </para>
/// </remarks>
public sealed class ProjectPackageCodec
{
    /// <summary>项目包格式标识。</summary>
    public const string SchemaName = "linkforge.project-package";

    /// <summary>当前项目包版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>单个项目包文件字节上限。</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>环境数量上限。</summary>
    public const int MaxEnvironmentCount = 200;

    /// <summary>资产数量上限。</summary>
    public const int MaxAssetCount = 5000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly string[] SecretNameFragments =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "apikey",
        "appkey",
        "privatekey",
        "accesskey",
        "sessionid",
        "cookie",
        "authorization"
    ];

    /// <summary>
    /// 把真实存储记录编码为 UTF-8 JSON。
    /// </summary>
    /// <param name="project">项目。</param>
    /// <param name="environments">环境。</param>
    /// <param name="assets">资产。</param>
    /// <returns>无 BOM 的 UTF-8 JSON 字节。</returns>
    public byte[] Encode(
        Project project,
        IReadOnlyList<EnvironmentProfile> environments,
        IReadOnlyList<Asset> assets)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(environments);
        ArgumentNullException.ThrowIfNull(assets);

        var package = new ProjectPackage
        {
            Project = project,
            Environments = environments,
            Assets = assets
        };

        Validate(package);

        var dto = PackageDto.FromDomain(package);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, SerializerOptions);

        if (bytes.Length > MaxFileBytes)
        {
            throw new ProjectPackageFormatException(
                $"项目包超过 {MaxFileBytes / 1024 / 1024} MiB 上限，请减少资产、标签或环境变量后重试。");
        }

        return bytes;
    }

    /// <summary>
    /// 从 UTF-8 JSON 严格解析项目包。
    /// </summary>
    /// <param name="content">文件内容。</param>
    /// <returns>通过全部校验的项目包。</returns>
    public ProjectPackage Decode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            throw new ProjectPackageFormatException("项目包为空。");
        }

        if (content.Length > MaxFileBytes)
        {
            throw new ProjectPackageFormatException(
                $"项目包超过 {MaxFileBytes / 1024 / 1024} MiB 上限，已拒绝读取。");
        }

        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });

            InspectElement(document.RootElement, "$");

            var dto = JsonSerializer.Deserialize<PackageDto>(content, SerializerOptions)
                ?? throw new ProjectPackageFormatException("项目包根对象为空。");

            var package = dto.ToDomain();
            Validate(package);
            return package;
        }
        catch (ProjectPackageFormatException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ProjectPackageFormatException(
                $"项目包 JSON 无法解析：{ex.Message}",
                ex);
        }
        catch (ArgumentException ex)
        {
            throw new ProjectPackageFormatException(
                $"项目包字段无效：{ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// 校验项目包的跨记录不变量与字段上限。
    /// </summary>
    /// <param name="package">项目包。</param>
    public static void Validate(ProjectPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var project = package.Project
            ?? throw new ProjectPackageFormatException("项目包缺少项目记录。");

        ValidateId(project.Id, "项目");

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            throw new ProjectPackageFormatException("项目名称不能为空。");
        }

        if (project.Name.Length > 200)
        {
            throw new ProjectPackageFormatException("项目名称超过 200 字符上限。");
        }

        if (project.Description is { Length: > 1024 })
        {
            throw new ProjectPackageFormatException("项目描述超过 1024 字符上限。");
        }

        if (project.CreatedAt == default)
        {
            throw new ProjectPackageFormatException("项目创建时间无效。");
        }

        ValidateTimeout(project.DefaultTimeoutSeconds, "项目");

        if (package.Environments is null)
        {
            throw new ProjectPackageFormatException("项目包缺少 environments 数组。");
        }

        if (package.Assets is null)
        {
            throw new ProjectPackageFormatException("项目包缺少 assets 数组。");
        }

        if (package.Environments.Count > MaxEnvironmentCount)
        {
            throw new ProjectPackageFormatException(
                $"环境数量超过 {MaxEnvironmentCount} 条上限。");
        }

        if (package.Assets.Count > MaxAssetCount)
        {
            throw new ProjectPackageFormatException(
                $"资产数量超过 {MaxAssetCount} 条上限。");
        }

        ValidateEnvironments(project.Id, package.Environments);
        ValidateAssets(project.Id, package.Assets);
    }

    private static void ValidateEnvironments(
        string projectId,
        IReadOnlyList<EnvironmentProfile> environments)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeCount = 0;

        foreach (var environment in environments)
        {
            if (environment is null)
            {
                throw new ProjectPackageFormatException("环境数组包含 null 项。");
            }

            ValidateId(environment.Id, "环境");

            if (!ids.Add(environment.Id))
            {
                throw new ProjectPackageFormatException(
                    $"项目包中环境 ID “{environment.Id}” 重复。");
            }

            if (!string.Equals(environment.ProjectId, projectId, StringComparison.Ordinal))
            {
                throw new ProjectPackageFormatException(
                    $"环境 “{environment.Name}” 的 projectId 与包项目不一致。");
            }

            if (string.IsNullOrWhiteSpace(environment.Name))
            {
                throw new ProjectPackageFormatException("环境名称不能为空。");
            }

            if (environment.Name.Length > 200)
            {
                throw new ProjectPackageFormatException(
                    $"环境 “{environment.Name}” 的名称超过 200 字符上限。");
            }

            if (!names.Add(environment.Name))
            {
                throw new ProjectPackageFormatException(
                    $"项目包中环境名称 “{environment.Name}” 重复。");
            }

            if (environment.ProxyUrl is { Length: > 512 })
            {
                throw new ProjectPackageFormatException(
                    $"环境 “{environment.Name}” 的代理地址超过 512 字符上限。");
            }

            ValidateTimeout(environment.DefaultTimeoutSeconds, $"环境 “{environment.Name}”");

            if (environment.IsActive && ++activeCount > 1)
            {
                throw new ProjectPackageFormatException("同一项目最多只能有一个激活环境。");
            }

            ValidateEnvironmentVariables(environment);
        }
    }

    private static void ValidateEnvironmentVariables(EnvironmentProfile environment)
    {
        if (environment.Variables is null)
        {
            throw new ProjectPackageFormatException(
                $"环境 “{environment.Name}” 缺少 variables 对象。");
        }

        if (environment.Variables.Count > EnvironmentVariableText.Count)
        {
            throw new ProjectPackageFormatException(
                $"环境 “{environment.Name}” 的变量超过 {EnvironmentVariableText.Count} 条上限。");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in environment.Variables)
        {
            if (!seen.Add(pair.Key))
            {
                throw new ProjectPackageFormatException(
                    $"环境 “{environment.Name}” 的变量名 “{pair.Key}” 重复（大小写不敏感）。");
            }

            var parsed = EnvironmentVariableText.Parse($"{pair.Key}={pair.Value}");

            if (parsed.Errors.Count > 0)
            {
                throw new ProjectPackageFormatException(
                    $"环境 “{environment.Name}” 的变量 “{pair.Key}” 无效："
                    + parsed.Errors[0].Message);
            }

            if (parsed.Variables.Count != 1)
            {
                throw new ProjectPackageFormatException(
                    $"环境 “{environment.Name}” 的变量 “{pair.Key}” 无法往返解析。");
            }
        }
    }

    private static void ValidateAssets(
        string projectId,
        IReadOnlyList<Asset> assets)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var asset in assets)
        {
            if (asset is null)
            {
                throw new ProjectPackageFormatException("资产数组包含 null 项。");
            }

            ValidateId(asset.Id, "资产");

            if (!ids.Add(asset.Id))
            {
                throw new ProjectPackageFormatException(
                    $"项目包中资产 ID “{asset.Id}” 重复。");
            }

            if (!string.Equals(asset.ProjectId, projectId, StringComparison.Ordinal))
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的 projectId 与包项目不一致。");
            }

            if (string.IsNullOrWhiteSpace(asset.Name))
            {
                throw new ProjectPackageFormatException("资产名称不能为空。");
            }

            if (asset.Name.Length > AssetCsvLimits.NameLength)
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的名称超过 {AssetCsvLimits.NameLength} 字符上限。");
            }

            if (string.IsNullOrWhiteSpace(asset.Host))
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的主机不能为空。");
            }

            if (asset.Host.Length > AssetCsvLimits.HostLength)
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的主机超过 {AssetCsvLimits.HostLength} 字符上限。");
            }

            if (asset.Host.Any(char.IsWhiteSpace))
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的主机含空白字符。");
            }

            if (asset.Port is < 1 or > 65535)
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的端口必须在 1-65535 之间。");
            }

            if (!Enum.IsDefined(asset.Kind))
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的 kind 不是已支持的类型。");
            }

            if (asset.Vendor is { Length: > AssetCsvLimits.VendorLength })
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的厂商超过 {AssetCsvLimits.VendorLength} 字符上限。");
            }

            if (asset.Note is { Length: > AssetCsvLimits.NoteLength })
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的备注超过 {AssetCsvLimits.NoteLength} 字符上限。");
            }

            if (asset.Tags is null)
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 缺少 tags 数组。");
            }

            if (asset.Tags.Count > AssetCsvLimits.TagCount)
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的标签超过 {AssetCsvLimits.TagCount} 个上限。");
            }

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tag in asset.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    throw new ProjectPackageFormatException(
                        $"资产 “{asset.Name}” 含有空标签。");
                }

                if (tag.Length > AssetCsvLimits.TagLength)
                {
                    throw new ProjectPackageFormatException(
                        $"资产 “{asset.Name}” 的标签超过 {AssetCsvLimits.TagLength} 字符上限。");
                }

                if (!tags.Add(tag))
                {
                    throw new ProjectPackageFormatException(
                        $"资产 “{asset.Name}” 的标签 “{tag}” 重复。");
                }
            }

            if (!AssetCredentialReference.IsValidKey(asset.CredentialReference))
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{asset.Name}” 的凭据引用无效："
                    + AssetCredentialReference.InvalidReason());
            }
        }
    }

    private static void ValidateId(string? value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProjectPackageFormatException($"{kind} ID 不能为空。");
        }

        if (value.Length > AssetCsvLimits.AssetIdLength)
        {
            throw new ProjectPackageFormatException(
                $"{kind} ID 超过 {AssetCsvLimits.AssetIdLength} 字符上限。");
        }
    }

    private static void ValidateTimeout(int? seconds, string owner)
    {
        if (seconds is < 1 or > 3600)
        {
            throw new ProjectPackageFormatException(
                $"{owner}的默认超时必须在 1-3600 秒之间。");
        }
    }

    private static void InspectElement(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ProjectPackageFormatException(
                        $"项目包对象 {path} 含重复字段 “{property.Name}”。");
                }

                if (LooksLikeSecretName(property.Name)
                    && !string.Equals(
                        property.Name,
                        "credentialReference",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ProjectPackageFormatException(
                        $"项目包字段 “{property.Name}” 看起来承载秘密；项目包只允许凭据引用键。");
                }

                InspectElement(property.Value, $"{path}.{property.Name}");
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;

            foreach (var item in element.EnumerateArray())
            {
                InspectElement(item, $"{path}[{index}]");
                index++;
            }
        }
    }

    private static bool LooksLikeSecretName(string name)
    {
        var normalized = new StringBuilder(name.Length);

        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                normalized.Append(char.ToLowerInvariant(ch));
            }
        }

        var compact = normalized.ToString();

        foreach (var fragment in SecretNameFragments)
        {
            if (compact.Equals(fragment, StringComparison.Ordinal)
                || compact.EndsWith(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class PackageDto
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("project")]
        public ProjectDto? Project { get; init; }

        [JsonPropertyName("environments")]
        public List<EnvironmentDto>? Environments { get; init; }

        [JsonPropertyName("assets")]
        public List<AssetDto>? Assets { get; init; }

        public static PackageDto FromDomain(ProjectPackage package) =>
            new()
            {
                Schema = SchemaName,
                SchemaVersion = CurrentSchemaVersion,
                Project = ProjectDto.FromDomain(package.Project),
                Environments = package.Environments
                    .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(EnvironmentDto.FromDomain)
                    .ToList(),
                Assets = package.Assets
                    .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(AssetDto.FromDomain)
                    .ToList()
            };

        public ProjectPackage ToDomain()
        {
            if (!string.Equals(Schema, SchemaName, StringComparison.Ordinal))
            {
                throw new ProjectPackageFormatException(
                    $"不是 LinkForge 项目包：schema 应为 “{SchemaName}”。");
            }

            if (SchemaVersion != CurrentSchemaVersion)
            {
                throw new ProjectPackageFormatException(
                    $"不支持的项目包版本 {SchemaVersion}；当前只支持 {CurrentSchemaVersion}。");
            }

            if (Project is null)
            {
                throw new ProjectPackageFormatException("项目包缺少 project 对象。");
            }

            if (Environments is null)
            {
                throw new ProjectPackageFormatException("项目包缺少 environments 数组。");
            }

            if (Assets is null)
            {
                throw new ProjectPackageFormatException("项目包缺少 assets 数组。");
            }

            var project = Project.ToDomain();

            return new ProjectPackage
            {
                Project = project,
                Environments = Environments.Select(item => item.ToDomain(project.Id)).ToArray(),
                Assets = Assets.Select(item => item.ToDomain(project.Id)).ToArray()
            };
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ProjectDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("defaultTimeoutSeconds")]
        public int? DefaultTimeoutSeconds { get; init; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; init; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; init; }

        public static ProjectDto FromDomain(Project project) =>
            new()
            {
                Id = project.Id,
                Name = project.Name,
                Description = project.Description,
                DefaultTimeoutSeconds = project.DefaultTimeoutSeconds,
                CreatedAt = project.CreatedAt,
                UpdatedAt = project.UpdatedAt
            };

        public Project ToDomain() =>
            new()
            {
                Id = Id ?? throw new ProjectPackageFormatException("项目 ID 缺失。"),
                Name = Name ?? throw new ProjectPackageFormatException("项目名称缺失。"),
                Description = Description,
                SchemaVersion = Project.CurrentSchemaVersion,
                DefaultTimeoutSeconds = DefaultTimeoutSeconds,
                CreatedAt = CreatedAt
                    ?? throw new ProjectPackageFormatException("项目创建时间缺失。"),
                UpdatedAt = UpdatedAt
            };
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class EnvironmentDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("variables")]
        public Dictionary<string, string>? Variables { get; init; }

        [JsonPropertyName("proxyUrl")]
        public string? ProxyUrl { get; init; }

        [JsonPropertyName("defaultTimeoutSeconds")]
        public int? DefaultTimeoutSeconds { get; init; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; init; }

        public static EnvironmentDto FromDomain(EnvironmentProfile environment) =>
            new()
            {
                Id = environment.Id,
                Name = environment.Name,
                Variables = environment.Variables
                    .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.Ordinal),
                ProxyUrl = environment.ProxyUrl,
                DefaultTimeoutSeconds = environment.DefaultTimeoutSeconds,
                IsActive = environment.IsActive
            };

        public EnvironmentProfile ToDomain(string projectId) =>
            new()
            {
                Id = Id ?? throw new ProjectPackageFormatException("环境 ID 缺失。"),
                ProjectId = projectId,
                Name = Name ?? throw new ProjectPackageFormatException("环境名称缺失。"),
                Variables = Variables is null
                    ? throw new ProjectPackageFormatException("环境 variables 缺失。")
                    : new Dictionary<string, string>(
                        Variables,
                        StringComparer.OrdinalIgnoreCase),
                ProxyUrl = ProxyUrl,
                DefaultTimeoutSeconds = DefaultTimeoutSeconds,
                IsActive = IsActive
            };
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class AssetDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("host")]
        public string? Host { get; init; }

        [JsonPropertyName("port")]
        public int? Port { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("vendor")]
        public string? Vendor { get; init; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; init; }

        [JsonPropertyName("credentialReference")]
        public string? CredentialReference { get; init; }

        [JsonPropertyName("note")]
        public string? Note { get; init; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; init; }

        public static AssetDto FromDomain(Asset asset) =>
            new()
            {
                Id = asset.Id,
                Name = asset.Name,
                Host = asset.Host,
                Port = asset.Port,
                Kind = asset.Kind.ToString(),
                Vendor = asset.Vendor,
                Tags = asset.Tags.ToList(),
                CredentialReference = asset.CredentialReference,
                Note = asset.Note,
                UpdatedAt = asset.UpdatedAt
            };

        public Asset ToDomain(string projectId)
        {
            if (!Enum.TryParse<AssetKind>(Kind, ignoreCase: false, out var kind))
            {
                throw new ProjectPackageFormatException(
                    $"资产 “{Name ?? Id ?? "(未命名)"}” 的 kind “{Kind}” 不受支持。");
            }

            return new Asset
            {
                Id = Id ?? throw new ProjectPackageFormatException("资产 ID 缺失。"),
                ProjectId = projectId,
                Name = Name ?? throw new ProjectPackageFormatException("资产名称缺失。"),
                Host = Host ?? throw new ProjectPackageFormatException("资产主机缺失。"),
                Port = Port,
                Kind = kind,
                Vendor = Vendor,
                Tags = Tags ?? throw new ProjectPackageFormatException("资产 tags 缺失。"),
                CredentialReference = CredentialReference,
                Note = Note,
                UpdatedAt = UpdatedAt
            };
        }
    }
}
