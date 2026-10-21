using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LinkForge.App.Presentation;

/// <summary>HTTP 方法选项。</summary>
public sealed record HttpMethodOption(string Method)
{
    /// <inheritdoc />
    public override string ToString() => Method;
}

/// <summary>请求体类型选项。</summary>
public sealed record HttpBodyKindOption(string Key, string Title)
{
    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>认证方式选项。</summary>
public sealed record HttpAuthModeOption(string Key, string Title)
{
    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>请求历史条目；只保存脱敏副本。</summary>
public sealed record ApiHistoryEntry(
    DateTimeOffset Timestamp,
    string Method,
    string DisplayUrl,
    string? StatusText,
    TimeSpan? Duration,
    bool IsSuccess)
{
    /// <summary>历史列表显示文本。</summary>
    public string Summary => $"{Method} {DisplayUrl}";

    /// <summary>结果摘要。</summary>
    public string ResultText => StatusText is null
        ? "未收到响应"
        : $"{StatusText} · {FormatDuration(Duration)}";

    /// <summary>时间文本。</summary>
    public string TimestampText => Timestamp.ToLocalTime()
        .ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan? duration) =>
        duration is null
            ? "-"
            : duration.Value.TotalSeconds >= 1
                ? $"{duration.Value.TotalSeconds:0.0} s"
                : $"{duration.Value.TotalMilliseconds:0} ms";
}

/// <summary>环境变量条目。</summary>
public sealed partial class ApiEnvironmentVariable : ObservableObject
{
    /// <summary>变量名，例如 baseUrl。</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>变量值；秘密值只驻留内存，不写历史或导出。</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>是否为秘密值。</summary>
    [ObservableProperty]
    public partial bool IsSecret { get; set; }

    /// <summary>显示值；秘密值只显示掩码。</summary>
    public string DisplayValue => IsSecret && !string.IsNullOrEmpty(Value)
        ? SecretRedactor.Mask
        : Value;

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(DisplayValue));

    partial void OnIsSecretChanged(bool value) => OnPropertyChanged(nameof(DisplayValue));
}

/// <summary>变量解析结果。</summary>
public sealed record ApiVariableExpansion(string Text, IReadOnlyList<string> MissingVariables);

/// <summary>可编辑的请求/响应头或查询参数一行。</summary>
public sealed partial class ApiHeaderRow : ObservableObject
{
    /// <summary>创建一行。</summary>
    public ApiHeaderRow(string name, string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>名称。</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>值。</summary>
    [ObservableProperty]
    public partial string Value { get; set; }

    /// <summary>已按 Header/参数名称脱敏的显示值。</summary>
    public string DisplayValue => SecretRedactor.RedactNamedValue(Name, Value);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayValue));

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(DisplayValue));
}

/// <summary>可观测的环境变量集合。</summary>
public sealed class ApiEnvironmentVariables : ObservableCollection<ApiEnvironmentVariable>
{
}
