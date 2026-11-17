using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdbDock.Core;
using Microsoft.Win32;

namespace AdbDock.App;

public sealed class SettingsWindow : Window
{
    private readonly TextBox adbPath = new(), scrcpyPath = new(), screenshots = new();
    private readonly CheckBox topmost = new() { Content = "窗口置顶" }, startup = new() { Content = "登录 Windows 后自动启动" };
    private readonly TextBlock feedback = new() { Foreground = Palette.Gray, Margin = new Thickness(0, 12, 0, 0) };
    public AppSettings Value { get; private set; }
    public SettingsWindow(AppSettings settings, string dataDirectory)
    {
        Value = settings;
        Title = "设置 · ADB Dock"; Width = 520; Height = 650; ShowInTaskbar = false; ResizeMode = ResizeMode.CanMinimize; Background = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "SettingsWindow");
        System.Windows.Automation.AutomationProperties.SetAutomationId(startup, "StartupCheckBox");
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "让连接按你的习惯工作", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        AddPath(root, "ADB 路径（留空自动查找）", adbPath, settings.AdbPath, false, "AdbPath");
        AddPath(root, "scrcpy 路径（留空自动查找）", scrcpyPath, settings.ScrcpyPath, false, "ScrcpyPath");
        AddPath(root, "截图默认目录", screenshots, settings.ScreenshotDirectory, true, "ScreenshotDirectory");
        topmost.IsChecked = settings.Topmost; startup.IsChecked = settings.AutoStart;
        root.Children.Add(topmost); root.Children.Add(startup);
        root.Children.Add(new TextBlock { Text = "点击贴边箭头展开后，面板保持显示。\n拖动标题栏到屏幕边缘并松开，即可收起。", FontSize = 11, Foreground = Palette.Gray, Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(new TextBlock { Text = "查找顺序：指定路径 → 系统 PATH → 随附工具。\n配置与本地错误日志：" + dataDirectory, FontSize = 11, Foreground = Palette.Gray, Margin = new Thickness(0, 12, 0, 10) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var detect = new Button { Content = "检测工具", Margin = new Thickness(0, 0, 8, 0) };
        detect.Click += async (_, _) =>
        {
            detect.IsEnabled = false;
            feedback.Text = "正在检测…";
            try
            {
                var adb = ToolResolver.Resolve(adbPath.Text, "adb.exe");
                var adbVersion = await ProcessRunner.RunAsync(adb, new[] { "version" }, TimeSpan.FromSeconds(8)); adbVersion.EnsureSuccess();
                var scrcpy = ToolResolver.Resolve(scrcpyPath.Text, "scrcpy.exe");
                var scrcpyVersion = await ProcessRunner.RunAsync(scrcpy, new[] { "--version" }, TimeSpan.FromSeconds(8)); scrcpyVersion.EnsureSuccess();
                feedback.Text = adbVersion.Output.Trim() + "\n" + scrcpyVersion.Output.Trim();
            }
            catch (Exception ex) { feedback.Text = ex.Message; }
            finally { detect.IsEnabled = true; }
        };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存", IsDefault = true, Background = (Brush)FindResource("Accent"), Foreground = Brushes.White };
        System.Windows.Automation.AutomationProperties.SetAutomationId(save, "SaveSettingsButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(cancel, "CancelSettingsButton");
        save.Click += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(adbPath.Text)) ToolResolver.Resolve(adbPath.Text, "adb.exe");
                if (!string.IsNullOrWhiteSpace(scrcpyPath.Text)) ToolResolver.Resolve(scrcpyPath.Text, "scrcpy.exe");
                var directory = Path.GetFullPath(screenshots.Text.Trim());
                Value = new AppSettings { AdbPath = adbPath.Text.Trim(), ScrcpyPath = scrcpyPath.Text.Trim(), ScreenshotDirectory = directory, Topmost = topmost.IsChecked == true, AutoStart = startup.IsChecked == true };
                DialogResult = true;
            }
            catch (Exception ex) { feedback.Text = ex.Message; }
        };
        buttons.Children.Add(detect); buttons.Children.Add(cancel); buttons.Children.Add(save);
        root.Children.Add(buttons); root.Children.Add(feedback);
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "未知";
        var versionInfo = new TextBlock
        {
            Text = "ADB Dock · v" + version, FontSize = 11, Foreground = Palette.Gray,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 0, 22, 16)
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(versionInfo, "AppVersionInfo");
        var layout = new DockPanel();
        DockPanel.SetDock(versionInfo, Dock.Bottom);
        layout.Children.Add(versionInfo);
        layout.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
    }
    private void AddPath(Panel root, string label, TextBox text, string value, bool folder, string automationId)
    {
        root.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Palette.Gray, Margin = new Thickness(0, 8, 0, 5) });
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        var browse = new Button { Content = "浏览", Margin = new Thickness(7, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) =>
        {
            if (folder)
            {
                var picker = new OpenFolderDialog { Title = "选择截图目录", Multiselect = false };
                if (picker.ShowDialog(this) == true) text.Text = picker.FolderName;
            }
            else
            {
                var picker = new OpenFileDialog { Title = label, Filter = "可执行文件|*.exe", CheckFileExists = true };
                if (picker.ShowDialog(this) == true) text.Text = picker.FileName;
            }
        };
        text.Text = value;
        System.Windows.Automation.AutomationProperties.SetAutomationId(text, automationId);
        row.Children.Add(browse); row.Children.Add(text); root.Children.Add(row);
    }
}
