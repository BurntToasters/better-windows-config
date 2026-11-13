# ⬇️ Downloads

| <img height="20" src="https://raw.githubusercontent.com/BurntToasters/bcls/main/media/windows.png" /> Windows | <img height="20" src="https://raw.githubusercontent.com/BurntToasters/bcls/main/media/mac.png" /> macOS | <img height="20" src="https://raw.githubusercontent.com/BurntToasters/bcls/main/media/linux.png" /> Linux |
| :--- | :--- | :--- |
| **Setup EXE:** [x64](https://github.com/BurntToasters/better-windows-config/releases/download/v0.1.0/BetterWindowsConfig-0.1.0-win-x64-setup.exe) / [arm64](https://github.com/BurntToasters/better-windows-config/releases/download/v0.1.0/BetterWindowsConfig-0.1.0-win-arm64-setup.exe) | | |

> Better Windows Config is Windows only. It needs Windows 10 version 2004 (build 19041) or later, or Windows 11.

> [!IMPORTANT]
> The `.sig` and `.minisig` files in this repo are NOT normal GPG signatures. They are for Better Windows Config's built-in updater to verify the integrity of updates before downloading and installing them.
>
> The `.asc` files are my normal GPG signatures which you can verify using my GPG Public Key: https://tuxedo.rosie.run/GPG/BurntToasters_0xF2FBC20F_public.asc
>
> ⚠️ This app is currently unstable. Bugs, issues, and rough edges are expected. Keep a backup (`File` > `Export`) before you change registry keys you care about.

### ℹ️ Enjoying Better Windows Config? Consider [❤️ Supporting Me! ❤️](https://rosie.run/support)

Better Windows Config! Faithful, modern re-implementations of the classic Windows tools, starting with the Registry Editor.

## Changes in `v0.1.0:`

### Welcome to Better Windows Config! This is the very first release :)

- **NEW - Registry Editor:** Added a re-implementation of classic `regedit.exe` that keeps its layout and density, in light, dark, and high contrast themes. `View` > `Theme` overrides the system setting.
  - Classic tree and list behavior: type-ahead, numpad expand and collapse, Ctrl and Shift selection, inline `New Key #1` / `New Value #1` and rename, and column resizing.
  - Classic menus with mnemonics, and the classic accelerators.
  - Editors for String, Expandable String, DWORD and QWORD (hexadecimal and decimal), Multi-String, and Binary values, plus the Binary Data viewer for resource values.
  - Find with the classic options and progress window, Favorites, Permissions (the system security editor), Print, Load Hive, and Unload Hive.
  - `.reg` import and export compatible with classic regedit (v5 and `REGEDIT4`, text and hive export, a selected branch or the whole registry), with the classic progress windows and result messages.
  - Command-line switches `/s`, `/e`, `/a`, and `/m`, plus `regedit file.reg`.
  - Single instance, with remembered window placement, splitter, column widths, and font.
  - Read-only remote registry browsing.
- **NEW - Hub:** Added a launcher for the Better Windows Config tools, with classic menus, About, and update settings.
- **Updater:** Added a signed self-updater on the stable channel. Update manifests are signed and expire after one year.
- **Windows:** Added per-user setup installers for x64 and ARM64. The app binaries and both installers are Authenticode-signed.

### Known issues

- **UI:** Remote registry browsing is read-only.
- **Misc:** Not every classic regedit behavior has been audited yet. If you notice a difference, please open an issue :)

## ℹ️ Release Info

- **GPG Signed:** Release assets come with my `.asc` GPG signatures to ensure authenticity.
- **GPG Key:** You can get my public GPG key here: https://tuxedo.rosie.run/GPG/BurntToasters_0xF2FBC20F_public.asc
- **Code Signing:** Windows binaries and installers are Authenticode-signed with Azure Artifact Signing.
- **Updates:** The built-in updater only installs an update when its manifest and installer signatures match the key built into the app.
