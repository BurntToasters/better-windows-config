# Release

This document covers the release tooling from D4 to D7: the minisign signer, the manifest
validator, the offline dry run, the release session and preflight, and the GitHub draft,
verify, and publish commands. It does not cover NSIS (D1), Authenticode or GnuPG signing (D8),
or the first beta run on a release VM (D10).

The update format is fixed by [contracts/update-manifest.md](contracts/update-manifest.md).
The release failure cases are in [failure-modes/release.md](failure-modes/release.md).

## Status

- D4 to D7 are implemented as scripts. Nothing is published, and no release exists.
- The built-in update key is still the placeholder in
  `src/Bwc.Update/Signing/TrustedKeys.cs`. Every `--apply` path refuses it.
- The dry-run harness uses an ephemeral test key and a `.dry-run.json` installer stand-in
  when NSIS has not produced a real setup EXE. `--apply` refuses both.
- NSIS 3.10 or newer and GnuPG 2.4 or newer are not installed. The scripts never call
  `makensis` or `gpg`.

## Prerequisites

| Tool | Why |
|------|-----|
| Node 22.22.2, 24.15, or 26+ | All scripts. |
| .NET SDK 10.0.403 or newer | Only a non-dry-run publish uses it. |
| NSIS 3.10+ | A real setup EXE. The dry run does not need it. |
| GnuPG 2.4+ | The user's release signing policy (D8). These scripts do not call it. |
| GitHub CLI, authenticated | Only `--apply` for draft, verify, and publish. Dry runs never start `gh`. |
| A real minisign release key | Created and protected by the user. |

Refresh PATH in PowerShell before a build or release command:

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
```

## User-owned steps

These are not implemented by D4 to D7.

1. Create the real minisign release key. Keep the secret key off the repository and off the
   release VM image history, and encrypt it with a password you keep in a password manager
   (`minisign -G`). The keygen in `scripts/sign-update-manifest.js` writes test keys only; never use
   one for a release. Check the encrypted-key path once with a key that minisign made (see 2a).
2. Put the real public key in `src/Bwc.Update/Signing/TrustedKeys.cs`, in the `Embedded` list, and
   set `IsPlaceholder = false`. The release tooling trusts exactly the keys in that list, matched by
   key id and key bytes. Rotation lists the old and new key during the overlap.
3. Choose the https hosting origin and path for the manifest, signature, notes page, and
   installers. The dry run defaults to `https://example.invalid/bwc/<version>`. Replace it
   with `--download-base-url`.
4. Install NSIS 3.10+ and compile `installer/bwc.nsi` (D1). Until then, a dry-run artifact
   contains a stand-in, not an installer.
5. Sign the setup EXE with the Windows code-signing certificate (D8). SmartScreen warns on
   an unsigned download.
6. Run the first beta (`0.1.0-beta.1`) on the release VM (D10). That is the first run that
   may use `--apply`.

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
  --manifest artifacts/release/0.1.0-beta.1/beta/manifest.json `
  --signature artifacts/release/0.1.0-beta.1/beta/manifest.json.minisig `
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
| `--download-base-url <url>` | Default `https://example.invalid/bwc`. Must be https. |
| `--installer-<rid> <path>` | Use an explicit installer file for one RID. |
| `--current-version <semver>` | Also check the generated version is newer. |
| `--max-age-hours <n>` | Freshness limit used by the validation report. Default 24. |

Output is `artifacts/release/<version>/<channel>/` (one manifest per channel). The version
folder also holds `publish-<rid>.json`:

| File | Contents |
|------|----------|
| `manifest.json` | Schema 1 manifest for the chosen version and channel. |
| `manifest.json.minisig` | Raw minisign signature. Its trusted comment is `version:<semver> channel:<channel> expires:<utc>`. |
| `manifest.json.sig` | Base64 envelope of the same signature. |
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
must be named with `--repo`, `GH_REPO`, or an origin remote; the tooling never assumes one
(`repo-invalid`). `--root <dir>` selects another checkout on the same commands.

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
`<owner>/<repo>` with the repository resolved from `--repo`, `GH_REPO`, or the origin
remote.

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

## Self-test

The executable self-test creates temporary keys, manifests, and a temporary git repository.
It proves the signer, the named validation refusals, preflight, and session creation:

```powershell
node scripts/release-selftest.js
```

Result: `artifacts/release/selftest/result.json`. It covers wrong key, bad hash, size
mismatch, wrong channel, placeholder key, malformed JSON, wrong architecture, installer
architecture mismatch, expired manifest, downgrade, dirty tree, version mismatch, missing
artifact, wrong branch, and a successful preflight and session in a clean temporary repo.
No network and no `gh` call.

## What remains

- D1: install NSIS 3.10+ and compile `installer/bwc.nsi`. A real release must use the setup
  EXE, not the `.dry-run.json` stand-in.
- D8: the real release key, Authenticode signing, and the user's GnuPG policy.
- D10: the first `0.1.0-beta.1` run on the release VM, including the real hosting URL and a
  real `gh` login.
- The update server URLs in `src/Bwc.Hub/Updates/UpdateSources.cs` are still empty.
- The real key must replace the placeholder in `TrustedKeys.cs`; until then every updater
  check fails closed.
