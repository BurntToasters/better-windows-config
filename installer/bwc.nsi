; Better Windows Config installer (NSIS 3.10 or newer, Unicode).
;
; Built by scripts/build-installer.js. Compile by hand only for debugging; the script expects every
; define below. Failure modes and intended behavior: docs/failure-modes/installer.md.
;
; Per machine (0.1.2). The ship setup asks for administrator rights, installs to %ProgramFiles%\Better Windows Config,
; writes its uninstall entry to HKLM (64-bit view), and puts the shortcuts in the all-users Start menu. The E2E flavor
; stays a per-user sandbox (asInvoker), plus the E2E-only /MACHINE switch.
;
; Command line:
;   (none)             interactive install to %ProgramFiles%\Better Windows Config
;   /S                 silent install or uninstall
;   /D=<dir>           ship: never the install folder. Accepted (the updater passes it) and ignored; nothing there is
;                      read, closed, or changed. Must be last and unquoted (NSIS rule).
;                      E2E per-user sandbox: the install folder, as before.
;   /RELAUNCH          start the Hub when the run ends, through BwcUpdate.exe --launch-hub (never directly, so an
;                      elevated setup does not start an elevated Hub): the new Hub after success, or the installed
;                      Hub with --update-result InstallerFailed after failure. Without it no Hub starts.
;   /PARENTPID=<n>     the updater that launched this setup. The setup waits for it to exit, then
;                      excludes it from the close step.
;   /RUNID=<id>        E2E builds only (required). Uninstall key under BWC-E2E\<id> (HKCU, or HKLM with /MACHINE).
;   /SHORTCUTS=<dir>   E2E builds only (required unless /MACHINE). Folder for the two Start menu shortcuts.
;   /MACHINE           E2E builds only. The per-machine layout in a sandbox: %ProgramFiles%\BWC-E2E\<id>,
;                      HKLM\Software\BWC-E2E\<id>, shortcuts in the all-users Start menu folder BWC-E2E-<id>.
;                      Needs a shell that is already elevated. It never asks for elevation.
;
; Defines (all passed by scripts/build-installer.js with /D):
;   BWC_VERSION        display version, for example 0.2.0-beta.3
;   BWC_FILE_VERSION   four-part numeric version for VIProductVersion, for example 0.2.0.3
;   BWC_RID            win-x64 or win-arm64
;   BWC_ARCH           x64 or arm64
;   BWC_OUTPUT         absolute path of the setup EXE to write
;   BWC_PAYLOAD_NSH    generated file with the payload macros (file list, moves, deletes, rmdirs)
;   BWC_UPDATER_SRC    absolute path of the BwcUpdate.exe that is installed (runs the close step)
;   BWC_LICENSE        absolute path of the repo LICENSE file
;   BWC_NOTICES        absolute path of the generated THIRD-PARTY-NOTICES.txt
;   BWC_ICON           absolute path of resources\bwc.ico
;   BWC_ESTIMATED_KB   payload size in KiB, for Apps and Features
;   BWC_E2E            defined only for the E2E flavor (no value)
;   BWC_SIGN           defined only when Authenticode signing is enforced (no value)
;   BWC_SIGN_SCRIPT    scripts\windows-artifact-sign.ps1, which signs the setup EXE and the uninstaller
;   BWC_SIGN_UNINST_COPY  where the signed uninstaller is copied, for the verify step

!ifndef BWC_VERSION
  !error "BWC_VERSION is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_FILE_VERSION
  !error "BWC_FILE_VERSION is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_RID
  !error "BWC_RID is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_ARCH
  !error "BWC_ARCH is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_OUTPUT
  !error "BWC_OUTPUT is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_PAYLOAD_NSH
  !error "BWC_PAYLOAD_NSH is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_UPDATER_SRC
  !error "BWC_UPDATER_SRC is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_LICENSE
  !error "BWC_LICENSE is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_NOTICES
  !error "BWC_NOTICES is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_ICON
  !error "BWC_ICON is not defined. Run scripts/build-installer.js."
!endif
!ifndef BWC_ESTIMATED_KB
  !error "BWC_ESTIMATED_KB is not defined. Run scripts/build-installer.js."
!endif

; Authenticode signing. The setup EXE is signed after makensis writes it (!finalize). The uninstaller
; is signed before NSIS embeds it (!uninstfinalize, NSIS 3.08+), so the setup signature covers it.
; Both run the same script; the uninstaller copy is also kept for the verify step.
!ifdef BWC_SIGN
  !ifndef BWC_SIGN_SCRIPT
    !error "BWC_SIGN is set but BWC_SIGN_SCRIPT is not. Run scripts/build-installer.js."
  !endif
  !ifndef BWC_SIGN_UNINST_COPY
    !error "BWC_SIGN is set but BWC_SIGN_UNINST_COPY is not. Run scripts/build-installer.js."
  !endif
  !finalize 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${BWC_SIGN_SCRIPT}" -FilePath "%1"'
  !uninstfinalize 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${BWC_SIGN_SCRIPT}" -FilePath "%1" -CopyTo "${BWC_SIGN_UNINST_COPY}"'
!endif

Unicode true
SetCompressor /SOLID lzma
ManifestDPIAware true
; Ship asks for elevation. The E2E flavor never does, so no test or dev build can show a prompt.
; scripts/build-installer.js checks the manifest of the built EXE against the flavor.
!ifdef BWC_E2E
  RequestExecutionLevel user
!else
  RequestExecutionLevel admin
!endif
ShowInstDetails show
ShowUninstDetails show

!define BWC_ROOT "${__FILEDIR__}"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

; FileFunc macros used by bwc-common.nsh and by .onInit, un.onInit, and the install section. NSIS
; needs each ${...} defined before the line that uses it, so these come before the include below.
; The un. variants are needed by the uninstaller. GetParent splits the folder of a payload file.
!insertmacro GetParameters
!insertmacro GetOptions
!insertmacro un.GetParameters
!insertmacro un.GetOptions
!insertmacro GetParent

!include "${BWC_ROOT}\include\bwc-common.nsh"

; Generated by scripts/build-installer.js: BWC_PAYLOAD_FILES, BWC_PAYLOAD_MOVE_IN,
; BWC_PAYLOAD_DELETE, and BWC_PAYLOAD_RMDIRS.
!include "${BWC_PAYLOAD_NSH}"

Name "${BWC_PRODUCT}"
OutFile "${BWC_OUTPUT}"
; Ship and /MACHINE: a per-machine run sets $INSTDIR itself in .onInit (BwcPickMachineTarget), so this default is not
; used. There is no InstallDirRegKey: the recorded folder is read and checked by hand. E2E per-user sandbox: /D= or
; this default, as before; a run without /D= never reads a real uninstall key.
InstallDir "$LOCALAPPDATA\Programs\${BWC_PRODUCT}"
BrandingText "${BWC_PRODUCT}"

VIProductVersion "${BWC_FILE_VERSION}"
VIAddVersionKey "ProductName" "${BWC_PRODUCT}"
VIAddVersionKey "CompanyName" "${BWC_PUBLISHER}"
VIAddVersionKey "FileDescription" "${BWC_PRODUCT} setup"
VIAddVersionKey "FileVersion" "${BWC_VERSION}"
VIAddVersionKey "ProductVersion" "${BWC_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (c) Better Windows Config contributors"

; ---------------------------------------------------------------------------
; Modern UI 2
; ---------------------------------------------------------------------------

!define MUI_ABORTWARNING
!define MUI_ICON "${BWC_ICON}"
!define MUI_UNICON "${BWC_ICON}"
; The finish page option runs a function instead of the Hub: it calls BwcUpdate.exe --launch-hub, which starts
; the Hub at the shell integrity level, so an elevated setup never leaves an elevated Hub behind.
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION BwcLaunchFromFinish
!define MUI_FINISHPAGE_RUN_TEXT "Start ${BWC_PRODUCT}"

; No directory page: the install folder is not a choice (see docs/installer.md).
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${BWC_LICENSE}"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------------------------
; Initialization
; ---------------------------------------------------------------------------

; Order: mode (registry view, shell context, elevated token), raw /D=, target folder, host checks, folder rules, newer
; version. Everything here runs before any file or registry value changes. There is no directory page, so the folder is
; always checked here.
Function .onInit
  Call BwcInitMode
  Call BwcCheckRawDir
  StrCmp $BwcMachine "1" 0 BwcInit_target
  Call BwcPickMachineTarget
  Call BwcNoteRawDir
  BwcInit_target:
  Call BwcTrimInstDir
  Call BwcCheckHost
  Call BwcCheckArchitecture
  Call BwcCheckInstDir
  Call BwcRefuseNewer
  StrCmp $BwcMachine "1" 0 +2
  Call BwcRefusePendingUninstall
  Call BwcReadParentPid
FunctionEnd

Function un.onInit
  !ifdef BWC_E2E
    Call un.BwcReadE2eSwitches
  !else
    StrCpy $BwcMachine "1"
    StrCpy $BwcUninstallKey "${BWC_UNINSTALL_SUBKEY}"
  !endif
  ; A per-machine run first drops the runtime variables, so no child inherits them (installer.md M48). The folder the
  ; uninstaller was started for is then checked against the uninstall entry (M47) before anything runs or is deleted.
  StrCmp $BwcMachine "1" 0 +2
  Call un.BwcClearRuntimeEnvironment
  Call un.BwcApplyContext
  StrCmp $BwcMachine "1" 0 BwcUnInit_done
  Call un.BwcRequireElevated
  Call un.BwcCheckInstallLocation
  BwcUnInit_done:
FunctionEnd

; /PARENTPID=<n> is optional. When it is present it must be 1 to 10 decimal digits, or the setup
; stops before any change. An invalid value is never ignored.
Function BwcReadParentPid
  StrCpy $BwcParentPid ""
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/PARENTPID=" $R1
  IfErrors BwcPid_none 0
  StrCpy $R0 $R1
  StrLen $R3 $R0
  StrCmp $R3 "0" BwcPid_bad 0
  IntCmp $R3 10 BwcPid_lenok BwcPid_lenok BwcPid_bad
  BwcPid_lenok:
  !insertmacro BWC_CHARS_OK "pid" "$R0" "${BWC_DIGITS}" $R2
  StrCmp $R2 "1" BwcPid_ok BwcPid_bad
  BwcPid_ok:
  StrCpy $BwcParentPid $R0
  Return
  BwcPid_none:
  Return
  BwcPid_bad:
  Abort "/PARENTPID must be the numeric process id of the updater. Nothing was changed."
FunctionEnd

; Starts the Hub through BwcUpdate.exe --launch-hub, never directly: an elevated setup that started the Hub itself would
; leave an elevated Hub. The helper starts it at the shell's integrity level (a normal start when it is not elevated,
; which is the E2E per-user flavor). $R5 = the extra arguments ("" or "--update-result InstallerFailed"). The helper
; starts from %WINDIR%, never from a folder that a standard user can write. BwcUpdate.exe is a console program, so it
; runs through nsExec::Exec, which starts it hidden (a plain Exec would flash a console window). Its return value is
; popped into $R6 and ignored: a failed start never changes the exit code. A missing helper or Hub is a log line.
Function BwcLaunchHub
  IfFileExists "$INSTDIR\${BWC_UPDATER_EXE}" 0 BwcLaunch_missing
  IfFileExists "$INSTDIR\${BWC_HUB_EXE}" 0 BwcLaunch_missing
  SetOutPath "$WINDIR"
  nsExec::Exec '"$INSTDIR\${BWC_UPDATER_EXE}" --launch-hub $R5'
  Pop $R6
  Return
  BwcLaunch_missing:
  DetailPrint "The Hub was not started: ${BWC_UPDATER_EXE} or ${BWC_HUB_EXE} is missing from $INSTDIR."
FunctionEnd

; The finish page "Start" option.
Function BwcLaunchFromFinish
  StrCpy $R5 ""
  Call BwcLaunchHub
FunctionEnd

; /RELAUNCH starts the Hub after a successful silent or interactive install.
Function .onInstSuccess
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RELAUNCH" $R1
  IfErrors BwcRelaunch_skip 0
  StrCpy $R5 ""
  Call BwcLaunchHub
  BwcRelaunch_skip:
FunctionEnd

; A failed install removes its staging folder. After the swap has started, BwcRollback owns the
; cleanup, so the folders are left alone here. The staging folder is removed only when this run
; confirmed it is ours (BwcStageOwned). With /RELAUNCH the installed Hub starts again and is
; told the update failed. It is skipped when the Hub is missing (a rollback that did not finish).
Function .onInstFailed
  StrCmp $BwcCommitStarted "1" BwcFailed_relaunch 0
  StrCmp $BwcStageOwned "1" 0 BwcFailed_relaunch
  ; SetOutPath made the staging folder the working directory, and Windows cannot remove that folder.
  SetOutPath "$WINDIR"
  RMDir /r "$INSTDIR${BWC_STAGE_SUFFIX}"
  BwcFailed_relaunch:
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RELAUNCH" $R1
  IfErrors BwcFailed_done 0
  StrCpy $R5 "--update-result InstallerFailed"
  Call BwcLaunchHub
  BwcFailed_done:
FunctionEnd

; Close step for the install. It runs the BwcUpdate.exe in the staging folder, which is the version being installed
; and is a standalone NativeAOT exe (no .NET runtime beside it). The payload is staged before this runs.
; The target's apps are closed only when an earlier version is present (a first install has nothing to close).
Function BwcCloseRunningApps
  ; The updater that launched this setup exits right after it starts the setup. Wait for it first, so
  ; no file is replaced while its copy of BwcUpdate.exe is still in use. Then close the apps, excluding it.
  StrCpy $BwcCloseArgs ""
  StrCmp $BwcParentPid "" BwcCloseInst_args 0
  !insertmacro BWC_WAIT_PARENT_BODY "install" "$INSTDIR${BWC_STAGE_SUFFIX}\${BWC_UPDATER_EXE}"
  StrCpy $BwcCloseArgs "--exclude-pid $BwcParentPid"
  BwcCloseInst_args:
  IfFileExists "$INSTDIR\${BWC_UPDATER_EXE}" 0 BwcCloseInst_end
  !insertmacro BWC_CLOSE_APPS_BODY "install" "$INSTDIR${BWC_STAGE_SUFFIX}\${BWC_UPDATER_EXE}" "$INSTDIR"
  BwcCloseInst_end:
FunctionEnd

; Close step for the uninstall. The uninstaller cannot embed files, so it runs the BwcUpdate.exe
; that the installer placed in the install folder. If that copy is missing, the uninstall stops and
; nothing changes.
Function un.BwcCloseRunningApps
  IfFileExists "$INSTDIR\${BWC_UPDATER_EXE}" +2 0
  Abort "Stopped: ${BWC_UPDATER_EXE} is missing from $INSTDIR, so the running apps cannot be closed. Nothing was changed. Reinstall, then uninstall."
  StrCpy $BwcCloseArgs ""
  !insertmacro BWC_CLOSE_APPS_BODY "uninstall" "$INSTDIR\${BWC_UPDATER_EXE}" "$INSTDIR"
FunctionEnd

; ---------------------------------------------------------------------------
; Upgrade swap. The new version is staged in <install folder>.new (step 2), then moved in (step 3).
; The old version's listed files go to <install folder>.old first, so a failure can put them back.
; ---------------------------------------------------------------------------

; Moves the new payload into the install folder. Any failed move jumps to BwcCommit_rollback.
Function BwcCommit
  StrCpy $BwcCommitStarted "1"
  StrCpy $BwcMovedIn "0"
  CreateDirectory "$INSTDIR"
  CreateDirectory "$INSTDIR${BWC_BACKUP_SUFFIX}"
  !insertmacro BWC_MOVE_OUT_LISTED_BODY "commit"
  StrCmp $BwcOutFailed "1" BwcCommit_rollback 0
  IfFileExists "$INSTDIR\${BWC_UNINSTALLER}" BwcCommit_moveUninst BwcCommit_noUninst
  BwcCommit_moveUninst:
  ClearErrors
  Rename "$INSTDIR\${BWC_UNINSTALLER}" "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_UNINSTALLER}"
  IfErrors BwcCommit_rollback 0
  BwcCommit_noUninst:
  StrCpy $BwcMovedIn "1"
  !insertmacro BWC_PAYLOAD_MOVE_IN
  Delete "$INSTDIR\LICENSE"
  ClearErrors
  Rename "$INSTDIR${BWC_STAGE_SUFFIX}\LICENSE" "$INSTDIR\LICENSE"
  IfErrors BwcCommit_rollback 0
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  ClearErrors
  Rename "$INSTDIR${BWC_STAGE_SUFFIX}\THIRD-PARTY-NOTICES.txt" "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  IfErrors BwcCommit_rollback 0
  Delete "$INSTDIR\${BWC_MANIFEST}"
  ClearErrors
  Rename "$INSTDIR${BWC_STAGE_SUFFIX}\${BWC_MANIFEST}" "$INSTDIR\${BWC_MANIFEST}"
  IfErrors BwcCommit_rollback 0
  Delete "$INSTDIR\${BWC_UNINSTALLER}"
  ClearErrors
  Rename "$INSTDIR${BWC_STAGE_SUFFIX}\${BWC_UNINSTALLER}" "$INSTDIR\${BWC_UNINSTALLER}"
  IfErrors BwcCommit_rollback 0
  Return
  BwcCommit_rollback:
  Call BwcRollback
FunctionEnd

; Puts the previous version back and removes what this run added. It always ends in Abort. The
; staging and backup folders are removed only when every file is back in place. BWC_PAYLOAD_MOVE_IN creates the payload
; folders, so after the file deletes the empty ones are removed too (RMDir without /r: a folder with anything else stays).
; When no previous install is in place afterwards (no installed-files.txt in the install folder: the new one was deleted
; and none came back from .old), this was a first install and the then-empty install folder goes as well (installer.md M60).
Function BwcRollback
  DetailPrint "The install failed. Rolling it back."
  SetOutPath "$WINDIR"
  StrCmp $BwcMovedIn "1" BwcRb_removeNew BwcRb_restore
  BwcRb_removeNew:
  !insertmacro BWC_PAYLOAD_DELETE
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\${BWC_MANIFEST}"
  Delete "$INSTDIR\${BWC_UNINSTALLER}"
  !insertmacro BWC_PAYLOAD_RMDIRS
  BwcRb_restore:
  !insertmacro BWC_RESTORE_LISTED_BODY "rollback"
  StrCmp $BwcRestoreFailed "1" BwcRb_keep 0
  IfFileExists "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_MANIFEST}" BwcRb_manifest BwcRb_noManifest
  BwcRb_manifest:
  Delete "$INSTDIR\${BWC_MANIFEST}"
  ClearErrors
  Rename "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_MANIFEST}" "$INSTDIR\${BWC_MANIFEST}"
  IfErrors BwcRb_keep 0
  BwcRb_noManifest:
  IfFileExists "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_UNINSTALLER}" BwcRb_uninst BwcRb_noUninst
  BwcRb_uninst:
  Delete "$INSTDIR\${BWC_UNINSTALLER}"
  ClearErrors
  Rename "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_UNINSTALLER}" "$INSTDIR\${BWC_UNINSTALLER}"
  IfErrors BwcRb_keep 0
  BwcRb_noUninst:
  RMDir /r "$INSTDIR${BWC_STAGE_SUFFIX}"
  RMDir /r "$INSTDIR${BWC_BACKUP_SUFFIX}"
  IfFileExists "$INSTDIR\${BWC_MANIFEST}" BwcRb_wasUpgrade 0
  RMDir "$INSTDIR"
  Abort "The first install failed and was rolled back. Nothing was installed and nothing else was changed."
  BwcRb_wasUpgrade:
  Abort "The upgrade was rolled back. The previous version is still installed. Nothing else was changed."
  BwcRb_keep:
  Abort "The upgrade failed and the rollback did not finish. The previous files are in $INSTDIR${BWC_BACKUP_SUFFIX}. Restore them by hand, see docs/installer.md, then run the installer again."
FunctionEnd

; A shortcut or the uninstall entry could not be written. The install is rolled back as a whole (BwcRollback ends in
; Abort). A first install also loses what was written here. An upgrade keeps the previous entry and shortcuts: they
; have the same names and point at the files that the rollback puts back.
Function BwcWriteFailed
  DetailPrint "A shortcut or the uninstall entry could not be written (see above)."
  IfFileExists "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_MANIFEST}" BwcWf_upgrade 0
  Delete "$BwcShortcutDir\${BWC_HUB_SHORTCUT}"
  Delete "$BwcShortcutDir\${BWC_REGEDIT_SHORTCUT}"
  Delete "$BwcShortcutDir\${BWC_GPEDIT_SHORTCUT}"
  DeleteRegKey SHCTX "$BwcUninstallKey"
  BwcWf_upgrade:
  Call BwcRollback
FunctionEnd

; Runs after the registry and shortcuts are written. The upgrade is complete, so the backup of the
; previous version is removed. The marker is written first: if the backup stays (a locked file), the
; next run finds the marker and removes it. A failed cleanup is a warning, not a rollback, because
; the new version is already in place.
Function BwcFinishUpgrade
  ClearErrors
  FileOpen $R8 "$INSTDIR\${BWC_CLEANUP_MARKER}" w
  IfErrors BwcFinish_nomarker 0
  FileWrite $R8 "The upgrade finished. The backup folder can be removed.$\r$\n"
  FileClose $R8
  RMDir /r "$INSTDIR${BWC_BACKUP_SUFFIX}"
  IfFileExists "$INSTDIR${BWC_BACKUP_SUFFIX}" BwcFinish_keep 0
  Delete "$INSTDIR\${BWC_CLEANUP_MARKER}"
  Goto BwcFinish_stage
  BwcFinish_keep:
  DetailPrint "WARNING: could not remove $INSTDIR${BWC_BACKUP_SUFFIX}. The next run removes it."
  Goto BwcFinish_stage
  BwcFinish_nomarker:
  DetailPrint "WARNING: could not write the cleanup marker. $INSTDIR${BWC_BACKUP_SUFFIX} is kept."
  BwcFinish_stage:
  SetOutPath "$WINDIR"
  RMDir /r "$INSTDIR${BWC_STAGE_SUFFIX}"
FunctionEnd

; Leftover backup from an earlier upgrade. The marker is written before the backup is removed, so both
; present means that upgrade finished and the backup is removed. Without the marker the folder may hold
; the only copy of the previous version, so the install aborts before any change and names it.
Function BwcClearFinishedBackup
  IfFileExists "$INSTDIR${BWC_BACKUP_SUFFIX}" BwcLeft_found BwcLeft_clear
  BwcLeft_found:
  IfFileExists "$INSTDIR\${BWC_CLEANUP_MARKER}" BwcLeft_finished BwcLeft_leftover
  BwcLeft_leftover:
  Abort "A folder from an earlier failed upgrade is in the way: $INSTDIR${BWC_BACKUP_SUFFIX}. Check it, remove it, then run the installer again. Nothing was changed."
  BwcLeft_finished:
  DetailPrint "Removing the backup of an earlier upgrade that finished: $INSTDIR${BWC_BACKUP_SUFFIX}"
  RMDir /r "$INSTDIR${BWC_BACKUP_SUFFIX}"
  IfFileExists "$INSTDIR${BWC_BACKUP_SUFFIX}" BwcLeft_stuck BwcLeft_clear
  BwcLeft_stuck:
  Abort "Stopped: $INSTDIR${BWC_BACKUP_SUFFIX} could not be removed, probably because a file in it is in use. Close that program, then run the installer again. Nothing was changed."
  BwcLeft_clear:
  Delete "$INSTDIR\${BWC_CLEANUP_MARKER}"
FunctionEnd

; ---------------------------------------------------------------------------
; Install
; ---------------------------------------------------------------------------

Section "Better Windows Config" SecMain
  SectionIn RO

  ; The registry view and shell folders were applied in .onInit. Applying them here too costs nothing and keeps the
  ; section independent of that call.
  Call BwcApplyContext
  StrCmp $BwcHintNote "" +2 0
  DetailPrint "$BwcHintNote"

  ; 0. Abort before any change when <install folder>.new is a file or a folder that is not a staging folder.
  ;    A per-machine run first removes what a crashed update left in <install folder>.upd (only 32-hex folders, never
  ;    through a link, never the folder this setup runs from).
  StrCmp $BwcMachine "1" 0 BwcSec_noUpd
  Call BwcClearStaleProtected
  BwcSec_noUpd:
  Call BwcCheckStageFree
  StrCpy $BwcStageOwned "1"

  ; 1. Remove the backup of an earlier upgrade that finished. Abort before any change when a backup
  ;    without the marker is in the way: it may hold the only copy of the old files.
  Call BwcClearFinishedBackup

  ; 2. Stage the new version in <install folder>.new. The install folder is not touched in this step.
  ;    Step 0 confirmed the folder is absent, empty, or this product's staging folder.
  RMDir /r "$INSTDIR${BWC_STAGE_SUFFIX}"
  CreateDirectory "$INSTDIR${BWC_STAGE_SUFFIX}"
  FileOpen $R9 "$INSTDIR${BWC_STAGE_SUFFIX}\${BWC_MANIFEST}" w
  !insertmacro BWC_PAYLOAD_FILES "$INSTDIR${BWC_STAGE_SUFFIX}"

  SetOutPath "$INSTDIR${BWC_STAGE_SUFFIX}"
  FileWrite $R9 "LICENSE$\r$\n"
  File "/oname=LICENSE" "${BWC_LICENSE}"
  FileWrite $R9 "THIRD-PARTY-NOTICES.txt$\r$\n"
  File "/oname=THIRD-PARTY-NOTICES.txt" "${BWC_NOTICES}"
  FileClose $R9
  WriteUninstaller "$INSTDIR${BWC_STAGE_SUFFIX}\${BWC_UNINSTALLER}"

  ; 3. Stop BWC processes that run from the install folder, using the staged BwcUpdate.exe. Abort
  ;    before any installed file changes when they stay. .onInstFailed removes the staging folder.
  Call BwcCloseRunningApps

  ; 4. Swap the files in. A failure restores the previous version (see BwcCommit and BwcRollback).
  Call BwcCommit

  ; 5. Shortcuts, the uninstall entry, and the AppUserModelID the apps set at startup. The shortcuts are all-users in a
  ;    per-machine run. A shortcut or entry that cannot be written is fatal: the install is rolled back as a whole
  ;    (BwcWriteFailed). Only the AUMID write is non-fatal: the shortcut still works without it.
  ClearErrors
  CreateDirectory "$BwcShortcutDir"
  IfErrors BwcSec_writeFailed 0
  CreateShortCut "$BwcShortcutDir\${BWC_HUB_SHORTCUT}" "$INSTDIR\${BWC_HUB_EXE}" "" "$INSTDIR\${BWC_HUB_EXE}" 0
  IfErrors BwcSec_writeFailed 0
  CreateShortCut "$BwcShortcutDir\${BWC_REGEDIT_SHORTCUT}" "$INSTDIR\${BWC_REGEDIT_EXE}" "" "$INSTDIR\${BWC_REGEDIT_EXE}" 0
  IfErrors BwcSec_writeFailed 0
  CreateShortCut "$BwcShortcutDir\${BWC_GPEDIT_SHORTCUT}" "$INSTDIR\${BWC_GPEDIT_EXE}" "" "$INSTDIR\${BWC_GPEDIT_EXE}" 0
  IfErrors BwcSec_writeFailed 0

  WriteRegStr SHCTX "$BwcUninstallKey" "DisplayName" "${BWC_PRODUCT}"
  WriteRegStr SHCTX "$BwcUninstallKey" "DisplayVersion" "${BWC_VERSION}"
  WriteRegStr SHCTX "$BwcUninstallKey" "Publisher" "${BWC_PUBLISHER}"
  WriteRegStr SHCTX "$BwcUninstallKey" "DisplayIcon" "$INSTDIR\${BWC_HUB_EXE}"
  WriteRegStr SHCTX "$BwcUninstallKey" "InstallLocation" "$INSTDIR"
  ; _?=$INSTDIR makes the uninstaller run in place from the install folder (admin-only in a per-machine install)
  ; instead of from a copy in %TEMP%, which an elevated process must not run (installer.md M37). The uninstaller then
  ; cannot delete itself: Section Uninstall removes it and the folder at the next restart when it cannot (M38).
  WriteRegStr SHCTX "$BwcUninstallKey" "UninstallString" '"$INSTDIR\${BWC_UNINSTALLER}" _?=$INSTDIR'
  WriteRegStr SHCTX "$BwcUninstallKey" "QuietUninstallString" '"$INSTDIR\${BWC_UNINSTALLER}" /S _?=$INSTDIR'
  WriteRegDWORD SHCTX "$BwcUninstallKey" "EstimatedSize" ${BWC_ESTIMATED_KB}
  WriteRegDWORD SHCTX "$BwcUninstallKey" "NoModify" 1
  WriteRegDWORD SHCTX "$BwcUninstallKey" "NoRepair" 1
  !ifdef BWC_E2E
    ; Not a standard value, E2E only. The E2E uninstaller reads it to find the shortcuts it created. The ship
    ; uninstaller knows its shortcut folder (the all-users Start menu).
    WriteRegStr SHCTX "$BwcUninstallKey" "ShortcutDir" "$BwcShortcutDir"
  !endif
  IfErrors BwcSec_writeFailed 0

  ; The AUMID is written by the installed BwcUpdate.exe (--set-aumid), never by PowerShell (installer.md M45). The working
  ; folder is moved out of the staging folder first, so the child never inherits a folder the install is about to remove.
  SetOutPath "$WINDIR"
  !insertmacro BWC_SET_AUMID_BODY "$BwcShortcutDir\${BWC_HUB_SHORTCUT}" "${BWC_HUB_AUMID}"
  !insertmacro BWC_SET_AUMID_BODY "$BwcShortcutDir\${BWC_REGEDIT_SHORTCUT}" "${BWC_REGEDIT_AUMID}"
  !insertmacro BWC_SET_AUMID_BODY "$BwcShortcutDir\${BWC_GPEDIT_SHORTCUT}" "${BWC_GPEDIT_AUMID}"
  Goto BwcSec_written
  BwcSec_writeFailed:
  Call BwcWriteFailed
  BwcSec_written:

  ; 6. The upgrade is complete. Remove the backup of the previous version and the staging folder.
  Call BwcFinishUpgrade
SectionEnd

; ---------------------------------------------------------------------------
; Uninstall
; ---------------------------------------------------------------------------

Section "Uninstall"
  Call un.BwcApplyContext

  ; Stop the apps first. Abort, with nothing removed, when they stay.
  Call un.BwcCloseRunningApps

  ; Shortcuts. Ship: the three all-users shortcuts, in the folder the installer used ($SMPROGRAMS with the all-users
  ; context). E2E: the folder that the install recorded; the /MACHINE sandbox also removes its own Start menu folder
  ; when it is empty.
  !ifdef BWC_E2E
    ReadRegStr $R0 SHCTX "$BwcUninstallKey" "ShortcutDir"
  !else
    StrCpy $R0 "$SMPROGRAMS"
  !endif
  StrCmp $R0 "" BwcUn_noShortcuts 0
  Delete "$R0\${BWC_HUB_SHORTCUT}"
  Delete "$R0\${BWC_REGEDIT_SHORTCUT}"
  Delete "$R0\${BWC_GPEDIT_SHORTCUT}"
  !ifdef BWC_E2E
    StrCmp $BwcMachine "1" 0 BwcUn_noShortcuts
    RMDir "$R0"
  !endif
  BwcUn_noShortcuts:

  ; Payload. The lists come from this version's build, and the uninstaller is written by the same
  ; version, so they match what was installed. A file left by an older version was removed by the
  ; upgrade through the manifest.
  ; The files BwcUpdate.exe loaded for the close step can stay locked for a moment after it exits (x64
  ; emulation on ARM64 caches them), so the deletes are retried for up to 10 s. Delete sets the error flag
  ; only for a file that exists and could not be removed.
  StrCpy $R9 0
  BwcUn_deleteFiles:
  ClearErrors
  !insertmacro BWC_PAYLOAD_DELETE
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\${BWC_MANIFEST}"
  Delete "$INSTDIR\${BWC_CLEANUP_MARKER}"
  IfErrors 0 BwcUn_filesDeleted
  IntOp $R9 $R9 + 1
  IntCmp $R9 20 BwcUn_filesDeleted 0 BwcUn_filesDeleted
  Sleep 500
  Goto BwcUn_deleteFiles
  BwcUn_filesDeleted:
  !insertmacro BWC_PAYLOAD_RMDIRS

  ; The folders of the elevated updater: the protected copies of the installer in <install folder>.upd (only 32-hex
  ; folders, never through a link), then the .upd folder itself when it is empty. The staging folder in the user's
  ; %TEMP% is not touched here: an elevated recursive delete in a user-writable folder is not safe, and the Hub
  ; prunes its own staging. No user's data folder is touched either.
  StrCmp $BwcMachine "1" 0 BwcUn_noUpd
  Call un.BwcRemoveProtected
  RMDir "$INSTDIR${BWC_PROTECTED_SUFFIX}"
  BwcUn_noUpd:

  ; The imported policy templates in %ProgramData%\BetterWindowsConfig\PolicyDefinitions go, with the folders they leave
  ; empty. The policy backups (gpedit\backup) stay. The step is skipped when a link is on the path (installer.md M62 to M65).
  ; The E2E flavor reads its stand-in folder from BWC_E2E_INSTALLER_PROGRAMDATA and skips the step without it.
  Call un.BwcRemoveTemplates

  ; Remove the folder only when it is empty. User files in it stay. A copy of the uninstaller (no _?=) deletes the
  ; original at once. An uninstaller that runs in place cannot delete itself or the folder it runs from, so the deletes
  ; are repeated with /REBOOTOK, and only then (M38). The working folder moves out of the install folder first, so the
  ; uninstaller's own current directory does not hold it.
  SetOutPath "$WINDIR"
  ClearErrors
  Delete "$INSTDIR\${BWC_UNINSTALLER}"
  IfErrors 0 BwcUn_uninstallerGone
  Delete /REBOOTOK "$INSTDIR\${BWC_UNINSTALLER}"
  BwcUn_uninstallerGone:
  ClearErrors
  RMDir "$INSTDIR"
  IfErrors 0 BwcUn_folderDone
  RMDir /REBOOTOK "$INSTDIR"
  BwcUn_folderDone:
  !ifdef BWC_E2E
    StrCmp $BwcMachine "1" 0 BwcUn_noE2eRoot
    RMDir "$PROGRAMFILES64\${BWC_E2E_FOLDER}"
    BwcUn_noE2eRoot:
  !endif

  ; The uninstall entry is removed only when its InstallLocation is this folder (case-insensitive). An
  ; entry that names another folder belongs to another install, so it stays.
  ReadRegStr $R0 SHCTX "$BwcUninstallKey" "InstallLocation"
  System::Call 'kernel32::lstrcmpiW(t "$R0", t "$INSTDIR") i .R1'
  StrCmp $R1 "0" BwcUn_keyOwn 0
  DetailPrint "Kept the uninstall entry $BwcUninstallKey: its InstallLocation is $R0, not $INSTDIR."
  Goto BwcUn_keyDone
  BwcUn_keyOwn:
  DeleteRegKey SHCTX "$BwcUninstallKey"
  !ifdef BWC_E2E
    ; The sandbox parents go when they are empty, so a finished run leaves nothing under BWC-E2E\<RUNID>.
    DeleteRegKey /ifempty SHCTX "${BWC_E2E_ROOT}\$BwcRunId\Uninstall"
    DeleteRegKey /ifempty SHCTX "${BWC_E2E_ROOT}\$BwcRunId"
  !endif
  BwcUn_keyDone:
SectionEnd
