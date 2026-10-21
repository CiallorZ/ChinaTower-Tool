using System.Runtime.InteropServices;

namespace LinkForge.Plugin.Hosting.Tests;

/// <summary>插件目录扫描与加载前阻断测试。</summary>
public class PluginCatalogScannerTests
{
    [Fact]
    public async Task Compatible_plugin_should_pass_hash_and_platform_checks()
    {
        using var context = new PluginTestContext();
        context.CreatePlugin();

        var results = await new PluginCatalogScanner().ScanAsync(context.PluginsRoot);

        results.ShouldHaveSingleItem().Status.ShouldBe(PluginValidationStatus.Compatible);
        results[0].IsLoadable.ShouldBeTrue();
        results[0].Manifest!.Id.ShouldBe("test.sample");
    }

    [Fact]
    public async Task Api_mismatch_should_be_blocked_before_entry_hash_is_trusted()
    {
        using var context = new PluginTestContext();
        context.CreatePlugin(apiVersion: new Version(2, 0));

        var results = await new PluginCatalogScanner().ScanAsync(context.PluginsRoot);

        results.ShouldHaveSingleItem().Status.ShouldBe(PluginValidationStatus.IncompatibleApi);
        results[0].IsLoadable.ShouldBeFalse();
        results[0].Detail.ShouldContain("API 2.0");
    }

    [Fact]
    public async Task Unsupported_architecture_should_be_blocked()
    {
        using var context = new PluginTestContext();
        string[] unsupported = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? ["arm64"]
            : ["x64"];

        context.CreatePlugin(architectures: unsupported);

        var results = await new PluginCatalogScanner().ScanAsync(context.PluginsRoot);

        results.ShouldHaveSingleItem().Status.ShouldBe(
            PluginValidationStatus.UnsupportedPlatform);
        results[0].IsLoadable.ShouldBeFalse();
    }

    [Fact]
    public async Task Hash_mismatch_should_be_blocked()
    {
        using var context = new PluginTestContext();
        context.CreatePlugin(corruptHash: true);

        var results = await new PluginCatalogScanner().ScanAsync(context.PluginsRoot);

        results.ShouldHaveSingleItem().Status.ShouldBe(PluginValidationStatus.HashMismatch);
        results[0].IsLoadable.ShouldBeFalse();
        results[0].ActualEntryPointSha256.ShouldNotBeNull();
    }

    [Fact]
    public async Task Missing_entry_should_be_blocked()
    {
        using var context = new PluginTestContext();
        context.CreatePlugin(entryPointOverride: "missing.exe");

        var results = await new PluginCatalogScanner().ScanAsync(context.PluginsRoot);

        results.ShouldHaveSingleItem().Status.ShouldBe(
            PluginValidationStatus.EntryPointMissing);
        results[0].IsLoadable.ShouldBeFalse();
    }

    [Fact]
    public async Task A_broken_plugin_should_not_hide_other_plugins()
    {
        using var context = new PluginTestContext();
        context.CreatePlugin(directoryName: "test.good", pluginId: "test.good");
        var broken = context.CreatePlugin(
            directoryName: "test.broken",
            pluginId: "test.broken",
            apiVersion: new Version(9, 0));

        var results = await new PluginCatalogScanner().ScanAsync(context.PluginsRoot);

        results.Count.ShouldBe(2);
        results.Single(result => result.Manifest?.Id == "test.good")
            .Status.ShouldBe(PluginValidationStatus.Compatible);
        results.Single(result => Path.GetDirectoryName(result.ManifestPath) == broken)
            .Status.ShouldBe(PluginValidationStatus.IncompatibleApi);
    }
}
