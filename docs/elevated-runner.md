# Elevated E2E runner

Some E2E and registry tests need an administrator token, for example a hive save and restore
through `SeBackupPrivilege` and `SeRestorePrivilege`. Those tests are tagged
`[TestCategory("Elevated")]`. Normal runs (`scripts/test-e2e.js`, `scripts/test-all.js`) skip them,
so ordinary development never needs admin and never shows a UAC prompt.

The elevated runner is an opt-in way to run only the Elevated tests. A scheduled task,
"BWC Elevated E2E", runs them in an elevated process for your user. Agents and developers start the
task with `scripts/elevated-runner/run.js`, which does not elevate and does not show a UAC prompt.

## Security trade-off

Read this before you install it.

- The task runs elevated code from this checkout. `test-e2e.js` runs `dotnet test` on the test
  projects and runs the app publish, and both come from this repo. Anything that can change files in
  this checkout (an agent, a dependency update, an edited script or test) can get code run as
  administrator the next time the task runs.
- Residual risk, in plain words: the runner protects only itself. Its copy in
  `%ProgramData%\BwcElevatedRunner` can be changed only by Administrators and SYSTEM, and
  `install.ps1` refuses to reuse a folder that a link or a non-administrator owns. It does not
  protect the checkout that the runner builds and tests. Whatever is in that checkout runs as
  administrator when the task runs.
- The task is disabled at the end of every run, including a refused run. You must enable it again
  for each run. A forgotten enable is not a risk; a left-on task is, so the runner turns itself off.
- The runner uses only the machine PATH plus `System32`. It does not use the user PATH, because a
  user-writable folder on that PATH could supply a tool to the elevated run.
- Prefer a disposable Windows VM with a copy of the repo for this. Use the runner on your
  everyday machine only if you accept the risk above.
- The task runs only for your user, only when you are logged on at the desktop, and only when
  started. It has no triggers.

## What it does

`install.ps1` (run once, elevated) does these things:

- refuses to run unless the PowerShell is already elevated, and never elevates itself;
- refuses to reuse an existing `%ProgramData%\BwcElevatedRunner` (or its files) that is a link or
  junction, or that is owned by anything other than Administrators or SYSTEM. It stops and changes
  nothing; run `uninstall.ps1` from an elevated shell, then install again;
- copies `run-elevated.ps1` to `%ProgramData%\BwcElevatedRunner\run-elevated.ps1`, with the owner set
  to Administrators and an ACL that only Administrators and SYSTEM can modify (your user gets read
  access);
- records this checkout and the `node` path in `%ProgramData%\BwcElevatedRunner\config.json`;
- registers the task "BWC Elevated E2E" for the current user, interactive, with "Run with highest
  privileges". The action is a fixed command: PowerShell runs the copied runner with no arguments.
  The task has no triggers and is disabled unless you pass `-Enable`.

When the task runs, `run-elevated.ps1` does only this:

1. checks that it is elevated and that it takes no arguments;
2. reads the repo and node paths from `config.json`, checks that the repo is a real folder (not a
   link or junction) and a BWC checkout, and checks that the node file exists;
3. sets the PATH to `System32` and the machine PATH only, then maps the node architecture to the
   host RID (`win-arm64` or `win-x64`);
4. checks the E2E publish folder `publish/<rid>-e2e` with `scripts/flavor-metadata.js`. It must
   be flavor E2E, with `asInvoker` in every exe head;
5. runs `node scripts/test-e2e.js --rid <rid> --flavor e2e --filter "TestCategory=Elevated"` from
   that repo, and nothing else;
6. disables the scheduled task "BWC Elevated E2E", on every path including a refused run, and
   records `taskDisabled=yes` (or `no`) in `last-run.txt`;
7. writes its log to `%ProgramData%\BwcElevatedRunner\logs\<runId>.log` and its state to
   `%ProgramData%\BwcElevatedRunner\last-run.txt`, then exits.

`run.js` (non-elevated) does this:

1. checks that the task exists and is enabled. If not, it prints the install or enable command and
   exits 2;
2. checks that the recorded checkout is the one `run.js` is in. If not, it refuses, so results never
   come from a different tree. Re-run `install.ps1` from the other checkout to switch;
3. checks the E2E publish folder, and asks for a build if it is missing or wrong;
4. starts the task with `schtasks /Run /TN "BWC Elevated E2E"`, polls the task state and
   `last-run.txt`, and stops after 30 minutes;
5. prints the result and the artifact summary from `artifacts/e2e/result-<rid>-e2e.json`.

## Commands

Run the install, enable, and disable commands from an elevated PowerShell in the checkout. The
others run from a normal shell.

```powershell
# Once: install the task, disabled. Elevated.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\elevated-runner\install.ps1

# Before a run: enable the task. Elevated. The runner disables it again when it finishes.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\elevated-runner\install.ps1 -Enable

# Build the E2E publish the runner tests. Normal shell.
node scripts/dotnet-build.js --rid win-arm64 --flavor e2e

# Run the Elevated tests and wait for the result. Normal shell. Never needs UAC.
node scripts/elevated-runner/run.js

# After a run that was stopped early, or to be sure: disable the task. Elevated.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\elevated-runner\install.ps1 -Disable

# Remove the task and %ProgramData%\BwcElevatedRunner. Elevated.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\elevated-runner\uninstall.ps1
```

`install.ps1 -Enable` and `-Disable` both need an elevated shell. A later `install.ps1` without
a switch registers the task again and leaves it disabled.

## Results

`run.js` exits with one of these codes:

| Exit | Meaning |
|---|---|
| 0 | Every Elevated test passed. |
| 1 | An Elevated test failed, or the task stopped without a final result. |
| 2 | Not set up, or the runner refused before any test ran. The message says what to do. |
| 3 | NOT RUN. The task did not start, timed out, or no test is tagged Elevated. |

An Elevated run that is not elevated never reports PASS. `test-e2e.js --filter TestCategory=Elevated`
run from a normal shell exits 3 with NOT RUN.

## Which tests are Elevated

- `tests/Bwc.Registry.Tests/RegistryHiveTests.cs`: `ElevatedSaveAndRestoreRoundTripStaysInsideTheSandbox`.
  It is compiled in the E2E flavor and returns Inconclusive without both privileges.
- No E2E test in `tests/Bwc.E2E` is tagged Elevated yet. The hive mount round trip (File > Load Hive
  and Unload Hive) is not automated. Add a test with `[TestCategory("Elevated")]` when it is written.

The runner runs only the `Bwc.E2E` project, as specified. The registry round trip above runs only
when you run its project from an elevated shell yourself, for example
`dotnet test tests/Bwc.Registry.Tests/Bwc.Registry.Tests.csproj -c Release -p:BwcFlavor=E2E --filter TestCategory=Elevated`.
Until an Elevated E2E test exists, the runner reports NOT RUN.

## Files

| Path | Role |
|---|---|
| `scripts/elevated-runner/install.ps1` | Elevated: installs, enables, or disables the task. |
| `scripts/elevated-runner/run-elevated.ps1` | Fixed runner. Installed as a copy in ProgramData. |
| `scripts/elevated-runner/uninstall.ps1` | Elevated: removes the task and the ProgramData folder. |
| `scripts/elevated-runner/run.js` | Non-elevated entry point. Starts the task and waits. |
| `scripts/elevated-runner/elevation.mjs` | Elevation check shared with `test-e2e.js` and the launch helper. |
| `%ProgramData%\BwcElevatedRunner\config.json` | Repo and node paths recorded by `install.ps1`. |
| `%ProgramData%\BwcElevatedRunner\last-run.txt` | Status of the latest run, read by `run.js`. |
| `%ProgramData%\BwcElevatedRunner\logs\` | One log per run. |
