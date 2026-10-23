using LinkForge.Application.Secrets;
using LinkForge.Domain.Projects;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// 资产页的 DPAPI 凭据保存、检查、删除与明文清理。
/// </summary>
public class AssetCatalogCredentialTests
{
    private const string ProjectId = "prj-1";

    [Fact]
    public async Task Saving_a_credential_should_store_it_and_clear_the_input()
    {
        var harness = await NewLoadedHarnessAsync();
        harness.ViewModel.AssetCredentialReference = "asset.cam-01.password";
        harness.ViewModel.AssetSecretValue = "LocalSecret42!";

        await harness.ViewModel.SaveCredentialCommand.ExecuteAsync(null);

        harness.Secrets.SaveCallCount.ShouldBe(1);
        harness.Secrets.FindValue("asset.cam-01.password").ShouldBe("LocalSecret42!");
        harness.ViewModel.AssetSecretValue.ShouldBeEmpty("明文不得继续留在视图模型。");
        harness.ViewModel.CredentialStatusText.ShouldContain("已保存");
        harness.ViewModel.CredentialFeedback.ShouldContain("项目包与导出文件不包含凭据值");
        harness.ViewModel.IsCredentialFailure.ShouldBeFalse();
    }

    [Fact]
    public async Task Credential_commands_should_require_a_valid_reference_key()
    {
        var harness = await NewLoadedHarnessAsync();
        harness.ViewModel.AssetCredentialReference = "Passw0rd!";
        harness.ViewModel.AssetSecretValue = "secret";

        harness.ViewModel.SaveCredentialCommand.CanExecute(null).ShouldBeFalse();
        harness.ViewModel.CheckCredentialCommand.CanExecute(null).ShouldBeFalse();
        harness.ViewModel.DeleteCredentialCommand.CanExecute(null).ShouldBeFalse();

        await harness.ViewModel.SaveCredentialCommand.ExecuteAsync(null);

        harness.Secrets.SaveCallCount.ShouldBe(0);
        harness.ViewModel.CredentialStatusText.ShouldContain("有效");
    }

    [Fact]
    public async Task Changing_the_reference_key_should_clear_unsubmitted_plaintext()
    {
        var harness = await NewLoadedHarnessAsync();
        harness.ViewModel.AssetCredentialReference = "asset.cam-01.password";
        harness.ViewModel.AssetSecretValue = "TopSecretValue";

        harness.ViewModel.AssetCredentialReference = "asset.cam-02.password";

        harness.ViewModel.AssetSecretValue.ShouldBeEmpty("旧引用下的明文不得继续用于新引用。");
        harness.Secrets.SaveCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Check_should_distinguish_missing_and_available_entries()
    {
        var harness = await NewLoadedHarnessAsync();
        harness.ViewModel.AssetCredentialReference = "asset.cam-01.password";

        await harness.ViewModel.CheckCredentialCommand.ExecuteAsync(null);
        harness.ViewModel.CredentialStatusText.ShouldContain("未保存");

        harness.ViewModel.AssetSecretValue = "secret";
        await harness.ViewModel.SaveCredentialCommand.ExecuteAsync(null);
        await harness.ViewModel.CheckCredentialCommand.ExecuteAsync(null);

        harness.ViewModel.CredentialStatusText.ShouldContain("已保存");
    }

    [Fact]
    public async Task Delete_should_remove_only_the_selected_reference()
    {
        var harness = await NewLoadedHarnessAsync();
        harness.ViewModel.AssetCredentialReference = "asset.cam-01.password";
        harness.ViewModel.AssetSecretValue = "secret";
        await harness.ViewModel.SaveCredentialCommand.ExecuteAsync(null);

        await harness.ViewModel.DeleteCredentialCommand.ExecuteAsync(null);

        harness.Secrets.DeleteCallCount.ShouldBe(1);
        harness.Secrets.FindValue("asset.cam-01.password").ShouldBeNull();
        harness.ViewModel.CredentialFeedback.ShouldContain("已从当前 Windows 用户");
    }

    [Fact]
    public async Task Credential_store_failure_should_be_readable_and_not_echo_the_secret()
    {
        var harness = await NewLoadedHarnessAsync();
        harness.Secrets.SaveFailure = new SecretStoreException("Windows DPAPI 不可用。");
        harness.ViewModel.AssetCredentialReference = "asset.cam-01.password";
        harness.ViewModel.AssetSecretValue = "TopSecretValue";

        await harness.ViewModel.SaveCredentialCommand.ExecuteAsync(null);

        harness.ViewModel.IsCredentialFailure.ShouldBeTrue();
        harness.ViewModel.CredentialFeedback.ShouldContain("DPAPI 不可用");
        harness.ViewModel.CredentialFeedback.Contains("TopSecretValue", StringComparison.Ordinal)
            .ShouldBeFalse();
        harness.ViewModel.AssetSecretValue.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_a_credential_check_should_not_be_reported_as_failure()
    {
        var harness = await NewLoadedHarnessAsync();
        harness.ViewModel.AssetCredentialReference = "asset.cam-01.password";
        harness.Secrets.StatusStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Secrets.StatusRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var check = harness.ViewModel.CheckCredentialCommand.ExecuteAsync(null);
        await harness.Secrets.StatusStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.ViewModel.CheckCredentialCommand.Cancel();
        await check.WaitAsync(TimeSpan.FromSeconds(5));

        harness.ViewModel.CredentialFeedback.ShouldContain("已取消");
        harness.ViewModel.IsCredentialFailure.ShouldBeFalse();
    }

    private static async Task<AssetCatalogHarness> NewLoadedHarnessAsync()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();
        return harness;
    }
}
