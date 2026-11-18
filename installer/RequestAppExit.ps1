#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TargetExecutable,
    [string]$ExitEventPrefix = 'Local\AdbDock.Exit.'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The installer runs the 64-bit Windows PowerShell. Keep a handle to each
# matching process until completion so its PID cannot be reused for a new app.
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class DockExitNative {
    delegate bool EnumWindow(IntPtr hwnd, IntPtr state);
    [DllImport("advapi32", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32")] static extern bool EnumWindows(EnumWindow callback, IntPtr state);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("shell32")] static extern int Shell_NotifyIconGetRect(ref IconId icon, out Rect rect);
    [StructLayout(LayoutKind.Sequential)]
    struct IconId { public uint Size; public IntPtr Hwnd; public uint Id; public Guid Guid; }
    struct Rect { public int Left, Top, Right, Bottom; }

    public static bool HasOwner(Process process, string sid) {
        IntPtr token;
        if (!OpenProcessToken(process.Handle, 8, out token)) return false;
        try { using (var identity = new WindowsIdentity(token)) return identity.User.Value == sid; }
        finally { CloseHandle(token); }
    }

    public static bool OpenTrayMenu(int pid) {
        bool opened = false;
        EnumWindows(delegate(IntPtr hwnd, IntPtr _) {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != pid) return true;
            // Historical clients create one WinForms notification icon.
            for (uint id = 0; id <= 1; ++id) {
                var icon = new IconId { Size = (uint)Marshal.SizeOf(typeof(IconId)), Hwnd = hwnd, Id = id };
                Rect rect;
                if (Shell_NotifyIconGetRect(ref icon, out rect) < 0) continue;
                // WinForms NotifyIcon uses WM_USER+1024 for its Shell callback,
                // and opens its ContextMenuStrip on WM_RBUTTONUP.
                opened = PostMessage(hwnd, 0x0800, new IntPtr(id), new IntPtr(0x0205));
                if (opened) return false;
            }
            return true;
        }, IntPtr.Zero);
        return opened;
    }
}
'@

function Request-LegacyExit([Diagnostics.Process]$Process) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $owner = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    # Preserve the original Chinese menu label with PowerShell 5.1's ANSI fallback.
    $exitLabel = ([string][char]0x9000) + [char]0x51fa + ' ADB Dock'
    $exitName = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $exitLabel)
    $exitType = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::MenuItem)
    $trayExit = New-Object Windows.Automation.AndCondition($exitName, $exitType)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $trayOpened = $false
    while ($clock.ElapsedMilliseconds -lt 4500 -and -not $Process.HasExited) {
        if (-not $trayOpened) { $trayOpened = [DockExitNative]::OpenTrayMenu($Process.Id) }
        if (-not $trayOpened) { Start-Sleep -Milliseconds 100; continue }
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $owner)
        foreach ($window in $windows) {
            try {
                $item = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $trayExit)
                if ($item -and $item.Current.ProcessId -eq $Process.Id -and $item.Current.IsEnabled -and -not $item.Current.IsOffscreen) {
                    $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                    return $true
                }
            } catch [Windows.Automation.ElementNotAvailableException] { }
        }
        Start-Sleep -Milliseconds 100
    }
    return $Process.HasExited
}

try {
    if (-not [IO.Path]::IsPathRooted($TargetExecutable)) { exit 2 }
    $target = [IO.Path]::GetFullPath($TargetExecutable)
    if ([IO.Path]::GetFileName($target) -ine 'AdbDock.exe') { exit 2 }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach ($process in [Diagnostics.Process]::GetProcessesByName('AdbDock')) {
        try {
            if ($process.SessionId -ne $session -or $process.HasExited) { continue }
            if ($process.MainModule.FileName -ine $target -or -not [DockExitNative]::HasOwner($process, $sid)) { continue }
            try {
                $signal = [Threading.EventWaitHandle]::OpenExisting($ExitEventPrefix + $sid + '.' + $process.Id)
                try { [void]$signal.Set() } finally { $signal.Dispose() }
            } catch [Threading.WaitHandleCannotBeOpenedException] {
                if (-not (Request-LegacyExit $process)) { exit 2 }
            }
            if (-not $process.WaitForExit(8000)) { exit 2 }
        } catch [InvalidOperationException] {
            if (-not $process.HasExited) { throw }
        } finally { $process.Dispose() }
    }
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 2 }
