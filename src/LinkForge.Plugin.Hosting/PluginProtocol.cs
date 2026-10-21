using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkForge.Plugin.Hosting;

/// <summary>插件进程协议消息类型。</summary>
public static class PluginProtocolMethods
{
    /// <summary>握手：返回插件身份与能力。</summary>
    public const string Hello = "hello";

    /// <summary>声明本次运行的步骤计划。</summary>
    public const string Plan = "plan";

    /// <summary>执行一个步骤。</summary>
    public const string ExecuteStep = "executeStep";
}

/// <summary>宿主发给插件进程的一行请求。</summary>
/// <param name="Protocol">协议版本。</param>
/// <param name="Method">方法名。</param>
/// <param name="RequestId">请求标识（进程内唯一）。</param>
/// <param name="Payload">方法参数。</param>
public sealed record PluginRequest(
    string Protocol,
    string Method,
    string RequestId,
    JsonElement Payload);

/// <summary>插件进程返回的一行响应。</summary>
public sealed record PluginResponse
{
    /// <summary>协议版本。</summary>
    public string? Protocol { get; init; }

    /// <summary>对应的请求标识。</summary>
    public string? RequestId { get; init; }

    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }

    /// <summary>结构化结果；失败时为 null。</summary>
    public JsonElement? Result { get; init; }

    /// <summary>可读错误；成功时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>错误类别，例如 <c>invalid_payload</c>。</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>握手结果。</summary>
public sealed record PluginHello(
    string Id,
    string DisplayName,
    string Version,
    string ApiVersion,
    IReadOnlyList<string> Capabilities);

/// <summary>插件声明的步骤（线协议表示）。</summary>
public sealed record PluginStepPlanDto
{
    /// <summary>步骤标识。</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>步骤名称。</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>依赖组件。</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>前置步骤。</summary>
    [JsonPropertyName("dependsOn")]
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>超时秒数；0 表示使用编排器默认值。</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; }
}

/// <summary>插件步骤结果（线协议表示）。</summary>
public sealed record PluginStepResultDto
{
    /// <summary>步骤标识。</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>步骤名称。</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>状态：passed / failed / warning / unknown / skipped / cancelled。</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>面向用户的摘要。</summary>
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>下一步建议。</summary>
    [JsonPropertyName("suggestion")]
    public string? Suggestion { get; init; }

    /// <summary>结构化事实。</summary>
    [JsonPropertyName("facts")]
    public IReadOnlyList<PluginFactDto> Facts { get; init; } = [];

    /// <summary>原始输出（会被宿主再次脱敏，并限制长度）。</summary>
    [JsonPropertyName("rawOutput")]
    public string? RawOutput { get; init; }
}

/// <summary>一条结构化事实。</summary>
public sealed record PluginFactDto
{
    /// <summary>事实名称。</summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    /// <summary>事实取值。</summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

/// <summary>协议 JSON 选项。</summary>
public static class PluginProtocolJson
{
    /// <summary>序列化与反序列化选项。</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };
}
