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
