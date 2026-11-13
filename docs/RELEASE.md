# Release

This document covers the release tooling: the minisign signer, the manifest validator, the
offline dry run, the release session and preflight, the GitHub draft, verify, and publish
commands, and the Windows release VM flow (`npm run release:win`) that builds, signs, and uploads
the stable release. The first release is `0.1.0` on the stable channel, with no beta.

The update format is fixed by [contracts/update-manifest.md](contracts/update-manifest.md).
The release failure cases are in [failure-modes/release.md](failure-modes/release.md).

## Release VM runbook

Run these steps on the Windows release VM, in order. The sections after this one give the
detail and the refusal reasons.

### 1. One-time VM setup

1. Install Node 22.22.2 or newer (or 24.15+), the .NET SDK 10.0.403 or newer, NSIS 3.10 or newer
   (`makensis.exe` on PATH, or `NSIS_HOME` set), and GnuPG 2.4 or newer with the release signing
   key imported.
2. Run `gh auth login` for the account that can create releases in `BurntToasters/better-windows-config`.
3. In an Administrator PowerShell, from the repository root, run `npm run setup:win:artifact-signing`.
   It installs the Artifact Signing client tools (see "Windows code signing" below).
4. Copy Zinnia's `.env` to this checkout's `.env`. Its signing variables are `TAURI_SIGNING_PRIVATE_KEY`,
   `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`, `GPG_KEY_ID`, `GPG_PASSPHRASE`, and the `AZURE_*` values.
   Nothing else is needed. The release flow reads only variables that Zinnia's `.env` has. The trusted
   public key comes from `src/Bwc.Update/Signing/TrustedKeys.cs` (user step 2 below), not from `.env`.
   `GH_REPO_OWNER` and `GH_REPO_NAME` may stay unset.

### 1a. Release update key (once per key)

The update key signs `bwc-stable.json` and each setup EXE; installed copies trust only the public
keys compiled into `src/Bwc.Update/Signing/TrustedKeys.cs`. Use a key of BWC's own, not Zinnia's.

1. On a trusted machine: `npx @tauri-apps/cli signer generate -w C:\keys\bwc-updater.key` and set a
   password. Keep `bwc-updater.key` and the password in your password manager.
2. In the release VM's `.env`: `TAURI_SIGNING_PRIVATE_KEY` = the contents of `bwc-updater.key`,
   `TAURI_SIGNING_PRIVATE_KEY_PASSWORD` = the password.
3. In a dev checkout on a branch: `npm run release:embed-key -- --pub C:\keys\bwc-updater.key.pub`
   (or `npm run release:embed-key -- --from-env` with that `.env`, which derives the public key from
   the private key so the two cannot differ). It rewrites `TrustedKeys.cs` with `IsPlaceholder =
   false`, reads it back, and prints the key id. Commit it and merge to `main`.
4. Rotation later: `--rotate` keeps the old key beside the new one for one release, so installed
   copies can verify the release that switches keys; drop the old key with `--replace` afterwards.

The release flow signs each manifest with a one-year expiry (`RELEASE_EXPIRES_HOURS` in
`scripts/run-release.js`). Installed copies refuse an expired feed, so publish a release (or re-sign
the feed) at least once a year.

### 2. Release branch

Stable releases come from `main`. Merge `experimental` into `main` through the normal review, then:

```powershell
git switch main
git pull --ff-only origin main
```

`package.json`, `Directory.Build.props`, and the `CHANGELOG.md` section for `0.1.0` must agree.
The preflight checks this, and a dirty tree is refused.

### 3. Build, gate, sign, and upload the draft

```powershell
npm run release:win
```

This runs, in order: the stable policy (no beta overrides, no non-canonical GitHub target), the
preflight, the quick quality gate (`test:all --quick`, which runs no GUI E2E on this VM), a release
session that records the gate with E2E `skipped`, a clean `artifacts/release/<version>/` folder, and
then `npm run release:win:continue`. The continuation builds both RIDs (ship flavor), builds the two
signed setup EXEs, assembles and signs the bundle, adds GPG signatures, checks Authenticode, creates
or reuses the single draft and uploads its assets, verifies the draft, and runs `release:finalize`.

`release:finalize` mirrors the release folder and then runs `git fetch origin && git reset --hard @{u}
&& git clean -fd`. That discards local changes in this checkout, so run it only on this disposable
release VM. The runner prints a warning before it starts. Artifacts under `artifacts/` are git-ignored
and survive it.

If a step fails, fix the cause, then run `npm run release:win:resume`. It runs the policy and the
preflight, then the continuation. A session older than 24 hours is refused, so start again with
`npm run release:win` in that case.

### 4. Packaged-artifact QA

On a clean x64 VM, install the signed setup EXE from `artifacts/release/<version>/stable/`, then test
the update path, then uninstall. Check that each installed file is signed, that the Hub's update check
finds the published feed, and that the shell integration registers and removes cleanly. Do not
publish a draft that has not passed.

### 5. Publish, then verify the live feed

```powershell
npm run release:publish
npm run release:verify:published
```

`release:publish` runs the draft verification again against the downloaded assets before it flips
the draft to published. `release:verify:published` then proves the live updater feed and the
signatures for the published version.

## Status

- The release tooling (D4 to D7) and the Windows release VM flow are implemented as scripts. Nothing
  is published until the VM runbook above runs with `--apply` (`release:publish`).
- The built-in update key is still the placeholder in
  `src/Bwc.Update/Signing/TrustedKeys.cs`. Preflight, draft, verify, and publish all refuse it, so
  the real public key must be embedded first (user step 2 below).
- The dry-run harness uses an ephemeral test key and a `.dry-run.json` installer stand-in
  when NSIS has not produced a real setup EXE. `--apply` refuses both.
- On the release VM, `build-installer.js` runs NSIS and `gpg-sign.js` runs GnuPG. A dry run calls
  neither. `release:draft`, `release:verify`, and `release:publish` call `gh` only with `--apply`.
- The stable policy is `scripts/release-policy.js`. Only the release entry points call it, so local
  dev builds may still set `SKIP_WIN_CODESIGN=1`.

## Prerequisites

| Tool | Why |
|------|-----|
| Node 22.22.2, 24.15, or 26+ | All scripts. |
| .NET SDK 10.0.403 or newer | Only a non-dry-run publish uses it. |
| NSIS 3.10+ | A real setup EXE. The dry run does not need it. |
| GnuPG 2.4+ | Detached signatures of the release folder (`scripts/gpg-sign.js`). Used on the release VM only. |
| GitHub CLI, authenticated | Only `--apply` for draft, verify, and publish. Dry runs never start `gh`. |
| A real minisign release key | Created and protected by the user. |

Refresh PATH in PowerShell before a build or release command:

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
```

## User-owned steps

The release VM runbook above covers the day-to-day flow. These are the one-time duties the
scripts do not do for you.

1. Create the real minisign release key. Keep the secret key off the repository and off the
   release VM image history, and encrypt it with a password you keep in a password manager
   (`minisign -G`, or `tauri signer generate`, which writes the same format). The keygen in
   `scripts/sign-update-manifest.js` writes test keys only; never use one for a release. Check the
   encrypted-key path once with a key that minisign made (see 2a). Put the key in `.env` as
   `TAURI_SIGNING_PRIVATE_KEY` (base64 of the key file, the form `tauri signer generate` prints) and
   its password in `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`.
2. Put the real public key in `src/Bwc.Update/Signing/TrustedKeys.cs`, in the `Embedded` list, and
   set `IsPlaceholder = false`. The release tooling trusts exactly the keys in that list, matched by
   key id and key bytes. Rotation lists the old and new key during the overlap.
3. Hosting is GitHub Releases, the same as Zinnia. The repository is
   `BurntToasters/better-windows-config` unless `.env` sets `GH_REPO_OWNER` and `GH_REPO_NAME`.
   Run `gh auth login` on the release VM. The stable manifest is the `bwc-stable.json` asset of the
   latest stable release (see the Hub's `UpdateSources.cs`), and each installer is an asset of
   `releases/download/v<version>/`. The beta URL stays empty until a beta channel is published.
4. Install NSIS 3.10+ on the release VM. `build-installer.js` compiles `installer/bwc.nsi` with it. A
   dry-run artifact contains a stand-in, not an installer, and a release never uses one.
5. Authenticode-sign the BWC binaries and the setup EXE with Azure Artifact Signing (see "Windows code
   signing" below). SmartScreen warns on an unsigned download.
6. The first release is `0.1.0`, stable, with no beta. Build it from `main` on the release VM (see the
   runbook above). That is the first run that may use `--apply`.

## Secrets (`.env`)

`.env` is git-ignored. Copy `.env.example` to `.env` and fill it in. Zinnia's `.env` can be copied
unchanged, because the variables BWC does not use are kept in `.env.example`. The release scripts that
need it (`release:win`, `release:win:continue`, `release:win:resume`, `release:policy`,
`release:preflight`, `release:session:*`, `release:assemble`, `release:sign`, `release:sign:gpg`,
`release:mirror`, `release:draft`, `release:verify`, `release:verify:draft`, `release:publish`,
`installer:*`, and `release:dry-run`) load it with `node --env-file-if-exists=.env`. That needs Node
22.9 or newer, and it adds no dependency. Variables already set in the shell win over `.env`. The
PowerShell Authenticode step (`release:authenticode`) reads the environment of the run that loaded
`.env`, so start it through `npm run release:win:continue`, or set the Azure variables in the shell.

The variables this tooling reads are `TAURI_SIGNING_PRIVATE_KEY`, `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`,
`GPG_KEY_ID`, `GPG_PASSPHRASE`, `GH_REPO`, `GH_REPO_OWNER`, and `GH_REPO_NAME`. The trusted public keys are
not read from the environment: when no `--trusted-key` is given, the release tooling trusts exactly the
non-placeholder keys embedded in `TrustedKeys.cs`. A stable release refuses
`SKIP_WIN_CODESIGN`, `FORCE_UPLOAD`, `SKIP_RELEASE_MIRROR`, `ALLOW_ASSET_REPLACE`, and `SKIP_E2E` when
they are truthy, and any GitHub target other than `BurntToasters/better-windows-config` (policy S1 to
S5 in [failure-modes/release.md](failure-modes/release.md)). The signing key is never printed, logged, written
unencrypted, or passed on a command line. `--key` and `--secret-key` accept a path only. The failure
cases are K1 to K13 in [failure-modes/release.md](failure-modes/release.md).

## Version, channel, and branch

A release version is `X.Y.Z` or `X.Y.Z-beta.N`. The installers accept no other prerelease
form.

| Version | Channel | Branch | GitHub prerelease |
|---------|---------|--------|-------------------|
| `X.Y.Z-beta.N` | `beta` | `experimental` | `true` |
| `X.Y.Z` | `stable` | `main` | `false` |

The preflight refuses a version that disagrees with `package.json`,
`Directory.Build.props`, or the `CHANGELOG.md` section for that version. It also refuses a
dirty worktree. A detached HEAD is allowed only with `--allow-detached`, and only when the
commit is contained in the expected branch.

## 1. Test key and signatures (D4)

Create an unencrypted test key (or an encrypted one with `--password-env <NAME>`; `--test-kdf`
lowers the scrypt cost for tests only):

```powershell
node scripts/sign-update-manifest.js keygen --out artifacts/release/test-key --test-key
```

Sign any file (the tool writes `<file>.minisig` and `<file>.sig`):

```powershell
node scripts/sign-update-manifest.js sign artifacts/release/example.bin `
  --key artifacts/release/test-key `
  --trusted-comment "version:0.1.0-beta.1"
```

Verify one file:

```powershell
node scripts/sign-update-manifest.js verify artifacts/release/example.bin `
  --signature artifacts/release/example.bin.minisig `
  --trusted-key artifacts/release/test-key/minisign.pub
```

The algorithm is minisign prehashed `ED`: Ed25519 over BLAKE2b-512 of the file, then an
Ed25519 global signature over the signature bytes and the trusted comment. No
`minisign.exe` is needed.

Validate a manifest, its signature, and optional installer bytes:

```powershell
node scripts/validate-updater-manifest.js `
  --manifest artifacts/release/0.1.0-beta.1/beta/bwc-beta.json `
  --signature artifacts/release/0.1.0-beta.1/beta/bwc-beta.json.minisig `
  --trusted-key artifacts/release/0.1.0-beta.1/beta/ephemeral/minisign.pub `
  --version 0.1.0-beta.1 --channel beta `
  --require-arch win-x64 --require-arch win-arm64 `
  --installer win-x64=artifacts/release/0.1.0-beta.1/beta/BetterWindowsConfig-0.1.0-beta.1-win-x64-setup.dry-run.json `
  --installer win-arm64=artifacts/release/0.1.0-beta.1/beta/BetterWindowsConfig-0.1.0-beta.1-win-arm64-setup.dry-run.json
```

Add `--current-version <installed>` to refuse `same-version` and `downgrade`. Add
`--max-age-hours <n>` to refuse `expired`. Add `--structure-only` only for a deliberate
structural check without a signature.

## 2. Offline dry run (D5)

The exact acceptance command:

```powershell
node scripts/release-dry-run.js --rid win-arm64 --version 0.1.0-beta.1 --dry-run
```

Both RIDs, for a draft with both installers:

```powershell
node scripts/release-dry-run.js --rid all --version 0.1.0-beta.1 --dry-run
```

Options:

| Option | Meaning |
|--------|---------|
| `--rid win-x64\|win-arm64\|all` | Default `all`. `all` builds or describes both. |
| `--dry-run` | Do not publish. Describe the build commands and use existing artifacts. |
| `--no-build` | Do not build, but do not label the run a dry run. |
| `--pub-date <utc>` | Normalized UTC. Default now, or `SOURCE_DATE_EPOCH`. |
| `--download-base-url <url>` | Installer directory, https. Default `https://github.com/<owner>/<repo>/releases/download/v<version>`. |
| `--notes-url <url>` | Notes page, https. Default `https://github.com/<owner>/<repo>/releases/tag/v<version>`. |
| `--secret-key <dir\|file>` | Sign with this key file or folder instead of the ephemeral test key. The environment key is not read by the dry run. |
| `--installer-<rid> <path>` | Use an explicit installer file for one RID. |
| `--current-version <semver>` | Also check the generated version is newer. |
| `--max-age-hours <n>` | Freshness limit used by the validation report. Default 24. |

Output is `artifacts/release/<version>/<channel>/` (one manifest per channel). The version
folder also holds `publish-<rid>.json`:

| File | Contents |
|------|----------|
| `bwc-<channel>.json` | Schema 1 manifest for the chosen version and channel (`bwc-stable.json` or `bwc-beta.json`). These are the GitHub asset names. |
| `bwc-<channel>.json.minisig` | Raw minisign signature. Its trusted comment is `version:<semver> channel:<channel> expires:<utc>`. |
| `bwc-<channel>.json.sig` | Base64 envelope of the same signature. |
| `BetterWindowsConfig-<version>-<rid>-setup.exe` | Real setup EXE, when a matching installer record exists. |
| `BetterWindowsConfig-<version>-<rid>-setup.dry-run.json` | Stand-in when NSIS has not produced a setup EXE. |
| `<installer>.minisig`, `<installer>.sig` | Installer signature, raw and base64. Its trusted comment is `version:<semver> arch:<win-x64 or win-arm64>`. |
| `assembly.json` | Key id, fingerprint, expiry, arches, and whether the key was a test key. No secret material. |
| `ephemeral/minisign.pub` | Public key of an ephemeral test key (dry run only). The secret key is never written. |
| `publish-<rid>.json` | Every file in `publish/<rid>` with its size and SHA-256, plus a tree hash. |
| `release-plan.json` | Version, channel, RIDs, build commands, and artifact sources. |
| `validation-report.json` | D4 validator result, key fingerprint, sizes, hashes, and errors. |

The signed comments are exact. The manifest comment is
`version:<semver> channel:<stable|beta> expires:<yyyy-MM-ddTHH:mm:ssZ>`. A manifest without a
channel, with another channel, or past its `expires` is refused (`trusted-comment-invalid`,
`channel-mismatch`, `expired`). The expiry defaults to 168 hours after the publish time and is set
with `--expires-hours`.

## 2a. Assemble a bundle with a real key (`scripts/assemble-bundle.js`)

`release-dry-run.js` calls the same assembler. Use it directly to sign a release bundle with the
user's key:

```powershell
node scripts/assemble-bundle.js --version 0.1.0-beta.1 `
  --installer-win-x64 artifacts/installer/BetterWindowsConfig-0.1.0-beta.1-win-x64-setup.exe `
  --installer-win-arm64 artifacts/installer/BetterWindowsConfig-0.1.0-beta.1-win-arm64-setup.exe `
  --secret-key C:\release-keys\bwc-release `
  --password-env BWC_RELEASE_KEY_PASSWORD
```

With `TAURI_SIGNING_PRIVATE_KEY` set in `.env`, leave out `--secret-key` and `--password-env`. The key
comes from that variable, and its password from `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`. Through npm the
same command is `npm run release:assemble -- --version 0.1.0 --installer-win-x64 <setup.exe>`, which
loads `.env`. The bundle is written to `artifacts/release/<version>/<channel>/` with
`bwc-<channel>.json`, `bwc-<channel>.json.minisig`, and `bwc-<channel>.json.sig`. Stable is
`bwc-stable.json`, and beta is `bwc-beta.json`. The installer URLs default to
`https://github.com/<owner>/<repo>/releases/download/v<version>/`, and `--download-base-url` replaces
that directory. `--notes-url` replaces the notes page.

- The signing key's public half must be one of the `Embedded` keys in
  `src/Bwc.Update/Signing/TrustedKeys.cs`, matched by key id and key bytes. A key that is not
  embedded is refused (`embedded-key-mismatch`). Only `--dry-run` allows another key, and then
  the output is labelled `testKey: true`.
- A password is never a command-line argument. `--password-env <NAME>` reads it from that
  environment variable. Without it, an encrypted key asks on a terminal, and in a script it
  refuses (`password-missing`, exit 2). `--password <value>` is refused everywhere
  (`password-argument`).
- The assembler validates its own output with the validator before it reports PASS. A
  wrong password refuses `wrong-password` and writes nothing.

**Key formats.** A key made by `sign-update-manifest.js keygen --test-key` is a test key: it is
unencrypted by default, or encrypted with `--password-env`. The encrypted format follows the
minisign layout (`Ed`, kdf `Sc` with scrypt, checksum `B2`, XSalsa20 keystream). The tool checks it
against keys it creates. It has not been checked against a key that the minisign program created,
so the first real release key must be tested with the user's own minisign before it signs anything.
The scrypt cost is the libsodium sensitive setting (about 1 GiB of memory, a few seconds per use).

The key id and the public key bytes are the only things that decide whether a key is trusted. The
comment line of a key file is never used for that decision.

The harness never opens a socket and never publishes. A non-dry run invokes
`scripts/dotnet-build.js` for a missing publish; that build uses the local SDK and NuGet
cache. A dry run does not build.

## 3. Preflight and session (D6)

Preflight alone:

```powershell
node scripts/release-preflight.js --version 0.1.0-beta.1 --channel beta `
  --trusted-key artifacts/release/0.1.0-beta.1/beta/ephemeral/minisign.pub
```

Add `--artifact-dir artifacts/release/0.1.0-beta.1` to validate the manifest, signature,
both architectures, and installer hashes as part of preflight. Add `--allow-detached` only
for a CI checkout whose commit is contained in `experimental` or `main`.

Create the session after preflight passes:

```powershell
node scripts/release-session.js create --version 0.1.0-beta.1 --channel beta `
  --trusted-key artifacts/release/0.1.0-beta.1/beta/ephemeral/minisign.pub `
  --artifact-dir artifacts/release/0.1.0-beta.1
```

Verify or show it:

```powershell
node scripts/release-session.js verify --version 0.1.0-beta.1 --channel beta
node scripts/release-session.js show
```

The session is `artifacts/release/session.json`. It records the version, channel, commit,
branch, package version, changelog heading, trusted key fingerprint, start time, and maximum
age (default 24 hours). It is a local proof, not a signature. The session file is
git-ignored, so creating it does not dirty the worktree.

Create the session with the same key you will use for `--apply`. Preflight now requires the
trusted key to be one of the embedded keys in `TrustedKeys.cs`, so a dry-run test key is refused
(`embedded-key-mismatch`) by preflight, session creation, draft, verify, and publish. A test key
therefore can not produce a session or a draft. Test keys are for the offline bundle checks in
sections 1 and 2 only.

`--commit <rev>` on draft, verify, and publish must resolve to HEAD. Any other commit is refused
(`session-commit-mismatch`). A draft never targets the literal name `HEAD`. The GitHub repository
is `--repo`, then `GH_REPO`, then `GH_REPO_OWNER` and `GH_REPO_NAME`, then the default
`BurntToasters/better-windows-config`. The origin remote is never read (`repo-invalid` for a
malformed name). `--root <dir>` selects another checkout on the same commands.

## 4. Draft (D7)

The default is a dry run. It prints the exact commands and starts no `gh` process:

```powershell
node scripts/release-draft.js --version 0.1.0-beta.1 --channel beta `
  --dir artifacts/release/0.1.0-beta.1 `
  --trusted-key artifacts/release/0.1.0-beta.1/beta/ephemeral/minisign.pub
```

A dry run accepts a test key only with `--allow-test-key`, and accepts a `.dry-run.json`
stand-in only as a plan warning. `--apply` refuses both. The draft plan writes these body
files under the artifact folder:

- `gh/create-draft.json`
- `gh/edit-draft.json`
- `release-notes.md`

The release notes are the whole `CHANGELOG.md`, which is written as a BCLS `binary` release body
(downloads table, signing callout, `## Changes in \`vX.Y.Z:\`` sections, and Release Info). The
draft needs exactly one non-empty `## Changes in` section for the version, without the
sync-version placeholder.

Apply, on the release VM, with the real key:

```powershell
node scripts/release-draft.js --version 0.1.0-beta.1 --channel beta `
  --dir artifacts/release/0.1.0-beta.1 `
  --trusted-key C:\release-keys\bwc-release.pub `
  --apply
```

The draft path is idempotent. If one draft exists, it is patched with the current notes and
body. If two drafts exist for the tag, it refuses `duplicate-drafts`. If a published
release exists, it refuses `already-published`. Assets are uploaded only when the name is
absent; a same-size asset is skipped, and a same-name asset with another size refuses
`asset-conflict` unless `--replace-assets` is given.

## 5. Verify (D7)

Dry run:

```powershell
node scripts/release-verify.js --version 0.1.0-beta.1 --channel beta `
  --dir artifacts/release/0.1.0-beta.1 `
  --trusted-key artifacts/release/0.1.0-beta.1/beta/ephemeral/minisign.pub
```

Apply, against the draft:

```powershell
node scripts/release-verify.js --version 0.1.0-beta.1 --channel beta `
  --dir artifacts/release/0.1.0-beta.1 `
  --trusted-key C:\release-keys\bwc-release.pub `
  --apply
```

`--apply` lists the releases, selects the single draft for `v<version>`, checks the target
commit and prerelease flag, and downloads every required asset through
`gh api -H "Accept: application/octet-stream" /repos/.../releases/assets/<id>`. It writes
each stream to a temporary folder, compares its SHA-256 with the local artifact, and runs
the D4 validator again on the downloaded bytes. The result is
`artifacts/release/<version>/draft-verification.json`.

A draft is required. A published release refuses `already-published`. A missing draft
refuses `draft-missing`. A missing asset refuses `asset-missing`.

## 6. Publish (D7)

Dry run:

```powershell
node scripts/release-publish.js --version 0.1.0-beta.1 --channel beta `
  --dir artifacts/release/0.1.0-beta.1 `
  --trusted-key artifacts/release/0.1.0-beta.1/beta/ephemeral/minisign.pub
```

Apply:

```powershell
node scripts/release-publish.js --version 0.1.0-beta.1 --channel beta `
  --dir artifacts/release/0.1.0-beta.1 `
  --trusted-key C:\release-keys\bwc-release.pub `
  --apply
```

`--apply` first runs `release-verify.js --apply` against the draft. Only if every
downloaded asset matches the local signed artifact does it send the PATCH body
`{"draft":false,"prerelease":true|false}`. If the release is already published with the
expected prerelease flag, it prints a no-op and exits 0. It never creates a draft.

## GitHub CLI commands

All commands use `gh api`, so they work with a draft that has no git tag yet. Replace
`<owner>/<repo>` with the repository resolved from `--repo`, `GH_REPO`, `GH_REPO_OWNER` and
`GH_REPO_NAME`, or the default `BurntToasters/better-windows-config`.

List releases:

```
gh api --paginate --slurp /repos/<owner>/<repo>/releases?per_page=100
```

Create a draft:

```
gh api --method POST /repos/<owner>/<repo>/releases --input artifacts/release/<version>/gh/create-draft.json
```

Patch an existing draft:

```
gh api --method PATCH /repos/<owner>/<repo>/releases/<draft-id> --input artifacts/release/<version>/gh/edit-draft.json
```

Upload an asset:

```
gh api --method POST -H "Content-Type: application/octet-stream" --input <asset> /repos/<owner>/<repo>/releases/<draft-id>/assets?name=<url-encoded-name>
```

Download an asset:

```
gh api -H "Accept: application/octet-stream" /repos/<owner>/<repo>/releases/assets/<asset-id>
```

Publish:

```
gh api --method PATCH /repos/<owner>/<repo>/releases/<draft-id> --input artifacts/release/<version>/gh/publish.json
```

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Pass, draft created, publish no-op, or dry-run plan printed with no refusal. |
| 1 | Refused by policy or validation. The line names a reason in brackets. |
| 2 | Bad arguments or usage. |
| 3 | A required input, session, manifest, or artifact is missing. |
| 4 | An external command failed under `--apply` or a requested build. |

A dry run that finds a refusal still prints the plan and exits 1.

## Refusal reasons

| Reason | Meaning |
|--------|---------|
| `dirty-tree` | Modified or untracked files. |
| `version-mismatch` | package.json, Directory.Build.props, changelog, or the requested version disagree. |
| `changelog-missing`, `changelog-empty` | No usable notes for the version. |
| `branch-mismatch`, `detached-head` | Wrong branch or an unverified detached HEAD. |
| `placeholder-key` | The built-in key, a key file, or a manifest key is a placeholder. |
| `missing-trusted-key` | No public release key was given. |
| `wrong-key` | The signature key id is not in the trusted list. |
| `malformed-key` | A trusted key does not parse. The whole check fails. |
| `signature-missing`, `signature-format`, `signature-invalid` | Unsigned, malformed, or altered manifest. |
| `trusted-comment-invalid`, `version-mismatch`, `wrong-arch` | The signed comment disagrees with the manifest or installer. |
| `manifest-missing`, `manifest-invalid`, `unsupported-schema` | Missing or unreadable manifest. |
| `bom`, `invalid-json`, `duplicate-key`, `trailing-data`, `too-deep` | Strict JSON refused. |
| `version-invalid`, `channel-invalid`, `channel-mismatch`, `prerelease-on-stable` | Version or channel policy. |
| `pubdate-invalid`, `pubdate-future`, `expired` | Freshness policy. |
| `downgrade`, `same-version` | Version is not newer than `--current-version`. |
| `url-rejected` | http, user info, fragment, dot segment, encoded traversal, or bad characters. |
| `missing-architecture` | A required RID has no installer entry. |
| `installer-missing`, `asset-missing` | Required bytes are absent. |
| `installer-size-mismatch`, `installer-hash-mismatch` | Installer bytes differ from the manifest. |
| `installer-signature-invalid`, `installer-version-mismatch` | Installer signature or signed version is wrong. |
| `missing-session`, `session-expired`, `session-mismatch`, `session-commit-mismatch`, `session-exists` | Session proof is absent, already present, or stale. |
| `missing-artifact`, `missing-trusted-key-source` | The D5 artifact folder, its manifest, or the key source is absent. |
| `test-key`, `dry-run-artifact` | A dry-run key or stand-in cannot be published. |
| `already-published`, `draft-missing`, `duplicate-drafts` | Release state is not the expected draft. |
| `draft-target-mismatch`, `prerelease-mismatch` | Draft metadata is not for this commit or channel. |
| `asset-conflict`, `asset-size-mismatch`, `asset-hash-mismatch` | Remote asset differs from the local artifact. |
| `gh-missing`, `gh-failed`, `repo-invalid` | `gh` is absent or failed, or the repository is not owner/name. |
| `build-failed`, `verification-failed` | A requested publish failed, or the pre-publish verify refused. |
| `key-missing`, `key-unreadable`, `key-malformed` | No key source, a key given as text on the command line, or an environment value that is neither minisign key text, a key path, nor base64 of one. The value is never printed. |
| `stable-override` | A stable release set a beta override (`SKIP_WIN_CODESIGN`, `FORCE_UPLOAD`, `SKIP_RELEASE_MIRROR`, `ALLOW_ASSET_REPLACE`, `SKIP_E2E`) or a non-canonical GitHub target. Exit 1. |
| `asset-replace-stable` | `--replace-assets` was given for a stable release. Exit 1. |
| `gate-missing`, `gate-failed`, `gate-commit-mismatch` | `release-session.js create --require-gate` found no passing quality gate for HEAD. Exit 1, no session written. |

## Self-test

The executable self-test creates temporary keys, manifests, and a temporary git repository.
It proves the signer, the named validation refusals, preflight, and session creation:

```powershell
node scripts/release-selftest.js
```

The release VM flow has its own self-test, with no network, no `gh`, and no build:

```powershell
node scripts/run-release-selftest.js
```

It checks the stable policy refusals, the exact step lists of `release:win` and `release:win:continue`,
the quality-gate proof, the trusted-key default, and the package.json entry points.

Result: `artifacts/release/selftest/result.json`. It covers wrong key, bad hash, size
mismatch, wrong channel, placeholder key, malformed JSON, wrong architecture, installer
architecture mismatch, expired manifest, downgrade, dirty tree, version mismatch, missing
artifact, wrong branch, and a successful preflight and session in a clean temporary repo.
It also covers `TAURI_SIGNING_PRIVATE_KEY` (base64 form, folder path, wrong, empty, and malformed
values, and a check that a failing run prints neither the key nor the password), the embedded-key
refusal for an environment key, and the default repository. No network and no `gh` call.

## Windows code signing (Azure Artifact Signing)

The installer's Windows files are Authenticode-signed with Azure Artifact Signing, the same way
Zinnia signs them. The build signs these files, and nothing else:

- The BWC binaries in the publish folder root: `BetterWindowsConfig.exe`, `BwcRegedit.exe`,
  `BwcUpdate.exe`, `BetterWindowsConfig.dll`, `BwcRegedit.dll`, `BwcUpdate.dll`, and `Bwc.*.dll`.
  Microsoft and third-party runtime files are not touched.
- The setup EXE (NSIS `!finalize`) and the uninstaller (NSIS `!uninstfinalize`, signed before it is
  embedded, so the setup signature covers it).

After signing, `scripts/verify-windows-authenticode.ps1` checks each of these files: a Valid
signature, the publisher name, the full subject, and an RFC 3161 timestamp. The failure modes are in
`docs/failure-modes/installer.md` ("Authenticode signing").

### One-time setup on the release VM

Run this once, in a PowerShell opened as Administrator, from the repository root:

```powershell
npm run setup:win:artifact-signing
```

It installs the Microsoft Artifact Signing Client Tools (SignTool and the Artifact Signing dlib) with
winget, or from Microsoft's MSI when winget fails. The MSI must carry a valid Microsoft signature.
The script refuses to run in a session that is not elevated, and it changes nothing when the tools are
already installed. If Windows asks for a restart, restart before the first signed build. The build
never runs this setup itself and never starts UAC.

### Variables

The variables have the same names as in Zinnia's `.env`. Copy that file. The `installer:x64` and
`installer:arm64` scripts load it with `node --env-file-if-exists=.env`, so `npm run installer:x64`
needs no extra setup. A direct run of `scripts/build-installer.js` reads the environment only, so load
`.env` first in that case.

| Variable | Use |
| --- | --- |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET` | The service principal that signs. The secret is never written to a file or printed. |
| `AZURE_ARTIFACT_SIGNING_ENDPOINT` | The regional endpoint of the signing account. |
| `AZURE_ARTIFACT_SIGNING_ACCOUNT`, `AZURE_ARTIFACT_SIGNING_PROFILE` | The code signing account and its certificate profile. |
| `AZURE_ARTIFACT_SIGNING_PUBLISHER` | The publisher simple name the signature must carry (checked after each signature). |
| `AZURE_ARTIFACT_SIGNING_PUBLISHER_DN` | The full certificate subject the signature must carry (checked after each signature). |
| `AZURE_SUBSCRIPTION_ID` | Accepted in the `.env`. Signing does not use it. |
| `SKIP_WIN_CODESIGN` | Only the exact value `1` skips signing and verification. Unset, `0`, or any other value enforces signing. Use `1` only for an unsigned test build, never for a release. |
| `AZURE_ARTIFACT_SIGNING_SIGNTOOL_PATH`, `AZURE_ARTIFACT_SIGNING_DLIB_PATH` | Optional. Use these paths instead of the installed client tools. A path that does not exist fails the build. |

A variable that is missing or blank stops the build before publish and before NSIS. The message lists
each missing name, not the values. The client tools are checked the same way, before any file is
signed.

### Read the publisher name and subject

Run this on a setup EXE signed with the release certificate to get the two values for the `.env`:

```powershell
$sig = Get-AuthenticodeSignature .\artifacts\installer\BetterWindowsConfig-<version>-win-x64-setup.exe
$sig.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
$sig.SignerCertificate.Subject
```

The first line is `AZURE_ARTIFACT_SIGNING_PUBLISHER`, and the second is `AZURE_ARTIFACT_SIGNING_PUBLISHER_DN`.

### Build and check

```powershell
npm run installer:x64        # signs, builds, signs the setup EXE, then verifies everything
node scripts/artifact-signing-selftest.js   # offline checks of the signing scripts (no Azure needed)
```

Verify one file by hand: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-windows-authenticode.ps1 -Files <path>`.

## What remains

- The real release key: the placeholder in `TrustedKeys.cs` must be replaced with the public key. Until
  then `npm run release:win` refuses before its first step. Nothing above can run a real release.
- The first `0.1.0` stable run on the release VM (runbook above), with a real `gh` login and the real key.
- `release:verify:published` (`scripts/validate-updater-live.js`) and `release:mirror`
  (`scripts/finalize-release-assets.js`) and `release:sign:gpg` (`scripts/gpg-sign.js`) are called by
  the flow but are not part of this change set. Each must exist before the flow reaches it.
- `release:finalize` mirrors `artifacts/release/<version>/stable`, because its npm script names that
  folder. A beta release on this VM would need its own finalize entry.
- The stable manifest URL in `src/Bwc.Hub/Updates/UpdateSources.cs` points at the GitHub latest-release
  asset `bwc-stable.json`. The beta URL is empty. Until the real key replaces the placeholder, the
  Hub reports "not configured" and never fetches the manifest.
- The real key must replace the placeholder in `TrustedKeys.cs`; until then every updater
  check fails closed.
