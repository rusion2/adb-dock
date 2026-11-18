using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AdbDock.Core;
using Microsoft.Win32;

namespace AdbDock.App;

public sealed class SettingsWindow : Window
{
    private readonly TextBox adbPath = new(), scrcpyPath = new(), screenshots = new();
    private readonly TextBox autoCollapseDelay = new() { Width = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly CheckBox topmost = new() { Content = "窗口置顶" }, startup = new() { Content = "登录 Windows 后自动启动" },
        autoCollapse = new() { Content = "鼠标移开后自动收起面板" };
    private readonly TextBlock feedback = new() { Foreground = Palette.Red, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    public AppSettings Value { get; private set; }
    public SettingsWindow(AppSettings settings, string dataDirectory, bool focusAdb = false)
    {
        Value = settings;
        Title = "设置 · ADB Dock"; Width = 520; Height = 650; ShowInTaskbar = false; ResizeMode = ResizeMode.CanMinimize; Background = Brushes.White;
        AutomationProperties.SetAutomationId(this, "SettingsWindow");
        AutomationProperties.SetAutomationId(startup, "StartupCheckBox");
        AutomationProperties.SetAutomationId(autoCollapse, "AutoCollapseCheckBox");
        AutomationProperties.SetAutomationId(autoCollapseDelay, "AutoCollapseDelayInput");
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "ADB 与 scrcpy", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(DownloadInstructions("下载并解压 Windows 版 ", "Android SDK Platform-Tools",
            "https://developer.android.com/tools/releases/platform-tools", "，点击“浏览”选择 adb.exe。点击“检测工具”，确认通过后保存。"));
        AddPath(root, "ADB 路径（连接设备必需）", adbPath, settings.AdbPath, false, "AdbPath");
        var adbResult = AddDetectionResult(root, adbPath, "ADB", "AdbDetectionResult");
        root.Children.Add(DownloadInstructions("如需投屏，下载并解压 ", "scrcpy 官方发行包",
            "https://github.com/Genymobile/scrcpy", "，浏览选择 scrcpy.exe。"));
        AddPath(root, "scrcpy 路径（仅投屏需要，可留空）", scrcpyPath, settings.ScrcpyPath, false, "ScrcpyPath");
        var scrcpyResult = AddDetectionResult(root, scrcpyPath, "scrcpy", "ScrcpyDetectionResult");
        var detect = new Button { Content = "检测工具", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(detect, "DetectToolsButton");
        detect.Click += async (_, _) =>
        {
            detect.IsEnabled = false;
            using var cancellation = new CancellationTokenSource();
            void CancelDetection(object? sender, EventArgs e) => cancellation.Cancel();
            Closed += CancelDetection;
            try
            {
                await Task.WhenAll(
                    DetectToolAsync(adbPath, adbResult, "ADB", "adb.exe", "version", required: true, cancellation.Token),
                    DetectToolAsync(scrcpyPath, scrcpyResult, "scrcpy", "scrcpy.exe", "--version", required: false, cancellation.Token));
            }
            finally
            {
                Closed -= CancelDetection;
                detect.IsEnabled = true;
            }
        };
        root.Children.Add(detect);
        root.Children.Add(new TextBlock
        {
            Text = "路径留空时，先查找系统 PATH，再查找随附工具。", FontSize = 11, Foreground = Palette.Gray,
            Margin = new Thickness(0, 8, 0, 12)
        });
        AddPath(root, "截图默认目录", screenshots, settings.ScreenshotDirectory, true, "ScreenshotDirectory");
        topmost.IsChecked = settings.Topmost; startup.IsChecked = settings.AutoStart;
        autoCollapse.IsChecked = settings.AutoCollapse;
        autoCollapseDelay.Text = settings.AutoCollapseDelaySeconds.ToString(CultureInfo.InvariantCulture);
        root.Children.Add(topmost); root.Children.Add(autoCollapse);
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 4) };
        var delayLabel = new TextBlock { Text = "等待时间", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetLabeledBy(autoCollapseDelay, delayLabel);
        delayRow.Children.Add(delayLabel); delayRow.Children.Add(autoCollapseDelay);
        delayRow.Children.Add(new TextBlock { Text = "秒（1–300）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Palette.Gray });
        delayRow.IsEnabled = settings.AutoCollapse;
        autoCollapse.Checked += (_, _) => delayRow.IsEnabled = true;
        autoCollapse.Unchecked += (_, _) => delayRow.IsEnabled = false;
        root.Children.Add(delayRow);
        root.Children.Add(new TextBlock
        {
            Text = !settings.HasConnectedDevice
                ? "首次发现在线设备后，自动收起才会生效。"
                : settings.ShowFloatingTab
                ? "鼠标回到面板或原浮窗位置会取消倒计时。"
                : "浮窗已关闭。在主面板顶部开启“显示浮窗”后，自动收起设置才会生效。",
            FontSize = 11, Foreground = Palette.Gray, Margin = new Thickness(20, 4, 0, 4)
        });
        root.Children.Add(startup);
        root.Children.Add(new TextBlock { Text = "配置与本地错误日志：" + dataDirectory, FontSize = 11, Foreground = Palette.Gray, Margin = new Thickness(0, 12, 0, 0) });
        var footer = new StackPanel { Margin = new Thickness(22, 0, 22, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存", IsDefault = true, Background = (Brush)FindResource("Accent"), Foreground = Brushes.White };
        AutomationProperties.SetAutomationId(save, "SaveSettingsButton");
        AutomationProperties.SetAutomationId(cancel, "CancelSettingsButton");
        save.Click += (_, _) =>
        {
            try
            {
                var enabled = autoCollapse.IsChecked == true;
                var validDelay = int.TryParse(autoCollapseDelay.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var delay) &&
                    delay is >= AppSettings.MinimumAutoCollapseDelaySeconds and <= AppSettings.MaximumAutoCollapseDelaySeconds;
                if (!validDelay)
                {
                    if (enabled) throw new InvalidOperationException("自动收起等待时间请输入 1 至 300 之间的整数（秒）。");
                    delay = settings.AutoCollapseDelaySeconds;
                }
                if (!string.IsNullOrWhiteSpace(adbPath.Text)) ToolResolver.Resolve(adbPath.Text, "adb.exe");
                var directory = Path.GetFullPath(screenshots.Text.Trim());
                Value = new AppSettings
                {
                    AdbPath = adbPath.Text.Trim(), ScrcpyPath = scrcpyPath.Text.Trim(), ScreenshotDirectory = directory,
                    Topmost = topmost.IsChecked == true, AutoStart = startup.IsChecked == true,
                    ShowFloatingTab = settings.ShowFloatingTab, AutoCollapse = enabled, AutoCollapseDelaySeconds = delay
                };
                DialogResult = true;
            }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        footer.Children.Add(feedback); footer.Children.Add(buttons);
        var assembly = typeof(SettingsWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var channel = version.Contains('-') ? "测试版" : "正式版";
        var versionInfo = new TextBlock
        {
            Text = $"ADB Dock · v{version} · {channel}", FontSize = 11, Foreground = Palette.Gray,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 0, 22, 16)
        };
        AutomationProperties.SetAutomationId(versionInfo, "AppVersionInfo");
        var layout = new DockPanel();
        DockPanel.SetDock(versionInfo, Dock.Bottom);
        layout.Children.Add(versionInfo);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var body = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Local);
        KeyboardNavigation.SetTabIndex(body, 0);
        layout.Children.Add(body);
        Content = layout;
        if (focusAdb)
        {
            ContentRendered += (_, _) =>
            {
                adbPath.Focus();
                adbPath.SelectAll();
            };
        }
    }

    private TextBlock DownloadInstructions(string prefix, string linkText, string url, string suffix)
    {
        var instructions = new TextBlock { FontSize = 12 };
        var link = new Hyperlink(new Run(linkText)) { Foreground = (Brush)FindResource("Accent"), ToolTip = url };
        link.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { ShowError("无法打开下载页面。" + ex.Message); }
        };
        instructions.Inlines.Add(new Run(prefix));
        instructions.Inlines.Add(link);
        instructions.Inlines.Add(new Run(suffix));
        return instructions;
    }

    private static TextBlock AddDetectionResult(Panel root, TextBox input, string tool, string automationId)
    {
        var result = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetAutomationId(result, automationId);
        void ResetResult()
        {
            result.Text = $"{tool}：尚未检测";
            result.Foreground = Palette.Gray;
            result.ToolTip = null;
        }
        ResetResult();
        input.TextChanged += (_, _) => ResetResult();
        root.Children.Add(result);
        return result;
    }

    private static async Task DetectToolAsync(TextBox input, TextBlock result, string tool, string executable,
        string versionArgument, bool required, CancellationToken cancellationToken)
    {
        var configuredPath = input.Text;
        result.Text = $"{tool}：正在检测…";
        result.Foreground = Palette.Gray;
        result.ToolTip = null;
        try
        {
            var path = ToolResolver.Resolve(configuredPath, executable);
            var version = await ProcessRunner.RunAsync(path, new[] { versionArgument }, TimeSpan.FromSeconds(8), cancellationToken);
            version.EnsureSuccess();
            if (cancellationToken.IsCancellationRequested || input.Text != configuredPath) return;
            var output = version.Output.Trim();
            var preview = output.Split(['\r', '\n'], 2)[0];
            result.Text = preview.Length == 0 ? $"{tool}：检测通过" : $"{tool}：检测通过 · {preview}";
            result.Foreground = Palette.Green;
            result.ToolTip = output.Length == 0 ? path : $"{output}\n{path}";
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested || input.Text != configuredPath) return;
            if (!required && ex is FileNotFoundException && string.IsNullOrWhiteSpace(configuredPath))
            {
                result.Text = $"{tool}：未配置，仅影响投屏。";
                result.Foreground = Palette.Gray;
            }
            else
            {
                result.Text = $"{tool}：检测失败。{ex.Message}";
                result.Foreground = required ? Palette.Red : Palette.Amber;
            }
        }
    }

    private void ShowError(string message)
    {
        feedback.Text = message;
        feedback.Visibility = Visibility.Visible;
    }

    private void AddPath(Panel root, string label, TextBox text, string value, bool folder, string automationId)
    {
        var pathLabel = new TextBlock { Text = label, FontSize = 12, Foreground = Palette.Gray, Margin = new Thickness(0, 8, 0, 5) };
        root.Children.Add(pathLabel);
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
        AutomationProperties.SetAutomationId(text, automationId);
        AutomationProperties.SetLabeledBy(text, pathLabel);
        row.Children.Add(browse); row.Children.Add(text); root.Children.Add(row);
    }
}
