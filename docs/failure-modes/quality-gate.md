# Quality gate failure modes

The quality gate is `node scripts/test-all.js` plus the three validators it runs
first. This page lists the ways the gate can lie, hang, or produce artifacts that
look better than the tree they describe.

## Em dash scan false positives

- **Binary files decoded as text.** A DLL, icon, or `.pri` resource can contain
  the bytes for U+2014. The scanner skips known binary extensions and any file
  containing a NUL byte.
- **UTF-16 text.** UTF-16 encoded source has NUL bytes, so the scanner treats it
  as binary and does not report an em dash inside it. Convert the file to UTF-8
  if it must be checked.
- **Minified or generated text.** A generated bundle may contain punctuation the
  author never typed. The gate scans tracked text only; generated output belongs
  in an ignored directory.
- **Self-test coverage.** `--self-test` creates a temporary tree with one clean
  line and one em dash line, runs the same scanner, and requires exit 1 plus the
  expected `path:line:column`. It never writes inside the repository.

## Changelog heading missing or duplicated

`scripts/validate-changelog-version.js` requires exactly one heading of the form
`## [<package version>]`. A missing heading means release notes were not updated.
A duplicate heading means a merge or a version bump copied an old section. Dates
are optional; the gate does not parse them.

## TODO markers in strings and XML docs

The comment linter only parses comments. `TODO` inside a normal string, verbatim
string, raw string, or character literal is ignored. Interpolated string holes
are skipped with the surrounding string, so a comment inside `{...}` is not
checked. XML doc comments (`///`) are parsed as comments, so a marker there does
fail. If a marker is legitimate, remove it or rephrase the comment; the linter
does not support suppression attributes.

## Git is not available

The em dash scanner prefers `git ls-files` so ignored build output is not read.
When git is missing or the directory is not a checkout, the scanner walks the
tree and applies the same skip rules. `--root` always uses the walk path, which
keeps the self-test independent of the caller's repository state.

## dotnet is missing

`scripts/test-all.js` starts `dotnet` from PATH. If the process cannot start
with `ENOENT`, it retries `C:\Program Files\dotnet\dotnet.exe`. If both fail, the
step is recorded as failed with a summary, and later steps are not attempted.
The script never installs a toolchain.

## Wrong architecture

The default RID comes from `process.arch`, never from `PROCESSOR_ARCHITECTURE`.
Under x64 emulation on an ARM64 machine, the environment variable can describe
the emulated process rather than the host. Pass `--rid` explicitly when the host
and the target differ. Cross builds are allowed; running ARM64 test binaries on
an x64 host is not.

## Test project is missing

Each `dotnet test` step checks that its project file exists before spawning. A
renamed or deleted project fails the step with `test project not found` instead
of producing a confusing MSBuild error.

## E2E flakiness

The E2E and publish steps are excluded from `--quick` so local iteration and the
CI unit gate stay deterministic. Full runs keep them as real blocking steps and
exercise both flavors: the ship run proves the released binary still works, and
the e2e run proves the sandbox fail-closed path. Failures are uploaded as
artifacts; do not mark the step `continue-on-error` to hide a flake. Fix the
harness or quarantine the specific check.

The harness only touches what it started. The test host gets a filtered environment (`BWC_E2E_*`, `BWC_DATA_ROOT`,
`NODE_OPTIONS` from the parent shell are dropped), it only kills or adopts a Registry Editor from the publish folder
under test that started after the run began, and the e2e flavor is read from the app binaries. See
`e2e-harness.md` sections 29 to 36. `scripts/check-registry-writer.js` also scans `tools/` and `scripts/`, so a
PowerShell or Node helper cannot write the registry unseen (section 37).

## Artifact overwrite

Every run rewrites `artifacts/gate/result.json`, and the full gate also rewrites
`artifacts/e2e/result-<rid>-<flavor>.json` and the publish folder. Two concurrent runs can
overwrite each other. CI uses a per-ref concurrency group with cancel-in-progress.
Local runs should be serialized by the operator.

## CI npm without a lockfile

The repository has no `package-lock.json`, so CI uses
`npm install --no-audit --no-fund`. `npm ci` would fail immediately. The
`prepare` script installs the git hooks; on CI that only sets configuration and
does not affect the build.

## CI branch triggers

The workflow runs on `main`, `next-*`, and `experimental`. `experimental` is the beta release branch (`RELEASE_BRANCHES.beta` in `scripts/lib/release-policy.js`), so a beta push must be gated too.

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| T1 | `experimental` is missing from the push or pull_request branch list | A beta release branch is not gated | Keep `experimental` in both lists. |
| T2 | `cancel-in-progress` is true for `main` | A newer push cancels a release gate run on main | `cancel-in-progress` is `github.ref != 'refs/heads/main'`. |

## CI GUI E2E on a hosted runner

A `windows-latest` runner has a 1024x768 Server desktop where screen captures come back black and
the clipboard and window placement differ from a real desktop. The first CI run that reached the GUI
suite (0.1.1) failed 14 pixel, theme, placement, and clipboard checks there that pass on a real desktop,
and then hung until the 60 minute job limit.

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| H1 | A test leaves an app running that inherited the dotnet output pipes | `test-e2e.js` waits on 'close' forever | It settles on 'exit' and cuts the pipes after a 10 s drain. |
| H2 | GUI E2E fails on the hosted desktop | CI is always red and hides a real quick-gate failure | The quick gate blocks. The GUI and installer E2E steps run with `continue-on-error` and their own `timeout-minutes`, and upload their result. The local `node scripts/test-all.js --rid all` on a real desktop is the release gate. |
| H3 | The hosted runner shell is elevated | The installer E2E refuses to start (the Hub must run asInvoker) | Expected on CI; the step reports it and does not block. Run it locally with `node scripts/test-installer-e2e.js`. |

## Elevated runner environment (`scripts/elevated-runner/run-elevated.ps1`)

The scheduled task runs elevated and inherits the environment of the task definition. The runner has
no parameters, so nothing it runs may take a path or a setting from that environment.

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| E1 | `NODE_OPTIONS` or `NODE_PATH` is set for the task | Node loads code from outside the repository in an elevated run | Both are removed before any node process starts. |
| E2 | `BWC_PUBLISH_DIR` is set for the task | The E2E publish folder is redirected to another folder | Removed. The runner never sets it, so `scripts/test-e2e.js` uses `publish/<rid>-e2e`. |
| E3 | Any other `BWC_*` variable is set for the task | A variable changes the elevated test run | All `BWC_*` variables are removed before node starts. |
| E4 | The installed copy in `%ProgramData%` predates this change | The fix is not in effect | Run `scripts/elevated-runner/install.ps1` again. The copy is not updated automatically. |

## Workflow permissions

The workflow declares `contents: read` and uses no secrets, signing keys, or
publish credentials. Adding a release or signing step requires a separate
workflow with reviewed permissions; do not widen this gate.

## Parallel runs and shared obj/bin folders

Both architecture builds use the same projects, so two gate runs can collide on
`obj/` and `bin/` locks. The script runs steps sequentially and never spawns
commands concurrently. If MSBuild reports MSB3021 or MSB3027, wait for the other
run to finish rather than deleting its output.

## Path separators

Reports use `path.join` for filesystem access and forward slashes for artifact
paths. A Windows path printed into JSON stays readable in CI logs and in the
artifact viewer. Do not compare raw paths across platforms.

## Live updater client rehearsal (`scripts/updater-client-live.js`)

`npm run release:rehearse:client` runs the built ship `publish/<rid>/BwcUpdate.exe` with `--check`
and then `--download` against the live stable feed. It writes
`artifacts/release/client-live/result.json`. Written before the code, per AGENTS.md.

`release:win:continue` leaves this step out for `FIRST_V2_FEED_VERSION` (0.1.2) only. It is the first release that
publishes `bwc-stable-v2.json`, so before it ships the live feed returns 404 (C4) and there is nothing to check. Run
`npm run release:rehearse:client` by hand once that release is published. Every later version runs the step as usual.

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| C1 | `publish/<rid>/BwcUpdate.exe` missing | A rehearsal passes on a tree that was never built | Refuse `binary-missing` before any network call. Exit 3. |
| C2 | `--rid` not win-x64 or win-arm64, or the host arch is neither | Wrong binary is tested | Usage error naming the valid RIDs. Exit 2. |
| C3 | `--url-base` not https, and not loopback http | Feed traffic goes over plain HTTP | Refuse `base-url-refused`. Exit 2. |
| C4 | Expected version not given and the manifest cannot be fetched or parsed | A stale or missing feed is accepted | Refuse `expect-unavailable`. Exit 4. |
| C5 | `--expect-version` is not SemVer or is a prerelease | The stable feed is compared with the wrong version | Usage error `version-invalid`. Exit 2. |
| C6 | Client exits nonzero (network, signature, policy, or a crash) | A refused update looks like success | Refuse `client-failed` with the exit code and a stderr tail. Exit 1. |
| C7 | Client exits 0 but prints no final JSON line with a boolean `ok` | Progress lines are mistaken for a verdict | Refuse `output-unparsed`. Exit 1. |
| C8 | Final JSON has `ok:false` | Client refusal hidden by exit 0 | Refuse `client-refused` and name `failure`. Exit 1. |
| C9 | Final JSON `version` differs from the expected version | Stale CDN copy or a wrong release served | Refuse `version-mismatch`. Exit 1. |
| C10 | `--download` final JSON lacks `stagingFolder`, or the folder is absent | Download claimed but nothing was staged | Refuse `staging-missing`. Exit 1. |
| C11 | `stagingFolder` is not directly under `%TEMP%\BWC-Update` with a GUID name | The script deletes an arbitrary folder | Refuse `staging-path-refused`. Never delete. Exit 1. |
| C12 | The staging folder already existed before the run | The script deletes a folder it did not create | Keep it, and record `staging-preexisting` in the result. |
| C13 | Child hangs | The release VM stalls forever | Kill only the child this script started after 5 min. Refuse `timeout`. Exit 4. |
| C14 | Staging folder cannot be removed (file lock, permissions) | Leftover update payloads accumulate silently | Refuse `cleanup-failed`. Exit 1. |
| C15 | `result.json` cannot be written | The run passes with no artifact | Report `artifact-write-failed`. Exit 4. |
| C16 | Progress line (`progress` key) is the only JSON with `ok` absent | Progress mistaken for the final line | Only the last line with a boolean `ok` counts. Covered by C7. |
| C17 | `--rid` omitted on x64 Node under ARM64 Windows emulation | Node reports x64 and the x64 exe is tested | Document: pass `--rid` explicitly on a mixed host. |
| C18 | Exe cannot start (wrong architecture, missing runtime) | Spawn error reads as a feed problem | Refuse `client-failed` with the spawn error text. Exit 4. |
| C19 | `--download` exits nonzero or prints no verdict after it created a staging folder | A failed download leaves update payloads in the BWC-Update folder under TEMP | Remove the folder this run created, with the C11 and C12 guards. Record `staging.cleanup` in the result. Exit as the failure says. |
| C20 | Client exits nonzero and also prints a final JSON verdict on stdout (for one, `RunningElevated` from an elevated shell) | The refusal shows only an empty stderr tail, so the reason is lost | Refuse `client-failed` with the exit code, the verdict's `failure` and `detail`, and the stderr tail. Exit 1. |
| C21 | The release shell is elevated (the build VM is reached over SSH, which gives an administrator the full token) | The client refuses `RunningElevated`, so the rehearsal cannot run where releases are built | Every client run goes through `scripts/run-restricted.ps1`: a restricted copy of the caller's token (`LUA_TOKEN`, `DISABLE_MAX_PRIVILEGE`, Administrators deny-only) at medium integrity, started with `CreateProcessAsUser`. No desktop shell, password, or scheduled task is used, and the shipped client gets no bypass. The result records `launch: restricted-token`. |
| C22 | The restricted token cannot be made, or the child cannot be started | The rehearsal falls back to an elevated client and the check means nothing | Refuse `deelevate-failed` with the step and the Win32 error. Exit 4. Never start the client any other way. |
| C23 | The started child is still elevated or at high integrity (the token call silently kept a right) | The client runs elevated after all | The child starts suspended. Its token is read before it runs: `TokenElevation` must be 0 and the integrity below high. Otherwise it is terminated before it runs a single instruction, and the run is refused `deelevate-failed`. |
| C24 | The child hangs | The wrapper waits forever, and killing the wrapper leaves the client running | The wrapper terminates the child after the timeout and reports it, and the script refuses `timeout` (C13). The script also kills the wrapper after the timeout plus a margin. |
| C25 | Client arguments carry spaces, quotes, or trailing backslashes | The client gets different arguments than the script passed | The arguments go to the wrapper as base64 JSON and are quoted with the CommandLineToArgvW rules. |
| C26 | The wrapper cannot write the child's output files or its result file | A run with no verdict looks like an unparsed output | Refuse `deelevate-failed` naming the missing result. Exit 4. |
| C27 | The restricted child cannot open the window station or desktop it starts on. Over SSH, Windows makes the session's station (`Service-0x0-<logon id>$`) from the elevated token's default DACL, which grants Administrators and SYSTEM, and the child holds Administrators as deny-only | Every client run dies at start with 0xC0000142 (STATUS_DLL_INIT_FAILED) and prints nothing | The child starts on a private window station and desktop whose DACL grants only the current user and SYSTEM, labeled medium, with `DETACHED_PROCESS` so no console host is needed. The station is created with `CWF_CREATE_ONLY`, unnamed first and then with a GUID name, so an existing station is never reused. Only an unelevated caller may fall back to its own desktop, which its medium child can already open. An elevated caller with no private station is refused `deelevate-failed` naming both Win32 errors. The result records the desktop used. An exit of 0xC0000142 is `deelevate-failed`, not a client verdict. |

## Policy writer guard and the policy test projects (release 0.2.0)

Three steps were added to `scripts/test-all.js`. They run in `--quick` mode too. The step logs and the
`artifacts/gate/result.json` entries follow the same format as every other step. Every case for the guard is in
`policy-writer.md`.

| Step | Command | What it guards |
|------|---------|----------------|
| `policy-writer` | `node scripts/check-policy-writer.js`, right after `registry-writer` | No file write reaches the real `%SystemRoot%\System32\GroupPolicy`, a `PolicyDefinitions` folder, or the BWC import store except through `src/Bwc.Gpo/Store/GpoFileWriter.cs` and `src/Bwc.Policy/Store/TemplateStoreWriter.cs`. `RefreshPolicy*`, the critical section calls, and `IGroupPolicyObject` appear only in `src/Bwc.Gpo/Refresh/PolicyRefresh.cs`. `gpupdate`, `secedit`, `gpedit.msc`, and `mmc` are launched only by `tools/Bwc.Oracle.Capture/Gpedit/**`. |
| `test-gpo` | `dotnet test tests/Bwc.Gpo.Tests/Bwc.Gpo.Tests.csproj -c Release -p:Platform=<host> --nologo --filter TestCategory!=Elevated` | The `Registry.pol` codec and, later, the GPO folder store, with the same filter, platform, and project check as `test-core`. |
| `test-policy` | `dotnet test tests/Bwc.Policy.Tests/Bwc.Policy.Tests.csproj -c Release -p:Platform=<host> --nologo --filter TestCategory!=Elevated` | The ADMX and ADML loader and, later, the template store. |

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| P1 | A new file in `src/Bwc.Gpo`, `Bwc.Policy`, `Bwc.Gpedit`, `Bwc.Gpedit.Editor`, or `Bwc.Gpedit.App` calls a file-write API | A second writer bypasses the write policy | `policy-writer` fails with `protected-write`. Route the write through the named writer. |
| P2 | A test or script names `GroupPolicy`, `PolicyDefinitions`, `Registry.pol`, `gpt.ini`, or `comment.cmtx` next to a write | A test run changes this machine's policy | `policy-writer` fails with `policy-path-write` unless the file is allowlisted with a reason. |
| P3 | `RefreshPolicyEx` or `gpupdate` appears outside the named files | A policy refresh runs from a test or a second code path | `policy-writer` fails with `refresh-name` or `policy-process-launch`. |
| P4 | The guard itself stops matching (a refactor breaks a pattern) | A rule passes everything | `--self-test` holds a fixture for every rule and every pass case, and fails on any miss. Run it with `node scripts/check-policy-writer.js --self-test`. |
| P5 | `tests/Bwc.Gpo.Tests` or `tests/Bwc.Policy.Tests` is renamed or deleted | The step runs nothing | Like every `dotnet test` step, it fails with `test project not found` before spawning. |
| P6 | A `.pol` file (binary `Registry.pol` fixtures) is scanned for U+2014 | A false positive from bytes that are not text | `.pol` is in the em dash scanner's binary extensions, so it is skipped like `.dll`. `.admx` and `.adml` are XML text and stay scanned. |
| P7 | The gate passes while the real GroupPolicy folder changed | A test wrote to the real folder through a path the guard cannot see | The E2E harness hashes the real folder before and after every unelevated run (`gpo.real-unchanged`, a later piece). The guard stops the code that could write; the hash proves nothing did. |

## Audit fixes AF3: gate steps, ship elevation, update E2E branch, tracked paths, CI pins

Written before the code, per AGENTS.md. Each row names what goes wrong and how the gate now detects it.

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| GATE-1 | The self-tests of `check-registry-writer.js`, `check-policy-writer.js` and `validate-no-em-dash.js`, and `check-regedit-strings.js`, run in no gate step (the last one only in `npm run validate`) | A guard stops detecting and nothing fails, because the guard's own proof never runs | The quick gate has the steps `registry-writer-selftest`, `policy-writer-selftest`, `no-em-dash-selftest`, `regedit-strings`, `tracked-paths-selftest` and `tracked-paths`. Each runs the script that proves the guard, and a non-zero exit fails the gate. |
| GATE-2 | `scripts/test-e2e.js --flavor ship` runs from an elevated shell, because elevation was checked only for Elevated tests | The ship tools run with an administrator token against the real registry and, later, a writable real policy object | `--flavor ship` checks `elevationState()` before any build or test. An elevated process, or one whose elevation cannot be checked, prints `NOT RUN:` with the reason and exits 3. The e2e flavor stays allowed when elevated, because its apps are sandboxed. `BWC_E2E_FORCE_ELEVATED=1` can only force the refusal, never make an elevated process look unelevated. In `test-all.js` an exit of 3 is not 0, so a NOT RUN of a required step is a failed step. |
| GATE-3 | `tests/Bwc.Update.Tests` runs only in the ship flavor, so the `#else` branch of `#if !BWC_E2E` (`Non_Elevated_With_E2E_Root_Proceeds` in `ElevatedApplierTests.cs`) is in no gate run | A break in the E2E-root path of the elevated applier goes unseen | A `test-update-e2e` step runs the same project with `-p:BwcFlavor=E2E`, right after `test-update`, like `test-registry-e2e`. |
| PRIV-1 | A tracked oracle file holds an absolute path with the Windows account name of the machine that captured it, and a test comment uses the same name as an example | A private account name is published, and the capture cannot be repeated on another machine with the same output | Both captures write the repo root as `<repo>`. `scripts/check-tracked-paths.js` fails on any tracked text file that holds a drive, `:`, `Users`, and a name segment with `\`, `\\` (JSON-escaped) or `/` separators, unless the name is a placeholder (starts with `<`, `%` or `$`), `Public`, `Default`, `All Users`, or one of the explicit neutral example names `user`, `x`, `someone` and `test`. A name outside that list, including an 8.3 short name, fails. It prints each hit as `path:line` and never prints the content. Its `--self-test` plants one path in each separator style and requires a failure, and requires the allowed forms to pass. It runs in the quick gate and in `npm run validate`. |
| CI-1 | `ci.yml` pins actions by tag, installs any NSIS version, and the two `continue-on-error` E2E steps fail with nothing reporting it | A moved tag changes what runs, a new NSIS changes the installer under test, and a red E2E step looks the same as a green one | Actions are pinned by commit with the tag in a comment. NSIS is installed at 3.13.0. Both E2E steps have an `id`, and a last `if: always()` step prints a `::warning::` annotation for each one whose outcome is `failure`. `continue-on-error` stays, so the quick gate still decides the job. |
