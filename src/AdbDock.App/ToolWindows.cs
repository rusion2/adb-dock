using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AdbDock.Core;
using Microsoft.Win32;

namespace AdbDock.App;

public class TextWindow : Window
{
    protected readonly TextBox Output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Consolas"), FontSize = 12, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
    protected readonly TextBlock Status = new() { Foreground = Palette.Gray, Margin = new Thickness(0, 8, 0, 0), FontSize = 11 };
    public TextWindow(string title, string text)
    {
        Title = title; Width = 580; Height = 480; MinWidth = 350; MinHeight = 260; ShowInTaskbar = false; Background = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "TextWindow");
        System.Windows.Automation.AutomationProperties.SetAutomationId(Output, "TextOutput");
        System.Windows.Automation.AutomationProperties.SetName(Output, "输出内容");
        var root = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 15) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); bottom.Children.Add(Status); bottom.Children.Add(Buttons); root.Children.Add(bottom);
        var copy = new Button { Content = "复制", Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(Output.Text); Status.Text = "内容已复制；日志与设备信息不会自动上传。"; } catch (Exception ex) { Status.Text = ex.Message; } };
        var export = new Button { Content = "导出", Margin = new Thickness(0, 0, 8, 0) };
        export.Click += (_, _) =>
        {
            try
            {
                var picker = new SaveFileDialog { Title = "导出当前内容", Filter = "文本文件|*.txt", FileName = "AdbDock-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", OverwritePrompt = true };
                if (picker.ShowDialog(this) == true) { System.IO.File.WriteAllText(picker.FileName, Output.Text); Status.Text = "已导出：" + picker.FileName; }
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        };
        var close = new Button { Content = "关闭", IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "DialogCloseButton");
        close.Click += (_, _) => Close();
        Buttons.Children.Add(copy); Buttons.Children.Add(export); Buttons.Children.Add(close);
        Output.Text = text;
        root.Children.Add(Output); Content = root;
    }
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

public sealed class LogWindow : TextWindow
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentQueue<string> queue = new();
    private readonly LogFilterBuffer buffer = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBox filterInput = new() { MaxLength = 2048 };
    private readonly TextBlock filterStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 58, Margin = new Thickness(0, 5, 0, 10) };
    private volatile bool paused;
    private Task? reader;
    public LogWindow(string serial, AdbOperations service) : base("Logcat · " + serial, "")
    {
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
        var root = (DockPanel)Content;
        root.Children.Insert(root.Children.IndexOf(Output), filterPanel);
        filterInput.TextChanged += (_, _) => { filterTimer.Stop(); filterTimer.Start(); };
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); ApplyFilter(); };
        clear.Click += (_, _) =>
        {
            filterInput.Clear(); filterTimer.Stop(); ApplyFilter(); filterInput.Focus();
        };
        UpdateFilterStatus();
        var pause = new Button { Content = "暂停显示", Margin = new Thickness(0, 0, 8, 0) };
        pause.Click += (_, _) => { paused = !paused; pause.Content = paused ? "继续显示" : "暂停显示"; Status.Text = paused ? "显示已暂停；后台仍在读取并丢弃新日志。" : "正在读取真实日志。"; while (queue.TryDequeue(out _)) { } };
        Buttons.Children.Insert(0, pause);
        timer.Tick += (_, _) =>
        {
            var chunk = new List<string>();
            for (var i = 0; i < 500 && queue.TryDequeue(out var line); i++) chunk.Add(line);
            if (chunk.Count == 0 || paused) return;
            if (buffer.Append(chunk))
            {
                if (buffer.LastRemovedVisibleLines > 0) RenderOutput();
                else if (buffer.LastAppendedVisibleLines.Count > 0)
                {
                    Output.AppendText(string.Join(Environment.NewLine, buffer.LastAppendedVisibleLines) + Environment.NewLine);
                    Output.ScrollToEnd();
                }
            }
            UpdateFilterStatus();
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
                await service.ReadLogcatAsync(serial, line =>
                {
                    if (paused) return;
                    queue.Enqueue(line.Length > LogFilterBuffer.MaximumLineLength ? line[..LogFilterBuffer.MaximumLineLength] : line);
                    while (queue.Count > 3000) queue.TryDequeue(out _);
                }, cancellation.Token);
                if (!cancellation.IsCancellationRequested) Status.Text = "日志会话已结束。";
            }
            catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) Status.Text = "设备已断开，日志会话已停止。"; }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested) Status.Text = "日志读取失败：" + ex.Message; }
        }
    }

    private void ApplyFilter()
    {
        if (buffer.SetFilter(filterInput.Text)) RenderOutput();
        UpdateFilterStatus();
    }

    private void RenderOutput()
    {
        Output.Text = string.Join(Environment.NewLine, buffer.VisibleLines);
        if (buffer.VisibleLines.Count > 0) Output.AppendText(Environment.NewLine);
        Output.ScrollToEnd();
    }

    private void UpdateFilterStatus()
    {
        filterStatus.Text = buffer.FilterError ?? $"显示 {buffer.VisibleLines.Count:N0} / 缓存 {buffer.TotalCount:N0} 行；复制和导出仅包含当前显示的日志。";
        filterStatus.Foreground = buffer.FilterError is null ? Palette.Gray : Palette.Red;
        filterStatus.ToolTip = filterStatus.Text;
    }
}
