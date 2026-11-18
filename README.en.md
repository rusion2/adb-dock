<p align="center">
  <img src="src/AdbDock.App/Assets/AppIcon.svg" width="76" height="76" alt="ADB Dock icon">
</p>

<h1 align="center">ADB Dock</h1>

<p align="center">Android device status and everyday actions, at the edge of your Windows desktop.</p>

<p align="center"><a href="README.md">简体中文</a> · <strong>English</strong></p>

<p align="center"><code>Windows x64</code> · <code>.NET 10</code> · <code>WPF</code> · <code>v0.1.2</code></p>

<p align="center">
  <a href="#quick-start">Get started</a> · <a href="#preview">Screenshots</a> · <a href="#usage">Usage</a> · <a href="#development">Build from source</a> · <a href="#packaging">Packaging</a>
</p>

ADB Dock is a local Windows companion for Android devices. Check connection status, choose a device, then mirror its screen, save a screenshot, install an APK, send a basic key event, or inspect logs. It switches between a device panel and a compact edge tab, with no account or backend service required.

## Features

- **Clear device status**: distinguish online, unauthorized, offline, no devices, and ADB failures, with separate online and discovered counts.
- **Four-edge docking and a system tray**: click to expand and keep the panel open; when unpinned, drag to an edge and release to collapse. The pin button has a distinct selected state.
- **Six everyday tools**: scrcpy mirroring, PNG screenshots, APK installation, Home / Back / Recent / Wake keys, device information, and Logcat. Every device action targets the selected serial number.
- **Regex log filtering**: retain up to 20,000 recent lines, filter or clear the view, pause display, copy, and export.
- **Whole-device monitoring**: manually start or pause CPU, memory, battery level, and battery-temperature sampling from device information.
- **Per-user installation**: the installer includes the .NET runtime and supports sign-in startup and uninstallation, without administrator privileges.

<a id="preview"></a>
## Screenshots

These are native WPF windows with fixture devices, logs, and metrics. The application UI is currently in Chinese; this repository provides documentation in Chinese and English.

<p align="center">
  <img src="docs/images/device-panel.png" width="320" alt="Device panel with online count, device list, and connection states">
  <img src="docs/images/device-actions.png" width="320" alt="Device actions and the selected serial number">
</p>

<p align="center">
  <img src="docs/images/logcat.png" width="320" alt="Logcat with a regular-expression filter">
  <img src="docs/images/performance.png" width="320" alt="Device information and whole-device performance monitoring">
</p>

<p align="center">
  <img src="docs/images/docked-bar.png" width="266" alt="Top edge tab with a click-to-expand button">
</p>

<a id="quick-start"></a>
## Get started

The repository currently contains source code and packaging scripts; no GitHub Release has been published yet. [Build from source](#development) or [generate a package](#packaging) locally. Check the [Releases page](https://github.com/rusion2/adb-dock/releases) for published packages.

- **Installer**: run the generated `*win-x64-Setup*.exe`. It installs to `%LOCALAPPDATA%\Programs\AdbDock` by default, includes the .NET 10 runtime, and adds Start menu and uninstall entries. A desktop shortcut is optional.
- **Portable**: extract the entire ZIP, then run `AdbDock.exe`. The default portable package requires the **Windows x64 .NET 10 Desktop Runtime**; a `self-contained` package includes its runtime.

On a first installation, “Start ADB Dock when I sign in to Windows” is selected by default. You can disable it in the wizard or application settings; upgrades preserve a previous opt-out. Exit the existing instance from the tray before updating or uninstalling.

### Configure ADB and scrcpy

These tools are not bundled:

1. Obtain the official [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools).
2. For mirroring, obtain a complete official [scrcpy](https://github.com/Genymobile/scrcpy) distribution.
3. Open Settings in the upper-right corner, choose `adb.exe` / `scrcpy.exe`, and click the tool-detection button. You can also add their directories to `PATH`.

Lookup order: **configured path → system PATH → tools beside the application**. An invalid configured path produces an error instead of silently selecting another version. Missing scrcpy affects mirroring only.

<details>
<summary>Place tools beside the application</summary>

```text
AdbDock.exe
AdbDock.dll
...other published files
tools/
  platform-tools/
    adb.exe
    ...DLLs, dependencies, and license files from the official distribution
  scrcpy/
    scrcpy.exe
    ...server, DLLs, dependencies, and license files
```

Keep each distribution complete rather than copying only its executable.

</details>

### Connect a device

Enable USB debugging in Android Developer options, connect a data-capable USB cable, and authorize the computer on the phone. Click an online device to open its actions. Unauthorized and offline devices show connection guidance instead. Existing ADB network connections also appear; there is currently no wireless-pairing wizard.

APK installation and key events act on the selected device. Check its serial number before proceeding.

<a id="usage"></a>
## Usage

### Desktop controls

- **Expand**: click the edge arrow, ADB/count area, or a device icon. Hovering does not open the panel. Once open, it stays visible when the pointer leaves or focus changes.
- **Collapse**: when unpinned, drag the title bar to any screen edge and release. Releasing in the middle keeps the panel open. The title bar and tray also provide explicit collapse actions.
- **Pin open**: the selected pin has a purple background and border. A pinned panel stays open even after an edge drag. Unpinning does not immediately collapse it; manual collapse clears the pin.
- **Move the edge tab**: hold the ADB/count area for about 350 ms and drag, or focus it and use the arrow keys to choose an edge.
- **Inspect results**: the bottom results button retains the latest 20 operation results for the current run. Copy, export, or open the latest screenshot; device refreshes do not clear them.
- **Exit**: closing the main window hides it to the tray. Use “退出 ADB Dock” to quit. The settings footer shows the current version.

### Log filtering

Enter a regular expression in Logcat, for example:

```regex
error|fatal
```

Matching is case-sensitive by default. Use `(?i)error|fatal` to ignore case. Clearing the expression restores all currently retained lines. Invalid expressions keep the last valid filter; expensive expressions suspend filtering and display a message asking you to change the pattern.

The cache retains up to **20,000 lines**, bounded by approximately **32 MiB of raw text**, with at most 8,000 characters per line. Copy and export use the currently displayed text. **Pausing display discards incoming lines** while preserving the existing cache; this is not a continuous log recorder.

### Performance monitoring

Open device information and click “开始监控” to sample whole-device CPU usage, used/total memory, battery level, and **battery temperature**. CPU usage requires two valid samples. Each completed sample is followed by a wait of about two seconds. Unavailable metrics display a reason while other metrics continue updating.

Pausing, closing the window, or disconnecting the device stops sampling. Reconnection requires an explicit restart. Copy and export in this window include static device information only.

### Settings and uninstallation

Settings and local error logs are stored in `%LOCALAPPDATA%\AdbDock` by default. Portable and source builds do not register sign-in startup automatically; installed builds follow the wizard and Settings selection. Sign-in startup does not restart the application after a crash.

Uninstall through Windows Installed apps or the Start menu. Removal deletes bundled application files, shortcuts, and the matching startup entry, while retaining user settings, logs, screenshots, and unrelated files added to the installation directory. Quitting stops only this application's client sessions, leaving the shared ADB server running.

<a id="development"></a>
## Build from source

Requires **Windows x64, Git, and the .NET 10 SDK**. Run in PowerShell:

```powershell
git clone https://github.com/rusion2/adb-dock.git
cd adb-dock
dotnet build .\AdbDock.sln -c Release --disable-build-servers -m:1 -nr:false
.\src\AdbDock.App\bin\Release\net10.0-windows\AdbDock.exe
```

### Automated checks

After building, run the core checks without a third-party test framework:

```powershell
.\tests\AdbDock.Checks\bin\Release\net10.0\AdbDock.Checks.exe
```

<details>
<summary>Native UI, startup registration, and installer checks</summary>

UI checks require an interactive Windows desktop. Exit other ADB Dock instances in the same user session first. Tests use fixture devices and isolated settings, without installing applications or sending keys to real phones.

```powershell
$fixture = (Resolve-Path '.\tests\AdbDock.Checks\bin\Release\net10.0\AdbDock.Checks.exe').Path
$uiChecks = (Resolve-Path '.\tests\AdbDock.UiChecks\bin\Release\net10.0-windows\AdbDock.UiChecks.exe').Path
& $uiChecks $fixture (Join-Path $PWD 'artifacts\ui-checks')
& $uiChecks --startup-checks (Join-Path $PWD 'artifacts\startup-checks')
```

Startup checks use temporary registry keys and do not change the real startup entry. Installer checks use separate installation directories, shortcuts, and test registry keys:

```powershell
.\scripts\TestInstaller.ps1 `
  -PayloadDirectory 'C:\path\to\self-contained-output' `
  -NsisCompilerPath 'C:\path\to\makensis.exe'
```

</details>

For v0.1.2, **34 core checks, 24 native UI checks, and 8 isolated installer lifecycle checks have passed**. Automated checks do not replace real-device or mixed-DPI testing. See the [development record](开发进度.md) for the precise validation scope.

<a id="packaging"></a>
## Packaging

```powershell
# Self-contained Windows x64 installer: build, core checks, publish, and NSIS
.\scripts\BuildInstaller.ps1

# Portable ZIP requiring the Desktop Runtime
.\scripts\Publish.ps1

# Portable ZIP with its runtime included
.\scripts\Publish.ps1 -SelfContained
```

Outputs go to `artifacts\`, including the installer or ZIP, a SHA256 checksum, and validation metadata in JSON. Each run uses a new output directory and preserves previous artifacts. The installer supports per-user sign-in startup by default; ADB and scrcpy still need separate configuration.

The first build needs network access to restore runtime packages. The installer script downloads and verifies a pinned NSIS 3.13 portable compiler without installing NSIS system-wide. For installer builds, runtime packages are cached in `.build\nuget-packages`.

<details>
<summary>Advanced packaging options</summary>

- Both scripts support `-IncludeUiChecks` for native-window and published-app startup checks; use an interactive desktop and exit other clients first.
- `-SkipChecks` skips core checks; use it only after separate verification. The skip is recorded in JSON. It cannot be combined with `-IncludeUiChecks`.
- The installer script accepts `-NsisCompilerPath` for an existing NSIS 3.13 compiler and `-PayloadDirectory` for a complete self-contained output folder.
- Keep the full runtime payload. The script adds project documentation, .NET licensing and third-party notices, and an exact uninstall manifest.

</details>

## Troubleshooting

- **ADB unavailable**: verify the configured path, run tool detection, and check that companion DLLs are present.
- **Unauthorized or offline device**: unlock the phone, confirm USB debugging authorization, check the cable, then refresh.
- **Mirroring fails**: check the complete scrcpy distribution and its path. A mirroring failure does not change the ADB online count.
- **Portable app will not start**: install the .NET 10 **Desktop Runtime**, or use a self-contained package.
- **Build fails in a restricted environment**: set `$env:MSBuildEnableWorkloadResolver = 'false'` in the current PowerShell session. To isolate CLI settings, also set `$env:DOTNET_CLI_HOME = Join-Path $PWD '.build\dotnet-home'`.

## Project layout and further reading

```text
src/AdbDock.App/        WPF windows, tray, settings, logs, and monitoring UI
src/AdbDock.Core/       ADB communication, cancellation, processes, and mirroring
tests/                 Core and native-window checks
installer/             NSIS installation and uninstallation
scripts/               Packaging, installer validation, and icon generation
docs/images/           Native screenshots with fixture data
index.html             Browser interaction prototype; simulated devices/actions only
```

[Requirements and acceptance criteria (Chinese)](需求说明.md) · [Development record (Chinese)](开发进度.md) · [Report an issue](https://github.com/rusion2/adb-dock/issues)
