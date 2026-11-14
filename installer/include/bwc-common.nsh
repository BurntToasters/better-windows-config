; Shared identity, host checks, and reusable macros for installer/bwc.nsi.
;
; Names here are part of the contract. The updater looks for BetterWindowsConfig.exe and
; BwcUpdate.exe by name, the Hub reads THIRD-PARTY-NOTICES.txt from its own folder, and the
; E2E harness looks for the uninstall key and shortcuts by these exact names.
; Do not rename anything here without updating docs/installer.md and the E2E harness.
;
; Every "StrCmp" and "IfErrors" below passes its fall-through label explicitly as 0, so the
; reader never has to remember which NSIS version accepts a short form.

!define BWC_PRODUCT "Better Windows Config"
!define BWC_PUBLISHER "Better Windows Config"
!define BWC_HUB_EXE "BetterWindowsConfig.exe"
!define BWC_REGEDIT_EXE "BwcRegedit.exe"
!define BWC_UPDATER_EXE "BwcUpdate.exe"
!define BWC_HUB_SHORTCUT "Better Windows Config.lnk"
!define BWC_REGEDIT_SHORTCUT "Registry Editor (BWC).lnk"
; Must match Bwc.Core AppIdentity (HubAppUserModelId, RegeditAppUserModelId).
!define BWC_HUB_AUMID "BetterWindowsConfig.Hub"
!define BWC_REGEDIT_AUMID "BetterWindowsConfig.Regedit"
; Per-user uninstall key for ship builds. E2E builds use a key under BWC-E2E\<RUNID>.
!define BWC_UNINSTALL_SUBKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterWindowsConfig"
!define BWC_E2E_ROOT "Software\BWC-E2E"
!define BWC_MANIFEST "installed-files.txt"
!define BWC_UNINSTALLER "Uninstall.exe"
; Upgrade staging and backup folders sit next to the install folder. They hold the only copy of
; the previous version until an upgrade succeeds, so they are never removed while they may be needed.
!define BWC_STAGE_SUFFIX ".new"
!define BWC_BACKUP_SUFFIX ".old"
; Written in the install folder before the backup is removed. Both present at the next run means the
; upgrade finished, so the backup is removed. The uninstaller deletes it. Not in the payload.
!define BWC_CLEANUP_MARKER "upgrade-cleanup-pending"
; Folder from versions before 0.2.0. Its PowerShell helper is removed on upgrade and uninstall.
!define BWC_LEGACY_TOOLS_DIR "bwc-tools"
!define BWC_LEGACY_CLOSE_HELPER "bwc-close-apps.ps1"
!define BWC_AUMID_HELPER "bwc-set-aumid.ps1"
; Windows 10 2004 (19041) is TargetPlatformMinVersion in build/Bwc.WinUIApp.props.
!define BWC_MIN_BUILD 19041
!define BWC_RUNID_CHARS "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
!define BWC_DIGITS "0123456789"

; Runtime state shared by install and uninstall. Set in .onInit and un.onInit.
Var BwcRunId
Var BwcShortcutDir
Var BwcUninstallKey
Var BwcParentPid
Var BwcPowerShell
Var BwcCloseArgs
Var BwcCommitStarted
Var BwcMovedIn
Var BwcOutFailed
Var BwcRestoreFailed
; "1" once the section has confirmed that <install folder>.new is free to use (absent, empty, or ours).
Var BwcStageOwned

; ---------------------------------------------------------------------------
; Host checks (install only). Both run in .onInit, before any file is touched.
; MessageBox is skipped in silent mode because NSIS would otherwise show it and block /S.
; ---------------------------------------------------------------------------

Function BwcCheckHost
  ; CurrentBuildNumber is a string such as "26200". A failed read is empty, which compares as 0
  ; and refuses. IntCmp takes its labels as equal, less, greater: less refuses, the others go on.
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  IntCmp $0 ${BWC_MIN_BUILD} BwcHost_ok BwcHost_old BwcHost_ok
  BwcHost_old:
  IfSilent +2 0
  MessageBox MB_OK|MB_ICONSTOP "${BWC_PRODUCT} needs Windows 10 build ${BWC_MIN_BUILD} or later. This PC reports build $0."
  Abort "Windows build too old or unreadable: $0"
  BwcHost_ok:
FunctionEnd

; The native machine comes from IsWow64Process2. GetNativeSystemInfo is not used: it reports AMD64 to
; the emulated 32-bit setup stub on an ARM64 PC. IMAGE_FILE_MACHINE_AMD64 = 34404, ARM64 = 43620.
; The ARM64 installer needs an ARM64 host. The x64 installer also runs on ARM64, under emulation.
; A failed call aborts rather than guessing.
Function BwcCheckArchitecture
  System::Call 'kernel32::GetCurrentProcess() p .r1'
  System::Call 'kernel32::IsWow64Process2(p r1, *i .r2, *i .r3) i .r0'
  StrCmp $0 "0" BwcArch_unreadable 0
  StrCmp "${BWC_ARCH}" "arm64" 0 BwcArch_x64
  StrCmp $3 "43620" BwcArch_ok BwcArch_bad
  BwcArch_x64:
  StrCmp $3 "34404" BwcArch_ok 0
  StrCmp $3 "43620" BwcArch_ok BwcArch_bad
  BwcArch_unreadable:
  StrCpy $3 "unreadable"
  BwcArch_bad:
  IfSilent +2 0
  MessageBox MB_OK|MB_ICONSTOP "This installer is for ${BWC_RID}. This PC reports machine code $3. Download the matching installer."
  Abort "Architecture mismatch: installer ${BWC_RID}, native machine $3"
  BwcArch_ok:
FunctionEnd

; ---------------------------------------------------------------------------
; Install folder checks. .onInit (silent) and .onVerifyInstDir (interactive) run BwcCheckInstDir.
; The folder is accepted when it is absent, empty, or holds this product's manifest. A file, a drive
; root, and a share root are refused. BwcTrimInstDir removes trailing backslashes first.
; ---------------------------------------------------------------------------

!macro BWC_DIR_REFUSE TEXT
  IfSilent +2 0
  MessageBox MB_OK|MB_ICONSTOP "${TEXT}"
  Abort "${TEXT}"
!macroend

; Removes every trailing backslash from $INSTDIR, so the quoted --install-dir argument stays intact.
Function BwcTrimInstDir
  BwcTrim_loop:
  StrLen $R1 $INSTDIR
  StrCmp $R1 "0" BwcTrim_done 0
  StrCpy $R2 $INSTDIR 1 -1
  StrCmp $R2 "\" 0 BwcTrim_done
  StrCpy $INSTDIR $INSTDIR -1
  Goto BwcTrim_loop
  BwcTrim_done:
FunctionEnd

; NSIS drops /D= from $CMDLINE and replaces a root given to /D= with the default folder before .onInit runs.
; This reads the raw /D= value from GetCommandLineW and refuses a root, so the refusal still happens.
; An empty value is not refused. The value stays in $R9, which BwcTrimInstDir and BwcIsRoot do not use.
Function BwcCheckRawDir
  System::Call 'kernel32::GetCommandLineW() t .R9'
  StrLen $R7 $R9
  StrCpy $R6 0
  BwcRaw_find:
  IntCmp $R6 $R7 BwcRaw_done BwcRaw_scan BwcRaw_done
  BwcRaw_scan:
  StrCpy $R5 $R9 3 $R6
  StrCmp $R5 "/D=" BwcRaw_found 0
  IntOp $R6 $R6 + 1
  Goto BwcRaw_find
  BwcRaw_found:
  IntOp $R6 $R6 + 3
  StrCpy $R9 $R9 "" $R6
  BwcRaw_trim:
  StrLen $R7 $R9
  StrCmp $R7 "0" BwcRaw_checked 0
  StrCpy $R5 $R9 1 -1
  StrCmp $R5 " " 0 BwcRaw_checked
  StrCpy $R9 $R9 -1
  Goto BwcRaw_trim
  BwcRaw_checked:
  StrCmp $R9 "" BwcRaw_done 0
  StrCpy $R8 $INSTDIR
  StrCpy $INSTDIR $R9
  Call BwcTrimInstDir
  Call BwcIsRoot
  StrCpy $INSTDIR $R8
  StrCmp $R0 "1" 0 BwcRaw_done
  !insertmacro BWC_DIR_REFUSE "$R9 is the root of a drive or a network share. Choose a folder inside it. Nothing was changed."
  BwcRaw_done:
FunctionEnd

; $R0 = "1" when $INSTDIR is empty, a drive root (C:), or a server or share root (\\server\share).
; Counts the backslashes of a UNC path: \\server\share has 3, so 3 or fewer is a root or an incomplete path.
Function BwcIsRoot
  StrCpy $R0 "0"
  StrLen $R1 $INSTDIR
  StrCmp $R1 "0" BwcRoot_yes 0
  StrCpy $R2 $INSTDIR 2 0
  StrCmp $R2 "\\" BwcRoot_unc 0
  StrCmp $R1 "2" 0 BwcRoot_no
  StrCpy $R2 $INSTDIR 1 1
  StrCmp $R2 ":" BwcRoot_yes BwcRoot_no
  BwcRoot_unc:
  StrCpy $R3 0
  StrCpy $R4 0
  BwcRoot_count:
  IntCmp $R3 $R1 BwcRoot_counted BwcRoot_step BwcRoot_counted
  BwcRoot_step:
  StrCpy $R5 $INSTDIR 1 $R3
  StrCmp $R5 "\" 0 +2
  IntOp $R4 $R4 + 1
  IntOp $R3 $R3 + 1
  Goto BwcRoot_count
  BwcRoot_counted:
  IntCmp $R4 3 BwcRoot_yes BwcRoot_yes BwcRoot_no
  BwcRoot_yes:
  StrCpy $R0 "1"
  BwcRoot_no:
FunctionEnd

; $R7 = folder. $R0 = "1" when the folder has no entry other than . and .., else "0".
Function BwcFolderIsEmpty
  StrCpy $R0 "1"
  FindFirst $R1 $R2 "$R7\*.*"
  BwcEmpty_loop:
  StrCmp $R2 "" BwcEmpty_done 0
  StrCmp $R2 "." BwcEmpty_next 0
  StrCmp $R2 ".." BwcEmpty_next 0
  StrCpy $R0 "0"
  Goto BwcEmpty_done
  BwcEmpty_next:
  FindNext $R1 $R2
  Goto BwcEmpty_loop
  BwcEmpty_done:
  FindClose $R1
FunctionEnd

; $R0 = "1" when <install folder>.new is absent, empty, or holds this product's staging manifest. "0" when
; it is a file or any other folder. Such a folder is never removed.
Function BwcStageFree
  StrCpy $R0 "1"
  IfFileExists "$INSTDIR${BWC_STAGE_SUFFIX}\*.*" BwcSf_dir BwcSf_notdir
  BwcSf_notdir:
  IfFileExists "$INSTDIR${BWC_STAGE_SUFFIX}" BwcSf_file BwcSf_done
  BwcSf_dir:
  IfFileExists "$INSTDIR${BWC_STAGE_SUFFIX}\${BWC_MANIFEST}" BwcSf_done 0
  StrCpy $R7 "$INSTDIR${BWC_STAGE_SUFFIX}"
  Call BwcFolderIsEmpty
  Goto BwcSf_done
  BwcSf_file:
  StrCpy $R0 "0"
  BwcSf_done:
FunctionEnd

; Aborts before any change when <install folder>.new belongs to something else. Names the folder.
Function BwcCheckStageFree
  Call BwcStageFree
  StrCmp $R0 "1" BwcStage_ok 0
  !insertmacro BWC_DIR_REFUSE "The folder $INSTDIR${BWC_STAGE_SUFFIX} is in the way and is not a Better Windows Config staging folder. Move it, then run the installer again. Nothing was changed."
  BwcStage_ok:
FunctionEnd

; Refuses the install folder: a file, a drive or share root, or a folder that is not empty and has no
; installed-files.txt. Then checks the staging folder. Runs before any change.
Function BwcCheckInstDir
  Call BwcIsRoot
  StrCmp $R0 "1" BwcDir_root 0
  IfFileExists "$INSTDIR" 0 BwcDir_free
  IfFileExists "$INSTDIR\*.*" BwcDir_isdir BwcDir_isfile
  BwcDir_isfile:
  !insertmacro BWC_DIR_REFUSE "$INSTDIR is a file, not a folder. Choose another folder. Nothing was changed."
  BwcDir_isdir:
  IfFileExists "$INSTDIR\${BWC_MANIFEST}" BwcDir_free 0
  StrCpy $R7 "$INSTDIR"
  Call BwcFolderIsEmpty
  StrCmp $R0 "1" BwcDir_free 0
  !insertmacro BWC_DIR_REFUSE "$INSTDIR is not empty and is not a Better Windows Config install (no ${BWC_MANIFEST}). Choose another folder. Nothing was changed."
  BwcDir_root:
  !insertmacro BWC_DIR_REFUSE "$INSTDIR is the root of a drive or a network share. Choose a folder inside it. Nothing was changed."
  BwcDir_free:
  Call BwcCheckStageFree
FunctionEnd

; Picks the 64-bit PowerShell when it exists. A 32-bit process sees System32 as SysWOW64, so the
; Sysnative alias is the only way to reach the native host. Used only by the AUMID helper.
Function BwcFindPowerShell
  IfFileExists "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" +2 0
  StrCpy $BwcPowerShell "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe"
  IfFileExists "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" 0 +2
  StrCpy $BwcPowerShell "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
FunctionEnd

; ---------------------------------------------------------------------------
; Strict character check, written inline so that install and uninstall can both use it.
; RESULT becomes "1" when the value is not empty and every character of VALUE appears in ALLOWED,
; otherwise "0". Uses $R3 to $R8. ID keeps the labels unique within the function it is used in.
; ---------------------------------------------------------------------------

!macro BWC_CHARS_OK ID VALUE ALLOWED RESULT
  StrCpy ${RESULT} "0"
  StrLen $R3 ${VALUE}
  StrCmp $R3 "0" BwcChk_${ID}_done 0
  StrLen $R6 ${ALLOWED}
  StrCpy $R4 0
  BwcChk_${ID}_char:
  StrCpy $R5 ${VALUE} 1 $R4
  StrCpy $R7 0
  BwcChk_${ID}_allowed:
  StrCpy $R8 ${ALLOWED} 1 $R7
  StrCmp $R8 $R5 BwcChk_${ID}_found 0
  IntOp $R7 $R7 + 1
  IntCmp $R7 $R6 BwcChk_${ID}_bad BwcChk_${ID}_allowed BwcChk_${ID}_bad
  BwcChk_${ID}_found:
  IntOp $R4 $R4 + 1
  IntCmp $R4 $R3 BwcChk_${ID}_all BwcChk_${ID}_char BwcChk_${ID}_all
  BwcChk_${ID}_all:
  StrCpy ${RESULT} "1"
  Goto BwcChk_${ID}_done
  BwcChk_${ID}_bad:
  StrCpy ${RESULT} "0"
  BwcChk_${ID}_done:
!macroend

; ---------------------------------------------------------------------------
; Close running BWC processes with the copy of BwcUpdate.exe that is being installed. The body is
; shared by the install and uninstall functions. ID keeps labels unique. EXE is the helper's path.
; $BwcCloseArgs holds the optional "--exclude-pid <n>" (the updater that launched this setup).
;
; Exit 0 continues. Exit 3 means a process is still running: interactive runs offer Retry or
; Cancel, silent runs abort with exit code 2. Any other exit (bad arguments, a missing or
; unrunnable helper) aborts. Nothing is changed before this step succeeds.
; ---------------------------------------------------------------------------

!macro BWC_CLOSE_APPS_BODY ID EXE
  BwcClose_${ID}_run:
  nsExec::ExecToLog '"${EXE}" --close-running --install-dir "$INSTDIR" $BwcCloseArgs --timeout-seconds 30'
  Pop $0
  StrCmp $0 "0" BwcClose_${ID}_done 0
  StrCmp $0 "3" BwcClose_${ID}_busy 0
  DetailPrint "The close step failed (exit $0). Nothing was changed."
  Abort "Stopped: the close step failed (exit $0). Nothing was changed."
  BwcClose_${ID}_busy:
  DetailPrint "Better Windows Config is still running from $INSTDIR."
  IfSilent BwcClose_${ID}_silent 0
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${BWC_PRODUCT} or Registry Editor (BWC) is still running from $INSTDIR.$\r$\n$\r$\nClose those windows, then click Retry. Click Cancel to stop without changing anything." IDRETRY BwcClose_${ID}_run
  Abort "Stopped: Better Windows Config is still running. Nothing was changed."
  BwcClose_${ID}_silent:
  SetErrorLevel 2
  Abort "Stopped: Better Windows Config is still running. Nothing was changed."
  BwcClose_${ID}_done:
!macroend

; ---------------------------------------------------------------------------
; Wait for the updater that launched this setup (/PARENTPID). The updater starts the installer and exits
; at once, so its copy of BwcUpdate.exe is not held for long. The wait runs before the close step, so
; no file is replaced while the parent still runs. $BwcParentPid holds the pid. EXE is the helper's path.
;
; Exit 0 continues. Exit 3 means the parent still runs: interactive runs offer Retry or Cancel, silent
; runs abort with exit code 2. Any other exit aborts. Nothing is changed before this step succeeds.
; ---------------------------------------------------------------------------

!macro BWC_WAIT_PARENT_BODY ID EXE
  BwcWait_${ID}_run:
  nsExec::ExecToLog '"${EXE}" --wait-pid $BwcParentPid --timeout-seconds 30'
  Pop $0
  StrCmp $0 "0" BwcWait_${ID}_done 0
  StrCmp $0 "3" BwcWait_${ID}_busy 0
  DetailPrint "The wait for the updater failed (exit $0). Nothing was changed."
  Abort "Stopped: the wait for the updater failed (exit $0). Nothing was changed."
  BwcWait_${ID}_busy:
  DetailPrint "The updater (pid $BwcParentPid) is still running."
  IfSilent BwcWait_${ID}_silent 0
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "The Better Windows Config updater is still running. Click Retry to wait again, or Cancel to stop without changing anything." IDRETRY BwcWait_${ID}_run
  Abort "Stopped: the updater is still running. Nothing was changed."
  BwcWait_${ID}_silent:
  SetErrorLevel 2
  Abort "Stopped: the updater is still running. Nothing was changed."
  BwcWait_${ID}_done:
!macroend

; ---------------------------------------------------------------------------
; Write System.AppUserModel.ID to a shortcut. Non-fatal: a warning is logged and the install
; continues. The running apps still set their own AUMID at startup.
; ---------------------------------------------------------------------------

!macro BWC_SET_AUMID_BODY HELPER SHORTCUT APPID
  nsExec::ExecToLog '"$BwcPowerShell" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${HELPER}" -ShortcutPath "${SHORTCUT}" -AppUserModelId "${APPID}"'
  Pop $0
  StrCmp $0 "0" +2 0
  DetailPrint "WARNING: AppUserModelID ${APPID} was not written to ${SHORTCUT} (helper exit $0). Taskbar grouping of a pinned shortcut may differ."
!macroend

; Sets RESULT to "1" when VALUE is a relative path that may be moved: no leading "\", no ":" at position 2,
; and no "/", ":", or ".." anywhere. Uses $R5 to $R8. ID keeps the labels unique.
!macro BWC_PATH_LINE_OK ID VALUE RESULT
  StrCpy ${RESULT} "0"
  StrCpy $R5 ${VALUE} 1 0
  StrCmp $R5 "\" BwcPl_${ID}_done 0
  StrCpy $R5 ${VALUE} 1 1
  StrCmp $R5 ":" BwcPl_${ID}_done 0
  StrLen $R6 ${VALUE}
  StrCpy $R5 0
  BwcPl_${ID}_char:
  IntCmp $R5 $R6 BwcPl_${ID}_clean BwcPl_${ID}_scan BwcPl_${ID}_clean
  BwcPl_${ID}_scan:
  StrCpy $R7 ${VALUE} 1 $R5
  StrCmp $R7 "/" BwcPl_${ID}_done 0
  StrCmp $R7 ":" BwcPl_${ID}_done 0
  IntOp $R8 $R5 + 1
  StrCpy $R8 ${VALUE} 1 $R8
  StrCmp $R7 "." 0 BwcPl_${ID}_next
  StrCmp $R8 "." BwcPl_${ID}_done 0
  BwcPl_${ID}_next:
  IntOp $R5 $R5 + 1
  Goto BwcPl_${ID}_char
  BwcPl_${ID}_clean:
  StrCpy ${RESULT} "1"
  BwcPl_${ID}_done:
!macroend

; ---------------------------------------------------------------------------
; Upgrade: move the files that the previous version listed in installed-files.txt out of the
; install folder and into <install folder>.old. Only relative paths that pass the checks are moved:
; empty, absolute, ".." and "/" or ":" lines are skipped. A folder that the move empties is removed,
; except the install folder itself. The previous manifest moves too, and the rest of the commit reads
; it from the backup. ID keeps labels unique. Sets $BwcOutFailed to "1" on a failed move.
; ---------------------------------------------------------------------------

!macro BWC_MOVE_OUT_LISTED_BODY ID
  StrCpy $BwcOutFailed "0"
  IfFileExists "$INSTDIR\${BWC_MANIFEST}" BwcOut_${ID}_open BwcOut_${ID}_done
  BwcOut_${ID}_open:
  ClearErrors
  Rename "$INSTDIR\${BWC_MANIFEST}" "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_MANIFEST}"
  IfErrors BwcOut_${ID}_failed 0
  FileOpen $R0 "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_MANIFEST}" r
  IfErrors BwcOut_${ID}_failed 0
  BwcOut_${ID}_loop:
  ClearErrors
  FileRead $R0 $R1
  IfErrors BwcOut_${ID}_close 0
  ; Remove a trailing CR if FileRead kept one.
  StrCpy $R3 $R1 1 -1
  StrCmp $R3 "$\r" 0 +2
  StrCpy $R1 $R1 -1
  StrCmp $R1 "" BwcOut_${ID}_loop 0
  !insertmacro BWC_PATH_LINE_OK "${ID}_path" "$R1" $R2
  StrCmp $R2 "1" 0 BwcOut_${ID}_loop
  IfFileExists "$INSTDIR\$R1" BwcOut_${ID}_move BwcOut_${ID}_loop
  BwcOut_${ID}_move:
  ${GetParent} "$INSTDIR${BWC_BACKUP_SUFFIX}\$R1" $R4
  CreateDirectory "$R4"
  ClearErrors
  Rename "$INSTDIR\$R1" "$INSTDIR${BWC_BACKUP_SUFFIX}\$R1"
  IfErrors BwcOut_${ID}_failed_open 0
  ${GetParent} "$INSTDIR\$R1" $R4
  StrCmp $R4 "$INSTDIR" BwcOut_${ID}_loop 0
  RMDir "$R4"
  Goto BwcOut_${ID}_loop
  BwcOut_${ID}_failed_open:
  FileClose $R0
  BwcOut_${ID}_failed:
  StrCpy $BwcOutFailed "1"
  Goto BwcOut_${ID}_done
  BwcOut_${ID}_close:
  FileClose $R0
  BwcOut_${ID}_done:
!macroend

; The same walk for a restore: move every listed file from <install folder>.old back to its place.
; A file that is not in the backup is skipped. A failed move sets $BwcRestoreFailed.
!macro BWC_RESTORE_LISTED_BODY ID
  StrCpy $BwcRestoreFailed "0"
  IfFileExists "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_MANIFEST}" BwcRes_${ID}_open BwcRes_${ID}_done
  BwcRes_${ID}_open:
  FileOpen $R0 "$INSTDIR${BWC_BACKUP_SUFFIX}\${BWC_MANIFEST}" r
  IfErrors BwcRes_${ID}_failed 0
  BwcRes_${ID}_loop:
  ClearErrors
  FileRead $R0 $R1
  IfErrors BwcRes_${ID}_close 0
  StrCpy $R3 $R1 1 -1
  StrCmp $R3 "$\r" 0 +2
  StrCpy $R1 $R1 -1
  StrCmp $R1 "" BwcRes_${ID}_loop 0
  !insertmacro BWC_PATH_LINE_OK "${ID}_path" "$R1" $R2
  StrCmp $R2 "1" 0 BwcRes_${ID}_loop
  IfFileExists "$INSTDIR${BWC_BACKUP_SUFFIX}\$R1" BwcRes_${ID}_move BwcRes_${ID}_loop
  BwcRes_${ID}_move:
  ${GetParent} "$INSTDIR\$R1" $R4
  CreateDirectory "$R4"
  Delete "$INSTDIR\$R1"
  ClearErrors
  Rename "$INSTDIR${BWC_BACKUP_SUFFIX}\$R1" "$INSTDIR\$R1"
  IfErrors BwcRes_${ID}_failed_open 0
  Goto BwcRes_${ID}_loop
  BwcRes_${ID}_failed_open:
  FileClose $R0
  BwcRes_${ID}_failed:
  StrCpy $BwcRestoreFailed "1"
  Goto BwcRes_${ID}_done
  BwcRes_${ID}_close:
  FileClose $R0
  BwcRes_${ID}_done:
!macroend

; ---------------------------------------------------------------------------
; E2E switch handling. Release builds never call these. Both switches are required so that a
; test run cannot write to the real Start menu or the real Uninstall key by accident.
; RUNID must be 1 to 64 letters, digits, dash, or underscore. The harness controls it.
; ---------------------------------------------------------------------------

!ifdef BWC_E2E
Function BwcReadE2eSwitches
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RUNID=" $BwcRunId
  IfErrors 0 +2
  ReadEnvStr $BwcRunId "BWC_E2E_INSTALLER_RUNID"
  ClearErrors
  ${GetOptions} $R0 "/SHORTCUTS=" $BwcShortcutDir
  IfErrors 0 +2
  ReadEnvStr $BwcShortcutDir "BWC_E2E_INSTALLER_SHORTCUTS"
  StrCmp $BwcRunId "" BwcE2e_missing 0
  StrCmp $BwcShortcutDir "" BwcE2e_missing 0
  StrLen $R3 $BwcRunId
  IntCmp $R3 64 BwcE2e_lenok BwcE2e_lenok BwcE2e_missing
  BwcE2e_lenok:
  !insertmacro BWC_CHARS_OK "install" "$BwcRunId" "${BWC_RUNID_CHARS}" $R2
  StrCmp $R2 "1" BwcE2e_idok 0
  Abort "The E2E installer needs /RUNID=<id> with 1 to 64 letters, digits, dash, or underscore. Nothing was changed."
  BwcE2e_idok:
  StrCpy $BwcUninstallKey "${BWC_E2E_ROOT}\$BwcRunId\Uninstall\BetterWindowsConfig"
  Return
  BwcE2e_missing:
  Abort "The E2E installer needs /RUNID=<id> and /SHORTCUTS=<folder>. Nothing was changed."
FunctionEnd

Function un.BwcReadE2eSwitches
  ${un.GetParameters} $R0
  ClearErrors
  ${un.GetOptions} $R0 "/RUNID=" $BwcRunId
  IfErrors 0 +2
  ReadEnvStr $BwcRunId "BWC_E2E_INSTALLER_RUNID"
  StrCmp $BwcRunId "" BwcE2eUn_missing 0
  StrLen $R3 $BwcRunId
  IntCmp $R3 64 BwcE2eUn_lenok BwcE2eUn_lenok BwcE2eUn_missing
  BwcE2eUn_lenok:
  !insertmacro BWC_CHARS_OK "uninstall" "$BwcRunId" "${BWC_RUNID_CHARS}" $R2
  StrCmp $R2 "1" BwcE2eUn_idok 0
  Abort "The E2E uninstaller needs /RUNID=<id> with 1 to 64 letters, digits, dash, or underscore. Nothing was changed."
  BwcE2eUn_idok:
  StrCpy $BwcUninstallKey "${BWC_E2E_ROOT}\$BwcRunId\Uninstall\BetterWindowsConfig"
  Return
  BwcE2eUn_missing:
  Abort "The E2E uninstaller needs /RUNID=<id>. Nothing was changed."
FunctionEnd
!endif
