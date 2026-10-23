using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkForge.App.Controls;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.Tests.ViewModels;
using LinkForge.App.Views.Pages;
using LinkForge.Application.Projects;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;

namespace LinkForge.App.Tests.Controls;

/// <summary>项目包在设备与资产页中的真实渲染（LF-015）。</summary>
public class AssetCatalogProjectPackagePageTests
{
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Import_success_should_render_a_success_badge_and_real_counts()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();
            Drive(harness.ViewModel.LoadAsync());

            var package = NewPackage();
            harness.PackageFiles.ImportFile = new ProjectPackageImportFile(
                "success.lfpkg",
                new ProjectPackageCodec().Encode(
                    package.Project,
                    package.Environments,
                    package.Assets));
            Drive(harness.ViewModel.ImportProjectPackageCommand.ExecuteAsync(null));

            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var badges = FindDescendants<StatusBadge>(page)
                .Select(static badge => badge.ResolvedText)
                .ToArray();

            badges.ShouldContain("导入成功");

            var text = CollectText(page);
            text.ShouldContain("已导入项目");
            text.ShouldContain("2 条资产");
            text.ShouldContain("不含明文凭据");

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Running_export_should_render_a_real_cancel_action()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();
            var package = NewPackage();
            harness.Projects.Items.Add(package.Project);
            harness.Environments.Items.AddRange(package.Environments);
            harness.Assets.Items.AddRange(package.Assets);
            harness.PackageFiles.WriteStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            harness.PackageFiles.WriteRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Drive(harness.ViewModel.LoadAsync());

            var export = harness.ViewModel.ExportProjectPackageCommand.ExecuteAsync(null);
            Drive(harness.PackageFiles.WriteStarted.Task);

            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var cancel = FindDescendants<Button>(page)
                .FirstOrDefault(static button =>
                    button.Content as string == "取消导出项目包"
                    && button.Visibility == Visibility.Visible);

            cancel.ShouldNotBeNull("项目包导出运行中必须提供可见的取消入口。");
            cancel!.IsEnabled.ShouldBeTrue();

            var text = CollectText(page);
            text.ShouldContain("正在从本地数据库读取项目并生成项目包");

            harness.ViewModel.CancelProjectPackageExportCommand.Execute(null);
            Drive(export);

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Export_button_should_be_disabled_when_there_is_no_project()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();
            Drive(harness.ViewModel.LoadAsync());

            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var export = FindDescendants<Button>(page)
                .Single(static button => button.Content as string == "导出项目包");

            export.Visibility.ShouldBe(Visibility.Visible);
            export.IsEnabled.ShouldBeFalse("没有项目时不能导出一个不存在的项目包。");

            window.Close();
            Pump();
        });
    }

    private static Window CreateWindow(AssetCatalogPage page) =>
        new()
        {
            Content = page,
            Width = ReferenceWidth,
            Height = ReferenceHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0
        };

    private static void Layout(Window window)
    {
        window.Measure(new Size(ReferenceWidth, ReferenceHeight));
        window.Arrange(new Rect(0, 0, ReferenceWidth, ReferenceHeight));
        window.UpdateLayout();
    }

    private static void Drive(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }

        task.IsCompleted.ShouldBeTrue("异步操作未在 10 秒内完成。");

        if (task.Exception is { } exception)
        {
            throw exception;
        }

        Pump();
    }

    private static void Pump()
    {
        for (var i = 0; i < 60; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
        }
    }

    private static string CollectText(DependencyObject root)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var textBlock in FindDescendants<TextBlock>(root))
        {
            builder.Append(textBlock.Text);
            builder.Append('\n');

            foreach (var run in textBlock.Inlines.OfType<System.Windows.Documents.Run>())
            {
                builder.Append(run.Text);
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static ProjectPackage NewPackage()
    {
        const string projectId = "prj-page-package";

        return new ProjectPackage
        {
            Project = new Project
            {
                Id = projectId,
                Name = "东门视频项目",
                CreatedAt = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(8))
            },
            Environments =
            [
                new EnvironmentProfile
                {
                    Id = "env-page",
                    ProjectId = projectId,
                    Name = "测试环境",
                    IsActive = true
                }
            ],
            Assets =
            [
                new Asset
                {
                    Id = "asset-page-1",
                    ProjectId = projectId,
                    Name = "东门球机",
                    Host = "10.0.0.8",
                    Kind = AssetKind.Camera,
                    CredentialReference = "asset.cam.password"
                },
                new Asset
                {
                    Id = "asset-page-2",
                    ProjectId = projectId,
                    Name = "园区网关",
                    Host = "10.0.0.10",
                    Kind = AssetKind.Gateway
                }
            ]
        };
    }
}
