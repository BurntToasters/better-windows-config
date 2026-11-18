# Policy writer failure modes

Scope: piece G0 of release 0.2.0, the static guard `scripts/check-policy-writer.js` and its gate step
`policy-writer`. It covers every way a write to the machine's real local group policy
(`%SystemRoot%\System32\GroupPolicy`), to the system or BWC template folders (`PolicyDefinitions`), or to the BWC import
store (`%ProgramData%\BetterWindowsConfig\PolicyDefinitions`) could slip into the tree without passing the named writer
and the write policy. This page was written before the guard code. The contract is
`docs/contracts/policy-engine.md`, section "`Bwc.Gpo`: write policy and E2E seams". The model is
`docs/failure-modes/registry-writer.md` and `scripts/check-registry-writer.js`.

The guard is a safety net against mistakes in the app, in tests, and in tools run by a developer. It is not a security
boundary against a hostile process. It reads source text, so a path assembled at run time from pieces the checker cannot
see (section C) is a known gap that the protected folders close by forbidding every write API there.

## Named files (fixed here, later pieces use these names)

| File | Role |
| --- | --- |
| `src/Bwc.Gpo/Store/GpoFileWriter.cs` | The only writer of the GPO folder. Atomic temp-then-replace, with a backup. May not name `PolicyDefinitions`. |
| `src/Bwc.Policy/Store/TemplateStoreWriter.cs` | The only writer of the BWC import store. May not name `GroupPolicy`, `GroupPolicyUsers`, `Registry.pol`, `gpt.ini`, or `comment.cmtx`. |
| `src/Bwc.Gpo/Refresh/PolicyRefresh.cs` | The only file that may name `RefreshPolicy`, `RefreshPolicyEx`, `ForceSyncFgPolicy`, `EnterCriticalPolicySection`, `LeaveCriticalPolicySection`, `IGroupPolicyObject`, or `CLSID_GroupPolicyObject`. |
| `src/Bwc.Gpo/Store/NativeMethods.txt`, `src/Bwc.Policy/Store/NativeMethods.txt` | The only `NativeMethods.txt` files in the protected projects that may list file-write functions, beside the writer that needs them. |
| `src/Bwc.Gpo/Refresh/NativeMethods.txt` | The only `NativeMethods.txt` that may list the refresh and critical section functions, beside `PolicyRefresh.cs`. |
| `tools/Bwc.Oracle.Capture/Gpedit/**` | May launch `gpupdate`, `secedit`, `gpedit.msc`, `mmc`. It runs only elevated, by hand, inside a backup session. |

The protected projects are `src/Bwc.Gpo`, `src/Bwc.Policy`, `src/Bwc.Gpedit`, `src/Bwc.Gpedit.Editor`, and
`src/Bwc.Gpedit.App`. The pure codec in `src/Bwc.Gpo/Pol` and the template parser in `src/Bwc.Policy/Admx` do no file I/O,
so the guard holds them to that. A parser that wants to read a template file takes a `Stream` or a string from the
caller.

## Rules

| Rule | Where | Meaning |
| --- | --- | --- |
| `protected-write` | protected projects | Any file-write API (section A) in a file that is not a named writer. |
| `protected-native-name` | protected projects, `NativeMethods.txt` too | A native file-write name (section A) in a file that is not a named writer or its adjacent `NativeMethods.txt`. |
| `policy-path-write` | `src/`, `tests/`, `tools/`, `scripts/` | A file-write API in a file that also holds a literal naming `GroupPolicy`, `GroupPolicyUsers`, `PolicyDefinitions`, `Registry.pol`, `gpt.ini`, or `comment.cmtx`, unless allowlisted with a reason. |
| `refresh-name` | everywhere | A refresh, critical section, or `IGroupPolicyObject` name, or the GUID of `CLSID_GroupPolicyObject` or `IID_IGroupPolicyObject` (`EA502722-...`, `EA502723-A23D-11D1-A7D3-0000F87571E3`, for a COM import that never spells the name), in code, a string, or a script, outside `PolicyRefresh.cs` and its `NativeMethods.txt`. |
| `policy-process-launch` | everywhere | A launch of `gpupdate`, `secedit`, `gpedit.msc`, or `mmc` outside `tools/Bwc.Oracle.Capture/Gpedit/**`. |
| `writer-scope` | the two writers | `GpoFileWriter.cs` names `PolicyDefinitions`, or `TemplateStoreWriter.cs` names a GPO folder literal. Each writer touches its own folder only. |

Comments are stripped before matching. String literals are matched where a rule says "literal". A literal that only
quotes an API name in prose is a false positive that the allowlist handles (section D).

## A. Write APIs the guard recognizes

| ID | Case | Risk | How the checker catches it |
| --- | --- | --- | --- |
| A1 | `File.WriteAllText`, `WriteAllBytes`, `WriteAllLines`, `AppendAllText`, `AppendAllLines`, their `Async` forms, `File.Create`, `CreateText`, `AppendText`, `OpenWrite`, `File.Encrypt`, `Decrypt` | A direct write | The `File.` or `Directory.` member name list, matched on code with comments and literals blanked. |
| A2 | `File.Delete`, `File.Move`, `File.Copy`, `File.Replace`, `File.SetAttributes`, `File.SetUnixFileMode`, `File.CreateSymbolicLink` | A delete, rename, or copy into the folder, or a swap of `Registry.pol` | Same list. |
| A3 | `Directory.CreateDirectory`, `Directory.Delete`, `Directory.Move`, `Directory.SetCreationTime` and the other `Set*Time` members, `Directory.CreateSymbolicLink` | A folder created, removed, or renamed | Same list. |
| A4 | `FileInfo` or `DirectoryInfo` members `Delete`, `Create`, `MoveTo`, `CopyTo`, `Replace`, `Open`, `CreateText`, `AppendText`, `OpenWrite`, `CreateSubdirectory`, `SetAccessControl` | A write through an instance, where the call site has no `File.` or `Directory.` name to match | `CreateText`, `AppendText`, `OpenWrite`, `CreateSubdirectory`, and `SetAccessControl` are matched wherever they appear. `Delete`, `Create`, `Replace`, `MoveTo`, `CopyTo`, and `Open` are common names on other types (`XmlReader.Create`, `string.Replace`, `Stream.CopyTo`), so they are matched only on a variable the file declares as `FileInfo`, `DirectoryInfo`, or `FileSystemInfo`, or on a `new FileInfo(...)` chain. A `var` that holds a `FileInfo` from `GetFiles()` is a known gap (D9). |
| A5 | `new FileStream(...)`, `File.Open(...)`, `File.OpenHandle(...)`, `FileInfo.Open(...)` with `FileMode.Open` and no `FileAccess` | `FileStream` defaults to read and write access, so a "read" open can write | `FileMode.Create`, `CreateNew`, `OpenOrCreate`, `Append`, and `Truncate` are matched alone. `FileAccess.Write` and `FileAccess.ReadWrite` are matched alone. `new FileStream(` and `File.Open(` are violations unless their argument list names `FileAccess.Read`. |
| A6 | `FileOptions.DeleteOnClose` | A read handle that deletes the file when it closes | Matched alone. `FileShare` values are not matched, because a tolerant read legitimately uses `FileShare.ReadWrite`. |
| A7 | `new StreamWriter(path)`, `new XmlTextWriter(path)`, `XmlWriter.Create(path)`, `document.Save(path)`, `new BinaryWriter(File.Open...)` | A text or XML writer over a path | These are matched unless the argument list is empty or names a stream (`stream`, `memory`, `buffer`, `writer`, `builder`, `ms`, `sb`). A writer over a `MemoryStream` or a stream parameter passes. A writer over any other name is reviewed, which errs toward a violation. A literal first argument always counts. `BinaryWriter` over `File.Open` is caught by A5. |
| A8 | A `SafeFileHandle` from a `CreateFile` P/Invoke with `GENERIC_WRITE`, `FILE_GENERIC_WRITE`, `GENERIC_ALL`, or a `CREATE_*` disposition | A write that never touches `System.IO` | The native name list matches `CreateFile`, `CreateFileA`, `CreateFileW`, `CreateFile2`, `CreateFileTransacted*`, `NtCreateFile`, `ZwCreateFile`, `NtOpenFile`. Every call is a violation, whatever its access, because the checker does not read the argument values. A read-only `CreateFile` outside the protected projects needs no allowlist because the native name rule applies only inside them (A12). |
| A9 | `MoveFileEx`, `MoveFile*`, `ReplaceFile`, `DeleteFile`, `RemoveDirectory`, `CreateDirectory`, `CopyFile*`, `CreateHardLink`, `CreateSymbolicLink`, `SetFileAttributes`, `SetEndOfFile`, `WriteFile`, `WriteFileEx`, `SetFileInformationByHandle`, `NtWriteFile`, `NtSetInformationFile`, `NtDeleteFile` | A P/Invoke write, delete, or rename | The native name list, with or without the `A`, `W`, `Ex`, `2`, and `Transacted` suffixes. Matched as a code identifier, as the whole text of a string literal (an `EntryPoint = "..."`), and as a line of `NativeMethods.txt`. |
| A10 | A native name inside a `LibraryImport` or `DllImport` attribute | A write hidden in an attribute | The attribute names the function in `EntryPoint`, a literal, or the extern method name, which is an identifier. All three are matched. |
| A11 | `NativeMethods.txt` lists a write name for CsWin32 to generate | The generated `PInvoke` class exposes a write that no source file names | `NativeMethods.txt` lines are matched by exact name (with a trailing `A` or `W`) in the protected projects. |
| A12 | The same native name in a project that is not protected | `MoveFileEx` is used by the installer and by tests, legitimately | Not matched by name alone. Outside the protected projects only rule `policy-path-write` applies, so such a file is flagged only when it also names a policy path. |
| A13 | `Process.Start("cmd", "/c copy x C:\Windows\System32\GroupPolicy")` or PowerShell `Copy-Item` | A write by a child process | The file holds a `GroupPolicy` literal and, in a script, the write cmdlet or command in code. See B. |

## B. Scripts and processes

| ID | Case | Risk | How the checker catches it |
| --- | --- | --- | --- |
| B1 | A `.ps1` runs `Set-Content`, `Add-Content`, `Out-File`, `Copy-Item`, `Move-Item`, `Remove-Item`, `New-Item`, `Rename-Item`, `Clear-Content`, `Set-Acl`, `Export-Clixml`, `Export-Csv`, `Tee-Object`, `Compress-Archive`, `Expand-Archive`, an alias at the start of a line (`copy`, `cp`, `move`, `mv`, `del`, `rm`, `ri`, `ren`, `md`, `mkdir`, `ni`, `xcopy`, `robocopy`, `icacls`, `takeown`, `attrib`, `mklink`), or `[System.IO.File]::WriteAllText` and the other static members, in a file that names a policy path | A script edits the real folder | The cmdlets, aliases, and static members are matched in code (comments and string contents blanked). The file must also hold a policy path name, in code or in a string, for `policy-path-write` to fire. Output redirection (`>`, `>>`) is not matched (C1). |
| B2 | A `.cs`, `.ps1`, or `.js` file starts a process with `cmd /c copy ...`, `xcopy`, `robocopy`, `move`, `del`, `icacls`, `mklink`, or similar in a string | Same, through a command line | A literal that starts with the command (behind an optional `cmd /c`, `powershell -c`) counts as a write in a file that references `Process`, `ProcessStartInfo`, `ShellExecute`, `CreateProcess*`, `child_process`, `spawn`, `exec*`, or `Start-Process`. It fires only with a policy path name in the same file. |
| B3 | A `.js`, `.mjs`, or `.cjs` calls `fs.writeFileSync`, `writeFile`, `appendFile`, `copyFile`, `cp`, `rename`, `rm`, `unlink`, `mkdir`, `rmdir`, `createWriteStream`, `truncate`, `symlink`, or their `Sync` and `promises` forms | A Node helper writes the folder | Matched in code by member name. Fires with a policy path literal. |
| B4 | A process launch of `gpupdate`, `secedit`, `gpedit.msc`, or `mmc`, as the executable or as the head of a command line | `gpupdate /force` rewrites policy and `secedit /configure` applies a template to the machine | A string literal that is exactly the executable name, or that starts with it (optionally behind `cmd /c` or a directory), in a file that references `Process`, `ProcessStartInfo`, `ShellExecute`, `CreateProcess*`, `child_process`, `spawn`, `exec*`, or `Start-Process`. In a `.ps1` the bare command word in code also counts. |
| B5 | A launch from `tools/Bwc.Oracle.Capture/Gpedit/**` | The oracle capture must start classic `gpedit.msc` and `mmc` | Allowed by path. It runs only elevated and by hand, inside `GpeditSession`, which backs up and restores. |
| B6 | `Start-Process gpupdate` or `& secedit` in `scripts/` | A developer script applies policy | Matched in `.ps1` code by command word. A `.js` helper spawning them is matched by B4. |

## C. Paths the guard cannot see through

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| C1 | A path built from pieces: `Path.Combine(system32, "Group" + "Policy")` | The folder name never appears whole | Known gap outside the protected projects. Inside them, every write API is a violation, so the split path has nothing to write with. Review a new `Path.Combine` into `System32` the same way as a new write. |
| C2 | An environment variable path (`%SystemRoot%`, `%windir%`, `%ProgramData%`, `Environment.SystemDirectory`, `SpecialFolder.System`, `SpecialFolder.CommonApplicationData`) | The base is read at run time | The checker matches the folder name literal, not the base. A path still ends in `GroupPolicy` or `PolicyDefinitions`, so the literal rule fires. |
| C3 | A path read from a file, a command line, or an environment variable at run time | The literal is not in the source | Known gap. The E2E harness hashes the real `GroupPolicy` folder before and after every unelevated run (`gpo.real-unchanged`, a later piece), which catches an actual write. This guard stops the code that could do it, the harness proves it did not. |
| C4 | A test writes to the real folder | A test run changes the machine's policy | A test file with a write API and a policy path literal is a violation unless allowlisted with a reason that says it writes only into a temp sandbox. Today no test file is allowlisted for this. |
| C5 | A sandbox test whose temp path is built from a fixture name that happens to be `GroupPolicy` | A legitimate temp write trips the rule | Allowlist that file with the reason, and the test must build the folder under `Path.GetTempPath()` or the run data root. |
| C6 | The write policy (`GpeditStorePolicy`) is bypassed by a caller that calls `GpoFileWriter` with no policy | A write skips the sandbox check | Not a guard rule. It is a contract and code review rule: `GpoFileWriter` takes the policy in its constructor and every public write method calls it first. Tested in `tests/Bwc.Gpo.Tests`. |
| C7 | A second class in `src/Bwc.Gpo` writes a file and is not named | A new writer with its own checks | A `protected-write` violation. The fix is to route the write through `GpoFileWriter`, not to add an allowlist entry. |
| C8 | A folder name spelled in a different case (`policydefinitions`) | Windows paths are case insensitive | `PolicyDefinitions` is matched case sensitively (D10), so the lowercase spelling is a gap outside the protected projects. The on-disk spelling used by Windows and BWC is `PolicyDefinitions`. |
| C9 | Output redirection in a script (`> file`) or a write by a tool the checker does not know (`tar`, `7z`, `certutil`) | A write with no recognized API | Known gap outside the protected projects. A script that names a policy path is reviewed. The E2E hash check (C3) catches an actual write. |

## D. False positives and the allowlist

| ID | Case | Handling |
| --- | --- | --- |
| D1 | `scripts/check-policy-writer.js` holds every pattern | Allowlisted by path with a reason. |
| D2 | A literal in a message, such as `"Registry.pol was not written"`, in a file with an unrelated `File.WriteAllText` | An allowlist entry with a reason, or reword the message. The checker does not guess intent. |
| D3 | A C# helper named `CreateFile`, `DeleteFile`, or `MoveFile` that is not a P/Invoke | An identifier match inside the protected projects. Rename the helper. |
| D4 | `tools/Bwc.Oracle.Capture/Gpedit/Session/**`, `Driver/**`, `Matrix/**`, `Probe/**` write the GPO and template folders | Allowlisted by directory for `policy-path-write` with the reason "oracle capture: runs only elevated by hand inside GpeditSession, which backs up and restores". |
| D5 | The oracle capture files outside those four folders (`GpeditCommands.cs`, `GpeditDpi.cs`, `Program.cs`) | Not allowlisted. They fire only if they hold a write API and a policy literal together. |
| D6 | Tests that name `Registry.pol` or `GroupPolicy` as fixture text and write only to a sandbox | Allowlist per file, with a reason, only if the rule fires. |
| D7 | The allowlist has an entry for a file that no longer exists or no longer trips | The checker does not fail on stale entries, because a later piece may add the file back. Entries are removed when a file moves. |
| D11 | A protected project must write one file that is not a policy file. Today: `src/Bwc.Gpedit/GpeditRootView.Editor.cs` saves the editor view settings with `Bwc.Core.SettingsStore<T>.Save` into the BWC data root | The counted allowlist (`countedWriteAllowlist` in the checker). An entry is keyed by file and holds one exact hit label (`.Save over a path`), the most hits of that label (1) and a reason. It is not a whole-file exemption: any other write label in the file is still a `protected-write` violation, and so is every hit of the allowed label beyond the maximum. The self-test has four cases: one `.Save(x)` passes; two fail on the second; one `.Save(x)` plus `File.WriteAllText` fails on the `WriteAllText`; the same call in another protected file fails. A second save call, or any other write, in that file needs a new decision. |
| D8 | `NativeMethods.txt` comments and blank lines | Skipped, as in the registry checker. |
| D9 | `Delete`, `Replace`, or `Create` on a receiver the checker cannot type | Only variables declared as `FileInfo`, `DirectoryInfo`, or `FileSystemInfo` in the same file, and `new FileInfo(...)` chains, are checked for the bare members, so a `var` from `GetFiles()` and a wrapper type such as `IFileSystem` are gaps. In the protected projects any such wrapper must itself be a named writer, and a new one is a design review item, not an allowlist entry. |
| D10 | The ADMX root element `policyDefinitions` and the assembly name `Microsoft.GroupPolicy.AdmTmplEditor` appear in literals | Test fixtures and tools write XML text and mention assemblies, which are not folders | `PolicyDefinitions` is matched case sensitively, so the element does not match. `GroupPolicy` must be a whole word with no dot beside it, so `Microsoft.GroupPolicy.AdmTmplEditor` and `GroupPolicyObject` do not match. A lowercased folder name (`grouppolicy`) matches, a lowercased `policydefinitions` does not (C8). |
| D11 | A help or log literal that starts with a tool name, such as `"gpupdate applies policy"`, in a file that starts a process | Read as a command line | A `policy-process-launch` violation. Reword so the tool name is not first, or allowlist the file with a reason. |
| D12 | `scripts/check-registry-writer.js` | It writes a temporary fixture tree and holds `Registry.pol` in an allowlist reason string | Allowlisted by path for `policy-path-write` with that reason. |
| D13 | `tools/Bwc.Oracle.Capture/Gpedit/Probe/ProbeCommand.cs` | Prints "gpupdate and RefreshPolicyEx are never called" | Allowlisted for `refresh-name` with that reason. It calls neither. |

## E. Self-test

`node scripts/check-policy-writer.js --self-test` builds a temporary tree with one violation for every rule and every
rule's pass cases: a writer file, a comment, a prose literal, a read-only `FileStream`, a sandbox test, an oracle capture
file, and the adjacent `NativeMethods.txt`. It requires exit 1 and the exact list of reported lines, and it requires the
clean files to stay out of the output. It never writes inside the repository.

## F. Gate

The gate runs the guard as step `policy-writer` right after `registry-writer`, including `--quick`. The gate also runs
`tests/Bwc.Gpo.Tests` as `test-gpo` and `tests/Bwc.Policy.Tests` as `test-policy`. See `quality-gate.md`.
