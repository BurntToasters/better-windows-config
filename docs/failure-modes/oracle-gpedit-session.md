# Oracle gpedit capture session failure modes

The gpedit captures (`tools/Bwc.Oracle.Capture/Gpedit`) drive classic `gpedit.msc` on a resettable VM
and let it save into the machine's real local group policy. This document lists every way such a
capture can damage the machine or leave state behind, and what the session code does about each.
It was written before the session, the probe and the dispatcher. Every later gpedit capture command
runs inside `GpeditSession.Run`, so the rules below hold for all of them.

Commands: `gpedit probe [--dry-run]`, `gpedit restore`, `gpedit status`, `gpedit selftest`.
Output folder: `artifacts/oracle/gpedit/` (git-ignored). The backup lives in
`artifacts/oracle/gpedit/backup/` and the manifest is `backup/manifest.json`.

What a session may change, and nothing else:

- `%SystemRoot%\System32\GroupPolicy` (the local GPO folder, hidden): classic writes here on OK.
- `%APPDATA%\Microsoft\MMC\gpedit`: MMC saved console state (a file, or a folder; both handled).
- `%SystemRoot%\PolicyDefinitions\<our files>` and `...\en-US\<our files>`: capture templates.
- Registry keys `HKLM\Software\Policies\BWC-Oracle`, `HKCU\Software\Policies\BWC-Oracle`,
  `HKLM\Software\BWC-Oracle-Gpedit`, `HKCU\Software\BWC-Oracle-Gpedit`.
- Files under `artifacts/oracle/gpedit/`.

The VM snapshot taken by the user before the run is the final backstop for everything not listed.

## 1. Crash of the tool mid-run

An unhandled exception, an out-of-memory, or a bug skips the normal flow.

- The session body runs inside `try`/`finally`; the `finally` runs the full restore.
- A fault before the manifest exists changes nothing outside `artifacts/` (the live folders are only
  read until the manifest is written), so there is nothing to restore.
- A fault after the manifest exists leaves the manifest in place by design. The next `gpedit
  restore` (or any gpedit command that starts a session, which refuses and says so) undoes it.

## 2. Ctrl+C, Ctrl+Break, console window closed, logoff, shutdown

- `Console.CancelKeyPress` cancels the default termination, sets a cancellation flag and returns. The
  session checks the flag between steps and unwinds into the `finally` restore.
- `SetConsoleCtrlHandler` covers CLOSE, LOGOFF and SHUTDOWN events, which cannot be cancelled. The
  handler runs the same restore directly, under the restore lock, so the main thread's `finally` and
  the handler never restore at the same time. Windows allows only a few seconds there; if the
  process is killed first the manifest remains and case 3 applies.
- `Stop-Process` or Task Manager gives the tool no chance to run anything. Case 3 applies.

## 3. Power loss, kill, blue screen, VM reset (the process dies with no restore)

- The manifest was written before anything outside `artifacts/` changed, and it records every hash
  and every template copy it planned. After a restart, `gpedit status` shows the backup and `gpedit
  restore` undoes everything, whatever step the run had reached.
- The manifest is written with a temp file, flush to disk, then an atomic replace, so a crash during
  the write leaves either no manifest or a complete one, never half of one. A stale
  `manifest.json.tmp` is ignored and removed.
- A crash while the backup is being copied leaves a partial `backup/` folder and no manifest. The live
  folders were untouched. The next run deletes the partial folder and starts again.
- `gpedit restore` is idempotent, so a crash during restore is repaired by running it again.

## 4. MMC left open, holding files

An mmc.exe from a failed run can keep `Registry.pol` open or recreate state when it exits.

- Restore closes only the mmc.exe processes the session started. Each is recorded in the manifest as
  pid plus process start time, so a pid that was reused by another program is never touched.
- Closing is `CloseMainWindow`, a wait of 5 seconds, then `Kill` of that one process, then a wait for
  exit before any file is touched. An mmc.exe the user started by hand is never closed. If it holds a
  file, restore fails on that file with a clear message (case 6) and the manifest stays.
- MMC writes its console state on exit. Restore closes MMC first and restores the console state
  after, so the state it writes while closing is also undone.

## 5. Backup incomplete or wrong

- Each file is hashed (SHA-256) before the copy, copied, hashed in the copy, and hashed again at the
  source. A difference at any point (the policy service wrote the file during the copy) aborts the
  session before the manifest exists.
- The manifest records, for the GroupPolicy folder and for the MMC state: whether it existed, every
  file and folder as a path relative to the root, size, hash and attributes, and the last write time.
- A reparse point (symlink, junction) at the root or inside is refused, never followed.
- An empty folder is recorded as a folder. A file that cannot be opened (sharing violation) fails the
  backup. Nothing is skipped silently.
- The manifest is written last, after every hash in it has been verified against the copy.
- Not captured: ACLs and timestamps of folders. Restore recreates missing items with inherited
  security. The VM snapshot covers this.

## 6. Restore partial

- Restore makes the folder equal to the manifest: files not in the manifest are deleted, files whose
  hash differs are rewritten from the backup, missing files and folders are recreated, and the folder
  is removed when it did not exist at the start. Attributes (hidden, system) are set back.
- Every step is attempted even if an earlier one failed, and all failures are collected and printed.
- After the steps, the session scans again and compares every hash, every path and the four registry
  keys against the manifest. Only a clean comparison deletes the manifest.
- Any failure keeps the manifest and exits with code 1. The message names each path that could not be
  restored. Run `gpedit restore` again after fixing the cause (for example close the program that
  holds the file).
- A template file whose hash no longer matches what the session copied is not ours any more. It is
  left in place and counts as a failure, so a person decides. After deleting it by hand, restore
  succeeds.

## 7. Re-run while a backup exists

A second run would back up the changed state and hide the original.

- Every session command refuses with exit code 2 when `backup/manifest.json` exists, and says to run
  `gpedit restore`. The check is made before the GUI mutex and again inside it.
- `gpedit status` prints whether the manifest exists, when it was written, the command that wrote
  it, and what it holds (file counts, template files, started processes).

## 8. Capture templates left in PolicyDefinitions

- Each file name and hash is in the manifest before the file is copied, so even a copy cut short is
  recognized by restore.
- Restore deletes a file only when its hash matches the recorded hash (case 6 for a mismatch).
- Only files the session's own template set names are ever created or deleted. Other files in the
  folder are never listed, moved or touched.
- If a file with the same name already exists before the copy, the session refuses (exit code 2).
  It cannot tell whether that file is a leftover or the user's, so it never overwrites or deletes it.
- The folders `PolicyDefinitions` and `PolicyDefinitions\en-US` must already exist. If not, the
  session refuses instead of creating folders it would then have to remove.

## 9. Applied registry values left behind

Classic gpedit may apply a saved policy to the live registry through a policy refresh, and the
capture templates write only below `Software\Policies\BWC-Oracle` (a key nothing reads).

- Restore deletes exactly four keys with `DeleteSubKeyTree`: `HKLM` and `HKCU` `Software\Policies\BWC-Oracle`
  and `HKLM` and `HKCU` `Software\BWC-Oracle-Gpedit`. A guard compares each path against that
  list before any delete. No other key is ever deleted or written.
- After deleting, it opens each key again and fails the restore if one still exists.
- Not touched: the policy engine's own bookkeeping (for example the Group Policy `History` and `State`
  keys under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy`). A refresh may add entries
  there that name the probe. They are harmless and the snapshot revert removes them.

## 10. MMC console state changed

MMC saves per-user console changes (column widths, tree width, view mode, last selection) to
`%APPDATA%\Microsoft\MMC\gpedit` when it closes, and the probe changes the selected tab and node.

- The file or folder is backed up and restored like the GroupPolicy folder. A file that did not exist
  is deleted. The parent `MMC` folder is left in place if MMC created it (an empty folder is
  harmless).

## 11. Refresh applying the probe policy to the live registry

- The session never calls `gpupdate`, `RefreshPolicyEx`, or any refresh API. Classic may refresh on its
  own after OK; the probe records, after each OK, whether `HKLM\Software\Policies\BWC-Oracle\Probe`
  appeared (this answers matrix group G). Restore deletes the keys (case 9).
- The templates write only to `BWC-Oracle`, never to a key a program reads, so a refresh cannot change
  machine behavior in the meantime.

## 12. Run unelevated

Classic refuses to open without administrator rights, and GroupPolicy cannot be backed up for restore
without write access.

- Without elevation, every session command except `status`, `probe --dry-run` and `selftest` refuses
  with exit code 2 and a message that tells the user to start the tool from an administrator
  terminal. A filtered token is not elevated. `restore` with no manifest only reports that there is
  nothing to restore; with a manifest it refuses unless elevated.
- The tool never self-elevates, never uses the `runas` verb and never shows a UAC prompt. The mmc.exe
  child is started with `UseShellExecute = false`, so it inherits this process's elevation.
- The check is made before the GUI mutex, before reading the manifest, and before any backup.

## 13. Run over SSH or on a non-interactive desktop

- The session refuses (exit code 2) when `Environment.UserInteractive` is false, when the process runs
  in session 0, when the window station is not `WinSta0`, or when `SSH_CONNECTION`, `SSH_CLIENT` or
  `SSH_TTY` is set. UI Automation and key input need the interactive desktop.
- A locked workstation or secure desktop makes UI Automation return nothing; the probe steps that
  need a window time out and record the failure (case 17). The restore still runs.

## 14. GUI mutex held by another run

- The session takes `Local\BwcGuiTestLock` through the existing `GuiLock`, waits up to 15 minutes and
  then fails before any change. The backup is made after the mutex is held, so an E2E run that holds
  the mutex cannot interleave with it. `GuiLock` handles an abandoned mutex.
- The mutex is process-wide for the user's session. The elevated tool and an unelevated E2E run share
  it only when they run in the same logon session, which is the case on the VM.

## 15. Two sessions at the same time

- The second command sees the mutex held and waits, then finds the manifest the first one wrote and
  refuses. `gpedit restore` takes the mutex too, so it cannot run while a capture is active. It
  never closes a process it did not record.
- A shared restore lock inside the process stops the Ctrl handler and `finally` from running restore
  at the same time.

## 16. Classic UI differs from expectations

The tree, the tabs, the dialog and the menus are located through UI Automation by name and control
type, with Windows version and language differences possible.

- Every probe step is wrapped: the step records success or the exception, a screenshot is taken where
  a window exists, and the run continues when it is safe. A step that needs a dialog closed first
  checks that no dialog of the mmc process is open and skips otherwise.
- A step never clicks or invokes anything it did not identify by exact role and name. Menus are only
  expanded and collapsed, never invoked, except File > Exit at the end.
- Output goes to `probe.json` after every step, so a hang or a crash keeps all earlier results.
- Probe policies (Zeta, Alpha) are set by name; if a name is not found the step fails and no other
  policy is touched. The restore deletes whatever was saved.
- An unexpected modal dialog from MMC (an error, a "save console settings" question) is recorded with
  its text and buttons and a screenshot. The probe does not answer it. The restore closes MMC.

## 17. Timeouts and hangs

- Every wait is bounded (window start 90 seconds, tree population 30 seconds, dialog open 15 seconds,
  file change 5 seconds). On a timeout the step fails and the run continues or ends.
- A hung MMC does not block the restore: restore kills only the recorded mmc.exe after the 5 second
  wait (case 4).
- A hung UI Automation call blocks the tool thread. The user ends the tool with Ctrl+C (case 2) or
  closes the terminal; the restore handler runs, or `gpedit restore` repairs it afterwards.

## 18. Probe-specific state

- Probe policies are saved by classic into `Machine\Registry.pol`, `gpt.ini` and possibly
  `comment.cmtx`. All three are inside the backed up folder and are restored (case 6).
- The last probe steps return both probe policies to Not Configured, but the session does not rely on
  that: the folder is restored from the backup anyway.
- Probe output files (`probe.json`, `probe/*.png`, `*.hex` text inside the JSON) hold Microsoft UI text
  and local state. They stay under `artifacts/` and are never tracked. The JSON writer redacts the
  user's SID and the machine name.

## 19. Self test

- `gpedit selftest` runs the same session code against a sandbox folder under
  `artifacts/oracle/gpedit/selftest/` (fake GroupPolicy, PolicyDefinitions and MMC state) and one
  registry key, `HKCU\Software\BWC-Oracle-Gpedit-Selftest`. It never reads or writes the real
  folders, never starts mmc.exe, and needs no elevation. It mutates the sandbox in each way listed
  above (edit, delete, add, template collision, leftover registry value, re-run with a manifest,
  crash simulated by skipping the restore, then `restore`) and writes
  `artifacts/oracle/gpedit/selftest.json` with a pass or fail line per case.
- The sandbox is selected only by this command, never by an environment variable or an option of the
  real commands, so a capture cannot be redirected by accident.
