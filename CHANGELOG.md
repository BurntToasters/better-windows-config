# ⬇️ Downloads

| <img height="20" src="https://raw.githubusercontent.com/BurntToasters/bcls/main/media/windows.png" /> Windows | <img height="20" src="https://raw.githubusercontent.com/BurntToasters/bcls/main/media/mac.png" /> macOS | <img height="20" src="https://raw.githubusercontent.com/BurntToasters/bcls/main/media/linux.png" /> Linux |
| :--- | :--- | :--- |
| **Setup EXE:** [x64](https://github.com/BurntToasters/better-windows-config/releases/download/v0.2.0/BetterWindowsConfig-0.2.0-win-x64-setup.exe) / [arm64](https://github.com/BurntToasters/better-windows-config/releases/download/v0.2.0/BetterWindowsConfig-0.2.0-win-arm64-setup.exe) | | |

> Better Windows Config is Windows only. It needs Windows 10 version 2004 (build 19041) or later, or Windows 11.

> [!IMPORTANT]
> The `.sig` and `.minisig` files in this repo are NOT normal GPG signatures. They are for Better Windows Config's built-in updater to verify the integrity of updates before downloading and installing them.
>
> The `.asc` files are my normal GPG signatures which you can verify using my GPG Public Key: https://tuxedo.rosie.run/GPG/BurntToasters_0xF2FBC20F_public.asc
>
> ⚠️ This app is currently unstable. Bugs, issues, and rough edges are expected. Keep a backup (`File` > `Export`) before you change registry keys you care about.

### ℹ️ Enjoying Better Windows Config? Consider [❤️ Supporting Me! ❤️](https://rosie.run/support)

Better Windows Config! Faithful, modern re-implementations of the classic Windows tools, starting with the Registry Editor.

## Changes in `v0.2.0:`

- **Local Group Policy Editor:** New tool, a faithful clone of the classic Local Group Policy Editor.
  - Browse the Administrative Templates of Computer Configuration and User Configuration.
  - The policy setting dialog shows the policy's options and comment, with Previous and Next to move through the policies in order.
  - Saving writes the local policy files (`Registry.pol`, `gpt.ini`, and `comment.cmtx`) through one locked writer. Before the first write of a session, BWC keeps a backup of the three files, the last 10. After a save, the policy is refreshed.
  - Without administrator rights, on Windows Home, or without write access to the policy folder, the editor opens read-only and shows a notice.
  - Filter Options, Filter On, and Re-Apply filter the tree. All Settings lists every policy with a Path column.
  - Columns can be sorted, and the sort is kept for each node.
  - Add/Remove Templates imports ADMX templates and removes them. Classic `.adm` files are not accepted.
  - Templates that fail to load are listed in a load-error list. One bad template does not hide the rest.
  - View > Add/Remove Columns, Customize View, and Export List.
  - File > Options, and the Help menu with the web site entry, About, and the snap-in About.
  - Refresh (Action menu, context menus, toolbar, and F5) reloads the templates and the policy files.
- **Hub and installer:** The Hub and the installer now list three tools. An update from 0.1.x adds the Local Group Policy Editor.
- **Hardening:** DLL loading searches System32 only, the Hub opens release notes links only when they are plain https addresses, and app manifests carry the product version.
- **Known differences from the classic editor:**
  - Template load errors are shown in a list and a status bar notice, not in a start-up message box. Each problem has a sentence of its own.
  - BWC uses its own icons.
  - There is no Favorites menu, no Extra Registry Settings node, and no action pane.
  - The editor opens read-only when it is not elevated or on Windows Home, instead of refusing to open.
  - Saving keeps changes made outside BWC by reading and changing the files under a lock, instead of rewriting them from an older copy.
  - The Extended view pane is native, not an HTML page.
  - Ctrl+P, Ctrl+F10, Ctrl+F5, and Ctrl+Alt+Shift+F7 are not bound.
- Writing policies on Windows Home and on non-English Windows was simulated only. It has not been verified on real machines.

### Unreleased

- **Updater:** Refusals relaunch the Hub from the helper's own folder at the shell's integrity level; an elevated helper never starts an elevated Hub.
- **Core:** Elevated file access, enumeration, and child creation use the held resolved path even if an ancestor junction is retargeted.
- **Local Group Policy Editor:** Read-only controls and collapsed menu commands do not respond to keyboard access keys.
- **Local Group Policy Editor:** Null settings maps use defaults, and the Add/Remove Templates dialog cannot close while an add or remove is running.
- **Local Group Policy Editor:** Additive prefixed lists remove stale values after entries are deleted or reordered.
- **Local Group Policy Editor:** Partial saves refresh the model, and save controls stay locked while a write is running.
- **Local Group Policy Editor:** Enter and Escape on an untouched trailing Show Contents row accept or cancel; committing a cleared two-column row keeps focus at the following row's first column.

## Changes in `v0.1.5:`

- **Update:** This update is purely to test the new BWC updater.

## Changes in `v0.1.4:`

- **Updater:** No more console window during an update.
  - The update helper that installs the update now runs hidden, so a blank command prompt window no longer appears after you accept the Windows permission prompt.
  - Reopening Better Windows Config after the update no longer flashes a console window.
  - When an update starts, Windows shows a notification: "Better Windows Config is updating and will restart automatically...". The app closes, installs the update, and reopens by itself.

## Changes in `v0.1.3:`

- **Update:** This update is purely to test the BWC updater.

## Changes in `v0.1.2:`

> [!IMPORTANT]
> **Coming from 0.1.1?** Download the 0.1.2 setup EXE above and run it. 0.1.2 installs for all users in Program Files and asks for administrator permission.
> 0.1.2 does not remove a 0.1.1 install, so uninstall 0.1.1 from Settings (Apps > Installed apps) first. Your settings are kept.
> 0.1.1 won't offer this update in the app.
> From 0.1.2 on, in-app updates ask Windows for permission once per update.

- **Installer and updater:** Installs for all users in Program Files, with Start menu shortcuts for everyone.
  - Updates are checked and verified before Windows asks for administrator permission, then install from a protected copy.
  - After an update, the Hub reopens at normal rights, not as administrator.
  - Installing and updating need administrator rights. If Better Windows Config is running in another Windows session, the update stops and says so.
  - The update dialog says when Windows will ask for permission.
  - The update helper is a native program that starts no .NET host, so .NET profiler, trace, and startup hook settings in the user's environment can't reach the administrator step.
- **Registry Editor:**
  - Remote registry editing: Connect Network Registry opens the Select Computer picker, and every command works on a connected computer.
  - Permissions: owner, auditing, and effective access. Apply keeps inherited entries.
  - Print shows progress with a Cancel button. Text export matches classic regedit byte for byte.
  - Resource values open in the classic viewers: resource lists, full resource descriptors, and requirements lists.
  - Keyboard and menus match classic: F4 for the address bar, Alt+Up for the parent key, Shift+F10 for the context menu, and a slow second click to rename. Add to Favorites and the Export branch box match the classic dialogs.
  - Screen readers announce list cells, hex bytes, and dialog controls by their classic labels. Dialogs follow high contrast themes.
  - A number too large for a DWORD or QWORD asks the classic truncation question instead of refusing.
  - Sibling keys sort in classic order.
- **Fixes:**
  - Delete, Modify, and rename act on the key the list shows, so they can't hit a value of the same name in another key.
  - A value named `(Default)` is shown separately from the key's default value.
  - A failed key delete refreshes the tree, so subkeys that were already removed no longer stay listed.
  - Keys named `.` or `..` show and work in the tree, Find, Permissions, and export.

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
