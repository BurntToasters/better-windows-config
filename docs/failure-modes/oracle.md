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
- Exception (0.1.2 `remote` capture, authorized by the manager): the Connect picker's computer name is typed with
  `SendInput` (KEYEVENTF_UNICODE characters), and the picker is brought to the foreground with
  `SetForegroundWindow` first. Both are used for that one dialog only. Before each character,
  `GetForegroundWindow` must be the picker's top-level window and the keyboard focus of regedit's GUI thread must be
  the name box. If either check fails, typing stops, the picker is cancelled with IDCANCEL, and the record says
  `not measured: focus lost`. The whole step holds the GUI mutex. The user's cursor is not moved. The picker is
  cancelled at the end, and the foreground returns to whatever the user had active only as far as Windows does it
  for the closed dialog.

## 6. Clicking OK or changing a value

Dialogs such as Edit String, Edit DWORD, Rename, Find and Permissions have an OK or Apply path that
writes to the registry. Dialog captures only cancel.

- Dialog close uses `WM_COMMAND` with `IDCANCEL` (2) or `WM_CLOSE` posted to the dialog. The
  tools never post `IDOK` or press Enter on a dialog.
- Edit dialogs are opened on a value under BWC-Oracle only, so even a mistaken OK writes to the
  seeded tree, which is deleted afterwards.
- Inline Rename is cancelled with Escape on the edit control. Its text is never committed.
- Import (`regedit /s`) is the only capture that writes data, and it is limited to section 1.
- Exception (0.1.2 `remote` capture only): the Select Computer dialog's OK connects to 127.0.0.1, and the disconnect
  dialog's OK drops the connection. Neither writes to a registry. Section 18 lists the checks that gate them.

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

## 17. Permissions capture (Advanced Security Settings entry list)

The `permissions` capture (`PermissionsCapture.cs`) seeds one key under `HKCU\Software\BWC-Oracle\Perm` with an
explicit allow ACE per inheritance flag set (0x0, 0x1, 0x2, 0x3, 0x9, 0xA, 0xB), each for a different
well-known group. It opens classic Edit > Permissions, presses Advanced, reads the entry list and the
Applies-to choices of one entry editor, and cancels every window. The ways it can fail:

- **ACE flags written differently than intended.** `InheritanceFlags` and `PropagationFlags` map to the
  winnt.h bits (`ObjectInherit` is 0x1, `ContainerInherit` is 0x2, `InheritOnly` is 0x8). A mapping slip
  would record the wrong label for a flag set. The seeded DACL is read back as raw bytes
  (`RawSecurityDescriptor`) before classic starts. Each seeded ACE must carry exactly its flags, the allow
  type, and KEY_READ, or the run fails before any window opens.
- **Wrong principal matched to a seeded row.** Each seeded SID is matched to a list row by its account name
  (translated from the SID, locale of the desktop). A row matches only when its principal is the name or the
  name followed by " (". Zero or two matches fail the run. The capture never guesses a row by position.
- **Column meaning misread.** The columns are found by header text (Type, Principal, Access, Inherited from,
  Applies to), read with `LVM_GETCOLUMNW`. A missing header fails the run and the header texts are listed in the error.
- **Wrong key or wrong dialog.** The Advanced window must be owned by the Permissions sheet, which must be
  owned by the main window, and the Advanced window title must contain the key name `Perm`. The run fails
  otherwise, so a dialog for the Regedit root or the user's own key is never recorded.
- **Permissions command not the expected one.** The Edit menu item "Permissions..." must have command id
  678 and not be grayed. Any other id fails before a message is posted.
- **Advanced button missing or disabled.** The button is matched by its label with ampersands removed. The
  button list is included in the error. The capture posts `BM_CLICK` with `PostMessage`, so the modal
  Advanced window does not block a `SendMessage` call.
- **Entry list not yet populated.** The list is read only after its row count is non-zero and unchanged on
  two reads 800 ms apart. The row count must include all seven seeded rows.
- **Wrong combo box read.** The entry editor has two combo boxes (Type and Applies to). The Applies-to combo
  is the only one whose items carry inherit-type data (`CB_GETITEMDATA` points at aclui's 24-byte
  `SI_INHERIT_TYPE` entry). The run fails unless exactly one combo has that data and it has at least three
  items. The Type combo has no item data and is skipped.
- **Misreading the inherit-type table.** The table is read with `ReadProcessMemory` from regedit. A bad
  pointer or a short read throws. The table has the flags at offset 8 and a string resource id at offset 16
  (not a pointer). Only the items the combo reports (`CB_GETCOUNT`) are read. Measured: the table holds three
  entries, and the memory after them is heap data that must not be read as entries.
- **Combo text read as empty.** `CB_GETLBTEXT` is marshalled across processes: Windows copies the text into the
  caller's buffer. A buffer in regedit's address space is silently not written and reads back as zeros. The text
  is read into a local (`Marshal.AllocHGlobal`) buffer. The run also fails when the returned length is not positive.
- **Input reaching another window.** Every window is found by the process id the tool started. Input is
  posted with `PostMessage` or sent with `SendMessageTimeout` to those windows only. The tool never calls
  `SendInput`, `SetForegroundWindow` or `keybd_event`, so no keystroke can reach a terminal or another app.
- **Unexpected window.** A message box or any other `#32770` window that the capture did not open (including a
  "Registry Editor" question) makes the run fail. It is never clicked. A UAC or consent prompt belongs to
  another process, so the tool cannot see it or answer it. The wait times out, the tool kills only the regedit
  process it started, and the run fails. A user who sees a consent prompt must cancel it by hand.
- **Cancel does not close a window.** Every window is closed with `IDCANCEL`, then `WM_CLOSE`, with a
  5 second wait each (`RegeditWindow.CloseDialog`). The entry editor, Advanced window and sheet are closed in
  that order. The process is killed only if it is still running at the end, and it is the process the tool started.
- **OK is never posted.** The capture never posts `IDOK`, never presses Enter and never clicks OK, so no ACE
  is changed by the dialog. The seeded DACL exists only under `HKCU\Software\BWC-Oracle\Perm`, which the
  cleanup deletes.
- **Writes outside BWC-Oracle.** The DACL write goes through `RegistryGuard.SetOracleKeySecurity`, which asserts
  the path is under `HKCU\Software\BWC-Oracle` before it opens the key with `WRITE_DAC`. The applet key is restored
  by the snapshot in `OracleSession`, as in every other capture.
- **Personal data in tracked output.** Inherited rows can name the current user or the machine. The tracked
  file records only the seeded group names. Other rows are recorded with the principal replaced by a
  placeholder and a flag for the current user. The raw display texts go to `docs/parity/oracle/local/`, which
  is untracked.
- **Wrong DPI.** The capture runs under `RequireDpi` like the other UI captures. The DPI is written into the output.
- **Cleanup fails.** The key has explicit ACEs for other SIDs but the user owns it and inherits full control,
  so `DeleteSubKeyTree` succeeds. If it fails, `OracleSession` reports the error and the run fails. The next run
  deletes the tree first, and that delete is checked the same way.

## 18. Key, command and context captures (keys, context, binary, print, security, remote, manifest)

Added for release 0.1.2. These captures open classic menus, dialogs and the Advanced Security Settings
window, read them, and cancel them. They never change a value outside BWC-Oracle. The remote capture is
the only one that connects to another registry, and it writes nothing to it.

- **Key delivered by message, modifier not seen.** Accelerators are matched by TranslateAccelerator, which
  reads the modifier state of the thread's keyboard queue. A posted WM_KEYDOWN does not set that state. The
  `keys` capture attaches its input queue to regedit's GUI thread (AttachThreadInput), sets the Shift, Ctrl and
  Alt bits with SetKeyboardState, posts the key, then clears the state and detaches in a `finally`. Alt keys are
  posted as WM_SYSKEYDOWN. A key whose attach fails is recorded as `measured: false` with the Win32 error.
- **Key not delivered, read as "no effect".** Each key's accelerator command id comes from the resource dump
  (VK_F4 to 642, Alt+1 to 689 through Alt+9 to 697, Alt+Up to 698, Shift+F10 to 896). The record carries
  `deliveryMismatch: true` when the key changed nothing but the same command id posted with WM_COMMAND did
  change something. Such a key is not reported as ignored by classic.
- **Command id that writes.** The WM_COMMAND ids (642, 689 to 698, 896) are posted with the seeded key under
  BWC-Oracle selected, and the tree is deleted afterwards. None of these ids is a menu item that writes a value
  (Delete 913, Rename 914, New 663 to 677 are not in the list). Each command runs in a fresh process.
- **Popup or dialog left open.** Every new top-level window of the started process is closed after it is
  measured: popups with Escape then WM_CANCELMODE, dialogs with IDCANCEL then WM_CLOSE. A window that does not
  close fails the step and the process is killed.
- **Value context menu opened on the wrong item.** A posted right click takes its position from the real
  cursor when the context menu is generated (WM_RBUTTONUP becomes WM_CONTEXTMENU at GetMessagePos), and a
  WM_CONTEXTMENU with a point may be ignored in the same way. The `context` capture opens a value's menu with
  Shift+F10 with the value selected and the list focused. The point and right-button methods were tried
  first and depend on the real cursor position (one run opened RT_MENU 105, another RT_MENU 106), so the
  capture no longer uses them for values. A popup is accepted only when its leaf ids equal RT_MENU 105
  (Modify, Modify Binary Data, Delete, Rename). A popup that matches RT_MENU 106 (the list background, New)
  is never accepted as a value menu. If Shift+F10 does not reach RT_MENU 105, the value is recorded as
  `measured: false`. It is never guessed from position.
- **Text edit menu attributed to the wrong control.** RT_MENU 108 (Cut, Copy, Paste, Delete, Select All) and the
  system edit menu (ids 768 to 772 for Cut, Copy, Paste, Clear and Undo, plus the Unicode control entries) have
  different ids. Each popup is attributed by its item ids, not by where the click landed. Each text control is
  recorded with every method tried (`attempts`), and a popup that matches neither is recorded as no template.
- **Binary Data variant misidentified.** The variant is read from the live control ids, not from the caption.
  Template 2020 has 8 controls and template 2021 has 10, including id 2026 (the "Value Type:" label). A dialog
  whose control ids match neither is recorded as `unknown` and the step fails for that value type.
- **Print dialog not found.** The Print dialog's owner is not guaranteed to be the main window (the earlier
  `dialogs` run saw nothing owned by it). `PrintCapture` looks for new top-level windows of the started process
  of any owner. The run never sends IDOK to the Print dialog, so no job is submitted, and the dialog is closed
  with IDCANCEL. A physical printer is never used.
- **Print to PDF not measured.** Printing needs IDOK on the Print dialog and then a save-path dialog, which
  this capture does not drive. The record says `measured: false` with that reason. Only the printer list
  (installed or not, as a boolean) is read, through `System.Drawing.Printing`.
- **Permissions sheet and Advanced window cancelled wrongly.** The security capture reuses the Permissions
  helpers from `PermissionsCapture` and presses the Advanced button by BM_CLICK, as that capture does. It never
  presses any other button on those windows. Every window is closed with IDCANCEL. "Disable inheritance" and
  "Replace all child object permission entries" are read only, never toggled.
- **Remote connect writes to the registry.** Connecting to 127.0.0.1 is a read-only tree attach. It is the one
  place where this tool posts IDOK (the Select Computer dialog's OK), which is an exception to section 6. The
  exception applies only when the edit control reads back exactly `127.0.0.1` before the OK is posted. Otherwise
  the dialog is cancelled. The remote capture never posts a command to a remote key other than menu reads.
- **Wrong computer connected.** The only target is the loopback address. The tree node that appears must carry
  `127.0.0.1` in its text before its children are read. Any other new node stops the step.
- **Message box from a failed connect.** A message box owned by the started process is closed with IDCANCEL
  and its text is recorded, as in section 7. Remote measurements are then recorded as `measured: false` with the
  message.
- **Import picker chooses a file.** File > Import first opens a computer picker. The picker is identified by its
  control ids, not by the template number alone. The capture cancels it and never reaches the file dialog. If the
  first window opened is a file dialog, it is closed with IDCANCEL.
- **Disconnect dialog chooses the wrong entry.** The disconnect dialog (template 107) is set to the entry whose
  text contains `127.0.0.1`. OK is posted only after that entry is selected and read back. Otherwise the dialog
  is cancelled and the record says so.
- **Redaction.** The current user's SID and the machine name are replaced by `<SID>` and `<COMPUTER>` in every
  text that leaves the capture, tracked or untracked. Redaction runs before the JSON is written.
- **Personal data in tracked output.** Tracked JSON carries ids, flags, control classes and geometry. Item
  text, captions and titles go to `docs/parity/oracle/local/`, which is untracked.
- **Manifest read wrong.** The manifest is read with LoadLibraryEx (LOAD_LIBRARY_AS_IMAGE_RESOURCE) and
  FindResource on RT_MANIFEST. A missing resource or an XML parse error fails the step. The manifest is not
  executed and regedit is not started for it.

## 19. String parity check (scripts/check-regedit-strings.js)

`scripts/check-regedit-strings.js` compares each RT_STRING of the regedit.exe dump with BWC's strings. It runs in
`npm run validate`. It must skip cleanly on CI, where the dump is absent.

- **Dump absent.** The script prints a skip note and exits 0. It never writes `string-parity.json` in that case.
- **Dump of another schema.** A missing `modules[0].stringTables` or a missing `id` or `text` fails with exit 1.
- **Partial output.** The output is written to a temporary file and renamed into place, so a reader never sees a
  partial file.
- **Oracle text in tracked output.** The output holds ids and statuses only, sorted by id. No text, no timestamp,
  no machine name.
- **Template misread as equal.** Text with `%%u` or `%1!...!` tokens is reported as `template` and not compared.
  `%1` and `%2` are converted to `{0}` and `{1}` before comparison.
- **False "absent".** A resource that BWC builds by concatenation or interpolation is not found. The check reports
  it as `absent`. The status means "text not found by exact match", not "BWC lacks the feature".
- **Line endings.** Decoded XML entities (`&#13;&#10;` and the like) are compared with CRLF converted to LF on
  both sides, so a line break encoded differently does not read as a difference.
- **Comment misattribution.** An id is attributed to a resw entry only when the comment immediately before the
  `<data>` element cites `Oracle <id>` (the ids after "Oracle" are parsed from that comment). Ids in other
  comments are ignored.
- **Source scan too broad or too narrow.** The C# literal search reads only `.cs` files under `src/`, skipping
  `bin` and `obj`, and matches the escaped literal form. Verbatim strings and raw literals are not matched.

## 20. Text typed into a dialog by message (remote connect)

The Select Computer dialog's name box ignored WM_SETTEXT and WM_CHAR posted to it (0.1.2 run). The `remote`
capture types the name by message to a control that it first gives keyboard focus, so no keyboard state of
the user is used and no window is brought to the foreground.

- **Typing into the wrong window.** Focus is set with SetFocus on this thread's input queue, after
  AttachThreadInput to regedit's GUI thread. The focus change stays inside regedit's queue. The keys are
  posted with PostMessage to the focused control. Nothing is sent with SendInput or keybd_event, so
  the user's physical keyboard and the foreground window are not used. The manager's request for SendInput is
  not followed, because section 5 forbids it and it would type into whatever window the user has active.
- **Name box that rejects input.** A control that reads back the empty string after WM_SETTEXT and after
  typing is recorded with its class, id and whether it has ES_READONLY (0x0800). Rich edit controls
  (RICHEDIT50W) are candidates too. If no candidate accepts `127.0.0.1`, the step is `measured: false` and
  the dialog is cancelled with IDCANCEL. OK is not pressed.
- **Text accepted but wrong.** The text must read back exactly `127.0.0.1` before OK is posted. Any other text
  cancels the dialog.
- **Connect starts the service.** The RemoteRegistry service is trigger started. The tool does not start,
  stop or configure it. The connect is the only action that starts it, and the tool does not touch it.

## 21. Print dialog outside regedit (print capture)

On Windows 11 the Print dialog can open as a top-level window that is not owned by regedit's process. The
`print` capture therefore looks at every new visible top-level window on the desktop after File > Print, for up
to 60 seconds, not only at windows of the started process.

- **Foreign window identified by process.** Each new window is recorded with its class and the name of its
  owning process. Titles go to the local text file. A window is interacted with only when its owning process is
  one of the print hosts (its process name contains "Print", or it is an ApplicationFrameWindow whose title
  contains "Print"). Any other new window is recorded and not touched, because it may belong to the user.
- **Printer not verified.** The XAML print dialog's controls cannot be read through Win32. Reading its printer
  selection needs UI Automation, which this tool does not reference (a new package would need a version in
  Directory.Packages.props, which is outside this piece's file list). Without a verified selection of exactly
  `Microsoft Print to PDF`, the dialog is cancelled and the record says `printed: false`. Print and OK are never
  posted, and the physical printers on this machine are never used.
- **Cancel by message.** Escape is posted to the dialog with PostMessage, then WM_CLOSE if it stays visible for
  3 seconds. The record says whether it closed.
- **PDF not produced.** No PDF is produced by this tool, so no file is written under artifacts/oracle/. The
  record says `measured: false` for the PDF page count and size, with the reason.

## 22. Print dialog read with UI Automation (print capture)

The Windows print host (a CoreWindow of PrintDialog, or an application frame) cannot be read through Win32. The
`print` capture reads it with UI Automation (System.Windows.Automation from Microsoft.WindowsDesktop.App). UI Automation
reads properties and patterns. It does not send input.

- **Reading changes the dialog.** Only read patterns are used: Name, ControlType, IsEnabled, the Toggle and Value
  patterns for state and text, and Selection for the selected item. The one exception, authorized by the manager, is
  the hyperlink named "More settings" (ControlType Hyperlink, exact name): it is expanded with ExpandCollapse.Expand
  when it has that pattern, or invoked with Invoke.Invoke when it has only that. This changes the display so that the
  page range controls become visible. No other Invoke, Select, SetValue or Expand is called on the print dialog, and
  nothing is printed.
- **Wrong printer chosen.** The printer is read from the printer selector's value. No printer is changed. A selector
  whose value is not exactly `Microsoft Print to PDF` is recorded as is, and the dialog is cancelled.
- **PDF printed to a physical printer.** Print is pressed only when UI Automation confirms the selector value is
  exactly `Microsoft Print to PDF`. Otherwise the dialog is cancelled and nothing is printed. The PDF path is not
  implemented in this pass: the save-path dialog would need its file name set, which this capture does not do, so the
  record says `measured: false` with that reason.
- **Range panel not in the tree.** Whether regedit's template 108 panel appears inside the host is recorded as a
  boolean from the element names (the words "branch" and "range", case-insensitive). The names themselves go to the
  local text, not the tracked file.
- **Element tree too large.** The walk stops at depth 12 or 800 elements, and the record says so.
- **Print host not found.** The 60 second search and the cancel in section 21 still apply.
