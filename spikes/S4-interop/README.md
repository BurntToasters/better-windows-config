# S4 interop spike

Proves four interop behaviours a WinUI 3 re-implementation of a classic Win32 tool needs: classic menu keyboard
handling, an owned modal dialog, the COM common file dialogs, and the aclui security editor.

Result: all four are achievable. The E2E suite (11 tests, 70 checks) passed in three consecutive full runs on the
final code, unelevated, on Windows 11 Enterprise ARM64 at 100% DPI. Elevated runs were not done (out of scope).

## Layout

- `Bwc.Spike.Interop/` - WinUI 3 app (WindowsAppSDK 2.6.5, unpackaged, self-contained, CsWin32 for the COM file dialogs).
- `Bwc.Spike.Interop.E2E/` - MSTest.Sdk + FlaUI.UIA3 tests. The build references the app for `win-arm64` and copies its
  build output to `bin/.../app/`; the tests launch that copy.
- `out/s4-result.json` - artifact written at the end of every run (git-ignored). One entry per check: `id`, `pass`, `notes`.

Run: `dotnet test spikes/S4-interop/Bwc.Spike.Interop.E2E` (the app is built as part of the test build; no publish step).
Tests hold `Local\BwcGuiTestLock` while they drive a window, kill only the process they launched, and delete
`HKCU\Software\BWC-Spike-S4` at the end of the run (the app creates it at startup).

## 1. Classic menu keyboard - works

Hook approach: `SetWindowSubclass` (comctl32, hand-written `LibraryImport`, `[UnmanagedCallersOnly]` callback, no delegates)
on the top-level HWND and on every descendant. Keys are delivered to the XAML input-site child
(`InputSiteWindowClass`, under `Microsoft.UI.Content.DesktopChildSiteBridge`), so the hook must cover the children.
I did not test a top-level-only hook separately.

- Alt press+release alone (no other key between) and F10 activate the bar: "File" gets focus (MenuItem, focus visible).
  Alt is passed to `DefWindowProc`; the activating Alt-up is consumed.
- Escape while the bar is active restores the focus saved at activation.
- Alt+F (and Alt+E/V, and a bare mnemonic while the bar is active) opens that item's flyout.
- Down opens the flyout of the focused item. Flyout open via `MenuBarItemAutomationPeer.Expand()` (public UIA
  ExpandCollapse member), which is the supported path (WinUI 2.6 has no public flyout property on `MenuBarItem`).
- Escape with a flyout open is passed to XAML, which closes the flyout; the bar stays active.
- `MenuState` (AutomationId) shows `Idle`, `Focused:<item>`, or `Open:<item>`.

**Arrow keys bypass the window hook.** WinUI's island input pipeline handles Left/Right before window messages are
dispatched. Observed: focus moved to the next item with no `WM_KEYDOWN(VK_RIGHT)` reaching the hook. Also, `PreviewKeyDown`
runs before XAML's focus navigation, and marking the event handled does not stop that navigation (it still moved focus,
so calling `Move` as well moved two items). The final design:
- Left/Right: an XYFocus ring on the MenuBarItems (`XYFocusLeft/Right`, File-Edit-View, wraps). XAML moves focus.
- Up/Down: `XYFocusUp/Down` point at the item itself, so XAML does not move focus.
- Down (opens the flyout): handled in `MenuBar.PreviewKeyDown`, which runs before XAML's default processing.
- `MenuBarItem.GotFocus` keeps `MenuState` in step with focus, whichever path moved it.

## 2. Owned modal dialog - works, with two WinAppSDK 2.6 findings

- `OverlappedPresenter.CreateForDialog()`, `IsModal = true`, `AppWindow.IsShownInSwitchers = false`,
  centred on the owner with `AppWindow.Move`.
- Owner: `AppWindow.OwnerWindowId` is read-only in 2.6, and `AppWindow.Create(presenter, ownerId)` builds a non-XAML
  window. So the owner is set with `SetWindowLongPtr(GWLP_HWNDPARENT, owner)` before the dialog is first shown.
  Verified: `GetWindow(GW_OWNER)` is the main window; the dialog is not in the taskbar (owned, no `WS_EX_APPWINDOW`).
- **`IsModal` does not disable the owner in 2.6.** Measured with `IsModal` set and no explicit disable:
  `IsWindowEnabled(owner)` stayed TRUE while the dialog was open. The owner is therefore disabled with
  `EnableWindow(owner, FALSE)` and re-enabled, plus `SetActiveWindow`, on `Closed`. Verified in the test.

## 3. ComboBox in the modal - dropdown opens and selects; bug #10722 did not reproduce

- The dropdown opens with the mouse and its items are visible in UIA.
- Selection works by mouse click, UIA `SelectionItem.Select`, and keyboard (Alt+Down, Down, Enter). Each path leaves
  `ModalSelection` = "Selected: Beta". The combo tests then close the dialog with Cancel, so the OK path (`ok: Beta`) was
  not exercised by the E2E suite.
- Intermittent mouse misses: 2 misses were seen before the fix below (the total number of mouse selections was not
  counted). One was in the modal and one in a non-modal control ComboBox in the main window, so this is not
  modal-specific. The likely cause is a click computed from a popup that is still animating in. A `ClickWhenStable`
  helper (wait until the item's rectangle stops changing, then click) was added. Since the change: 6 ComboBox-only runs
  and 3 full runs, no misses. The sample is small.

## 4. Common file dialog - works

- `IFileOpenDialog` / `IFileSaveDialog` (CsWin32 `FileOpenDialog` / `FileSaveDialog`), shown with the WinUI HWND as owner.
- The Win32 `Name|Pattern` filter strings are passed as `COMDLG_FILTERSPEC` pairs (the COM form).
- CsWin32 COM methods throw `COMException` on failure; cancel is `0x800704C7`, caught and reported as `cancelled`.
- Verified: a `#32770` dialog owned by the main window (titles "Open" and "Save As"); Escape cancels; `FileResult` is `cancelled`.
- Escape closed the Open dialog on the first press in the final runs. In one earlier run the first press was not
  processed; the test now retries once. I did not find the cause of that single miss.

## 5. Security editor - works, with three important quirks

- `ISecurityInformation` is a `[GeneratedComInterface]` implemented by a `[GeneratedComClass]`, shown through
  `aclui.dll!EditSecurity` (`LibraryImport`). `SI_EDIT_ALL | SI_ADVANCED | SI_READONLY`, `SetSecurity` returns
  `E_ACCESSDENIED`, `GetAccessRights` lists the KEY_* masks and the specific rights, `MapGeneric` uses the KEY mapping.
- Verified: the dialog is `#32770`, titled "Permissions for BWC-Spike-S4", owned by the main window; the owner is
  disabled while it is open; Cancel closes it; `EditSecurity` returns `S_FALSE` (0x1).
- Quirk: every `int`-returning method needs `[PreserveSig]`. Without it the source generator emits an extra out-pointer
  in the vtable ABI, and aclui calls it with only the struct pointer, so the process faults in
  `GetObjectInformation` (access violation).
- Quirk: **aclui releases the descriptor it gets from `GetSecurity` (`LocalFree`).** Freeing it, or replacing it on a
  later call, caused heap corruption (`0xC0000374`) and a process exit right after Cancel. Verified by skipping only
  that free (4/4 passing, process alive). Fix: allocate with `LocalAlloc` and never free it. Other buffers (object name,
  access array) are owned by the object and freed in `Dispose`, after `EditSecurity` returns.
- Not verified: UI-level read-only (the Apply button is not checked). `SetSecurity` returning `E_ACCESSDENIED` is by code only.
  The audit (SACL) page needs `SeSecurityPrivilege`; the code falls back to owner/DACL on `ERROR_PRIVILEGE_NOT_HELD`, but
  that path was not exercised. Inheritance types are not offered (`E_NOTIMPL`).

## Crashes and quirks

- **Startup crash 0xC000027B with any XAML `MenuBar`** unless `XamlControlsResources` is merged in `App.xaml`.
  Verified: an empty `<MenuBar />` crashes at XAML load; the same page with the merge runs. The S1 spike's build output
  also exited with 0xC000027B at startup during this session. I did not investigate that further; it may have the same cause.
- Publish was not needed: the build output runs once the merge is present. (Publish was tried first and also crashed,
  before the merge was added.)
- UIA `BoundingRectangle` for the modal's OK and Cancel buttons is `0,0,0,0`, so a mouse `Click()` throws
  `NoClickablePointException`. The tests use UIA `Invoke` for those buttons. ComboBoxes and their items have valid rectangles.
- The main window's UIA root, captured at launch, goes stale after a Win32 modal (aclui). The harness re-acquires the root
  from the HWND on each query.
- `IsWindowEnabled` returns FALSE for a destroyed window, so an "enabled" check can read as a false negative after a crash.
- Early in the work, clicks sometimes landed before a button reached its final position after launch. Buttons and popups
  now use `ClickWhenStable`, and the dialog-opening clicks retry once. Retrying is safe: the owner is disabled once a dialog exists.

## Not done / limitations

- No elevated run (out of scope). Common file dialogs and the security editor were only tested unelevated.
- Mouse-close of a flyout does not reset `MenuState` (state is tracked from keyboard and focus events only).
- Mnemonics use `MenuBarItem.AccessKey` only; no underlines or KeyTip rendering were checked.
- The mouse-selection flake is not ruled out; the ComboBox evidence is 6 ComboBox-only runs and 3 full runs since the
  stable-click change (the last 3 also include the open-dialog retry).
