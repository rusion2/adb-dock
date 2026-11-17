using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdbDock.App;
using AdbDock.Core;
using VectorPath = System.Windows.Shapes.Path;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length > 0 && args[0] == "--smoke") return PublishedSmoke.Run(args[1..]);
        if (args.Length > 0 && args[0] == "--startup-checks") return StartupChecks.Run(args[1..]);
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: AdbDock.UiChecks <AdbDock.Checks.exe> [artifact directory]");
            return 2;
        }
        var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine("artifacts", "ui-checks"));
        var run = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        var app = new AdbDock.App.App();
        var checks = new UiChecks(Path.GetFullPath(args[0]), run, app);
        checks.Attach();
        app.InitializeComponent();
        return app.Run();
    }
}

// Use the production Application so its compiled resources, startup and shutdown are exercised.
internal sealed class UiChecks(string tool, string artifactDirectory, AdbDock.App.App application)
{
    private const string Initial = "UI-C device usb:1 model:Fixture_One\nUI-A unauthorized usb:2\nUI-B offline usb:3\nUI-D device usb:4 model:Fixture_Two\n";
    private readonly string snapshotFile = Path.Combine(artifactDirectory, "devices.txt");
    private readonly List<string> failures = [];
    private MainWindow window = null!;
    private MainViewModel model = null!;
    private AppSettings settings = null!;
    private int passed;
    private Task? listener;
    private bool runFinished;

    public void Attach()
    {
        // These values are inherited only by our fixture clients; no user configuration is read.
        Environment.SetEnvironmentVariable("ADBDOCK_HOME", artifactDirectory);
        Environment.SetEnvironmentVariable("ADBDOCK_UI_SNAPSHOT", snapshotFile);
        File.WriteAllText(snapshotFile, Initial);
        new SettingsStore(artifactDirectory).Save(new AppSettings
        {
            AdbPath = tool, ScrcpyPath = tool, ScreenshotDirectory = artifactDirectory,
            AutoStart = false, Topmost = false, AutoCollapse = false, Side = DockSide.None
        });
        application.Startup += (_, _) => application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (application.MainWindow is not MainWindow main) return;
            window = main;
            model = Field<MainViewModel>(window, "model");
            settings = Field<AppSettings>(window, "settings");
            application.DispatcherUnhandledException += (_, args) =>
            {
                failures.Add("Unhandled dispatcher exception: " + args.Exception);
                Console.Error.WriteLine(failures[^1]);
                args.Handled = true;
                foreach (var owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            };
            await RunChecksAsync();
        }));
        application.Exit += OnExit;
    }

    private async Task RunChecksAsync()
    {
        Console.WriteLine("Native WPF fixture checks; artifacts: " + artifactDirectory);
        try
        {
            await Check("listener, mixed device counts and actual bindings", async () =>
            {
                await Until(() => model.Devices.Count == 4 && model.AdbAvailable);
                Equal("2", model.OnlineCount); Equal("/4", model.TotalCount); Equal("需要处理", model.Aggregate);
                Equal("UI-C,UI-D,UI-A,UI-B", string.Join(',', model.Devices.Select(row => row.Serial)));
                await Layout();
                Equal("2", Descendants<TextBlock>(window).Single(item => AutomationProperties.GetAutomationId(item) == "OnlineCount").Text);
                Require(!window.Topmost, "isolated topmost setting was not applied");
                Equal(ExpectedStartupState(), settings.AutoStart);
                Capture(window, "01-device-list.png");
            });
            await Check("window and tray use the embedded multi-size application icon", async () =>
            {
                using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/AdbDock;component/Assets/App.ico"))?.Stream
                    ?? throw new InvalidOperationException("Embedded application icon missing");
                using var bytes = new MemoryStream(); resource.CopyTo(bytes);
                var iconBytes = bytes.ToArray();
                using var source = new MemoryStream(iconBytes);
                var frames = new IconBitmapDecoder(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
                foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
                    Require(frames.Any(frame => frame.PixelWidth == size && frame.PixelHeight == size), "missing icon size " + size);
                Require(window.Icon is BitmapSource { IsFrozen: true, PixelWidth: >= 64 }, "window icon not initialized from application artwork");
                var tray = Field<object>(window, "tray");
                var trayIcon = tray.GetType().GetProperty("Icon")!.GetValue(tray) ?? throw new InvalidOperationException("Tray icon missing");
                using var saved = new MemoryStream();
                trayIcon.GetType().GetMethod("Save", new[] { typeof(Stream) })!.Invoke(trayIcon, new object[] { saved });
                Require(iconBytes.SequenceEqual(saved.ToArray()), "tray icon is not the embedded application icon");
                var headerButtons = new[] { "PinButton", "SettingsButton", "CollapseButton" }
                    .Select(id => Descendants<Button>(window).Single(button => AutomationProperties.GetAutomationId(button) == id)).ToArray();
                foreach (var button in headerButtons)
                {
                    Equal(32d, button.Width); Equal(32d, button.Height);
                    Require(ReferenceEquals(headerButtons[0].Style, button.Style), "header icon buttons do not share their visual style");
                    Require(button.Content is Viewbox { Width: 16, Height: 16 }, "header glyph is not a 16-DIP vector icon");
                    Require(ButtonVectorGeometry(button).Length > 0, "header vector icon has no geometry");
                    Require(Descendants<VectorPath>(button).All(path => path.StrokeThickness >= 1.7 && path.StrokeThickness <= 1.8), "header vector strokes are inconsistent");
                }
                await Task.CompletedTask;
            });
            await Check("selected serial and six real action controls", async () =>
            {
                Invoke("OpenDevice", model.Devices.First(row => row.Serial == "UI-D"));
                await Layout();
                Equal("UI-D", model.Selected?.Serial);
                var actions = Named<UniformGrid>("Actions");
                Equal(6, actions.Children.Count); Require(actions.IsEnabled, "online actions disabled");
                Equal("UI-D", Descendants<TextBlock>(window).Single(item => AutomationProperties.GetAutomationId(item) == "SelectedSerial").Text);
                Equal("mirror,screenshot,install,keys,info,logcat", string.Join(',', actions.Children.OfType<Button>().Select(button => button.Tag)));
                Capture(window, "02-device-actions.png");
            });
            await Check("unauthorized and offline guidance never selects target", async () =>
            {
                Invoke("BackClick", window, new RoutedEventArgs());
                foreach (var serial in new[] { "UI-A", "UI-B" })
                {
                    await InspectDialog(() => Invoke("OpenDevice", model.Devices.Single(row => row.Serial == serial)), async dialog =>
                    {
                        Require(Output(dialog).Text.Contains(serial), "guidance lost its serial");
                        Require(model.Selected is null, "unavailable device became actionable");
                        await Task.CompletedTask;
                    });
                }
            });
            await Check("list screenshot shortcuts follow per-device availability", async () =>
            {
                await Layout();
                var shortcuts = Descendants<Button>(Named<ItemsControl>("DeviceList"))
                    .Where(button => AutomationProperties.GetName(button) == "保存此设备截图").ToArray();
                Equal(4, shortcuts.Length);
                foreach (var shortcut in shortcuts)
                    Equal(((DeviceRow)shortcut.DataContext).IsOnline, shortcut.IsEnabled);
            });
            await Check("released card drag collapses immediately at all four edges", async () =>
            {
                foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Top, DockSide.Bottom })
                {
                    settings.AutoCollapse = false; settings.Pinned = false;
                    await ReleaseCardAtEdge(side);
                    var bounds = Desktop.Bounds(window); var work = Desktop.WorkArea(window); var scale = Desktop.Scale(window);
                    Within(bounds, work);
                    Require(Field<bool>(window, "collapsed"), "edge drag release did not collapse the card");
                    Equal(side, settings.Side);
                    var horizontal = side is DockSide.Top or DockSide.Bottom;
                    Equal(horizontal ? Orientation.Horizontal : Orientation.Vertical, Named<StackPanel>("EdgePanel").Orientation);
                    Require(horizontal ? bounds.Height <= 70 * scale : bounds.Width <= 58 * scale, "native hit area stayed expanded");
                    var distance = side switch { DockSide.Left => bounds.Left - work.Left, DockSide.Right => work.Right - bounds.Right, DockSide.Top => bounds.Top - work.Top, _ => work.Bottom - bounds.Bottom };
                    Require(Math.Abs(distance) <= 2, "tag failed to touch expected edge");
                    var arrow = Named<Button>("EdgeArrow");
                    Require(arrow.ActualWidth >= 32 && arrow.ActualHeight >= 32, "expand control target is smaller than 32 DIP");
                    Equal(horizontal ? 32d : 36d, arrow.Width); Equal(horizontal ? 36d : 32d, arrow.Height);
                    Require(arrow.Content is Viewbox { Width: 18, Height: 18 }, "expand control is not the redesigned vector icon");
                    Require(Named<VectorPath>("EdgeArrowGlyph").Data is not null && !Descendants<TextBlock>(arrow).Any(), "expand control relies on a font glyph");
                    var angle = side switch { DockSide.Left => 0d, DockSide.Right => 180d, DockSide.Top => 90d, _ => 270d };
                    Equal(angle, Named<RotateTransform>("EdgeArrowRotation").Angle);
                    Capture(window, "03-edge-" + side.ToString().ToLowerInvariant() + ".png");
                }
            });
            await Check("arrow hover remains compact beyond the former hover and leave delays", async () =>
            {
                var arrow = Named<Button>("EdgeArrow");
                var before = Desktop.Bounds(window);
                RaiseMouseEvent(arrow, Mouse.MouseEnterEvent);
                await Task.Delay(250);
                Require(Field<bool>(window, "collapsed"), "arrow hover opened the card after 200ms");
                await Task.Delay(500);
                Require(Field<bool>(window, "collapsed"), "arrow hover opened the card after 600ms");
                Equal(before.Width, Desktop.Bounds(window).Width); Equal(before.Height, Desktop.Bounds(window).Height);
                RaiseMouseEvent(arrow, Mouse.MouseLeaveEvent);
            });
            await Check("summary keyboard directions and activation", async () =>
            {
                foreach (var (key, side) in new[] { (Key.Left, DockSide.Left), (Key.Up, DockSide.Top), (Key.Right, DockSide.Right), (Key.Down, DockSide.Bottom) })
                {
                    Named<Button>("EdgeSummary").RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    await Layout(); Equal(side, settings.Side); Within(Desktop.Bounds(window), Desktop.WorkArea(window));
                }
                Named<Button>("EdgeSummary").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Layout(); Require(!Field<bool>(window, "collapsed"), "keyboard activation did not expand");
            });
            await Check("arrow click keeps card open across leave, deactivate and legacy preferences", async () =>
            {
                foreach (var (automatic, pinned) in new[] { (true, false), (false, false), (true, true), (false, true) })
                {
                    settings.AutoCollapse = automatic; settings.Pinned = false;
                    await ReleaseCardAtEdge(DockSide.Right);
                    Require(Field<bool>(window, "collapsed"), "arrow test did not begin with a compact tag");
                    settings.Pinned = pinned;
                    Named<Button>("EdgeArrow").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Layout(); Require(!Field<bool>(window, "collapsed"), "arrow click did not expand card");
                    RaiseMouseEvent(window, Mouse.MouseLeaveEvent); RaiseDeactivated();
                    await Task.Delay(750); await Layout();
                    Require(!Field<bool>(window, "collapsed"), $"card collapsed after leave with legacy AutoCollapse={automatic}, Pinned={pinned}");
                }
                settings.Pinned = false; Invoke("UpdateView");
            });
            await Check("pin button selection is visible, saved and controls edge drag collapse", async () =>
            {
                Invoke("ExpandPanel", true); settings.Pinned = false; Invoke("UpdateView"); await Layout();
                var pin = Named<Button>("PinButton");
                var accent = BrushColor((Brush)window.FindResource("Accent"));
                var tint = BrushColor((Brush)window.FindResource("AccentTint"));
                var idleBackground = BrushColor(pin.Background);
                var idleForeground = BrushColor(pin.Foreground); var idleBorder = BrushColor(pin.BorderBrush);
                var vectorContent = pin.Content; var vectorGeometry = ButtonVectorGeometry(pin);
                Require(Descendants<VectorPath>(pin).Any(path => path.Name.Length > 0), "pin vector path is not named");
                Equal("固定展开", pin.ToolTip as string);
                Equal("固定展开", AutomationProperties.GetName(pin));
                Equal("未固定，未选中", AutomationProperties.GetItemStatus(pin));
                Equal(new Thickness(0), pin.BorderThickness);
                pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                Require(settings.Pinned && new SettingsStore(artifactDirectory).Load().Pinned, "selected pin state was not saved");
                Require(ReferenceEquals(vectorContent, pin.Content), "pin toggle replaced its vector artwork");
                Equal(vectorGeometry, ButtonVectorGeometry(pin)); Equal("取消固定展开", pin.ToolTip as string);
                Equal("取消固定展开", AutomationProperties.GetName(pin));
                Equal("已固定，已选中", AutomationProperties.GetItemStatus(pin));
                Equal(accent, BrushColor(pin.Foreground)); Equal(accent, BrushColor(pin.BorderBrush));
                Equal(tint, BrushColor(pin.Background)); Equal(new Thickness(1), pin.BorderThickness);
                Require(BrushColor(pin.Background) != idleBackground, "selected pin has no distinct background");
                Capture(window, "13-pin-selected.png");
                await ReleaseCardAtEdge(DockSide.Right);
                Require(!Field<bool>(window, "collapsed") && settings.Pinned, "pinned card collapsed after edge drag release");
                pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                Require(!settings.Pinned && !new SettingsStore(artifactDirectory).Load().Pinned, "unselected pin state was not saved");
                Require(ReferenceEquals(vectorContent, pin.Content), "unpinning replaced its vector artwork");
                Equal(vectorGeometry, ButtonVectorGeometry(pin)); Equal("固定展开", pin.ToolTip as string);
                Equal("固定展开", AutomationProperties.GetName(pin));
                Equal("未固定，未选中", AutomationProperties.GetItemStatus(pin));
                Equal(idleBackground, BrushColor(pin.Background)); Equal(new Thickness(0), pin.BorderThickness);
                Equal(idleForeground, BrushColor(pin.Foreground)); Equal(idleBorder, BrushColor(pin.BorderBrush));
                Capture(window, "14-pin-unselected.png");
                await ReleaseCardAtEdge(DockSide.Right);
                Require(Field<bool>(window, "collapsed"), "unselected pin prevented edge drag collapse");
                Named<Button>("EdgeArrow").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                RaiseMouseEvent(window, Mouse.MouseLeaveEvent); RaiseDeactivated(); await Task.Delay(750);
                Require(!Field<bool>(window, "collapsed"), "unpinning restored mouse-leave collapse");
                pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Invoke("CollapsePanel", true); await Layout();
                Require(Field<bool>(window, "collapsed") && !settings.Pinned && !new SettingsStore(artifactDirectory).Load().Pinned, "manual collapse did not clear the selected pin");
                Invoke("ExpandPanel", false); await Layout();
                Equal(vectorGeometry, ButtonVectorGeometry(pin)); Equal("未固定，未选中", AutomationProperties.GetItemStatus(pin));
            });
            await Check("centered card drag release stays expanded and clears docking", async () =>
            {
                Invoke("ExpandPanel", false); await Layout();
                var bounds = Desktop.Bounds(window); var work = Desktop.WorkArea(window);
                Desktop.Place(window, work.Left + (work.Width - bounds.Width) / 2, work.Top + (work.Height - bounds.Height) / 2);
                await Layout(); Invoke("FinishTitleDrag"); await Layout();
                Require(!Field<bool>(window, "collapsed"), "centered drag release collapsed card");
                Equal(DockSide.None, settings.Side); Within(Desktop.Bounds(window), Desktop.WorkArea(window));
                RaiseMouseEvent(window, Mouse.MouseLeaveEvent); RaiseDeactivated(); await Task.Delay(750);
                Require(!Field<bool>(window, "collapsed"), "centered card collapsed after leave");
            });
            await Check("edge device click opens its own operation card and keeps it open", async () =>
            {
                await ReleaseCardAtEdge(DockSide.Right);
                var button = Named<StackPanel>("EdgeDevices").Children.OfType<Button>()
                    .Single(item => item.DataContext is DeviceRow { Serial: "UI-D" });
                RaiseMouseEvent(button, Mouse.MouseEnterEvent); await Task.Delay(250);
                Require(Field<bool>(window, "collapsed"), "device hover opened the card");
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                Equal("UI-D", model.Selected?.Serial);
                Require(!Field<bool>(window, "collapsed"), "device click failed to open card");
                RaiseMouseEvent(window, Mouse.MouseLeaveEvent); RaiseDeactivated(); await Task.Delay(750);
                Require(!Field<bool>(window, "collapsed"), "device operation card collapsed after leave");
                Equal("UI-D", model.Selected?.Serial);
                Capture(window, "10-edge-device-card.png");
            });
            await Check("context menu balances interaction guard and preserves expanded card", async () =>
            {
                settings.AutoCollapse = true; settings.Pinned = false; settings.Side = DockSide.Right;
                var menu = Named<Border>("Frame").ContextMenu;
                menu.PlacementTarget = Named<Border>("Frame"); menu.IsOpen = true;
                await Layout();
                Require(Field<int>(window, "interactionDepth") > 0, "context menu did not pause interaction");
                Invoke("CollapsePanel", true); Require(!Field<bool>(window, "collapsed"), "menu allowed a collapse");
                menu.IsOpen = false; await Until(() => Field<int>(window, "interactionDepth") == 0); await Layout();
                Equal(0, Field<int>(window, "interactionDepth"));
                RaiseMouseEvent(window, Mouse.MouseLeaveEvent); RaiseDeactivated(); await Task.Delay(750);
                Require(!Field<bool>(window, "collapsed"), "menu closure caused later collapse");
            });
            await Check("modal work-area limits, initial focus and focus restoration", async () =>
            {
                var trigger = Named<Button>("CollapseButton"); window.Activate(); trigger.Focus();
                Invoke("SyncStartupState");
                var actualAutoStart = settings.AutoStart;
                settings.AutoStart = !actualAutoStart;
                await InspectDialog(() => Invoke("OpenSettings", window, new RoutedEventArgs()), async dialog =>
                {
                    await Layout(); Within(Desktop.Bounds(dialog), Desktop.WorkArea(dialog));
                    Require(dialog.IsKeyboardFocusWithin, "focus did not enter modal");
                    Require(dialog.MaxHeight <= Desktop.WorkArea(dialog).Height / Desktop.Scale(dialog), "dialog is taller than work area");
                    var startup = Descendants<CheckBox>(dialog).Single(box => AutomationProperties.GetAutomationId(box) == "StartupCheckBox");
                    Equal(actualAutoStart, startup.IsChecked == true);
                    Equal("登录 Windows 后自动启动", startup.Content as string);
                    var versionInfo = Descendants<TextBlock>(dialog).Single(block => AutomationProperties.GetAutomationId(block) == "AppVersionInfo");
                    Equal("ADB Dock · v" + typeof(SettingsWindow).Assembly.GetName().Version!.ToString(3), versionInfo.Text);
                    Require(versionInfo.IsVisible && versionInfo.ActualHeight > 0 && versionInfo.ActualWidth > 0, "settings version footer is not visible");
                    var versionTop = versionInfo.TranslatePoint(new Point(), dialog).Y;
                    var bottomGap = dialog.ActualHeight - versionTop - versionInfo.ActualHeight;
                    Require(bottomGap >= 0 && bottomGap <= 60, "settings version is not aligned to the window bottom");
                    var body = ((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single();
                    var bodyOrigin = body.TranslatePoint(new Point(), dialog);
                    Require(body.ViewportHeight > 0 && bodyOrigin.Y >= 0 && bodyOrigin.Y + body.ActualHeight <= versionTop + 1,
                        "settings body overlaps the version footer or loses its viewport");
                    Require(bodyOrigin.X >= 0 && bodyOrigin.X + body.ActualWidth <= dialog.ActualWidth + 1
                        && ((FrameworkElement)body.Content).ActualWidth <= body.ViewportWidth + 1, "settings body is clipped horizontally");
                    Capture(dialog, "04-settings.png");
                });
                await Layout(); Equal(trigger, Keyboard.FocusedElement);
            });
            await Check("overflow counts, stable edge order and hidden-device aggregate", async () =>
            {
                await SetSnapshot(SixDevices(), () => model.Devices.Count == 6 && model.OnlineCount == "6");
                settings.Side = DockSide.Right; Invoke("CollapsePanel", true); await Layout();
                var original = string.Join(',', model.EdgeDevices.Select(row => row.Serial));
                var overflow = Named<StackPanel>("EdgeDevices").Children.OfType<Button>().Last();
                Require(overflow.Content is string label && label.StartsWith('+'), "overflow entry missing");
                await SetSnapshot(SixDevices(lastOffline: true), () => model.OnlineCount == "5");
                await Layout(); Equal(original, string.Join(',', model.EdgeDevices.Select(row => row.Serial)));
                overflow = Named<StackPanel>("EdgeDevices").Children.OfType<Button>().Last();
                Equal(Palette.Amber, overflow.Foreground); Equal("/6", model.TotalCount);
                Capture(window, "05-overflow-unavailable.png");
            });
            await Check("disconnect clears selected device and disables actions", async () =>
            {
                Invoke("OpenDevice", model.Devices.Single(row => row.Serial == "UI-1"));
                await SetSnapshot(SixDevices(firstOffline: true), () => model.Devices.Single(row => row.Serial == "UI-1").Device.State == "offline");
                Require(model.Selected is null, "disconnected selection survived");
                Require(!Named<UniformGrid>("Actions").IsEnabled, "unavailable target retained actions");
            });
            await Check("listener distinguishes empty, failure and recovery", async () =>
            {
                await SetSnapshot("", () => model.AdbAvailable && model.Devices.Count == 0);
                Equal("0", model.OnlineCount); Equal("未连接", model.Aggregate);
                await SetSnapshot("ERROR:fixture ADB unavailable", () => !model.AdbAvailable);
                Equal("—", model.OnlineCount); Equal("", model.TotalCount); Equal(0, model.EdgeDevices.Count);
                Capture(window, "06-adb-error.png");
                await SetSnapshot(Initial, () => model.AdbAvailable && model.Devices.Count == 4);
                Equal("2", model.OnlineCount);
            });
            await Check("operation detail survives subsequent listener snapshots", async () =>
            {
                const string detail = "UI result retained after refresh: complete diagnostic output";
                window.ReportError(new InvalidOperationException(detail));
                await SetSnapshot(SixDevices(), () => model.Devices.Count == 6);
                Require(Named<Button>("ResultButton").IsEnabled, "result entry unavailable");
                await InspectDialog(() => Invoke("ResultsClick", window, new RoutedEventArgs()), async dialog =>
                {
                    Require(Output(dialog).Text.Contains(detail), "result was overwritten by status refresh");
                    Capture(dialog, "07-operation-results.png"); await Task.CompletedTask;
                });
            });
            await Check("device information and manual performance monitoring stay bound to selected fixture", async () =>
            {
                Task? monitor = null;
                Invoke("OpenDevice", model.Devices.Single(row => row.Serial == "UI-2"));
                await InspectDialog(() => ClickAction("info"), async dialog =>
                {
                    Require(Output(dialog).Text.Contains("UI-2") && Output(dialog).Text.Contains("UI Fixture Phone"), "information lost target or tool output");
                    TextBlock Metric(string id) => Descendants<TextBlock>(dialog).Single(block => AutomationProperties.GetAutomationId(block) == id);
                    var cpu = Metric("CpuUsageValue"); var memory = Metric("MemoryUsageValue");
                    var battery = Metric("BatteryLevelValue"); var temperature = Metric("BatteryTemperatureValue");
                    var status = Metric("PerformanceStatus");
                    var toggle = Descendants<Button>(dialog).Single(button => AutomationProperties.GetAutomationId(button) == "PerformanceToggleButton");
                    var metrics = new[] { cpu, memory, battery, temperature };
                    Require(metrics.All(metric => metric.Text == "未开始"), "performance sampling started without an explicit action");
                    Require(Field<Task?>(dialog, "samplingTask") is null, "performance reader started automatically");
                    toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => MetricNear(cpu.Text, 30, percent: true) && MetricNear(memory.Text, 62.5, percent: true)
                        && MetricNear(battery.Text, 75, percent: true) && MetricNear(temperature.Text, 31.5));
                    monitor = Field<Task>(dialog, "samplingTask");
                    Require(!monitor.IsCompleted, "performance monitoring stopped before pause");
                    Capture(dialog, "08-device-information.png");
                    toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => monitor.IsCompleted && toggle.IsEnabled && status.Text.Contains("暂停"));
                    var frozen = metrics.Select(metric => metric.Text).ToArray();
                    await Task.Delay(2250);
                    Require(metrics.Select(metric => metric.Text).SequenceEqual(frozen), "paused performance values continued updating");
                    toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => Field<Task?>(dialog, "samplingTask") is { IsCompleted: false } current && !ReferenceEquals(current, monitor));
                    monitor = Field<Task>(dialog, "samplingTask");
                });
                await Until(() => monitor?.IsCompleted == true);
            });
            await Check("key panel exposes only four permitted commands", async () =>
            {
                await InspectDialog(() => ClickAction("keys"), async dialog =>
                {
                    Require(dialog is KeysWindow, "incorrect action dialog");
                    var commands = Descendants<Button>(dialog).Select(button => button.Content?.ToString()).ToArray();
                    foreach (var command in new[] { "Home", "返回", "最近任务", "亮屏" }) Require(commands.Contains(command), "missing key " + command);
                    Require(!commands.Any(command => command?.Contains("重启") == true || command?.Contains("关机") == true), "power command exposed");
                    await Task.CompletedTask;
                });
            });
            await Check("Logcat regex semantics refilter retained raw lines and keep retention bounded", async () =>
            {
                var buffer = new LogFilterBuffer();
                buffer.Append(new[] { "I boot", "E crash", "W warning", "DEBUG detail" });
                Require(buffer.SetFilter("crash|warning"), "regex alternation rejected");
                Require(buffer.VisibleLines.SequenceEqual(new[] { "E crash", "W warning" }), "alternation did not match either branch");
                Require(buffer.SetFilter("(?i)^debug"), "inline case-insensitive flag rejected");
                Require(buffer.VisibleLines.SequenceEqual(new[] { "DEBUG detail" }), "inline flag did not match mixed case");
                Require(buffer.SetFilter("no-match"), "valid empty-result filter rejected");
                Equal(0, buffer.VisibleLines.Count);
                buffer.Append("W retained while hidden");
                Equal(5, buffer.TotalCount);
                Require(buffer.SetFilter("^W"), "changed filter rejected");
                Require(buffer.VisibleLines.SequenceEqual(new[] { "W warning", "W retained while hidden" }), "changing filter lost previously hidden raw lines");
                Require(buffer.SetFilter(""), "clear filter rejected");
                Equal(5, buffer.VisibleLines.Count);
                var retained = new LogFilterBuffer();
                Equal(20_000, LogFilterBuffer.MaximumLines);
                Equal(16 * 1024 * 1024, LogFilterBuffer.MaximumCharacters);
                Equal(8000, LogFilterBuffer.MaximumLineLength);
                retained.Append(Enumerable.Range(0, 22_000).Select(index => "line-" + index));
                Equal(LogFilterBuffer.MaximumLines, retained.TotalCount);
                Equal("line-2000", retained.VisibleLines.First());
                Equal("line-21999", retained.VisibleLines.Last());
                Require(retained.SetFilter("^line-21999$") && retained.VisibleLines.Count == 1, "latest retained line could not be refiltered");
                retained.SetFilter(""); Equal(LogFilterBuffer.MaximumLines, retained.VisibleLines.Count);
                var bounded = new LogFilterBuffer(capacity: 10, maxLineLength: 12, maxCharacters: 20);
                bounded.Append(new[] { "1234567890", "abcdefghij", "KLMNOPQRST" });
                Equal(2, bounded.TotalCount); Equal(20, bounded.TotalCharacters);
                Require(bounded.VisibleLines.SequenceEqual(new[] { "abcdefghij", "KLMNOPQRST" }), "character cap did not evict oldest raw lines");
                bounded.Append(new string('x', 100));
                Require(bounded.VisibleLines.Single().Length <= 12 && bounded.TotalCharacters <= 20, "long log line exceeded its line or character bound");
                var longLines = new LogFilterBuffer();
                longLines.Append(new string('x', LogFilterBuffer.MaximumLineLength + 500));
                Require(longLines.VisibleLines.Single().Length <= LogFilterBuffer.MaximumLineLength, "default long-line bound was exceeded");
                await Task.CompletedTask;
            });
            await Check("invalid regex reports error while last valid filter remains usable", async () =>
            {
                var buffer = new LogFilterBuffer();
                buffer.Append(new[] { "KEEP alpha", "drop beta" });
                Require(buffer.SetFilter("^KEEP"), "initial valid regex rejected");
                var before = buffer.VisibleLines.ToArray();
                Require(!buffer.SetFilter("("), "invalid regex was accepted");
                Require(!string.IsNullOrWhiteSpace(buffer.FilterError), "invalid regex did not report an explicit error");
                Equal("^KEEP", buffer.ActivePattern);
                Require(buffer.VisibleLines.SequenceEqual(before), "invalid regex changed last valid results");
                buffer.Append("KEEP gamma");
                Require(buffer.VisibleLines.SequenceEqual(new[] { "KEEP alpha", "KEEP gamma" }), "invalid edit stopped the last valid live filter");
                Require(buffer.SetFilter(""), "clear failed after invalid regex");
                Require(buffer.FilterError is null && !buffer.IsFilterSuspended, "clear retained the regex error state");
                Equal(3, buffer.VisibleLines.Count);
                await Task.CompletedTask;
            });
            await Check("expensive regex times out once and stays suspended until text changes", async () =>
            {
                Require(LogFilterBuffer.MatchTimeout <= TimeSpan.FromMilliseconds(50), "per-match timeout exceeds 50ms");
                var buffer = new LogFilterBuffer();
                var hostile = new string('a', 4096) + "!";
                buffer.Append(new[] { "safe log", hostile });
                Require(buffer.SetFilter("safe"), "initial safe regex rejected");
                var before = buffer.VisibleLines.ToArray();
                var watch = Stopwatch.StartNew();
                Require(!buffer.SetFilter("^(a+)+$"), "pathological regex unexpectedly completed");
                Require(watch.Elapsed < TimeSpan.FromMilliseconds(750), "pathological regex blocked beyond its configured budget");
                Require(buffer.IsFilterSuspended && !string.IsNullOrWhiteSpace(buffer.FilterError), "timeout did not suspend the filter with an error");
                Require(buffer.VisibleLines.SequenceEqual(before), "timeout replaced the last complete view");
                watch.Restart();
                for (var i = 0; i < 12; i++) { buffer.Append(hostile); buffer.SetFilter("^(a+)+$"); }
                Require(watch.Elapsed < TimeSpan.FromMilliseconds(200), "suspended filter retried the same expensive pattern");
                Require(buffer.IsFilterSuspended && buffer.VisibleLines.SequenceEqual(before), "new logs unexpectedly resumed a suspended filter");
                Require(buffer.SetFilter("safe|^a"), "changed valid filter did not resume matching");
                Require(!buffer.IsFilterSuspended && buffer.FilterError is null, "changed filter retained timeout state");
                Equal(buffer.TotalCount, buffer.VisibleLines.Count);
                await Task.CompletedTask;
            });
            await Check("Logcat regex UI filters live output, clears errors, pauses and stops its client", async () =>
            {
                Task? reader = null;
                await InspectDialog(() => ClickAction("logcat"), async dialog =>
                {
                    Require(dialog is LogWindow, "incorrect Logcat dialog");
                    await Until(() => Output(dialog).Text.Contains("UI-2 test log"));
                    reader = Field<Task>(dialog, "reader");
                    var pause = Descendants<Button>(dialog).Single(button => Equals(button.Content, "暂停显示"));
                    pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var before = Output(dialog).Text; await Task.Delay(400); Equal(before, Output(dialog).Text);
                    pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => Output(dialog).Text != before);
                    var filter = Descendants<TextBox>(dialog).Single(box => AutomationProperties.GetAutomationId(box) == "LogFilterInput");
                    var clear = Descendants<Button>(dialog).Single(button => AutomationProperties.GetAutomationId(button) == "ClearLogFilterButton");
                    var status = Descendants<TextBlock>(dialog).Single(block => AutomationProperties.GetAutomationId(block) == "LogFilterStatus");
                    filter.Text = @"test log \d*[02468]$";
                    await Until(() => HasOnlyEvenLogLines(Output(dialog).Text));
                    var matched = Output(dialog).Text;
                    await Until(() => Output(dialog).Text != matched && HasOnlyEvenLogLines(Output(dialog).Text));
                    Capture(dialog, "11-logcat-regex-filter.png");
                    filter.Text = "^WILL_NOT_MATCH$";
                    await Until(() => Output(dialog).Text.Length == 0);
                    await Task.Delay(400); Equal("", Output(dialog).Text);
                    filter.Text = @"test log \d*[02468]$";
                    await Until(() => HasOnlyEvenLogLines(Output(dialog).Text));
                    filter.Text = "(";
                    await Until(() => HasFilterError(status.Text));
                    Require(Output(dialog).Text.Contains("UI-2 test log"), "invalid regex erased last valid output");
                    Require(reader?.IsFaulted == false, "invalid regex terminated the log reader");
                    Capture(dialog, "12-logcat-invalid-regex.png");
                    clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => filter.Text.Length == 0 && !HasFilterError(status.Text));
                    Require(LogLines(Output(dialog).Text).Any(line => "13579".Contains(line[^1])), "clearing regex did not reveal retained odd-numbered logs");
                    Capture(dialog, "09-logcat.png");
                });
                await Until(() => reader?.IsCompleted == true);
            });
            if (Field<AdbService?>(window, "adb") is { } adb) listener = Field<Task?>(adb, "_backgroundTask");
        }
        catch (Exception exception)
        {
            failures.Add("Runner failure: " + exception); Console.Error.WriteLine(failures[^1]);
        }
        finally
        {
            runFinished = true;
            foreach (var owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            await (Task)Invoke("ExitAsync")!;
        }
    }

    private void OnExit(object? sender, ExitEventArgs e)
    {
        if (!runFinished) failures.Add("Application exited before native checks finished; another instance may own this desktop session.");
        if (listener is not null && !listener.IsCompleted) failures.Add("Listener did not stop before application shutdown.");
        Console.WriteLine($"Native WPF checks: {passed} passed, {failures.Count} failed. Artifacts: {artifactDirectory}");
        e.ApplicationExitCode = failures.Count == 0 ? 0 : 1;
    }

    private async Task Check(string label, Func<Task> check)
    {
        try { await check(); passed++; Console.WriteLine("PASS " + label); }
        catch (Exception exception)
        {
            failures.Add(label + ": " + exception); Console.Error.WriteLine("FAIL " + failures[^1]);
            Named<Border>("Frame").ContextMenu.IsOpen = false;
            foreach (var owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            settings.AutoCollapse = false; settings.Pinned = false;
            Invoke("ExpandPanel", false);
        }
    }

    private async Task InspectDialog(Action trigger, Func<Window, Task> inspect)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += async (_, _) =>
        {
            var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault(candidate => candidate.IsVisible);
            if (dialog is null)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(12)) { timer.Stop(); completion.TrySetException(new TimeoutException("Expected modal did not appear.")); }
                return;
            }
            timer.Stop();
            try { await inspect(dialog); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { dialog.Close(); }
        };
        timer.Start();
        try { trigger(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { timer.Stop(); }
    }

    private async Task SetSnapshot(string text, Func<bool> predicate)
    {
        var temporary = snapshotFile + ".tmp";
        File.WriteAllText(temporary, text); File.Move(temporary, snapshotFile, true);
        await Until(predicate); await Layout();
    }
    private static string SixDevices(bool firstOffline = false, bool lastOffline = false) => string.Concat(Enumerable.Range(1, 6).Select(index =>
        $"UI-{index} {(index == 1 && firstOffline || index == 6 && lastOffline ? "offline" : "device")} usb:{index} model:Fixture_{index}\n"));
    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("UI condition was not satisfied in 10 seconds.");
            await Task.Delay(50);
        }
    }
    private static bool ExpectedStartupState()
    {
        try { return Desktop.IsStartupEnabled(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
    private async Task Layout()
    {
        await application.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }
    private async Task ReleaseCardAtEdge(DockSide side)
    {
        Invoke("ExpandPanel", false); await Layout();
        var bounds = Desktop.Bounds(window); var work = Desktop.WorkArea(window);
        var x = side == DockSide.Left ? work.Left : side == DockSide.Right ? work.Right - bounds.Width : work.Left + (work.Width - bounds.Width) / 2;
        var y = side == DockSide.Top ? work.Top : side == DockSide.Bottom ? work.Bottom - bounds.Height : work.Top + (work.Height - bounds.Height) / 2;
        Desktop.Place(window, x, y); await Layout();
        Invoke("FinishTitleDrag"); await Layout();
    }
    private static void RaiseMouseEvent(UIElement target, RoutedEvent routedEvent) => target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = routedEvent });
    private void RaiseDeactivated() => typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { EventArgs.Empty });
    private void ClickAction(string action) => Named<UniformGrid>("Actions").Children.OfType<Button>().Single(button => Equals(button.Tag, action)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private T Named<T>(string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing named control: " + name);
    private object? Invoke(string method, params object?[] args)
    {
        try { return typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null) { throw exception.InnerException; }
    }
    private static T Field<T>(object target, string name)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field) return (T)field.GetValue(target)!;
        throw new InvalidOperationException("Missing field: " + name);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static TextBox Output(Window dialog) => Descendants<TextBox>(dialog).Single(box => AutomationProperties.GetAutomationId(box) == "TextOutput");
    private static string[] LogLines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    private static bool HasOnlyEvenLogLines(string text) => LogLines(text) is { Length: > 0 } lines && lines.All(line => "02468".Contains(line[^1]));
    private static bool HasFilterError(string text) => text.Contains("无效") || text.Contains("错误") || text.Contains("失败");
    private static string ButtonVectorGeometry(Button button)
    {
        var paths = Descendants<VectorPath>(button).ToArray();
        Require(paths.Length > 0 && paths.All(path => path.Data is not null), "button vector path is missing");
        return string.Join("|", paths.Select(path => path.Data!.ToString(CultureInfo.InvariantCulture)));
    }
    private static Color BrushColor(Brush brush) => brush is SolidColorBrush solid ? solid.Color : throw new InvalidOperationException("Expected a solid button-state brush");
    private static bool MetricNear(string text, double expected, bool percent = false)
    {
        var match = Regex.Match(text, percent ? @"(-?\d+(?:[.,]\d+)?)\s*%" : @"(-?\d+(?:[.,]\d+)?)");
        return match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var actual)
            && Math.Abs(actual - expected) <= .15;
    }
    private void Capture(Window target, string name)
    {
        target.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth), (int)Math.Ceiling(target.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(artifactDirectory, name)); encoder.Save(file);
    }
    private static void Within(NativeRect bounds, NativeRect work) => Require(bounds.Left >= work.Left - 2 && bounds.Top >= work.Top - 2 && bounds.Right <= work.Right + 2 && bounds.Bottom <= work.Bottom + 2, "window exceeds monitor work area");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected '{expected}', got '{actual}'.");
}
