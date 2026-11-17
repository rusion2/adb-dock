using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdbDock.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AdbDock.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore store = new(Environment.GetEnvironmentVariable("ADBDOCK_HOME"));
    private readonly MainViewModel model = new();
    private readonly AppSettings settings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim restartGate = new(1, 1);
    private readonly HashSet<string> busy = new(StringComparer.Ordinal);
    private readonly HashSet<object> openMenus = new();
    private AdbService? adb;
    private AdbOperations? operations;
    private MirrorManager? mirrors;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private int generation;
    private int interactionDepth;
    private bool exiting;
    private bool loaded;

    public MainWindow()
    {
        settings = store.Load();
        SyncStartupState();
        InitializeComponent();
        Icon = AppIcon.CreateWindowIcon();
        DataContext = model;
        Topmost = settings.Topmost;
        InitializeInteraction();
        Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            RestorePlacement();
            CreateTray();
            await RestartServicesAsync();
            if (store.LoadError is not null) model.Feedback(store.LoadError, true);
        };
        Closing += ClosingWindow;
    }

    private void CreateTray()
    {
        trayIcon = AppIcon.CreateTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "ADB Dock · 正在连接", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Opened += (_, _) => SetMenuInteraction(menu, true);
        menu.Closed += (_, _) => SetMenuInteraction(menu, false);
        menu.Items.Add("显示设备面板", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
        menu.Items.Add("收起到屏幕边缘", null, (_, _) => Dispatcher.BeginInvoke(() => { Show(); CollapsePanel(true); }));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.BeginInvoke(() => { ShowFromTray(); OpenSettings(this, new RoutedEventArgs()); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 ADB Dock", null, (_, _) => Dispatcher.BeginInvoke(async () => await ExitAsync()));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
    }

    private async Task RestartServicesAsync()
    {
        await restartGate.WaitAsync();
        try
        {
            var currentGeneration = ++generation;
            await StopServicesAsync();
            if (exiting) return;
            model.Apply(new(Array.Empty<AdbDevice>(), "正在连接 ADB…"));
            UpdateView();
            var path = ToolResolver.Resolve(settings.AdbPath, "adb.exe");
            operations = new AdbOperations(path);
            adb = new AdbService(path);
            adb.SnapshotChanged += snapshot => Dispatcher.BeginInvoke(() =>
            {
                if (currentGeneration != generation || exiting) return;
                var online = snapshot.Devices.Where(d => d.IsOnline).Select(d => d.Serial).ToArray();
                operations?.UpdateOnlineDevices(online);
                mirrors?.UpdateOnlineDevices(online);
                var selected = model.Selected?.Serial;
                model.Apply(snapshot);
                if (selected is not null && model.Selected is null) model.Feedback("设备已断开或不可用，未完成的操作已取消", true);
                else if (snapshot.Error is not null) model.Feedback(snapshot.Error, true);
                else if (!busy.Any()) model.Feedback("设备状态已同步 · " + model.Aggregate);
                if (tray is not null) tray.Text = $"ADB Dock · {model.OnlineCount}{model.TotalCount} · {model.Aggregate}";
                UpdateView();
            });
            await adb.StartAsync(lifetime.Token);
        }
        catch (Exception ex) when (!exiting)
        {
            model.Apply(new(Array.Empty<AdbDevice>(), ex.Message));
            ReportError(ex);
            UpdateView();
        }
        finally { restartGate.Release(); }
    }

    private async Task StopServicesAsync()
    {
        var oldMirrors = mirrors; mirrors = null;
        var oldOperations = operations; operations = null;
        var oldAdb = adb; adb = null;
        if (oldMirrors is not null) await oldMirrors.DisposeAsync();
        if (oldOperations is not null) await oldOperations.DisposeAsync();
        if (oldAdb is not null) await oldAdb.DisposeAsync();
    }

    private void UpdateView()
    {
        ListPanel.Visibility = model.Selected is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = model.Selected is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = model.HasDevices ? Visibility.Collapsed : Visibility.Visible;
        ListLabel.Visibility = model.HasDevices ? Visibility.Visible : Visibility.Collapsed;
        Actions.IsEnabled = model.Selected is { IsOnline: true } selected && !busy.Contains(selected.Serial) && operations is not null;
        PinButton.Background = settings.Pinned ? (Brush)FindResource("AccentTint") : Brushes.Transparent;
        PinButton.Foreground = settings.Pinned ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        PinButton.BorderBrush = settings.Pinned ? (Brush)FindResource("Accent") : Brushes.Transparent;
        PinButton.BorderThickness = new Thickness(settings.Pinned ? 1 : 0);
        PinButton.ToolTip = settings.Pinned ? "取消固定展开" : "固定展开";
        System.Windows.Automation.AutomationProperties.SetName(PinButton, settings.Pinned ? "取消固定展开" : "固定展开");
        System.Windows.Automation.AutomationProperties.SetItemStatus(PinButton, settings.Pinned ? "已固定，已选中" : "未固定，未选中");
        RenderEdge();
    }

    private void DeviceClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DeviceRow row) OpenDevice(row);
    }
    private void OpenDevice(DeviceRow row)
    {
        ExpandPanel(true);
        if (!row.IsOnline)
        {
            var message = row.Device.State == "unauthorized"
                ? "请解锁手机，在手机上允许此电脑进行 USB 调试，然后刷新设备状态。\n\n设备：" + row.Serial
                : "设备当前无法执行常用操作。请检查连接线、USB 调试和设备状态。\n\n" + row.Status + " · " + row.Serial;
            ShowText("设备需要处理", message);
            return;
        }
        model.Select(row);
        UpdateView();
    }
    private void BackClick(object sender, RoutedEventArgs e) { model.Select(null); UpdateView(); }
    private void CopySerialClick(object sender, RoutedEventArgs e)
    {
        if (model.Selected is null) return;
        try { Clipboard.SetText(model.Selected.Serial); model.Feedback("序列号已复制"); }
        catch (Exception ex) { ReportError(ex); }
    }
    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (adb is null) await RestartServicesAsync();
            else await adb.RefreshAsync(lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }
    private async void ActionClick(object sender, RoutedEventArgs e)
    {
        if (model.Selected is not { IsOnline: true } row || operations is not { } service) return;
        var action = ((Button)sender).Tag as string;
        if (action == "mirror")
        {
            await ExecuteAsync(row, "启动投屏", async token =>
            {
                if (mirrors is null)
                {
                    mirrors = new MirrorManager(ToolResolver.Resolve(settings.ScrcpyPath, "scrcpy.exe"), adb!.ExecutablePath);
                    mirrors.UpdateOnlineDevices(model.Devices.Where(d => d.IsOnline).Select(d => d.Serial));
                    var mirrorGeneration = generation;
                    mirrors.StatusChanged += (serial, text) => Dispatcher.BeginInvoke(() =>
                    {
                        if (exiting || generation != mirrorGeneration) return;
                        var failed = text.Contains("失败") || text.Contains("未能") || text.Contains("超时") || text.StartsWith("scrcpy 已退出", StringComparison.Ordinal);
                        model.Feedback($"{serial} · {text}", failed);
                        RecordResult(serial + " · 投屏会话", text);
                    });
                }
                await mirrors.StartOrShowAsync(row.Serial, token);
            }, "投屏会话已启动；关闭 scrcpy 窗口可结束投屏");
        }
        else if (action == "screenshot")
        {
            await SaveScreenshotAsync(row);
        }
        else if (action == "install")
        {
            var picker = new OpenFileDialog { Title = "选择要安装到 " + row.Name + " 的 APK", Filter = "Android 安装包|*.apk", CheckFileExists = true, Multiselect = false };
            interactionDepth++;
            bool? result;
            try { result = picker.ShowDialog(this); } finally { interactionDepth--; }
            if (result != true) return;
            await ExecuteAsync(row, "安装 APK", token => service.InstallAsync(row.Serial, picker.FileName, token), "安装成功 · " + Path.GetFileName(picker.FileName));
        }
        else if (action == "info")
        {
            string? text = null;
            if (await ExecuteAsync(row, "读取设备信息", async token => text = await service.ReadInfoAsync(row.Serial, token), "设备信息读取完成", detail: () => text))
                ShowOwned(new DeviceInfoWindow(row.Serial, service, text!));
        }
        else if (action == "keys")
        {
            var dialog = new KeysWindow(row.Name, key => ExecuteAsync(row, "发送按键 " + key, token => service.SendKeyAsync(row.Serial, key, token), "按键已发送 · " + key), row.Serial);
            ShowOwned(dialog);
        }
        else if (action == "logcat") ShowOwned(new LogWindow(row.Serial, service));
    }

    private async Task<bool> ExecuteAsync(DeviceRow device, string label, Func<CancellationToken, Task> action, string success,
        Func<string?>? detail = null, string? filePath = null)
    {
        if (!model.Devices.Any(d => d.Serial == device.Serial && d.IsOnline))
        {
            model.Feedback(device.Name + " · 设备已断开或不可用，本次操作未开始", true);
            RecordResult(device.Name + " · " + label, $"设备：{device.Serial}\n设备已断开或不可用，本次操作未开始。");
            return false;
        }
        if (!busy.Add(device.Serial))
        {
            model.Feedback(device.Name + " · 正在执行其他操作，请稍后重试", true);
            return false;
        }
        model.Feedback(device.Name + " · " + label + "…");
        UpdateView();
        try
        {
            await action(lifetime.Token);
            model.Feedback(device.Name + " · " + success);
            RecordResult(device.Name + " · " + label, $"设备：{device.Serial}\n{detail?.Invoke() ?? success}", filePath);
            return true;
        }
        catch (OperationCanceledException)
        {
            model.Feedback(device.Name + " · 操作已取消或设备已断开", true);
            RecordResult(device.Name + " · " + label, $"设备：{device.Serial}\n操作已取消或设备已断开。");
            return false;
        }
        catch (Exception ex)
        {
            ReportError(new InvalidOperationException($"{device.Name} · {label}\n设备：{device.Serial}\n{ex.Message}", ex));
            return false;
        }
        finally { busy.Remove(device.Serial); UpdateView(); }
    }

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        SyncStartupState();
        var dialog = new SettingsWindow(settings, store.DirectoryPath);
        if (ShowOwned(dialog) != true) return;
        var value = dialog.Value;
        try
        {
            if (value.AutoStart != settings.AutoStart) Desktop.ConfigureStartup(value.AutoStart);
            settings.AdbPath = value.AdbPath;
            settings.ScrcpyPath = value.ScrcpyPath;
            settings.ScreenshotDirectory = value.ScreenshotDirectory;
            settings.Topmost = value.Topmost;
            SyncStartupState();
            Topmost = value.Topmost;
            SaveSettings();
            await RestartServicesAsync();
        }
        catch (Exception ex) { ReportError(ex); }
    }
    private void SyncStartupState()
    {
        try { settings.AutoStart = Desktop.IsStartupEnabled(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            settings.AutoStart = false;
            store.Log("无法读取登录自动启动状态：" + ex.Message);
        }
    }
    private void EmptyHelpClick(object sender, RoutedEventArgs e)
    {
        if (!model.AdbAvailable) OpenSettings(sender, e);
        else ShowText("连接 Android 设备", "1. 在手机开发者选项中开启 USB 调试。\n2. 使用支持数据传输的 USB 线连接电脑。\n3. 解锁手机，允许此电脑进行 USB 调试。\n4. 点击刷新；网络设备请先通过 ADB 建立连接。\n\n本程序不会自动重启共享 ADB 服务。");
    }
    private bool? ShowOwned(Window dialog)
    {
        var previousFocus = Keyboard.FocusedElement;
        interactionDepth++;
        dialog.Owner = this;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.Topmost = Topmost;
        FitDialog(dialog, Desktop.WorkArea(this), Desktop.Scale(this));
        RoutedEventHandler onLoaded = (_, _) =>
        {
            FitDialog(dialog, Desktop.WorkArea(dialog), Desktop.Scale(dialog));
            Desktop.Clamp(dialog);
            dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        };
        dialog.Loaded += onLoaded;
        try { return dialog.ShowDialog(); }
        finally
        {
            dialog.Loaded -= onLoaded;
            interactionDepth--;
            if (!exiting && IsVisible)
            {
                if (previousFocus is UIElement element && element.IsVisible && element.IsEnabled && element.Focusable)
                    Keyboard.Focus(element);
                else if (collapsed) EdgeSummary.Focus();
                else CollapseButton.Focus();
            }
        }
    }
    private static void FitDialog(Window dialog, NativeRect work, double scale)
    {
        var width = Math.Max(1, work.Width / scale - 16);
        var height = Math.Max(1, work.Height / scale - 16);
        dialog.MinWidth = Math.Min(dialog.MinWidth, width);
        dialog.MinHeight = Math.Min(dialog.MinHeight, height);
        dialog.MaxWidth = width;
        dialog.MaxHeight = height;
        if (!double.IsNaN(dialog.Width)) dialog.Width = Math.Min(dialog.Width, width);
        if (!double.IsNaN(dialog.Height)) dialog.Height = Math.Min(dialog.Height, height);
    }
    private void ContextMenuOpened(object sender, RoutedEventArgs e) => SetMenuInteraction(sender, true);
    private void ContextMenuClosed(object sender, RoutedEventArgs e) => SetMenuInteraction(sender, false);
    private void SetMenuInteraction(object menu, bool isOpen)
    {
        if (isOpen)
        {
            if (!openMenus.Add(menu)) return;
            interactionDepth++;
        }
        else
        {
            if (!openMenus.Remove(menu)) return;
            interactionDepth--;
        }
    }
    private void ShowText(string title, string text) => ShowOwned(new TextWindow(title, text));
    public void ReportError(Exception ex)
    {
        store.Log(ex.ToString());
        model.Feedback(ex.Message, true);
        RecordResult("操作失败", ex.Message);
    }
    private void SaveSettings()
    {
        try
        {
            if (IsLoaded)
            {
                var rect = Desktop.Bounds(this);
                settings.WindowX = rect.Left; settings.WindowY = rect.Top;
            }
            store.Save(settings);
        }
        catch (Exception ex) { ReportError(ex); }
    }
    public void ShowFromTray()
    {
        Show();
        ExpandPanel(true);
        Desktop.Clamp(this);
    }
    private void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        pressTimer.Stop();
        if (pressStart is not null) FinishPress(true);
        SaveSettings(); Hide();
    }
    private async void ExitClick(object sender, RoutedEventArgs e) => await ExitAsync();
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        SaveSettings();
        pressTimer.Stop();
        lifetime.Cancel();
        await restartGate.WaitAsync();
        try { await StopServicesAsync(); }
        catch (Exception ex) { store.Log(ex.ToString()); }
        finally { restartGate.Release(); }
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        trayIcon?.Dispose();
        trayIcon = null;
        lifetime.Dispose();
        Application.Current.Shutdown();
    }
}
