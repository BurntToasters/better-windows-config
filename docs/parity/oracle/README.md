# Classic regedit oracle

The oracle is the classic `C:\Windows\regedit.exe` on this machine. Parity work measures the
re-implementation against it. This folder holds two kinds of files:

- **Tracked** files are numbers, flags, and goldens made from BWC's own test data. They are in git.
- **Untracked** files are Microsoft content: resource text, dialog and menu screenshots, and control
  text. They are listed in `.gitignore` and regenerated on each machine.

## How to regenerate

From the repository root, on Windows, with the desktop at 100 percent scale when possible:

```
npm run oracle                          # every step, in order
npm run oracle -- --only dialogs        # one step
npm run oracle -- --only metrics,menus  # several steps
npm run oracle -- --list                # step names
npm run oracle -- --skip-build          # reuse built tool binaries
```

Steps: `resources`, `export`, `metrics`, `dialogs`, `menus`, `import`, `find`, `sort`, `permissions`, `keys`, `context`,
`binary`, `print`, `security`, `remote`, `manifest`. `node scripts/check-regedit-strings.js` (run by `npm run validate`)
compares the resource strings with BWC's. `scripts/oracle.js`
builds both tools (Release, host platform), runs them, then deletes `HKCU\Software\BWC-Oracle`. Its run
record (step names, exit codes, timings) goes to `artifacts/oracle/last-run.json`, which is never tracked.

`resources` runs `tools/Bwc.Oracle.Resources` and writes `regedit-resources.json` and
`regedit-resources.md`. The other steps run `tools/Bwc.Oracle.Capture`.

The tools never show UAC. Each classic regedit process starts with `__COMPAT_LAYER=RunAsInvoker`
on the child only. Each capture holds the named mutex `Local\BwcGuiTestLock`, the same mutex the E2E
tests use, and restores the Regedit applet state (`LastKey`, `View`, `FindFlags`, `Favorites`) after
it runs. The failure modes are in `docs/failure-modes/oracle.md`.

## Files

| File | Tracked | Made by | What it is |
| --- | --- | --- | --- |
| `regedit-resources.json` | no | `resources` | Dump of regedit.exe and en-US regedit.exe.mui: menus, dialogs, accelerators, string tables. Read by the classic dialog templates and the menu table as their source. |
| `regedit-resources.md` | no | `resources` | Review rendering of the same dump. |
| `regedit-100dpi.png` | no | `metrics` | Main window screenshot (PrintWindow). The file name says 100 percent; the capture is at the desktop's scale, and `regedit-metrics.json` records the DPI. |
| `regedit-metrics.json` | yes | `metrics` | Main window, menu bar, tree, list, status bar, and font metrics in device pixels relative to the window. |
| `regedit-dialog-rects.json` | yes | `dialogs` | Per dialog: window and client sizes, and each control's id, class, enabled and visible flags, and rectangle relative to the client origin. Numbers only. |
| `regedit-export-range.json` | yes | `export-range` | Classic Export range panel (control 1282 enable state and start text under each radio choice) and the state of the missing-key box. The box caption and text are not measured. Texts go to `local/export-range-text.json`. |
| `dialogs/*.png` | no | `dialogs` | Screenshot per dialog (`edit-string`, `find`, `export`, `load-hive`, `permissions`, and the others in the rects file). Inline renames are screenshots of the main window. |
| `local/dialog-text.json` | no | `dialogs` | Dialog titles and control text, the Microsoft strings behind the rectangles. |
| `regedit-menu-metrics.json` | yes | `menus` | Popup metrics for File, Edit, View, Favorites, Help, the tree-key context menu and the list background context menu: item and separator heights, padding, text inset, check gutter, submenu arrow gutter. Measured from the open popup's pixels and its own menu handle. |
| `local/menu-text.json` | no | `menus` | Item text per menu, the Microsoft strings behind the metrics. |
| `exports/bwc-oracle-v5.reg` | yes | `export` | Classic `/e` export of the seeded tree (Unicode v5). Golden. |
| `exports/bwc-oracle-regedit4.reg` | yes | `export` | Classic `/a` export of the same tree (REGEDIT4). Golden. |
| `exports/bwc-oracle-many300-v5.reg` | yes | `export` | Classic `/e` export of the 300-value key. Golden. |
| `seed-tree.md` | yes | every capture that seeds | The seeded tree: names, types, byte lengths, and the read-back check. |
| `import-behavior.json` | yes | `import` | Classic `regedit /s` on 30 malformed or unusual `.reg` files. Each record has the file's lines, its encoding, the exit code, and the BWC-Oracle subtree after the import. Files whose headers fall outside BWC-Oracle are recorded as refused and never run. |
| `find-sequence.json` | yes | `find` | Hits from classic Find for six option sets over a seeded tree with the needle `ab`: key path and value name per hit, and why the sequence ended. |
| `sort-order.json` | yes | `sort` | The order classic regedit lists seeded value names in, and whether clicking the Name and Type column headers reorders them. |
| `regedit-keys.json` | yes | `keys` | Each navigation key and accelerator command, with focus in the tree and in the list: what changed, and the windows it opened (ids, classes, template matches). |
| `regedit-context.json` | yes | `context` | Context menus (tree, value by type, list background, text edit controls): item ids, enabled flags, RT_MENU template matched, and every method tried. |
| `regedit-binary.json` | yes | `binary` | View > Display Binary Data per value type (variant, default radio, Format radios) and the Modify Binary Data dialog (template). |
| `regedit-print.json` | yes | `print` | File > Print enabled state at Computer, a hive and a key; the Print dialog range panel when it opens; the PDF export status. |
| `regedit-security.json` | yes | `security` | Edit > Permissions sheet and Advanced Security Settings: controls, button states, and check box state. Cancelled. |
| `regedit-remote.json` | yes | `remote` | Connect Network Registry, the remote node's menus and File menu states, the Import picker, Disconnect. Measured only when the connect succeeds. |
| `regedit-manifest.json` | yes | `manifest` | The embedded RT_MANIFEST's requestedExecutionLevel and uiAccess, read as an image resource. regedit is not started. |
| `string-parity.json` | yes | `check-regedit-strings.js` | Per RT_STRING id: `equal`, `differs`, `template` or `absent` against BWC's resw and C# literals. Ids and statuses only. |
| `local/keys-text.json`, `local/context-text.json`, `local/binary-text.json`, `local/print-text.json`, `local/security-text.json`, `local/remote-text.json` | no | the steps above | Item text, captions and titles for each new step. The user's SID and the machine name are replaced by `<SID>` and `<COMPUTER>`. |

The import working files are in `artifacts/oracle/import` (git-ignored). The run record is in
`artifacts/oracle/last-run.json`.

## Findings from the new captures

- Import (`import-behavior.json`): classic `regedit /s` keeps going after a line it cannot parse. A
  bad line in the middle of a section is skipped, and the sections after it are imported. A hex value
  that fails to parse is dropped, and the values after it are imported. A header with no closing
  bracket imports nothing, and no error is reported in the exit code (0). A version 5 file in UTF-16
  without a byte-order mark imports nothing (exit 0). A missing-ancestor section creates the ancestors.
- Find (`find-sequence.json`): the search ends when it moves out of the start key's subtree in some
  option sets, and ends with no change in others. Keys and values are matched by substring, case
  insensitively. DWORD and binary data are not searched. Whole-string matching applies to names and
  string data.
- Sort (`sort-order.json`): value names are listed with `(Default)` first, then case-insensitively,
  with digit runs compared as text (`1`, `10`, `2`, not natural order). Clicking the Name or Type
  column header does not reorder the list.

## Findings from the 0.1.2 captures

- Keys (`regedit-keys.json`): F4 (command 642) moves focus to the address bar and opens its Auto-Suggest dropdown.
  Alt+Up (698) and Backspace in the tree go to the parent key. Shift+F10 (896) opens a context menu. Alt+1 to Alt+9
  (689 to 697) changed nothing in this state, and the Favorites they may select were not verified. Shift+F6 has no
  accelerator and changed nothing. No key had a delivery mismatch (the key and its command agreed).
- Context (`regedit-context.json`): the value menu (RT_MENU 105: Modify, Modify Binary Data, Delete, Rename) opens with
  Shift+F10 on a selected value, for all eight types, with Modify and Modify Binary Data enabled. The point and
  right-button methods opened the list background menu (RT_MENU 106), except once for REG_BINARY, so they are not
  relied on. RT_MENU 108 (Cut, Copy, Paste, Delete, Select All) is the menu of the Edit Binary Value hex area. The
  address bar, the Find what box and the Edit String data box show the system edit menu (ids 768 to 772 and the
  Unicode entries). The Computer root menu matches RT_MENU 107 (Collapse, Export, Disconnect). The hive and key menus
  match RT_MENU 104 without its "Go to HKEY_LOCAL_MACHINE" item, so no template is matched.
- Binary (`regedit-binary.json`): View > Display Binary Data is enabled for all nine samples, and every one opens
  template 2021 (variant B, with the Value Type label). The Format radios are Byte, Word, Dword and Qword, and Byte
  (2023) is checked by default. Modify Binary Data on a REG_BINARY value opens template 103, captioned
  "Edit Binary Value". Template 102 has the same control ids, so the caption breaks the tie.
- Print (`regedit-print.json`): File > Print is enabled with Computer, HKEY_CURRENT_USER and a key selected.
- Security (`regedit-security.json`): the Permissions sheet is titled "Permissions for <key>" and has one tab, Security.
  The Advanced Security Settings window has buttons OK, Cancel, Apply, Add, Remove, View, Disable inheritance, and the
  check box "Replace all child object permission entries with inheritable permission entries from this object".
- Manifest (`regedit-manifest.json`): requestedExecutionLevel is `highestAvailable`, uiAccess false.
- String parity (`string-parity.json`): 278 RT_STRING ids: equal 135, differs 7, template 4, absent 132.

## Not captured

- Edit String, Edit Expandable String, Edit DWORD, Edit QWORD, Edit Multi-String and Edit Binary
  (`dialogs`): the value commands did not run by message in the `dialogs` step. The `context` step
  opens the value menu with Shift+F10 on a selected row, and the Modify commands (912, 917) open the editors.
- Print (File > Print, `print`): the File > Print command is enabled (see `regedit-print.json`), but no Print
  dialog appeared within 25 seconds. The window list at the timeout shows only the main window and tooltips.
  Not measured. The PDF export is not measured either: it needs IDOK and a save dialog this capture does not drive.
- Remote (`remote`): the Connect Network Registry dialog's edits did not accept `127.0.0.1` by WM_SETTEXT or by
  WM_CHAR, so no remote node exists and the remote menus, the Import picker and the disconnect dialog are not
  measured. The reason is in `regedit-remote.json`.
- Security (`security`): the Advanced Security Settings window is a DirectUI window. Its tab names and the Owner line
  are not Win32 controls, so they are not measured. Its buttons and the check box are.
- Binary first-line text (`binary`): the Format list is a ListBox whose LB_GETTEXT returns no string, so the first line
  is recorded as `firstLineReadable: false`.
- Context menus for the remote root: not opened. The remote root needs a connection.

## Scale

This machine runs at 200 percent. The UI captures run with `--allow-dpi`, so the tracked outputs
record `dpi.system` and `dpi.window`. Values captured at 192 DPI are not 96 DPI values; divide by
`dpi.window / 96`. See VIEW-001 in `docs/parity/regedit-inventory.md`.
