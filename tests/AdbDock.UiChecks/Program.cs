using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
        // Keep window checks from signaling the user's installed client.
        var app = new AdbDock.App.App($"UiChecks.{Environment.ProcessId}");
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
    private bool exitRequestSent;
    private bool exitDialogClosed;
    private const int ExitCheckDelay = 137;

    public void Attach()
    {
        // These values are inherited only by our fixture clients; no user configuration is read.
        Environment.SetEnvironmentVariable("ADBDOCK_HOME", artifactDirectory);
        Environment.SetEnvironmentVariable("ADBDOCK_UI_SNAPSHOT", snapshotFile);
        File.WriteAllText(snapshotFile, "");
        new SettingsStore(artifactDirectory).Save(new AppSettings
        {
            AdbPath = Path.Combine(artifactDirectory, "missing", "adb.exe"), ScrcpyPath = tool, ScreenshotDirectory = artifactDirectory,
            AutoStart = false, Topmost = false, ShowFloatingTab = true, AutoCollapse = true,
            AutoCollapseDelaySeconds = 2, Side = DockSide.None
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
            await Check("first launch guides setup and authorization and holds the panel open until a device is online", async () =>
            {
                var config = new SettingsStore(artifactDirectory);
                var leave = Field<DispatcherTimer>(window, "leaveTimer");
                try
                {
                    await Until(() => model.EmptyTitle == "先配置 ADB");
                    await Layout();
                    var setup = Descendants<Button>(window).Single(button => AutomationProperties.GetAutomationId(button) == "EmptyHelpButton");
                    Equal("配置 ADB", (string)setup.Content);
                    Require(!leave.IsEnabled && !config.Load().HasConnectedDevice, "first run started auto-collapse before connecting");
                    var outside = OutsidePoint(); var now = Environment.TickCount64;
                    Invoke("EvaluateAutoCollapse", outside, now);
                    Invoke("EvaluateAutoCollapse", outside, now + 10000);
                    Require(!Field<bool>(window, "collapsed"), "first-run setup collapsed while the user was reading");
                    Capture(window, "17-first-launch-configure-adb.png");

                    await InspectDialog(() => setup.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), async dialog =>
                    {
                        T Control<T>(string id) where T : DependencyObject =>
                            Descendants<T>(dialog).Single(item => AutomationProperties.GetAutomationId(item) == id);
                        var adbPath = Control<TextBox>("AdbPath");
                        var scrcpyPath = Control<TextBox>("ScrcpyPath");
                        var adbResult = Control<TextBlock>("AdbDetectionResult");
                        var scrcpyResult = Control<TextBlock>("ScrcpyDetectionResult");
                        var detect = Control<Button>("DetectToolsButton");
                        await Until(() => adbPath.IsKeyboardFocused || ReferenceEquals(FocusManager.GetFocusedElement(dialog), adbPath));
                        detect.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => adbResult.Text.Contains("检测失败") && scrcpyResult.Text.Contains("检测通过") && detect.IsEnabled);

                        adbPath.Text = tool;
                        scrcpyPath.Text = Path.Combine(artifactDirectory, "missing", "scrcpy.exe");
                        detect.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => adbResult.Text.Contains("检测通过") && scrcpyResult.Text.Contains("检测失败") && detect.IsEnabled);
                        Capture(dialog, "18-independent-tool-results.png");
                        Control<Button>("SaveSettingsButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Require(!dialog.IsVisible, "unavailable optional scrcpy prevented saving ADB");
                    });
                    await Until(() => model.AdbAvailable && model.Devices.Count == 0);
                    await Layout();
                    Equal("等待连接 Android 设备", model.EmptyTitle);
                    Equal("查看连接步骤", (string)setup.Content);
                    Require(!leave.IsEnabled, "empty device list activated first-run auto-collapse");
                    Capture(window, "19-waiting-for-phone.png");

                    await SetSnapshot("UI-A unauthorized usb:2\n", () => model.Devices.Count == 1 && model.Devices[0].NeedsAuthorization);
                    var hint = Descendants<TextBlock>(Named<ItemsControl>("DeviceList")).Single(item =>
                        AutomationProperties.GetAutomationId(item) == "AuthorizationHint");
                    Require(hint.IsVisible && hint.Text.Contains("解锁手机") && hint.Text.Contains("自动更新"),
                        "authorization guidance was not visible in the device list");
                    Require(!leave.IsEnabled && !config.Load().HasConnectedDevice, "an unauthorized device completed onboarding");
                    Capture(window, "20-authorization-in-device-list.png");

                    await SetSnapshot(Initial, () => model.AdbAvailable && model.OnlineCount == "2");
                    Require(leave.IsEnabled && config.Load().HasConnectedDevice,
                        "a confirmed online device did not persist completion and enable the configured timer");
                }
                finally
                {
                    settings.AutoCollapse = false;
                    settings.AdbPath = settings.ScrcpyPath = tool;
                    Invoke("UpdateView");
                    Invoke("SaveSettings");
                    if (Field<AdbService?>(window, "adb") is null) await (Task)Invoke("RestartServicesAsync")!;
                    await SetSnapshot(Initial, () => model.AdbAvailable && model.Devices.Count == 4);
                }
            });
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
                var headerButtons = new[] { "FloatButton", "SettingsButton", "CloseButton" }
                    .Select(id => Descendants<ButtonBase>(window).Single(button => AutomationProperties.GetAutomationId(button) == id)).ToArray();
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
            await Check("full device switcher preserves its surface and routes actions to the selected serial", async () =>
            {
                Invoke("OpenDevice", model.Devices.First(row => row.Serial == "UI-D"));
                await Layout();
                Equal("UI-D", model.Selected?.Serial);
                var actions = Named<UniformGrid>("Actions");
                Equal(6, actions.Children.Count); Require(actions.IsEnabled, "online actions disabled");
                Equal("UI-D", Descendants<TextBlock>(window).Single(item => AutomationProperties.GetAutomationId(item) == "SelectedSerial").Text);
                Equal("mirror,screenshot,install,keys,info,logcat", string.Join(',', actions.Children.OfType<Button>().Select(button => button.Tag)));
                var picker = Named<ComboBox>("DeviceSwitcher");
                Equal("Serial", picker.SelectedValuePath); Equal("DisplayText", TextSearch.GetTextPath(picker));
                Require(PickerOptions(picker).Select(option => option.Serial).ToHashSet().SetEquals(new[] { "UI-C", "UI-D" }), "full picker contains unavailable devices or loses an online device");
                var surface = Named<StackPanel>("DetailPanel"); var bounds = Desktop.Bounds(window);
                picker.SelectedValue = "UI-C"; await Layout();
                Equal("UI-C", model.Selected?.Serial); Equal("Fixture One", model.Selected?.Name);
                Require(surface.IsVisible && actions.IsEnabled, "switching device left the operation surface");
                EqualBounds(bounds, Desktop.Bounds(window));
                var itemsSource = picker.ItemsSource; var chosen = picker.SelectedItem;
                Invoke("UpdateView");
                Require(ReferenceEquals(itemsSource, picker.ItemsSource) && ReferenceEquals(chosen, picker.SelectedItem), "unchanged refresh rebuilt picker identity");
                settings.AutoCollapse = true; settings.AutoCollapseDelaySeconds = 2; Invoke("UpdateView");
                picker.IsDropDownOpen = true; await Layout();
                var outside = OutsidePoint(); var now = Environment.TickCount64;
                Invoke("EvaluateAutoCollapse", outside, now); Invoke("EvaluateAutoCollapse", outside, now + 10_000);
                Require(!Field<bool>(window, "collapsed"), "open full-device dropdown allowed auto collapse");
                var longName = "Same Phone " + new string('W', 100);
                var longModel = longName.Replace(' ', '_');
                var sameNames = Initial.Replace("Fixture_One", longModel, StringComparison.Ordinal).Replace("Fixture_Two", longModel, StringComparison.Ordinal);
                await SetSnapshot(sameNames, () => model.Devices.Where(row => row.IsOnline).All(row => row.Name == longName));
                Equal("UI-C", model.Selected?.Serial); Equal("UI-C", picker.SelectedValue as string);
                await Layout();
                var options = PickerOptions(picker);
                foreach (var option in options)
                {
                    var item = picker.ItemContainerGenerator.ContainerFromItem(option) as ComboBoxItem
                        ?? throw new InvalidOperationException("Device picker dropdown item missing");
                    var texts = Descendants<TextBlock>(item).ToArray();
                    Require(texts.Any(text => text.Text == longName && text.TextTrimming == TextTrimming.CharacterEllipsis),
                        "long device name does not use ellipsis in the dropdown");
                    Require(texts.Any(text => text.Text.Contains(option.Serial, StringComparison.Ordinal) &&
                        text.Text.Contains("在线", StringComparison.Ordinal) && text.TextTrimming == TextTrimming.None),
                        "dropdown does not display the full serial and online status");
                    Require(texts.All(text => text.TextWrapping == TextWrapping.NoWrap), "dropdown item wraps");
                    Equal(option.DisplayText, AutomationProperties.GetName(item));
                    Equal(option.DisplayText, item.ToolTip as string);
                }
                picker.IsDropDownOpen = false; settings.AutoCollapse = false; Invoke("UpdateView");
                await Layout();
                var selection = Descendants<TextBlock>(picker).Single(text => text.Text == longName);
                Equal(TextWrapping.NoWrap, selection.TextWrapping);
                Equal(TextTrimming.CharacterEllipsis, selection.TextTrimming);
                Equal(((DevicePicker.DeviceOption)picker.SelectedItem).DisplayText, picker.ToolTip as string);
                await InspectDialog(() => ClickAction("info"), async dialog =>
                {
                    Require(Output(dialog).Text.Contains("UI Fixture Phone UI-C", StringComparison.Ordinal), "full switcher did not send the selected serial to ADB -s");
                    Require(ReferenceEquals(dialog.Owner, window) && surface.IsVisible, "full operation dialog moved to a different surface");
                    await Task.CompletedTask;
                });
                await SetSnapshot(Initial, () => model.Devices.Single(row => row.Serial == "UI-C").Name == "Fixture One");
                Equal("UI-C", picker.SelectedValue as string);
                picker.SelectedValue = "UI-D"; await Layout();
                Equal("UI-D", model.Selected?.Serial); Equal("Fixture Two", model.Selected?.Name);
                Equal("UI-D", Descendants<TextBlock>(window).Single(item => AutomationProperties.GetAutomationId(item) == "SelectedSerial").Text);
                EqualBounds(bounds, Desktop.Bounds(window));
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
            await Check("screenshot picker creates its configured directory and recovers from an unusable path", async () =>
            {
                var oldDirectory = settings.ScreenshotDirectory;
                var row = model.Devices.First(device => device.IsOnline);
                Microsoft.Win32.SaveFileDialog OpenPicker() =>
                    (Microsoft.Win32.SaveFileDialog)Invoke("CreateScreenshotPicker", row)!;
                try
                {
                    settings.ScreenshotDirectory = Path.Combine(artifactDirectory, "新建截图目录", "screens");
                    var picker = OpenPicker();
                    Equal(settings.ScreenshotDirectory, picker.InitialDirectory);
                    Require(Directory.Exists(picker.InitialDirectory), "first screenshot did not create its configured directory");
                    settings.ScreenshotDirectory = Path.Combine(snapshotFile, "not-a-directory");
                    await InspectDialog(() => picker = OpenPicker(), async dialog =>
                    {
                        Require(Output(dialog).Text.Contains(settings.ScreenshotDirectory), "unusable screenshot directory missing from feedback");
                        await Task.CompletedTask;
                    });
                    Require(string.IsNullOrEmpty(picker.InitialDirectory), "picker cannot fall back to another folder");
                }
                finally { settings.ScreenshotDirectory = oldDirectory; }
            });
            await Check("list camera saves PNGs directly to the configured folder for the clicked device", async () =>
            {
                var oldDirectory = settings.ScreenshotDirectory;
                try
                {
                    settings.ScreenshotDirectory = Path.Combine(artifactDirectory, "快捷截图");
                    var camera = Descendants<Button>(Named<ItemsControl>("DeviceList")).Single(button =>
                        AutomationProperties.GetName(button) == "保存此设备截图" && button.DataContext is DeviceRow { Serial: "UI-D" });
                    Require(camera.Content is Viewbox, "quick screenshot does not display the camera glyph");
                    for (var count = 1; count <= 2; count++)
                    {
                        camera.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => Directory.Exists(settings.ScreenshotDirectory)
                            && Directory.GetFiles(settings.ScreenshotDirectory, "*.png").Length == count
                            && !Field<HashSet<string>>(window, "busy").Contains("UI-D"));
                    }
                    Require(model.Selected is null && !window.OwnedWindows.Cast<Window>().Any(dialog => dialog.IsVisible),
                        "quick screenshot changed the selected device or opened a dialog");
                    Require(File.ReadAllLines(snapshotFile + ".screenshots").SequenceEqual(new[] { "UI-D", "UI-D" }),
                        "quick screenshot did not use the clicked serial");
                    Require(Named<Button>("OpenLatestScreenshotButton").IsVisible, "screenshot completion has no open action");
                    Require(Directory.GetFiles(settings.ScreenshotDirectory, "*.png").All(path => File.ReadAllBytes(path)[0] == 0x89),
                        "quick screenshot did not save PNG data");
                }
                finally { settings.ScreenshotDirectory = oldDirectory; }
            });
            await Check("released card drag collapses immediately at all four edges", async () =>
            {
                foreach (var side in new[] { DockSide.Left, DockSide.Right, DockSide.Top, DockSide.Bottom })
                {
                    settings.AutoCollapse = false; settings.ShowFloatingTab = true;
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
                    var expandLabel = Named<TextBlock>("EdgeExpandLabel");
                    Equal("展开", expandLabel.Text);
                    Require(ReferenceEquals(arrow.Content, expandLabel) && expandLabel.IsVisible && expandLabel.ActualWidth > 0,
                        "dedicated expand control has no visible text label");
                    Capture(window, "03-edge-" + side.ToString().ToLowerInvariant() + ".png");
                    Named<Button>("EdgeArrow").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                    Within(bounds, Desktop.Bounds(window));
                    Require(!Field<bool>(window, "collapsed"), "edge arrow failed to open the covering card");
                    Invoke("CollapsePanel"); await Layout();
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
            await Check("summary ignores clicks, drags at the system threshold, restores cancellation and supports keyboard docking", async () =>
            {
                var summary = Named<Border>("EdgeSummary");
                Require(summary.Focusable, "drag summary is not keyboard focusable");
                var original = Desktop.Bounds(window); var originalSide = settings.Side;
                var screen = summary.PointToScreen(new Point(summary.ActualWidth / 2, summary.ActualHeight / 2));
                var start = new NativePoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
                var threshold = SystemParameters.MinimumHorizontalDragDistance * Desktop.Scale(window);
                var tiny = new NativePoint { X = start.X + Math.Max(0, (int)Math.Ceiling(threshold) - 1), Y = start.Y };
                var crossed = new NativePoint { X = start.X + (int)Math.Ceiling(threshold) + 2, Y = start.Y };
                Invoke("BeginSummaryPress", start);
                Require(summary.IsMouseCaptured, "summary could not capture its own drag");
                Invoke("FinishPress", false, start);
                Require(Field<bool>(window, "collapsed"), "stationary summary click opened the panel");
                EqualBounds(original, Desktop.Bounds(window));
                Invoke("BeginSummaryPress", start);
                Invoke("UpdateSummaryDrag", tiny);
                Require(!Field<bool>(window, "draggingEdge"), "sub-threshold jitter started a drag");
                EqualBounds(original, Desktop.Bounds(window));
                Invoke("FinishPress", false, tiny);
                Require(Field<bool>(window, "collapsed"), "tiny summary jitter opened the panel");
                EqualBounds(original, Desktop.Bounds(window));
                Invoke("BeginSummaryPress", start);
                Invoke("UpdateSummaryDrag", crossed);
                Require(Field<bool>(window, "draggingEdge"), "crossing the system drag threshold still requires a hold delay");
                Equal(original.Left + crossed.X - start.X, Desktop.Bounds(window).Left);
                Equal(original.Top, Desktop.Bounds(window).Top);
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                EqualBounds(original, Desktop.Bounds(window)); Equal(originalSide, settings.Side);
                Require(!summary.IsMouseCaptured && !Field<bool>(window, "draggingEdge") && ToolTipService.GetIsEnabled(Named<StackPanel>("EdgePanel")),
                    "cancelled drag did not restore capture, tooltip and drag state");
                var work = Desktop.WorkArea(window);
                var release = new NativePoint { X = work.Right - 1, Y = work.Top + work.Height / 2 };
                Invoke("BeginSummaryPress", start);
                // Mouse-up must honor the final displacement even without an intervening move event.
                Invoke("FinishPress", false, release);
                Require(Field<bool>(window, "collapsed"), "drag release accidentally opened the panel");
                Equal(DockSide.Right, settings.Side); Within(Desktop.Bounds(window), Desktop.WorkArea(window));
                await Layout();
                foreach (var (key, side) in new[] { (Key.Left, DockSide.Left), (Key.Up, DockSide.Top), (Key.Right, DockSide.Right), (Key.Down, DockSide.Bottom) })
                {
                    summary.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    await Layout(); Equal(side, settings.Side); Within(Desktop.Bounds(window), Desktop.WorkArea(window));
                }
                Named<Button>("EdgeArrow").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Layout(); Require(!Field<bool>(window, "collapsed"), "dedicated expand control did not open the panel");
            });
            await Check("edge icons catch up after a stationary summary press or lost capture", async () =>
            {
                await ReleaseCardAtEdge(DockSide.Right);
                var summary = Named<Border>("EdgeSummary");
                bool HasIcon(string serial) => Named<StackPanel>("EdgeDevices").Children.OfType<Button>()
                    .Any(button => button.DataContext is DeviceRow row && row.Serial == serial);
                foreach (var loseCapture in new[] { false, true })
                {
                    var point = summary.PointToScreen(new Point(summary.ActualWidth / 2, summary.ActualHeight / 2));
                    var start = new NativePoint { X = (int)point.X, Y = (int)point.Y };
                    Invoke("BeginSummaryPress", start);
                    await SetSnapshot(Initial.Replace("UI-D device", "UI-Z device"), () => model.Devices.Any(row => row.Serial == "UI-Z"));
                    Require(summary.IsMouseCaptured && HasIcon("UI-D"), "fixture did not defer the update during capture");
                    if (loseCapture) summary.ReleaseMouseCapture();
                    else Invoke("FinishPress", false, start);
                    Require(HasIcon("UI-Z") && !HasIcon("UI-D"), "released tab still shows the previous device");
                    await SetSnapshot(Initial, () => model.Devices.Any(row => row.Serial == "UI-D"));
                }
            });
            await Check("auto-collapse honors enabled state, configured deadline and original-tab grace", async () =>
            {
                foreach (var (automatic, seconds) in new[] { (true, 1), (true, 2), (true, 300), (false, 1) })
                {
                    settings.AutoCollapse = false; settings.ShowFloatingTab = true;
                    await ReleaseCardAtEdge(DockSide.Right);
                    Require(Field<bool>(window, "collapsed"), "arrow test did not begin with a compact tag");
                    var tab = Desktop.Bounds(window);
                    settings.AutoCollapse = automatic; settings.AutoCollapseDelaySeconds = seconds;
                    Named<Button>("EdgeArrow").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Layout(); Require(!Field<bool>(window, "collapsed"), "arrow click did not expand card");
                    var originalTab = Field<NativeRect?>(window, "expandedFromTab");
                    Require(originalTab is not null, "opening the card lost the original tab rectangle");
                    EqualBounds(tab, originalTab!.Value);
                    var tabPoint = new NativePoint { X = tab.Left + tab.Width / 2, Y = tab.Top + tab.Height / 2 };
                    var outside = OutsidePoint(); var now = Environment.TickCount64; var delay = seconds * 1000L;
                    Invoke("EvaluateAutoCollapse", tabPoint, now);
                    Invoke("EvaluateAutoCollapse", tabPoint, now + 10_000);
                    Require(!Field<bool>(window, "collapsed"), "unmoved pointer over the old tab allowed collapse");
                    Invoke("EvaluateAutoCollapse", outside, now + 10_001);
                    Invoke("EvaluateAutoCollapse", outside, now + 10_000 + delay);
                    Require(!Field<bool>(window, "collapsed"), "card collapsed before the configured deadline");
                    Invoke("EvaluateAutoCollapse", tabPoint, now + 10_001 + delay);
                    Invoke("EvaluateAutoCollapse", outside, now + 10_002 + delay);
                    Invoke("EvaluateAutoCollapse", outside, now + 10_001 + delay * 2);
                    Require(!Field<bool>(window, "collapsed"), "returning to the old tab did not reset the grace period");
                    Invoke("EvaluateAutoCollapse", outside, now + 10_002 + delay * 2);
                    Equal(automatic, Field<bool>(window, "collapsed"));
                    if (!automatic)
                    {
                        Invoke("EvaluateAutoCollapse", outside, now + 1_000_000);
                        Require(!Field<bool>(window, "collapsed") && !Field<DispatcherTimer>(window, "leaveTimer").IsEnabled,
                            "disabled auto-collapse still ran its timer");
                    }
                }
                settings.AutoCollapse = false; settings.AutoCollapseDelaySeconds = 2; Invoke("UpdateView");
            });
            await Check("floating-tab toggle persists and close routes to tab or tray without exiting", async () =>
            {
                settings.ShowFloatingTab = true; settings.AutoCollapse = false;
                Invoke("ExpandPanel", true); Invoke("UpdateView"); await Layout();
                var floating = Named<ToggleButton>("FloatButton"); var toggle = ToggleProvider(floating);
                var indicator = Named<System.Windows.Shapes.Rectangle>("FloatIndicator");
                Equal("显示浮窗", AutomationProperties.GetName(floating));
                Equal(true, floating.IsChecked == true); Equal(ToggleState.On, toggle.ToggleState);
                Require(indicator.IsVisible && Named<VectorPath>("FloatGlyph").Data is not null, "enabled floating-tab artwork is missing");
                var enabledContent = floating.Content; var geometry = ButtonVectorGeometry(floating);
                var before = Desktop.Bounds(window);
                floating.Focus(); toggle.Toggle(); await Layout();
                EqualBounds(before, Desktop.Bounds(window));
                Equal(false, floating.IsChecked == true); Equal(ToggleState.Off, toggle.ToggleState);
                Require(!settings.ShowFloatingTab && !new SettingsStore(artifactDirectory).Load().ShowFloatingTab, "floating-tab disable was not saved");
                Require(!indicator.IsVisible && floating.IsKeyboardFocused, "focused disabled floating-tab toggle still looks selected");
                Require((floating.ToolTip as string)?.Contains("已关闭") == true, "disabled floating-tab tooltip is unclear");
                Equal("显示浮窗", AutomationProperties.GetName(floating));
                settings.AutoCollapse = true; Invoke("UpdateView");
                Require(!Field<DispatcherTimer>(window, "leaveTimer").IsEnabled, "floating-tab disable left auto-collapse running");
                var outside = OutsidePoint(); var now = Environment.TickCount64;
                Invoke("EvaluateAutoCollapse", outside, now); Invoke("EvaluateAutoCollapse", outside, now + 1_000_000);
                Require(window.IsVisible && !Field<bool>(window, "collapsed"), "disabled floating tab allowed timed collapse");
                RefreshTrayMenuAvailability();
                var trayFold = TrayItem("收起到屏幕边缘");
                Equal(false, (bool)trayFold.GetType().GetProperty("Enabled")!.GetValue(trayFold)!);
                Capture(window, "14-floating-tab-disabled.png");
                Named<Button>("CloseButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                Require(!window.IsVisible && TrayIsVisible(), "close with floating tab disabled did not leave only the tray");
                InvokeTrayItem("显示设备面板"); await Until(() => window.IsVisible && !Field<bool>(window, "collapsed"));
                Require(!settings.ShowFloatingTab && toggle.ToggleState == ToggleState.Off, "tray reopening changed the floating-tab preference");
                window.Close(); await Layout();
                Require(!window.IsVisible && TrayIsVisible(), "OS close with floating tab disabled exited or left a panel");
                InvokeTrayItem("显示设备面板"); await Until(() => window.IsVisible && !Field<bool>(window, "collapsed"));
                toggle.Toggle(); await Layout();
                Require(settings.ShowFloatingTab && new SettingsStore(artifactDirectory).Load().ShowFloatingTab, "floating-tab enable was not saved");
                Equal(ToggleState.On, toggle.ToggleState); Require(indicator.IsVisible, "enabled indicator did not return");
                Require(ReferenceEquals(enabledContent, floating.Content), "floating-tab toggle replaced its vector artwork");
                Equal(geometry, ButtonVectorGeometry(floating));
                Equal(BrushColor((Brush)window.FindResource("AccentTint")), BrushColor(floating.Background));
                Equal(BrushColor((Brush)window.FindResource("Accent")), BrushColor(floating.BorderBrush));
                Capture(window, "13-floating-tab-enabled.png");
                Named<Button>("CloseButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                Require(window.IsVisible && Field<bool>(window, "collapsed"), "close with floating tab enabled did not show a compact tab");
                Within(Desktop.Bounds(window), Desktop.WorkArea(window));
                InvokeTrayItem("显示设备面板"); await Until(() => window.IsVisible && !Field<bool>(window, "collapsed"));
                window.Close(); await Layout();
                Require(window.IsVisible && Field<bool>(window, "collapsed"), "OS close did not use the compact-tab behavior");
                InvokeTrayItem("显示设备面板"); await Until(() => window.IsVisible && !Field<bool>(window, "collapsed"));
                settings.AutoCollapse = false; settings.AutoCollapseDelaySeconds = 2; Invoke("UpdateView");
            });
            await Check("centered and tangential edge drags preserve the card and suspend the leave timer", async () =>
            {
                Invoke("ExpandPanel", false); await Layout();
                var bounds = Desktop.Bounds(window); var work = Desktop.WorkArea(window);
                Desktop.Place(window, work.Left + (work.Width - bounds.Width) / 2, work.Top + (work.Height - bounds.Height) / 2);
                await Layout(); Invoke("FinishTitleDrag", bounds); await Layout();
                Require(!Field<bool>(window, "collapsed"), "centered drag release collapsed card");
                Equal(DockSide.None, settings.Side); Within(Desktop.Bounds(window), Desktop.WorkArea(window));
                RaiseMouseEvent(window, Mouse.MouseLeaveEvent); RaiseDeactivated(); await Task.Delay(750);
                Require(!Field<bool>(window, "collapsed"), "centered card collapsed before its leave grace");
                bounds = Desktop.Bounds(window);
                Desktop.Place(window, work.Left + (work.Width - bounds.Width) / 2, work.Top); await Layout();
                var edgeStart = Desktop.Bounds(window);
                var shift = (int)Math.Round(30 * Desktop.Scale(window));
                Desktop.Place(window, edgeStart.Left + shift, edgeStart.Top); await Layout();
                typeof(MainWindow).GetField("titleDragging", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                try
                {
                    var now = Environment.TickCount64; var outside = OutsidePoint();
                    Invoke("EvaluateAutoCollapse", outside, now); Invoke("EvaluateAutoCollapse", outside, now + 10_000);
                    Require(!Field<bool>(window, "collapsed"), "active title drag allowed auto collapse");
                }
                finally { typeof(MainWindow).GetField("titleDragging", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false); }
                Invoke("FinishTitleDrag", edgeStart); await Layout();
                Require(!Field<bool>(window, "collapsed"), "moving along an existing edge collapsed the card");
                Equal(edgeStart.Top, Desktop.Bounds(window).Top);
                await VerifyActualLeaveTimerTick();
            });
            await Check("edge device opens the compact main action surface for the exact device", async () =>
            {
                settings.AutoCollapse = false;
                Invoke("OpenDevice", model.Devices.Single(row => row.Serial == "UI-C"));
                await ReleaseCardAtEdge(DockSide.Right);
                Button EdgeButton(string serial) => Named<StackPanel>("EdgeDevices").Children.OfType<Button>()
                    .Single(item => item.DataContext is DeviceRow row && row.Serial == serial);
                var button = EdgeButton("UI-D");
                RaiseMouseEvent(button, Mouse.MouseEnterEvent); await Task.Delay(250);
                Require(Field<bool>(window, "collapsed"), "device hover opened the main panel");
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                Require(!Field<bool>(window, "collapsed") && Named<StackPanel>("DetailPanel").IsVisible, "edge click did not open the action panel");
                Require(!window.OwnedWindows.Cast<Window>().Any(item => item.IsVisible), "edge click created a second surface");
                Equal("UI-D", model.Selected?.Serial);
                Equal("UI-D", Named<ComboBox>("DeviceSwitcher").SelectedValue as string);
                Within(Desktop.Bounds(window), Desktop.WorkArea(window));
                var heading = Named<TextBlock>("DeviceActionsHeading");
                var picker = Named<ComboBox>("DeviceSwitcher");
                Require(ReferenceEquals(heading.Parent, picker.Parent), "device picker is not on the action heading row");
                var actions = Named<UniformGrid>("Actions").Children.OfType<Button>().ToArray();
                Require(actions.Length == 6 && actions.All(action => action.ActualHeight is >= 44 and <= 62),
                    "compact action panel does not contain six smaller buttons");
                Require(Named<ScrollViewer>("ContentScroll").ScrollableHeight < 1, "the six compact actions do not fit the panel");
                Capture(window, "15-edge-selected-device.png");
                await InspectDialog(() => ClickAction("info"), async dialog =>
                {
                    Require(ReferenceEquals(dialog.Owner, window), "device action dialog has the wrong owner");
                    Require(Output(dialog).Text.Contains("UI Fixture Phone UI-D", StringComparison.Ordinal), "edge selection sent the wrong serial to ADB");
                    await Task.CompletedTask;
                });

                Invoke("CollapsePanel"); await Layout();
                await InspectDialog(() => EdgeButton("UI-A").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), async dialog =>
                {
                    Require(Output(dialog).Text.Contains("UI-A"), "unavailable device guidance has the wrong target");
                    Require(model.Selected is null && Named<StackPanel>("ListPanel").IsVisible, "unavailable icon selected live actions");
                    await Task.CompletedTask;
                });
                Invoke("CollapsePanel"); await Layout();
                Named<Button>("EdgeArrow").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                Require(!Field<bool>(window, "collapsed") && model.Selected is null && Named<StackPanel>("ListPanel").IsVisible,
                    "the general Expand button did not open all devices");
            });
            await Check("device remarks persist by serial and label the edge device", async () =>
            {
                async Task SaveRemark(string text, Action? open = null)
                {
                    await InspectDialog(open ?? (() => Named<Button>("AliasButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))), async dialog =>
                    {
                        var input = Descendants<TextBox>(dialog).Single(control => AutomationProperties.GetAutomationId(control) == "DeviceAliasInput");
                        var save = Descendants<Button>(dialog).Single(control => AutomationProperties.GetAutomationId(control) == "SaveDeviceAliasButton");
                        input.Text = text;
                        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Task.CompletedTask;
                    });
                    await Layout();
                }

                Invoke("OpenDevice", model.Devices.Single(row => row.Serial == "UI-C"));
                await SaveRemark(" 测试机 ");
                Equal("测试机", model.Selected?.Name);
                var picker = Named<ComboBox>("DeviceSwitcher");
                Equal("测试机", PickerOptions(picker).Single(option => option.Serial == "UI-C").Name);
                Equal("测试机", new SettingsStore(artifactDirectory).Load().DeviceAliases["UI-C"]);
                Invoke("CollapsePanel"); await Layout();
                var edge = Named<StackPanel>("EdgeDevices").Children.OfType<Button>()
                    .Single(item => item.DataContext is DeviceRow { Serial: "UI-C" });
                Equal("测", Descendants<TextBlock>(edge).Single(label => AutomationProperties.GetAutomationId(label) == "EdgeDeviceLabel").Text);
                edge.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Layout();
                Equal("UI-C", model.Selected?.Serial);
                await SaveRemark(" ");
                Equal("Fixture One", model.Selected?.Name);
                Require(!new SettingsStore(artifactDirectory).Load().DeviceAliases.ContainsKey("UI-C"), "cleared remark remained on disk");
                Invoke("BackClick", window, new RoutedEventArgs()); await Layout();

                var unavailable = Descendants<Button>(Named<ItemsControl>("DeviceList")).Single(item =>
                    item.DataContext is DeviceRow { Serial: "UI-A" } && AutomationProperties.GetAutomationId(item) == "DeviceRowButton");
                var menu = unavailable.ContextMenu;
                menu.PlacementTarget = unavailable;
                try
                {
                    menu.IsOpen = true;
                    await Layout();
                    var edit = menu.Items.OfType<MenuItem>().Single();
                    Require(edit.CommandParameter is DeviceRow { Serial: "UI-A" }, "row context menu targeted the wrong device");
                    await SaveRemark("待授权机", () => edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                }
                finally { menu.IsOpen = false; }
                Equal("待授权机", model.Devices.Single(row => row.Serial == "UI-A").Name);
                Equal("待授权机", new SettingsStore(artifactDirectory).Load().DeviceAliases["UI-A"]);
            });
            await Check("context menu suspends auto collapse and restores the full leave grace", async () =>
            {
                settings.AutoCollapse = true; settings.AutoCollapseDelaySeconds = 2;
                settings.ShowFloatingTab = true; settings.Side = DockSide.Right; Invoke("UpdateView");
                var menu = Named<Border>("Frame").ContextMenu;
                var beforeMenu = Environment.TickCount64; var outside = OutsidePoint(); var current = Desktop.Bounds(window);
                Invoke("EvaluateAutoCollapse", new NativePoint { X = current.Left + current.Width / 2, Y = current.Top + current.Height / 2 }, beforeMenu);
                Invoke("EvaluateAutoCollapse", outside, beforeMenu - 5000);
                menu.PlacementTarget = Named<Border>("Frame"); menu.IsOpen = true;
                await Layout();
                Require(Field<int>(window, "interactionDepth") > 0, "context menu did not pause interaction");
                Invoke("EvaluateAutoCollapse", outside, beforeMenu + 10_000);
                Require(!Field<bool>(window, "collapsed"), "an open menu allowed timed collapse");
                Invoke("CollapsePanel"); Require(!Field<bool>(window, "collapsed"), "menu allowed a collapse");
                var closedAt = Environment.TickCount64;
                menu.IsOpen = false; await Until(() => Field<int>(window, "interactionDepth") == 0); await Layout();
                Equal(0, Field<int>(window, "interactionDepth"));
                AssertFullLeaveGraceAfterGuard(closedAt);
                Invoke("ExpandPanel", true); await Layout();
            });
            await Check("modal work-area limits, initial focus and focus restoration", async () =>
            {
                var trigger = Named<Button>("CloseButton"); window.Activate(); trigger.Focus();
                Invoke("SyncStartupState");
                var actualAutoStart = settings.AutoStart;
                settings.AutoStart = !actualAutoStart;
                var closedAt = 0L;
                await InspectDialog(() => Invoke("OpenSettings", window, new RoutedEventArgs()), async dialog =>
                {
                    await Layout(); Within(Desktop.Bounds(dialog), Desktop.WorkArea(dialog));
                    Require(dialog.IsKeyboardFocusWithin, "focus did not enter modal");
                    Require(dialog.MaxHeight <= Desktop.WorkArea(dialog).Height / Desktop.Scale(dialog), "dialog is taller than work area");
                    var startup = Descendants<CheckBox>(dialog).Single(box => AutomationProperties.GetAutomationId(box) == "StartupCheckBox");
                    Equal(actualAutoStart, startup.IsChecked == true);
                    Equal("登录 Windows 后自动启动", startup.Content as string);
                    var collapseOptions = AutoCollapseControls(dialog);
                    Equal(settings.AutoCollapse, collapseOptions.Enabled.IsChecked == true);
                    Equal(settings.AutoCollapseDelaySeconds.ToString(CultureInfo.InvariantCulture), collapseOptions.Delay.Text);
                    var versionInfo = Descendants<TextBlock>(dialog).Single(block => AutomationProperties.GetAutomationId(block) == "AppVersionInfo");
                    var version = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                        ?? typeof(SettingsWindow).Assembly.GetName().Version!.ToString(3);
                    Equal("ADB Dock · v" + version + (version.Contains('-') ? " · 测试版" : " · 正式版"), versionInfo.Text);
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
                    var now = Environment.TickCount64; var outside = OutsidePoint();
                    Invoke("EvaluateAutoCollapse", outside, now); Invoke("EvaluateAutoCollapse", outside, now + 10_000);
                    Require(!Field<bool>(window, "collapsed"), "modal settings allowed timed collapse");
                    Capture(dialog, "04-settings.png");
                    closedAt = Environment.TickCount64;
                });
                await Layout(); Equal(trigger, Keyboard.FocusedElement);
                AssertFullLeaveGraceAfterGuard(closedAt);
                Invoke("ExpandPanel", false); await Layout();
                var service = Field<object>(window, "adb");
                await InspectDialog(() => Invoke("OpenSettings", window, new RoutedEventArgs()), async dialog =>
                {
                    var controls = AutoCollapseControls(dialog);
                    Require(controls.Enabled.IsChecked == true && controls.Delay.IsEnabled, "enabled auto-collapse delay cannot be edited");
                    foreach (var invalid in new[] { "0", "301", "1000" })
                    {
                        controls.Delay.Text = invalid;
                        controls.Save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Require(dialog.IsVisible, "out-of-range auto-collapse delay was accepted: " + invalid);
                    }
                    controls.Delay.Text = "not-a-number";
                    ToggleProvider(controls.Enabled).Toggle();
                    Require(!controls.Delay.IsEnabled, "disabled auto-collapse still permits editing its delay");
                    controls.Save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Task.CompletedTask;
                });
                Require(!settings.AutoCollapse && settings.AutoCollapseDelaySeconds == 2, "disabling after an invalid edit did not retain the previous valid delay");
                var saved = new SettingsStore(artifactDirectory).Load();
                Require(!saved.AutoCollapse && saved.AutoCollapseDelaySeconds == 2, "disabled auto-collapse was not saved");
                Require(!Field<DispatcherTimer>(window, "leaveTimer").IsEnabled && ReferenceEquals(service, Field<object>(window, "adb")), "settings-only save restarted devices or left the timer enabled");
                await InspectDialog(() => Invoke("OpenSettings", window, new RoutedEventArgs()), async dialog =>
                {
                    var controls = AutoCollapseControls(dialog);
                    Require(controls.Enabled.IsChecked != true && !controls.Delay.IsEnabled, "saved disabled preference was not restored");
                    Equal("2", controls.Delay.Text);
                    ToggleProvider(controls.Enabled).Toggle(); controls.Delay.Text = "3";
                    ToggleProvider(controls.Enabled).Toggle();
                    controls.Save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.CompletedTask;
                });
                saved = new SettingsStore(artifactDirectory).Load();
                Require(!saved.AutoCollapse && saved.AutoCollapseDelaySeconds == 3, "valid edited delay was not saved while auto-collapse was disabled");
                await InspectDialog(() => Invoke("OpenSettings", window, new RoutedEventArgs()), async dialog =>
                {
                    var controls = AutoCollapseControls(dialog); Equal("3", controls.Delay.Text);
                    ToggleProvider(controls.Enabled).Toggle(); controls.Delay.Text = "2";
                    controls.Save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.CompletedTask;
                });
                saved = new SettingsStore(artifactDirectory).Load();
                Require(saved.AutoCollapse && saved.AutoCollapseDelaySeconds == 2, "auto-collapse enable and delay were not restored through settings controls");
                Require(Field<DispatcherTimer>(window, "leaveTimer").IsEnabled && ReferenceEquals(service, Field<object>(window, "adb")), "saved auto-collapse preference did not take effect immediately");
            });
            await Check("overflow counts, stable edge order and hidden-device aggregate", async () =>
            {
                await SetSnapshot(SixDevices(), () => model.Devices.Count == 6 && model.OnlineCount == "6");
                settings.Side = DockSide.Right; Invoke("CollapsePanel"); await Layout();
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
                await Layout(); Equal("UI-1", Named<ComboBox>("DeviceSwitcher").SelectedValue as string);
                await SetSnapshot(SixDevices(firstOffline: true), () => model.Devices.Single(row => row.Serial == "UI-1").Device.State == "offline");
                Require(model.Selected is null, "disconnected selection survived");
                Require(!Named<UniformGrid>("Actions").IsEnabled, "unavailable target retained actions");
                var picker = Named<ComboBox>("DeviceSwitcher");
                Require(picker.SelectedValue is null && PickerOptions(picker).All(option => option.Serial != "UI-1"), "full picker retained its disconnected target");
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
            await Check("an active installation and mirror survive discovery failures until a confirmed disconnect", DiscoveryDuringOperationsAsync);
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
            await Check("full Logcat view virtualizes rolling logs and preserves paused history until catch-up", async () =>
            {
                var log = new LogWindow("LOG-LIMIT-FIXTURE", Field<AdbOperations>(window, "operations")) { Owner = window };
                try
                {
                    log.Show();
                    await Layout();
                    const int capacity = LogFilterBuffer.MaximumLines, batch = 500, batches = 6;
                    var lines = Enumerable.Range(0, capacity + batch * batches)
                        .Select(i => i + " 日志 " + new string('x', 770 + i % 41)).ToArray();
                    lines[4] = lines[15000] = lines[15002] = "repeated log";
                    lines[15001] = "selected middle";
                    log.AppendLines(lines.Take(capacity));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var output = LogList(log);
                    var selectedDuplicate = output.Items[15002];
                    output.SelectedItems.Add(selectedDuplicate);
                    output.SelectedItems.Add(output.Items[15001]);
                    output.SelectedItems.Add(output.Items[4]);
                    Equal(string.Join(Environment.NewLine, new[] { "repeated log", "selected middle", "repeated log" }), log.GetSelectedText());
                    var host = Descendants<VirtualizingStackPanel>(log).Single();
                    var scroll = Descendants<ScrollViewer>(output).Single();
                    object? reading = null;
                    var rollingTime = Stopwatch.StartNew();
                    for (var i = 0; i < batches; i++)
                    {
                        if (i == batches / 2)
                        {
                            scroll.ScrollToVerticalOffset(7000);
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Equal(7000d, host.VerticalOffset);
                            reading = output.Items[(int)host.VerticalOffset];
                        }
                        int offset = i * batch;
                        var chunk = lines.AsSpan(capacity + offset, batch).ToArray();
                        log.AppendLines(chunk);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Equal(capacity, output.Items.Count);
                        Equal(lines[offset + batch], output.Items[0].ToString());
                        Equal(chunk[^1], output.Items[capacity - 1].ToString());
                        if (reading is null)
                            Require(host.VerticalOffset + host.ViewportHeight >= host.ExtentHeight - 1, "Logcat stopped following the newest batch");
                        else
                            Require(ReferenceEquals(reading, output.Items[(int)host.VerticalOffset]), "rolling updates moved the historical line being read");
                        Require(host.Children.Count is > 0 and < 200, "Logcat laid out more than its viewport and prefetch rows");
                        Require(output.ItemContainerGenerator.ContainerFromIndex(capacity / 2) is null, "offscreen log rows were materialized");
                    }
                    Console.WriteLine($"INFO Logcat applied {batches} batches of {batch} to the full view in {rollingTime.ElapsedMilliseconds} ms");
                    Require(output.SelectedItems.Contains(selectedDuplicate) && output.SelectedItems.Count == 2, "selection lost row identity during eviction");
                    Equal("selected middle" + Environment.NewLine + "repeated log", log.GetSelectedText());
                    Equal(string.Join(Environment.NewLine, lines.Skip(batch * batches)) + Environment.NewLine, log.GetOutputText());

                    var pause = Descendants<Button>(log).Single(button => AutomationProperties.GetAutomationId(button) == "LogPauseButton");
                    var retained = Field<LogFilterBuffer>(log, "buffer");
                    var pausedOffset = host.VerticalOffset;
                    var firstVisible = output.Items[0];
                    var lastVisible = output.Items[capacity - 1];
                    pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    const int overflow = 125;
                    log.AppendLines(Enumerable.Range(0, capacity + overflow).Select(i => "paused log " + i));
                    await Until(() => Equals(pause.Content, $"回到最新（新增 {capacity + overflow:N0} 行）"));
                    Equal(capacity, retained.TotalCount);
                    Equal(capacity, output.Items.Count);
                    Equal(pausedOffset, host.VerticalOffset);
                    Require(ReferenceEquals(firstVisible, output.Items[0]) && ReferenceEquals(lastVisible, output.Items[capacity - 1]),
                        "paused view changed after the raw cache rolled over");
                    Require(output.SelectedItems.Contains(selectedDuplicate), "paused view lost its selection");

                    pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Equal(capacity, output.Items.Count);
                    Equal("paused log " + overflow, output.Items[0].ToString());
                    Equal("paused log " + (capacity + overflow - 1), output.Items[capacity - 1].ToString());
                    Require(host.VerticalOffset + host.ViewportHeight >= host.ExtentHeight - 1, "resuming from paused history did not jump to the latest line");
                    Require(host.Children.Count is > 0 and < 200, "catch-up materialized offscreen log rows");
                }
                finally { log.Close(); }
            });
            await Check("Logcat opens selectable original text by double-click and Enter", async () =>
            {
                var log = new LogWindow("LOG-DETAIL-FIXTURE", Field<AdbOperations>(window, "operations")) { Owner = window };
                try
                {
                    log.Show();
                    var rows = new[] { "I/example: id=4102", "W/example: id=5203" };
                    log.AppendLines(rows);
                    await Layout();
                    var list = LogList(log);
                    var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.Items[1]);
                    await InspectDialog(() => item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent
                    }), dialog => InspectText(dialog, rows[1], "5203"));
                    list.SelectedItems.Add(list.Items[0]);
                    if (!list.SelectedItems.Contains(list.Items[1])) list.SelectedItems.Add(list.Items[1]);
                    await InspectDialog(() => list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list)!, Environment.TickCount, Key.Enter)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    }), dialog => InspectText(dialog, string.Join(Environment.NewLine, rows), "4102"));

                    Task InspectText(Window dialog, string expected, string fragment)
                    {
                        Require(ReferenceEquals(log, dialog.Owner), "log detail has the wrong owner");
                        var text = Output(dialog);
                        Require(text.IsReadOnly, "log detail is editable");
                        Equal(expected, text.Text);
                        text.Select(text.Text.IndexOf(fragment, StringComparison.Ordinal), fragment.Length);
                        Equal(fragment, text.SelectedText);
                        Require(ApplicationCommands.Copy.CanExecute(null, text), "cannot copy the selected log fragment");
                        return Task.CompletedTask;
                    }
                }
                finally { log.Close(); }
            });
            await Check("Logcat regex UI filters live output, clears errors, pauses and stops its client", async () =>
            {
                Task? reader = null;
                await InspectDialog(() => ClickAction("logcat"), async dialog =>
                {
                    if (dialog is not LogWindow log) throw new InvalidOperationException("incorrect Logcat dialog");
                    await Until(() => VisibleLogText(dialog).Contains("UI-2 test log"));
                    reader = Field<Task>(dialog, "reader");
                    var pause = Descendants<Button>(dialog).Single(button => AutomationProperties.GetAutomationId(button) == "LogPauseButton");
                    var filter = Descendants<TextBox>(dialog).Single(box => AutomationProperties.GetAutomationId(box) == "LogFilterInput");
                    var clear = Descendants<Button>(dialog).Single(button => AutomationProperties.GetAutomationId(button) == "ClearLogFilterButton");
                    var status = Descendants<TextBlock>(dialog).Single(block => AutomationProperties.GetAutomationId(block) == "LogFilterStatus");
                    filter.Text = @"test log \d*[02468]$";
                    await Until(() => HasOnlyEvenLogLines(VisibleLogText(dialog)));
                    var matched = VisibleLogText(dialog);
                    await Until(() => VisibleLogText(dialog) != matched && HasOnlyEvenLogLines(VisibleLogText(dialog)));
                    Capture(dialog, "11-logcat-regex-filter.png");

                    var buffer = Field<LogFilterBuffer>(dialog, "buffer");
                    pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    const string emptyPause = "回到最新（新增 0 行）";
                    Equal(emptyPause, pause.Content);
                    Require(!filter.IsEnabled && !clear.IsEnabled, "filter controls can change the paused view");
                    var before = log.GetOutputText();
                    var rawAtPause = buffer.TotalCount;
                    await Until(() => buffer.TotalCount >= rawAtPause + 4 && !Equals(emptyPause, pause.Content));
                    Equal(before, log.GetOutputText());

                    var rawBeforeResume = buffer.TotalCount;
                    pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Equal("暂停显示", pause.Content);
                    Require(filter.IsEnabled && clear.IsEnabled, "filter controls stayed disabled after catch-up");
                    var resumed = VisibleLogText(dialog);
                    Require(HasOnlyEvenLogLines(resumed), "catch-up did not respect the active regex");
                    var pausedMatchingLine = (rawBeforeResume - 1) / 2 * 2;
                    Require(LogLines(resumed).Any(line => line.EndsWith("test log " + pausedMatchingLine, StringComparison.Ordinal)),
                        "catch-up lost a matching line received while paused");

                    filter.Text = "^WILL_NOT_MATCH$";
                    await Until(() => VisibleLogText(dialog).Length == 0);
                    await Task.Delay(400); Equal("", VisibleLogText(dialog));
                    filter.Text = @"test log \d*[02468]$";
                    await Until(() => HasOnlyEvenLogLines(VisibleLogText(dialog)));
                    filter.Text = "(";
                    await Until(() => HasFilterError(status.Text));
                    Require(VisibleLogText(dialog).Contains("UI-2 test log"), "invalid regex erased last valid output");
                    Require(reader?.IsFaulted == false, "invalid regex terminated the log reader");
                    Capture(dialog, "12-logcat-invalid-regex.png");
                    clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => filter.Text.Length == 0 && !HasFilterError(status.Text));
                    Require(LogLines(VisibleLogText(dialog)).Any(line => "13579".Contains(line[^1])), "clearing regex did not reveal retained odd-numbered logs");
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
            try { RequestInstallerExitWithSettingsOpen(); }
            catch (Exception exception)
            {
                failures.Add("Installer exit request failed: " + exception);
                await window.ExitAsync();
            }
        }
    }

    private void RequestInstallerExitWithSettingsOpen()
    {
        using var request = EventWaitHandle.OpenExisting($@"Local\AdbDock.Exit.UiChecks.{Environment.ProcessId}.{Environment.ProcessId}");
        settings.AutoCollapseDelaySeconds = ExitCheckDelay;
        var dialog = new SettingsWindow(settings, artifactDirectory);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timeout.Tick += async (_, _) =>
        {
            timeout.Stop();
            failures.Add("Timed out waiting for installer-requested shutdown with the settings window open.");
            await window.ExitAsync();
        };
        dialog.Loaded += (_, _) =>
        {
            timeout.Start();
            exitRequestSent = request.Set();
        };
        dialog.Closed += (_, _) => { timeout.Stop(); exitDialogClosed = true; };
        Invoke("ShowOwned", dialog);
    }

    private void OnExit(object? sender, ExitEventArgs e)
    {
        if (!runFinished) failures.Add("Application exited before native checks finished; another instance may own this desktop session.");
        if (exitRequestSent && exitDialogClosed && listener?.IsCompleted == true &&
            new SettingsStore(artifactDirectory).Load().AutoCollapseDelaySeconds == ExitCheckDelay)
        {
            passed++;
            Console.WriteLine("PASS installer exit event closes modal settings, saves preferences and drains the listener");
        }
        else failures.Add("Installer-requested exit did not close the settings dialog, persist preferences and stop the listener.");
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
            settings.AutoCollapse = false; settings.ShowFloatingTab = true; settings.AutoCollapseDelaySeconds = 2;
            if (!window.IsVisible) window.Show();
            Invoke("ExpandPanel", false);
        }
    }

    private async Task InspectDialog(Action trigger, Func<Window, Task> inspect)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = application.Windows.Cast<Window>().ToHashSet();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += async (_, _) =>
        {
            var dialog = application.Windows.Cast<Window>().FirstOrDefault(candidate => candidate.IsVisible && !existing.Contains(candidate));
            if (dialog is null)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(12)) { timer.Stop(); completion.TrySetException(new TimeoutException("Expected modal did not appear.")); }
                return;
            }
            timer.Stop();
            try { await inspect(dialog); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { if (dialog.IsVisible) dialog.Close(); }
        };
        timer.Start();
        try { trigger(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { timer.Stop(); }
    }

    private async Task DiscoveryDuringOperationsAsync()
    {
        const string serial = "UI-C";
        var row = model.Devices.Single(device => device.Serial == serial);
        await (Task)Invoke("RunDeviceActionAsync", row, "mirror")!;
        using var mirror = Process.GetProcessById(int.Parse(File.ReadAllText(snapshotFile + ".mirror-pid")));
        var operations = Field<AdbOperations>(window, "operations");
        using var cleanup = new CancellationTokenSource();
        var apk = snapshotFile + ".apk";
        var started = apk + ".started";
        var restart = snapshotFile + ".restart-tracker";
        try
        {
            File.Create(apk).Dispose();
            var install = operations.InstallAsync(serial, apk, cleanup.Token);
            await Until(() => File.Exists(started));
            File.Create(restart).Dispose();
            await Until(() => model.ListenerWarning is not null);
            Require(model.AdbAvailable && model.Caption.Contains("监听恢复中") && model.Devices.Any(d => d.Serial == serial && d.IsOnline),
                "tracker failure hid the online device or recovery indicator");
            File.Delete(restart);
            await Until(() => model.ListenerWarning is null);
            Require(!install.IsCompleted && !mirror.HasExited, "tracker restart stopped an online device's clients");

            await SetSnapshot("ERROR:temporary devices -l failure", () => !model.AdbAvailable);
            await SetSnapshot(Initial, () => model.AdbAvailable && model.Devices.Any(d => d.Serial == serial && d.IsOnline));
            File.Delete(apk);
            await install.WaitAsync(TimeSpan.FromSeconds(10));
            Require(!mirror.HasExited, "failed list query stopped the existing mirror process");

            File.Delete(started);
            File.Create(apk).Dispose();
            var nextInstall = operations.InstallAsync(serial, apk, cleanup.Token);
            await Until(() => File.Exists(started));
            await SetSnapshot(Initial.Replace("UI-C device", "UI-C offline"), () => model.Devices.Any(d => d.Serial == serial && !d.IsOnline));
            await Until(() => nextInstall.IsCompleted && mirror.HasExited);
            Require(nextInstall.IsCanceled, "confirmed offline device did not cancel its installation");
        }
        finally
        {
            cleanup.Cancel();
            File.Delete(apk);
            File.Delete(restart);
            await SetSnapshot(Initial, () => model.AdbAvailable && model.Devices.Any(d => d.Serial == serial && d.IsOnline));
        }
    }

    private async Task SetSnapshot(string text, Func<bool> predicate)
    {
        var temporary = snapshotFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            var watch = Stopwatch.StartNew();
            while (true)
            {
                try { File.Move(temporary, snapshotFile, true); break; }
                catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && watch.Elapsed < TimeSpan.FromSeconds(3))
                {
                    // Shared readers permit replacement; Windows can still briefly hold a
                    // rename/delete handle. Keep the previous complete snapshot until retry.
                    await Task.Delay(25);
                }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
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
        Desktop.Place(window, work.Left + (work.Width - bounds.Width) / 2, work.Top + (work.Height - bounds.Height) / 2);
        await Layout();
        var startingBounds = Desktop.Bounds(window);
        var x = side == DockSide.Left ? work.Left : side == DockSide.Right ? work.Right - bounds.Width : work.Left + (work.Width - bounds.Width) / 2;
        var y = side == DockSide.Top ? work.Top : side == DockSide.Bottom ? work.Bottom - bounds.Height : work.Top + (work.Height - bounds.Height) / 2;
        Desktop.Place(window, x, y); await Layout();
        Invoke("FinishTitleDrag", startingBounds); await Layout();
    }
    private NativePoint OutsidePoint()
    {
        var work = Desktop.WorkArea(window);
        return new NativePoint { X = work.Right + 100, Y = work.Bottom + 100 };
    }
    private async Task VerifyActualLeaveTimerTick()
    {
        var originalWidth = window.Width; var originalHeight = window.Height;
        try
        {
            settings.ShowFloatingTab = true; settings.AutoCollapse = true; settings.AutoCollapseDelaySeconds = 1;
            Invoke("ExpandPanel", false); Invoke("UpdateView"); await Layout();
            var work = Desktop.WorkArea(window); var scale = Desktop.Scale(window);
            window.Width = Math.Min(window.Width, work.Width / (3 * scale));
            window.Height = Math.Min(window.Height, work.Height / (3 * scale));
            await Layout();
            var bounds = Desktop.Bounds(window); var cursor = Desktop.Cursor;
            var margin = (int)Math.Round(8 * scale);
            var candidates = new[]
            {
                new NativeRect(work.Left + margin, work.Top + margin, bounds.Width, bounds.Height),
                new NativeRect(work.Right - bounds.Width - margin, work.Top + margin, bounds.Width, bounds.Height),
                new NativeRect(work.Left + margin, work.Bottom - bounds.Height - margin, bounds.Width, bounds.Height),
                new NativeRect(work.Right - bounds.Width - margin, work.Bottom - bounds.Height - margin, bounds.Width, bounds.Height)
            };
            var away = candidates.First(candidate => !candidate.Contains(cursor));
            Desktop.Place(window, away.Left, away.Top); await Layout();
            Invoke("FinishTitleDrag", Desktop.Bounds(window)); Invoke("UpdateView");
            Require(!Field<bool>(window, "collapsed") && Field<DispatcherTimer>(window, "leaveTimer").IsEnabled,
                "real timer test started already collapsed or disabled");
            var watch = Stopwatch.StartNew();
            await Until(() => Field<bool>(window, "collapsed"));
            Require(watch.Elapsed >= TimeSpan.FromMilliseconds(900), "real timer collapsed before the configured one-second delay");
            Console.WriteLine($"INFO Real DispatcherTimer collapsed after {watch.Elapsed.TotalSeconds:0.00}s without moving the system cursor");
        }
        finally
        {
            settings.AutoCollapse = false; settings.AutoCollapseDelaySeconds = 2;
            Invoke("ExpandPanel", false);
            window.Width = originalWidth; window.Height = originalHeight;
            Invoke("UpdateView"); await Layout();
        }
    }
    private void AssertFullLeaveGraceAfterGuard(long closedAt)
    {
        Require(Field<DispatcherTimer>(window, "leaveTimer").IsEnabled, "interaction closure left auto collapse disabled");
        var since = Field<long?>(window, "outsideSince") ?? Environment.TickCount64;
        Require(since >= closedAt, "interaction closure retained an expired outside timer");
        var outside = OutsidePoint();
        var delay = settings.AutoCollapseDelaySeconds * 1000L;
        Invoke("EvaluateAutoCollapse", outside, since);
        Invoke("EvaluateAutoCollapse", outside, since + delay - 1);
        Require(!Field<bool>(window, "collapsed"), "interaction closure did not grant the full configured grace");
        Invoke("EvaluateAutoCollapse", outside, since + delay);
        Require(Field<bool>(window, "collapsed"), "auto collapse did not resume after interaction closure");
    }
    private static IToggleProvider ToggleProvider(ToggleButton control)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(control) ?? throw new InvalidOperationException("Control automation peer missing");
        return peer.GetPattern(PatternInterface.Toggle) as IToggleProvider ?? throw new InvalidOperationException("Control does not expose UIA TogglePattern");
    }
    private static (CheckBox Enabled, TextBox Delay, Button Save) AutoCollapseControls(Window dialog) => (
        Descendants<CheckBox>(dialog).Single(control => AutomationProperties.GetAutomationId(control) == "AutoCollapseCheckBox"),
        Descendants<TextBox>(dialog).Single(control => AutomationProperties.GetAutomationId(control) == "AutoCollapseDelayInput"),
        Descendants<Button>(dialog).Single(control => AutomationProperties.GetAutomationId(control) == "SaveSettingsButton"));
    private object TrayItem(string label)
    {
        var tray = Field<object>(window, "tray");
        var menu = tray.GetType().GetProperty("ContextMenuStrip")!.GetValue(tray)!;
        var items = (System.Collections.IEnumerable)menu.GetType().GetProperty("Items")!.GetValue(menu)!;
        return items.Cast<object>().Single(item => Equals(item.GetType().GetProperty("Text")?.GetValue(item), label));
    }
    private void RefreshTrayMenuAvailability()
    {
        var tray = Field<object>(window, "tray");
        var menu = tray.GetType().GetProperty("ContextMenuStrip")!.GetValue(tray)!;
        menu.GetType().GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(System.ComponentModel.CancelEventArgs) }, null)!.Invoke(menu,
            new object[] { new System.ComponentModel.CancelEventArgs(false) });
    }
    private void InvokeTrayItem(string label)
    {
        var item = TrayItem(label);
        item.GetType().GetMethod("PerformClick", Type.EmptyTypes)!.Invoke(item, null);
    }
    private bool TrayIsVisible()
    {
        var tray = Field<object>(window, "tray");
        return (bool)tray.GetType().GetProperty("Visible")!.GetValue(tray)!;
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
    private static DevicePicker.DeviceOption[] PickerOptions(ComboBox picker) => picker.Items.Cast<DevicePicker.DeviceOption>().ToArray();
    private static ListBox LogList(Window dialog) => Descendants<ListBox>(dialog).Single(list => AutomationProperties.GetAutomationId(list) == "LogLines");
    private static string VisibleLogText(Window dialog) => string.Join(Environment.NewLine, LogList(dialog).Items.Cast<object>());
    private static string[] LogLines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    private static bool HasOnlyEvenLogLines(string text) => LogLines(text) is { Length: > 0 } lines && lines.All(line => "02468".Contains(line[^1]));
    private static bool HasFilterError(string text) => text.Contains("无效") || text.Contains("错误") || text.Contains("失败");
    private static string ButtonVectorGeometry(ButtonBase button)
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
    private static void EqualBounds(NativeRect expected, NativeRect actual)
    {
        Equal(expected.Left, actual.Left); Equal(expected.Top, actual.Top);
        Equal(expected.Right, actual.Right); Equal(expected.Bottom, actual.Bottom);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected '{expected}', got '{actual}'.");
}
