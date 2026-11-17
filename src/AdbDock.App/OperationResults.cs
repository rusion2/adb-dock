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
    private sealed record OperationResult(DateTime Time, string Title, string Detail, string? FilePath);
    private readonly List<OperationResult> results = new();

    private void RecordResult(string title, string detail, string? filePath = null)
    {
        results.Add(new(DateTime.Now, title, detail, filePath));
        if (results.Count > 20) results.RemoveAt(0);
        ResultButton.IsEnabled = true;
    }

    private void ResultsClick(object sender, RoutedEventArgs e)
    {
        if (results.Count == 0) return;
        var text = string.Join("\n\n────────────────────────\n\n", results.AsEnumerable().Reverse()
            .Select(result => $"{result.Time:yyyy-MM-dd HH:mm:ss} · {result.Title}\n{result.Detail}"));
        ShowOwned(new ResultsWindow(text, results.LastOrDefault(result => result.FilePath is not null)?.FilePath));
    }

    private async void QuickScreenshotClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is DeviceRow { IsOnline: true } row)
            await SaveScreenshotAsync(row);
    }

    private async System.Threading.Tasks.Task SaveScreenshotAsync(DeviceRow row)
    {
        if (operations is not { } service) return;
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存 " + row.Name + " 的截图", Filter = "PNG 图片|*.png",
            FileName = $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true, OverwritePrompt = true
        };
        if (Directory.Exists(settings.ScreenshotDirectory)) picker.InitialDirectory = settings.ScreenshotDirectory;
        interactionDepth++;
        bool? accepted;
        try { accepted = picker.ShowDialog(this); }
        finally { interactionDepth--; }
        if (accepted != true) return;
        await ExecuteAsync(row, "截图", token => service.ScreenshotAsync(row.Serial, picker.FileName, token),
            "截图已保存：" + picker.FileName, filePath: picker.FileName);
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
                if (!File.Exists(screenshot)) throw new FileNotFoundException("截图文件已移动或删除。", screenshot);
                Process.Start(new ProcessStartInfo(screenshot) { UseShellExecute = true });
            }
            catch (Exception ex) { Status.Text = "无法打开截图：" + ex.Message; }
        };
        Buttons.Children.Insert(0, open);
    }
}
