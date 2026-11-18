using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

// Stand-in for a running installation. It uses its own WPF window and tray icon,
// and never contacts adb or reads application settings.
internal static class RunningDockFixture
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 5) return 2;
        string mode = args[0], readyFile = args[3], exitFile = args[4];
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var panel = new Border { Background = Brushes.White };
        var window = new Window { Title = "ADB Dock", Width = 380, Height = 360, Content = panel,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        AutomationProperties.SetAutomationId(window, "MainWindow");
        Action<string> exit = reason =>
        {
            File.WriteAllText(exitFile, reason);
            app.Shutdown();
        };
        // Previous releases closed to the tab/tray on a regular window close.
        window.Closing += (sender, e) =>
        {
            if (File.Exists(exitFile)) return;
            e.Cancel = true;
            window.Hide();
        };
        if (mode == "legacy")
        {
            var item = new MenuItem { Header = "Exit ADB Dock" };
            AutomationProperties.SetAutomationId(item, "ExitMenuItem");
            item.Click += (sender, e) => exit("menu");
            panel.ContextMenu = new ContextMenu();
            panel.ContextMenu.Items.Add(item);
        }
        RegisteredWaitHandle registration = null;
        using (var identity = WindowsIdentity.GetCurrent())
        using (var process = Process.GetCurrentProcess())
        using (var mutex = new Mutex(false, args[1] + identity.User.Value))
        using (var tray = mode == "legacy" ? new Forms.NotifyIcon() : null)
        using (var menu = mode == "legacy" ? new Forms.ContextMenuStrip() : null)
        using (var signal = mode == "modern" ? new EventWaitHandle(false, EventResetMode.AutoReset,
            args[2] + identity.User.Value + "." + process.Id) : null)
        {
            if (tray != null)
            {
                tray.Icon = System.Drawing.SystemIcons.Application;
                tray.Text = "ADB Dock (installer test)";
                menu.Items.Add("\u9000\u51fa ADB Dock", null, (sender, e) => exit("menu"));
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += (sender, e) => app.Dispatcher.BeginInvoke(new Action(() => window.Show()));
                tray.Visible = true;
            }
            if (signal != null)
                registration = ThreadPool.RegisterWaitForSingleObject(signal,
                    (state, timeout) => app.Dispatcher.BeginInvoke(new Action(() => exit("event"))),
                    null, Timeout.Infinite, true);
            try
            {
                app.Startup += (sender, e) =>
                {
                    window.Show();
                    window.Hide();
                    app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => File.WriteAllText(readyFile, mode)));
                };
                return app.Run();
            }
            finally { if (registration != null) registration.Unregister(null); }
        }
    }
}
