using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using AdbDock.Core;

internal static class PublishedSmoke
{
    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder title, int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("--smoke <published AdbDock.exe> <fixture exe> [output directory]"); return 2; }
        var executable = Path.GetFullPath(args[0]);
        var fixture = Path.GetFullPath(args[1]);
        var directory = Path.GetFullPath(args.Length > 2 ? args[2] : Path.Combine("artifacts", "release-smoke-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var snapshot = Path.Combine(directory, "devices.txt");
        File.WriteAllText(snapshot, "SMOKE-USB device usb:1-1 model:Fixture_Phone\nSMOKE-AUTH unauthorized usb:1-2\n");
        var settingsDirectory = Path.Combine(directory, "settings");
        new SettingsStore(settingsDirectory).Save(new AppSettings
        {
            AdbPath = fixture, ScrcpyPath = fixture, ScreenshotDirectory = directory,
            Topmost = false, AutoStart = false, AutoCollapse = false
        });
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true
        };
        start.Environment["ADBDOCK_HOME"] = settingsDirectory;
        start.Environment["ADBDOCK_UI_SNAPSHOT"] = snapshot;
        var clientDirectory = Path.Combine(directory, "fixture-clients");
        start.Environment["ADBDOCK_UI_CLIENTS"] = clientDirectory;
        start.Environment.Remove("ADB_DOCK_CHECKS_STATE");
        start.Environment.Remove("ADB_DOCK_OPERATIONS_CHECK_PIPE");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch published app.");
        var success = false;
        try
        {
            nint windowHandle = 0;
            var window = Until(() =>
            {
                process.Refresh();
                if (process.HasExited) throw new InvalidOperationException("Published app exited before displaying its window (or another instance already owns this session).");
                // ShowInTaskbar=false windows have a hidden WPF owner and are excluded by
                // Process.MainWindowHandle. Match only the visible window of our own PID.
                windowHandle = FindWindow(process.Id);
                return windowHandle == 0 ? null : AutomationElement.FromHandle(windowHandle);
            });
            Until(() => Find(window, "OnlineCount") is { Current.Name: "1" } ? window : null);
            Console.WriteLine("PASS Published folder launches and reads isolated mixed fixture devices");

            using (var second = Process.Start(start) ?? throw new InvalidOperationException("Could not launch second instance."))
            {
                if (!second.WaitForExit(5000))
                {
                    second.Kill(entireProcessTree: false);
                    throw new InvalidOperationException("Second instance did not exit.");
                }
                if (second.ExitCode != 0 || process.HasExited) throw new InvalidOperationException("Single-instance handoff failed.");
            }
            Console.WriteLine("PASS Published second instance exits and preserves first instance");

            // Supply a point inside the frame: keyboard context requests depend on foreground
            // focus, which Windows may refuse to grant to an automated background process.
            var bounds = window.Current.BoundingRectangle;
            var contextPoint = (nint)((((int)bounds.Top + 80) & 0xffff) << 16 | (((int)bounds.Left + 8) & 0xffff));
            if (SendMessageTimeout(windowHandle, 0x007B, windowHandle, contextPoint, 2, 2000, out _) == 0)
                throw new TimeoutException("Published app did not respond to its context-menu message.");
            var clientPoint = (nint)((80 << 16) | 8);
            foreach (var message in new uint[] { 0x0200, 0x0204, 0x0205 })
                if (!PostMessage(windowHandle, message, message == 0x0204 ? 2 : 0, clientPoint))
                    throw new TimeoutException("Published app did not respond to its directed mouse message.");
            Thread.Sleep(250);
            var closeButton = Find(window, "CloseButton");
            closeButton?.SetFocus();
            PostMessage(windowHandle, 0x0100, 0x5D, 0x015D0001);
            PostMessage(windowHandle, 0x0101, 0x5D, unchecked((nint)(int)0xC15D0001));
            var exit = Until(() =>
            {
                var roots = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                foreach (AutomationElement root in roots)
                    if (Find(root, "ExitMenuItem") is { Current.IsOffscreen: false } item) return item;
                return null;
            });
            ((InvokePattern)exit.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            if (!process.WaitForExit(10000)) throw new TimeoutException("Published app did not shut down through its Exit menu.");
            if (process.ExitCode != 0) throw new InvalidOperationException($"Published app exit code {process.ExitCode}.");
            var remaining = OwnedClients(clientDirectory, fixture);
            foreach (var client in remaining) client.Dispose();
            if (remaining.Count > 0) throw new InvalidOperationException("Published app left a fixture client running.");
            Console.WriteLine("PASS Published Exit menu shuts down owned sessions cleanly");
            success = true;
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL Published smoke: " + exception); return 1; }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(3000);
            }
            // If startup failed, allow any already-created fixture client to finish publishing
            // its identity marker, then stop only those exact clients. Never kill a process tree.
            if (!success)
            {
                var cleanup = Stopwatch.StartNew();
                while (cleanup.Elapsed < TimeSpan.FromSeconds(2))
                {
                    foreach (var client in OwnedClients(clientDirectory, fixture))
                    {
                        using (client)
                        {
                            try { client.Kill(entireProcessTree: false); client.WaitForExit(1000); }
                            catch (InvalidOperationException) { }
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            File.WriteAllText(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(new
            {
                executable, success, fixtureOnly = true, realDevicesTested = false, checkedAt = DateTimeOffset.Now
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static AutomationElement? Find(AutomationElement root, string id) => root.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static nint FindWindow(int processId)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != processId || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "ADB Dock") return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    private static List<Process> OwnedClients(string directory, string fixture)
    {
        var clients = new List<Process>();
        if (!Directory.Exists(directory)) return clients;
        foreach (var marker in Directory.EnumerateFiles(directory, "*.pid"))
        {
            Process? client = null;
            try
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(marker), out var id) ||
                    !long.TryParse(File.ReadAllText(marker), out var started)) continue;
                client = Process.GetProcessById(id);
                if (!client.HasExited && client.StartTime.ToUniversalTime().Ticks == started &&
                    string.Equals(client.MainModule?.FileName, fixture, StringComparison.OrdinalIgnoreCase))
                {
                    clients.Add(client);
                    client = null;
                }
            }
            catch (ArgumentException) { /* The short-lived fixture client already exited. */ }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (IOException) { }
            finally { client?.Dispose(); }
        }
        return clients;
    }

    private static T Until<T>(Func<T?> find) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            try { if (find() is { } found) return found; }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Published window/control did not become available.");
    }
}
