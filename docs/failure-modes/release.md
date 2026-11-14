# Release tooling failure modes

Scope: `scripts/sign-update-manifest.js`, `scripts/validate-updater-manifest.js`,
`scripts/assemble-bundle.js`, `scripts/release-dry-run.js`, `scripts/release-preflight.js`,
`scripts/release-session.js`,
`scripts/release-draft.js`, `scripts/release-verify.js`, `scripts/release-publish.js`,
`scripts/release-cleanup.js`, `scripts/run-release.js`, `scripts/release-selftest.js`, and
`scripts/lib/**`. Written before the code, per AGENTS.md.

The update verifier's own failure modes are in
[update-verifier.md](update-verifier.md). This file covers the release side: making a test
manifest, validating it, recording a release session, and creating or publishing a GitHub
draft. No release script calls `gh`, starts an elevated process, or opens a socket unless the
command carries `--apply`. `release:sign:gpg` (`scripts/gpg-sign.js`) is the only script that calls
`gpg`; its failure modes are in [release-assets.md](release-assets.md). A non-dry-run D5 publish invokes
`scripts/dotnet-build.js`, which uses the local SDK and NuGet cache. `--apply` still never
creates a release in the D4 to D7 work package; it is the seam the user runs on the release
VM.

## Exit codes

All release scripts use the same contract.

| Code | Meaning |
|------|---------|
| 0 | Pass, or a dry-run plan was printed and no refusal was found. |
| 1 | Refused by policy or validation. The output names at least one reason code. |
| 2 | Bad arguments or usage. |
| 3 | A required input, session, manifest, or artifact is missing. |
| 4 | An external command failed, but only when `--apply` or a build was requested. |

A dry run that finds a refusal prints the refusal reasons and the commands it would have
run, then exits 1. This keeps the plan visible without pretending the release is ready.

## A. Signer (`scripts/sign-update-manifest.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| S1 | Key output folder already has `minisign.key` | Overwrite a test key | Refuse `key-exists` unless `--force`. Exit 1. |
| S2 | Secret key text has a placeholder marker | A placeholder signs a release | Refuse `placeholder-key`. Exit 1. |
| S3 | Secret key packet is not 42 bytes or not `Ed` | Crash or accept a foreign key | Refuse `malformed-secret-key`. Exit 1. |
| S4 | Trusted comment has a line break or CR | Two comments in one signature | Refuse `trusted-comment-invalid`. Exit 1. |
| S5 | File to sign is missing | Empty signature | Refuse `signature-input-missing`. Exit 3. |
| S6 | Legacy `Ed` signing is requested | Legacy signs raw bytes, not BLAKE2b | Sign only with explicit `--legacy`; every release path refuses it later. |
| S7 | Key file is unreadable | Silent use of another key | Refuse `key-unreadable`. Exit 3. |
| S8 | Encrypted secret key (`EdSc` layout) is opened with a wrong password | A wrong password looks like a valid key | Refuse `wrong-password` when the BLAKE2b checksum of the decrypted key fails. Exit 1. Nothing is written. |
| S9 | Encrypted key uses another kdf or checksum, or its scrypt parameters are out of range | Wrong or unsafe derivation | Refuse `unsupported-key-kdf` or `malformed-secret-key`. Exit 1. |
| S10 | Password is given on the command line | It leaks into shell history and process lists | Refuse `password-argument` with exit 2. Only an environment variable name or an interactive prompt is accepted. |

## B. Manifest validator (`scripts/validate-updater-manifest.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| V1 | Manifest file missing | Accepted by accident | Refuse `manifest-missing`. Exit 3. |
| V2 | UTF-8 BOM, comments, trailing bytes, duplicate property, or nesting over 16 | Two readers disagree | Refuse `bom`, `invalid-json`, `duplicate-key`, `trailing-data`, or `too-deep`. Exit 1. |
| V3 | Manifest over 256 KiB | Memory abuse | Refuse `manifest-too-large`. Exit 1. |
| V4 | `schema` is not 1 | Unknown layout read as v1 | Refuse `unsupported-schema`. Exit 1. |
| V5 | Version is not strict SemVer | Wrong precedence | Refuse `version-invalid`. Exit 1. |
| V6 | Version differs from the requested release version | Wrong release signs | Refuse `version-mismatch`. Exit 1. |
| V7 | Channel is not `stable` or `beta`, or differs from the requested channel | Stable client takes beta | Refuse `channel-invalid` or `channel-mismatch`. Exit 1. |
| V8 | Stable channel carries a prerelease | Testers reach stable clients | Refuse `prerelease-on-stable`. Exit 1. |
| V9 | `pubDate` is not normalized UTC | Ambiguous date | Refuse `pubdate-invalid`. Exit 1. |
| V10 | `pubDate` is over 24 hours in the future | Forged future release | Refuse `pubdate-future`. Exit 1. |
| V11 | `pubDate` is older than `--max-age-hours` | Stale manifest reused | Refuse `expired`. Exit 1. |
| V12 | Offered version is older than `--current-version` or equal to it | Downgrade or reinstall loop | Refuse `downgrade` or `same-version`. Exit 1. |
| V13 | URL is not https, has user info, a fragment, a dot segment, encoded traversal, or bad characters | Downgrade or confusion | Refuse `url-rejected`. Exit 1. |
| V14 | A required architecture is absent | Wrong architecture installed | Refuse `missing-architecture`. Exit 1. |
| V15 | Installer entry is malformed: bad size, hash, or signature envelope | Garbage reaches the updater | Refuse `manifest-invalid` or `signature-format`. Exit 1. |
| V16 | Installer file size or SHA-256 does not match the manifest | Tampered or wrong file | Refuse `installer-size-mismatch` or `installer-hash-mismatch`. Exit 1. |
| V17 | Installer signature is for another architecture | Cross-architecture install | Refuse `wrong-arch`. Exit 1. |
| V18 | Installer signed comment version differs | Replay of another release | Refuse `installer-version-mismatch`. Exit 1. |
| V19 | Manifest signature missing | Unsigned release | Refuse `signature-missing`. Exit 1. |
| V20 | Manifest is signed by a key that is not listed | Unauthorized release | Refuse `wrong-key`. Exit 1. |
| V21 | Trusted key text carries a placeholder marker | Placeholder accepts updates | Refuse `placeholder-key`. Exit 1. |
| V22 | One trusted key is malformed while another is valid | Fail-open key list | Refuse `malformed-key` for the whole call. Exit 1. |
| V23 | Manifest byte or trusted comment changed after signing | Body and signed claim disagree | Refuse `signature-invalid` or `trusted-comment-invalid`. Exit 1. |
| V23a | `--trusted-key` is not byte-identical in key id and key bytes to a key embedded in `src/Bwc.Update/Signing/TrustedKeys.cs` | A key outside the shipped list is accepted by the release tooling | Refuse `embedded-key-mismatch`. Exit 1. The check parses the embedded list, not the comments. |
| V24 | Installer file missing for an entry inspected with `--installer` | Unverified bytes | Refuse `installer-missing`. Exit 3. |
| V25 | Manifest trusted comment has no `channel:` or no `expires:` field, or a field is malformed | Signed claim is incomplete, so freshness and channel cannot be checked | Refuse `trusted-comment-invalid`. Exit 1. |
| V26 | Signed `channel:` differs from the manifest `channel` | A stable manifest re-labelled as beta, or the reverse | Refuse `channel-mismatch`. Exit 1. |
| V27 | Signed `expires:` is in the past, by the checker's clock | Replay of an old, signed manifest | Refuse `expired`. Exit 1. |
| V28 | Trusted comment has extra fields, or an expiry that is not `yyyy-MM-ddTHH:mm:ssZ` | Two readers parse the signed claim differently | Refuse `trusted-comment-invalid`. Exit 1. The manifest format is exactly `version:<semver> channel:<stable or beta> expires:<utc>`. |
| V29 | Manifest signed for one channel, with the manifest `channel` field set to the other | Cross-channel reuse | Refuse `channel-mismatch`. Exit 1. |

## C. Dry-run harness (`scripts/release-dry-run.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| D1 | `--rid all` with no existing publish and `--dry-run` | Network or a long build during a dry run | No build, no network. Write synthetic dry-run installers and record `publish.present:false`. |
| D2 | Publish or installer record is stale | Manifest signs old bytes | Use the real setup EXE only when its record says `pass:true`, version and RID match, and the file exists. Otherwise use the dry-run artifact. |
| D3 | NSIS is not installed | DRY-RUN cannot run | Use a deterministic `.dry-run.json` installer stand-in. The real release still needs D1. |
| D4 | Version is not `X.Y.Z` or `X.Y.Z-beta.N` | Installer cannot map FileVersion | Refuse `version-invalid`. Exit 2. |
| D5 | Requested channel disagrees with the version | Beta version on stable | Refuse `channel-mismatch`. Exit 2. |
| D6 | Manifest validation fails after generation | Bad artifact recorded as pass | Write `validation-report.json` with `ok:false` and exit 1. |
| D7 | Ephemeral secret key is left in artifacts | Test key leak | Keep the secret only in the process temp folder and delete it in `finally`. Write only the public key. |
| D8 | Two runs with the same version | Old files mixed with new | Overwrite only the files this tool owns under `artifacts/release/<version>/<channel>`. |
| D9 | `--secret-key` is an encrypted key and the password is wrong or missing | Wrong key material, or a prompt that never comes | A wrong password refuses `wrong-password`, exit 1. No password source (no env var, no TTY) refuses `password-missing`, exit 2. |
| D10 | A real key is given but its public key is not in `src/Bwc.Update/Signing/TrustedKeys.cs` | A test or foreign key signs a release bundle | Refuse `embedded-key-mismatch`. Exit 1 in `assemble-bundle`. A dry run with a test key is labelled `testKey: true` by key id and bytes, never by comment text. |
| D11 | `assemble-bundle` output is not exactly what `BwcUpdate --check` and `--apply` read | Updater refuses a valid bundle, or accepts a bad one | The bundle is validated with the same validator the updater contract describes before the command reports PASS. Manifest trusted comment: exactly `version:<semver> channel:<stable or beta> expires:<yyyy-MM-ddTHH:mm:ssZ>`. Installer trusted comment: exactly `version:<semver> arch:<win-x64 or win-arm64>`. |
| D12 | Manifest for one channel is written into another channel's folder | Stable and beta bundles mixed | One manifest per channel, in `artifacts/release/<version>/<channel>/`. The channel is part of the path and of the signed comment. |

## D. Preflight and session (`scripts/release-preflight.js`, `scripts/release-session.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| P1 | Worktree has modified or untracked files | Release from an unrecorded tree | Refuse `dirty-tree` with paths. Exit 1. |
| P2 | Package version, `Directory.Build.props`, or requested version disagree | Artifact version lies | Refuse `version-mismatch`. Exit 1. |
| P3 | Changelog heading for the version is missing or empty | Blank release notes | Refuse `changelog-missing` or `changelog-empty`. Exit 1. |
| P4 | Beta is not built from `experimental`, or stable is not built from `main` | Release from the wrong branch | Refuse `branch-mismatch`. Exit 1. |
| P5 | Detached HEAD | Cannot prove the branch | Refuse `detached-head` unless `--allow-detached` is given and HEAD is contained in the expected branch. A remote `origin/<branch>` counts, so a detached checkout of a pushed commit passes. |
| P6 | Built-in release key is still the placeholder | Updates fail closed, or a release ships unusable | Refuse `placeholder-key`. Exit 1. |
| P7 | Session file missing, expired, or for another version, channel, or commit | Reuse an old proof | Refuse `missing-session`, `session-expired`, `session-mismatch`, or `session-commit-mismatch`. Exit 3 or 1. |
| P8 | `--artifact-dir` points at a release without a manifest, signature, or both architectures | Draft from incomplete artifacts | Refuse `missing-artifact`, `signature-missing`, or `missing-architecture`. Exit 1. |
| P9 | A manifest installer URL has a malformed percent escape (for example `%ZZ`) | `decodeURIComponent` throws a URIError and the preflight crashes | Refuse `asset-url-malformed`. Exit 1. No throw. |

The session is a local proof, not a signature. It records the version, channel, commit,
branch, package version, changelog heading, trusted key fingerprint, and start time. It
lives under `artifacts/release/session.json`, which is git-ignored.

## E. Draft, verify, and publish (`release-draft.js`, `release-verify.js`, `release-publish.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| G1 | No `--apply` | A dry run creates or publishes a release | Print the exact `gh api` commands and exit 0 or 1. No `gh` process is started. |
| G2 | No session for `--apply` | Draft from an unrecorded tree | Refuse `missing-session`. Exit 3. |
| G3 | Manifest has no signature | Unsigned release | Refuse `signature-missing`. Exit 1. |
| G4 | Trusted key is the repository placeholder, or is not one of the keys embedded in `TrustedKeys.cs` (a test key) | Placeholder or test key signs a real release | Refuse `placeholder-key` or `test-key`. Exit 1. The test-key decision is made from the embedded key list (key id and bytes), never from comment text an editor can change. `release-draft.js` needs `--allow-test-key` even for a dry run; verify and publish dry runs accept it with a warning, and every `--apply` path refuses it. |
| G5 | Installer is a `.dry-run.json` stand-in | A fake installer is published | Refuse `dry-run-artifact` in `--apply`, and in a dry-run plan print the refusal. |
| G6 | Draft exists for the tag | Duplicate drafts | Reuse and patch the one draft. Multiple drafts refuse `duplicate-drafts`. Exit 1. |
| G7 | Release is already published | A second draft or a re-publish | Draft refuses `already-published`. Publish is a no-op success only when tag, prerelease flag, and target commit match. |
| G8 | Draft targets another commit or has the wrong prerelease flag | Assets from another tree | Refuse `draft-target-mismatch` or `prerelease-mismatch`. Exit 1. |
| G9 | Asset with the same name but another size | Old asset mixed into a release | Refuse `asset-conflict`, unless `--replace-assets` is given. Exit 1. |
| G10 | Draft is missing a required asset | Incomplete release | Verify refuses `asset-missing`. Exit 1. |
| G11 | Downloaded asset size or hash differs from the manifest | Tampered upload | Verify refuses `asset-hash-mismatch` or `asset-size-mismatch`. Exit 1. |
| G12 | Publish is attempted with no draft | Publish creates a release | Refuse `draft-missing`. Exit 1. |
| G13 | `gh` is missing or exits non-zero under `--apply` | Silent partial release | Refuse `gh-missing` or `gh-failed`. Exit 4. |
| G14 | `--commit` does not resolve to HEAD, or HEAD is not the session commit | Release from a commit that was not verified | Refuse `session-commit-mismatch`. Exit 1. A draft never falls back to the literal target `HEAD`. |
| G15 | Repository name is not `owner/name` after `--repo`, `GH_REPO`, `GH_REPO_OWNER`, and `GH_REPO_NAME` are resolved | Release sent to a malformed or unintended target | Refuse `repo-invalid`. Exit 2. The default is `BurntToasters/better-windows-config` (see L1). The `origin` remote is never read. |
| G16 | Draft asset has the same name and size as the local file but a different SHA-256 | Size-only comparison accepts a tampered asset | Compare by the SHA-256 of the downloaded asset, never by size alone. A difference refuses `asset-conflict` unless `--replace-assets`. |
| G17 | GitHub reports no digest for a draft asset | Digest-less comparison is unverified | The tool downloads the asset and hashes it. The result is the same comparison as G16. |
| G18 | An asset id, name, or digest changes between verification and the publish PATCH | Race with another upload or edit | `release-publish --apply` re-reads the release immediately before the PATCH and refuses `asset-changed` when any asset id, name, or digest differs from the verified set. No PATCH is sent. Exit 1. |
| G19 | `--replace-assets` and a same-name draft asset exists | The existing asset is downloaded and hashed first, so a broken asset fails before the replace | With `--replace-assets`, the same-name asset is deleted and re-uploaded. No download of the old asset. |
| G20 | A draft asset has `state` other than `uploaded` (for example `new` or `starter`) | `gh` download fails with an unclear error | Treat as broken. With `--replace-assets`, delete and re-upload. Without it, refuse `asset-incomplete`. Exit 1. |

`release-draft.js --apply` creates or updates a draft only. `release-publish.js --apply`
requires a fresh verification of the downloaded draft assets before the PATCH that clears
`draft`.

## K. Signing key from the environment (`TAURI_SIGNING_PRIVATE_KEY`)

`scripts/sign-update-manifest.js sign` and `scripts/assemble-bundle.js` take the key from
`--key` or `--secret-key` (a file or folder path only), or else from `TAURI_SIGNING_PRIVATE_KEY`.
The variable holds one of: the base64 of a whole minisign secret key file (what
`tauri signer generate` prints), the raw minisign key file text, or a path. A password comes from
`--password-env <NAME>` when given. Otherwise an env key takes `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`
(an empty value is valid for an unencrypted key). A file key without `--password-env` prompts on a
terminal. The key and the password are never printed, logged, written unencrypted, or put on a
command line. A key given as text on the command line is refused.

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| K1 | No `--key`, no `--secret-key`, and `TAURI_SIGNING_PRIVATE_KEY` is unset or empty | Signing with no key, or with a stale default | Refuse `key-missing`. Exit 2. Nothing is written. |
| K2 | Env value is not minisign text, not a path, and not canonical base64 of minisign text | A typo or a truncated paste signs nothing useful, or is read as another format | Refuse `key-malformed`. Exit 1. The message names the variable, never its value. |
| K3 | Env value is larger than 16 KiB | Unbounded input | Refuse `key-malformed`. Exit 1. |
| K4 | Key text passed as a command-line argument (`--key` or `--secret-key` that is not an existing path) | The secret reaches shell history and the process list | Refuse `key-unreadable`. Exit 3. The value is not echoed. |
| K5 | `--key` or `--secret-key` names a path that is missing, or a folder with no `minisign.key` | Silent use of another key | Refuse `key-unreadable`. Exit 3. |
| K6 | Encrypted key with `TAURI_SIGNING_PRIVATE_KEY_PASSWORD` wrong | A wrong password looks like a valid key | Refuse `wrong-password`. Exit 1. Nothing is written. |
| K7 | Encrypted key with `TAURI_SIGNING_PRIVATE_KEY_PASSWORD` unset or empty | A blank password, or a prompt in an unattended run | Refuse `password-missing`. Exit 2. No prompt is shown for an env key. |
| K8 | Unencrypted key with an empty or unset password variable | Valid key refused for no reason | Signs. The password is not used for an unencrypted key. |
| K9 | Env key is not one of the keys embedded in `TrustedKeys.cs` (assemble, not dry run) | A foreign key signs a release bundle | Refuse `embedded-key-mismatch`. Exit 1. `sign` alone does not check trust; the bundle, preflight, draft, and publish gates do. |
| K10 | Failing run prints the key or the password | Secret in logs or CI output | Every refusal above omits the value. The self-test checks stdout and stderr for the key text and the password. |
| K11 | `--password-env` names a variable and `TAURI_SIGNING_PRIVATE_KEY` is also set | Ambiguous password source | The named variable wins. The Tauri password variable is read only for an env key with no `--password-env`. |
| K12 | Decoded key text is not UTF-8, or a CRLF pair appears in a raw or decoded key | Two readers disagree on the key bytes | Non-UTF-8 refuses `key-malformed`. CRLF is normalized to LF before parsing. A lone CR inside a line refuses `malformed-secret-key`. Exit 1. |
| K13 | Encrypted layout differs from what minisign writes | A real key cannot be read, or reads wrong (it happened: the first real Tauri key failed with `wrong-password` because keynum_sk was masked through XSalsa20 instead of the raw 104-byte scrypt output) | The keynum_sk mask is the 104-byte scrypt output XORed directly, as minisign C and the Rust crate Tauri uses do, with libsodium's integer pickparams. Check a real key with `npm run release:embed-key -- --from-env --check`: it must decrypt and report the embedded key id. |

## M. Embedding the release public key (`scripts/embed-update-key.js`)

`npm run release:embed-key -- --pub <file>` or `npm run release:embed-key -- --from-env` writes the
release public key into `src/Bwc.Update/Signing/TrustedKeys.cs` and sets `IsPlaceholder` to false.
`--pub` takes the `.pub` file `tauri signer generate` writes (the base64 of a minisign public key
file) or a raw minisign public key file. `--from-env` derives the public key from
`TAURI_SIGNING_PRIVATE_KEY` and `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`, so the embedded key matches the
signing key by construction. `--check` prints the result and writes nothing.

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| M1 | The `.pub` file is not minisign text or base64 of it, or the packet is malformed | A broken key compiles in, and every update fails | Refuse `malformed-key`. Exit 1. The file is unchanged. |
| M2 | The key is a BWC test key or the placeholder | A throwaway key becomes the trusted key | Refuse `test-key` or `placeholder-key`. Exit 1. |
| M3 | `--pub` is given and `TAURI_SIGNING_PRIVATE_KEY` is set to a different key | The embedded key does not match the key that signs, so every release is refused later, on the VM | Refuse `embedded-key-mismatch`, naming both key ids. Exit 1. When the env key cannot be read (no password), say so and continue with the `.pub` file. |
| M4 | `--from-env` with the key or password missing, wrong, or malformed | Wrong key derived, or the secret is printed | Refuse with the signer's reasons (`key-missing`, `key-malformed`, `wrong-password`, `password-missing`). The key and password are never printed. |
| M5 | `TrustedKeys.cs` already embeds a different real key | A rotation silently drops the old key, so installed copies cannot verify the new release | Refuse `key-already-embedded` unless `--rotate` (keep the old keys and add the new one) or `--replace` (only the new key) is given. |
| M6 | The same key is already embedded | Duplicate entries | Exit 0 with "already embedded". The file is unchanged. |
| M7 | The written file cannot be read back by `scripts/lib/trusted-keys.js` | The release tooling cannot see the key it must match | After writing, the file is parsed again and every embedded key must match; otherwise the old file is restored and the run fails. |
| M8 | The public key is a secret | None: a public key is public | The key text and fingerprint are printed so the user can check them. |

## L. Repository and hosting names

Release assets are named by channel. A stable manifest is `bwc-stable.json` with
`bwc-stable.json.minisig`. A beta manifest is `bwc-beta.json` with `bwc-beta.json.minisig`. The
installers keep their versioned names, so no two releases share an asset name. The
Hub reads the stable manifest from `https://github.com/BurntToasters/better-windows-config/releases/latest/download/bwc-stable.json`.
The beta URL is empty until a beta channel is published.

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| L1 | `--repo`, `GH_REPO`, `GH_REPO_OWNER`, and `GH_REPO_NAME` are all unset | Release sent to a fork's origin, or nowhere | Use `BurntToasters/better-windows-config`. `GH_REPO_OWNER` or `GH_REPO_NAME` alone overrides one part. Empty values count as unset. The `origin` remote is not read. |
| L2 | Resolved repository is not `owner/name` with safe characters | Malformed API path | Refuse `repo-invalid`. Exit 2. |
| L3 | `--download-base-url` is not https, or carries a query or fragment | Installer URLs point outside the release | Refuse `url-rejected` (manifest) or exit 2 (dry run). The default is `https://github.com/<owner>/<repo>/releases/download/v<version>`. |
| L4 | Stable manifest is published as the beta asset (or the reverse) | A stable manifest offered to beta users | The signed `channel:` and the validator decide (`channel-mismatch`, V26). The file name is not trusted. |
| L5 | Draft exists for `v<version>` | Draft assets become visible to the Hub | Drafts are invisible to `releases/latest/download`. The Hub sees a release only after publish, which is the only point at which `bwc-stable.json` changes. |
| L6 | Beta publish | A prerelease changes the stable manifest URL | `releases/latest` ignores prereleases, so the stable URL is unchanged by a beta. |
| L7 | Installer URL file name is not an asset of the release | The Hub downloads a 404 | Draft and verify refuse `asset-missing` (G10). The installer URL file name is the asset name. |
| L8 | Notes URL for a version | A broken notes link | `notesUrl` defaults to `https://github.com/<owner>/<repo>/releases/tag/v<version>`, and `--notes-url` overrides it. |

## F. Not enforced here (user duties)

| ID | Case | Note |
|----|------|------|
| U1 | Real minisign release key | The user creates it and replaces `TrustedKeys.cs`. The built-in key is a placeholder. The keygen in this tool makes unencrypted test keys only. |
| U2 | Hosting | Releases are GitHub release assets (section L). The user runs `gh auth login` on the release VM and keeps the repository owner and name in `.env` or the default. |
| U3 | Windows Authenticode signing | D8, and it needs a certificate. The scripts here sign update manifests and installers with minisign only. |
| U4 | NSIS compile | D1. The dry-run harness never needs it, and a real release does. |
| U5 | Release VM | D10. The user runs `--apply` there, with a real `gh` login and the real release key. |
| U6 | GnuPG | The user owns the GPG key (`GPG_KEY_ID`, `GPG_PASSPHRASE`). Only `scripts/gpg-sign.js` calls `gpg` (see release-assets.md). |

## N. Installer publish reuse (`scripts/build-installer.js`, `publishIsGood`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| N1 | The build record in `artifacts/build` names another version than `package.json` | A publish folder from an older version is reused in a new installer | Not reused. `dotnet-build.js` runs again. |
| N2 | The build record has a `commit` that differs from HEAD | A publish from another commit is reused | Not reused. `dotnet-build.js` records no commit today, so only the version is checked. Add the commit check when it is recorded. |
| N3 | The build record has `pass` other than true, or its `rid` or `flavor` differ | A failed or wrong-flavor publish is reused | Not reused (existing behavior). |

## Release VM flow

The Windows release VM runs one entry point, `npm run release:win` (or `npm run release:win:upload`
to resume after a failed upload). Then the user runs `npm run release:publish`,
`npm run release:verify:published`, and `npm run release:cleanup`. The automatic flow ends at the
mirror step and never runs git. Secrets come from `.env`, loaded with `node --env-file-if-exists=.env`.
The first release is stable `0.1.0`, so the stable policy applies. Written before the code, per AGENTS.md.

The policy refuses an override only for a stable version (`X.Y.Z`, no suffix). A beta version
keeps the beta recovery paths. `build-installer.js` never calls the policy, so a local dev build
with `SKIP_WIN_CODESIGN=1` still works.

### Stable policy (`scripts/release-policy.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| S1 | Stable version and `SKIP_WIN_CODESIGN` is truthy (`1`, `true`, `yes`, `on`, any case, surrounding spaces) | Unsigned stable installer | Refuse, naming the variable. Exit 1. |
| S2 | Stable version and `FORCE_UPLOAD`, `SKIP_RELEASE_MIRROR`, `ALLOW_ASSET_REPLACE`, or `SKIP_E2E` is truthy | Forced upload, skipped mirror, replaced asset, or skipped proof | Refuse, naming each variable. Exit 1. |
| S3 | Stable version and `GH_REPO` is not `BurntToasters/better-windows-config` | Release sent to a fork | Refuse, naming the value. Exit 1. |
| S4 | Stable version and `GH_REPO_OWNER` or `GH_REPO_NAME` is not `BurntToasters` or `better-windows-config` | Release sent to a fork | Refuse, naming the value. Exit 1. |
| S5 | Stable version and `RELEASE_DOWNLOAD_BASE_URL` is set and is not `https://github.com/BurntToasters/better-windows-config/releases/download/v<version>` (a trailing slash is allowed) | Installer URLs point elsewhere | Refuse, naming the value. Exit 1. |
| S6 | Stable version, clean environment, or the variables set to `0`, `false`, empty, or the canonical values | False refusal | Accept. Exit 0. |
| S7 | Beta version (`X.Y.Z-beta.N`) with any of S1 to S5 set | Beta recovery path blocked | Accept. The policy is stable only. |
| S8 | `scripts/release-policy.js` is run and the stable policy refuses | Caller ignores the result | Print the refusal to stderr and exit 1. A clean policy prints `ok` and exits 0. |
| S9 | `scripts/release-policy.js` gets an unknown argument | Silent ignore of a typo | Exit 2 with usage. |
| S10 | Existing exports (`signingPolicy`, `bwcSigningTargets`, and the others) | Build scripts break | Keep them unchanged. `signingPolicy({ SKIP_WIN_CODESIGN: "1" }).skip` stays `true`. |

### Run entry points (`scripts/run-release.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| R1 | Unknown flag (`--skip-tests`) or any positional argument | A typo runs a different gate | Exit 2 with `Unknown release flag` or usage. Nothing runs. |
| R2 | `src/Bwc.Update/Signing/TrustedKeys.cs` still carries the placeholder, has no embedded key, or cannot be read | The flow builds, then refuses at draft or publish | Refuse before the first step: `placeholder-key` (exit 1), `missing-trusted-key` (exit 1), or `missing-trusted-key-source` (exit 3). The message points at docs/RELEASE.md, "User-owned steps". No step runs. The environment is not read. |
| R13 | Operator copies Zinnia's `.env` unchanged | A BWC-only variable is missing, and the run refuses | Cannot happen. The release flow reads only variables Zinnia's `.env` has (`TAURI_SIGNING_*`, `AZURE_*`, `GPG_*`, `GH_REPO*`, `SKIP_*`, `FORCE_UPLOAD`, `ALLOW_ASSET_REPLACE`). The trusted set comes from TrustedKeys.cs. |
| R3 | `release:win` order | A skipped gate or a draft before proof | Exactly: `release:policy`, `release:preflight --require-trusted-key`, `test:all -- --quick --rid all`, `release:session:create -- --require-gate --force`, `release:clean`, `release:win:continue`. The `--force` replaces a session from an earlier attempt, so the proof matches this gate. |
| R4 | `release:win:continue` order | Signing after upload, or publish-side work before the signed bundle exists | Exactly: `release:policy`, `release:preflight --require-trusted-key`, `release:session:verify`, `build:win:x64`, `build:win:arm64`, `installer:x64`, `installer:arm64`, `release:assemble`, `release:sign:gpg`, `release:authenticode` once per setup EXE, `release:draft`, `release:verify:draft`, `release:mirror -- --dir artifacts/release/<version>/<channel>`. The flow ends at the mirror and never runs git. |
| R5 | A step fails | Later steps run on a broken state | The first failing step stops the run with its exit code. Later steps are not started. |
| R6 | Quality gate runs GUI E2E on the signing VM | The release VM cannot start the GUI session | The gate is `test:all --quick`. It runs no `test-e2e.js` step and records `e2e: "skipped"` in the release session. |
| R7 | `release:win:continue` runs alone (resume) | Continue without the policy, readiness, or session checks | It starts with `release:policy`, `release:preflight --require-trusted-key`, and `release:session:verify`. `release:win:resume` runs `prerelease:prepare` first, which is the same gate. |
| R8 | Clean step removes the session | The verify step has no proof | The clean step removes only `artifacts/release/<version>/`. `artifacts/release/session.json` and `artifacts/gate/` survive. |
| R9 | The automatic flow runs git commands | `git reset --hard` moves HEAD, so `release:publish` refuses the next run | No step runs `git reset`, `git clean`, or `git fetch`. The flow ends at `release:mirror`. Cleanup is `release:cleanup` (section "Resume and cleanup"). |
| R10 | Draft step passes `--replace-assets` for stable | Stable asset replaced | Never for stable. Non-stable passes it only with `ALLOW_ASSET_REPLACE=1`. |
| R11 | Stable version with `--replace-assets` given to `release-draft.js --apply` | Stable asset replaced by an explicit flag | Refuse `asset-replace-stable`. Exit 1. |
| R12 | `release:publish` runs without the verify step first | Publish of an unverified draft | `release-publish.js --apply` runs `release-verify.js --apply` before the PATCH (existing behavior). Any verify failure stops the publish. |
| R14 | A step is given a credential it does not read | A child process holds a secret it can leak or misuse | Each step gets only the variables in the `STEP_ENV` table in `scripts/run-release.js` (exported, self-tested). Every other credential is set to an empty string in that step's environment. Empty counts as unset, so a `.env` loaded by the step's npm script does not refill it. |
| R15 | A step's npm script loads `.env` after the runner filtered the environment | The filter is bypassed by `--env-file-if-exists` | Node does not overwrite a variable that is already defined, even as an empty string. The run-selftest proves this with a temporary env file. |
| R16 | `release:win` runs `release:win:continue` as a step | The nested run has no credentials to sign with | `release:win` and `release:win:continue` are runners. They pass the full environment, and the nested run filters each of its own steps. |

### Signing readiness (`scripts/release-preflight.js --require-trusted-key`)

Runs before any build, so a missing credential fails before the paid signing. Local checks run
first and all of them report. The network checks (`gh`) run only when every local check passes.
The GPG check signs a probe file in a temporary folder, not a release asset. Each reason's exit
code is in the JSON report (`--json`). The CLI exits 3 when any refusal is a missing input, and 1
otherwise.

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| RP1 | `TAURI_SIGNING_PRIVATE_KEY` unset or blank | Signing fails after the build | Refuse `key-missing`. Exit 3. |
| RP2 | Update key is encrypted and `TAURI_SIGNING_PRIVATE_KEY_PASSWORD` is unset | A prompt would hang the VM | Refuse `password-missing`. No prompt. Exit 1. |
| RP3 | Update key does not decrypt or is malformed | Manifest signing fails later | Refuse with the parser reason (for example `malformed-secret-key`). The key text is never printed. Exit 1. |
| RP4 | Public half of the update key is not one of the keys in `TrustedKeys.cs` | Signed bundle refused by the draft check | Refuse `embedded-key-mismatch`. Exit 1. |
| RP5 | `GPG_KEY_ID` unset or not 8, 16, or 40 hex digits | GPG signing fails after the build | Refuse `key-id-missing` or `key-id-invalid`. Exit 2. |
| RP6 | `GPG_PASSPHRASE` unset | GPG cannot sign | Refuse `passphrase-missing`. Exit 2. |
| RP7 | `gpg` not on PATH | GPG cannot sign | Refuse `gpg-missing`. Exit 4. |
| RP8 | No secret key matches `GPG_KEY_ID`, or the primary fingerprint differs | Signs with the wrong key | Refuse `key-not-found` (exit 3), or `key-mismatch` or `key-ambiguous` (exit 1). |
| RP9 | Probe signature fails, or the passphrase is wrong | Signing fails after the build | Refuse `sign-failed` (exit 4), or `verify-failed` (exit 1). The passphrase is redacted from the output. |
| RP10 | `AFTER_PACK_LOC` unset for stable, or for beta with `OVERRIDE_BETA_MIRROR_SKIP=1` | Mirror step fails after signing | Refuse `after-pack-loc-missing`. Exit 1. |
| RP11 | `AFTER_PACK_LOC` on an unmapped drive, or the folder does not exist | The check or the mirror crashes on the drive letter | Refuse `after-pack-loc-unavailable` with the path. Never throws. Exit 1. |
| RP12 | `AFTER_PACK_LOC` is not writable (probe file cannot be created) | Mirror fails after signing | Refuse `after-pack-loc-unwritable`. Exit 1. |
| RP13 | `AFTER_PACK_LOC` is relative, a drive root, a symbolic link, or inside the repository | Mirror refuses later | Refuse `after-pack-loc-invalid`. Exit 1. |
| RP14 | Azure Artifact Signing variables missing while `SKIP_WIN_CODESIGN` is not exactly `1` | Authenticode fails after the build | Refuse `signing-env-missing`, naming the variables (names only). Exit 1. |
| RP15 | `makensis.exe` not found (PATH, `NSIS_HOME`, or the NSIS folders) | Installer build fails after the first build | Refuse `makensis-missing`. Exit 3. |
| RP16 | SignTool or the Artifact Signing dlib cannot be resolved (`windows-artifact-sign.ps1 -CheckOnly` fails) | Authenticode fails after signing starts | Refuse `signing-tools-missing`. Exit 3. |
| RP17 | Node does not satisfy `package.json` `engines.node` | Build tools misbehave | Refuse `node-version`. Exit 1. |
| RP18 | HEAD is not an ancestor of `origin/<expected branch>` (`main` for stable, `experimental` for beta) | Release built from unpushed commits | Refuse `head-not-pushed`. Exit 1. Nothing is fetched; run `git fetch origin` first. |
| RP19 | `origin/<expected branch>` does not exist locally | The check cannot run | Refuse `remote-branch-missing`. Exit 1. |
| RP20 | `gh` not on PATH | Draft and publish fail after the signing | Refuse `gh-missing`. Exit 4. |
| RP21 | `gh auth status` fails (run with `ghEnvironment()`, so token variables are ignored) | Draft upload fails after the signing | Refuse `gh-auth-missing`. Exit 4. |
| RP22 | A published (not draft) release already exists for `v<version>` | Second release for one tag | Refuse `release-published`. Exit 1. |
| RP23 | `<version>` is not greater than the latest published non-draft release | Update feed moves backward | Refuse `version-not-newer`. Exit 1. |
| RP24 | A network check (RP20 to RP23) runs after a local check failed | Wasted gh calls | Not run. Only local findings are reported in that case. |

### Resume and cleanup (`scripts/run-release.js --from`, `scripts/release-cleanup.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| F1 | `--from <step>` names a step that is not in the continue list | Typo runs a different tail | Exit 2. Lists the valid names. Nothing runs. |
| F2 | `--from` without `--continue` | Ambiguous mode | Exit 2. Nothing runs. |
| F3 | `--from <step>` names a build step | Earlier build steps are skipped | The gate (`release:policy`, `release:preflight --require-trusted-key`, `release:session:verify`) always runs first. The steps before `<step>` are skipped. |
| F4 | `--from <step>` names a gate step | A gate is skipped | The list runs from that gate step, so `release:policy` or `release:preflight` can be run alone. |
| F5 | `release:win:upload` runs | Upload resumes without the gate | It is `--continue --from release:draft`. The gate runs first (F3). Draft reuses the existing draft for the same commit. |
| F6 | Resume after the draft exists, with an Authenticode re-run | Re-signed bytes differ from the draft and the draft check fails | Do not restart at `release:authenticode` after a draft exists. Restart at `release:draft`. A stable draft is never replaced (R10, R11). |
| F7 | `--from release:draft` after the release folder was cleaned | Upload of missing or unsigned files | `release-draft.js` refuses through the bundle check (`missing-artifact`, `signature-missing`). Run `release:win` again. |
| F8 | Session older than 24 hours on a resume | Stale gate proof | `session-expired` (P7). Run `release:win` again to record a new gate. |
| F9 | `release:publish` rerun after a successful publish, with the assets matching | Second publish refused, so the operator thinks it failed | Exit 0 with a message that ends `no-op`. The published release's target and prerelease must match HEAD, and its assets are verified against the local files (`release-verify.js --allow-published`). `publish-result.json` is rewritten with `noop: true`. |
| F10 | `release:publish` rerun after a publish, with an asset that differs | Published bytes differ from the local files | Refuse `asset-hash-mismatch` or `asset-size-mismatch`. Exit 1. |
| C1 | `release:cleanup` with a dirty working tree (tracked or untracked, outside git-ignored paths) | Uncommitted work is discarded | Refuse `dirty-tree` with the paths. Exit 1. Nothing is reset. |
| C2 | HEAD is not an ancestor of the upstream after `git fetch` | Unpushed commits are discarded | Refuse `head-not-upstream`. Exit 1. Nothing is reset. |
| C3 | No upstream is configured for the current branch | Target of the reset is unknown | Refuse `no-upstream`. Exit 1. |
| C4 | `publish-result.json` for this version and channel is missing or not `published: true` | Cleanup before the release is published | Refuse `not-published`. Exit 1. Nothing is reset. |
| C5 | `git fetch origin` fails | Reset to a stale upstream | Refuse `fetch-failed`. Exit 4. Nothing is reset. |
| C6 | `release:cleanup` succeeds | Lost git-ignored artifacts | Only `git reset --hard <upstream>` and `git clean -fd` run. Git-ignored `artifacts/` survives. The version comes from `package.json`, and the upstream is resolved by git. |
| C7 | `release:cleanup` is wired into an npm script with `%npm_package_version%` or `@{u}` | Shell-specific expansion breaks on Windows | Not allowed. The script is `node scripts/release-cleanup.js`. |

### Quality gate proof and release session (`scripts/lib/release-policy.js`, `scripts/release-session.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| Q1 | `artifacts/gate/result.json` is missing, or has no pass for HEAD | Release with no gate | Refuse `gate-missing`. Exit 1. No session is written. `--require-gate` is the default for `create`; passing it is accepted and changes nothing. |
| Q2 | Gate result has `pass` other than `true` | A failing gate proves nothing | Refuse `gate-failed`. Exit 1. |
| Q3 | Gate `git.commit` differs from HEAD | Gate proof for another commit | Refuse `gate-commit-mismatch`. Exit 1. |
| Q4 | Quick gate passes for HEAD | E2E silently counted as run | Session records `qualityGate.mode: "quick"` and `qualityGate.e2e: "skipped"`. |
| Q5 | Full gate passes for HEAD | E2E skipped or unrecorded | Session records `qualityGate.e2e: "passed"`. |
| Q6 | Gate result is unreadable JSON | Garbage accepted | Treat as missing (Q1). |
| Q7 | Preflight, session, draft, verify, or publish runs without `--trusted-key` | Trusted set is an unexpected key | The trusted set is the non-placeholder keys embedded in `TrustedKeys.cs`. Publish passes the verify child only an explicit `--trusted-key`; the child reads the embedded keys itself. |
| Q8 | Gate result has `git.dirty: true` (the gate ran on uncommitted files) | The pass does not describe HEAD | Refuse `gate-dirty-tree`. Exit 1. No session is written. |
| Q9 | Session file has no `qualityGate`, or it is null | A session from an older tool or an edited file skips the gate | `release-session.js verify` refuses `missing-quality-gate`, and so do the draft, verify, and publish checks (`verifySessionFile`). Exit 1. |
| Q10 | `release-session.js create` runs with no passing gate for HEAD | A session with no gate proof is written | Refuse `gate-missing`, since `--require-gate` is the default. Exit 1. No session is written. |

### Package scripts (`package.json`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| N1 | A required script is missing | A documented step does not run | `release:win`, `release:win:continue`, `release:win:resume`, `release:win:upload`, `release:cleanup`, `release:mirror`, `release:sign:gpg`, `release:verify:draft`, `release:verify:published`, and `release:publish` exist. `release:finalize` is removed, because it ran `git reset`. |
| N2 | `release:sign:gpg`, `release:mirror`, or `release:verify:published` differs from the given command | Wrong script runs | Exact strings from the release brief. The first two take `--dir` through `npm run ... -- <args>`. |
| N3 | An existing script name is removed | Old runbook breaks | `release:session`, `release:draft`, `release:verify`, `release:preflight`, `release:assemble`, `release:sign`, `release:validate`, `release:dry-run`, `release:selftest`, `build:win:x64`, `build:win:arm64`, `installer:x64`, `installer:arm64`, `setup:win:artifact-signing`, `signing:selftest`, `test:all`, and `validate` stay. |
| N4 | A script that needs secrets runs without `.env` | Missing `TAURI_SIGNING_PRIVATE_KEY` or Azure variables | Each such script loads `.env` with `node --env-file-if-exists=.env`. The PowerShell Authenticode step inherits the environment of the run that loaded `.env`. |

### Not enforced by these scripts (user duties)

| ID | Case | Note |
|----|------|------|
| V1 | Built-in trusted key is still the placeholder | Preflight refuses `placeholder-key` (P6). The user adds the real public key to `TrustedKeys.cs` first. |
| V2 | An explicit `--trusted-key` names a `.pub` file that is not embedded in `TrustedKeys.cs` | A foreign key becomes trusted | Refuse `embedded-key-mismatch` in preflight, draft, verify, and publish. The check compares key id and bytes with the embedded list. |
| V3 | `release:cleanup` on a checkout with local work | Discards uncommitted or unpushed work. `release:cleanup` refuses both (C1, C2). Run it only after `release:publish`. |
