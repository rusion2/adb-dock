using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AdbDock.App;

public partial class MainWindow
{
    private sealed record OperationResult(DateTime Time, string Title, string Detail);
    private readonly List<OperationResult> results = new();
    private string? latestScreenshot;

    private void RecordResult(string title, string detail, string? filePath = null)
    {
        results.Add(new(DateTime.Now, title, detail));
        if (results.Count > 20) results.RemoveAt(0);
        ResultButton.IsEnabled = true;
        if (filePath is not null)
        {
            latestScreenshot = filePath;
            OpenLatestScreenshotButton.Visibility = Visibility.Visible;
        }
    }

    private void ResultsClick(object sender, RoutedEventArgs e)
    {
        if (results.Count == 0) return;
        var text = string.Join("\n\n────────────────────────\n\n", results.AsEnumerable().Reverse()
            .Select(result => $"{result.Time:yyyy-MM-dd HH:mm:ss} · {result.Title}\n{result.Detail}"));
        ShowOwned(new ResultsWindow(text, latestScreenshot));
    }

    private void OpenLatestScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (latestScreenshot is not { } path) return;
        try { ResultsWindow.OpenScreenshot(path); }
        catch (Exception ex) { ReportError(ex); }
    }
    private async void QuickScreenshotClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is DeviceRow { IsOnline: true } row)
            await QuickScreenshotAsync(row);
    }

    private async System.Threading.Tasks.Task QuickScreenshotAsync(DeviceRow row)
    {
        string path;
        try
        {
            var fileName = $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}.png";
            path = Path.Combine(Path.GetFullPath(settings.ScreenshotDirectory), fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ReportError(new IOException("无法使用截图目录：" + settings.ScreenshotDirectory, ex));
            return;
        }
        await CaptureScreenshotAsync(row, path);
    }

    private async System.Threading.Tasks.Task SaveScreenshotAsync(DeviceRow row)
    {
        if (operations is null) return;
        var picker = CreateScreenshotPicker(row);
        outsideSince = null;
        interactionDepth++;
        bool? accepted;
        try { accepted = picker.ShowDialog(this); }
        finally { interactionDepth--; }
        if (accepted == true) await CaptureScreenshotAsync(row, picker.FileName);
    }

    private async System.Threading.Tasks.Task CaptureScreenshotAsync(DeviceRow row, string path)
    {
        if (operations is not { } service) return;
        await ExecuteAsync(row, "截图", token => service.ScreenshotAsync(row.Serial, path, token),
            "截图已保存", detail: () => "已保存到：" + path, filePath: path);
    }

    private Microsoft.Win32.SaveFileDialog CreateScreenshotPicker(DeviceRow row)
    {
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存 " + row.Name + " 的截图", Filter = "PNG 图片|*.png",
            FileName = $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true, OverwritePrompt = true
        };
        try
        {
            picker.InitialDirectory = Directory.CreateDirectory(settings.ScreenshotDirectory).FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowOwned(new TextWindow("截图目录不可用", settings.ScreenshotDirectory + "\n" + ex.Message
                + "\n\n关闭此提示后，可在保存窗口中选择其他目录。"));
        }
        return picker;
    }
}

public sealed class ResultsWindow : TextWindow
{
    public ResultsWindow(string text, string? screenshot) : base("最近的操作结果", text)
    {
        Status.Text = "仅在本次运行中保留最近 20 条结果。";
        if (screenshot is null) return;
        var open = new Button { Content = "打开最近截图", Margin = new Thickness(0, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(open, "OpenScreenshotButton");
        open.Click += (_, _) =>
        {
            try
            {
                OpenScreenshot(screenshot);
            }
            catch (Exception ex) { Status.Text = "无法打开截图：" + ex.Message; }
        };
        Buttons.Children.Insert(0, open);
    }

    internal static void OpenScreenshot(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("截图文件已移动或删除。", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
