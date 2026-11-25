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

## 0.2.0 additions (Local Group Policy Editor)

The rules above still apply. Release 0.2.0 adds the lanes and shared files below. The plan with the
full piece table is the manager's; the ids here match it.

### New manager-owned shared files

- `src/Bwc.Gpedit.App/App.xaml` (the exe head, like every other `src/*/App.xaml`)
- The `NativeMethods.txt` of each new project: `src/Bwc.Gpo/`, `src/Bwc.Policy/`,
  `src/Bwc.Gpedit/`, `src/Bwc.Gpedit.Editor/`, and `src/Bwc.Gpedit.App/`
- `docs/contracts/policy-engine.md` (covered by `docs/contracts/**`)

### New lanes

| Lane | Owns | Pieces |
| --- | --- | --- |
| Docs foundation | `docs/contracts/policy-engine.md`, `docs/decisions/0002-file-ownership.md`, `docs/parity/gpedit-inventory.md`, version files | M0 |
| Fixtures | `docs/parity/oracle/gpedit/fixture/` | F1 |
| Oracle | `tools/Bwc.Oracle.Resources/`, `scripts/oracle.js`, `scripts/check-regedit-strings.js`, `.gitignore` (O1); `tools/Bwc.Oracle.Capture/Gpedit/Session/` (O2), `.../Driver/` (O3a), `.../Matrix/` (O3b); goldens in `docs/parity/oracle/gpedit/` | O1, O2, O3a, O3b |
| Scaffold | new `src/` projects, `src/Bwc.Core/`, `src/Bwc.Hub/`, the build and flavor scripts, `BetterWindowsConfig.slnx` | S1a |
| Distribution | `installer/`, `src/Bwc.Update/` and its tests, release scripts | S1b |
| Guards | `scripts/check-policy-writer.js`, gate steps, `tests/Bwc.E2E/Harness/GpoGuard.cs`, `docs/failure-modes/quality-gate.md` | G0 |
| Gpo engine | `src/Bwc.Gpo/Pol/` and `tests/Bwc.Gpo.Tests/Pol*` (E1); `src/Bwc.Gpo/{Store,Comments,Refresh}/` (E3) | E1, E3 |
| Policy engine | `src/Bwc.Policy/Admx/` (E2); `src/Bwc.Policy/Model/` (E4); `src/Bwc.Policy/{Filter,Store}/` (E5) | E2, E4, E5 |
| Console frame | `src/Bwc.UI/Console/Shell/` (U2a), `.../List/` (U2b), `.../Extended/` (U2c), `.../Frame/` (U3a), `.../Favorites/` and `.../Options/` (U3b) | U2a to U3b |
| UI controls (shared additions) | `src/Bwc.UI/{Menu,Tree,Icons}` and `src/Bwc.Regedit/Commands/RegeditMenu.cs` (U1a); `src/Bwc.UI/Dialogs/` (U1b) | U1a, U1b |
| Editor | `src/Bwc.Gpedit.Editor/Dialog/` (G3a), `.../Options/` (G3b) | G3a, G3b |
| Gpedit | `src/Bwc.Gpedit/{Tree,List,Policy}` (G1), `Commands` (G2), `Save` (G4), `Filter` (G5), `Templates` (G6) | G1, G2, G4 to G6 |
| Elevated interop | `tests/Bwc.E2E/Gpedit/Interop/`, `scripts/elevated-runner/` | A2 |

Inside a lane, the same one-worker-per-subfolder rule as above applies. Engine tests live under
`tests/Bwc.Gpo.Tests/` and `tests/Bwc.Policy.Tests/` and belong to the piece that owns the code they
cover. GUI tests live under `tests/Bwc.E2E/Gpedit/` and `tests/Bwc.E2E/Console/` and belong to the
piece that holds the token.

### Rules that matter for parallel batches

- **Token.** `publish/<rid>-e2e` and `tests/Bwc.E2E` are shared. In one batch, only one piece
  publishes `publish/<rid>-e2e` and runs GUI tests. Pieces marked `T` in the plan hold the token;
  the manager gives it to one of them per batch.
- **Builds.** No two workers build the same project at once. A worker builds its own project with
  `--no-dependencies`. The manager does the full build at the start of each batch. The public API
  of an upstream project changes only between batches.
- **Strings.** Each piece owns its own `.resw` file inside its own project (for example
  `Strings/en-US/Filter.resw`). Workers do not edit a shared `Resources.resw` and never share one
  `.resw` file.
- **Commits.** Workers never commit. The manager commits a checkpoint after each accepted batch.
