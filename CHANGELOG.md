# Changelog

## [Unreleased]

### Added

- Registry Editor: classic dialog system with classic message boxes, dense control styles, and
  editors for String, DWORD, QWORD, Multi-String, and Binary values.
- Registry Editor: classic menus with measured metrics, check marks, mnemonics, and letter navigation.
- Registry Editor: classic tree and list behavior, including type-ahead, numpad expand and collapse,
  Ctrl and Shift selection, and column resizing.
- Registry Editor: theme support (System, Light, Dark) with a View menu override, and light colors
  matched to classic regedit.
- Registry Editor: runtime icons loaded from the system, with drawn fallbacks.
- Registry Editor: app services. Log, crash handler, single instance with `/m`,
  `regedit file.reg`, View > Font, Help > About, and persisted window placement, splitter, and
  column widths.
- Registry Editor: Load Hive and Unload, enabled only under the right roots.
- Registry Editor: Favorites, Permissions, Print, Find, hex viewer and editor, context menus, and
  remote registry browsing (read-only).
- Registry engine: classic-compatible `.reg` import (missing ancestors, lenient parsing, partial
  import reports) and export in the v5 and REGEDIT4 formats.
- Command-line: `/s`, `/e`, `/a`, and `/m` switches.
- Hub: classic menus, update check and update dialogs, and a dense launcher layout.
- Updater: manifest model, minisign verification, download and hash checks, and an apply helper.
  The release key is still a placeholder, so updates fail closed.
- Oracle tooling: `npm run oracle` regenerates the classic regedit captures from the local
  `regedit.exe`. Microsoft content is no longer tracked.
- Installer: NSIS script and build script (not yet compiled), and third-party notices generation.
- Opt-in elevated E2E runner. Nothing elevates during normal development.

### Changed

- Mutating E2E tests run only in the E2E flavor. The ship flavor runs read-only tests. Every
  mutating test first checks that the address bar is inside the sandbox key.
- Gate results are per flavor (`result-<rid>-<flavor>.json`). The gate artifact records the commit,
  dirty paths, tool versions, and per-step logs.
- The registry write guard is stricter. Sandbox writes refuse symbolic links, root renames, and
  restores from outside the sandbox. The static writer check covers more entry points.
- The Registry Editor's code-behind is split into feature controllers.
- Test hooks that narrow Find or show debug text in UI Automation compile only into the E2E flavor.

### Fixed

- Embedded NUL characters in value names no longer hang enumeration or return another value's data.
- Classic dialogs paint their own colors, so they are readable in any host app and theme.
- Import continues past bad lines and reports refused files and partial writes.

### Known issues

- Changing the list font live breaks the window's UI Automation tree (the row height check fails).
- The update release key is a placeholder. The update server is not configured.
- The installer is not compiled yet. NSIS is not installed on the build machine.

## [0.1.0] - Unreleased

- Initial project skeleton.
