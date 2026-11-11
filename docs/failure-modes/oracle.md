# Oracle capture failure modes

The oracle tools (`tools/Bwc.Oracle.Resources`, `tools/Bwc.Oracle.Capture`) drive the classic
`C:\Windows\regedit.exe` on the developer's desktop to record parity numbers. `scripts/oracle.js`
is the entry point. This document lists the ways a capture can corrupt user state, record wrong
data, or hang, how each is detected, and what the tools do about it. It was written before the
dialog, menu, import, find and sort captures were added.

## 1. Writes outside BWC-Oracle

A capture must never change a value the user owns. The only writable roots are
`HKCU\Software\BWC-Oracle` (created, seeded and deleted by the tools) and the exact Regedit
applet state described in section 2.

- `RegistryGuard` is the only class that writes the registry. Every write asserts its full path
  is under one of the two roots and throws otherwise.
- `.reg` files passed to `regedit /s` are validated before they run. Every header line must name
  `HKEY_CURRENT_USER\Software\BWC-Oracle` (optionally prefixed by `-` for deletion). Headers with
  `..` segments, HKLM, HKU, HKCR or any other root are refused and the file is never imported.
- The import capture runs regedit only on files it generated under `artifacts/oracle/import/`.
  It never accepts a path from the command line.
- A regedit export (`/e`) writes only a file. It cannot change the registry.

## 2. Regedit applet state left changed

Classic regedit writes its own state under `HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit`:
LastKey, FindFlags, View, window placement and the `Favorites` subkey. A capture that opens
Find, Favorites or a view option changes those values.

- Before any regedit run the tool snapshots the whole applet key recursively (value names, exact
  REG_* types and raw bytes, subkey names). It restores every value and subkey to that snapshot in
  a `finally` block, deletes values and subkeys that did not exist, and removes the applet key if
  it did not exist before.
- After restore the tool captures the snapshot again and compares it. A mismatch fails the run.
- A process killed mid-run (power loss, Ctrl+C, or a stopped tool process) skips the restore. The
  next run takes its snapshot from the changed state, so the change is carried forward. The run log
  prints the applet state before and after each command (`applet state before` and `applet state restored`).
- Writers outside the lock are not covered. During development another worker's E2E favorites
  test and a classic regedit run changed the same key while this tool held no lock. The snapshot
  restores only what it captured at the start of its own locked session. A subkey that another
  process deleted before the session started cannot be restored by this tool, because its contents
  were never recorded. Such a change is reported as a restore mismatch only when this tool itself
  changed the key.

## 3. Stale BWC-Oracle tree

A previous failed run can leave `HKCU\Software\BWC-Oracle` behind. Each capture deletes the tree
before seeding it and deletes it again in a `finally` block. The DoD check is that the tree does
not exist after `npm run oracle` returns.

## 4. UAC prompt

`C:\Windows\regedit.exe` requests `highestAvailable`. A direct launch on an administrator account
raises UAC, which blocks the run and can take focus while the user is away. Every child process
is started with `__COMPAT_LAYER=RunAsInvoker` in its own environment block (`ProcessStartInfo.Environment`),
never in the tool's own environment. `RegeditProcess.cs` is the only place that starts classic regedit,
and it sets `UseShellExecute = false`, so a `runas` verb cannot be applied. The tools never call
`runas`, `ShellExecute` with a verb, or any elevation API.

## 5. Driving another worker's window

Other workers run GUI tests on the same desktop in parallel. A capture that clicks while another
regedit or test window has focus would record the wrong window or drive the wrong test.

- Every capture that opens a window runs inside `GuiLock.Run`, which holds the named mutex
  `Local\BwcGuiTestLock` (the same name as `tests/Bwc.E2E/Harness/GuiGate.cs`). A 15 minute
  wait times out with an error and the capture does not run without the lock.
- Windows are bound to the process id the tool started, not to any window with a matching
  title or class. A user's own regedit window is never selected.
- Input uses `PostMessage` and `SendMessage` to the target window. The tools never call
  `SendInput`, `SetForegroundWindow` or `keybd_event`, so the user's keyboard and cursor are not
  taken over.

## 6. Clicking OK or changing a value

Dialogs such as Edit String, Edit DWORD, Rename, Find and Permissions have an OK or Apply path that
writes to the registry. Dialog captures only cancel.

- Dialog close uses `WM_COMMAND` with `IDCANCEL` (2) or `WM_CLOSE` posted to the dialog. The
  tools never post `IDOK` or press Enter on a dialog.
- Edit dialogs are opened on a value under BWC-Oracle only, so even a mistaken OK writes to the
  seeded tree, which is deleted afterwards.
- Inline Rename is cancelled with Escape on the edit control. Its text is never committed.
- Import (`regedit /s`) is the only capture that writes data, and it is limited to section 1.

## 7. Modal dialog left open

If a dialog does not close, the regedit process blocks on its message loop and the tool waits.

- Each wait has a deadline (30 seconds for a window, 15 seconds for a close). On timeout the tool
  kills only the process it started (`Process.Kill` on the handle it holds) and throws.
- The kill path checks `HasExited` first, so an already exited process is not an error.
- A message box owned by the started process is treated as a failure, not clicked.

## 8. Wrong DPI

Classic-regedit parity is measured at 96 DPI. This desktop runs at 200 percent, so the metrics
capture refuses by default. `scripts/oracle.js` passes `--allow-dpi` so the run completes, and
every tracked output records the system DPI and window DPI, so a 192 DPI value is never presented
as a 96 DPI value. Consumers divide by `dpi.window / 96` (see the inventory section VIEW-001).

## 9. Wrong window captured

`PrintWindow` on the wrong handle records another window, and `CopyFromScreen` records whatever is
on top. Dialog and menu screenshots use `PrintWindow` with `PW_RENDERFULLCONTENT` on the handle
of the dialog or popup the tool found for its own process. The tool checks the captured size against the
window rectangle and fails if they differ.

## 10. Wrong control rectangles

Control rectangles are read with `GetWindowRect` and converted to the dialog client origin with
`ClientToScreen` on the dialog, not from the top-left of the window frame. A control hidden by a
child template (for example the export range panel) is recorded with `visible: false` rather than
dropped, so a missing control is distinguishable from an absent one. Control ids come from
`GetDlgCtrlID`, not from text.

## 11. Find hit sequence misread

Find moves the tree and list selection asynchronously. A read taken before the move completes
records the previous hit.

- After each Find Next or F3 the tool polls until the tree caret and the list selection are stable
  for two consecutive reads, with a 5 second cap.
- The hit is the key path from the address bar text plus the selected list item text (the value
  name, or `(Default)` for the default value). Key hits have an empty value name.
- FindFlags are restored by the applet snapshot, so the next capture starts with the same flags.

## 12. Sort order misread

Classic regedit lists values in its own order. The tool reads rows with `LVM_GETITEMTEXTW` from the
list in regedit's address space through `RemoteBuffer`. A header click is sent as a mouse message
to the header control and the order is read again. A failed `VirtualAllocEx` or a short
`ReadProcessMemory` throws instead of recording an empty string.

## 13. Import behavior recorded from a half-written tree

`regedit /s` returns before the import finishes only if it crashes. The exit code is recorded and
the subtree is read after the process has exited. A process that does not exit within 30 seconds is
killed by the tool, recorded with `timedOut: true`, and the tree is still read so the record shows
the partial state. A file whose headers fail validation is never started; its record carries a
`refused` reason instead of a tree.

## 14. Microsoft content committed by accident

The resource dump (`regedit-resources.json`, `regedit-resources.md`) and the screenshots are
Microsoft content. They are untracked and listed in `.gitignore`. `git status` must not show them
as added. The tools write them on each run, so a stale copy is overwritten and never merged.

## 15. Non-deterministic tracked output

Tracked JSON carries no timestamps. The run record with start and finish times is
`artifacts/oracle/last-run.json`, written by `scripts/oracle.js` and never tracked. Lists are sorted
with ordinal comparison, and tracked files use LF line endings with no byte-order mark. The
definition of done compares two consecutive runs by hashing the tracked files.

## 16. Reparse of Resources output while a test reads it

The resource dump is written to a temporary file and renamed into place, so a test reading
`regedit-resources.json` never sees a partial file.
