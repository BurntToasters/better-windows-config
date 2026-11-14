# ⬇️ Downloads

| <img height="20" src="https://raw.githubusercontent.com/BurntToasters/bcls/main/media/windows.png" /> Windows | <img height="20" src="https://raw.githubusercontent.com/BurntToasters/bcls/main/media/mac.png" /> macOS | <img height="20" src="https://raw.githubusercontent.com/BurntToasters/bcls/main/media/linux.png" /> Linux |
| :--- | :--- | :--- |
| **Setup EXE:** [x64](https://github.com/BurntToasters/better-windows-config/releases/download/v0.1.1/BetterWindowsConfig-0.1.1-win-x64-setup.exe) / [arm64](https://github.com/BurntToasters/better-windows-config/releases/download/v0.1.1/BetterWindowsConfig-0.1.1-win-arm64-setup.exe) | | |

> Better Windows Config is Windows only. It needs Windows 10 version 2004 (build 19041) or later, or Windows 11.

> [!IMPORTANT]
> The `.sig` and `.minisig` files in this repo are NOT normal GPG signatures. They are for Better Windows Config's built-in updater to verify the integrity of updates before downloading and installing them.
>
> The `.asc` files are my normal GPG signatures which you can verify using my GPG Public Key: https://tuxedo.rosie.run/GPG/BurntToasters_0xF2FBC20F_public.asc
>
> ⚠️ This app is currently unstable. Bugs, issues, and rough edges are expected. Keep a backup (`File` > `Export`) before you change registry keys you care about.

### ℹ️ Enjoying Better Windows Config? Consider [❤️ Supporting Me! ❤️](https://rosie.run/support)

Better Windows Config! Faithful, modern re-implementations of the classic Windows tools, starting with the Registry Editor.

## Changes in `v0.1.1:`

> [!IMPORTANT]
> **Coming from 0.1.0?** Download the 0.1.1 setup EXE above and run it. 0.1.0 can't update itself, and the 0.1.0 installers refuse to run on most current Windows builds. Your settings are kept. From 0.1.1 on, updates install from inside the app.

- **Updater:** Fixed checking for and downloading updates from GitHub, which always failed in 0.1.0.
  - The Hub now checks for updates at startup, quietly, and only speaks up when an update is available. Turn it off in update settings.
  - After an update installs, the Hub reopens by itself. If the install fails, the Hub reopens and says so.
  - Clearer update messages, and the download window shows its progress.
  - Beta is hidden until a beta channel exists.
- **Installer:** Fixed the installers refusing to run on Windows builds newer than 19041, and the ARM64 installer refusing ARM64 PCs.
  - Fixed installing over an existing copy, which always stopped with an error.
  - A failed or interrupted upgrade no longer blocks the next one, and the installer refuses drive roots and folders that belong to something else.
- **Registry Editor:** Fixes from the first audit.
  - A `.reg` file opened while the Registry Editor is already running now waits for an open dialog instead of being dropped, and relative paths open the right file.
  - Closing the window now waits for a running import, Load Hive, or Unload Hive.
  - A key whose values can't be read shows the classic "Cannot open" error instead of an empty list. Load Hive says up front when the privilege is missing.
  - `View` > `Font` now remembers the face and style, not just the size.
  - Find skips a match it can't show instead of stopping, and searches again after you move the selection.
  - `/e` of the whole registry writes to a temp file and only replaces the target when the export finishes.
  - Export no longer fails on a key named `.` or `..`.
  - Binary editor: typing into an empty value and past the last byte works, Backspace deletes the byte before the caret, and paste in overwrite mode overwrites.
  - Editing a Multi-String value no longer drops an odd trailing byte.
  - Favorites: a stray default value under the Favorites key no longer breaks the menu.
  - Renaming a value commits when you click away, as the tree does.
- **Hub:** Remembers the monitor and maximized state of the window. Pressing and holding Enter no longer launches a tool repeatedly. Canceling the UAC prompt for a tool is no longer shown as an error.
- **Misc:** Crash reports are always written to the log first, and log folders are capped.

### Known issues

- **UI:** Remote registry browsing is read-only, and Modify is unavailable for remote values.
- **UI:** A value literally named `(Default)` is hard to tell apart from the key's default value.
- **Misc:** Not every classic regedit behavior has been audited yet. If you notice a difference, please open an issue :)

## ℹ️ Release Info

- **GPG Signed:** Release assets come with my `.asc` GPG signatures to ensure authenticity.
- **GPG Key:** You can get my public GPG key here: https://tuxedo.rosie.run/GPG/BurntToasters_0xF2FBC20F_public.asc
- **Code Signing:** Windows binaries and installers are Authenticode-signed with Azure Artifact Signing.
- **Updates:** The built-in updater only installs an update when its manifest and installer signatures match the key built into the app.
