# Release asset failure modes

Scope: `scripts/gpg-sign.js`, `scripts/finalize-release-assets.js`,
`scripts/validate-updater-live.js`, `scripts/release-assets-selftest.js`, and the helpers they
share in `scripts/lib/**`. Written before the code, per AGENTS.md.

These scripts sit after `scripts/assemble-bundle.js` and before `scripts/release-draft.js`.
They never upload, never call `gh`, and never open a socket except `validate-updater-live.js`,
which reads the public GitHub release over HTTPS. The self-test uses only 127.0.0.1.

Signature file name: `<asset>.asc` (GnuPG ASCII-armored detached signature), the same name
Zinnia uses. It does not collide with BWC's own sidecars: `<asset>.minisig` (minisign text) and
`<asset>.sig` (base64 minisign envelope). No BWC script writes a `.asc` file.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Pass, or a skip that the rules allow (beta mirror without the override). |
| 1 | Refused by policy or validation. The output names at least one reason code. |
| 2 | Bad arguments, bad environment value, or usage. |
| 3 | A required input is missing: release folder, key in the keyring, manifest file. |
| 4 | An external command or network step failed (gpg, file copy, HTTP transport). |

## A. GPG signer (`scripts/gpg-sign.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| G1 | `gpg` is not on PATH | Silent unsigned release | Refuse `gpg-missing` with the install hint. Exit 4. |
| G2 | `GPG_KEY_ID` is unset or blank | Default key signs | Refuse `key-id-missing`. Exit 2. |
| G3 | `GPG_KEY_ID` is not 8, 16, or 40 hex digits (after removing spaces) | Email or user ID picks a different key | Refuse `key-id-invalid`. Exit 2. |
| G4 | `GPG_PASSPHRASE` is unset or empty | gpg prompts, or an unprotected key signs | Refuse `passphrase-missing`. Exit 2. |
| G5 | No secret key matches `GPG_KEY_ID` in the keyring | Wrong or no key | Refuse `key-not-found`. Exit 3. |
| G6 | More than one secret key matches | Ambiguous signer | Refuse `key-ambiguous`. Exit 1. |
| G7 | The matched primary fingerprint does not match `GPG_KEY_ID` (40-hex: equal; 8 or 16-hex: suffix) | Signer is another key | Refuse `key-mismatch`. Exit 1. Nothing is signed. |
| G8 | Passphrase appears in argv | Process lists and shell history leak it | Never happens: passphrase goes only to stdin (`--passphrase-fd 0`, loopback pinentry). Self-test asserts it in no argv. |
| G9 | gpg stderr or an error message echoes the passphrase | Log leak | Every message is scrubbed of the passphrase text before printing. |
| G10 | `GPG_PASSPHRASE` is inherited by the gpg child environment | Env dump leaks it | The child environment omits `GPG_PASSPHRASE`. |
| G11 | gpg exits non-zero | Partial release | Refuse `sign-failed`. Exit 4. The temporary signature is deleted; an older `.asc` is untouched. |
| G12 | gpg exits 0 but writes no signature | Empty signature accepted | Refuse `sign-failed`. Exit 4. |
| G13 | `gpg --verify` of the new signature is not GOODSIG and VALIDSIG | Bad signature published | Refuse `verify-failed`. Exit 1. The temporary signature is deleted; the final name is not created. |
| G14 | VALIDSIG primary fingerprint differs from the keyring key | Signature by another key | Refuse `signer-mismatch`. Exit 1. |
| G15 | Release folder is missing or not a directory | Signs nothing, reports success | Refuse `release-dir-missing`. Exit 3. |
| G16 | Release folder has no signable regular file | Empty pass | Refuse `nothing-to-sign`. Exit 3. |
| G17 | A release entry is a symlink or junction | Signs or reads a file outside the folder | Refuse `symlink-refused`. Exit 1. |
| G18 | A release entry is a subdirectory | Nested asset invisible to the draft | Refuse `unexpected-directory`. Exit 1. |
| G19 | An existing `<asset>.asc` from an earlier run | Stale signature for a changed file | Re-signed in place (`--yes`); verification runs on the new file. Files ending in `.asc` are never signed. |
| G20 | Signature written straight to its final name, then verification fails | Bad `.asc` left in place | Not done: the signature is written to a hidden temporary sibling, verified, then renamed. |
| G21 | Signing a file that the caller meant to replace concurrently | Race | Residual: the script assumes the folder is not being written during signing. |
| G22 | Network use | Unexpected upload or key fetch | None. The script spawns only `gpg`. |

## B. Mirror (`scripts/finalize-release-assets.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| M1 | Stable version and `AFTER_PACK_LOC` unset or blank | Release with no off-repo copy | Refuse `after-pack-loc-missing`. Exit 1. |
| M2 | Beta version (`X.Y.Z-beta.N`) and `OVERRIDE_BETA_MIRROR_SKIP` is not exactly `1` | Beta copied when not wanted | Print `skipped-beta`. Exit 0. Nothing is written. |
| M3 | Beta version and `OVERRIDE_BETA_MIRROR_SKIP=1` | Beta never mirrored on demand | Mirror proceeds like stable. |
| M4 | `AFTER_PACK_LOC` is relative (`Z:ZINNIA`, `out/mirror`) | Copies into the working directory | Refuse `destination-relative`. Exit 2. |
| M5 | Destination is inside the repository, or equal to it | Mirror pollutes or is cleaned by git | Refuse `destination-in-repo`. Exit 1. |
| M6 | Destination path is a symlink or junction | Writes to an unexpected target | Refuse `destination-symlink`. Exit 1. |
| M7 | Destination equals or is inside the release folder | Copy onto itself, or into the source | Refuse `destination-in-release`. Exit 1. |
| M8 | The release folder is inside `<destination>/<version>` (destination is an ancestor) | Copy writes into the source tree | Refuse `destination-overlaps-release`. Exit 1. |
| M9 | Destination is a file, not a folder | Overwrites a file | Refuse `destination-not-directory`. Exit 1. |
| M10 | Destination is a drive or filesystem root | Writes at a root | Refuse `destination-root`. Exit 1. |
| M11 | Release folder missing | Mirror of nothing | Refuse `release-dir-missing`. Exit 3. |
| M12 | Release folder has no regular file | Empty mirror reported as ok | Refuse `release-dir-empty`. Exit 3. |
| M13 | Release folder entry is a symlink or junction | Copies a file outside the folder | Refuse `symlink-refused`. Exit 1. |
| M14 | Version is not strict SemVer | Wrong folder name | Refuse `version-invalid`. Exit 2. |
| M15 | Copied file size or SHA-256 differs from the source | Corrupt or truncated mirror | Refuse `verify-failed`. Exit 4. The staged copy is deleted; the previous destination file is restored. |
| M16 | Destination already has a same-name file for this version | Silent overwrite of a different build | Replaced only after the new copy verifies. The old file is removed only after the swap. |
| M17 | Destination has other files, or other versions | Deleting history | Never deleted. Only same-name files for this version are replaced. |
| M18 | Copy fails midway | Half a mirror | Refuse `copy-failed`. Exit 4. Files already verified stay; a rerun repairs the rest. |
| M19 | SMB or mapped drive rejects permission bits | Copy fails on Windows Z: | Fall back to a byte write, as Zinnia does. Verification still applies. |
| M20 | A previous run left hidden `.<name>.mirror-*` siblings | Clutter | Residual: not deleted, because the script does not delete unknown files. |

## C. Live validator (`scripts/validate-updater-live.js`)

| ID | Case | Risk | Required result |
|----|------|------|-----------------|
| L1 | `bwc-stable.json` or its `.minisig` returns a non-200 status | Hub reads nothing | Refuse `http-status`. Exit 1. |
| L2 | Transport error or timeout | Unknown state reported as pass | Refuse `fetch-failed`. Exit 4. |
| L3 | Manifest is not strict JSON or breaks the schema | Hub parses differently | Refuse with the validator's reason (`invalid-json`, `manifest-invalid`, and so on). Exit 1. |
| L4 | Manifest is not signed by an embedded key | Forged manifest | Refuse `embedded-key-mismatch`. Exit 1. |
| L5 | Manifest bytes changed after signing | Tampered manifest | Refuse `manifest-signature`. Exit 1. |
| L6 | `TrustedKeys.cs` still has the placeholder, or is unreadable | Check passes with no real key | Refuse `placeholder-key` or `trusted-keys-unreadable`. Exit 1. The real repository fails until the release key lands. |
| L7 | Manifest version differs from `--expected-version` | Old or wrong release is live | Refuse `version-mismatch`. Exit 1. |
| L8 | `--expected-version` is a prerelease, or the current package version is one | Beta checked against stable latest | Refuse usage. Exit 2. The check covers only the stable channel. |
| L9 | Manifest channel is not `stable` | Wrong feed | Refuse `channel-mismatch`. Exit 1. |
| L10 | Signed expiry has passed | Stale signature served | Refuse `expired`. Exit 1. |
| L11 | Installer URL is not https (loopback test base excepted) | Downgrade or confusion | Refuse `url-rejected`. Exit 1. |
| L12 | Installer URL returns a non-200 status | Hub cannot download | Refuse `http-status`. Exit 1. |
| L13 | Downloaded installer size or SHA-256 differs from the manifest | Hub installs different bytes | Refuse `installer-size-mismatch` or `installer-hash-mismatch`. Exit 1. |
| L14 | Installer signature invalid, or signed for another version or architecture | Wrong binary offered | Refuse `installer-signature-invalid`, `installer-version-mismatch`, or `wrong-arch`. Exit 1. |
| L15 | A required architecture is missing from the manifest | One architecture cannot update | Refuse `missing-architecture`. Exit 1. Default required: `win-x64`, `win-arm64`. |
| L16 | Installer download is longer than the manifest size | Memory and disk abuse | Stop at the declared size and refuse `installer-size-mismatch`. Exit 1. |
| L17 | `UPDATER_LIVE_BASE_URL` is set to a non-loopback host, or to a non-http(s) scheme | Release check redirected to another host | Refuse usage. Exit 2. Loopback http is accepted for tests only. |
| L18 | `--repo` or `GH_REPO` is not owner/name | Checks the wrong repository | Refuse usage. Exit 2. |
| L19 | Redirect from github.com to the object store | Fails if the redirect is refused | Accepted: `fetch` follows redirects. The hash check still applies to the bytes received. |
| L20 | `--root` points at a checkout with other keys | Wrong keys trusted | Residual: `--root` exists for tests. The orchestrator must not pass it. |

## D. Self-test (`scripts/release-assets-selftest.js`)

The self-test proves the gpg checks with a fake spawner and a real missing-gpg run, the mirror
rules with real temporary folders, and the live validator against a local HTTP server. It
writes `artifacts/release/selftest/release-assets-result.json`. It never reads a real key,
never imports one into a keyring, and never starts `gh`.

## E. Not enforced here (user duties)

| ID | Item | Why it is not here |
|----|------|--------------------|
| U1 | Creating the GPG release key and its passphrase | The user owns the key and its policy. |
| U2 | Trust level of the GPG key in the keyring | `gpg --verify` checks the signature, not owner trust. |
| U3 | Replacing the placeholder in `TrustedKeys.cs` | Source change, made by the user. |
| U4 | Running the live check after publish against the real repository | Needs the published release. |
| U5 | Reading or changing the Windows release VM's environment | Out of scope for scripts. |
