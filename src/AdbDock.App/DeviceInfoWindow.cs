using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdbDock.Core;

namespace AdbDock.App;

public sealed class DeviceInfoWindow : TextWindow
{
    private readonly string serial;
    private readonly AdbOperations service;
    private readonly Button toggle = new() { Content = "开始监控", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock cpu = Metric("CpuUsageValue"), memory = Metric("MemoryUsageValue"), battery = Metric("BatteryLevelValue"), temperature = Metric("BatteryTemperatureValue");
    private readonly TextBlock performanceStatus = new() { Text = "监控未开始；点击开始后读取当前设备。", FontSize = 11, Foreground = Palette.Gray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? samplingCancellation;
    private Task? samplingTask;
    private CpuTimes? previousCpu;
    private bool closing;
    private bool closeReady;

    public DeviceInfoWindow(string serial, AdbOperations service, string info) : base("设备信息与性能 · " + serial, info)
    {
        this.serial = serial;
        this.service = service;
        Height = 650;
        AutomationProperties.SetAutomationId(toggle, "PerformanceToggleButton");
        AutomationProperties.SetAutomationId(performanceStatus, "PerformanceStatus");
        Status.Text = "复制与导出仅包含下方的静态设备信息。";
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new TextBlock { Text = "整机性能 · " + serial, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var values = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        values.ColumnDefinitions.Add(new ColumnDefinition());
        AddMetric(values, 0, "CPU 使用率", cpu);
        AddMetric(values, 1, "内存使用", memory);
        AddMetric(values, 2, "电池电量", battery);
        AddMetric(values, 3, "电池温度", temperature);
        panel.Children.Add(values);
        panel.Children.Add(performanceStatus);
        panel.Children.Add(toggle);
        DockPanel.SetDock(panel, Dock.Top);
        ((DockPanel)Content).Children.Insert(1, panel);
        toggle.Click += async (_, _) =>
        {
            if (samplingCancellation is null) StartMonitoring();
            else await PauseMonitoringAsync();
        };
        Closing += OnClosing;
    }

    private static TextBlock Metric(string id)
    {
        var value = new TextBlock { Text = "未开始", Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(value, id);
        return value;
    }

    private static void AddMetric(Grid grid, int row, string label, TextBlock value)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, Foreground = Palette.Gray, Margin = new Thickness(0, 3, 8, 3) };
        Grid.SetRow(caption, row); grid.Children.Add(caption);
        Grid.SetRow(value, row); Grid.SetColumn(value, 1); grid.Children.Add(value);
    }

    private void StartMonitoring()
    {
        if (closing) return;
        var cancellation = new CancellationTokenSource();
        samplingCancellation = cancellation;
        previousCpu = null;
        cpu.Text = "采样中（等待下一次采样）";
        performanceStatus.Text = "正在读取当前设备；每次完成后约 2 秒刷新。";
        toggle.Content = "暂停监控";
        samplingTask = MonitorAsync(cancellation);
    }

    private async Task MonitorAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await service.MonitorPerformanceAsync(serial, sample => Dispatcher.BeginInvoke(() =>
            {
                if (!closing && ReferenceEquals(samplingCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    ApplySample(sample);
            }), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested && !closing)
                performanceStatus.Text = "设备已断开，监控已停止；重新连接后请手动开始。";
        }
        catch (Exception exception)
        {
            if (!closing) performanceStatus.Text = "监控已停止：" + exception.Message + "；请手动重新开始。";
        }
        finally
        {
            if (ReferenceEquals(samplingCancellation, cancellation))
            {
                samplingCancellation = null;
                previousCpu = null;
                toggle.Content = "开始监控";
            }
            cancellation.Dispose();
        }
    }

    private void ApplySample(DevicePerformanceSample sample)
    {
        var usage = PerformanceParser.CpuUsagePercent(previousCpu, sample.Cpu);
        cpu.Text = sample.Cpu is null ? "不可用" : usage is { } percent ? percent.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "采样中（等待有效间隔）";
        previousCpu = sample.Cpu;
        memory.Text = sample.Memory is { } ram
            ? $"{(ram.UsedBytes / 1048576d).ToString("0", CultureInfo.InvariantCulture)} / {(ram.TotalBytes / 1048576d).ToString("0", CultureInfo.InvariantCulture)} MiB · {ram.UsedPercent.ToString("0.0", CultureInfo.InvariantCulture)}%"
            : "不可用";
        battery.Text = sample.Battery?.LevelPercent is { } level ? level.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "不可用";
        temperature.Text = sample.Battery?.TemperatureCelsius is { } degrees ? degrees.ToString("0.0", CultureInfo.InvariantCulture) + " °C" : "不可用";
        performanceStatus.Text = "更新于 " + sample.CollectedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
            (sample.Errors.Count == 0 ? " · 每次完成后约 2 秒刷新" : "\n部分指标不可用：" + string.Join("；", sample.Errors));
    }

    private async Task PauseMonitoringAsync()
    {
        var cancellation = samplingCancellation;
        if (cancellation is null) return;
        toggle.IsEnabled = false;
        cancellation.Cancel();
        if (samplingTask is { } work) await work;
        previousCpu = null;
        if (!closing)
        {
            performanceStatus.Text = "监控已暂停；再次开始将重新采样。";
            toggle.Content = "开始监控";
            toggle.IsEnabled = true;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        toggle.IsEnabled = false;
        samplingCancellation?.Cancel();
        if (samplingTask is { } work) await work;
        closeReady = true;
        // A never-started or already-paused monitor may complete synchronously. Defer the
        // second Close until WPF has finished processing this cancelled Closing event.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
