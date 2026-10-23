using System.IO;
using System.Text;
using LinkForge.App.Services;
using LinkForge.Application.Projects;
using LinkForge.Application.Storage;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Diagnostics;
using LinkForge.Domain.Projects;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>项目包在设备与资产页中的真实状态与取消语义（LF-015）。</summary>
public class AssetCatalogProjectPackageTests
{
    [Fact]
    public async Task Import_should_write_the_package_and_select_the_imported_project()
    {
        var harness = new AssetCatalogHarness();
        await harness.ViewModel.LoadAsync();

        var package = NewPackage();
        harness.PackageFiles.ImportFile = new ProjectPackageImportFile(
            "east-gate.lfpkg",
            new ProjectPackageCodec().Encode(package.Project, package.Environments, package.Assets));
        harness.PackageImporter.OnImport = imported =>
        {
            harness.Projects.Items.Add(imported.Project);
            harness.Environments.Items.AddRange(imported.Environments);
            harness.Assets.Items.AddRange(imported.Assets);
        };

        await harness.ViewModel.ImportProjectPackageCommand.ExecuteAsync(null);

        harness.PackageImporter.LastPackage.ShouldNotBeNull();
        harness.PackageImporter.LastPackage!.Project.Id.ShouldBe(package.Project.Id);
        harness.ViewModel.SelectedProject!.Id.ShouldBe(package.Project.Id);
        harness.ViewModel.HasPackageResult.ShouldBeTrue();
        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Completed);
        harness.ViewModel.PackageFeedback.ShouldContain("2 条资产");
        harness.ViewModel.Projects.ShouldContain(project => project.Id == package.Project.Id);
    }

    [Fact]
    public async Task Invalid_package_should_report_a_readable_format_error()
    {
        var harness = new AssetCatalogHarness();
        await harness.ViewModel.LoadAsync();
        harness.PackageFiles.ImportFile = new ProjectPackageImportFile(
            "broken.lfpkg",
            Encoding.UTF8.GetBytes("{ not-json"));

        await harness.ViewModel.ImportProjectPackageCommand.ExecuteAsync(null);

        harness.ViewModel.HasPackageResult.ShouldBeTrue();
        harness.ViewModel.IsPackageFailure.ShouldBeTrue();
        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Failed);
        harness.ViewModel.PackageFeedback.ShouldContain("项目包校验失败");
        harness.ViewModel.PackageFeedback.ShouldContain("JSON");
    }

    [Fact]
    public async Task Import_conflict_should_keep_the_package_atomic_and_explain_the_conflict()
    {
        var harness = new AssetCatalogHarness();
        await harness.ViewModel.LoadAsync();
        var package = NewPackage();
        harness.PackageFiles.ImportFile = new ProjectPackageImportFile(
            "duplicate.lfpkg",
            new ProjectPackageCodec().Encode(package.Project, package.Environments, package.Assets));
        harness.PackageImporter.Failure = new StorageException(
            "项目名称冲突；未写入任何记录。");

        await harness.ViewModel.ImportProjectPackageCommand.ExecuteAsync(null);

        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Failed);
        harness.ViewModel.PackageFeedback.ShouldContain("项目名称冲突");
        harness.Projects.Items.ShouldBeEmpty("冲突导入必须整体回滚，不能留下半包数据。");
    }

    [Fact]
    public async Task Cancelling_import_should_be_cancelled_not_failed()
    {
        var harness = new AssetCatalogHarness();
        await harness.ViewModel.LoadAsync();
        var package = NewPackage();
        harness.PackageFiles.ImportFile = new ProjectPackageImportFile(
            "slow.lfpkg",
            new ProjectPackageCodec().Encode(package.Project, package.Environments, package.Assets));
        harness.PackageImporter.ImportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.PackageImporter.ImportRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var import = harness.ViewModel.ImportProjectPackageCommand.ExecuteAsync(null);
        await harness.PackageImporter.ImportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.ViewModel.IsImportingPackage.ShouldBeTrue();
        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Running);
        harness.ViewModel.CancelProjectPackageImportCommand.Execute(null);
        await import.WaitAsync(TimeSpan.FromSeconds(5));

        harness.ViewModel.IsPackageCancelled.ShouldBeTrue();
        harness.ViewModel.IsPackageFailure.ShouldBeFalse();
        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Cancelled);
        harness.ViewModel.PackageFeedback.ShouldContain("已取消");
    }

    [Fact]
    public async Task Export_should_write_a_package_without_plaintext_secret_fields()
    {
        var harness = new AssetCatalogHarness();
        var package = NewPackage();
        harness.Projects.Items.Add(package.Project);
        harness.Environments.Items.AddRange(package.Environments);
        harness.Assets.Items.AddRange(package.Assets);
        await harness.ViewModel.LoadAsync();

        await harness.ViewModel.ExportProjectPackageCommand.ExecuteAsync(null);

        harness.PackageFiles.WrittenContent.ShouldNotBeNull();
        harness.PackageFiles.WrittenFileName.ShouldNotBeNull();
        harness.PackageFiles.WrittenFileName.ShouldContain("东门视频项目");

        var bytes = harness.PackageFiles.WrittenContent;
        var text = Encoding.UTF8.GetString(bytes);
        text.ShouldContain("\"credentialReference\"");
        text.ShouldNotContain("\"password\":");
        text.ShouldNotContain("\"token\":");

        var decoded = new ProjectPackageCodec().Decode(bytes);
        decoded.Project.Id.ShouldBe(package.Project.Id);
        decoded.Environments.Count.ShouldBe(1);
        decoded.Assets.Count.ShouldBe(2);
        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Completed);
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_should_be_cancelled_not_success()
    {
        var harness = new AssetCatalogHarness();
        var package = NewPackage();
        harness.Projects.Items.Add(package.Project);
        harness.Environments.Items.AddRange(package.Environments);
        harness.Assets.Items.AddRange(package.Assets);
        harness.PackageFiles.WriteResult = false;
        await harness.ViewModel.LoadAsync();

        await harness.ViewModel.ExportProjectPackageCommand.ExecuteAsync(null);

        harness.ViewModel.HasPackageResult.ShouldBeTrue();
        harness.ViewModel.IsPackageCancelled.ShouldBeTrue();
        harness.ViewModel.IsPackageFailure.ShouldBeFalse();
        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Cancelled);
        harness.ViewModel.PackageStatusText.ShouldBe("已取消");
    }

    [Fact]
    public async Task Export_write_failure_should_be_reported_as_failure()
    {
        var harness = new AssetCatalogHarness();
        var package = NewPackage();
        harness.Projects.Items.Add(package.Project);
        harness.Environments.Items.AddRange(package.Environments);
        harness.Assets.Items.AddRange(package.Assets);
        harness.PackageFiles.WriteFailure = new IOException("目标文件被占用。");
        await harness.ViewModel.LoadAsync();

        await harness.ViewModel.ExportProjectPackageCommand.ExecuteAsync(null);

        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Failed);
        harness.ViewModel.PackageFeedback.ShouldContain("目标文件被占用");
    }

    [Fact]
    public async Task Export_should_be_disabled_when_no_project_exists()
    {
        var harness = new AssetCatalogHarness();
        await harness.ViewModel.LoadAsync();

        harness.ViewModel.ExportProjectPackageCommand.CanExecute(null).ShouldBeFalse();
        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Pending);
    }

    [Fact]
    public async Task Package_commands_should_be_mutually_exclusive_while_export_is_running()
    {
        var harness = new AssetCatalogHarness();
        var package = NewPackage();
        harness.Projects.Items.Add(package.Project);
        harness.Environments.Items.AddRange(package.Environments);
        harness.Assets.Items.AddRange(package.Assets);
        harness.PackageFiles.WriteStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.PackageFiles.WriteRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.ViewModel.LoadAsync();

        var export = harness.ViewModel.ExportProjectPackageCommand.ExecuteAsync(null);
        await harness.PackageFiles.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.ViewModel.IsExportingPackage.ShouldBeTrue();
        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Running);
        harness.ViewModel.ImportProjectPackageCommand.CanExecute(null).ShouldBeFalse();
        harness.ViewModel.ExportProjectPackageCommand.CanExecute(null).ShouldBeFalse();

        harness.ViewModel.CancelProjectPackageExportCommand.Execute(null);
        await export.WaitAsync(TimeSpan.FromSeconds(5));
        harness.ViewModel.PackageState.ShouldBe(TaskRunState.Cancelled);
    }

    private static ProjectPackage NewPackage()
    {
        const string projectId = "prj-imported";

        return new ProjectPackage
        {
            Project = new Project
            {
                Id = projectId,
                Name = "东门视频项目",
                Description = "项目包导入与导出",
                CreatedAt = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(8))
            },
            Environments =
            [
                new EnvironmentProfile
                {
                    Id = "env-imported",
                    ProjectId = projectId,
                    Name = "测试环境",
                    Variables = new Dictionary<string, string>
                    {
                        ["REGION"] = "cn-east"
                    },
                    IsActive = true
                }
            ],
            Assets =
            [
                new Asset
                {
                    Id = "asset-imported-1",
                    ProjectId = projectId,
                    Name = "东门球机",
                    Host = "10.0.0.8",
                    Port = 554,
                    Kind = AssetKind.Camera,
                    Vendor = "海康",
                    Tags = ["园区"],
                    CredentialReference = "asset.cam.password"
                },
                new Asset
                {
                    Id = "asset-imported-2",
                    ProjectId = projectId,
                    Name = "园区网关",
                    Host = "10.0.0.10",
                    Kind = AssetKind.Gateway,
                    Tags = ["园区"]
                }
            ]
        };
    }
}
