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
!define BWC_TOOLS_DIR "bwc-tools"
!define BWC_CLOSE_HELPER "bwc-close-apps.ps1"
!define BWC_AUMID_HELPER "bwc-set-aumid.ps1"
!define BWC_POWERSHELL "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe"
; Windows 10 2004 (19041) is TargetPlatformMinVersion in build/Bwc.WinUIApp.props.
!define BWC_MIN_BUILD 19041

; Runtime state shared by install and uninstall. Set in .onInit and un.onInit.
Var BwcRunId
Var BwcShortcutDir
Var BwcUninstallKey

; ---------------------------------------------------------------------------
; Host checks (install only). Both run in .onInit, before any file is touched.
; MessageBox is skipped in silent mode because NSIS would otherwise show it and block /S.
; ---------------------------------------------------------------------------

Function BwcCheckHost
  ; CurrentBuildNumber is a string such as "26200". A failed read compares as 0 and refuses.
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  IntCmp $0 ${BWC_MIN_BUILD} BwcHost_old BwcHost_ok BwcHost_ok
  BwcHost_old:
  IfSilent +2 0
  MessageBox MB_OK|MB_ICONSTOP "${BWC_PRODUCT} needs Windows 10 build ${BWC_MIN_BUILD} or later. This PC reports build $0."
  Abort "Windows build too old or unreadable: $0"
  BwcHost_ok:
FunctionEnd

; PROCESSOR_ARCHITECTURE_AMD64 = 9, PROCESSOR_ARCHITECTURE_ARM64 = 12. GetNativeSystemInfo
; reports the real machine, so an emulated shell cannot hide an ARM64 host.
; The ARM64 installer needs an ARM64 host. The x64 installer also runs on ARM64, under emulation.
Function BwcCheckArchitecture
  System::Alloc 64
  Pop $1
  System::Call 'kernel32::GetNativeSystemInfo(p $1)'
  System::Call "*$1(&i2 .r2)"
  System::Free $1
  StrCmp "${BWC_ARCH}" "arm64" BwcArch_arm64 0
  StrCmp $2 "9" BwcArch_ok 0
  StrCmp $2 "12" BwcArch_ok BwcArch_bad
  BwcArch_arm64:
  StrCmp $2 "12" BwcArch_ok BwcArch_bad
  BwcArch_bad:
  IfSilent +2 0
  MessageBox MB_OK|MB_ICONSTOP "This installer is for ${BWC_RID}. This PC reports native architecture code $2. Download the matching installer."
  Abort "Architecture mismatch: installer ${BWC_RID}, native code $2"
  BwcArch_ok:
FunctionEnd

; ---------------------------------------------------------------------------
; Close running BWC processes. The body is shared by the install and uninstall functions.
; ID keeps labels unique. HELPER is the path to bwc-close-apps.ps1 in that context.
; Exit 0 continues. Any other result offers Retry (interactive) or aborts (silent).
; ---------------------------------------------------------------------------

!macro BWC_CLOSE_APPS_BODY ID HELPER
  BwcClose_${ID}_run:
  nsExec::ExecToLog '"${BWC_POWERSHELL}" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${HELPER}" -InstallDir "$INSTDIR" -TimeoutSeconds 20'
  Pop $0
  StrCmp $0 "0" BwcClose_${ID}_done 0
  DetailPrint "Better Windows Config is still running from $INSTDIR (close helper exit $0)."
  IfSilent BwcClose_${ID}_abort 0
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${BWC_PRODUCT} or Registry Editor (BWC) is still running from $INSTDIR.$\r$\n$\r$\nClose those windows, then click Retry. Click Cancel to stop without changing anything." IDRETRY BwcClose_${ID}_run
  BwcClose_${ID}_abort:
  Abort "Stopped: Better Windows Config is still running. Close it and run the installer again."
  BwcClose_${ID}_done:
!macroend

; ---------------------------------------------------------------------------
; Write System.AppUserModel.ID to a shortcut. Non-fatal: a warning is logged and the install
; continues. The running apps still set their own AUMID at startup.
; ---------------------------------------------------------------------------

!macro BWC_SET_AUMID_BODY HELPER SHORTCUT APPID
  nsExec::ExecToLog '"${BWC_POWERSHELL}" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${HELPER}" -ShortcutPath "${SHORTCUT}" -AppUserModelId "${APPID}"'
  Pop $0
  StrCmp $0 "0" +2 0
  DetailPrint "WARNING: AppUserModelID ${APPID} was not written to ${SHORTCUT} (helper exit $0). Taskbar grouping of a pinned shortcut may differ."
!macroend

; ---------------------------------------------------------------------------
; Delete the files that the previous version listed in installed-files.txt. Lines that are
; empty, absolute, or start with "..\" are skipped. A missing manifest (first install or a
; folder copied by hand) removes nothing. ID keeps labels unique.
; ---------------------------------------------------------------------------

!macro BWC_REMOVE_LISTED_BODY ID
  IfFileExists "$INSTDIR\${BWC_MANIFEST}" BwcList_${ID}_open BwcList_${ID}_done
  BwcList_${ID}_open:
  FileOpen $R0 "$INSTDIR\${BWC_MANIFEST}" r
  BwcList_${ID}_loop:
  ClearErrors
  FileRead $R0 $R1
  IfErrors BwcList_${ID}_close 0
  ; Remove a trailing CR if FileRead kept one.
  StrCpy $R3 $R1 1 -1
  StrCmp $R3 "$\r" 0 +2
  StrCpy $R1 $R1 -1
  StrCmp $R1 "" BwcList_${ID}_loop 0
  StrCpy $R2 $R1 1 0
  StrCmp $R2 "\" BwcList_${ID}_loop 0
  StrCpy $R2 $R1 1 1
  StrCmp $R2 ":" BwcList_${ID}_loop 0
  StrCpy $R2 $R1 2 0
  StrCmp $R2 ".." BwcList_${ID}_loop 0
  Delete "$INSTDIR\$R1"
  Goto BwcList_${ID}_loop
  BwcList_${ID}_close:
  FileClose $R0
  Delete "$INSTDIR\${BWC_MANIFEST}"
  BwcList_${ID}_done:
!macroend

Function BwcRemovePreviousPayload
  !insertmacro BWC_REMOVE_LISTED_BODY "install"
FunctionEnd

; ---------------------------------------------------------------------------
; E2E switch handling. Release builds never call these. Both switches are required so that a
; test run cannot write to the real Start menu or the real Uninstall key by accident.
; RUNID must be a simple name (letters, digits, dot, dash, underscore). The harness controls it.
; ---------------------------------------------------------------------------

!ifdef BWC_E2E
Function BwcReadE2eSwitches
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RUNID=" $BwcRunId
  IfErrors BwcE2e_missing 0
  ClearErrors
  ${GetOptions} $R0 "/SHORTCUTS=" $BwcShortcutDir
  IfErrors BwcE2e_missing 0
  StrCmp $BwcRunId "" BwcE2e_missing 0
  StrCmp $BwcShortcutDir "" BwcE2e_missing 0
  StrCpy $BwcUninstallKey "${BWC_E2E_ROOT}\$BwcRunId\Uninstall\BetterWindowsConfig"
  Return
  BwcE2e_missing:
  Abort "The E2E installer needs /RUNID=<id> and /SHORTCUTS=<folder>. Nothing was changed."
FunctionEnd

Function un.BwcReadE2eSwitches
  ${un.GetParameters} $R0
  ClearErrors
  ${un.GetOptions} $R0 "/RUNID=" $BwcRunId
  IfErrors BwcE2eUn_missing 0
  StrCmp $BwcRunId "" BwcE2eUn_missing 0
  StrCpy $BwcUninstallKey "${BWC_E2E_ROOT}\$BwcRunId\Uninstall\BetterWindowsConfig"
  Return
  BwcE2eUn_missing:
  Abort "The E2E uninstaller needs /RUNID=<id>. Nothing was changed."
FunctionEnd
!endif
