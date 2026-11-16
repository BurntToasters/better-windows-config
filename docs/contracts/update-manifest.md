# Contract: update manifest, receipt, and helper interface (`Bwc.Update`)

This contract fixes the update format that the Hub (check and download), the release tooling (sign and
publish), and the installer (D1) must follow. A change to this file is a manager decision.
Failure cases and their tests are in `docs/failure-modes/update-verifier.md`. The process commands the
installer calls (`--close-running`, `--wait-pid`) are in `docs/contracts/update-cli.md`.

Status: the verifier and helper are implemented and tested. The release public key is embedded in
`src/Bwc.Update/Signing/TrustedKeys.cs`, and the placeholder is off (`IsPlaceholder` is false). The stable
manifest URL is the GitHub latest-release asset set in `src/Bwc.Hub/Updates/UpdateSources.cs`. A key list
that is malformed is refused as `TrustedKeysInvalid`. `TrustedKeysNotConfigured` (the Hub's "not configured"
message) is returned only while the placeholder list is in place.

## Files published per channel

Each channel has its own manifest URL. A manifest is signed for one channel, so one URL cannot serve both
(the Hub passes the URL for the channel the user selected). For one channel, the release publishes two files:

| File | Content |
|------|---------|
| Manifest | UTF-8 JSON, schema 1, described below. No BOM, no comments. |
| Manifest signature | A minisign signature of the manifest bytes. Raw minisign text, or its base64 envelope. |

The updater never assumes a URL pattern. The Hub passes both URLs. Convention for the release
tooling: the signature sits beside the manifest as `<manifest>.minisig`, and when the manifest URL has a
query string, the `.minisig` suffix goes before the query (`beta.json.minisig?token=...`). The release
asset names are `bwc-stable.json` and `bwc-beta.json`, with `.minisig` signatures beside them.

Every URL must be `https` (`http` is allowed only in tests), at most 4096 characters, with no user
information and no fragment. The URL is split at its first `?`:

- The path (the text before `?`) may not contain a dot segment (`.` or `..`), an encoded `%2e`, `%2f`,
  `%5c`, or `%00` (in any letter case), a backslash, a space, a control or non-ASCII character, or a
  double quote.
- The query (the text after `?`) gets only a character check: no space, control or non-ASCII character,
  backslash, double quote, or `#`. Signed release URLs carry `%2F`, `%3D`, `%3B`, and `+` in the query,
  so those are allowed there.

Each redirect target is checked the same way, including the raw `Location` text (a raw space in a query is
refused, not escaped). At most 5 redirects.

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
| `channel` | `stable` or `beta`. Must equal the `channel:` field of the signed trusted comment, and the channel the client asked for. |
| `pubDate` | UTC ISO-8601 ending in `Z`, with no fraction or exactly 3 fraction digits. No offset form. Not more than 24 hours ahead of the local clock. Informational: it is not a freshness check. |
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

The trusted comment is signed, so its fields can be trusted once the signature verifies. The untrusted
comment line is never displayed as authenticated; the verifier returns only the trusted comment.

| Signature | Trusted comment |
|-----------|-----------------|
| Manifest | Exactly `version:<semver> channel:<stable\|beta> expires:<yyyy-MM-ddTHH:mm:ssZ>`. One space between the fields, in this order, and nothing else. |
| Installer | `version:<entry version>`, `arch:<win-x64 or win-arm64>`, `product:bwc`, and `channel:<stable or beta>` (the manifest channel), as `key:value` fields separated by spaces or tabs. Optional `file:<plain file name>`, which may not contain a separator, a colon, or a dot name. Keys are lowercase letters, and a key may appear only once. Since 0.1.2 the updater requires `product` and `channel`; a 0.1.1 updater reads only `version`, `arch`, and `file` and ignores the rest. |

The manifest comment binds three things:

- `version` must equal the manifest `version`.
- `channel` must equal the manifest `channel`, so a stable manifest cannot be served to a beta request.
- `expires` is the time after which the manifest is refused (`ManifestExpired`). The check is
  `now >= expires`, so an expiry equal to the current time is already expired. The time must be a real
  UTC time in exactly that form (no offset, no fraction, no date-only form).

Example manifest trusted comment:
`version:0.2.0-beta.1 channel:beta expires:2026-11-18T00:00:00Z`

Example installer trusted comment:
`timestamp:1794102823 file:BetterWindowsConfig-Setup-0.2.0-beta.1-x64.exe version:0.2.0-beta.1 arch:win-x64 product:bwc channel:beta`

The release tooling (`scripts/assemble-bundle.js` with `scripts/sign-update-manifest.js`) builds the comment
with `manifestTrustedComment` in `scripts/lib/update-manifest.js`, which writes exactly this comment.
`tests/Bwc.Update.Tests/Support/minisign.mjs` is a test-only copy of the signer. A manifest that is signed
with any other comment is refused.

## Version and channel policy

- Compare with SemVer 2.0.0 precedence. Build metadata is ignored. A release ranks above its
  prereleases. Numeric identifiers compare as numbers, without overflow.
- `channel` must equal the requested channel (`ChannelMismatch`).
- `stable` never offers a prerelease version.
- The offered version must be newer than the installed version. Same version is `SameVersion`.
  Older is `Downgrade`.
- A signed manifest is refused once its `expires` has passed (`ManifestExpired`). Replay of an old
  manifest is stopped by the expiry and by the version rule.

## Check command (manifest only)

```
BwcUpdate.exe --check --manifest-url <https url> --signature-url <https url> --channel stable|beta --current-version <semver>
```

The check verifies the manifest and its signature, the trusted comment, the body, the expiry, the
channel, and the version policy. It downloads no installer and creates no staging folder.

Stdout is one JSON line: `{"ok":true|false,"failure":"<UpdateFailure>","detail":"...","version":"...","notesUrl":"..."}`.
`version` is the offered version when the check is ready. `notesUrl` is the manifest's optional
`notesUrl` when ready and the manifest has one, otherwise `null`. Consumers must ignore unknown properties.
Exit codes: `0` ready, `1` refused, `2` usage. The architecture is the running process's
(`win-x64` or `win-arm64`).

## Download command

```
BwcUpdate.exe --download --manifest-url <https url> --signature-url <https url> --channel stable|beta --current-version <semver>
```

The download runs the same manifest verification as the check, then makes sure a verified installer is
staged under `%TEMP%\BWC-Update\<32 lowercase hex>\`:

1. If a staged folder already holds a receipt for the same version, channel, architecture, size,
   SHA-256, and signature, and its `installer.exe` still verifies, that folder is reused. No installer
   is downloaded. A folder whose installer no longer verifies is discarded.
2. Otherwise a new folder is created, the installer is streamed to `installer.exe` with a 30 minute
   overall limit and a per-read stall limit, and it is verified (size, SHA-256, minisign signature, signed
   `version:`, `arch:`, `product:bwc`, and the manifest's `channel:`). The receipt is written only after every check passes.
3. Every other staging folder is then removed.

Progress goes to stdout as one line per few megabytes, before the result line:
`{"progress":{"received":<bytes>,"total":<bytes>}}`. The result is the last line, one JSON object:
`{"ok":true|false,"failure":"<UpdateFailure>","detail":"...","version":"...","notesUrl":"...","stagingFolder":"<folder>"}`.
`stagingFolder` is set only when `ok` is true. A killed or canceled helper leaves no staging folder that
has a receipt, and the Hub removes folders without a receipt (`StagingCleanup.DiscardIncomplete`).

Exit codes: `0` staged, `1` refused, `2` usage.

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
  "trustedComment": "timestamp:... file:... version:... arch:..."
}
```

The receipt is at most 16 KiB and is not trusted by itself. The helper checks the installer again
against it, and against the installer's own signature.

## Helper (apply) command

An update takes two helper steps (details and exit codes: `update-cli.md`, "Hand-off from the helper"; failure cases:
`docs/failure-modes/update-elevation.md`). The medium-integrity step `--apply` checks everything and asks for elevation.
The elevated step `--apply-elevated` copies the installer into a protected folder, verifies the copy, and starts it.

```
BwcUpdate.exe --apply <installer.exe> --receipt <receipt.json> --current-version <semver>
              --install-dir <folder> [--wait-pid <pid>]... [--wait-timeout-ms <ms>]
              [--installer-switch <SWITCH[=value]>]...
```

Order:

1. The helper refuses when it runs elevated (administrator or high integrity). `%TEMP%` staging is
   writable by medium-integrity processes, so an elevated process of this mode could load a planted file.
2. The installer must be `installer.exe` in a 32-hex-digit folder directly under
   `%TEMP%\BWC-Update`, with `receipt.json` beside it. No link or junction may be on that path.
3. The receipt is read (strict, 16 KiB limit). Its architecture must match the helper's, and its
   version must be newer than `--current-version`.
4. `--install-dir` must be the folder of the running `BwcUpdate.exe` (in the ship build, inside Program Files). The
   switches are validated as before, but they are not forwarded.
5. Wait until every `--wait-pid` process and every BWC process running from `--install-dir` has
   exited (the shared rule in `RunningImageRule`: fail closed, so an unreadable process counts as
   running). Default wait: 60 seconds. If they are still running, stop: nothing is installed.
6. Open the installer with `FileShare.Read` only. Verify size, SHA-256, minisign signature, and the
   signed `version:`, `arch:`, `product:bwc`, and `channel:`. The file stays open while the elevated helper is started. That
   is a check for the user's benefit (a bad file is refused before the consent prompt), not a guarantee for the elevated helper:
   the handle is released when the start returns, so the elevated helper copies the file and verifies the copy.
7. Start `<folder of BwcUpdate.exe>\BwcUpdate.exe --apply-elevated <staging folder>` with the `runas` verb. Windows shows
   its consent prompt. The helper then returns at once. It does not start the installer and does not start the Hub.
   Declining the prompt (Win32 error 1223) is the failure `ElevationDeclined`.

Every refusal before step 7 completes (including a declined prompt) starts the Hub again with one argument,
`--update-result <UpdateFailure>` (for example `--update-result ProcessesStillRunning`). The Hub shows the reason.
Before the Hub is started, the helper waits (up to the wait timeout) for the wait-pid processes to exit, so the
single-instance gate does not hand the message to an old Hub. The argument carries only the failure code, never the detail.

`--apply-elevated <staging folder>` (elevated; the only option is the folder) runs these steps:

1. It needs an elevated token (`NotElevated` otherwise, and it does nothing). Its install dir is its own folder, and the
   current version is its own assembly version. It ignores any other value. A staged version that is not newer is
   refused (`SameVersion`, `Downgrade`).
2. The folder must be `%TEMP%\BWC-Update\<32 hex>`, with no link on the path or inside it. The receipt is read as above.
3. It removes older `<install dir>.upd\<32 hex>` folders, then copies `installer.exe` to
   `<install dir>.upd\<new 32 hex>\installer.exe` (the source is held with `FileShare.Read`, and the copy stops one byte
   over the receipt size). `<install dir>.upd` and the copy folder are created with an explicit protected DACL (SYSTEM and
   Administrators only); an existing root with another owner, or one that others can write, is refused.
4. It verifies the copy (size, SHA-256, minisign signature, signed `version:`, `arch:`, `product:bwc`, and `channel:`) with
   the copy held open from the first write to the start of the installer.
5. It starts the copy as `installer.exe` with the raw command line
   `/S /RELAUNCH /PARENTPID=<this pid> /D=<install dir>`. `/D=` is last and unquoted, as NSIS requires. There is no verb,
   no caller switch, and the working directory is the system folder. The installer's `TEMP` and `TMP` are a protected
   `tmp` folder inside the copy folder, and its environment has no .NET runtime variable. It then exits.
6. A refusal removes the copy and starts the Hub at the shell's integrity level with `--update-result <code>`.

The elevated helper writes no file log. The helper removes `__COMPAT_LAYER` from its own environment before it starts any
child, so the installer and the Hub do not inherit a compatibility layer. Neither helper requests elevation in its manifest
(`asInvoker`): the elevation comes from the `runas` start of `--apply-elevated`.

Installer switches (`--installer-switch`, kept for the argument contract): `/NAME` or `/NAME=value`. NAME is an uppercase letter
followed by uppercase letters, digits, or underscores (at most 32 characters), and is never `S` or `D`. The value is 1 to 256
characters of letters, digits, `_`, `.`, `-`, `:`, and `\`, with no `..`. `RELAUNCH` and `PARENTPID` are reserved.

Exit codes of `--apply`: `0` the elevated helper started, `2` usage, `3` refused (a declined prompt included), `4` apps still
running, `5` the elevated helper did not start, `6` refused and the Hub did not start, `7` unexpected. Stdout is one JSON line:
`{"ok":true|false,"failure":"<UpdateFailure>","detail":"...","installerStarted":false,"elevatedHelperStarted":true|false,"hubLaunched":true|false}`.
`elevatedHelperStarted` is true when the prompt was accepted and the helper started. `installerStarted` is true only in the
`--apply-elevated` result. `hubLaunched` is true only when a refusal relaunched the Hub.

## Notes for the release tooling and D1 (the installer)

- Sign the manifest with `sign-manifest` (the exact trusted comment above) and each installer with the
  release key, never with a test key.
- The installer must write the `BetterWindowsConfig.exe` and `BwcUpdate.exe` names unchanged, and the install is per machine
  (Program Files). The uninstaller should remove `<install dir>.upd`, where the elevated helper leaves its last installer copy.
- The helper no longer copies the install folder. The installer replaces files in place, so it must:
  1. Start the BWC applications it finds running, by calling
     `BwcUpdate.exe --close-running --install-dir "<install folder>" --exclude-pid <pid>` (see
     `update-cli.md`).
  2. Wait for the elevated helper to exit, with `BwcUpdate.exe --wait-pid <helper pid> --timeout-seconds <n>` (the
     pid is the `/PARENTPID` value, the elevated helper), because the helper's own `BwcUpdate.exe` is a locked file until
     it exits. The installer should run its own copy of that file (for example from `$PLUGINSDIR`).
  3. Replace the files. On success, start the Hub with `BwcUpdate.exe --launch-hub` from the install folder (never
     `Exec`, which would start an elevated Hub); `/RELAUNCH` asks for it. On failure, call
     `--launch-hub --update-result InstallerFailed`, so the user sees the reason (N16).
  4. Give up after its own time limit (30 minutes) and say so; the helper is not waiting for it.
- Real keys are created by the user. Rotation: list the old and the new key during the overlap,
  then remove the old key once no release uses it.
