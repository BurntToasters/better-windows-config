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
