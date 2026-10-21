using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkForge.Plugin.Hosting;

/// <summary>
/// 插件清单的严格解析器。
/// </summary>
/// <remarks>
/// 不使用“缺字段就填默认值”的反序列化方式：
/// 一个没有入口哈希、没有 API 版本的清单若被补成默认值，
/// 就会被当成合法插件启动，等于把缺失的安全声明变成默认可信。
/// </remarks>
public static partial class PluginManifestReader
{
    private const int MaxSteps = 64;
    private const int MaxCapabilities = 64;
    private const int MaxTextLength = 512;
    private const int MaxSummaryLength = 4096;
    private const int MaxTimeoutSeconds = 3600;

    /// <summary>解析一个清单文件。</summary>
    /// <param name="manifestPath">清单路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析结果；不抛业务异常。</returns>
    public static async Task<PluginManifestReadResult> ReadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return PluginManifestReadResult.Failure("清单路径为空。");
        }

        try
        {
            var info = new FileInfo(manifestPath);

            if (!info.Exists)
            {
                return PluginManifestReadResult.Failure("未找到 manifest.json。");
            }

            if (info.Length > PluginHostContract.MaxManifestBytes)
            {
                return PluginManifestReadResult.Failure(
                    $"清单超过 {PluginHostContract.MaxManifestBytes / 1024} KiB 上限，已拒绝读取。");
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return PluginManifestReadResult.Failure("清单文件是重解析点，已拒绝读取。");
            }

            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                useAsync: true);

            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions { MaxDepth = 16 },
                cancellationToken).ConfigureAwait(false);

            return Parse(document.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return PluginManifestReadResult.Failure($"清单不是有效 JSON：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return PluginManifestReadResult.Failure($"读取清单失败：{ex.GetType().Name} — {ex.Message}");
        }
    }

    /// <summary>
    /// 校验并计算入口文件哈希。
    /// </summary>
    /// <param name="pluginDirectory">插件目录。</param>
    /// <param name="manifest">清单。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>入口文件的绝对路径与真实 SHA-256；失败时抛出可读异常。</returns>
    public static async Task<(string EntryPath, string Sha256)> ResolveEntryPointAsync(
        string pluginDirectory,
        PluginManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ArgumentNullException.ThrowIfNull(manifest);

        var root = Path.GetFullPath(pluginDirectory);
        var entry = Path.GetFullPath(Path.Combine(root, manifest.EntryPoint));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!entry.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("入口文件越出插件目录，已拒绝加载。");
        }

        if (!File.Exists(entry))
        {
            throw new FileNotFoundException("入口可执行文件不存在。", Path.GetFileName(entry));
        }

        var attributes = File.GetAttributes(entry);

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("入口文件是重解析点，已拒绝加载。");
        }

        await using var stream = new FileStream(
            entry,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return (entry, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static PluginManifestReadResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return PluginManifestReadResult.Failure("清单根节点必须是 JSON 对象。");
        }

        var idResult = ReadString(root, "id", required: true);

        if (!idResult.Succeeded)
        {
            return PluginManifestReadResult.Failure(idResult.Error!);
        }

        var id = idResult.Value!;

        if (!PluginIdPattern().IsMatch(id))
        {
            return PluginManifestReadResult.Failure(
                "id 必须为 3–64 字符的小写字母、数字、点、短横线或下划线，且以字母或数字开头。");
        }

        var displayName = ReadString(root, "displayName", required: true);
        var version = ReadVersion(root, "version");
        var apiVersion = ReadVersion(root, "apiVersion");
        var publisher = ReadString(root, "publisher", required: true);
        var description = ReadString(root, "description", required: false);
        var entryPoint = ReadString(root, "entryPoint", required: true);
        var hash = ReadString(root, "entryPointSha256", required: true);
        var signature = ReadString(root, "signatureStatus", required: true);
        var capabilities = ReadStringArray(root, "capabilities", required: true, MaxCapabilities);
        var architectures = ReadStringArray(root, "architectures", required: true, 8);
        var steps = ReadSteps(root);

        foreach (var failure in new[]
                 {
                     displayName.Error,
                     version.Error,
                     apiVersion.Error,
                     publisher.Error,
                     description.Error,
                     entryPoint.Error,
                     hash.Error,
                     signature.Error,
                     capabilities.Error,
                     architectures.Error,
                     steps.Error
                 })
        {
            if (!string.IsNullOrWhiteSpace(failure))
            {
                return PluginManifestReadResult.Failure(failure);
            }
        }

        if (!IsSafeRelativePath(entryPoint.Value!))
        {
            return PluginManifestReadResult.Failure("entryPoint 必须是插件目录内的相对路径，且不能包含 ..。");
        }

        if (!Sha256Pattern().IsMatch(hash.Value!))
        {
            return PluginManifestReadResult.Failure("entryPointSha256 必须是 64 位十六进制 SHA-256。");
        }

        if (!TryParseSignature(signature.Value!, out var signatureStatus))
        {
            return PluginManifestReadResult.Failure(
                "signatureStatus 只能是 unsigned 或 declared-signed-unverified；宿主不会把声明当作已验证。");
        }

        var normalizedArchitectures = architectures.Values!
            .Select(static value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedArchitectures.Any(static value => value is not ("x64" or "x86" or "arm64" or "any")))
        {
            return PluginManifestReadResult.Failure("architectures 只能包含 x64、x86、arm64 或 any。");
        }

        var normalizedCapabilities = capabilities.Values!
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedCapabilities.Length != capabilities.Values!.Count)
        {
            return PluginManifestReadResult.Failure("capabilities 不能包含空项或重复项。");
        }

        return PluginManifestReadResult.Success(new PluginManifest
        {
            Id = id,
            DisplayName = displayName.Value!,
            Version = version.Value!,
            ApiVersion = apiVersion.Value!,
            Publisher = publisher.Value!,
            Description = description.Value ?? string.Empty,
            Capabilities = normalizedCapabilities,
            Architectures = normalizedArchitectures,
            EntryPoint = entryPoint.Value!,
            EntryPointSha256 = hash.Value!.ToLowerInvariant(),
            SignatureStatus = signatureStatus,
            Steps = steps.Values!
        });
    }

    private static TextReadResult ReadString(JsonElement root, string name, bool required)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return required
                ? TextReadResult.Failure($"缺少必填字段 {name}。")
                : TextReadResult.Success(null);
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return TextReadResult.Failure($"字段 {name} 必须是字符串。");
        }

        var value = element.GetString()?.Trim();

        if (required && string.IsNullOrWhiteSpace(value))
        {
            return TextReadResult.Failure($"字段 {name} 不能为空。");
        }

        if (value?.Length > MaxTextLength)
        {
            return TextReadResult.Failure($"字段 {name} 超过 {MaxTextLength} 字符上限。");
        }

        return TextReadResult.Success(value);
    }

    private static VersionReadResult ReadVersion(JsonElement root, string name)
    {
        var text = ReadString(root, name, required: true);

        if (!text.Succeeded)
        {
            return VersionReadResult.Failure(text.Error!);
        }

        return Version.TryParse(text.Value, out var version)
            && version.Major >= 0
            && version.Minor >= 0
            && version.Build <= 9999
            && version.Revision <= 9999
            ? VersionReadResult.Success(version)
            : VersionReadResult.Failure($"字段 {name} 不是有效版本号。");
    }

    private static StringArrayReadResult ReadStringArray(
        JsonElement root,
        string name,
        bool required,
        int maxCount)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return required
                ? StringArrayReadResult.Failure($"缺少必填字段 {name}。")
                : StringArrayReadResult.Success([]);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return StringArrayReadResult.Failure($"字段 {name} 必须是字符串数组。");
        }

        if (element.GetArrayLength() > maxCount)
        {
            return StringArrayReadResult.Failure($"字段 {name} 超过 {maxCount} 项上限。");
        }

        var values = new List<string>();

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return StringArrayReadResult.Failure($"字段 {name} 只能包含字符串。");
            }

            var value = item.GetString()?.Trim();

            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTextLength)
            {
                return StringArrayReadResult.Failure($"字段 {name} 包含空项或超长项。");
            }

            values.Add(value);
        }

        return StringArrayReadResult.Success(values);
    }

    private static StepsReadResult ReadSteps(JsonElement root)
    {
        if (!root.TryGetProperty("steps", out var element))
        {
            return StepsReadResult.Failure("缺少必填字段 steps。");
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return StepsReadResult.Failure("字段 steps 必须是数组。");
        }

        if (element.GetArrayLength() > MaxSteps)
        {
            return StepsReadResult.Failure($"steps 超过 {MaxSteps} 项上限。");
        }

        var steps = new List<PluginStepDeclaration>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return StepsReadResult.Failure("steps 的每一项必须是对象。");
            }

            var id = ReadString(item, "id", required: true);
            var name = ReadString(item, "name", required: true);

            if (!id.Succeeded)
            {
                return StepsReadResult.Failure(id.Error!);
            }

            if (!name.Succeeded)
            {
                return StepsReadResult.Failure(name.Error!);
            }

            if (!StepIdPattern().IsMatch(id.Value!))
            {
                return StepsReadResult.Failure($"步骤 id {id.Value} 格式非法。");
            }

            if (!ids.Add(id.Value!))
            {
                return StepsReadResult.Failure($"步骤 id {id.Value} 重复。");
            }

            var timeoutSeconds = 30;

            if (item.TryGetProperty("timeoutSeconds", out var timeoutElement))
            {
                if (!timeoutElement.TryGetInt32(out timeoutSeconds)
                    || timeoutSeconds is < 1 or > MaxTimeoutSeconds)
                {
                    return StepsReadResult.Failure(
                        $"步骤 {id.Value} 的 timeoutSeconds 必须在 1–{MaxTimeoutSeconds} 之间。");
                }
            }

            var dependencies = ReadStringArray(item, "dependencies", required: false, 16);
            var prerequisites = ReadStringArray(item, "dependsOn", required: false, 16);

            if (!dependencies.Succeeded)
            {
                return StepsReadResult.Failure(dependencies.Error!);
            }

            if (!prerequisites.Succeeded)
            {
                return StepsReadResult.Failure(prerequisites.Error!);
            }

            steps.Add(new PluginStepDeclaration(
                id.Value!,
                name.Value!,
                TimeSpan.FromSeconds(timeoutSeconds),
                dependencies.Values!,
                prerequisites.Values!));
        }

        foreach (var step in steps)
        {
            foreach (var prerequisite in step.Prerequisites)
            {
                if (!ids.Contains(prerequisite))
                {
                    return StepsReadResult.Failure(
                        $"步骤 {step.StepId} 的前置步骤 {prerequisite} 不存在。");
                }
            }
        }

        return StepsReadResult.Success(steps);
    }

    private static bool TryParseSignature(string value, out PluginSignatureStatus status)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "unsigned":
                status = PluginSignatureStatus.Unsigned;
                return true;

            case "declared-signed-unverified":
                status = PluginSignatureStatus.DeclaredSignedUnverified;
                return true;

            default:
                status = PluginSignatureStatus.Unsigned;
                return false;
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Contains('"', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Length > 0
            && segments.All(static segment => segment is not ("." or ".."));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private sealed record TextReadResult(string? Value, string? Error)
    {
        public bool Succeeded => Error is null;

        public static TextReadResult Success(string? value) => new(value, null);

        public static TextReadResult Failure(string error) => new(null, error);
    }

    private sealed record VersionReadResult(Version? Value, string? Error)
    {
        public bool Succeeded => Error is null;

        public static VersionReadResult Success(Version value) => new(value, null);

        public static VersionReadResult Failure(string error) => new(null, error);
    }

    private sealed record StringArrayReadResult(IReadOnlyList<string>? Values, string? Error)
    {
        public bool Succeeded => Error is null;

        public static StringArrayReadResult Success(IReadOnlyList<string> values) => new(values, null);

        public static StringArrayReadResult Failure(string error) => new(null, error);
    }

    private sealed record StepsReadResult(IReadOnlyList<PluginStepDeclaration>? Values, string? Error)
    {
        public bool Succeeded => Error is null;

        public static StepsReadResult Success(IReadOnlyList<PluginStepDeclaration> values) =>
            new(values, null);

        public static StepsReadResult Failure(string error) => new(null, error);
    }
}
