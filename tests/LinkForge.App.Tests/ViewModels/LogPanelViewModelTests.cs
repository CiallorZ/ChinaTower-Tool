using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;

namespace LinkForge.App.Tests.ViewModels;

public sealed class LogPanelViewModelTests
{
    [Fact]
    public void Copy_should_only_include_entries_matching_level_and_search()
    {
        var logs = new FakeStructuredLogService();
        logs.Entries.Add(Log(LfLogLevel.Information, "alpha started", "network.start"));
        logs.Entries.Add(Log(LfLogLevel.Error, "beta failed", "network.failure"));

        var exporter = new FakeLogPanelExporter();
        var viewModel = new ShellViewModel(structuredLogs: logs, logExporter: exporter)
        {
            LogMinimumLevel = LfLogLevel.Error,
            LogSearchText = "beta"
        };

        viewModel.CopyLogsCommand.CanExecute(null).ShouldBeTrue();
        viewModel.CopyLogsCommand.Execute(null);

        exporter.CopiedText.ShouldNotBeNull();
        exporter.CopiedText.ShouldContain("beta failed");
        exporter.CopiedText.ShouldNotContain("alpha started");
        viewModel.LogActionFeedback.ShouldContain("已复制 1 行");
        viewModel.HasLogActionError.ShouldBeFalse();
    }

    [Fact]
    public async Task Export_should_use_the_same_filtered_view_and_report_the_saved_path()
    {
        var logs = new FakeStructuredLogService();
        logs.Entries.Add(Log(LfLogLevel.Warning, "first", "network.warning"));
        logs.Entries.Add(Log(LfLogLevel.Error, "second", "network.error"));

        var exporter = new FakeLogPanelExporter
        {
            ExportPath = @"C:\temp\linkforge.log"
        };
        var viewModel = new ShellViewModel(structuredLogs: logs, logExporter: exporter);

        await viewModel.ExportLogsCommand.ExecuteAsync(null);

        exporter.ExportedText.ShouldNotBeNull();
        exporter.ExportedText.ShouldContain("first");
        exporter.ExportedText.ShouldContain("second");
        viewModel.LogActionFeedback.ShouldContain(@"C:\temp\linkforge.log");
    }

    [Fact]
    public async Task Cancelled_export_should_be_distinct_from_failure()
    {
        var logs = new FakeStructuredLogService();
        logs.Entries.Add(Log(LfLogLevel.Information, "message", "test.event"));

        var exporter = new FakeLogPanelExporter { ExportPath = null };
        var viewModel = new ShellViewModel(structuredLogs: logs, logExporter: exporter);

        await viewModel.ExportLogsCommand.ExecuteAsync(null);

        viewModel.LogActionFeedback.ShouldContain("已取消");
        viewModel.HasLogActionError.ShouldBeFalse();
    }

    [Fact]
    public void Empty_filter_should_disable_copy_and_export_commands()
    {
        var logs = new FakeStructuredLogService();
        logs.Entries.Add(Log(LfLogLevel.Information, "alpha", "test.alpha"));
        var viewModel = new ShellViewModel(structuredLogs: logs);

        viewModel.LogSearchText = "does-not-exist";

        viewModel.HasVisibleLogs.ShouldBeFalse();
        viewModel.CopyLogsCommand.CanExecute(null).ShouldBeFalse();
        viewModel.ExportLogsCommand.CanExecute(null).ShouldBeFalse();
    }

    private static LogEntry Log(LfLogLevel level, string message, string eventId) =>
        new(
            DateTimeOffset.Now,
            level,
            "Network",
            "run-1",
            "10.0.0.8",
            message,
            eventId);

    private sealed class FakeLogPanelExporter : ILogPanelExporter
    {
        public string? CopiedText { get; private set; }

        public string? ExportedText { get; private set; }

        public string? ExportPath { get; set; } = @"C:\temp\default.log";

        public void Copy(string text) => CopiedText = text;

        public Task<string?> ExportAsync(
            string suggestedFileName,
            string content,
            CancellationToken cancellationToken = default)
        {
            ExportedText = content;
            return Task.FromResult(ExportPath);
        }
    }

    private sealed class FakeStructuredLogService : IStructuredLogService
    {
        public ObservableCollection<LogEntry> Entries { get; } = [];

        public string? StorageError => null;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public void Attach(ObservableCollection<LogEntry> source)
        {
            foreach (var entry in source)
            {
                Entries.Add(entry);
            }

            source.CollectionChanged += OnSourceChanged;
        }

        public void Publish(LogEntry entry) => Entries.Add(entry);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
        }

        private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            foreach (var entry in e.NewItems?.OfType<LogEntry>() ?? [])
            {
                Entries.Add(entry);
            }
        }
    }
}
