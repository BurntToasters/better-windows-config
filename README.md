<div align="center">
  <img src="resources/bwc.png" alt="Better Windows Config logo" width="128" />

  <h1>Better Windows Config</h1>

  <p>Faithful, modern re-implementations of the classic Windows tools, starting with the Registry Editor.</p>

  <p>
    <a href="https://github.com/BurntToasters/better-windows-config/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/BurntToasters/better-windows-config?label=release" /></a>
    <img alt="Windows 10 2004+ and Windows 11" src="https://img.shields.io/badge/Windows-10%202004%2B%20%7C%2011-0078D4" />
    <img alt="x64 and ARM64" src="https://img.shields.io/badge/arch-x64%20%7C%20ARM64-555" />
    <a href="LICENSE"><img alt="License: MPL-2.0" src="https://img.shields.io/badge/license-MPL--2.0-brightgreen" /></a>
  </p>

  <p>
    <a href="https://github.com/BurntToasters/better-windows-config/releases/latest"><b>⬇️ Download</b></a>
    &nbsp;·&nbsp;
    <a href="CHANGELOG.md"><b>Changelog</b></a>
    &nbsp;·&nbsp;
    <a href="https://rosie.run/support"><b>❤️ Support</b></a>
  </p>
</div>

---

I like the classic Windows admin tools. They are dense, fast, and every key has a shortcut. What I
don't like is that they look and feel stuck in 2001 next to the rest of Windows 11. Better Windows
Config keeps the classic layout and behavior and rebuilds the tools on .NET 10 and WinUI 3, with
proper light, dark, and high contrast themes.

The first tool is the **Registry Editor**. Classic `regedit.exe` is the reference: if it behaves
differently from the original, that's a bug.

> [!WARNING]
> Better Windows Config is early and unstable. Keep a backup (`File` > `Export`) before you change
> registry keys you care about.

## Tools

<table>
  <tr>
    <td width="96" align="center" valign="top"><img src="resources/bwc-regedit.png" alt="Registry Editor icon" width="64" /></td>
    <td valign="top">
      <b>Registry Editor</b><br />
      The classic tree and list with classic menus, mnemonics, and accelerators. It includes
      editors for every common value type, Find, Favorites, Permissions, Load and Unload Hive,
      <code>.reg</code> import and export compatible with classic regedit, the classic command-line
      switches, and read-only remote registry browsing.
    </td>
  </tr>
  <tr>
    <td width="96" align="center" valign="top"><img src="resources/bwc.png" alt="Hub icon" width="64" /></td>
    <td valign="top">
      <b>Hub</b><br />
      A launcher for the tools, with About, update settings, and a signed self-updater.
    </td>
  </tr>
</table>

More classic tools will follow once the Registry Editor is solid.

## Install

1. Download the setup EXE for your PC from the [latest release](https://github.com/BurntToasters/better-windows-config/releases/latest):
   `x64` for most PCs, `arm64` for Windows on ARM.
2. Run it. The installer is per-user, so it doesn't need administrator rights.

If Windows SmartScreen says it protected your PC, choose **More info** and then **Run anyway**. The
installer is signed, but a new release can take a while to build up SmartScreen reputation.

To update, accept the update the Hub offers, or run a newer setup EXE over the installed copy.
Coming from 0.1.0? 0.1.0 can't update itself, so download the latest setup EXE and run it once.

To uninstall, use **Settings** > **Apps** > **Installed apps** > **Better Windows Config** >
**Uninstall**. Your settings stay in `%LOCALAPPDATA%\BetterWindowsConfig`; delete that folder too
if you want them gone.

Requirements: Windows 10 version 2004 (build 19041) or later, or Windows 11.

Release assets are Authenticode-signed and come with my GPG signatures (`.asc`). You can verify
those with [my public key](https://tuxedo.rosie.run/GPG/BurntToasters_0xF2FBC20F_public.asc).

## Use

- Open **Better Windows Config** from the Start menu to get the Hub, or open
  **Registry Editor (BWC)** directly.
- The Registry Editor also works from the command line the way classic regedit does:

  ```powershell
  BwcRegedit.exe file.reg                       # import a .reg file
  BwcRegedit.exe /s file.reg                    # import silently
  BwcRegedit.exe /e backup.reg "HKCU\Software"  # export a branch
  ```

- Pick a theme in `View` > `Theme`. By default it follows the Windows setting.
- The Hub checks for updates when it starts and tells you when one is available. You can turn that
  off in the Hub's update settings. The updater only installs an update whose signatures match the
  key built into the app.

### ℹ️ Enjoying Better Windows Config? Consider [❤️ Supporting Me! ❤️](https://rosie.run/support)

## Report a problem

Open an issue on [GitHub](https://github.com/BurntToasters/better-windows-config/issues). If the
Registry Editor behaves differently from classic `regedit.exe`, say what you did and what each one
showed. Logs are in `%LOCALAPPDATA%\BetterWindowsConfig\<tool>\logs`.

## Contributing

Read [`AGENTS.md`](AGENTS.md) first. Run `npm run validate` before you
send a change. Commit subjects follow BCLS kind `commit` (for example `UI: Fix splitter drag.`), and
release notes in [`CHANGELOG.md`](CHANGELOG.md) follow BCLS kind `binary`.

<details>
<summary><b>Build from source</b></summary>

You need:

- Windows 10 version 2004 or later, or Windows 11.
- .NET SDK 10 (pinned in `global.json`).
- Node.js `26.13.1` or newer on the 26 line, and npm `12.2.2` or newer.
- NSIS 3.10 or newer, only to build the installer.

```powershell
npm install
node scripts/test-all.js --quick
npm run run:regedit
```

`--quick` runs the static checks, both app builds, and the test projects. See
[docs/build-setup.md](docs/build-setup.md) for the toolchain, flavors, the full gate, and launch
rules.

To change the version, edit `version` in `package.json` and run `npm run sync-version`. It copies
the version into `Directory.Build.props` and `CHANGELOG.md` (download links, the beta callout, and a
new `## Changes in` section). Replace the placeholder notes before you release.

</details>

<details>
<summary><b>Project layout and docs</b></summary>

| Path | What it is |
| --- | --- |
| `src/Bwc.Regedit.App` | The Registry Editor app. Its engine is `src/Bwc.Registry`. |
| `src/Bwc.Hub` | The Hub launcher. |
| `src/Bwc.Update` | The self-updater. |
| `installer/` | The NSIS installer script. |
| `tests/` | Test projects, including end-to-end UI tests in `tests/Bwc.E2E`. |
| `scripts/` | Node.js build, gate, and release scripts. |

- [`docs/build-setup.md`](docs/build-setup.md): toolchain, build, gate, flavors, and launch rules.
- [`docs/RELEASE.md`](docs/RELEASE.md): signing and the release runbook.
- [`docs/installer.md`](docs/installer.md): building the installer.
- [`docs/elevated-runner.md`](docs/elevated-runner.md): the opt-in runner for administrator tests.
- [`docs/parity/regedit-inventory.md`](docs/parity/regedit-inventory.md): the classic regedit parity
  inventory, and [`docs/parity/oracle/README.md`](docs/parity/oracle/README.md) for how the oracle is
  captured.
- [`docs/contracts/`](docs/contracts) and [`docs/failure-modes/`](docs/failure-modes): behavior
  contracts and failure modes.

</details>

## License

Mozilla Public License 2.0. See [`LICENSE`](LICENSE).
