using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkForge.Application.Secrets;
using LinkForge.Domain.Secrets;

namespace LinkForge.App.Services;

/// <summary>
/// 基于 Windows DPAPI（CurrentUser）的本地凭据库。
/// </summary>
/// <remarks>
/// <para>
/// 每条秘密独立保存为 DPAPI 密文文件，文件名为引用键的 SHA-256，
/// 因此文件系统路径不会泄漏引用键。DPAPI 附加熵同时绑定应用标识、
/// 存储格式版本、引用键和提供方，密文被改名后不能当作另一条秘密使用。
/// </para>
/// <para>
/// 这里刻意不使用明文索引：状态检查必须实际解密文件。
/// 若文件存在但无法解密，结论是“不可读取”，而不是“未保存”。
/// </para>
/// </remarks>
public sealed class WindowsDpapiSecretStore : ISecretStore
{
    private const string DirectoryName = "secrets";
    private const string FileExtension = ".dpapi";
    private const int SchemaVersion = 1;
    private const int MaximumSecretUtf8Bytes = 64 * 1024;
    private const string EntropyPrefix = "LinkForge.SecretStore.v1";

    private static readonly byte[] EntropyPurpose =
        Encoding.UTF8.GetBytes("LinkForge DPAPI secret entry");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;

    /// <summary>使用默认用户数据目录创建凭据库。</summary>
    public WindowsDpapiSecretStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinkForge",
            DirectoryName))
    {
    }

    /// <summary>使用指定目录创建凭据库（测试可注入临时目录）。</summary>
    public WindowsDpapiSecretStore(string directory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>凭据库目录（诊断用，不含秘密）。</summary>
    public string DirectoryPath => _directory;

    /// <inheritdoc />
    public Task<SecretEntryStatus> GetStatusAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolvePath(reference);

                if (!File.Exists(path))
                {
                    return SecretEntryStatus.Missing;
                }

                try
                {
                    var entry = ReadEntry(path, reference, cancellationToken);
                    return SecretEntryStatus.Available(entry.UpdatedAtUtc);
                }
                catch (Exception ex) when (ex is SecretStoreException or IOException or UnauthorizedAccessException
                    or CryptographicException or JsonException)
                {
                    return new SecretEntryStatus(
                        SecretEntryState.Unreadable,
                        null,
                        "凭据文件存在，但当前 Windows 用户无法解密。文件可能损坏、被替换或由其他用户创建。");
                }
            },
            cancellationToken);

    /// <inheritdoc />
    public Task SaveAsync(
        SecretReference reference,
        string secretValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretValue);
        ValidateReference(reference);

        if (secretValue.Length == 0)
        {
            throw new SecretStoreException("凭据值不能为空。");
        }

        if (Encoding.UTF8.GetByteCount(secretValue) > MaximumSecretUtf8Bytes)
        {
            throw new SecretStoreException("凭据值超过 64 KiB UTF-8 上限。");
        }

        // string 本身不可变；后台闭包持有的是稳定快照。
        var value = secretValue;

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_directory);

                var path = ResolvePath(reference);
                var now = _timeProvider.GetUtcNow();
                var payload = JsonSerializer.SerializeToUtf8Bytes(
                    new SecretEnvelope(
                        SchemaVersion,
                        reference.Key,
                        reference.Provider,
                        reference.Kind,
                        now,
                        value),
                    JsonOptions);

                byte[]? encrypted = null;

                try
                {
                    encrypted = ProtectedData.Protect(
                        payload,
                        BuildEntropy(reference),
                        DataProtectionScope.CurrentUser);
                    WriteAtomically(path, encrypted, cancellationToken);
                }
                catch (CryptographicException ex)
                {
                    throw new SecretStoreException(
                        "Windows DPAPI 无法保护该凭据；未写入任何文件。",
                        ex);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);

                    if (encrypted is not null)
                    {
                        CryptographicOperations.ZeroMemory(encrypted);
                    }
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<string?> ReadAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolvePath(reference);

                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    return ReadEntry(path, reference, cancellationToken).Value;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or CryptographicException or JsonException)
                {
                    throw new SecretStoreException(
                        "凭据存在，但当前 Windows 用户无法解密。",
                        ex);
                }
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolvePath(reference);

                if (!File.Exists(path))
                {
                    return false;
                }

                File.Delete(path);
                return true;
            },
            cancellationToken);

    private SecretEnvelope ReadEntry(
        string path,
        SecretReference reference,
        CancellationToken cancellationToken)
    {
        var encrypted = File.ReadAllBytes(path);

        if (encrypted.Length == 0 || encrypted.Length > MaximumSecretUtf8Bytes * 2)
        {
            throw new SecretStoreException("凭据文件长度无效。");
        }

        byte[]? payload = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            payload = ProtectedData.Unprotect(
                encrypted,
                BuildEntropy(reference),
                DataProtectionScope.CurrentUser);
            var entry = JsonSerializer.Deserialize<SecretEnvelope>(payload, JsonOptions)
                ?? throw new SecretStoreException("凭据文件内容为空。");

            if (entry.SchemaVersion != SchemaVersion
                || entry.Provider != reference.Provider
                || entry.Kind != reference.Kind
                || !string.Equals(entry.Key, reference.Key, StringComparison.Ordinal))
            {
                throw new SecretStoreException("凭据文件与请求的引用不匹配。");
            }

            return entry;
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static void ValidateReference(SecretReference reference)
    {
        if (reference.Provider is not SecretProvider.DpapiCurrentUser)
        {
            throw new SecretStoreException("当前凭据库只支持 Windows DPAPI（CurrentUser）。");
        }

        if (!LinkForge.Application.Assets.AssetCredentialReference.IsValidKey(reference.Key))
        {
            throw new SecretStoreException(
                LinkForge.Application.Assets.AssetCredentialReference.InvalidReason());
        }
    }

    private string ResolvePath(SecretReference reference)
    {
        ValidateReference(reference);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(reference.Key));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash) + FileExtension);
    }

    private static byte[] BuildEntropy(SecretReference reference)
    {
        var keyBytes = Encoding.UTF8.GetBytes(reference.Key);
        var material = new byte[EntropyPurpose.Length + sizeof(int) + keyBytes.Length];
        var offset = 0;

        EntropyPurpose.CopyTo(material, offset);
        offset += EntropyPurpose.Length;
        BinaryPrimitives.WriteInt32LittleEndian(material.AsSpan(offset), (int)reference.Provider);
        offset += sizeof(int);
        keyBytes.CopyTo(material, offset);

        return SHA256.HashData(material);
    }

    private static void WriteAtomically(
        string path,
        ReadOnlySpan<byte> content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed record SecretEnvelope(
        int SchemaVersion,
        string Key,
        SecretProvider Provider,
        SecretKind Kind,
        DateTimeOffset UpdatedAtUtc,
        string Value);
}
