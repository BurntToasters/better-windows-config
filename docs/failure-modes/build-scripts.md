# Build script failure modes

Scope: `scripts/dotnet-build.js`, `scripts/run-app.js`, `scripts/sync-version.js`,
`scripts/sync-version-helpers.js`, `scripts/strip-coauthor-trailers.js`, and
`scripts/direct-execution.mjs`.

## Architecture and toolchain

### Wrong architecture or emulated shell

`PROCESSOR_ARCHITECTURE` describes the shell, not the machine. On Windows 11 ARM64 a
32-bit or x64 PowerShell reports `AMD64` while `process.arch` reports `arm64`. Using the
environment variable would publish an x64 build and label it native.

Checks and behavior:

- `run-app.js` derives the default RID only from `process.arch`: `arm64` maps to
  `win-arm64`, `x64` maps to `win-x64`, and any other value asks for an explicit `--rid`.
- `dotnet-build.js` rejects any RID other than `win-x64` or `win-arm64` with exit code 2.
- The platform property is derived from the RID, never from the shell.
- A published x64 app still runs on ARM64 under emulation, so a smoke test that only
  checks "the window opens" cannot detect this mistake. Compare the artifact RID with
  `process.arch` in the build log.

### dotnet missing

The script spawns `dotnet` from `PATH`. A GUI-launched terminal, a fresh shell, or a
machine without the SDK reports `ENOENT`. The script retries once with
`C:\Program Files\dotnet\dotnet.exe`, which is where the Windows installer places the
host. If both attempts fail, the project record has a `null` exit code and the build
artifact is written with `pass: false` and an error string.

A missing SDK can also appear as a normal nonzero exit with `dotnet: command not found`
on a POSIX shell. The exit code path handles that as an ordinary failure.

### Warnings are failures

`TreatWarningsAsErrors` is enabled for the projects, but SDK, NuGet, XAML, and ReadyToRun
steps can still emit warnings that do not fail the build. The script scans every output
line for the word `warning` (case-insensitive). Any hit fails the build. The MSBuild
summary line `0 Warning(s)` is informational and is excluded so a clean build can pass.

The first 20 warning lines are reported. Remaining warnings are counted but not printed.
The artifact records the warnings per project.

### Publish drops `.pri` when `EnableMsixTooling` is off

Without `EnableMsixTooling=true` the publish folder can miss the app's `.pri` and compiled
XAML. The app then crashes at startup inside `Microsoft.UI.Xaml.dll` with `0xC000027B`.
The ordinary `bin` build is unaffected, so this only shows up after publish.

The script cannot infer this from exit codes. Mitigations:

- `Directory.Build.props` and the app projects keep `EnableMsixTooling=true`.
- The E2E harness launches the published app and fails when the window never appears.
- The file snapshot records `resources.pri` and app `.pri` files so a missing file is
  visible in the artifact.

### Different bytes at the same relative path

Hub, Regedit.App, and Update publish into one folder. A shared dependency can be copied
twice with different bytes, for example a stale project reference or a mismatched
runtime pack. The last writer silently wins.

The script hashes every file after each publish. If a later project writes an existing
relative path with a different SHA-256, the build fails and names both projects and the
file. Identical bytes are allowed because that is normal for shared runtime files.

### Partial publish leaves a mixed folder

If project two fails, the output folder contains project one plus whatever project two
wrote before failing. A later run that skips the delete would mix versions.

The script deletes the output folder before the first publish, so every run starts clean.
On failure it leaves the partial folder for diagnosis but writes `pass: false` in the
artifact. Consumers must check the artifact or the exit code, not merely the folder's
existence.

### Stale output hides a collision

Files from an earlier publish can survive if only project two is rerun. The snapshot then
compares new files against old files and can report a false collision, or worse, an old
file can make a broken build look complete.

The output folder is removed before the first publish. The artifact also records the
start timestamp and duration so a stale artifact is recognizable.

### ReadyToRun failure

`PublishReadyToRun=true` runs the crossgen step after compilation. It can fail on an
unsupported runtime pack, a mismatched SDK, or a locked output file. The failure is a
normal nonzero exit and is reported per project. There is no silent fallback to a
non-ReadyToRun publish, because that would hide a release-mode difference.

### E2E flavor leaking into a release

The E2E flavor passes `-p:BwcFlavor=E2E`, which defines `BWC_E2E` and enables the
registry sandbox switch. Publishing that binary as a release would ship a test switch.

- E2E output goes to `publish/<rid>-e2e`, never `publish/<rid>`.
- The artifact records `flavor`, and the file name includes `-e2e`.
- `run-app.js` uses the same suffix when it launches or builds.
- Release steps must read the artifact and refuse `flavor: "e2e"`.

### Version drift

`package.json`, `Directory.Build.props`, and `CHANGELOG.md` can disagree. The build then
stamps a version that does not match the release notes or the tag.

`sync-version.js` treats `package.json` as the source of truth. It updates `Version` and
`FileVersion` in the main `PropertyGroup` and ensures a matching CHANGELOG section.
`--check` fails when a rewrite would be needed, so CI can reject drift without writing.

### Prerelease FileVersion mapping

Windows file versions need four numeric fields. A SemVer prerelease cannot be copied
directly. `0.2.0-beta.3` maps to `0.2.0.3`; stable `0.2.0` maps to `0.2.0.0`.
Only `X.Y.Z` and `X.Y.Z-beta.N` with `N >= 0` are accepted. Anything else, including
`rc`, build metadata, and leading zeroes, exits 1 before any file is touched.

### Non-idempotent sync

A second run that rewrites formatting, duplicates a CHANGELOG heading, or appends another
blank line creates noisy diffs and makes `--check` useless.

The helpers compare the finished text with the input and return the original string when
nothing needs to change. A second run prints `in sync` and leaves every byte identical.
The acceptance run hashes the fixture after run one and run two.

### CRLF loss

`Directory.Build.props` and `CHANGELOG.md` are checked out with either LF or CRLF
depending on Git configuration. Rebuilding the file from split lines can silently convert
all line endings and produce a whole-file diff.

Both helpers detect the existing newline style, insert with that style, and never run a
global newline conversion. The original text is sliced, not re-serialized. The sync
script verifies the written file by reading it back.

### Corrupt XML after a props write

A bad insertion can duplicate a closing tag, nest a `PropertyGroup`, or leave
`<FileVersion>` outside the main group. MSBuild then fails with an opaque XML error.

The helper only replaces text inside the first unconditional `PropertyGroup`, requires
exactly one `<Version>`, rejects more than one `<FileVersion>`, and checks tag balance
before writing. After writing, the script re-reads the file and confirms both values. A
mismatch exits 1.

### `--root` path traversal

`--root` exists for temporary fixtures. A relative path with `..` or an absolute path can
point anywhere the user can write.

The script resolves the path to an absolute directory and refuses a missing directory or
one without `package.json`, `Directory.Build.props`, and `CHANGELOG.md`. It never takes a
file name from the caller: all writes are fixed joins under the resolved root. The flag is
documented as a test seam, not a sandbox escape.

### Missing or malformed source files

A missing `package.json`, invalid JSON, a missing `# Changelog` heading, or a props file
without a `<Version>` element stops the run with exit 1 before writing. The error names
the file and the missing structure. `--check` follows the same validation and never
writes a partial result.

## Commit trailer policy

### Human trailer deleted

The hook must keep a human co-author who writes `<!human@example.com>`. A broad regular
expression that removes every `Co-authored-by` line would delete that person.

The parser keeps `Co-authored-by` lines whose email is prefixed with `!`, removes the
`!`, and drops all other emailed trailers. Non-email trailers such as a URL are left
untouched.

### Agent trailer kept

Agent addresses must be dropped even when prefixed with `!`. The deny list covers
`@cursor.com`, `noreply@anthropic.com`, `copilot@github.com`, and
`*+copilot@users.noreply.github.com`.

### Newline and trailing newline changes

Commit message files use LF or CRLF. Rewriting with a fixed newline changes the message.
The stripper detects the existing style, preserves it, and preserves the original
trailing-newline behavior by trimming only trailing blank lines it would otherwise
accumulate.

### Git hooks run under sh on Windows

Git for Windows runs hooks with `sh`, which may not see the same `PATH` as PowerShell.
`.githooks/strip-coauthor-trailers.sh` routes through `cmd.exe` when `%OS%` is
`Windows_NT` and converts paths with `cygpath` when available. If node is still missing,
the hook exits 1 and the commit is blocked rather than committed with unwanted trailers.

`scripts/direct-execution.mjs` compares real paths so a script run through a symlink or a
Windows short path still executes `main()` instead of silently exiting 0.

### `--check-range` false negatives

The range check reads `git log --format` and looks for leftover `<!email>` markers or
agent addresses. A wrong range, an empty range, or a shallow clone can produce no commits
and therefore no hits. The script reports git failures with exit 1; callers must pass the
exact range they intend to police.

## Runtime launch

### Accidental elevation

Launching a WinUI app with a manifest that requests elevation triggers UAC on every run.
Development launches must not prompt.

The default path sets `__COMPAT_LAYER=RunAsInvoker` and spawns detached with ignored
stdio. `--elevated` is a separate manual path that uses PowerShell `Start-Process -Verb
RunAs` and deliberately does not set the compatibility layer. Automated runs never use
it.

### Missing executable

A launch before a build, or after a failed publish, finds no exe. The script prints the
full expected path and exits 1 instead of starting an unrelated binary or silently
succeeding.

### Node missing

All scripts are plain Node files with built-in modules only. If `node` is absent, npm is
absent too. The git hook wraps its node call and reports the missing runtime.
