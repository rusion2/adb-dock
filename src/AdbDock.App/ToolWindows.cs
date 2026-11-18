using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdbDock.Core;
using Microsoft.Win32;

namespace AdbDock.App;

public abstract class OutputWindow : Window
{
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
    protected readonly TextBlock Status = new() { Foreground = Palette.Gray, Margin = new Thickness(0, 8, 0, 0), FontSize = 11 };
    protected readonly DockPanel Layout = new() { Margin = new Thickness(20) };
    protected internal abstract string GetOutputText();

    protected OutputWindow(string title)
    {
        Title = title; Width = 580; Height = 480; MinWidth = 350; MinHeight = 260; ShowInTaskbar = false; Background = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "TextWindow");
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 15) };
        DockPanel.SetDock(heading, Dock.Top); Layout.Children.Add(heading);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); bottom.Children.Add(Status); bottom.Children.Add(Buttons); Layout.Children.Add(bottom);
        var copy = new Button { Content = "复制", Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(GetOutputText()); Status.Text = "内容已复制；日志与设备信息不会自动上传。"; } catch (Exception ex) { Status.Text = ex.Message; } };
        var export = new Button { Content = "导出", Margin = new Thickness(0, 0, 8, 0) };
        export.Click += (_, _) =>
        {
            try
            {
                var picker = new SaveFileDialog { Title = "导出当前内容", Filter = "文本文件|*.txt", FileName = "AdbDock-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", OverwritePrompt = true };
                if (picker.ShowDialog(this) == true) { System.IO.File.WriteAllText(picker.FileName, GetOutputText()); Status.Text = "已导出：" + picker.FileName; }
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        };
        var close = new Button { Content = "关闭", IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "DialogCloseButton");
        close.Click += (_, _) => Close();
        Buttons.Children.Add(copy); Buttons.Children.Add(export); Buttons.Children.Add(close);
        Content = Layout;
    }
}

public class TextWindow : OutputWindow
{
    protected readonly TextBox Output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Consolas"), FontSize = 12, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    public TextWindow(string title, string text, bool focusText = false) : base(title)
    {
        System.Windows.Automation.AutomationProperties.SetAutomationId(Output, "TextOutput");
        System.Windows.Automation.AutomationProperties.SetName(Output, "输出内容");
        Output.Text = text;
        Layout.Children.Add(Output);
        if (focusText) FocusManager.SetFocusedElement(this, Output);
    }
    protected internal override string GetOutputText() => Output.Text;
}

public sealed class KeysWindow : Window
{
    public KeysWindow(string device, Func<string, Task<bool>> send, string serial)
    {
        Title = "按键控制 · " + device; Width = 350; Height = 350; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Background = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "KeysWindow");
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = device, FontSize = 18, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = serial, ToolTip = serial, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) });
        root.Children.Add(new TextBlock { Text = "按键立即发送到此设备，请确认目标。", Foreground = Palette.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 12) });
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        var result = new TextBlock { FontSize = 11, Foreground = Palette.Gray, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (key, label) in new[] { ("Home", "Home"), ("Back", "返回"), ("Recent", "最近任务"), ("Wake", "亮屏") })
        {
            var button = new Button { Content = label, Margin = new Thickness(3), Height = 42 };
            button.Click += async (_, _) =>
            {
                grid.IsEnabled = false; result.Text = "发送中…";
                try { result.Text = await send(key) ? "按键已发送" : "未完成，请查看主窗口的操作反馈"; }
                finally { grid.IsEnabled = true; }
            };
            grid.Children.Add(button);
        }
        root.Children.Add(grid); root.Children.Add(result);
        var close = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "KeysCloseButton");
        close.Click += (_, _) => Close(); root.Children.Add(close);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

public sealed class LogWindow : OutputWindow
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly LogFilterBuffer buffer = new();
    private readonly ListBox linesView = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, SelectionMode = SelectionMode.Extended };
    private ScrollViewer? scroll;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBox filterInput = new() { MaxLength = 2048 };
    private readonly TextBlock filterStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 58, Margin = new Thickness(0, 5, 0, 10) };
    private bool paused;
    private Task? reader;
    public LogWindow(string serial, AdbOperations service) : base("Logcat · " + serial)
    {
        MinWidth = 420;
        Status.Text = "正在读取真实日志；最多缓存最近 20,000 行（原始文本约 32 MiB）。";
        var filterPanel = new StackPanel();
        var label = new TextBlock { Text = "正则筛选（留空显示全部）", FontSize = 11, Foreground = Palette.Gray, Margin = new Thickness(0, 0, 0, 5) };
        filterPanel.Children.Add(label);
        var filterRow = new DockPanel();
        var clear = new Button { Content = "清除", Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(filterInput, "LogFilterInput");
        System.Windows.Automation.AutomationProperties.SetName(filterInput, "日志正则筛选");
        System.Windows.Automation.AutomationProperties.SetLabeledBy(filterInput, label);
        System.Windows.Automation.AutomationProperties.SetAutomationId(clear, "ClearLogFilterButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(filterStatus, "LogFilterStatus");
        filterInput.ToolTip = "示例：error|fatal|WARN。默认区分大小写；(?i)error 可忽略大小写。留空显示全部。";
        DockPanel.SetDock(clear, Dock.Right);
        filterRow.Children.Add(clear); filterRow.Children.Add(filterInput);
        filterPanel.Children.Add(filterRow); filterPanel.Children.Add(filterStatus);
        DockPanel.SetDock(filterPanel, Dock.Top);
        Layout.Children.Add(filterPanel);
        System.Windows.Automation.AutomationProperties.SetAutomationId(linesView, "LogLines");
        System.Windows.Automation.AutomationProperties.SetName(linesView, "日志内容");
        VirtualizingPanel.SetIsVirtualizing(linesView, true);
        VirtualizingPanel.SetVirtualizationMode(linesView, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(linesView, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(linesView, ScrollBarVisibility.Auto);
        var lineStyle = new Style(typeof(TextBlock));
        lineStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        linesView.Resources[typeof(TextBlock)] = lineStyle;
        linesView.ItemsSource = buffer.VisibleRows;
        var rowStyle = new Style(typeof(ListBoxItem));
        rowStyle.Setters.Add(new EventSetter(Control.MouseDoubleClickEvent, new MouseButtonEventHandler((sender, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || sender is not ListBoxItem { Content: LogFilterBuffer.LogLine line }) return;
            e.Handled = true;
            ShowLogText(line.Text);
        })));
        linesView.ItemContainerStyle = rowStyle;
        linesView.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || linesView.SelectedItems.Count == 0) return;
            e.Handled = true;
            if (!e.IsRepeat) ShowLogText(GetSelectedText());
        };
        linesView.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (scroll is not null || e.OriginalSource is not ScrollViewer viewer) return;
            scroll = viewer;
            if (linesView.Items.Count > 0) scroll.ScrollToBottom();
        }));
        linesView.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) =>
        {
            try { Clipboard.SetText(GetSelectedText()); }
            catch (Exception ex) { Status.Text = ex.Message; }
        }, (_, e) => e.CanExecute = linesView.SelectedItems.Count > 0));
        Layout.Children.Add(linesView);
        filterInput.TextChanged += (_, _) => { filterTimer.Stop(); filterTimer.Start(); };
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); ApplyFilter(); };
        clear.Click += (_, _) =>
        {
            filterInput.Clear(); filterTimer.Stop(); ApplyFilter(); filterInput.Focus();
        };
        UpdateFilterStatus();
        var pause = new Button { Content = "暂停显示", Margin = new Thickness(0, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(pause, "LogPauseButton");
        long pausedAt = 0;
        long lastTickLineCount = 0;
        bool applyPendingFilterOnResume = false;
        pause.Click += (_, _) =>
        {
            paused = !paused;
            filterInput.IsEnabled = clear.IsEnabled = !paused;
            if (paused)
            {
                pausedAt = buffer.ReceivedLineCount;
                applyPendingFilterOnResume = filterTimer.IsEnabled;
                filterTimer.Stop();
                pause.Content = "回到最新（新增 0 行）";
                return;
            }

            pause.Content = "暂停显示";
            if (applyPendingFilterOnResume) ApplyFilter();
            RefreshLogView(goToLatest: true);
        };
        Buttons.Children.Insert(0, pause);
        timer.Tick += (_, _) =>
        {
            var count = buffer.ReceivedLineCount;
            if (count == lastTickLineCount) return;
            lastTickLineCount = count;
            if (paused) pause.Content = $"回到最新（新增 {count - pausedAt:N0} 行）";
            else RefreshLogView();
        };
        Loaded += (_, _) => { timer.Start(); reader = ReadAsync(); };
        Closed += async (_, _) =>
        {
            timer.Stop(); filterTimer.Stop(); cancellation.Cancel();
            try { if (reader is not null) await reader; }
            catch (OperationCanceledException) { }
            finally { cancellation.Dispose(); }
        };
        async Task ReadAsync()
        {
            try
            {
                await service.ReadLogcatAsync(serial, buffer.Retain, cancellation.Token);
                if (!cancellation.IsCancellationRequested) Status.Text = "日志会话已结束。";
            }
            catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) Status.Text = "设备已断开，日志会话已停止。"; }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested) Status.Text = "日志读取失败：" + ex.Message; }
        }
    }

    private void ShowLogText(string text)
    {
        var detail = new TextWindow("日志原文", text, focusText: true) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        detail.ShowDialog();
    }

    internal void AppendLines(IEnumerable<string> lines)
    {
        buffer.Retain(lines);
        if (!paused) RefreshLogView();
    }

    private void RefreshLogView(bool goToLatest = false)
    {
        bool follow = scroll is null || scroll.ScrollableHeight - scroll.VerticalOffset < 0.5;
        double oldOffset = scroll?.VerticalOffset ?? 0;
        if (buffer.UpdateVisible())
        {
            linesView.Items.Refresh();
            if (!goToLatest && !follow) scroll?.ScrollToVerticalOffset(Math.Max(0, oldOffset - buffer.LastRemovedVisibleLines));
            else goToLatest = true;
        }
        if (goToLatest) scroll?.ScrollToBottom();
        UpdateFilterStatus();
    }

    private void ApplyFilter()
    {
        if (buffer.SetFilter(filterInput.Text))
        {
            linesView.Items.Refresh();
            scroll?.ScrollToBottom();
        }
        UpdateFilterStatus();
    }

    internal string GetSelectedText()
    {
        var selected = linesView.SelectedItems.Cast<LogFilterBuffer.LogLine>().ToHashSet();
        return string.Join(Environment.NewLine, buffer.VisibleRows.Where(selected.Contains).Select(row => row.Text));
    }

    protected internal override string GetOutputText() => buffer.VisibleLines.Count == 0
        ? string.Empty : string.Join(Environment.NewLine, buffer.VisibleLines) + Environment.NewLine;

    private void UpdateFilterStatus()
    {
        filterStatus.Text = buffer.FilterError ?? $"显示 {buffer.VisibleLines.Count:N0} / 缓存 {buffer.TotalCount:N0} 行。复制和导出仅包含当前显示的日志。双击日志行或按 Enter 查看原文，可选取片段复制。";
        filterStatus.Foreground = buffer.FilterError is null ? Palette.Gray : Palette.Red;
        filterStatus.ToolTip = filterStatus.Text;
    }
}
