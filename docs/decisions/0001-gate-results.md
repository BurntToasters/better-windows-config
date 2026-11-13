# 0001: Gate spike results and the decisions they settle

Date: 2026-11-10. Machine: Windows 11 25H2 (build 26200) ARM64, display scale 200%
(192 DPI). All numbers below were reproduced by the manager, not only by the spike author.

## M0 Toolchain: pass

.NET SDK 10.0.403 and Windows App SDK 2.6.5 build, publish, and run WinUI 3 apps for x64 and
ARM64 from the CLI without Visual Studio. FlaUI UIA3 drives both builds (x64 under emulation).

Decision: `EnableMsixTooling=true` on every WinUI exe head. Without it, publish leaves out the
app `.pri` and compiled XAML and the app crashes at start with `0xC000027B`.
See `docs/build-setup.md`.

## M1 Oracle: pass, with two declared differences

Resource dump (`docs/parity/oracle/regedit-resources.*`), export goldens (`exports/`), metrics
(`regedit-metrics.json`), and the inventory (`docs/parity/regedit-inventory.md`, 534 rows).

- Metrics were captured at 192 DPI, not 96. Every value halves exactly to whole DIPs, so the
  dense theme uses the halved values: tree row 17, list row 18, header 20, menu bar 19, tree
  indent 16, font Segoe UI 9 pt (12 DIP). The file named `regedit-100dpi.png` is a 200%
  capture. A 100% capture on a VM is still owed before the pixel-exact density check.
- Classic regedit on Windows 11 25H2 has no status bar and no View > Status Bar item. BWC
  follows the current classic tool: no status bar.

## S1 Publish shape: pass, preferred shape adopted

Hub and tool exes publish sequentially into one folder per RID with zero differing file
collisions; each exe gets its own `<AssemblyName>.pri`. Shared folder is 310 MB on ARM64
against 615 MB for two separate folders. Library `.resw` strings need
`new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), "<Library>/Resources")`.

Decision: separate exe heads in one shared install folder, PowerToys style. Each tool also
gets its own Start menu shortcut with its AppUserModelID. The native-stub fallback is not
needed. Still to verify on a clean x64 VM: run from Program Files, elevated admin, standard
user, taskbar pinning per tool.

## S2 Tree and S3 List: pass

Both use `ItemsRepeater` + `StackLayout` in a `ScrollViewer` with fixed-height row elements
and no item container chrome. 200,000 rows, win-arm64 Release, manager rerun:

| Control | Row DIP | Max frame ms | p99 ms | Frames > 100 ms | Row height |
|---|---|---|---|---|---|
| Tree | 16 / 18 / 20 | 64 / 76 / 77 | 9.4 / 9.9 / 9.3 | 0 | exact |
| List | 17 / 18 / 20 | 68 / 19 / 19 | 29 / 9.5 / 9.5 | 0 | exact |

Text at 12 DIP is not clipped at 16 to 20 DIP rows. Decisions:

- `DenseTree` and `DetailsList` are built on `ItemsRepeater`, not `TreeView`/`ListView`.
- Tree expand of a node with 10,000+ children must insert in chunks across frames; a single
  insert peaked at 156 ms in some runs.
- List sort runs on the thread pool and applies on the UI thread (a 200k sort costs about
  150 ms).
- Column widths live in a shared model; resizing invalidates arrange only.

## S4 Interop: pass (unelevated); elevated run owed

- Alt alone, F10, Escape, Alt+mnemonic: window-procedure subclass on the top-level and
  descendant HWNDs. Arrow keys inside the MenuBar: XYFocus plus `MenuBar.PreviewKeyDown` (the
  window hook never sees arrows).
- Owned modal window: `OverlappedPresenter.CreateForDialog()`, owner set with
  `SetWindowLongPtr(GWLP_HWNDPARENT)` (AppWindow owner is read-only in 2.6), and an explicit
  `EnableWindow(owner, false)`: `IsModal` alone does not disable the owner in 2.6. ComboBox in
  the modal works (bug #10722 did not reproduce).
- Common file dialogs: CsWin32 `IFileOpenDialog`/`IFileSaveDialog` owned by the WinUI HWND.
- `EditSecurity`: source-generated COM `ISecurityInformation` with `[PreserveSig]`. aclui frees
  the descriptor returned by `GetSecurity` with `LocalFree`, so it must be `LocalAlloc`ed and
  never freed by BWC (double free crashes with `0xC0000374`).

Still to run: the same E2E suite elevated (needs an elevated test runner, VM pass).

## S5 Minisign: pass

.NET 10 has no Ed25519 and no BLAKE2b. BouncyCastle.Cryptography 2.6.2 verifies Node-signed
files and a real Zinnia release artifact with Zinnia's public key; 64 tamper and format tests
pass. Decision: the updater verifier uses BouncyCastle (verify-only), streams the file, refuses
legacy `Ed` signatures, and accepts current plus next public key for rotation.
