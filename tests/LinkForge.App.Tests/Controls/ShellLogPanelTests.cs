using System.Windows;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.ViewModels;
using LinkForge.App.Views;
using LinkForge.Application.Logging;

namespace LinkForge.App.Tests.Controls;

public sealed class ShellLogPanelTests
{
    [Fact]
    public void Shell_log_panel_should_bind_real_entries_and_copy_export_commands()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var logs = new StructuredLogService(new InMemoryLogStore());
            logs.Publish(new LogEntry(
                DateTimeOffset.Now,
                LfLogLevel.Information,
                "Network",
                "run-1",
                "10.0.0.8",
                "连接成功",
                "network.connected"));

            var exporter = new FakeExporter();
            var viewModel = new ShellViewModel(
                structuredLogs: logs,
                logExporter: exporter);
            viewModel.ToggleLogPanelCommand.Execute(null);

            var window = new ShellWindow(viewModel)
            {
                Width = 1366,
                Height = 768
            };

            window.Show();
            Pump();
            window.UpdateLayout();

            try
            {
                var viewer = (LogViewer)window.FindName("ShellLogViewer");
                var panel = (FrameworkElement)window.FindName("LogPanelHost");

                panel.Visibility.ShouldBe(Visibility.Visible);
                viewer.Entries.ShouldBeSameAs(viewModel.LogEntries);
                viewer.TotalCount.ShouldBe(1);
                viewer.CopyCommand.ShouldBeSameAs(viewModel.CopyLogsCommand);
                viewer.ExportCommand.ShouldBeSameAs(viewModel.ExportLogsCommand);
            }
            finally
            {
                window.Close();
                Pump();
            }
        });
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

    private sealed class InMemoryLogStore : IStructuredLogStore
    {
        public string DirectoryPath => "memory";

        public string CurrentFilePath => "memory.jsonl";

        public Task AppendAsync(
            StructuredLogRecord record,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StructuredLogRecord>> ReadRecentAsync(
            int maximumEntries,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StructuredLogRecord>>([]);
    }

    private sealed class FakeExporter : ILogPanelExporter
    {
        public void Copy(string text)
        {
        }

        public Task<string?> ExportAsync(
            string suggestedFileName,
            string content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
