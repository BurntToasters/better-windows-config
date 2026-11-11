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

Steps: `resources`, `export`, `metrics`, `dialogs`, `menus`, `import`, `find`, `sort`. `scripts/oracle.js`
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

## Not captured

- Edit String, Edit Expandable String, Edit DWORD, Edit QWORD, Edit Multi-String and Edit Binary
  (`dialogs`): the value commands did not run by message. Both a double click on the value name and
  the keyboard context-menu request opened the list background menu (New only), even with keyboard
  focus in the list and the value selected. Reads of `regedit-dialog-rects.json` and `notCaptured`
  show the reason. Needs an interactive session to see which input regedit uses for the value menu.
- Print (File > Print): no dialog appeared within 25 seconds on this machine, and the window list at
  the timeout shows only the main window. Not verified further.
- The value context menu (Modify, Modify Binary Data, Delete, Rename) in `regedit-menu-metrics.json`:
  the same cause as the edit dialogs. The menu recorded as `list-background-context` is the list
  background menu, and that is what it is.
- Context menus for the remote root and the text edit: not opened. The remote root needs a
  connection, and the text edit only exists while an inline edit is open.

## Scale

This machine runs at 200 percent. The UI captures run with `--allow-dpi`, so the tracked outputs
record `dpi.system` and `dpi.window`. Values captured at 192 DPI are not 96 DPI values; divide by
`dpi.window / 96`. See VIEW-001 in `docs/parity/regedit-inventory.md`.
