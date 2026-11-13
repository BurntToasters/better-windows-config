# 0002: File ownership during parallel work

Workers run in parallel lanes. Each lane owns a directory; only the manager edits shared files.
A worker that needs a change to a shared file reports it instead of making it.

## Manager-owned shared files

- `BetterWindowsConfig.slnx`, `global.json`, `nuget.config`
- `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
  `build/*.props`
- `package.json` (a scripts-lane worker may be granted the `scripts` block for one batch)
- `src/Bwc.Registry/NativeMethods.txt`, every other `NativeMethods.txt`
- `src/*/App.xaml` of exe heads
- `src/Bwc.UI/Themes/Dense.xaml` (theme metrics; package U1 owns it for its batch)
- The command table of each tool (menu and accelerator definitions)
- Every `Strings/*/Resources.resw`
- `AGENTS.md`, `docs/contracts/**`, `docs/decisions/**`

Features register through new files (a new command class, a new dialog file, a new `.resw`
under the feature's own folder) so parallel packages do not collide.

## Lanes

| Lane | Owns | Packages |
|---|---|---|
| Scripts | `scripts/`, `.github/` | F2, F5, D4 to D9 |
| Tests | `tests/` | F4 |
| Core and Hub | `src/Bwc.Core/`, `src/Bwc.Hub/` | H1, H2 |
| UI controls | `src/Bwc.UI/` | U1 to U11 |
| Registry engine | `src/Bwc.Registry/` | B1 to B11 |
| Registry Editor | `src/Bwc.Regedit/`, `src/Bwc.Regedit.App/` | R1 to R14 |
| Distribution | `installer/`, `src/Bwc.Update/` | D1 to D3 |

Inside a lane, two workers may run at once only if the manager splits the lane's directory
between them by subfolder for that batch.
