using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Plugin.Hosting.Tests;

/// <summary>
/// 进程外适配器端到端测试。
/// </summary>
/// <remarks>
/// 每个用例都启动真实的夹具子进程：崩溃、静默超时与协议错乱
/// 如果是 mock，就无法证明“插件崩溃不带走宿主”这条验收。
/// </remarks>
public class OutOfProcessDiagnosticAdapterTests
{
    [Fact]
    public async Task Passed_result_should_come_from_real_child_process()
    {
        using var context = new PluginTestContext();
        var adapter = await CreateAdapterAsync(context, "success");

        var result = await adapter.ExecuteStepAsync(
            Context("probe"), new Progress<DiagnosticProgress>(), CancellationToken.None);

        result.Status.ShouldBe(
            DiagnosticStatus.Passed,
            result.Summary);
        result.Summary.ShouldContain("夹具插件完成");
        result.RawOutput!.ShouldContain("camera-01");
        result.FactItems.ShouldContain(fact => fact.Label == "receivedParameters");
    }

    [Fact]
    public async Task Unknown_status_must_not_be_reported_as_passed_or_failed()
    {
        using var context = new PluginTestContext();
        var adapter = await CreateAdapterAsync(context, "unknown");

        var result = await adapter.ExecuteStepAsync(
            Context("probe", "unknown"), new Progress<DiagnosticProgress>(), CancellationToken.None);

        result.Status.ShouldBe(
            DiagnosticStatus.Unknown,
            result.Summary);
    }

    [Fact]
    public async Task Crash_should_be_mapped_to_failed_and_keep_host_process_alive()
    {
        using var context = new PluginTestContext();
        var adapter = await CreateAdapterAsync(context, "crash");
        var hostProcessId = Environment.ProcessId;

        var result = await adapter.ExecuteStepAsync(
            Context("probe", "crash"), new Progress<DiagnosticProgress>(), CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Failed);
        result.Summary.ShouldContain("插件进程");
        result.NextStepSuggestion!.ShouldContain("主程序未受影响");

        // 宿主仍在本线程正常执行，且能继续读环境。
        Environment.ProcessId.ShouldBe(hostProcessId);
        ProcessIsAlive(hostProcessId).ShouldBeTrue();
    }

    [Fact]
    public async Task Silent_plugin_should_time_out_and_not_hang_the_caller()
    {
        using var context = new PluginTestContext();
        var adapter = await CreateAdapterAsync(context, "silent");
        var hostProcessId = Environment.ProcessId;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await adapter.ExecuteStepAsync(
                Context("probe", "silent"),
                new Progress<DiagnosticProgress>(),
                cts.Token));

        Environment.ProcessId.ShouldBe(hostProcessId);
        ProcessIsAlive(hostProcessId).ShouldBeTrue();
    }

    [Fact]
    public async Task Credential_like_parameters_must_never_reach_the_plugin_process()
    {
        using var context = new PluginTestContext();
        var adapter = await CreateAdapterAsync(context, "success");
        var contextData = new DiagnosticStepContext(
            "run-1",
            "probe",
            "夹具探针",
            "camera-01",
            new Dictionary<string, string?>
            {
                ["target"] = "camera-01",
                ["password"] = "SuperSecret123!",
                ["tokenValue"] = "bearer-abc",
                ["privateKey"] = "-----BEGIN PRIVATE KEY-----",
                ["secretRef"] = "dpapi://camera/01"
            },
            DateTimeOffset.UtcNow);

        var result = await adapter.ExecuteStepAsync(
            contextData, new Progress<DiagnosticProgress>(), CancellationToken.None);

        var received = result.FactItems.Single(fact => fact.Label == "receivedParameters").Value;
        received.ShouldContain("camera-01");
        received.ShouldContain("dpapi://camera/01");
        received.ShouldNotContain("SuperSecret123");
        received.ShouldNotContain("bearer-abc");
        received.ShouldNotContain("PRIVATE KEY");
    }

    [Fact]
    public async Task Plan_should_come_from_manifest_without_starting_a_process()
    {
        using var context = new PluginTestContext();
        var adapter = await CreateAdapterAsync(context, "success");

        var plan = adapter.Plan(Request());

        plan.ShouldHaveSingleItem().StepId.ShouldBe("probe");
    }

    [Fact]
    public async Task Credentials_returned_by_a_plugin_must_be_masked_before_reaching_the_host()
    {
        // 参数白名单只保证"宿主不发秘密给插件"，不保证"插件不回传秘密"。
        // 一个有缺陷或恶意的插件完全可以把厂商 SDK 打印的账号密码塞进摘要、建议、
        // 事实与原始输出；这些文本会进入日志、历史、剪贴板与报告，
        // 因此宿主必须在出口自己再过滤一遍。
        using var context = new PluginTestContext();
        var adapter = await CreateAdapterAsync(context, "leaky");

        var result = await adapter.ExecuteStepAsync(
            Context("probe", "leaky"), new Progress<DiagnosticProgress>(), CancellationToken.None);

        // 夹具按 warning 返回：Passed 结论不携带建议字段，会漏测一条出口。
        result.Status.ShouldBe(DiagnosticStatus.Warning, result.Summary);

        result.Summary.ShouldNotContain("LeakySecret123");
        result.NextStepSuggestion!.ShouldNotContain("LeakySecret123");
        result.RawOutput!.ShouldNotContain("LeakySecret123");
        result.FactItems.ShouldAllBe(fact => !fact.Value.Contains("LeakySecret123", StringComparison.Ordinal));

        // 遮蔽而不是丢弃：结构仍在，用户才能看出"这里本来有一个凭据被返回过"。
        result.Summary.ShouldContain("password");
        result.RawOutput!.ShouldContain("Authorization");
    }

    [Fact]
    public void Non_loadable_descriptor_should_not_be_able_to_create_an_adapter()
    {
        var descriptor = new PluginDescriptor(
            "C:\\plugins\\blocked",
            "C:\\plugins\\blocked\\manifest.json",
            PluginValidationStatus.HashMismatch,
            "哈希不一致",
            null,
            null);

        Should.Throw<InvalidOperationException>(() =>
            new OutOfProcessDiagnosticAdapter(
                descriptor, "C:\\plugins\\blocked\\plugin.exe", "C:\\plugins\\blocked"));
    }

    private static async Task<OutOfProcessDiagnosticAdapter> CreateAdapterAsync(
        PluginTestContext context,
        string mode)
    {
        var directory = context.CreatePlugin(directoryName: $"test.{mode}");
        var results = await new PluginCatalogScanner().ScanAsync(context.PluginsRoot);
        var descriptor = results.Single(result =>
            string.Equals(
                Path.GetDirectoryName(result.ManifestPath),
                directory,
                StringComparison.OrdinalIgnoreCase));
        var entry = Path.Combine(directory, descriptor.Manifest!.EntryPoint);

        return new OutOfProcessDiagnosticAdapter(
            descriptor,
            entry,
            directory,
            handshakeTimeout: TimeSpan.FromSeconds(3));
    }

    private static DiagnosticStepContext Context(string stepId, string mode = "success") =>
        new(
            "run-1",
            stepId,
            "夹具探针",
            "camera-01",
            new Dictionary<string, string?> { ["capabilityProbe"] = mode },
            DateTimeOffset.UtcNow);

    private static DiagnosticRequest Request() =>
        new("run-1", "camera-01");

    private static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
