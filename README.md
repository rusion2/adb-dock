# ADB Dock

V0.1.2 更新了托盘和程序图标，并改为点击展开后常驻、拖到屏幕边缘松开再收起。安装版默认勾选“登录 Windows 后自动启动 ADB Dock”，可在安装时取消，或在应用设置中关闭。安装版自带 .NET 10 运行时，无需另行安装 .NET。安装前请从托盘退出已有 ADB Dock。

Windows 上的 Android 悬浮设备助手，使用 C#、.NET 10 和 WPF。设备面板支持四边停靠、点击展开、托盘常驻，以及投屏、截图、APK 安装、基础按键、设备信息和 Logcat。`index.html` 是保留的交互原型，真实设备功能在原生客户端中运行。

## 使用安装版

运行名称含 `win-x64-Setup` 的安装程序即可为当前用户安装，默认目录为 `%LOCALAPPDATA%\Programs\AdbDock`，无需单独安装 .NET。安装器提供开始菜单入口及可选桌面快捷方式。

安装向导中“登录 Windows 时启动”默认勾选；取消勾选即可关闭。安装后也可在应用设置中修改。此功能只在当前用户登录 Windows 时启动一次，不会在程序崩溃或主动退出后自动重启。需要停止常驻时，从托盘选择“退出 ADB Dock”。

可以从 Windows 的“已安装的应用”或开始菜单中的卸载入口卸载。卸载会移除该安装创建的程序文件、快捷方式和启动项，保留 `%LOCALAPPDATA%\AdbDock` 中的用户设置与日志；安装目录中后加入的其他文件也会保留。更新或卸载前先从托盘退出正在运行的应用。

设置窗口底部显示当前程序版本号，升级后可在这里确认。

## 使用免安装版

完整解压发布 ZIP，运行其中的 `AdbDock.exe`。默认发布版需要 **Windows x64 .NET 10 Desktop Runtime**；仅安装基础 .NET Runtime 不够。名称含 `self-contained` 的自包含版已带运行时。

首次启动后，在右上角设置中指定现有 `adb.exe` 和 `scrcpy.exe`，点击“检测工具”检查版本。也可以将工具目录加入系统 `PATH`。scrcpy 仅为投屏所需，缺少它不会妨碍设备列表和其他 ADB 操作。

工具查找顺序是“设置中的指定路径 → PATH → 程序目录下的工具”。指定了无效路径时会明确报错，不会自动改用另一份工具。若需要将工具与程序放在一起，目录结构为：

```text
AdbDock.exe
AdbDock.dll
AdbDock.Core.dll
...其他发布文件
tools/
  platform-tools/
    adb.exe
    ...完整官方 platform-tools 的 DLL、依赖与许可文件
  scrcpy/
    scrcpy.exe
    ...完整官方 scrcpy 的服务端、DLL、依赖与许可文件
```

本项目的发布脚本不下载或随附 ADB/scrcpy。自行加入工具时，应保留各自完整发行目录及许可、声明文件；不能只复制可执行文件。

连接手机后开启 USB 调试，并在手机上允许此电脑。点击在线设备打开操作页；未授权、离线设备只显示指引。安装 APK 和按键会修改设备状态，使用前确认操作页中的序列号。已有 ADB 网络连接可直接显示，暂不提供无线配对向导。

设备列表每行的截图按钮可直接保存该设备的截图，不会进入另一台设备的操作页；未授权和离线设备的截图按钮不可用。窗口底部的“结果”可查看、复制或导出本次运行中最近 20 条操作结果，成功截图还提供“打开最近截图”入口。设备自动刷新不会清空这些结果。

“查看日志”支持正则筛选，输入 `error|fatal` 可匹配任一关键词，`(?i)error|fatal` 可忽略大小写。筛选应用于保留的最近日志，清空表达式后恢复全部；复制和导出以当前显示内容为准。表达式无效或匹配耗时过长时会提示，不中断日志读取。

日志缓存保留最近最多 20,000 行，并限制总文本大小；筛选、暂停和清空表达式都在当前窗口内操作。设备信息中的“性能监控”可手动开始或暂停，约每 2 秒读取设备整体 CPU、内存、电量和电池温度；CPU 需要两个样本才显示占用，关闭窗口或断连后停止采样。

窗口标题空白处可拖动；拖到屏幕任意一边并松开，卡片会收成贴边浮窗。在屏幕中间松开则保持展开。点击贴边箭头或 ADB/数量区才展开，展开后保持显示，鼠标离开或切换窗口都不会收起。概况区长按约 350ms 可拖动贴边浮窗，聚焦后方向键可切换停靠边；仍可点击标题栏或托盘中的收起入口手动收起。关闭窗口会隐藏面板，通过托盘菜单重新显示或选择“退出 ADB Dock”。

标题栏的固定按钮选中时显示紫色底色和边框，提示变为“取消固定展开”；此时拖到边缘也保持卡片。取消固定后，拖到边缘松开才收起；手动点击收起会取消固定。

配置和本地错误日志默认保存在 `%LOCALAPPDATA%\AdbDock`。免安装版和源码运行默认关闭登录启动；安装版以向导中的选择为准，默认开启。退出仅结束本程序拥有的客户端会话，不执行 `adb kill-server`。

## 从源码运行

需要 Windows 和 .NET 10 SDK。以下 PowerShell 命令从工程根目录执行，使用 Release 目录避免覆盖正在运行的 Debug 客户端：

```powershell
dotnet build .\AdbDock.sln -c Release --disable-build-servers -m:1 -nr:false
.\src\AdbDock.App\bin\Release\net10.0-windows\AdbDock.exe
```

若受限终端中的 .NET 工作负载检查因权限失败，可在当前 PowerShell 会话先执行：

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_HOME = Join-Path $PWD '.build\dotnet-home'
```

SDK-only 自动检查使用独立假工具，不查找或操作实际 ADB 设备：

```powershell
dotnet build .\tests\AdbDock.Checks\AdbDock.Checks.csproj -c Release --disable-build-servers -m:1 -nr:false
.\tests\AdbDock.Checks\bin\Release\net10.0\AdbDock.Checks.exe
```

## 生成发布包

```powershell
# 默认：构建、自动检查、发布需要 Desktop Runtime 的免安装版
.\scripts\Publish.ps1

# 自包含版：首次发布可能需要联网还原运行时包
.\scripts\Publish.ps1 -SelfContained

# 交互桌面中：额外运行原生 WPF 回归和发布目录启动检查
.\scripts\Publish.ps1 -IncludeUiChecks
```

脚本生成新的 `artifacts\AdbDock-<版本>-win-x64-<类型>-<时间>-<标识>` 目录、对应 ZIP 和 SHA256 文件，保留此前产物。ZIP 中包含整个发布目录，必须完整解压；`release-info.json` 记录 SDK、发布类型和检查结果。`checksPassed` 表示核心假工具检查；`nativeUiChecksPassed` 和 `publishedSmokePassed` 在未指定 UI 检查时为 `null`，实际通过后为 `true`。

`-IncludeUiChecks` 在核心检查后运行原生 WPF 回归，在发布后运行真实发布程序的启动、单实例及菜单退出检查，然后才压缩。检查失败会阻止生成 ZIP，并保留已有诊断产物。可用 `-SkipChecks` 跳过已单独完成的核心检查，该状态会写入发布信息；不能与 `-IncludeUiChecks` 同时使用。

UI 检查需要可交互的 Windows 桌面；同一 Windows 用户会话中应先从托盘退出其他 ADB Dock 实例，避免单实例机制使发布启动检查失败。测试使用独立设置和假设备快照，假工具拒绝安装、按键、截图写设备命令和投屏启动，不读取日常配置；弹窗检查仅控制测试拥有的窗口，不发送全局键盘或鼠标输入。检查期间会短暂显示原生窗口，日志和截图保留在 `artifacts\ui-checks-*` 与 `artifacts\release-smoke-*`。

也可从工程根目录单独运行这两类检查：

```powershell
dotnet build .\AdbDock.sln -c Release --disable-build-servers -m:1 -nr:false
$fixture = (Resolve-Path '.\tests\AdbDock.Checks\bin\Release\net10.0\AdbDock.Checks.exe').Path
$uiChecks = (Resolve-Path '.\tests\AdbDock.UiChecks\bin\Release\net10.0-windows\AdbDock.UiChecks.exe').Path
& $uiChecks $fixture (Join-Path $PWD 'artifacts\ui-checks')

# 将下列路径替换为实际发布目录；生成独立启动检查目录
$publishedApp = 'G:\path\to\published-folder\AdbDock.exe'
$smokeArtifacts = Join-Path $PWD ('artifacts\release-smoke-' + [Guid]::NewGuid().ToString('N'))
& $uiChecks --smoke $publishedApp $fixture $smokeArtifacts
```

## 生成 Windows 安装程序

```powershell
# 构建、核心检查、自包含发布，再生成当前用户安装程序
.\scripts\BuildInstaller.ps1

# 可交互桌面中追加原生窗口回归与自包含程序启动检查
.\scripts\BuildInstaller.ps1 -IncludeUiChecks

# 复用已生成的完整自包含目录；不会修改该输入目录
.\scripts\BuildInstaller.ps1 -PayloadDirectory 'G:\path\to\self-contained-payload'
```

安装器始终包含 .NET 10 Windows x64 运行时。脚本使用项目内 `.build\nuget-packages` 缓存；首次还原自包含运行时和下载 NSIS 编译器需要网络。脚本固定 NSIS 3.13 便携版的下载地址及 SHA256，验证 ZIP 后才在 `.build\dependencies` 解压使用，不向系统安装 NSIS。也可通过 `-NsisCompilerPath '<完整路径>\makensis.exe'` 指定已有的 NSIS 3.13 编译器。

输出是 `artifacts\AdbDock-<版本>-win-x64-Setup-<时间>-<标识>.exe`、对应 `.sha256` 及 `.installer-info.json`。安装信息记录自包含运行时、默认登录启动和检查结果。脚本保留此前安装包及本次中间产物，不自动运行安装程序。`-SkipChecks` 可跳过已单独完成的核心检查，不能与 `-IncludeUiChecks` 同用；生成安装包本身不表示已经验证安装与卸载流程。

传入 `-PayloadDirectory` 时，必须包含 `coreclr.dll`、`hostfxr.dll` 和 `PresentationFramework.dll` 等完整自包含文件；脚本会复制到独立暂存目录，补入项目说明及 .NET 许可与第三方声明。卸载清单逐项列出这些文件，仅删除本程序随附的内容，不递归删除整个安装目录。安装包不包含 ADB 或 scrcpy，安装后仍按前文配置现有工具。

## 验证范围

核心自动检查覆盖参数传递、二进制截图、超时与取消、设备状态、断连队列、Logcat 和投屏会话等逻辑。原生 WPF 检查验证实际控件、四边窗口边界、状态切换、弹窗、结果保留和会话关闭等行为；发布启动检查验证实际发布目录中的程序。假工具检查与编译成功不能替代真机验收。鼠标长按拖动的实际手感、100%/125%/150%/200% 缩放、混合 DPI 双屏和显示器移除，以及真机插拔、未授权/离线与网络状态变化、截图、安装、按键、投屏，仍须按 [需求说明](需求说明.md) 和 [开发进度](开发进度.md) 记录实际结果。

工程结构：`src/AdbDock.Core` 为设备通信与进程管理，`src/AdbDock.App` 为 WPF 窗口与托盘，`tests/AdbDock.Checks` 为核心假工具检查，`tests/AdbDock.UiChecks` 为原生窗口与发布启动检查。检查程序不依赖第三方测试包。
