using System.Text;
using LinkForge.App.ViewModels;
using LinkForge.Application.Assets;
using LinkForge.Application.Storage;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// 设备与资产页的视图模型测试（LF-004）。
/// </summary>
/// <remarks>
/// 重点覆盖三条红线：
/// 1. 页面上的每一条数据都来自存储，不存在"示例数据"；
/// 2. 凭据只以引用键形态存在，明文密码进不来也出不去；
/// 3. CSV 导入的失败必须逐行给出行号与原因，不静默丢弃。
/// </remarks>
public class AssetCatalogViewModelTests
{
    private const string ProjectId = "prj-1";

    [Fact]
    public async Task Load_should_read_real_projects_from_the_store()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.HasProjects.ShouldBeTrue();
        harness.ViewModel.Projects.Single().Name.ShouldBe("东门视频项目");
        harness.ViewModel.SelectedProject!.Id.ShouldBe(ProjectId);
        harness.ViewModel.HasLoaded.ShouldBeTrue();
        harness.ViewModel.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task Empty_database_should_show_an_empty_state_not_fake_projects()
    {
        var harness = new AssetCatalogHarness();

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.Projects.ShouldBeEmpty("数据库为空时不得预置示例项目。");
        harness.ViewModel.HasProjects.ShouldBeFalse();
        harness.ViewModel.IsEmptyAfterLoad.ShouldBeTrue();
        harness.ViewModel.IsInitialEmpty.ShouldBeFalse("已加载过就不再是'尚未加载'。");
    }

    [Fact]
    public async Task Storage_failure_should_be_reported_as_a_readable_error()
    {
        var harness = new AssetCatalogHarness();
        harness.Storage.Failure = new StorageException("数据库文件被另一个 LinkForge 实例占用。");

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.HasLoadFailure.ShouldBeTrue("存储不可用必须显示错误，不能显示空白页。");
        harness.ViewModel.LoadErrorMessage.ShouldContain("占用");
        harness.ViewModel.IsEmptyAfterLoad.ShouldBeFalse("读取失败不是'没有数据'。");
    }

    [Fact]
    public async Task Corrupted_database_recovery_should_be_surfaced_to_the_user()
    {
        var harness = new AssetCatalogHarness();
        harness.Storage.Result = new DatabaseInitializationResult(
            DatabaseInitializationOutcome.RecoveredFromCorruption,
            @"C:\fake\linkforge.db",
            "linkforge.db.corrupt-20261021-120000");

        await harness.ViewModel.LoadAsync();

        // 静默重建会让用户以为数据凭空消失，因此必须显式告知。
        harness.ViewModel.ActionFeedback.ShouldContain("隔离");
    }

    [Fact]
    public async Task Duplicate_project_name_should_show_the_readable_conflict_message()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.UpsertFailure =
            new StorageException("已存在名为“东门”的项目。请换一个名称，或直接打开已有项目。");

        harness.ViewModel.NewProjectName = "东门";
        await harness.ViewModel.CreateProjectCommand.ExecuteAsync(null);

        harness.ViewModel.ActionFeedback.ShouldContain("已存在名为");
        harness.ViewModel.Projects.ShouldBeEmpty("冲突时不得插入半条记录。");
    }

    [Fact]
    public async Task Creating_a_project_should_persist_it_and_select_it()
    {
        var harness = new AssetCatalogHarness();
        harness.ViewModel.NewProjectName = " 东门视频项目 ";
        harness.ViewModel.NewProjectDescription = "一号楼东门";

        await harness.ViewModel.CreateProjectCommand.ExecuteAsync(null);

        harness.Projects.Items.ShouldHaveSingleItem();
        harness.Projects.Items[0].Name.ShouldBe("东门视频项目");
        harness.Projects.Items[0].Description.ShouldBe("一号楼东门");
        harness.ViewModel.SelectedProject!.Name.ShouldBe("东门视频项目");
        harness.ViewModel.NewProjectName.ShouldBeEmpty();
    }

    [Fact]
    public async Task Environment_variables_that_look_like_secrets_should_be_rejected()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.NewEnvironmentName = "测试环境";
        harness.ViewModel.NewEnvironmentVariables = "DEVICE_PORT=554\nAPI_TOKEN=abcdef123456";

        await harness.ViewModel.CreateEnvironmentCommand.ExecuteAsync(null);

        harness.Environments.Items.ShouldBeEmpty("含凭据的环境变量不得落库。");
        harness.ViewModel.ActionFeedback.ShouldContain("密码或令牌");
    }

    [Fact]
    public async Task Activating_an_environment_should_switch_the_active_flag()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.NewEnvironmentName = "测试环境";
        harness.ViewModel.NewEnvironmentVariables = "DEVICE_PORT=554";
        await harness.ViewModel.CreateEnvironmentCommand.ExecuteAsync(null);

        harness.ViewModel.Environments.Single().IsActive.ShouldBeFalse();

        harness.ViewModel.SelectedEnvironment = harness.ViewModel.Environments.Single();
        await harness.ViewModel.ActivateEnvironmentCommand.ExecuteAsync(null);

        harness.ViewModel.Environments.Single().IsActive.ShouldBeTrue();
        harness.ViewModel.HasActiveEnvironment.ShouldBeTrue();
    }

    [Fact]
    public async Task Saving_an_asset_with_a_plaintext_password_should_be_rejected()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.AssetName = "东门球机";
        harness.ViewModel.AssetHost = "10.0.0.8";
        harness.ViewModel.AssetCredentialReference = "Passw0rd!@#";

        await harness.ViewModel.SaveAssetCommand.ExecuteAsync(null);

        harness.Assets.Items.ShouldBeEmpty("明文密码不得写入存储。");
        harness.ViewModel.AssetFormError.ShouldContain("引用键");
    }

    [Fact]
    public async Task Saving_an_asset_should_persist_it_and_show_it_in_the_list()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.AssetName = "东门球机";
        harness.ViewModel.AssetHost = "10.0.0.8";
        harness.ViewModel.AssetPort = "554";
        harness.ViewModel.AssetTags = "园区; 一号楼";
        harness.ViewModel.AssetCredentialReference = "asset.cam-01.password";
        harness.ViewModel.AssetKind = harness.ViewModel.KindOptions
            .First(option => option.Kind == AssetKind.Camera);

        await harness.ViewModel.SaveAssetCommand.ExecuteAsync(null);

        var saved = harness.Assets.Items.ShouldHaveSingleItem();
        saved.Name.ShouldBe("东门球机");
        saved.Port.ShouldBe(554);
        saved.Kind.ShouldBe(AssetKind.Camera);
        saved.Tags.ShouldBe(["园区", "一号楼"]);
        saved.CredentialReference.ShouldBe("asset.cam-01.password");

        harness.ViewModel.Assets.ShouldHaveSingleItem();
        harness.ViewModel.VisibleAssetCount.ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_port_should_be_reported_on_the_form()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.AssetName = "东门球机";
        harness.ViewModel.AssetHost = "10.0.0.8";
        harness.ViewModel.AssetPort = "70000";

        await harness.ViewModel.SaveAssetCommand.ExecuteAsync(null);

        harness.Assets.Items.ShouldBeEmpty();
        harness.ViewModel.AssetFormError.ShouldContain("端口");
    }

    [Fact]
    public async Task Search_should_filter_by_name_host_vendor_and_tag()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        harness.Assets.Items.Add(NewAsset("asset-1", "东门球机", "10.0.0.8", AssetKind.Camera, "海康", ["园区"]));
        harness.Assets.Items.Add(NewAsset("asset-2", "西门枪机", "10.0.0.9", AssetKind.Camera, "大华", ["园区"]));
        harness.Assets.Items.Add(NewAsset("asset-3", "网关", "10.0.0.10", AssetKind.Gateway, "通用", ["机房"]));

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.VisibleAssetCount.ShouldBe(3);

        harness.ViewModel.SearchText = "西门";
        harness.ViewModel.VisibleAssetCount.ShouldBe(1);

        harness.ViewModel.SearchText = "10.0.0.10";
        harness.ViewModel.VisibleAssetCount.ShouldBe(1);

        harness.ViewModel.SearchText = "大华";
        harness.ViewModel.VisibleAssetCount.ShouldBe(1);

        harness.ViewModel.SearchText = "机房";
        harness.ViewModel.VisibleAssetCount.ShouldBe(1);

        harness.ViewModel.SearchText = string.Empty;
        harness.ViewModel.VisibleAssetCount.ShouldBe(3);
    }

    [Fact]
    public void Kind_filter_should_start_on_all_types_instead_of_a_blank_box()
    {
        // 回归：筛选下拉框的 SelectedItem 曾经没有初始化，
        // ComboBox 于是渲染成一个空白框 —— 用户看不到当前筛选状态，也无从改回去。
        var harness = new AssetCatalogHarness();

        harness.ViewModel.SelectedKindOption.ShouldNotBeNull(
            "类型筛选必须默认选中“全部类型”，不能是一个空白下拉框。");
        harness.ViewModel.SelectedKindOption!.Kind.ShouldBeNull("默认不应按任何类型过滤。");
        harness.ViewModel.SelectedKindOption.Title.ShouldBe("全部类型");
    }

    [Fact]
    public async Task Kind_filter_should_narrow_the_visible_assets()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        harness.Assets.Items.Add(NewAsset("asset-1", "东门球机", "10.0.0.8", AssetKind.Camera));
        harness.Assets.Items.Add(NewAsset("asset-2", "网关", "10.0.0.10", AssetKind.Gateway));

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.SelectedKindOption = harness.ViewModel.KindOptions
            .First(option => option.Kind == AssetKind.Camera);

        harness.ViewModel.VisibleAssetCount.ShouldBe(1);
        harness.ViewModel.VisibleAssets.Single().Name.ShouldBe("东门球机");
    }

    [Fact]
    public async Task Grouping_by_tag_should_produce_group_headers_that_cannot_be_selected()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        harness.Assets.Items.Add(NewAsset("asset-1", "东门球机", "10.0.0.8", AssetKind.Camera, null, ["园区"]));
        harness.Assets.Items.Add(NewAsset("asset-2", "网关", "10.0.0.10", AssetKind.Gateway, null, []));

        await harness.ViewModel.LoadAsync();
        harness.ViewModel.IsGroupByTag = true;

        harness.ViewModel.AssetRows.Count.ShouldBe(4, "两个分组头 + 两行资产。");
        harness.ViewModel.AssetRows.Count(static row => row.IsGroupHeader).ShouldBe(2);

        // 分组头不可选中：因此"选中项一定是一台真实资产"是结构保证。
        // 刻意不用 ShouldAllBe：它接收表达式树，表达式树不支持 is 模式匹配。
        foreach (var row in harness.ViewModel.AssetRows.Where(static row => row.IsGroupHeader))
        {
            row.Asset.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Import_should_write_valid_rows_and_report_invalid_ones_with_line_numbers()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        var csv = Encoding.UTF8.GetBytes(
            "name,host,port\r\n东门球机,10.0.0.8,554\r\n,10.0.0.9,554\r\n西门枪机,10.0.0.10,70000\r\n");

        harness.Files.ImportContent = csv;

        await harness.ViewModel.ImportCsvCommand.ExecuteAsync(null);

        harness.Assets.Items.ShouldHaveSingleItem("只有合法的行被写入。");
        harness.Assets.Items[0].Name.ShouldBe("东门球机");

        harness.ViewModel.HasImportResult.ShouldBeTrue();
        harness.ViewModel.IsPartialImport.ShouldBeTrue("部分成功不是失败，也不是成功。");
        harness.ViewModel.IsFailedImport.ShouldBeFalse();
        harness.ViewModel.ImportStatusText.ShouldBe("部分成功");

        // 被拒绝的行必须逐行给出行号与原因，不静默丢弃。
        harness.ViewModel.ImportIssues.Count.ShouldBe(2);
        harness.ViewModel.ImportIssues.ShouldContain(issue => issue.Contains("第 3 行"));
        harness.ViewModel.ImportIssues.ShouldContain(issue => issue.Contains("第 4 行"));
        harness.ViewModel.ImportIssues.ShouldContain(issue => issue.Contains("端口"));
    }

    [Fact]
    public async Task Import_with_a_fatal_error_should_fail_without_writing_anything()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.Files.ImportContent = Encoding.UTF8.GetBytes("host,port\r\n10.0.0.8,554\r\n");

        await harness.ViewModel.ImportCsvCommand.ExecuteAsync(null);

        harness.Assets.Items.ShouldBeEmpty("缺少必需列时不得写入任何记录。");
        harness.ViewModel.IsFailedImport.ShouldBeTrue();
        harness.ViewModel.ImportStatusText.ShouldBe("导入失败");
        harness.ViewModel.ImportSummary.ShouldContain("name");
    }

    [Fact]
    public async Task Cancelling_the_file_dialog_should_be_reported_as_cancelled_not_failed()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.Files.ImportContent = null;

        await harness.ViewModel.ImportCsvCommand.ExecuteAsync(null);

        harness.ViewModel.ActionFeedback.ShouldContain("取消");
        harness.ViewModel.HasImportResult.ShouldBeFalse("用户还没开始导入，不该有导入结论。");
        harness.ViewModel.IsFailedImport.ShouldBeFalse();
    }

    [Fact]
    public async Task Import_should_strip_inline_credentials_and_warn_about_it()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.Files.ImportContent = Encoding.UTF8.GetBytes(
            "name,host\r\n东门球机,rtsp://admin:Passw0rd@10.0.0.8:554\r\n");

        await harness.ViewModel.ImportCsvCommand.ExecuteAsync(null);

        var saved = harness.Assets.Items.ShouldHaveSingleItem();
        saved.Host.ShouldBe("10.0.0.8");
        saved.Port.ShouldBe(554);
        saved.CredentialReference.ShouldBeNull("内嵌凭据必须被丢弃，而不是存成引用键。");

        harness.ViewModel.ImportIssues.ShouldContain(issue => issue.Contains("丢弃"));
    }

    [Fact]
    public async Task Export_should_write_only_visible_assets_and_never_any_plaintext_password()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        harness.Assets.Items.Add(NewAsset("asset-1", "东门球机", "10.0.0.8", AssetKind.Camera, "海康", ["园区"]));
        harness.Assets.Items.Add(NewAsset("asset-2", "西门枪机", "10.0.0.9", AssetKind.Camera, "大华", ["园区"]));

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.SearchText = "东门";
        await harness.ViewModel.ExportCsvCommand.ExecuteAsync(null);

        harness.Files.WrittenContent.ShouldNotBeNull();

        var text = Encoding.UTF8.GetString(harness.Files.WrittenContent!);

        text.ShouldContain("东门球机");
        text.ShouldNotContain("西门枪机");
        text.ShouldNotContain("password=");

        harness.ViewModel.ActionFeedback.ShouldContain("引用键");
    }

    [Fact]
    public async Task Cancelling_the_export_dialog_should_be_reported_as_cancelled()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        harness.Assets.Items.Add(NewAsset("asset-1", "东门球机", "10.0.0.8", AssetKind.Camera));

        await harness.ViewModel.LoadAsync();

        harness.Files.WriteResult = false;
        await harness.ViewModel.ExportCsvCommand.ExecuteAsync(null);

        harness.ViewModel.ActionFeedback.ShouldContain("取消");
    }

    [Fact]
    public async Task Deleting_an_asset_should_remove_it_from_the_store_and_the_list()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        harness.Assets.Items.Add(NewAsset("asset-1", "东门球机", "10.0.0.8", AssetKind.Camera));

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.SelectedAsset = harness.ViewModel.Assets.Single();
        await harness.ViewModel.DeleteAssetCommand.ExecuteAsync(null);

        harness.Assets.Items.ShouldBeEmpty();
        harness.ViewModel.Assets.ShouldBeEmpty();
        harness.ViewModel.SelectedAsset.ShouldBeNull();
    }

    [Fact]
    public async Task Switching_project_should_clear_the_previous_project_assets()
    {
        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = ProjectId,
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        harness.Projects.Items.Add(new Project
        {
            Id = "prj-2",
            Name = "西门视频项目",
            CreatedAt = DateTimeOffset.Now.AddMinutes(-1)
        });

        harness.Assets.Items.Add(NewAsset("asset-1", "东门球机", "10.0.0.8", AssetKind.Camera));

        await harness.ViewModel.LoadAsync();
        harness.ViewModel.Assets.ShouldHaveSingleItem();

        harness.ViewModel.SelectedProject = harness.ViewModel.Projects
            .First(project => project.Id == "prj-2");

        // 切换项目后旧项目的资产不得残留，否则用户会以为它们属于新项目。
        harness.ViewModel.Assets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Import_without_a_project_should_explain_instead_of_silently_doing_nothing()
    {
        var harness = new AssetCatalogHarness();
        await harness.ViewModel.LoadAsync();

        await harness.ViewModel.ImportCsvCommand.ExecuteAsync(null);

        harness.ViewModel.ActionFeedback.ShouldContain("项目");
        harness.Assets.UpsertCallCount.ShouldBe(0);
    }

    private static Asset NewAsset(
        string id,
        string name,
        string host,
        AssetKind kind,
        string? vendor = null,
        string[]? tags = null) =>
        new()
        {
            Id = id,
            ProjectId = ProjectId,
            Name = name,
            Host = host,
            Kind = kind,
            Vendor = vendor,
            Tags = tags ?? []
        };
}
