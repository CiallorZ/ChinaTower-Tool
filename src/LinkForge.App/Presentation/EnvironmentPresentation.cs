using LinkForge.Application.Projects;
using LinkForge.Domain.Projects;

namespace LinkForge.App.Presentation;

/// <summary>
/// 环境配置的展示项（LF-004）。
/// </summary>
/// <remarks>
/// 与 <see cref="AssetDisplayItem"/> 同样的理由：派生信息（变量文本、激活状态文案）
/// 写在展示层，领域模型不必为了显示而长出成员。
/// </remarks>
public sealed class EnvironmentDisplayItem
{
    /// <summary>创建展示项。</summary>
    /// <param name="source">领域模型。</param>
    public EnvironmentDisplayItem(EnvironmentProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Source = source;
    }

    /// <summary>领域模型。</summary>
    public EnvironmentProfile Source { get; private set; }

    /// <summary>环境标识。</summary>
    public string Id => Source.Id;

    /// <summary>环境名称。</summary>
    public string Name => Source.Name;

    /// <summary>代理地址（未配置时为空）。</summary>
    public string ProxyUrl => Source.ProxyUrl ?? string.Empty;

    /// <summary>是否已配置代理。</summary>
    public bool HasProxy => !string.IsNullOrWhiteSpace(Source.ProxyUrl);

    /// <summary>变量条数。</summary>
    public int VariableCount => Source.Variables.Count;

    /// <summary>变量文本（每行 KEY=VALUE）。</summary>
    public string VariablesText => EnvironmentVariableText.Format(Source.Variables);

    /// <summary>是否为当前激活环境。</summary>
    public bool IsActive
    {
        get => Source.IsActive;
        set
        {
            if (Source.IsActive != value)
            {
                Source = Source with { IsActive = value };
            }
        }
    }

    /// <summary>激活状态文案。</summary>
    public string ActiveText => IsActive ? "当前环境" : "未激活";

    /// <summary>由领域模型创建展示项。</summary>
    /// <param name="profile">环境配置。</param>
    /// <returns>展示项。</returns>
    public static EnvironmentDisplayItem From(EnvironmentProfile profile) => new(profile);

    /// <summary>下拉框显示文本。</summary>
    /// <returns>环境名称。</returns>
    public override string ToString() => Name;
}
