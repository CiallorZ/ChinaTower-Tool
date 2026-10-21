using System.Text;

namespace LinkForge.Plugin.Hosting.Tests;

/// <summary>清单解析与兼容性校验测试。</summary>
public class PluginManifestReaderTests
{
    [Fact]
    public async Task Valid_manifest_should_be_parsed_with_all_security_fields()
    {
        using var context = new PluginTestContext();
        var directory = context.CreatePlugin();
        var path = Path.Combine(directory, PluginHostContract.ManifestFileName);

        var result = await PluginManifestReader.ReadAsync(path);

        result.Succeeded.ShouldBeTrue(result.Error);
        result.Manifest!.Id.ShouldBe("test.sample");
        result.Manifest.ApiVersion.ShouldBe(new Version(1, 0));
        result.Manifest.EntryPointSha256.Length.ShouldBe(64);
        result.Manifest.SignatureStatus.ShouldBe(PluginSignatureStatus.Unsigned);
        result.Manifest.Steps.ShouldHaveSingleItem().StepId.ShouldBe("probe");
    }

    [Fact]
    public async Task Missing_hash_should_fail_without_defaulting()
    {
        using var context = new PluginTestContext();
        var directory = context.CreatePlugin(writeManifest: false);
        var path = context.WriteManifest(directory, """
            {
              "id": "test.sample",
              "displayName": "测试",
              "version": "1.0.0",
              "apiVersion": "1.0",
              "publisher": "Tests",
              "entryPoint": "a.exe",
              "signatureStatus": "unsigned",
              "capabilities": ["test"],
              "architectures": ["any"],
              "steps": []
            }
            """);

        var result = await PluginManifestReader.ReadAsync(
            Path.Combine(path, PluginHostContract.ManifestFileName));

        result.Succeeded.ShouldBeFalse();
        result.Error!.ShouldContain("entryPointSha256");
    }

    [Fact]
    public async Task Unknown_signature_status_should_be_rejected()
    {
        // 不允许出现 "verified"：当前宿主没有证书链，无法验证签名。
        using var context = new PluginTestContext();
        var directory = context.CreatePlugin(writeManifest: false);
        var path = context.WriteManifest(directory, """
            {
              "id": "test.sample",
              "displayName": "测试",
              "version": "1.0.0",
              "apiVersion": "1.0",
              "publisher": "Tests",
              "entryPoint": "a.exe",
              "entryPointSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "signatureStatus": "verified",
              "capabilities": ["test"],
              "architectures": ["any"],
              "steps": []
            }
            """);

        var result = await PluginManifestReader.ReadAsync(
            Path.Combine(path, PluginHostContract.ManifestFileName));

        result.Succeeded.ShouldBeFalse();
        result.Error!.ShouldContain("signatureStatus");
    }

    [Fact]
    public async Task Entry_point_escape_should_be_rejected()
    {
        using var context = new PluginTestContext();
        var directory = context.CreatePlugin(writeManifest: false);
        var path = context.WriteManifest(directory, """
            {
              "id": "test.sample",
              "displayName": "测试",
              "version": "1.0.0",
              "apiVersion": "1.0",
              "publisher": "Tests",
              "entryPoint": "../escape.exe",
              "entryPointSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "signatureStatus": "unsigned",
              "capabilities": ["test"],
              "architectures": ["any"],
              "steps": []
            }
            """);

        var result = await PluginManifestReader.ReadAsync(
            Path.Combine(path, PluginHostContract.ManifestFileName));

        result.Succeeded.ShouldBeFalse();
        result.Error!.ShouldContain("entryPoint");
    }

    [Fact]
    public async Task Oversized_manifest_should_be_rejected_before_json_parse()
    {
        using var context = new PluginTestContext();
        var directory = context.CreatePlugin(writeManifest: false);

        File.WriteAllText(
            Path.Combine(directory, PluginHostContract.ManifestFileName),
            new string(' ', PluginHostContract.MaxManifestBytes + 1),
            new UTF8Encoding(false));

        var result = await PluginManifestReader.ReadAsync(
            Path.Combine(directory, PluginHostContract.ManifestFileName));

        result.Succeeded.ShouldBeFalse();
        result.Error!.ShouldContain("64 KiB");
    }
}
