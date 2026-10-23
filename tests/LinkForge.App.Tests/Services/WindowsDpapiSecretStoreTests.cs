using System.IO;
using System.Security.Cryptography;
using System.Text;
using LinkForge.App.Services;
using LinkForge.Application.Secrets;
using LinkForge.Domain.Secrets;

namespace LinkForge.App.Tests.Services;

/// <summary>
/// Windows CurrentUser DPAPI 凭据库的真实往返与失败边界。
/// </summary>
public class WindowsDpapiSecretStoreTests
{
    [Fact]
    public async Task Dpapi_store_should_round_trip_without_writing_plaintext()
    {
        using var fixture = new SecretStoreFixture();
        var reference = PasswordReference("asset.cam-01.password");
        const string secret = "LocalSecret42!";

        (await fixture.Store.GetStatusAsync(reference)).State.ShouldBe(SecretEntryState.Missing);
        await fixture.Store.SaveAsync(reference, secret);

        var status = await fixture.Store.GetStatusAsync(reference);
        status.State.ShouldBe(SecretEntryState.Available);
        status.UpdatedAtUtc.ShouldNotBeNull();
        (await fixture.Store.ReadAsync(reference)).ShouldBe(secret);

        var encryptedFile = Directory.GetFiles(fixture.DirectoryPath, "*.dpapi").ShouldHaveSingleItem();
        var bytes = await File.ReadAllBytesAsync(encryptedFile);
        Contains(bytes, Encoding.UTF8.GetBytes(secret)).ShouldBeFalse("DPAPI 文件不得包含 UTF-8 明文。");
        Contains(bytes, Encoding.Unicode.GetBytes(secret)).ShouldBeFalse("DPAPI 文件不得包含 UTF-16 明文。");
        Path.GetFileName(encryptedFile).ShouldNotContain(reference.Key);
    }

    [Fact]
    public async Task Different_reference_keys_should_be_isolated()
    {
        using var fixture = new SecretStoreFixture();
        var first = PasswordReference("asset.cam-01.password");
        var second = PasswordReference("asset.cam-02.password");

        await fixture.Store.SaveAsync(first, "first-secret");

        (await fixture.Store.ReadAsync(second)).ShouldBeNull();
        (await fixture.Store.GetStatusAsync(second)).State.ShouldBe(SecretEntryState.Missing);
    }

    [Fact]
    public async Task Corrupt_file_should_be_unreadable_not_missing()
    {
        using var fixture = new SecretStoreFixture();
        var reference = PasswordReference("asset.cam-01.password");
        await fixture.Store.SaveAsync(reference, "secret");
        var encryptedFile = Directory.GetFiles(fixture.DirectoryPath, "*.dpapi").ShouldHaveSingleItem();
        await File.WriteAllBytesAsync(encryptedFile, [0x01, 0x02, 0x03, 0x04]);

        var status = await fixture.Store.GetStatusAsync(reference);

        status.State.ShouldBe(SecretEntryState.Unreadable);
        status.Message.ShouldContain("无法解密");
        await Should.ThrowAsync<SecretStoreException>(() => fixture.Store.ReadAsync(reference));
    }

    [Fact]
    public async Task Encrypted_entry_copied_to_another_reference_should_be_unreadable()
    {
        using var fixture = new SecretStoreFixture();
        var source = PasswordReference("asset.cam-01.password");
        var target = PasswordReference("asset.cam-02.password");
        await fixture.Store.SaveAsync(source, "secret");

        var sourcePath = Directory.GetFiles(fixture.DirectoryPath, "*.dpapi").ShouldHaveSingleItem();
        var targetFileName = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(target.Key))) + ".dpapi";
        var targetPath = Path.Combine(fixture.DirectoryPath, targetFileName);
        File.Copy(sourcePath, targetPath);

        (await fixture.Store.GetStatusAsync(target)).State.ShouldBe(SecretEntryState.Unreadable);
        await Should.ThrowAsync<SecretStoreException>(() => fixture.Store.ReadAsync(target));
        (await fixture.Store.ReadAsync(source)).ShouldBe("secret");
    }

    [Fact]
    public async Task Delete_should_report_whether_an_entry_existed()
    {
        using var fixture = new SecretStoreFixture();
        var reference = PasswordReference("asset.cam-01.password");
        await fixture.Store.SaveAsync(reference, "secret");

        (await fixture.Store.DeleteAsync(reference)).ShouldBeTrue();
        (await fixture.Store.DeleteAsync(reference)).ShouldBeFalse();
        (await fixture.Store.GetStatusAsync(reference)).State.ShouldBe(SecretEntryState.Missing);
    }

    [Fact]
    public void Invalid_keys_and_non_dpapi_providers_should_be_rejected()
    {
        using var fixture = new SecretStoreFixture();
        var invalid = PasswordReference("P@ssw0rd with spaces");
        var credentialManager = new SecretReference(
            "asset.cam-01.password",
            SecretProvider.WindowsCredentialManager,
            SecretKind.Password);

        Should.Throw<SecretStoreException>(() => _ = fixture.Store.SaveAsync(invalid, "secret"));
        Should.Throw<SecretStoreException>(() => _ = fixture.Store.SaveAsync(credentialManager, "secret"));
    }

    [Fact]
    public void Empty_or_oversized_secret_should_be_rejected()
    {
        using var fixture = new SecretStoreFixture();
        var reference = PasswordReference("asset.cam-01.password");

        Should.Throw<SecretStoreException>(() => _ = fixture.Store.SaveAsync(reference, string.Empty));
        Should.Throw<SecretStoreException>(
            () => _ = fixture.Store.SaveAsync(reference, new string('x', 70 * 1024)));
    }

    [Fact]
    public async Task Pre_cancelled_operations_should_not_create_a_file()
    {
        using var fixture = new SecretStoreFixture();
        var reference = PasswordReference("asset.cam-01.password");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => fixture.Store.SaveAsync(reference, "secret", cancellation.Token));

        Directory.Exists(fixture.DirectoryPath).ShouldBeFalse();
    }

    private static SecretReference PasswordReference(string key) =>
        new(key, SecretProvider.DpapiCurrentUser, SecretKind.Password);

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;

    private sealed class SecretStoreFixture : IDisposable
    {
        public SecretStoreFixture()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "linkforge-dpapi-" + Guid.NewGuid().ToString("N"));
            Store = new WindowsDpapiSecretStore(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public WindowsDpapiSecretStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
