# Contract: update manifest, receipt, and helper interface (`Bwc.Update`)

This contract fixes the update format that the Hub (check), the release tooling (sign and
publish), and the installer (D1) must follow. A change to this file is a manager decision.
Failure cases and their tests are in `docs/failure-modes/update-verifier.md`.

Status: the verifier and helper are implemented and tested with test keys. The release public key
and the hosting URLs are not decided. Until the placeholder key is replaced, every update is
refused (`TrustedKeysInvalid`).

## Files published per channel

For one channel, the release publishes two files at URLs it chooses:

| File | Content |
|------|---------|
| Manifest | UTF-8 JSON, schema 1, described below. No BOM, no comments. |
| Manifest signature | A minisign signature of the manifest bytes. Raw minisign text, or its base64 envelope. |

The updater never assumes a URL pattern. The Hub passes both URLs. Convention for the release
tooling: the signature sits beside the manifest as `<manifest>.minisig`.

Every URL must be `https` (`http` is allowed only in tests). It may not contain user information,
a fragment, a backslash, a space, a non-ASCII character, a dot segment (`.` or `..`), or an encoded
`%2e`, `%2f`, `%5c`, or `%00`. Each redirect target is checked the same way. At most 5 redirects.

## Manifest (schema 1)

```json
{
  "schema": 1,
  "version": "0.2.0-beta.1",
  "channel": "beta",
  "pubDate": "2026-11-11T00:00:00Z",
  "notesUrl": "https://example.invalid/notes/0.2.0-beta.1",
  "installers": {
    "win-x64": {
      "url": "https://example.invalid/downloads/BetterWindowsConfig-Setup-0.2.0-beta.1-x64.exe",
      "size": 104857600,
      "sha256": "0123456789abcdef...64 lowercase hex characters",
      "signature": "<base64 of the installer's minisign signature text>"
    },
    "win-arm64": { "url": "...", "size": 0, "sha256": "...", "signature": "..." }
  }
}
```

| Field | Rule |
|-------|------|
| `schema` | Integer. Must be `1`. Any other value is `UnsupportedSchema`. |
| `version` | Strict SemVer 2.0.0, no leading `v`. Must equal the `version:` field of the signed trusted comment. |
| `channel` | `stable` or `beta`. Must equal the channel the client asked for. |
| `pubDate` | UTC ISO-8601 ending in `Z`, with no fraction or exactly 3 fraction digits. No offset form. Not more than 24 hours ahead of the local clock. |
| `notesUrl` | Optional. A valid URL under the URL rules above. |
| `installers` | Object. Keys are architectures. Known keys: `win-x64`, `win-arm64`. Other keys are ignored. |
| `installers.<arch>.url` | A valid URL under the URL rules above. |
| `installers.<arch>.size` | Integer, 1 to 1 GiB. Exact byte count of the installer. |
| `installers.<arch>.sha256` | 64 lowercase hex characters. SHA-256 of the installer bytes. |
| `installers.<arch>.signature` | Base64 of the installer's minisign signature text (not the raw text). Canonical base64 only. |

Other properties are ignored, so the schema can grow. Properties may not repeat at any depth, and
nesting is limited to 16 levels. The manifest is at most 256 KiB.

## Signatures

Minisign, prehashed (`ED`). The updater refuses legacy `Ed` signatures. Trusted keys are the list
in `src/Bwc.Update/Signing/TrustedKeys.cs`; a signature must verify under a listed key whose key id
matches. Several keys may be listed during rotation. One malformed key in the list fails the whole
check (fail closed).

The trusted comment is signed, so its fields can be trusted once the signature verifies. It is a
single line of `key:value` fields separated by spaces or tabs. Keys are lowercase letters, and a
key may appear only once.

| Signature | Required trusted comment fields |
|-----------|---------------------------------|
| Manifest | `version:<manifest version>` |
| Installer | `version:<entry version>` and `arch:<win-x64 or win-arm64>`. Optional `file:<plain file name>`, which may not contain a separator, a colon, or a dot name. |

The untrusted comment line is never displayed as authenticated. The verifier returns only the
trusted comment.

Example installer trusted comment:
`timestamp:1794102823 file:BetterWindowsConfig-Setup-0.2.0-beta.1-x64.exe version:0.2.0-beta.1 arch:win-x64`

## Version and channel policy

- Compare with SemVer 2.0.0 precedence. Build metadata is ignored. A release ranks above its
  prereleases. Numeric identifiers compare as numbers, without overflow.
- `channel` must equal the requested channel.
- `stable` never offers a prerelease version.
- The offered version must be newer than the installed version. Same version is `SameVersion`.
  Older is `Downgrade`.
- There is no time-based freshness check. Replay is stopped by the version rule.

## Check command

```
BwcUpdate.exe --check --manifest-url <https url> --signature-url <https url> --channel stable|beta --current-version <semver>
```

Stdout is one JSON line: `{"ok":true|false,"failure":"<UpdateFailure>","detail":"...","version":"..."}`.
Exit codes: `0` ready, `1` refused, `2` usage. The architecture is the running process's
(`win-x64` or `win-arm64`).

On success the staged files are in `%TEMP%\BWC-Update\<32 lowercase hex>\`:

- `installer.exe`: the verified installer.
- `receipt.json`: the receipt below.

Nothing is left in the staging folder after a refusal.

## Receipt (`receipt.json`)

```json
{
  "schema": 1,
  "version": "0.2.0-beta.1",
  "channel": "beta",
  "architecture": "win-x64",
  "size": 104857600,
  "sha256": "...",
  "signature": "<base64 envelope>",
  "trustedComment": "timestamp:... version:... arch:..."
}
```

The receipt is at most 16 KiB and is not trusted by itself. The helper checks the installer again
against it, and against the installer's own signature.

## Helper (apply) command

```
BwcUpdate.exe --apply <installer.exe> --receipt <receipt.json> --current-version <semver>
              --install-dir <folder> [--wait-pid <pid>]... [--wait-timeout-ms <ms>]
              [--installer-switch <SWITCH>]...
```

Order:

1. The installer must be `installer.exe` in a 32-hex-digit folder directly under
   `%TEMP%\BWC-Update`, with `receipt.json` beside it. No link or junction may be on that path.
2. The receipt is read (strict, 16 KiB limit). Its architecture must match the helper's, and its
   version must be newer than `--current-version`.
3. Wait until every `--wait-pid` process and every tool process running from `--install-dir` has
   exited. Default wait: 60 seconds. If they are still running, stop: nothing is installed, and the
   Hub is not started.
4. Open the installer with `FileShare.Read` only. Verify size, SHA-256, minisign signature, and the
   signed `version:` and `arch:`. The file stays open until the installer has been started.
5. Start the installer as `installer.exe` with the raw command line `/S <switches> /D=<install-dir>`.
   `/D=` is last and unquoted, as NSIS requires. Switches must be uppercase, have no `=` or spaces,
   and may not be `/S` or `/D`. The installer is never killed. If it passes its time limit (30
   minutes), the Hub is not started.
6. Start `<install-dir>\BetterWindowsConfig.exe` in the install folder.

If the helper runs from `--install-dir`, it copies its folder to
`%TEMP%\BWC-Update\<guid>\helper\`, runs the copy with `--relocated --wait-pid <original pid>`, and
exits. The copy of the publish folder is about 330 MB today; see the note below.

Exit codes: `0` installed, `2` usage, `3` refused, `4` apps still running, `5` installer failed or
timed out, `6` installed but the Hub could not be started. Stdout is one JSON line with the
`failure`, `detail`, `installerExitCode`, and `hubLaunched` fields.

The helper never requests elevation (`asInvoker`). Whether the installer needs elevation is the
installer's decision (per-user or per-machine, decided in D1). The installer runs through the shell,
so Windows can show its own consent prompt.

## Notes for the release tooling and D1

- Sign the manifest and each installer with the release key, never with a test key.
- The installer must write the `BetterWindowsConfig.exe` and `BwcUpdate.exe` names unchanged, so
  the helper finds the Hub.
- Relocation copies the whole install folder. A separate helper folder in the packaging would
  make the copy much smaller. That is a packaging decision for D1.
- Real keys are created by the user. Rotation: list the old and the new key during the overlap,
  then remove the old key once no release uses it.
