# Update verifier failure modes

Scope: `Bwc.Update` (`BwcUpdate.exe`): the manifest and installer verification pipeline (check
mode), the receipt handoff, and the helper mode (`--apply`) that waits for the apps to exit and
runs the verified installer. The minisign layer is the verifier proven in
`spikes/S5-minisign/FAILURE-MODES.md`; this file covers what the update flow adds on top of it.

Written before the tests and before the code, per AGENTS.md ("FIRST write all the ways it could
fail, THEN write the code").

Terms:
- **WA** = wrongly accept (a bad manifest, installer, or request passes).
- **WR** = wrongly reject (a good update is refused).
- **Test** = the MSTest method in `tests/Bwc.Update.Tests` that covers the row. `Doc` means the
  behavior is a documented caller or operator duty, so no automated test exists.
- Every rejection is a typed `UpdateFailure` (one enum for check and apply) with a reason.
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
| MS8 | Trusted comment edited, signature kept | WA (the comment carries `version:`) | Reject | `Manifest_TrustedComment_Edit_Rejected` |
| MS9 | Legacy `Ed` algorithm on the manifest signature | WA: legacy signs raw bytes and is not prehashed | Reject (`allowLegacy` is false in production) | `Manifest_LegacyEd_Rejected` |
| MS10 | Unknown algorithm bytes (not `ED` or `Ed`) | WA if treated as legacy | Reject | `Manifest_UnknownAlgorithm_Rejected` |
| MS11 | Signature as raw minisign text, or as the base64 envelope | WR if one form is refused | Accept both | `Manifest_RawAndEnvelope_Both_Accepted` |
| MS12 | Signature with a BOM, stray CR, extra whitespace, wrong line count | WA if trimmed silently | Reject (spike rules B11 to B17) | `Manifest_Signature_Format_Rejected` |
| MS13 | Trusted key list is empty | WA if "no keys" means "accept" | Reject, fail closed | `Empty_Key_List_Rejected` |
| MS14 | Shipped placeholder public key (no real key yet) | WA if the placeholder verifies anything | Every manifest is refused, `TrustedKeysInvalid` | `Placeholder_Key_Fails_Closed` |
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
| MC19 | `pubDate` years in the past (local clock wrong, or an old manifest) | WR if freshness is enforced | Accept. Replay is stopped by the version rule (section D), not by time | `PubDate_Old_Accepted_By_Design` |
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
| UR6 | Encoded traversal or separators (`%2e%2e`, `%2F`, `%5C`, `%00`) | Traversal after decoding | Reject | `Url_Encoded_Traversal_Rejected` |
| UR7 | Backslash, space, or control character in the URL | Parser disagreement | Reject | `Url_Bad_Characters_Rejected` |
| UR8 | URL longer than 2048 characters | Log or buffer abuse | Reject | `Url_TooLong_Rejected` |
| UR9 | Redirect to a non-https URL while https is required | WA on the final hop | Reject the redirect before following it | `Redirect_To_Http_Rejected` |
| UR10 | Redirect to another host over https | WR if rejected; the signature is the authority | Accept | `Redirect_To_Other_Host_Accepted` |
| UR11 | More than 5 redirects | Loop | Reject `DownloadFailed` | `Redirect_Loop_Rejected` |
| UR12 | Local installer file name derived from the URL or the trusted comment | Traversal into the staging root | Never derived. The file is always `installer.exe` in a new folder | `Local_File_Name_Is_Fixed` |

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
| IN15 | Second attempt after a failure | Stale installer reused | Each attempt uses a new random folder | `Stale_Installer_Not_Reused` |
| IN16 | Cancellation during the download | Partial file kept | Reject `Canceled`, partial file deleted | `Cancellation_Removes_Partial_File` |
| IN17 | Staging root exists as a file (cannot create the folder) | Unhandled exception | Reject `Io` with the exception type | `Staging_Root_Is_File_Reported` |
| IN18 | No connection (refused on loopback) | Unhandled exception | Reject `DownloadFailed` | `Connection_Refused_Reported` |
| IN19 | Served body is an HTML error page with HTTP 200 | Saved as an installer | Hash mismatch, reject | `Html_Page_As_Installer_Rejected` |

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
| HT7 | Helper runs from the install folder, which the installer replaces | Installer cannot replace locked helper files | The helper copies its own folder to the staging area and runs the copy | `Helper_Relocates_Out_Of_Install_Dir` |
| HT8 | Relocated copy starts and would relocate again | Loop | The copy carries `--relocated` and does not relocate | `Relocated_Copy_Does_Not_Relocate_Again` |

## H. Helper: processes, install folder, and launch

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HL1 | A waited process is still alive at the timeout | Installer runs over live files | Refuse `ProcessesStillRunning`, installer not run, Hub not relaunched | `Apply_Still_Running_Refused` |
| HL2 | Waited processes exit before the timeout | WR | Installer runs | `Apply_Waits_Then_Installs` |
| HL3 | `--wait-pid` value not a positive integer | Ambiguous wait set | Usage error, exit code 2 | `Wait_Pid_Invalid_Usage` |
| HL4 | `--wait-pid` names a process that does not exist | Wait forever | Treated as exited | `Missing_Pid_Treated_As_Exited` |
| HL5 | A tool executable running from the install folder (found by process name) | Overwrite of a running app | Counted as running. An unreadable image is also counted (fail closed), but that branch is not tested | `Scan_Counts_Tool_Under_Install_Dir_As_Running` |
| HL6 | Install folder contains `"`, a control character, or a trailing backslash | Breaks the unquoted `/D=` argument | Refuse `InstallDirUnsafe` | `Install_Dir_Unsafe_Refused` |
| HL7 | Install folder is relative or does not exist | Installs to the wrong place | Refuse `InstallDirUnsafe` | `Install_Dir_Missing_Refused` |
| HL8 | Pass-through installer switch that is `/S`, `/D`, lowercase, has spaces or quotes, or has `=` | Overriding the silent or directory switches | Refuse `InstallerArgumentRejected`; allowed uppercase switches pass | `Installer_Arg_Validation` |
| HL9 | Command line for the installer | `/D=` must be last and unquoted | Exactly `/S <switches> /D=<install dir>`, with the directory unquoted | `Installer_Command_Line_Exact` |
| HL10 | Installer exits non-zero | Silent failure | `InstallerFailed` with the exit code; the Hub is still relaunched | `Installer_Failure_Still_Relaunches_Hub` |
| HL11 | Installer does not exit within its timeout | Hang | `InstallerTimedOut`; the installer is not killed | `Installer_Timeout_Reported` |
| HL12 | Hub executable missing after the installer ran | Crash | Reported as `HubMissing`, no exception | `Hub_Missing_Reported` |
| HL13 | Hub relaunch | Wrong executable or folder | Path is `<install dir>\BetterWindowsConfig.exe`, working folder is the install dir | `Hub_Relaunch_Uses_Install_Dir` |

## I. Command line and entry

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| CL1 | No arguments, or `--help` | Confusing exit | Usage text, exit code 0 | `Help_Lists_Modes` |
| CL2 | Unknown option | Silent ignore | Usage error, exit code 2 | `Unknown_Option_Usage` |
| CL3 | Option given twice | Last value wins silently | Usage error, exit code 2 | `Duplicate_Option_Usage` |
| CL4 | `--check` manifest URL is `http` | WA on the production path | Refused (production always requires https) | `Check_Http_Manifest_Refused` |
| CL5 | `--check` missing `--channel` or with an unknown channel | Default channel guessed | Usage error, exit code 2 | `Check_Channel_Usage` |

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
| NE4 | Freshness or expiry of a signed manifest | `Doc`. Not enforced (MC19). Replay is stopped by the version rule. |
| NE5 | Secrets in logs | `Doc`. Logs record host and path only, never query strings, keys, or signature text. |
| NE6 | File replaced after the helper's launch | `Doc`. The lock (HT4) covers the window up to process creation. The installer owns the file after that. |
| NE7 | Wrong-but-signed release content (a bad build signed by the real key) | `Doc`. Signing policy and release review are outside the verifier. |
