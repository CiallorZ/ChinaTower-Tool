using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Tests.DesignTokens;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 日志查看器行为测试（UI 规范 §10 虚拟化、§5.3 列表）。
/// </summary>
/// <remarks>
/// 重点关注过滤与计数的正确性。真实缺陷模式是：
/// 日志过滤后计数不刷新，用户看到"共 1200 行，当前显示 1200 行"，
/// 实际屏幕上只有 3 行——计数与实际不符会让人以为工具坏了。
/// </remarks>
public class LogViewerTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 10, 15, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Without_filter_all_entries_should_be_counted()
    {
        var (total, visible) = StaRunner.Run(() =>
        {
            var viewer = CreateViewer(CreateEntries(10));
            return (viewer.TotalCount, viewer.VisibleCount);
        });

        total.ShouldBe(10);
        visible.ShouldBe(10);
    }

    [Fact]
    public void Minimum_level_should_filter_out_lower_levels()
    {
        // 级别是有序的：选 Warning 时必须同时看到 Error 与 Critical，
        // 否则用户会漏掉更严重的问题。
        var (total, visible) = StaRunner.Run(() =>
        {
            var viewer = CreateViewer(CreateOnePerLevel());
            viewer.MinimumLevel = LfLogLevel.Warning;

            return (viewer.TotalCount, viewer.VisibleCount);
        });

        total.ShouldBe(6);
        visible.ShouldBe(3);
    }

    [Fact]
    public void Search_should_filter_by_message_content()
    {
        var visible = StaRunner.Run(() =>
        {
            var viewer = CreateViewer(CreateEntries(50));
            viewer.SearchText = "connection";

            return viewer.VisibleCount;
        });

        visible.ShouldBe(50);
    }

    [Fact]
    public void Search_should_exclude_non_matching_entries()
    {
        var visible = StaRunner.Run(() =>
        {
            var viewer = CreateViewer(
            [
                Entry(LfLogLevel.Information, "connected to broker"),
                Entry(LfLogLevel.Information, "timeout waiting for ack")
            ]);

            viewer.SearchText = "broker";

            return viewer.VisibleCount;
        });

        visible.ShouldBe(1);
    }

    [Fact]
    public void Search_should_be_case_insensitive()
    {
        // 用户不会记得自己打的是大写还是小写。
        var visible = StaRunner.Run(() =>
        {
            var viewer = CreateViewer([Entry(LfLogLevel.Information, "MQTT CONNECT")]);
            viewer.SearchText = "mqtt";

            return viewer.VisibleCount;
        });

        visible.ShouldBe(1);
    }

    [Fact]
    public void Search_should_cover_module_target_and_event_id()
    {
        var visible = StaRunner.Run(() =>
        {
            var viewer = CreateViewer(
            [
                new LogEntry(
                    FixedTime,
                    LfLogLevel.Information,
                    "Network",
                    "run-42",
                    "camera-01",
                    "connected",
                    "network.tcp.connected")
            ]);

            viewer.SearchText = "camera-01";
            var byTarget = viewer.VisibleCount;

            viewer.SearchText = "network.tcp.connected";
            var byEvent = viewer.VisibleCount;

            viewer.SearchText = "Network";
            var byModule = viewer.VisibleCount;

            return (byTarget, byEvent, byModule);
        });

        visible.byTarget.ShouldBe(1);
        visible.byEvent.ShouldBe(1);
        visible.byModule.ShouldBe(1);
    }

    [Fact]
    public void Empty_source_should_expose_a_real_empty_message()
    {
        var message = StaRunner.Run(() =>
        {
            var viewer = CreateViewer([]);
            return viewer.EmptyMessage;
        });

        message.ShouldContain("暂无日志");
    }

    [Fact]
    public void Filter_with_no_matches_should_not_claim_the_source_is_empty()
    {
        var (hasVisible, message) = StaRunner.Run(() =>
        {
            var viewer = CreateViewer([Entry(LfLogLevel.Information, "connected")]);
            viewer.SearchText = "missing";

            return (viewer.HasVisibleEntries, viewer.EmptyMessage);
        });

        hasVisible.ShouldBeFalse();
        message.ShouldContain("没有匹配");
    }

    [Fact]
    public void Toolbar_should_expose_copy_and_export_commands_when_entries_exist()
    {
        Exception? failure = null;

        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var command = new AlwaysCommand();
            var viewer = new LogViewer
            {
                Entries = CreateEntries(1),
                CopyCommand = command,
                ExportCommand = command
            };
            var window = new Window { Content = viewer, Width = 900, Height = 400 };

            window.Loaded += (_, _) =>
            {
                try
                {
                    viewer.UpdateLayout();
                    var buttons = FindButtons(viewer);
                    var copy = buttons.Single(button => Equals(button.Content, "复制可见"));
                    var export = buttons.Single(button => Equals(button.Content, "导出"));

                    copy.Command.ShouldBeSameAs(command);
                    export.Command.ShouldBeSameAs(command);
                    copy.IsEnabled.ShouldBeTrue();
                    export.IsEnabled.ShouldBeTrue();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    window.Close();
                }
            };

            window.Show();
            Pump();

            if (window.IsVisible)
            {
                window.Close();
            }
        });

        if (failure is not null)
        {
            throw new InvalidOperationException("日志工具条命令未正确绑定。", failure);
        }
    }

    [Fact]
    public void Combined_level_and_search_should_both_apply()
    {
        // 两个条件是与关系。只应用其中一个会让用户看到不该看到的日志。
        var visible = StaRunner.Run(() =>
        {
            var viewer = CreateViewer(
            [
                Entry(LfLogLevel.Debug, "broker probe"),
                Entry(LfLogLevel.Error, "broker refused"),
                Entry(LfLogLevel.Error, "other failure")
            ]);

            viewer.MinimumLevel = LfLogLevel.Error;
            viewer.SearchText = "broker";

            return viewer.VisibleCount;
        });

        visible.ShouldBe(1);
    }

    [Fact]
    public void Is_filtered_should_reflect_actual_filter_state()
    {
        var (off, withLevel, withSearch) = StaRunner.Run(() =>
        {
            var plain = CreateViewer(CreateEntries(3));
            var level = CreateViewer(CreateEntries(3));
            level.MinimumLevel = LfLogLevel.Warning;

            var search = CreateViewer(CreateEntries(3));
            search.SearchText = "x";

            return (plain.IsFiltered, level.IsFiltered, search.IsFiltered);
        });

        off.ShouldBeFalse();
        withLevel.ShouldBeTrue();
        withSearch.ShouldBeTrue();
    }

    [Fact]
    public void Counts_should_follow_appended_entries()
    {
        // 长任务持续追加日志，计数必须跟上，否则用户以为日志停了。
        var (before, after) = StaRunner.Run(() =>
        {
            var entries = new ObservableCollection<LogEntry>(CreateEntries(3));
            var viewer = CreateViewer(entries);

            var before = viewer.VisibleCount;
            entries.Add(Entry(LfLogLevel.Information, "late entry"));

            return (before, viewer.VisibleCount);
        });

        before.ShouldBe(3);
        after.ShouldBe(4);
    }

    [Fact]
    public void Repeated_filter_changes_should_not_accumulate_handlers()
    {
        // 曾经的实现每次刷新都 Filter += ...，导致处理器累积、
        // 筛选结果不可预测。这里用"多次变更后结果仍正确"来锁定该回归。
        var visible = StaRunner.Run(() =>
        {
            var viewer = CreateViewer(CreateOnePerLevel());

            viewer.MinimumLevel = LfLogLevel.Error;
            var atError = viewer.VisibleCount;

            viewer.MinimumLevel = LfLogLevel.Warning;
            var atWarning = viewer.VisibleCount;

            viewer.MinimumLevel = null;

            return (atError, atWarning, viewer.VisibleCount);
        });

        visible.atError.ShouldBe(2);
        visible.atWarning.ShouldBe(3);
        visible.Item3.ShouldBe(6);
    }

    [Fact]
    public void Entries_entered_through_the_control_should_never_be_plaintext()
    {
        // 与日志脱敏契约联动：控件展示的内容必须已是脱敏后的。
        var rendered = StaRunner.Run(() =>
        {
            var viewer = CreateViewer(
            [Entry(LfLogLevel.Information, "rtsp://admin:S3cret@10.0.0.1/live")]);

            return CollectText(viewer);
        });

        rendered.Contains("S3cret", StringComparison.Ordinal).ShouldBeFalse(
            "日志查看器渲染了明文凭据。");
    }

    [Fact]
    public void Rows_should_resolve_design_tokens_when_measured()
    {
        // 真实缺陷记录（阶段 02，运行时崩溃捕获）：
        // 项模板内部用 {StaticResource} 引用令牌时，WPF 在 LoadContent 阶段解析，
        // 那时模板根元素还不在逻辑树上，沿树查找落空，
        // 抛 XamlParseException"无法找到名为 LfFontSizeCaption 的资源"。
        Exception? failure = null;

        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewer = new LogViewer();
            var window = new Window { Content = viewer, Width = 900, Height = 400 };

            window.Loaded += (_, _) =>
            {
                try
                {
                    // 必须先等模板应用完：ListBox.ItemsSource 绑定的是本控件的 View，
                    // 而 View 在 OnApplyTemplate 里才创建。
                    // 构造时就赋 Entries 的话 View 仍是 null，Items 为空，
                    // ScrollIntoView 会抛"ItemCollection 没有内部集合"。
                    viewer.Entries = CreateEntries(5);
                    viewer.UpdateLayout();

                    var list = FindListBox(viewer);
                    list.ShouldNotBeNull("未找到日志列表控件。");

                    list!.UpdateLayout();

                    // 真正让每一行的项模板被实例化：
                    // 只有走到 ScrollViewer 的呈现阶段，容器才会按视口逐行生成。
                    var presenter = FindScrollContentPresenter(list);
                    presenter?.UpdateLayout();

                    for (var i = 0; i < 5; i++)
                    {
                        // 注意 ContainerFromIndex 对未显示的行返回 null，
                        // 因此必须先把行滚动进视口，而不是直接按索引取。
                        list.ScrollIntoView(list.Items[i]);

                        var container = list.ItemContainerGenerator.ContainerFromIndex(i);
                        container.ShouldNotBeNull($"第 {i} 行的容器未生成。");
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    window.Close();
                }
            };

            window.Show();

            for (var i = 0; i < 60; i++)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => { }));
            }

            if (window.IsVisible)
            {
                window.Close();
            }
        });

        if (failure is not null)
        {
            throw new InvalidOperationException("日志行在呈现阶段抛出异常。", failure);
        }
    }

    private static ListBox? FindListBox(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is ListBox list)
            {
                return list;
            }

            if (FindListBox(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static List<Button> FindButtons(DependencyObject root)
    {
        var buttons = new List<Button>();

        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is Button button)
            {
                buttons.Add(button);
            }

            buttons.AddRange(FindButtons(child));
        }

        return buttons;
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

    private static ScrollContentPresenter? FindScrollContentPresenter(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is ScrollContentPresenter presenter)
            {
                return presenter;
            }

            if (FindScrollContentPresenter(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static LogViewer CreateViewer(IEnumerable<LogEntry> entries)
    {
        var viewer = new LogViewer { Entries = entries };
        ThemedHost.Host(viewer);

        return viewer;
    }

    private static List<LogEntry> CreateEntries(int count)
    {
        var entries = new List<LogEntry>();

        for (var i = 0; i < count; i++)
        {
            entries.Add(Entry(LfLogLevel.Information, $"connection attempt {i}"));
        }

        return entries;
    }

    private static List<LogEntry> CreateOnePerLevel() =>
    [
        Entry(LfLogLevel.Trace, "trace"),
        Entry(LfLogLevel.Debug, "debug"),
        Entry(LfLogLevel.Information, "information"),
        Entry(LfLogLevel.Warning, "warning"),
        Entry(LfLogLevel.Error, "error"),
        Entry(LfLogLevel.Critical, "critical")
    ];

    private static LogEntry Entry(LfLogLevel level, string message) =>
        new(FixedTime, level, "Network", "run-1", null, message);

    private static string CollectText(DependencyObject root)
    {
        var collected = new System.Text.StringBuilder();

        Walk(root);

        return collected.ToString();

        void Walk(DependencyObject node)
        {
            if (node is TextBlock textBlock)
            {
                collected.Append(textBlock.Text).Append('|');
            }

            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private sealed class AlwaysCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
    /// <summary>
    /// 锁住"替换日志源后界面不再更新"这个真实缺陷。
    /// </summary>
    /// <remarks>
    /// 缺陷根因：<c>View</c> 曾是普通 CLR 属性，模板里的
    /// <c>ItemsSource="{Binding View, ...}"</c> 收不到变更通知，
    /// 于是模板应用之后再换 Entries，Items 依旧空着、界面一行都不显示。
    ///
    /// 这个缺陷在构造时赋值完全看不出来（那时 OnApplyTemplate 还没跑），
    /// 所以必须专门有一条"先渲染、后换源"的用例，否则它随时可能回来。
    /// </remarks>
    [Fact]
    public void Replacing_entries_after_the_template_is_applied_should_repopulate_rows()
    {
        Exception? failure = null;

        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewer = new LogViewer();
            var window = new Window { Content = viewer, Width = 900, Height = 400 };

            window.Loaded += (_, _) =>
            {
                try
                {
                    viewer.Entries = CreateEntries(3);
                    viewer.UpdateLayout();

                    var list = FindListBox(viewer);
                    list.ShouldNotBeNull("未找到日志列表控件。");

                    // 关键：模板已应用之后再换数据源。
                    viewer.Entries = CreateEntries(7);
                    viewer.UpdateLayout();
                    list!.UpdateLayout();

                    list.Items.Count.ShouldBe(7, "替换日志源后行数未更新。");
                    viewer.TotalCount.ShouldBe(7);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    window.Close();
                }
            };

            window.Show();

            for (var i = 0; i < 60; i++)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => { }));
            }

            if (window.IsVisible)
            {
                window.Close();
            }
        });

        if (failure is not null)
        {
            throw new InvalidOperationException("替换日志源后未能重新填充行。", failure);
        }
    }
}
