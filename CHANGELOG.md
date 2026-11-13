# Changelog

## [Unreleased]

## [0.1.0] - Unreleased

First release. Better Windows Config is early and unstable: expect rough edges, and keep a backup
(File > Export) before you change registry keys you care about.

### Added

- Hub: a dense launcher for the Better Windows Config tools, with classic menus, About, update
  settings (stable channel), and a signed self-updater.
- Registry Editor: a re-implementation of classic `regedit.exe` with its layout and density, in
  light, dark, and high contrast themes (View > Theme overrides the system setting).
- Registry Editor: classic tree and list behavior (type-ahead, numpad expand and collapse, Ctrl and
  Shift selection, inline New Key #1 / New Value #1 and rename, column resizing), classic menus with
  mnemonics, and the classic accelerators.
- Registry Editor: editors for String, Expandable String, DWORD and QWORD (hexadecimal and decimal
  base), Multi-String, and Binary values, and the Binary Data viewer for resource values.
- Registry Editor: Find with the classic options and progress window, Favorites, Permissions
  (the system security editor), Print, Load Hive and Unload Hive, and read-only remote registry
  browsing.
- Registry Editor: `.reg` import and export compatible with classic regedit (v5 and REGEDIT4,
  text and hive export, selected branch or the whole registry), with the classic progress windows
  and result messages.
- Registry Editor: command-line switches `/s`, `/e`, `/a`, and `/m`, `regedit file.reg`, single
  instance, and persisted window placement, splitter, column widths, and font.
- Installer for x64 and ARM64, with Authenticode-signed binaries.

### Known issues

- Remote registry is read-only.
- Not every classic regedit behavior has been audited yet; report differences you notice.
