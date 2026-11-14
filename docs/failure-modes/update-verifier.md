# Update verifier failure modes

Scope: `Bwc.Update` (`BwcUpdate.exe`): the manifest verification step (`--check`), the installer
download and verification step (`--download`), the helper mode (`--apply`) that checks the
verified installer and starts it, and the two process commands (`--close-running`, `--wait-pid`)
that the installer calls. The minisign layer is the verifier proven in
`spikes/S5-minisign/FAILURE-MODES.md`; this file covers what the update flow adds on top of it.

Written before the tests and before the code, per AGENTS.md ("FIRST write all the ways it could
fail, THEN write the code"). This revision adds sections L to R and changes rows MS14, NE4, IN15,
HL5, HL8, HL10 to HL13, and HT7 and HT8. The 0.1.1 audit adds section S and changes row M4.

Terms:
- **WA** = wrongly accept (a bad manifest, installer, or request passes).
- **WR** = wrongly reject (a good update is refused).
- **Test** = the MSTest method in `tests/Bwc.Update.Tests` that covers the row. `Doc` means the
  behavior is a documented caller or operator duty, or is covered only by an E2E run, so no unit
  test exists.
- Every rejection is a typed `UpdateFailure` (one enum for check, download, and apply) with a reason.
  "Never throw" rows mean the public call returns the typed result.

## A. Manifest signature

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| MS1 | One manifest byte changed (first, middle, last) | WA | Reject, `ManifestSignatureInvalid` | `Manifest_TamperedByte_Rejected` |
| MS2 | Manifest signed by a key that is not trusted | WA | Reject | `Manifest_UnlistedKey_Rejected` |
| MS3 | Manifest signature packet key id changed, key bytes right | WA | Reject | `Manifest_WrongKeyId_Rejected` |
| MS4 | Signature of a different manifest served with this one | WA | Reject | `Manifest_SignatureOfOtherManifest_Rejected` |
| MS5 | Manifest signature truncated (a line or the base64 cut short) | WA if accepted, throw if not handled | Reject, no throw | `Manifest_SignatureTruncated_Rejected` |
| MS6 | Manifest signature URL returns 404 | Stuck or unclear error | Reject, `ManifestSignatureDownload` with status | `Manifest_SignatureNotFound_Reported` |
| MS7 | Untrusted comment line edited, signature kept | WR if the line is checked; WA if displayed as authenticated | Accept. The untrusted line is never returned as authenticated | `Manifest_UntrustedComment_Edit_Still_Accepted` |
| MS8 | Trusted comment edited, signature kept | WA (the comment carries `version:`, `channel:`, `expires:`) | Reject | `Manifest_TrustedComment_Edit_Rejected` |
| MS9 | Legacy `Ed` algorithm on the manifest signature | WA: legacy signs raw bytes and is not prehashed | Reject (`allowLegacy` is false in production) | `Manifest_LegacyEd_Rejected` |
| MS10 | Unknown algorithm bytes (not `ED` or `Ed`) | WA if treated as legacy | Reject | `Manifest_UnknownAlgorithm_Rejected` |
| MS11 | Signature as raw minisign text, or as the base64 envelope | WR if one form is refused | Accept both | `Manifest_RawAndEnvelope_Both_Accepted` |
| MS12 | Signature with a BOM, stray CR, extra whitespace, wrong line count | WA if trimmed silently | Reject (spike rules B11 to B17) | `Manifest_Signature_Format_Rejected` |
| MS13 | Trusted key list is empty | WA if "no keys" means "accept" | Reject, fail closed (`TrustedKeysInvalid`) | `Empty_Key_List_Rejected` |
| MS14 | Shipped placeholder public key (no real key yet) | WA if the placeholder verifies anything | Every manifest is refused, `TrustedKeysNotConfigured` (see R1) | `Placeholder_Key_Fails_Closed` |
| MS15 | Key rotation: the second trusted key signed | WR | Accept, return the trusted comment | `Rotation_Second_Key_Accepted` |
| MS16 | Key rotation: a key not in the list signed | WA | Reject | `Rotation_Unlisted_Key_Rejected` |
| MS17 | One malformed key in the list, a valid key signed | WA if the bad key is skipped | Reject the whole call (fail closed) | `Malformed_Key_Fails_Closed` |
| MS18 | Two trusted keys share a key id, the second signed | WR if only the first is tried | Accept | `Key_Id_Collision_Still_Accepts` |

## B. Manifest content

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| MC1 | UTF-8 BOM before the JSON | WR if stripped silently, WA if ignored | Reject, `ManifestInvalid` | `Manifest_Bom_Rejected` |
| MC2 | Invalid JSON, or trailing bytes after the root object | WA if the prefix is used | Reject | `Manifest_InvalidJson_Rejected` |
| MC3 | Duplicate property name at any level | WA: parsers disagree on which value wins | Reject | `Manifest_DuplicateKey_Rejected` |
| MC4 | JSON comments | WA if parsed differently by another reader | Reject (comments disallowed) | `Manifest_JsonComment_Rejected` |
| MC5 | Nesting deeper than 16 levels | Stack overflow or crash | Reject without crashing | `Manifest_DeepNesting_Rejected` |
| MC6 | Manifest larger than 256 KB | Memory exhaustion | Reject `ManifestTooLarge`; reading stops at the cap plus one byte | `Manifest_OverCap_Rejected` |
| MC7 | Manifest exactly at 256 KB | WR if the cap is off by one | Accept | `Manifest_AtCap_Accepted` |
| MC8 | `schema` is not 1 | WA if an unknown layout is read as v1 | Reject, `UnsupportedSchema` | `Unsupported_Schema_Rejected` |
| MC9 | Required field missing (`version`, `channel`, `pubDate`, `installers`, `schema`) | Null read, crash | Reject, `ManifestInvalid` | `Missing_Field_Rejected` |
| MC10 | Version not SemVer: `v0.1.0`, `0.1`, `01.0.0`, `0.1.0-01`, `0.1.0+`, leading or trailing space | WA on odd precedence | Reject | `Invalid_Version_Rejected` |
| MC11 | Manifest `version` differs from the signed trusted comment `version:` | WA: body and signed claim disagree | Reject | `Manifest_Version_Mismatch_With_Comment_Rejected` |
| MC12 | `channel` is not `stable` or `beta` | WA if unknown channels pass | Reject | `Unknown_Channel_Rejected` |
| MC13 | Manifest channel differs from the requested channel | WA: stable client takes beta | Reject `ChannelMismatch` | `Channel_Mismatch_Rejected` |
| MC14 | Stable manifest carries a prerelease version | WA: testers reach stable clients | Reject `PrereleaseOnStable` | `Prerelease_On_Stable_Rejected` |
| MC15 | Beta manifest carries a release version | WR | Accept | `Release_On_Beta_Accepted` |
| MC16 | `pubDate` not a normalized UTC ISO-8601 string (offset, no `Z`, date only) | WA on ambiguous dates | Reject | `PubDate_Format_Rejected` |
| MC17 | `pubDate` more than 24 hours after the local clock | Wrong local clock or forged future date | Reject `ManifestDatedInFuture` | `PubDate_Far_Future_Rejected` |
| MC18 | `pubDate` one hour after the local clock | WR from small skew | Accept | `PubDate_Small_Skew_Accepted` |
| MC19 | `pubDate` years in the past (local clock wrong, or an old manifest) | WR if the date alone is enforced | Accept. Staleness is stopped by `expires:` (section L) and the version rule (section D), not by `pubDate` | `PubDate_Old_Accepted_By_Design` |
| MC20 | `notesUrl` is `http` while https is required | WA on the notes link | Reject | `Notes_Url_Http_Rejected` |
| MC21 | Unknown property on the root or on an installer entry | WR on forward-compatible additions | Ignore | `Unknown_Fields_Ignored` |
| MC22 | No installer entry for the running architecture | Wrong architecture installed | Reject `NoInstallerForArchitecture` | `Missing_Architecture_Rejected` |
| MC23 | Installer entry field missing or wrong type (`size` as a string, `sha256` absent) | Null or coerced values | Reject, `ManifestInvalid` | `Installer_Field_Type_Rejected` |
| MC24 | Installer `size` is 0, negative, above the 1 GB cap, or not an integer | Zero-length or huge downloads | Reject | `Installer_Size_Out_Of_Range_Rejected` |
| MC25 | `sha256` not 64 lowercase hex characters | Two text forms for one hash | Reject | `Sha256_Format_Rejected` |
| MC26 | Installer `signature` is not a base64 minisign envelope | Garbage reaches the download step | Reject before download | `Installer_Signature_Not_Envelope_Rejected` |
| MC27 | Manifest carries an unknown architecture key (for example `win-x86`) | WR on forward-compatible additions | Ignore the key, keep the known ones | `Unknown_Architecture_Key_Ignored` |

## C. URLs

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| UR1 | `http` installer or manifest URL while https is required | WA: downgrade on the wire | Reject `UrlRejected` | `Http_Url_Rejected_When_Https_Required` |
| UR2 | Scheme `file`, `ftp`, `javascript`, `data`, or `ws` | Local file read or unexpected fetch | Reject | `Non_Http_Scheme_Rejected` |
| UR3 | User info in the URL (`https://user@host/`) | Credentials in logs, host confusion | Reject | `Url_UserInfo_Rejected` |
| UR4 | Fragment in the URL | Ambiguous identity | Reject | `Url_Fragment_Rejected` |
| UR5 | `.` or `..` path segment | Path traversal | Reject | `Url_Dot_Segment_Rejected` |
| UR6 | Encoded traversal or separators (`%2e%2e`, `%2F`, `%5C`, `%00`) in the path (the text before `?`, any case) | Traversal after decoding | Reject | `Url_Encoded_Traversal_Rejected` |
| UR7 | Backslash, space, or control character in the URL | Parser disagreement | Reject | `Url_Bad_Characters_Rejected` |
| UR8 | URL longer than 4096 characters | Log or buffer abuse | Reject | `Url_TooLong_Rejected` |
| UR9 | Redirect to a non-https URL while https is required | WA on the final hop | Reject the redirect before following it | `Redirect_To_Http_Rejected` |
| UR10 | Redirect to another host over https | WR if rejected; the signature is the authority | Accept | `Redirect_To_Other_Host_Accepted` |
| UR11 | More than 5 redirects | Loop | Reject `DownloadFailed` | `Redirect_Loop_Rejected` |
| UR12 | Local installer file name derived from the URL or the trusted comment | Traversal into the staging root | Never derived. The file is always `installer.exe` in a new folder | `Local_File_Name_Is_Fixed` |
| UR13 | GitHub-shaped release asset URL (about 1000 characters, final hop of a 302 chain) whose query holds `%2F`, `%3D`, `%3B`, `+`, `%20` | WR: every update check fails with `UrlRejected` | The query gets only the character-class check (UR15). Manifest and installer both succeed | `Github_Shaped_Redirect_Chain_Accepted` |
| UR14 | `%2f` or `%2F` in the path when a query follows (`/a%2Fb?x=1`) | Traversal hidden by a query | Reject `UrlRejected` (the path check runs on the text before `?`) | `Encoded_Separator_In_Path_Refused_With_Query` |
| UR15 | Query with a space, control character, non-ASCII character, backslash, double quote, or `#` | Parser disagreement | Reject `UrlRejected` | `Query_Bad_Characters_Rejected` |
| UR16 | Redirect `Location` whose query has a raw space (the URI parser would escape it silently) | Parser disagreement after normalization | Reject the redirect before following it | `Redirect_Raw_Space_In_Query_Rejected` |
| UR17 | URL of exactly 4096 characters | Limit off by one | Accept | `Url_At_Limit_Accepted` |

## D. Version policy

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| VS1 | Manifest version equals the installed version | Reinstall loop | Reject `SameVersion` | `Same_Version_Rejected` |
| VS2 | Manifest version lower than installed | Downgrade or replay of an old release | Reject `Downgrade` | `Downgrade_Rejected` |
| VS3 | Precedence order of the SemVer 2.0.0 example list (`1.0.0-alpha` to `1.0.0`) | Wrong prerelease order | Matches the spec | `Prerelease_Precedence_Matches_Spec` |
| VS4 | `0.1.0-beta.10` compared with `0.1.0-beta.2` | Lexical compare says lower | Numeric identifiers compare numerically | `Numeric_Prerelease_Compares_Numerically` |
| VS5 | Build metadata differs only (`0.1.0+a`, `0.1.0+b`) | Treated as a new version | Same precedence, reject `SameVersion` | `Build_Metadata_Ignored_For_Precedence` |
| VS6 | Numeric identifiers above 2^64 | Parse overflow | Compare without overflow | `Huge_Numeric_Identifiers_Compare` |
| VS7 | Installed prerelease, manifest release with the same core | WR: the stable release must upgrade a beta | Accept | `Release_Upgrades_Prerelease` |
| VS8 | Installed version is missing or malformed | Fail-open comparison | Reject `InvalidOptions` before any network call | `Invalid_Current_Version_Rejected` |
| VS9 | Prerelease with leading zeros or empty identifier (`1.0.0-alpha..1`) | Two spellings, one version | Reject | `Invalid_Prerelease_Identifier_Rejected` |

## E. Installer download and verification

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| IN1 | Server sends more bytes than the manifest `size` | Oversized payload | Stop at size plus one byte, reject `InstallerTooLarge` | `Installer_Larger_Than_Manifest_Rejected` |
| IN2 | Server sends fewer bytes than the manifest `size` | Truncated payload | Reject `InstallerSizeMismatch` | `Installer_Smaller_Than_Manifest_Rejected` |
| IN3 | Same size, one byte changed | WA if the hash is skipped | Reject `InstallerHashMismatch` | `Installer_Hash_Mismatch_Rejected` |
| IN4 | Correct hash, but the installer signature belongs to another file | WA | Reject `InstallerSignatureInvalid` | `Installer_Signature_Of_Other_File_Rejected` |
| IN5 | Valid signature for the `win-x64` build, served for `win-arm64` | Cross-architecture install | Reject `InstallerCommentMismatch` (comment `arch:` is signed) | `Installer_Arch_Mismatch_Rejected` |
| IN6 | Signed `version:` differs from the entry version | Replay of another release | Reject `InstallerCommentMismatch` | `Installer_Version_Mismatch_Rejected` |
| IN7 | Signed comment lacks `version:` or `arch:` | WA if missing fields pass | Reject `InstallerCommentMismatch` | `Installer_Comment_Missing_Field_Rejected` |
| IN8 | Signed `file:` value has a separator, `..`, or a colon | Traversal through a caller that uses the name | Reject | `Installer_Comment_File_Traversal_Rejected` |
| IN9 | Connection closed before `Content-Length` bytes arrive | Partial file reported as complete | Reject `InstallerDownloadInterrupted`, partial file deleted | `Interrupted_Download_Rejected` |
| IN10 | No bytes for longer than the stall timeout | Hang | Reject `Timeout` (stalled) | `Stalled_Download_Times_Out` |
| IN11 | Whole download longer than the overall timeout | Hang | Reject `Timeout` (overall) | `Overall_Timeout_Rejected` |
| IN12 | HTTP 404 or 500 on the installer | Error page saved as installer | Reject `InstallerDownload` with the status code | `Http_Error_Rejected` |
| IN13 | Any failure after the download started | Partial or unverified files left behind | The staging folder is removed | `Failed_Download_Leaves_No_Files` |
| IN14 | Receipt file | Receipt for an unverified file | Written only after every check passes | `Receipt_Only_After_Verification` |
| IN15 | Second download of the same version, channel, architecture, size, SHA-256, and signature | Repeated 330 MB download | Reuse the verified folder (section M, M2). A failed or changed attempt never reuses a folder | `Download_Reuses_Verified_Staging`, `Download_Reuse_Tampered_Installer_Redownloads` |
| IN16 | Cancellation during the download | Partial file kept | Reject `Canceled`, partial file deleted | `Cancellation_Removes_Partial_File` |
| IN17 | Staging root exists as a file (cannot create the folder) | Unhandled exception | Reject `Io` with the exception type | `Staging_Root_Is_File_Reported` |
| IN18 | No connection (refused on loopback) | Unhandled exception | Reject `DownloadFailed` | `Connection_Refused_Reported` |
| IN19 | Served body is an HTML error page with HTTP 200 | Saved as an installer | Hash mismatch, reject | `Html_Page_As_Installer_Rejected` |
| IN20 | Cancellation while the file is being written | Unhandled `OperationCanceledException` from the write or flush | Reject `Canceled` (or `Timeout` when the limit fired), partial file deleted | `Doc`: the write and flush use the same deadline token as the reads, which `Cancellation_Removes_Partial_File` exercises; no deterministic write-path test |

## F. Helper: installer path and receipt

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HP1 | Installer path outside the staging root (for example `C:\Windows\System32\...`) | Running an arbitrary file | Refuse, no process started | `Apply_Path_Outside_Root_Refused` |
| HP2 | Traversal in the installer path (`root\..\x\installer.exe`) | Escapes the root after normalization | Refuse | `Apply_Path_Traversal_Refused` |
| HP3 | Installer file name other than `installer.exe` | Caller-chosen names | Refuse | `Apply_Wrong_File_Name_Refused` |
| HP4 | Folder name not 32 lowercase hex characters | Caller-chosen folders | Refuse | `Apply_Folder_Name_Refused` |
| HP5 | Folder or file is a reparse point (junction or symlink) | Redirect to another file | Refuse | `Apply_Reparse_Point_Refused` |
| HP6 | Receipt path is not `receipt.json` beside the installer | Receipt from another attempt | Refuse | `Apply_Receipt_Elsewhere_Refused` |
| HP7 | Receipt malformed, not schema 1, over 16 KB, or with unknown channel | Parser confusion | Refuse | `Apply_Receipt_Invalid_Refused` |
| HP8 | Receipt architecture differs from the helper architecture | Wrong installer | Refuse | `Apply_Arch_Mismatch_Refused` |
| HP9 | Receipt version lower than or equal to the installed version | Downgrade or reinstall | Refuse `Downgrade` or `SameVersion` | `Apply_Downgrade_Refused`, `Apply_Same_Version_Refused` |
| HP10 | `--current-version` missing or invalid in apply mode | Fail-open comparison | Usage error, exit code 2 | `Apply_Missing_Current_Version_Usage` |

## G. Helper: time-of-check to time-of-use

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HT1 | Installer modified after download, before apply | Tampered file runs | Re-verification fails, refuse, not run | `Apply_Modified_Installer_Refused` |
| HT2 | Installer replaced by a validly signed file of another version | Replay | Refuse (comment `version:` or receipt mismatch) | `Apply_Replayed_Installer_Refused` |
| HT3 | Installer swapped during the wait for the apps to exit | Swap between the first check and the run | Re-verification right before launch refuses | `Apply_Swapped_During_Wait_Refused` |
| HT4 | Verified installer held open | Another process writes or replaces it between check and launch | Write-open and delete-open fail while the helper holds the file (FileShare.Read only) | `Verified_Installer_Is_Locked` |
| HT5 | Bytes the launcher starts | Different bytes than verified | The launcher sees the same hash as the verified file | `Launch_Sees_Verified_Bytes` |
| HT6 | Receipt rewritten to match a swapped installer | Forged receipt | Refuse: the minisign signature of the swapped file fails | `Apply_Forged_Receipt_Refused` |
| HT7 | Helper runs from the install folder, which the installer replaces | Installer cannot replace the locked `BwcUpdate.exe` | The helper does not copy itself. It starts the installer and exits at once; the installer waits for the helper pid with `--wait-pid` (section P), so the helper's file lock is gone before the installer writes. Installer duty (D1) | `Apply_Starts_Installer_And_Returns` (behavior), `Doc` (installer side) |
| HT8 | Helper folder copied to %TEMP% (removed in this revision) | Copies the whole install folder (about 330 MB) | No copy is made. Nothing to test: the relocation code is removed | `Doc` (removed) |

## H. Helper: processes, install folder, and launch

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HL1 | A waited process is still alive at the timeout | Installer runs over live files | Refuse `ProcessesStillRunning`, installer not started, Hub relaunched with the code (N5) | `Apply_Still_Running_Refused` |
| HL2 | Waited processes exit before the timeout | WR | Installer started | `Apply_Waits_Then_Installs` |
| HL3 | `--wait-pid` value not a positive integer | Ambiguous wait set | Usage error, exit code 2 | `Wait_Pid_Invalid_Usage` |
| HL4 | `--wait-pid` names a process that does not exist | Wait forever | Treated as exited | `Missing_Pid_Treated_As_Exited` |
| HL5 | A BWC executable running from the install folder (found by process name) | Overwrite of a running app | Counted as running by the shared predicate (section O). An unreadable image is counted as running (fail closed) | `Scan_Counts_Tool_Under_Install_Dir_As_Running`, `Unreadable_Image_Counts_As_Running` |
| HL6 | Install folder contains `"`, a control character, or a trailing backslash | Breaks the unquoted `/D=` argument | Refuse `InstallDirUnsafe` | `Install_Dir_Unsafe_Refused` |
| HL7 | Install folder is relative or does not exist | Installs to the wrong place | Refuse `InstallDirUnsafe` | `Install_Dir_Missing_Refused` |
| HL8 | Installer switch that is `/S`, `/D`, lowercase, has a space or quote, or has a value that breaks the rules in section O | Overriding the silent or directory switches, or a broken raw command line | Refuse `InstallerArgumentRejected`; allowed switches pass | `Installer_Arg_Validation` |
| HL9 | Command line for the installer | `/D=` must be last and unquoted | Exactly `/S /RELAUNCH /PARENTPID=<helper pid> <switches> /D=<install dir>`, directory unquoted | `Installer_Command_Line_Exact` |
| HL14 | Caller passes `/RELAUNCH` or `/PARENTPID` as an installer switch | Two values for one switch, or a PARENTPID that is not the helper | The helper drops them and writes its own, once each | `Installer_Relaunch_Switches_Not_Duplicated` |
| HL10 | Installer exit code | Helper waits and reports the wrong thing | Removed: the helper does not wait for the installer (N1). The installer's own result is its duty | `Apply_Starts_Installer_And_Returns` |
| HL11 | Installer does not exit | Hang in the helper | Removed: the helper has exited. The installer's time limit is its duty | `Doc` |
| HL12 | Hub executable missing for a refusal relaunch | Crash | Reported as `HubMissing`, no exception, exit 6 | `Hub_Missing_Reported` |
| HL13 | Hub relaunch | Wrong executable or folder | Path is `<install dir>\BetterWindowsConfig.exe`, working folder is the install dir | `Hub_Relaunch_Uses_Install_Dir` |

## I. Command line and entry

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| CL1 | No arguments, or `--help` | Confusing exit | Usage text, exit code 0 | `Help_Lists_Modes` |
| CL2 | Unknown option | Silent ignore | Usage error, exit code 2 | `Unknown_Option_Usage` |
| CL3 | Option given twice | Last value wins silently | Usage error, exit code 2 | `Duplicate_Option_Usage` |
| CL4 | `--check` or `--download` manifest URL is `http` | WA on the production path | Refused (production always requires https) | `Check_Http_Manifest_Refused` |
| CL5 | `--check` or `--download` missing `--channel` or with an unknown channel | Default channel guessed | Usage error, exit code 2 | `Check_Channel_Usage` |
| CL6 | `--download` takes the same options as `--check` and nothing else | Option drift between the steps | Same parser rules as `--check` | `Download_Arguments_Parse` |

## J. Keys and clock

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| KY1 | Real key rotated out of the list | Old releases verify forever | Only listed keys verify (MS15, MS16) | `Rotation_Unlisted_Key_Rejected` |
| KY2 | Public key text in the wrong form (not two lines, algorithm not `Ed`) | WA if accepted | Rejected at parse time (MS17) | `Malformed_Key_Fails_Closed` |

## K. Not enforced here (documented duties)

| ID | Case | Note |
|----|------|------|
| NE1 | Host allow list for the installer URL | `Doc`. The signature is the authority. A future allow list belongs to the hosting decision. |
| NE2 | Key compromise and rotation policy | `Doc`. The user creates real keys and decides rotation. The placeholder key fails closed (MS14). |
| NE3 | Attacker with the user's own account | `Doc`. Signatures stop remote tampering. Local malware under the same account is out of scope. |
| NE4 | Freshness of a signed manifest | Enforced only by `expires:` (section L) and the version rule (section D). A manifest with an expiry in the past is refused. `pubDate` is not a freshness check (MC19). |
| NE5 | Secrets in logs | `Doc`. Logs record host and path only, never query strings, keys, or signature text. |
| NE6 | File replaced after the helper's launch | `Doc`. The lock (HT4) covers the window up to process creation. The installer owns the file after that. |
| NE7 | Wrong-but-signed release content (a bad build signed by the real key) | `Doc`. Signing policy and release review are outside the verifier. |
| NE8 | Installer's own elevation and file replacement | `Doc`. The helper never elevates (N10 refuses an elevated helper). The installer decides per-user or per-machine (D1). |

## L. Signed manifest metadata

The trusted comment of the manifest signature must be exactly
`version:<semver> channel:<stable|beta> expires:<yyyy-MM-ddTHH:mm:ssZ>` (see
`docs/contracts/update-manifest.md`). It binds the version, the channel, and the expiry.

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| L1 | Trusted comment without `channel:` | A manifest not bound to a channel | Reject `ManifestInvalid` | `Comment_Missing_Channel_Rejected` |
| L2 | Trusted comment with an extra field (for example `timestamp:`), fields out of order, or extra spaces | Two spellings of one claim, or claims the verifier does not read | Reject `ManifestInvalid` (exact format) | `Comment_Format_Not_Exact_Rejected` |
| L3 | Signed `channel:` differs from the manifest body `channel` | Body moved to another channel | Reject `ManifestInvalid` | `Comment_Channel_Mismatch_Rejected` |
| L4 | Signed `expires:` is in the past | Replay of an old, correctly signed manifest | Reject `ManifestExpired` | `Manifest_Expired_Rejected` |
| L5 | Signed `expires:` equals the current time | Boundary case | Reject `ManifestExpired` (expiry is exclusive: now is not before it) | `Manifest_Expires_At_Now_Rejected` |
| L6 | `expires:` not `yyyy-MM-ddTHH:mm:ssZ` (offset, fraction, date only, impossible date such as 2026-02-30) | Ambiguous expiry | Reject `ManifestInvalid` | `Comment_Expires_Format_Rejected` |
| L7 | Signed `expires:` one day ahead | WR | Accept | `Manifest_Expires_Future_Accepted` |
| L8 | Signed `version:` is not SemVer | Parser confusion | Reject `ManifestInvalid` | `Comment_Version_Not_SemVer_Rejected` |
| L9 | Signed `channel:` is neither `stable` nor `beta` | Unknown channel | Reject `ManifestInvalid` | `Comment_Unknown_Channel_Rejected` |
| L10 | Stable manifest signed `channel:stable`, served to a beta request | Channel crossing | Reject `ChannelMismatch` (MC13) | `Channel_Mismatch_Rejected` |

## M. Check, download, and staging

`--check` verifies the manifest only. It reports the version and the notes URL and creates no
staging folder. `--download` runs the same manifest checks, then reuses or downloads the installer.

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| M1 | `--check` on a valid manifest | Downloads 330 MB for nothing | Reports version and notes URL; no installer request; no staging folder | `Check_Does_Not_Download_Or_Stage` |
| M2 | `--download` with a verified staged installer of the same version, channel, architecture, size, SHA-256, and signature | Repeated download | Reuse: no installer request, and the same folder is returned | `Download_Reuses_Verified_Staging` |
| M3 | Reused installer changed on disk | Tampered reuse | Re-verification fails, the folder is discarded, and a fresh download is verified | `Download_Reuse_Tampered_Installer_Redownloads` |
| M4 | Successful download prunes other staging folders, complete or incomplete | Growth of %TEMP%, or destroying a second install's staged update | Only folders older than 24 hours are removed (S13); younger ones are kept (S12). The returned folder always remains | `Download_Prunes_Older_Staging`, `Download_Keeps_Younger_Staging` |
| M5 | Staging root holds a folder whose name is not 32 lowercase hex, or a file | Deleting foreign data | Left alone | `Prune_Leaves_Foreign_Entries` |
| M6 | Staging folder is a reparse point (junction) | Deleting through a link | Skipped: not followed and not deleted | `Prune_Skips_Reparse_Point` |
| M7 | Download progress | Silent wait | Progress reports rise and end at the manifest size | `Download_Reports_Progress` |
| M8 | Cancellation or kill during the download | Partial folder kept | Helper deletes its own folder on cancellation (IN16). After a kill the Hub removes folders without `receipt.json` (M9) | `Cancellation_Removes_Partial_File` |
| M9 | Helper killed (no cleanup possible) and a folder without `receipt.json` remains | Partial installer kept | Folders without `receipt.json` are removed by the Hub's cleanup, never folders with one | `Discard_Incomplete_Removes_Folders_Without_Receipt` |
| M10 | Time limits | The Hub kills a helper that is still working | Hub limits match the helper: check 3 minutes (two 60 s manifest reads, margin); download 32 minutes (manifest pair plus the 30 minute installer limit). The helper's own limits are unchanged | `Doc` (hub.md) |
| M11 | `--download` with a reused folder whose receipt names another version | Wrong receipt reused | Not reused; the folder is pruned once it is older than 24 hours (S13) | `Download_Ignores_Receipt_Of_Other_Version` |

## N. Apply hand-off

`--apply` checks the installer and the receipt, waits for the Hub and the tools, verifies the
installer again, starts it, and exits. The installer is not waited for. Every refusal before the
installer starts relaunches the Hub with a result argument (`--update-result <UpdateFailure>`).

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| N1 | Every check passes | Helper stays alive holding locks | The installer is started. The helper exits 0 with `installerStarted: true`; it does not wait for the installer and does not start the Hub (the installer does) | `Apply_Starts_Installer_And_Returns` |
| N2 | The installer cannot be started (Win32 error) | Silent failure | Refuse `InstallerFailed` with the error type; Hub relaunched with the code | `Installer_Start_Failure_Relaunches_Hub` |
| N3 | Refusal before the installer starts (path, receipt, architecture, downgrade, same version, install folder, switch) | The user sees nothing | Hub relaunched with `--update-result <failure>` | `Refusal_Relaunches_Hub_With_Message` |
| N4 | The installer file cannot be opened for verification | Hub left with nothing (was exit 3 with no relaunch) | Same as N3: Hub relaunched with the code | `Open_Failure_Relaunches_Hub` |
| N5 | Processes still running after the wait | Hub relaunch races the single-instance gate | `ProcessesStillRunning`; installer not started; Hub relaunched with the code (no second wait) | `Apply_Still_Running_Refused` |
| N6 | A refusal while a `--wait-pid` process still runs | The relaunched Hub only activates the old one and exits | The helper waits (bounded by the wait timeout) for those pids to exit before it relaunches the Hub | `Refusal_Waits_For_Hub_Before_Relaunch` |
| N7 | The relaunch argument | Unsafe text on the command line | Only the failure code is passed, never the detail | `Refusal_Relaunches_Hub_With_Message` (asserts the argument list is exactly the code) |
| N8 | Hub executable missing for a refusal relaunch | Crash | `HubMissing`, no exception (HL12) | `Hub_Missing_Reported` |
| N9 | Verified installer held open | Replacement before launch | Lock held until the start (HT4) | `Verified_Installer_Is_Locked` |
| N15 | Installer succeeds (the helper passed `/RELAUNCH /PARENTPID=<pid>`) | The installer must start the Hub itself | Installer duty (D1): after the files are replaced it starts `BetterWindowsConfig.exe`. The helper does not | `Doc` (installer side) |
| N16 | Installer fails after the helper started it | Hub never comes back and the user sees nothing | Installer duty (D1): start the still-installed Hub with `--update-result InstallerFailed` | `Doc` (installer side) |
| N10 | Helper runs elevated (high integrity or administrator token) | Medium-integrity processes can write %TEMP% staging, so an elevated run would load planted files | Refuse `RunningElevated` before any action; Hub relaunched with the code; no installer started | `Apply_Elevated_Refused` |
| N11 | `__COMPAT_LAYER` in the helper environment | Compat layer leaks into the installer and the Hub | Removed from the helper's process environment before any child starts | `Compat_Layer_Removed_From_Children` |
| N12 | The Hub's relaunch environment | Hub started with inherited compat flags | Covered by N11 (the helper scrubs its environment, and the Hub no longer sets the variable) | `Compat_Layer_Removed_From_Children` |
| N13 | The GUI handoff: Download and Install pressed in the Hub, the helper from the publish folder | The helper or installer differs from what the user approved | The stand-in (the real signed bytes) runs from the staging folder after the Hub has exited, with exactly `/S /D=<install dir>`; the Hub starts again from the publish folder. Covered end to end by the GUI test, which reads the stand-in's record and the helper's log (`hub.install.*` checks in `docs/failure-modes/hub.md`) | `Hub_UpdateAvailable_InstallRunsVerifiedInstaller` |
| N14 | The GUI handoff with a tampered download (bytes differ from the signed hash) | The changed bytes run, or the user sees no reason | The helper refuses `InstallerHashMismatch` during the download; the stand-in never runs; no Hub starts; the Hub's message names the failure | `Hub_UpdateAvailable_TamperedInstallerIsRefused` |

## O. Shared process predicate (`RunningImageRule`)

One predicate, in `src/Bwc.Update/Process/RunningImageRule.cs`, is used by the Hub's pre-check,
the helper, and `--close-running`. The Hub compiles the same file (source link).

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| O1 | Image inside the install folder | Counted as running | Running | `Image_Inside_Install_Dir_Is_Running` |
| O2 | Image in a sibling folder whose name starts with the install folder's name (`C:\BWC2\x.exe` for `C:\BWC`) | Prefix match counts a stranger | Not running | `Sibling_Folder_Prefix_Is_Not_Running` |
| O3 | Image path differs from the install folder only in letter case | Missed match | Running | `Install_Dir_Compare_Ignores_Case` |
| O4 | Install folder given with a trailing separator | Double separator, missed match | Same result as without it | `Install_Dir_Trailing_Separator_Ignored` |
| O5 | Image cannot be read (access denied, unknown) | An install over a running app | Running (fail closed) | `Unreadable_Image_Counts_As_Running` |
| O6 | Process has exited (its handle is signaled) | Waiting for a ghost | Not running | `Exited_Process_Is_Not_Running` |
| O7 | Image readable and outside the install folder | Blocks an install for nothing | Not running | `Image_Outside_Install_Dir_Is_Not_Running` |
| O8 | Hub and helper decide differently | Drift between the pre-check and the helper | One source file, linked into the Hub. | `Doc` (build link) |

## P. Process commands for the installer (`--close-running`, `--wait-pid`)

Documented in `docs/contracts/update-cli.md`.

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| P1 | Missing `--install-dir`, a pid that is not a positive integer, a timeout outside 1 to 3600, an unknown option, or an option given twice | Ambiguous command | Exit 2 | `CloseRunning_Usage_Errors` |
| P2 | No BWC process runs from the folder | Nothing to close | Exit 0 | `CloseRunning_None_Running_Exits_Zero` |
| P3 | A BWC process with a top-level window runs from the folder, and closes on WM_CLOSE | Installer over a running app | Exit 0 | `Doc` (GUI path, covered by E2E) |
| P4 | A BWC process runs from the folder and has no window to close, or ignores the close | Installer over a running app | Exit 3; the names and pids are printed | `CloseRunning_Still_Running_Exits_Three` |
| P5 | `--close-running` excludes its own pid and the `--exclude-pid` values | Closing the installer's own helper | Those pids are never signaled | `CloseRunning_Excludes_Own_And_Listed_Pids` |
| P6 | `--wait-pid` names a process that is already gone | Waiting forever | Exit 0 | `WaitPid_Missing_Exits_Zero` |
| P7 | `--wait-pid` process exits within the timeout | WR | Exit 0 | `WaitPid_Exits_In_Time` |
| P8 | `--wait-pid` process still runs at the timeout | The installer overwrites a locked file | Exit 3 | `WaitPid_Live_Times_Out_Exits_Three` |

## Q. Environment and elevation

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| Q1 | `__COMPAT_LAYER` set in the helper's environment | Leaks to children | Removed (N11) | `Compat_Layer_Removed_From_Children` |
| Q2 | Installer switch with a `=` value that holds `"`, a space, a control character, `..`, or `=` | Broken raw command line | Refuse `InstallerArgumentRejected` | `Installer_Arg_Validation` |
| Q3 | Installer switch `/NAME=value` with a safe value (letters, digits, `_`, `.`, `-`, `:`, `\`) | Needed for the E2E installer (`/RUNID=`, `/SHORTCUTS=`) | Accept | `Installer_Arg_Validation` |
| Q4 | Installer switch with a lowercase name, an empty value, or a value over 256 characters | Ambiguous switch | Refuse | `Installer_Arg_Validation` |
| Q5 | E2E flavor: the `BWC_E2E` seam applies to `--apply` as well as `--check` | E2E installer switches pass the wrong checks | `Doc`: `UpdateE2ESeam` is compiled only into the E2E flavor and runs in the E2E build (not in the unit test project) | `Doc` |

## R. Keys and URLs

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| R1 | Shipped placeholder key list | Every manifest accepted or a misleading error | Refused with `TrustedKeysNotConfigured` (the Hub says updates are not configured) | `Placeholder_Key_Fails_Closed` |
| R2 | Key list is not the placeholder and is malformed | Reported as "not configured" | Refused with `TrustedKeysInvalid`, a distinct message in the Hub | `Malformed_Key_List_Reports_TrustedKeysInvalid` |
| R3 | Signature URL with a query string | `.minisig` appended after the query, so the signature is not found | The `.minisig` suffix goes before the query: `path.minisig?query` | `Signature_Url_Inserts_Before_Query` |
| R4 | Signature URL with no query | Wrong suffix | `path.minisig` | `Signature_Url_Without_Query` |
| R5 | Channel manifest URL | One URL serving both channels: one channel always fails | One manifest URL per channel (Hub). A channel mismatch is shown as its own message | `Doc` (hub.md) |

## S. Hardening (0.1.1 audit)

Added in the 0.1.1 audit. Every row is written before its test or code.

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| S1 | Receipt held open by another process (sharing violation) | An exception escapes; the user sees no app | `ReceiptInvalid` refusal, no exception; apply mode relaunches the Hub with the code | `Receipt_Locked_Refused_Not_Thrown` |
| S2 | Receipt cannot be read (access denied) | Same as S1 | `ReceiptInvalid` refusal, no exception | `Receipt_Unreadable_Refused` |
| S3 | A folder or file on the installer path cannot be inspected for reparse points (access denied, or a read error other than not-found) | Unknown is treated as safe | Refuse `PathRejected` (unknown counts as a link). Not-found counts as absent | `Doc` (an access-denied attribute read cannot be reproduced without elevation; the catch is checked by inspection) |
| S4 | Installer read fails while the verifier hashes it (I/O error or access denied) | Exception escapes the verifier | `Io` refusal; the installer is not started | `Verify_Read_Failure_Refused` |
| S5 | A staging folder cannot be enumerated, or its installer cannot be opened during reuse | Exception ends the download | Folder is not reused; the download continues | `Reuse_Unreadable_Folder_Skipped` |
| S6 | The installer's final path (GetFinalPathNameByHandle) is not the final path of the staging folder it was opened from, plus `installer.exe`. The folder is held open first (backup semantics, share read, write, delete) | A different file runs than the one verified (a junction or swap between the two opens) | Refuse `PathRejected` before the installer starts. The folder handle stays open until the start | `Final_Path_Mismatch_Refused`, `Installer_Outside_Held_Folder_Refused` |
| S7 | Final path spelling differs by a `\?\` prefix or by letter case | Refuses a good path | Equal after normalization (case-insensitive, prefix stripped) | `Final_Path_Compare_Normalizes` |
| S8 | Installer started with the staging folder as its working directory | A planted DLL in staging loads | Started by the verified final path, working directory is the system directory; the handle stays open until the start | `Doc` (WindowsUpdateProcessHost; exe check) |
| S9 | A BWC process of another session or another user runs from the install folder | Install blocked, or waits for a process the user cannot close | Ignored by the running check | `Other_Session_Process_Ignored` |
| S10 | A process whose session id cannot be read | Fail-open if ignored | Counted as running (fail closed, section O5) | `Unknown_Session_Counts_As_Running` |
| S11 | Wait pids given to `--apply` | Other sessions' pids held forever | Wait pids are not session-filtered (the caller names them) | `Doc` |
| S12 | Successful download while another staging folder is younger than 24 hours (a second install's staged update) | That staged update is destroyed | Kept | `Download_Keeps_Younger_Staging` |
| S13 | Staging folder older than 24 hours (age = receipt write time, or folder creation time when there is no receipt) | Growth of %TEMP% | Pruned | `Download_Prunes_Older_Staging` |
| S14 | Receipt written in place and cut short by a crash | A partial receipt is read | Written to a temp file in the same folder, then moved over `receipt.json` with overwrite; no temp file remains | `Receipt_Write_Is_Atomic` |
| S15 | Receipt write fails after the installer is verified | Temp file left in the folder | Temp file removed; the folder is discarded | `Doc` |
| S16 | Unexpected exception in check or download mode | No output for the caller | One JSON line with failure `Unexpected`, exit code 7, logged | `Doc` (exe check) |
| S17 | Unexpected exception in apply mode before the installer starts | The user is left with no app | Installer not started; the Hub is relaunched with `--update-result Unexpected`; exit code 7 | `Unexpected_In_Apply_Relaunches_Hub` |
| S18 | Unexpected exception after the installer started | Hub relaunched while the installer runs | No relaunch; `installerStarted: true`, failure `Unexpected`, exit code 7 | `Doc` |
| S19 | Proxy that requires the Windows account | Download fails behind an authenticated proxy | Default credentials sent to the proxy | `Doc` |
| S20 | Second instance sends to a pipe name that another user or session owns | Squatting or collision | See core.md, SingleInstance: client uses CurrentUserOnly, pipe name carries the session id | `Second_Instance_Message_Reaches_First` (core.md section) |
| S21 | The staging path is given in 8.3 short form (for example `C:\Users\JOHNSM~1\...`, which `Path.GetTempPath` can return) | Every update refused as PathRejected on such machines | Accepted: the final-path check compares resolved paths, so the short form applies. Skipped with a recorded reason only when 8.3 names are disabled on the volume | `Staging_Path_8dot3_Applies` |
| S22 | The staging root is reached through a directory junction in a parent (TEMP relocated through a junction) | Every update refused as PathRejected | Accepted: both paths resolve to the same final path and the update applies. A junction as the staging root itself is still refused (section F) | `Staging_Via_Junction_Applies` |
| S23 | Between the folder open and the installer open, the installer name is redirected to another folder (swap) | A different file is started | Refused `PathRejected`: the installer's final path is not the held folder's final path plus `installer.exe` | `Installer_Outside_Held_Folder_Refused` |
