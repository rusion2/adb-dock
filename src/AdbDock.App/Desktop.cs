using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AdbDock.Core;
using Microsoft.Win32;

namespace AdbDock.App;

[StructLayout(LayoutKind.Sequential)]
public struct NativePoint { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly bool Contains(NativePoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    public NativeRect(int x, int y, int width, int height) { Left = x; Top = y; Right = x + width; Bottom = y + height; }
}

public static class Desktop
{
    private const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StartupValue = "AdbDock";
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLong(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CommandLineToArgvW(string command, out int argumentCount);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    public static nint Handle(Window window) => new WindowInteropHelper(window).Handle;
    public static NativePoint Cursor { get { GetCursorPos(out var point); return point; } }
    public static NativeRect Bounds(Window window) { GetWindowRect(Handle(window), out var rect); return rect; }
    public static double Scale(Window window) => Math.Max(96, GetDpiForWindow(Handle(window))) / 96d;
    public static NativeRect WorkArea(Window window) => WorkAreaFor(MonitorFromWindow(Handle(window), 2));
    public static NativeRect WorkArea(NativePoint point) => WorkAreaFor(MonitorFromPoint(point, 2));
    private static NativeRect WorkAreaFor(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("无法读取显示器工作区。");
        return info.Work;
    }
    public static void ToolWindow(Window window)
    {
        var handle = Handle(window);
        var style = GetWindowLong(handle, -20).ToInt64();
        SetWindowLong(handle, -20, (nint)((style | 0x80) & ~0x40000));
    }
    public static void Place(Window window, int x, int y, int? width = null, int? height = null)
    {
        var rect = Bounds(window);
        SetWindowPos(Handle(window), 0, x, y, width ?? rect.Width, height ?? rect.Height, 0x0004 | 0x0010);
    }
    public static void Clamp(Window window)
    {
        var rect = Bounds(window); var work = WorkArea(window);
        Place(window, Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - rect.Width)), Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - rect.Height)));
    }
    public static void Foreground(Window window) { ShowWindow(Handle(window), 9); SetForegroundWindow(Handle(window)); }
    public static DockSide Nearest(NativePoint point, NativeRect work)
    {
        var distances = new[] { Math.Abs(point.X - work.Left), Math.Abs(point.X - work.Right), Math.Abs(point.Y - work.Top), Math.Abs(point.Y - work.Bottom) };
        var minimum = Array.IndexOf(distances, Math.Min(Math.Min(distances[0], distances[1]), Math.Min(distances[2], distances[3])));
        return new[] { DockSide.Left, DockSide.Right, DockSide.Top, DockSide.Bottom }[minimum];
    }
    public static bool IsStartupEnabled()
    {
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var run = currentUser.OpenSubKey(StartupRunKey);
        using var approval = currentUser.OpenSubKey(StartupApprovalKey);
        return IsStartupEnabled(run, approval, CurrentExecutable());
    }
    public static void ConfigureStartup(bool enabled)
    {
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var run = enabled ? currentUser.CreateSubKey(StartupRunKey, true) : currentUser.OpenSubKey(StartupRunKey, true);
        if (run is null) return;
        using var approval = enabled ? currentUser.OpenSubKey(StartupApprovalKey, true) : null;
        ConfigureStartup(enabled, run, approval, CurrentExecutable());
    }
    // Accept caller-owned keys so the registration logic can be verified in a temporary key.
    public static bool IsStartupEnabled(RegistryKey? run, RegistryKey? approval, string executablePath)
    {
        if (!StartupCommandTargetsExecutable(run?.GetValue(StartupValue) as string, executablePath)) return false;
        var state = approval?.GetValue(StartupValue);
        // Explorer uses a binary approval record. Honor its disabled or unknown states;
        // merely opening this application must never re-enable a Task Manager choice.
        return state is null || state is byte[] { Length: > 0 } bytes && bytes[0] is 0 or 2 or 6;
    }
    public static void ConfigureStartup(bool enabled, RegistryKey run, RegistryKey? approval, string executablePath)
    {
        if (enabled)
        {
            var command = '"' + Path.GetFullPath(executablePath) + '"';
            if (command.Length > 260) throw new InvalidOperationException("程序路径过长，无法注册登录自动启动。请将程序安装到较短的路径。");
            run.SetValue(StartupValue, command, RegistryValueKind.String);
            // An explicit enable action is the only time we clear Explorer's disabled state.
            approval?.DeleteValue(StartupValue, false);
        }
        else if (StartupCommandTargetsExecutable(run.GetValue(StartupValue) as string, executablePath))
            run.DeleteValue(StartupValue, false);
    }
    public static bool StartupCommandTargetsExecutable(string? command, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(command) || !Path.IsPathFullyQualified(executablePath)) return false;
        var arguments = CommandLineToArgvW(command.Trim(), out var count);
        if (arguments == 0) return false;
        try
        {
            var target = count > 0 ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments)) : null;
            return target is not null && Path.IsPathFullyQualified(target) &&
                string.Equals(Path.GetFullPath(target), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        finally { LocalFree(arguments); }
    }
    private static string CurrentExecutable() => Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
}
