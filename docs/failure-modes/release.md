# Release tooling failure modes

Scope: `scripts/sign-update-manifest.js`, `scripts/validate-updater-manifest.js`,
`scripts/assemble-bundle.js`, `scripts/release-dry-run.js`, `scripts/release-preflight.js`,
`scripts/release-session.js`,
`scripts/release-draft.js`, `scripts/release-verify.js`, `scripts/release-publish.js`,
`scripts/release-selftest.js`, and `scripts/lib/**`. Written before the code, per AGENTS.md.

The update verifier's own failure modes are in
[update-verifier.md](update-verifier.md). This file covers the release side: making a test
manifest, validating it, recording a release session, and creating or publishing a GitHub
draft. No release script calls `gh`, calls `gpg`, starts an elevated process, or opens a
socket unless the command carries `--apply`. A non-dry-run D5 publish invokes
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
| P5 | Detached HEAD | Cannot prove the branch | Refuse `detached-head` unless `--allow-detached` is given and HEAD is contained in the expected branch. |
| P6 | Built-in release key is still the placeholder | Updates fail closed, or a release ships unusable | Refuse `placeholder-key`. Exit 1. |
| P7 | Session file missing, expired, or for another version, channel, or commit | Reuse an old proof | Refuse `missing-session`, `session-expired`, `session-mismatch`, or `session-commit-mismatch`. Exit 3 or 1. |
| P8 | `--artifact-dir` points at a release without a manifest, signature, or both architectures | Draft from incomplete artifacts | Refuse `missing-artifact`, `signature-missing`, or `missing-architecture`. Exit 1. |

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
| G15 | No `--repo`, no `GH_REPO`, and no `origin` remote with a GitHub URL | Release sent to a hard-coded repository | Refuse `repo-invalid`. Exit 2. The tooling never assumes an owner or name. |
| G16 | Draft asset has the same name and size as the local file but a different SHA-256 | Size-only comparison accepts a tampered asset | Compare by the SHA-256 of the downloaded asset, never by size alone. A difference refuses `asset-conflict` unless `--replace-assets`. |
| G17 | GitHub reports no digest for a draft asset | Digest-less comparison is unverified | The tool downloads the asset and hashes it. The result is the same comparison as G16. |
| G18 | An asset id, name, or digest changes between verification and the publish PATCH | Race with another upload or edit | `release-publish --apply` re-reads the release immediately before the PATCH and refuses `asset-changed` when any asset id, name, or digest differs from the verified set. No PATCH is sent. Exit 1. |

`release-draft.js --apply` creates or updates a draft only. `release-publish.js --apply`
requires a fresh verification of the downloaded draft assets before the PATCH that clears
`draft`.

## F. Not enforced here (user duties)

| ID | Case | Note |
|----|------|------|
| U1 | Real minisign release key | The user creates it and replaces `TrustedKeys.cs`. The built-in key is a placeholder. The keygen in this tool makes unencrypted test keys only. |
| U2 | Hosting base URL | The dry-run harness defaults to `https://example.invalid/bwc/<version>`. The user chooses the real https origin and path. |
| U3 | Windows Authenticode signing | D8, and it needs a certificate. The scripts here sign update manifests and installers with minisign only. |
| U4 | NSIS compile | D1. The dry-run harness never needs it, and a real release does. |
| U5 | Release VM | D10. The user runs `--apply` there, with a real `gh` login and the real release key. |
| U6 | GnuPG | The user owns the release key and the signing policy. These scripts never call `gpg`. |
