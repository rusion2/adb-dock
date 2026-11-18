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
    private EventWaitHandle? exitEvent;
    private RegisteredWaitHandle? exitRegistration;
    private readonly string instanceIdentity;
    public App() : this(WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName) { }
    internal App(string instanceIdentity) => this.instanceIdentity = instanceIdentity;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\AdbDock.Instance." + instanceIdentity, out var first);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AdbDock.Show." + instanceIdentity);
        if (!first) { showEvent.Set(); Shutdown(); return; }
        exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\AdbDock.Exit.{instanceIdentity}.{Environment.ProcessId}");
        var window = new MainWindow();
        MainWindow = window;
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, false);
        exitRegistration = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => Dispatcher.BeginInvoke(async () => await window.ExitAsync()), null, Timeout.Infinite, true);
        DispatcherUnhandledException += (_, args) =>
        {
            window.ReportError(args.Exception);
            args.Handled = true;
        };
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        exitRegistration?.Unregister(null);
        exitEvent?.Dispose();
        showRegistration?.Unregister(null);
        showEvent?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
