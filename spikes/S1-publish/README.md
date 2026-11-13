# S1: shared-folder publish spike

Question: can the Hub and Tool heads (WinUI 3, unpackaged, self-contained) be published into ONE folder, sharing one
copy of the .NET runtime and the Windows App SDK, and still work?

Answer: yes. Publishing both heads sequentially into one folder produced no file with different content, and both heads
pass the E2E on win-x64 and win-arm64. The folder is about half the size of two separate self-contained folders.
Caveat: the library string is only reachable through an explicit PRI lookup (see "PRI").

Generated, git-ignored outputs: `out/<rid>/` (shared folder), `out/.separate/<rid>/tool/` (size baseline),
`out/publish-<rid>.json` (collisions, PRI, warnings, sizes), `out/s1-result.json` (E2E checks and sizes).

## Layout

| Path | What |
| --- | --- |
| `S1.slnx` | Solution for the UI library and both heads. It builds with 0 warnings. The E2E project is run on its own. |
| `Bwc.Spike.UI/` | WinUI class library: `ProbeBadge` user control, `Strings/en-US/Resources.resw` (`ProbeText`), `ProcessProbe` (token elevation, AppUserModelID), `SpikeOptions` (`--exit-code`, `--auto-close-ms`). |
| `Bwc.Spike.Hub/` | Exe head `BwcSpikeHub`, manifest `asInvoker`, AUMID `BWC.Spike.Hub`. |
| `Bwc.Spike.Tool/` | Exe head `BwcSpikeTool`, manifest `highestAvailable`, AUMID `BWC.Spike.Tool`. |
| `publish.ps1` | Publishes both heads into `out/<rid>/`, checks collisions, writes `out/publish-<rid>.json`. |
| `Bwc.Spike.E2E/` | MSTest.Sdk + FlaUI. Checks each present `out/<rid>/`, writes `out/s1-result.json`. |
| `.gitignore` | Ignores `out/`. |

## Commands

Refresh PATH in a new shell so `dotnet` resolves:
`$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File spikes\S1-publish\publish.ps1 -Rid win-x64
powershell -NoProfile -ExecutionPolicy Bypass -File spikes\S1-publish\publish.ps1 -Rid win-arm64
dotnet test spikes\S1-publish\Bwc.Spike.E2E
```

`publish.ps1` was run under Windows PowerShell 5.1 (not re-tested on PowerShell 7). The E2E launches real windows, so it needs an
interactive desktop. It holds `Local\BwcGuiTestLock` while it drives windows and sets `__COMPAT_LAYER=RunAsInvoker` on
every child, so the highestAvailable Tool does not prompt for UAC.

Last run (this machine, Windows 11 ARM64, 100% DPI): both publishes exit 0 with 0 build warning lines;
`dotnet test` 2 of 2 passed; `dotnet build S1.slnx -c Release` 0 warnings.

## What publish.ps1 does

1. `dotnet publish` the Hub into an empty `out/<rid>/`. Its file set is also the size of a separate Hub folder.
2. `dotnet publish` the Tool into the same `out/<rid>/`. Before this step every Hub file is hashed (SHA-256). After it,
   each Hub file is compared with the disk. Same hash: harmless. Different hash: collision.
3. `dotnet publish` the Tool alone into `out/.separate/<rid>/tool/`. That is the size of a separate Tool folder.
4. Writes `out/publish-<rid>.json`.

All publishes use `-r <rid> -p:Platform=<ARM64|x64> -p:PublishReadyToRun=true`. The Tool and Hub both reference the UI
library, which is built once per RID.

## Collisions: sequential publish into one folder is safe

| RID | Files in shared folder | Present in both publishes, identical bytes | Changed by 2nd publish | Tool-only files | Hub files removed |
| --- | --- | --- | --- | --- | --- |
| win-x64 | 527 | 521 | 0 | 6 | 0 |
| win-arm64 | 523 | 517 | 0 | 6 | 0 |

The six tool-only files are `BwcSpikeTool.exe`, `.dll`, `.deps.json`, `.runtimeconfig.json`, `.pdb`, and `.pri`.
Everything else (runtime, WinAppSDK, `Bwc.Spike.UI.dll`/`.pri`, the Hub files) is shared with identical bytes.
Hash comparison is the check, not timestamps: the copied output keeps the NuGet source timestamps.

Why it is safe here: the two heads share an identical runtime and identical library bits, and the only per-app
files are named after the assembly (`BwcSpikeHub.*`, `BwcSpikeTool.*`), so nothing collides. Two things would break it:
two heads with the same `AssemblyName`, or a shared PRI name.

## PRI

- Unpackaged WinAppSDK 2.6.5 loads `<exe dir>\<AssemblyName>.pri`. `ResourceLoader.GetDefaultResourceFilePath()` returns
  `...\BwcSpikeHub.pri` in the Hub and `...\BwcSpikeTool.pri` in the Tool. No `resources.pri` is generated
  (`resourcesPriPresent: false`). Each head therefore keeps its own PRI in the shared folder.
- Publish merges the library PRI into each head PRI. Both head PRIs contain the map `Bwc.Spike.UI/Resources` and the
  string. `Bwc.Spike.UI.pri` is also copied; it is identical for both heads.
- The brief says publishing without `EnableMsixTooling` drops the app PRI and crashes at startup with 0xC000027B.
  That was not re-tested here. This recipe keeps `EnableMsixTooling=true`.
- Reading the library string (measured with throwaway probes outside the repo):
  - `new ResourceLoader().GetString("ProbeText")` throws `0x80073B1F`. The default loader sees only the app's
    `Resources` map.
  - `new ResourceLoader("Bwc.Spike.UI/Resources")` treats the argument as a PRI path: `DirectoryNotFoundException`.
  - `new ResourceLoader().GetStringForUri(new Uri("ms-resource://.../ProbeText"))` throws `0x80073B1F`.
  - Works: `new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), "Bwc.Spike.UI/Resources").GetString("ProbeText")`
    (used in `ProbeBadge`).
  - Also works: `new ResourceManager().MainResourceMap.GetSubtree("Bwc.Spike.UI/Resources").GetValue("ProbeText").ValueAsString`.

## Sizes

MiB = 1,048,576 bytes. Bytes are from `out/s1-result.json` and `out/publish-<rid>.json`.

| RID | Shared folder | Two separate self-contained folders | Saved |
| --- | --- | --- | --- |
| win-x64 | 281.7 MiB (295,413,199 B) | 557.8 MiB (584,921,519 B: Hub 278.9 + Tool 278.9) | 276.1 MiB (289,508,320 B), 49.5% |
| win-arm64 | 310.3 MiB (325,326,182 B) | 614.9 MiB (644,776,097 B: Hub 307.5 + Tool 307.5) | 304.7 MiB (319,449,915 B), 49.5% |

The shared folder is about the size of one self-contained head. The runtime and WinAppSDK account for almost all of it.

## E2E (Bwc.Spike.E2E)

Per RID, 20 checks, all passing on both RIDs:
- publish: report matches folder; 0 build warnings; 0 differing collisions; no Hub file removed.
- each head: exe present; manifest `requestedExecutionLevel` (`asInvoker` for Hub, `highestAvailable` for Tool) read
  from RT_MANIFEST id 1 with LoadLibraryEx as datafile; `<AssemblyName>.pri` holds the library string.
- each head window (launched with RunAsInvoker): title shown; `ProbeBadge` shows `Shared library string OK`;
  Elevation is `not elevated`.
- each head `--exit-code 7 --auto-close-ms 1500`: closes itself and the process returns 7.

A RID whose folder is missing is Inconclusive (not a pass). It is recorded as `notPublished` in the artifact.

## Fragile points found while building

- `DISABLE_XAML_GENERATED_MAIN=true` did not suppress the generated `Program.Main` with WinAppSDK 2.6.5. A hand-written
  `Main` collided with it (CS0101). The heads keep the generated Main. `SpikeOptions` sets `Environment.ExitCode` from
  `--exit-code`, which the generated `void Main` returns to the OS.
- A WinUI app whose App.xaml lacks `XamlControlsResources` fails on the first `ThemeResource` with
  `Cannot find a Resource with the Name/Key AccentFillColorDefaultBrush`. The fault is inside `Microsoft.UI.Xaml.dll` and
  the process exits 0xC000027B, the same code as a missing PRI. Both heads merge `XamlControlsResources`.
- A managed exception thrown during XAML construction (a failing `ResourceLoader` call, for example) also surfaces as
  0xC000027B, with no .NET exception in the log. `ProbeBadge` catches the lookup error and shows
  `UNRESOLVED: ...`, so a failure shows as text in the E2E.
- UIA: the AutomationId set on the `UserControl` and on its `Border` did not show up in the UIA tree. The AutomationId
  `ProbeBadge` is on the `TextBlock` that holds the text, and that element is found and read.
- Windows PowerShell 5.1 has no `Path.GetRelativePath` and no `Encoding.Latin1`. `publish.ps1` avoids both. It also
  avoids `@()` over a generic `List`, which throws "Argument types do not match" on 5.1.

## Not verified here

- Program Files, elevated run, standard-user run: manager VM (out of scope).
- Taskbar pinning per tool with the explicit AppUserModelID. The E2E does not query the AUMID. `ProcessProbe` throws on a
  non-zero HRESULT, which would crash the head, so a passing run means the call returned S_OK.
- The brief's claim about 0xC000027B without `EnableMsixTooling` (see PRI).
