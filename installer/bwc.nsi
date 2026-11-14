; Better Windows Config installer (NSIS 3.10 or newer, Unicode).
;
; Built by scripts/build-installer.js. Compile by hand only for debugging; the script expects every
; define below. Failure modes and intended behavior: docs/failure-modes/installer.md.
;
; Command line:
;   (none)             interactive install, per user, to %LOCALAPPDATA%\Programs\Better Windows Config
;   /S                 silent install or uninstall
;   /D=<dir>           install folder; must be the last switch and unquoted (NSIS rule)
;   /RELAUNCH          start the Hub when the run ends: the new Hub after success, or the installed Hub
;                      with --update-result InstallerFailed after failure. Without it no Hub starts.
;   /PARENTPID=<n>     the updater that launched this setup. The setup waits for it to exit, then
;                      excludes it from the close step.
;   /RUNID=<id>        E2E builds only (required). Uninstall key under HKCU\Software\BWC-E2E\<id>.
;   /SHORTCUTS=<dir>   E2E builds only (required). Folder for the two Start menu shortcuts.
;
; Defines (all passed by scripts/build-installer.js with /D):
;   BWC_VERSION        display version, for example 0.2.0-beta.3
;   BWC_FILE_VERSION   four-part numeric version for VIProductVersion, for example 0.2.0.3
;   BWC_RID            win-x64 or win-arm64
;   BWC_ARCH           x64 or arm64
;   BWC_OUTPUT         absolute path of the setup EXE to write
;   BWC_PAYLOAD_NSH    generated file with the payload macros (file list, moves, deletes, rmdirs)
;   BWC_SCRIPTS_DIR    installer\scripts, which holds the AUMID helper
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
!ifndef BWC_SCRIPTS_DIR
  !error "BWC_SCRIPTS_DIR is not defined. Run scripts/build-installer.js."
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
RequestExecutionLevel user
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
; Per-user default. /D= overrides it. No per-machine mode in this version (see docs/installer.md).
InstallDir "$LOCALAPPDATA\Programs\${BWC_PRODUCT}"
; The release installer finds the folder of an earlier install through its uninstall key. The E2E
; flavor never reads the real key, so an E2E run without /D= cannot pick up a real install.
!ifndef BWC_E2E
  InstallDirRegKey HKCU "${BWC_UNINSTALL_SUBKEY}" "InstallLocation"
!endif
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
!define MUI_FINISHPAGE_RUN "$INSTDIR\${BWC_HUB_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${BWC_PRODUCT}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${BWC_LICENSE}"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------------------------
; Initialization
; ---------------------------------------------------------------------------

Function .onInit
  SetShellVarContext current
  Call BwcCheckRawDir
  Call BwcTrimInstDir
  Call BwcCheckHost
  Call BwcCheckArchitecture
  ; Silent installs never reach .onVerifyInstDir, so the folder is checked here. Interactive runs check
  ; the folder when the directory page is left.
  IfSilent 0 +2
  Call BwcCheckInstDir
  Call BwcFindPowerShell
  Call BwcReadParentPid
  !ifdef BWC_E2E
    Call BwcReadE2eSwitches
  !else
    StrCpy $BwcUninstallKey "${BWC_UNINSTALL_SUBKEY}"
    StrCpy $BwcShortcutDir "$SMPROGRAMS"
  !endif
FunctionEnd

; Interactive only. Runs when the user leaves the directory page, so a refused folder keeps the page open.
Function .onVerifyInstDir
  Call BwcTrimInstDir
  Call BwcCheckInstDir
FunctionEnd

Function un.onInit
  SetShellVarContext current
  !ifdef BWC_E2E
    Call un.BwcReadE2eSwitches
  !else
    StrCpy $BwcUninstallKey "${BWC_UNINSTALL_SUBKEY}"
  !endif
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

; /RELAUNCH starts the Hub after a successful silent or interactive install. A failed launch is
; not an installer failure, so Exec's result is ignored.
Function .onInstSuccess
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RELAUNCH" $R1
  IfErrors BwcRelaunch_skip 0
  Exec '"$INSTDIR\${BWC_HUB_EXE}"'
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
  SetOutPath "$TEMP"
  RMDir /r "$INSTDIR${BWC_STAGE_SUFFIX}"
  BwcFailed_relaunch:
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RELAUNCH" $R1
  IfErrors BwcFailed_done 0
  IfFileExists "$INSTDIR\${BWC_HUB_EXE}" 0 BwcFailed_done
  Exec '"$INSTDIR\${BWC_HUB_EXE}" --update-result InstallerFailed'
  BwcFailed_done:
FunctionEnd

; Close step for the install. It runs the BwcUpdate.exe in the staging folder, which has its .NET
; runtime beside it and is the version being installed. The payload is staged before this runs.
; It runs only when an earlier version is present, because a first install has nothing to close.
Function BwcCloseRunningApps
  IfFileExists "$INSTDIR\${BWC_UPDATER_EXE}" BwcCloseInst_update BwcCloseInst_first
  BwcCloseInst_first:
  Return
  BwcCloseInst_update:
  ; The updater that launched this setup exits right after it starts the setup. Wait for it first, so
  ; no file is replaced while its copy of BwcUpdate.exe is still in use. Then close the apps, excluding it.
  StrCmp $BwcParentPid "" BwcCloseInst_noparent 0
  !insertmacro BWC_WAIT_PARENT_BODY "install" "$INSTDIR${BWC_STAGE_SUFFIX}\${BWC_UPDATER_EXE}"
  BwcCloseInst_noparent:
  StrCpy $BwcCloseArgs ""
  StrCmp $BwcParentPid "" +2 0
  StrCpy $BwcCloseArgs "--exclude-pid $BwcParentPid"
  !insertmacro BWC_CLOSE_APPS_BODY "install" "$INSTDIR${BWC_STAGE_SUFFIX}\${BWC_UPDATER_EXE}"
FunctionEnd

; Close step for the uninstall. The uninstaller cannot embed files, so it runs the BwcUpdate.exe
; that the installer placed in the install folder. If that copy is missing, the uninstall stops and
; nothing changes.
Function un.BwcCloseRunningApps
  IfFileExists "$INSTDIR\${BWC_UPDATER_EXE}" +2 0
  Abort "Stopped: ${BWC_UPDATER_EXE} is missing from $INSTDIR, so the running apps cannot be closed. Nothing was changed. Reinstall, then uninstall."
  StrCpy $BwcCloseArgs ""
  !insertmacro BWC_CLOSE_APPS_BODY "uninstall" "$INSTDIR\${BWC_UPDATER_EXE}"
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
; staging and backup folders are removed only when every file is back in place.
Function BwcRollback
  DetailPrint "The upgrade failed. Putting the previous version back."
  SetOutPath "$TEMP"
  StrCmp $BwcMovedIn "1" BwcRb_removeNew BwcRb_restore
  BwcRb_removeNew:
  !insertmacro BWC_PAYLOAD_DELETE
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\${BWC_MANIFEST}"
  Delete "$INSTDIR\${BWC_UNINSTALLER}"
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
  Abort "The upgrade was rolled back. The previous version is still installed. Nothing else was changed."
  BwcRb_keep:
  Abort "The upgrade failed and the rollback did not finish. The previous files are in $INSTDIR${BWC_BACKUP_SUFFIX}. Restore them by hand, see docs/installer.md, then run the installer again."
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
  SetOutPath "$TEMP"
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
  SetShellVarContext current

  ; 0. Abort before any change when <install folder>.new is a file or a folder that is not a staging folder.
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

  ; 5. Uninstall entry, shortcuts, and the AppUserModelID the apps set at startup. The AUMID write is
  ;    non-fatal: the shortcut still works without it.
  WriteRegStr HKCU "$BwcUninstallKey" "DisplayName" "${BWC_PRODUCT}"
  WriteRegStr HKCU "$BwcUninstallKey" "DisplayVersion" "${BWC_VERSION}"
  WriteRegStr HKCU "$BwcUninstallKey" "Publisher" "${BWC_PUBLISHER}"
  WriteRegStr HKCU "$BwcUninstallKey" "DisplayIcon" "$INSTDIR\${BWC_HUB_EXE}"
  WriteRegStr HKCU "$BwcUninstallKey" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "$BwcUninstallKey" "UninstallString" '"$INSTDIR\${BWC_UNINSTALLER}"'
  WriteRegStr HKCU "$BwcUninstallKey" "QuietUninstallString" '"$INSTDIR\${BWC_UNINSTALLER}" /S'
  WriteRegDWORD HKCU "$BwcUninstallKey" "EstimatedSize" ${BWC_ESTIMATED_KB}
  WriteRegDWORD HKCU "$BwcUninstallKey" "NoModify" 1
  WriteRegDWORD HKCU "$BwcUninstallKey" "NoRepair" 1
  ; Not a standard value. The uninstaller reads it to find the shortcuts it created.
  WriteRegStr HKCU "$BwcUninstallKey" "ShortcutDir" "$BwcShortcutDir"

  CreateDirectory "$BwcShortcutDir"
  CreateShortCut "$BwcShortcutDir\${BWC_HUB_SHORTCUT}" "$INSTDIR\${BWC_HUB_EXE}" "" "$INSTDIR\${BWC_HUB_EXE}" 0
  CreateShortCut "$BwcShortcutDir\${BWC_REGEDIT_SHORTCUT}" "$INSTDIR\${BWC_REGEDIT_EXE}" "" "$INSTDIR\${BWC_REGEDIT_EXE}" 0
  InitPluginsDir
  File "/oname=$PLUGINSDIR\${BWC_AUMID_HELPER}" "${BWC_SCRIPTS_DIR}\${BWC_AUMID_HELPER}"
  !insertmacro BWC_SET_AUMID_BODY "$PLUGINSDIR\${BWC_AUMID_HELPER}" "$BwcShortcutDir\${BWC_HUB_SHORTCUT}" "${BWC_HUB_AUMID}"
  !insertmacro BWC_SET_AUMID_BODY "$PLUGINSDIR\${BWC_AUMID_HELPER}" "$BwcShortcutDir\${BWC_REGEDIT_SHORTCUT}" "${BWC_REGEDIT_AUMID}"

  ; 6. The upgrade is complete. Remove the backup of the previous version and the staging folder.
  Call BwcFinishUpgrade
SectionEnd

; ---------------------------------------------------------------------------
; Uninstall
; ---------------------------------------------------------------------------

Section "Uninstall"
  SetShellVarContext current

  ; Stop the apps first. Abort, with nothing removed, when they stay.
  Call un.BwcCloseRunningApps

  ; Shortcuts recorded at install time. Release builds record $SMPROGRAMS.
  ReadRegStr $R0 HKCU "$BwcUninstallKey" "ShortcutDir"
  StrCmp $R0 "" BwcUn_noShortcuts 0
  Delete "$R0\${BWC_HUB_SHORTCUT}"
  Delete "$R0\${BWC_REGEDIT_SHORTCUT}"
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
  ; Helper from versions before 0.2.0. It is removed when present.
  Delete "$INSTDIR\${BWC_LEGACY_TOOLS_DIR}\${BWC_LEGACY_CLOSE_HELPER}"
  IfErrors 0 BwcUn_filesDeleted
  IntOp $R9 $R9 + 1
  IntCmp $R9 20 BwcUn_filesDeleted 0 BwcUn_filesDeleted
  Sleep 500
  Goto BwcUn_deleteFiles
  BwcUn_filesDeleted:
  RMDir "$INSTDIR\${BWC_LEGACY_TOOLS_DIR}"
  !insertmacro BWC_PAYLOAD_RMDIRS

  ; The updater's download and staging folder. A file still in use stays and does not stop the uninstall.
  ; The E2E flavor never touches the real %TEMP% folder of the updater.
  !ifndef BWC_E2E
    RMDir /r "$TEMP\BWC-Update"
  !endif

  ; Remove the folder only when it is empty. User files in it stay.
  Delete "$INSTDIR\${BWC_UNINSTALLER}"
  RMDir "$INSTDIR"

  ; The uninstall entry is removed only when its InstallLocation is this folder (case-insensitive). An
  ; entry that names another folder belongs to another install, so it stays.
  ReadRegStr $R0 HKCU "$BwcUninstallKey" "InstallLocation"
  System::Call 'kernel32::lstrcmpiW(t "$R0", t "$INSTDIR") i .R1'
  StrCmp $R1 "0" BwcUn_keyOwn 0
  DetailPrint "Kept the uninstall entry $BwcUninstallKey: its InstallLocation is $R0, not $INSTDIR."
  Goto BwcUn_keyDone
  BwcUn_keyOwn:
  DeleteRegKey HKCU "$BwcUninstallKey"
  BwcUn_keyDone:
SectionEnd
