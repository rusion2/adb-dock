using System;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace AdbDock.App;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showRegistration;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        instance = new Mutex(true, @"Local\AdbDock.Instance." + identity, out var first);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AdbDock.Show." + identity);
        if (!first) { showEvent.Set(); Shutdown(); return; }
        var window = new MainWindow();
        MainWindow = window;
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, false);
        DispatcherUnhandledException += (_, args) =>
        {
            window.ReportError(args.Exception);
            args.Handled = true;
        };
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        showRegistration?.Unregister(null);
        showEvent?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
