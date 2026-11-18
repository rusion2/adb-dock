; Build with the portable official NSIS compiler. The build wrapper supplies a
; validated payload and an exact, generated uninstall manifest.
Unicode true
RequestExecutionLevel user
ManifestDPIAware true
!ifdef FAST_TEST_COMPRESSION
  SetCompressor zlib
!else
  SetCompressor /SOLID lzma
!endif

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "Sections.nsh"
!include "x64.nsh"

!ifndef APP_VERSION
  !error "APP_VERSION is required"
!endif
!ifndef APP_FILE_VERSION
  !error "APP_FILE_VERSION (four numeric components) is required"
!endif
!ifndef PAYLOAD_DIR
  !error "PAYLOAD_DIR is required"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif
!ifndef FILE_MANIFEST
  !error "FILE_MANIFEST is required"
!endif
!ifndef PAYLOAD_INVENTORY
  !error "PAYLOAD_INVENTORY is required"
!endif
!ifndef APP_ICON
  !define APP_ICON "${__FILEDIR__}\..\src\AdbDock.App\Assets\App.ico"
!endif

; Override these at compile time for an isolated smoke build. In particular,
; tests can place Run/uninstall values under an inert test-only HKCU subtree.
!ifndef APP_REG_KEY
  !define APP_REG_KEY "Software\AdbDock"
!endif
!ifndef UNINSTALL_REG_KEY
  !define UNINSTALL_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AdbDock"
!endif
!ifndef RUN_REG_KEY
  !define RUN_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
!endif
!ifndef RUN_VALUE
  !define RUN_VALUE "AdbDock"
!endif
!ifndef APPROVAL_REG_KEY
  !define APPROVAL_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
!endif
!ifndef LINKS_ROOT
  !define LINKS_ROOT "$SMPROGRAMS\ADB Dock"
!endif
!ifndef DESKTOP_ROOT
  !define DESKTOP_ROOT "$DESKTOP"
!endif
!ifndef INSTANCE_MUTEX_PREFIX
  !define INSTANCE_MUTEX_PREFIX "Local\AdbDock.Instance."
!endif
!ifndef EXIT_EVENT_PREFIX
  !define EXIT_EVENT_PREFIX "Local\AdbDock.Exit."
!endif

!include "${FILE_MANIFEST}"

Name "ADB Dock ${APP_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\AdbDock"
InstallDirRegKey HKCU "${APP_REG_KEY}" "InstallDir"
BrandingText "ADB Dock ${APP_VERSION}"
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "ADB Dock ${APP_VERSION}"
VIAddVersionKey /LANG=1033 "FileDescription" "ADB Dock ${APP_VERSION} per-user installer"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" ""

Var CurrentUserSid
Var InstallGuard
Var PayloadDeleteFailed
Var StartupApproved

!define MUI_ABORTWARNING
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_WELCOMEPAGE_TITLE "ADB Dock ${APP_VERSION}"
!define MUI_FINISHPAGE_RUN "$INSTDIR\AdbDock.exe"
!define MUI_FINISHPAGE_RUN_TEXT "$(LaunchApp)"
!define MUI_FINISHPAGE_TEXT "$(FinishSetupText)"
!define MUI_LANGDLL_REGISTRY_ROOT HKCU
!define MUI_LANGDLL_REGISTRY_KEY "${APP_REG_KEY}"
!define MUI_LANGDLL_REGISTRY_VALUENAME "InstallerLanguage"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

LangString CoreName ${LANG_SIMPCHINESE} "ADB Dock 主程序（必选）"
LangString CoreName ${LANG_ENGLISH} "ADB Dock application (required)"
LangString StartupName ${LANG_SIMPCHINESE} "登录 Windows 后自动启动 ADB Dock"
LangString StartupName ${LANG_ENGLISH} "Start ADB Dock when I sign in to Windows"
LangString DesktopName ${LANG_SIMPCHINESE} "创建桌面快捷方式"
LangString DesktopName ${LANG_ENGLISH} "Create a desktop shortcut"
LangString CoreDescription ${LANG_SIMPCHINESE} "为当前用户安装。点击安装后，会先退出正在运行的旧版 ADB Dock。保留已有配置和截图。"
LangString CoreDescription ${LANG_ENGLISH} "Install for this user. Setup closes the previous ADB Dock when you click Install. Existing settings and screenshots are retained."
LangString StartupDescription ${LANG_SIMPCHINESE} "默认启用。登录 Windows 后启动本程序，可在安装时取消，或稍后在应用设置中关闭。"
LangString StartupDescription ${LANG_ENGLISH} "Enabled by default. Start this application after signing in. Clear this option now or turn it off later in application Settings."
LangString DesktopDescription ${LANG_SIMPCHINESE} "在当前用户桌面添加 ADB Dock 快捷方式。"
LangString DesktopDescription ${LANG_ENGLISH} "Add an ADB Dock shortcut to this user's desktop."
LangString LaunchApp ${LANG_SIMPCHINESE} "立即启动 ADB Dock"
LangString LaunchApp ${LANG_ENGLISH} "Launch ADB Dock now"
LangString FinishSetupText ${LANG_SIMPCHINESE} "ADB Dock 已安装。$\r$\n$\r$\n连接 Android 设备需准备 Android SDK Platform-Tools（ADB），投屏另需 scrcpy。首次启动可按应用内的步骤配置。"
LangString FinishSetupText ${LANG_ENGLISH} "ADB Dock is installed.$\r$\n$\r$\nDevice connection requires Android SDK Platform-Tools (ADB). Screen mirroring also needs scrcpy. Follow the in-app setup steps on first launch."
LangString NeedX64 ${LANG_SIMPCHINESE} "ADB Dock 需要 64 位 Windows。"
LangString NeedX64 ${LANG_ENGLISH} "ADB Dock requires 64-bit Windows."
LangString AppRunning ${LANG_SIMPCHINESE} "ADB Dock 正在运行。请从程序托盘或右键菜单选择退出，然后重试。安装程序不会强制结束你的程序。"
LangString AppRunning ${LANG_ENGLISH} "ADB Dock is running. Exit it from its tray or context menu, then retry. Setup will not forcibly terminate the application."
LangString ClosingApp ${LANG_SIMPCHINESE} "正在退出旧版 ADB Dock…"
LangString ClosingApp ${LANG_ENGLISH} "Closing the previous ADB Dock…"
LangString ExitIncomplete ${LANG_SIMPCHINESE} "ADB Dock 仍在运行。请完成当前操作，从托盘退出程序，然后点击“重试”继续安装。"
LangString ExitIncomplete ${LANG_ENGLISH} "ADB Dock is still running. Finish any open operation, exit the app from its tray, and click Retry to continue setup."
LangString CheckFailed ${LANG_SIMPCHINESE} "无法确认当前用户的程序运行状态，安装或卸载已停止。"
LangString CheckFailed ${LANG_ENGLISH} "Could not verify the current user's application state. Installation or removal has stopped."
LangString InstallFailed ${LANG_SIMPCHINESE} "安装未完成。请查看详细信息，检查安装目录和当前用户权限。"
LangString InstallFailed ${LANG_ENGLISH} "Installation did not complete. Review the details and check the destination directory and user permissions."
LangString UninstallFailed ${LANG_SIMPCHINESE} "部分程序文件无法删除，卸载未完成。请关闭相关窗口后重试。用户配置、截图和其他非程序文件不会删除。"
LangString UninstallFailed ${LANG_ENGLISH} "Some application files could not be removed. Close related windows and retry. Settings, screenshots and unrelated files are retained."
LangString UninstallTitle ${LANG_SIMPCHINESE} "卸载 ADB Dock"
LangString UninstallTitle ${LANG_ENGLISH} "Uninstall ADB Dock"

Section "$(CoreName)" SecCore
  SectionIn RO
  Call EnsureApplicationClosed
  Call PreparePayload
  ClearErrors
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${PAYLOAD_DIR}\*"
  IfErrors install_failed
  Call RecordPayload
  ClearErrors
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "${LINKS_ROOT}"
  CreateShortcut "${LINKS_ROOT}\ADB Dock.lnk" "$INSTDIR\AdbDock.exe" "" "$INSTDIR\AdbDock.exe"
  CreateShortcut "${LINKS_ROOT}\Uninstall ADB Dock.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${APP_REG_KEY}" "Version" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "DisplayName" "ADB Dock ${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "Publisher" "ADB Dock"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "DisplayIcon" "$INSTDIR\AdbDock.exe"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "NoRepair" 1
  IfErrors install_failed
  Goto install_done
install_failed:
  MessageBox MB_OK|MB_ICONSTOP "$(InstallFailed)" /SD IDOK
  SetErrorLevel 2
  Abort
install_done:
SectionEnd

; Intentionally selected for a first install: the user requested login startup.
Section "$(StartupName)" SecStartup
  StrLen $0 '$\"$INSTDIR\AdbDock.exe$\"'
  ${If} $0 > 260
    MessageBox MB_OK|MB_ICONSTOP "$(InstallFailed)" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  ClearErrors
  WriteRegStr HKCU "${RUN_REG_KEY}" "${RUN_VALUE}" '$\"$INSTDIR\AdbDock.exe$\"'
  IfErrors startup_failed
  ; Selecting this component is an explicit enable action. Remove only this
  ; application's Explorer approval record, never another startup item's state.
  DeleteRegValue HKCU "${APPROVAL_REG_KEY}" "${RUN_VALUE}"
  Call ReadStartupApproval
  ${If} $StartupApproved == 0
    Goto startup_failed
  ${EndIf}
  ClearErrors
  Goto startup_done
startup_failed:
  MessageBox MB_OK|MB_ICONSTOP "$(InstallFailed)" /SD IDOK
  SetErrorLevel 2
  Abort
startup_done:
SectionEnd

Section /o "$(DesktopName)" SecDesktop
  ClearErrors
  CreateDirectory "${DESKTOP_ROOT}"
  CreateShortcut "${DESKTOP_ROOT}\ADB Dock.lnk" "$INSTDIR\AdbDock.exe" "" "$INSTDIR\AdbDock.exe"
  IfErrors 0 desktop_done
  MessageBox MB_OK|MB_ICONSTOP "$(InstallFailed)" /SD IDOK
  SetErrorLevel 2
  Abort
desktop_done:
SectionEnd

Section "-ApplyOptions"
  ; On reinstall, an explicitly cleared startup option must disable an existing
  ; registration for this installation without removing another copy's entry.
  SectionGetFlags ${SecStartup} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 == 0
    ReadRegStr $0 HKCU "${RUN_REG_KEY}" "${RUN_VALUE}"
    ReadRegStr $1 HKCU "${APP_REG_KEY}" "InstallDir"
    ${If} $1 == ""
      StrCpy $1 "$INSTDIR"
    ${EndIf}
    ${If} $0 == '$\"$INSTDIR\AdbDock.exe$\"'
    ${OrIf} $0 == '$\"$1\AdbDock.exe$\"'
      ClearErrors
      DeleteRegValue HKCU "${RUN_REG_KEY}" "${RUN_VALUE}"
      ${If} ${Errors}
        MessageBox MB_OK|MB_ICONSTOP "$(InstallFailed)" /SD IDOK
        SetErrorLevel 2
        Abort
      ${EndIf}
    ${EndIf}
  ${EndIf}
  ClearErrors
  WriteRegStr HKCU "${APP_REG_KEY}" "InstallDir" "$INSTDIR"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "$(InstallFailed)" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  System::Call 'kernel32::CloseHandle(p $InstallGuard)'
  StrCpy $InstallGuard 0
SectionEnd

Section "Uninstall"
  Call un.EnsureApplicationClosed
  Call un.PreparePayload
  StrCpy $PayloadDeleteFailed 0
  !insertmacro RemovePayload
  ${If} $PayloadDeleteFailed != 0
    MessageBox MB_OK|MB_ICONSTOP "$(UninstallFailed)" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  ClearErrors
  Delete "$INSTDIR\Uninstall.exe"
  IfFileExists "$INSTDIR\Uninstall.exe" 0 uninstall_files_done
  MessageBox MB_OK|MB_ICONSTOP "$(UninstallFailed)" /SD IDOK
  SetErrorLevel 2
  Abort
uninstall_files_done:
  DeleteRegValue HKCU "${APP_REG_KEY}\Payloads" "$INSTDIR"
  DeleteRegKey /ifempty HKCU "${APP_REG_KEY}\Payloads"
  ReadRegStr $0 HKCU "${RUN_REG_KEY}" "${RUN_VALUE}"
  ${If} $0 == '$\"$INSTDIR\AdbDock.exe$\"'
    DeleteRegValue HKCU "${RUN_REG_KEY}" "${RUN_VALUE}"
    DeleteRegValue HKCU "${APPROVAL_REG_KEY}" "${RUN_VALUE}"
  ${EndIf}
  ReadRegStr $0 HKCU "${APP_REG_KEY}" "InstallDir"
  ${If} $0 == "$INSTDIR"
    ; A user may have installed a newer copy in another directory. An older
    ; uninstaller must not remove the current copy's entry or shortcuts.
    Delete "${LINKS_ROOT}\ADB Dock.lnk"
    Delete "${LINKS_ROOT}\Uninstall ADB Dock.lnk"
    RMDir "${LINKS_ROOT}"
    Delete "${DESKTOP_ROOT}\ADB Dock.lnk"
    DeleteRegKey HKCU "${UNINSTALL_REG_KEY}"
    DeleteRegValue HKCU "${APP_REG_KEY}" "InstallDir"
    DeleteRegValue HKCU "${APP_REG_KEY}" "Version"
    DeleteRegValue HKCU "${APP_REG_KEY}" "InstallerLanguage"
    DeleteRegKey /ifempty HKCU "${APP_REG_KEY}"
  ${EndIf}
  ; Nonrecursive removal preserves settings and unrelated user-added files.
  RMDir "$INSTDIR"
  System::Call 'kernel32::CloseHandle(p $InstallGuard)'
  StrCpy $InstallGuard 0
  ClearErrors
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCore} "$(CoreDescription)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStartup} "$(StartupDescription)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "$(DesktopDescription)"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; Resolve the real process-token SID, matching App.xaml.cs exactly. Looking up a
; title or process name could block unrelated applications or miss a hidden dock.
!macro DefineStateFunctions Prefix
Function ${Prefix}GetCurrentUserSid
  Push $0
  Push $1
  Push $2
  Push $3
  Push $4
  Push $5
  StrCpy $CurrentUserSid ""
  System::Call 'kernel32::GetCurrentProcess() p.r0'
  System::Call 'advapi32::OpenProcessToken(p r0, i 0x0008, *p.r1) i.r2'
  StrCmp $2 0 sid_done
  System::Call 'advapi32::GetTokenInformation(p r1, i 1, p 0, i 0, *i.r2) i'
  IntCmp $2 0 sid_close_token sid_close_token
  System::Alloc $2
  Pop $3
  StrCmp $3 0 sid_close_token
  System::Call 'advapi32::GetTokenInformation(p r1, i 1, p r3, i r2, *i.r2) i.r4'
  StrCmp $4 0 sid_free
  System::Call '*$3(p.r4)'
  System::Call 'advapi32::ConvertSidToStringSidW(p r4, *p.r5) i.r4'
  StrCmp $4 0 sid_free
  System::Call '*$5(&w${NSIS_MAX_STRLEN}.r0)'
  StrCpy $CurrentUserSid $0
  System::Call 'kernel32::LocalFree(p r5)'
sid_free:
  System::Free $3
sid_close_token:
  System::Call 'kernel32::CloseHandle(p r1)'
sid_done:
  Pop $5
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Function ${Prefix}EnsureApplicationClosed
  Call ${Prefix}GetCurrentUserSid
  ${If} $CurrentUserSid == ""
    MessageBox MB_OK|MB_ICONSTOP "$(CheckFailed)" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
!if "${Prefix}" == ""
  StrCpy $2 0
!endif
check_again:
  ; Keep the singleton name reserved throughout file replacement or removal.
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "${INSTANCE_MUTEX_PREFIX}$CurrentUserSid") p.r0 ?e'
  Pop $1
  ${If} $0 != 0
    ${If} $1 != 183
      StrCpy $InstallGuard $0
      Return
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'
    Goto application_running
  ${EndIf}
  ${If} $1 != 5
    MessageBox MB_OK|MB_ICONSTOP "$(CheckFailed)" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
application_running:
!if "${Prefix}" == ""
  ${If} $2 == 0
    DetailPrint "$(ClosingApp)"
    InitPluginsDir
    File /oname=$PLUGINSDIR\RequestAppExit.ps1 "${__FILEDIR__}\RequestAppExit.ps1"
    ReadRegStr $3 HKCU "${APP_REG_KEY}" "InstallDir"
    ${If} $3 != ""
    ${AndIf} $3 != "$INSTDIR"
      Push $3
      Call RequestExitInDirectory
      Pop $4
      ${If} $4 != 0
        Goto request_incomplete
      ${EndIf}
    ${EndIf}
    Push $INSTDIR
    Call RequestExitInDirectory
    Pop $4
    ${If} $4 != 0
      Goto request_incomplete
    ${EndIf}
    StrCpy $2 1
    Goto check_again
  ${EndIf}
request_incomplete:
  StrCpy $2 0
  IfSilent stop_install
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(ExitIncomplete)" IDRETRY check_again
!else
  IfSilent stop_install
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(AppRunning)" IDRETRY check_again
!endif
stop_install:
  SetErrorLevel 2
  Abort
FunctionEnd
!macroend
!insertmacro DefineStateFunctions ""
!insertmacro DefineStateFunctions "un."

!macro DefinePayloadPreparation FunctionName Mode FailureMessage
Function ${FunctionName}
!if "${Mode}" != "Record"
  InitPluginsDir
  File /oname=$PLUGINSDIR\PreparePayload.ps1 "${__FILEDIR__}\PreparePayload.ps1"
  File /oname=$PLUGINSDIR\payload-inventory.txt "${PAYLOAD_INVENTORY}"
!endif
  nsExec::ExecToLog '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\PreparePayload.ps1" -Mode ${Mode} -InstallDirectory "$INSTDIR" -Inventory "$PLUGINSDIR\payload-inventory.txt" -RegistryKey "${APP_REG_KEY}\Payloads"'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "$(${FailureMessage})" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd
!macroend
!insertmacro DefinePayloadPreparation PreparePayload Install InstallFailed
!insertmacro DefinePayloadPreparation RecordPayload Record InstallFailed
!insertmacro DefinePayloadPreparation un.PreparePayload Uninstall UninstallFailed

; NSIS is a 32-bit process on 64-bit Windows. Use the native Windows PowerShell
; host so it can inspect the 64-bit installed app, including a hidden tray window.
Function RequestExitInDirectory
  Pop $4
  IfFileExists "$4\AdbDock.exe" request_exit
  Push 0
  Return
request_exit:
  nsExec::ExecToLog /TIMEOUT=18000 '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\RequestAppExit.ps1" -TargetExecutable "$4\AdbDock.exe" -ExitEventPrefix "${EXIT_EVENT_PREFIX}"'
FunctionEnd

Function ReadStartupApproval
  Push $0
  Push $1
  Push $2
  Push $3
  StrCpy $StartupApproved 0
  System::Alloc 1024
  Pop $3
  ${If} $3 != 0
    StrCpy $1 1024
    System::Call 'advapi32::RegGetValueW(p 0x80000001, w "${APPROVAL_REG_KEY}", w "${RUN_VALUE}", i 0x00010008, *i.r0, p r3, *i.r1) i.r2'
    ${If} $2 == 2
      StrCpy $StartupApproved 1
    ${ElseIf} $2 == 0
      ${If} $1 > 0
        System::Call '*$3(&i1.r2)'
        ${If} $2 == 0
        ${OrIf} $2 == 2
        ${OrIf} $2 == 6
          StrCpy $StartupApproved 1
        ${EndIf}
      ${EndIf}
    ${EndIf}
    System::Free $3
  ${EndIf}
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Function .onInit
  SetShellVarContext current
  SetRegView 64
  !insertmacro MUI_LANGDLL_DISPLAY
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "$(NeedX64)" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  ; Preserve a user's previous opt-out on an upgrade. A fresh install keeps the
  ; visible startup component selected by default.
  ReadRegStr $0 HKCU "${APP_REG_KEY}" "InstallDir"
  ${If} $0 != ""
    ReadRegStr $1 HKCU "${RUN_REG_KEY}" "${RUN_VALUE}"
    ${If} $1 != '$\"$0\AdbDock.exe$\"'
      !insertmacro UnselectSection ${SecStartup}
    ${Else}
      Call ReadStartupApproval
      ${If} $StartupApproved == 0
        !insertmacro UnselectSection ${SecStartup}
      ${EndIf}
    ${EndIf}
  ${EndIf}
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  !insertmacro MUI_UNGETLANGUAGE
FunctionEnd
