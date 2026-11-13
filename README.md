# Better Windows Config

Better Windows Config (BWC) is a set of Windows tools built with C# on .NET 10 and WinUI 3. Its
first tool is a re-implementation of the classic Registry Editor (`regedit.exe`) that keeps the
classic layout and density. Classic regedit is the reference for behavior and layout.

The project is in development and has no release yet. The version in `package.json` is 0.1.0.

## What is in the repo

- The Registry Editor (`src/Bwc.Regedit.App`), with its engine in `src/Bwc.Registry`.
- A Hub launcher (`src/Bwc.Hub`) and a self-updater (`src/Bwc.Update`).
- An NSIS installer script (`installer/`). It is not compiled yet.
- Tests in `tests/`, including end-to-end UI tests in `tests/Bwc.E2E`.
- Node.js build and gate scripts in `scripts/`.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11.
- .NET SDK 10 (pinned in `global.json`).
- Node.js 22.22.2 or newer on the 22 line, 24.15 or newer on the 24 line, or 26 or newer.
- NSIS 3.10 or newer is needed only to build the installer.

## Quick start

```
npm install
node scripts/test-all.js --quick
node scripts/launch-unelevated.js publish\win-arm64-e2e\BwcRegedit.exe
```

`--quick` runs the static checks, both app builds, and the test projects. Publish the app first with
`node scripts/dotnet-build.js --rid win-arm64 --flavor e2e` if the `publish` folder is missing.

App icons are generated from `resources/*.png` with ImageMagick. Run `npm run icons:normalize` after
changing a source, and `npm run icons:normalize -- --check` to verify the `.ico` files. See
[docs/build-setup.md](docs/build-setup.md#app-icons).

## Documentation

- [`handoff.md`](handoff.md): current state, work status, known issues, and rules for contributors.
- [`docs/build-setup.md`](docs/build-setup.md): toolchain, build, gate, flavors, and launch rules.
- [`docs/elevated-runner.md`](docs/elevated-runner.md): the opt-in runner for administrator tests.
- [`docs/installer.md`](docs/installer.md): building the installer.
- [`docs/parity/oracle/README.md`](docs/parity/oracle/README.md): how the classic regedit oracle
  is captured.
- [`docs/parity/regedit-inventory.md`](docs/parity/regedit-inventory.md): the parity inventory.
- [`docs/contracts/`](docs/contracts) and [`docs/failure-modes/`](docs/failure-modes): behavior
  contracts and failure modes.

## Contributing

Read [`AGENTS.md`](AGENTS.md) first. Run `npm run validate` before you send a change.

## License

Mozilla Public License 2.0. See [`LICENSE`](LICENSE).
