# S5 minisign verifier: failure modes

Written before the verifier, per AGENTS.md ("FIRST write all the ways it could fail").

Terms:
- **WA** = wrongly accept (a bad artifact or signature passes).
- **WR** = wrongly reject (a good artifact or signature fails).
- **Test** = the MSTest method in `dotnet/Bwc.Spike.Minisign.Tests` that covers the case. "Doc" means the case is a documented caller contract, not a verifier check, so no test.

Format rules the verifier enforces (fixed here, before code):
- Signature text is 4 lines: `untrusted comment:`, base64 packet, `trusted comment: `, base64 global signature.
- Packet = `ED` (prehashed, BLAKE2b-512 of the file, then Ed25519) or `Ed` (legacy, Ed25519 over the raw file), plus 8-byte key id, plus 64-byte signature. Total 74 bytes.
- Global signature = Ed25519 over (64-byte signature || trusted comment UTF-8 bytes), same key.
- Public key text = `untrusted comment:` line + base64 packet `Ed` + 8-byte key id + 32-byte key (42 bytes).
- Key ids are compared as raw bytes. The hex in the comment line is byte-reversed (minisign's display convention), so never compare hex text.
- Order of checks: key list, then signature format, then key-id filter, then global signature, then file hash, then prehash signature. The file is read only after the global signature passes.

## A. Payload (file bytes)

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| A1 | One byte changed (first, middle, last) | WA if hash is wrong or a range is skipped | Reject | `FileTamperedByte_Rejected` |
| A2 | Truncated file (last byte dropped) | WA if length is not hashed | Reject | `Truncated_File_Rejected` |
| A3 | Appended byte | WA if reader stops early | Reject | `Appended_Byte_Rejected` |
| A4 | Empty file signed as empty | WR if empty input is special-cased | Accept | `EmptyFile_SignedEmpty_Accepted` |
| A5 | Empty file checked against a signature of a non-empty file | WA | Reject | `EmptyFile_WrongSignature_Rejected` |
| A6 | 300 MB installer | WR if it times out; WA if the reader truncates; OOM if the whole file is buffered | Accept, and allocation stays bounded (the file is streamed) | `Huge_300MB_Verifies_With_Bounded_Allocation` |
| A7 | 300 MB installer with last byte flipped | WA if the streaming reader drops the tail | Reject | same test as A6 |
| A8 | Missing file or unreadable stream | Throw instead of Failure | `Ok = false` with a reason, no throw | `Missing_File_Returns_Failure`, `Unreadable_Stream_Returns_Failure` |
| A9 | Stream not at position 0 | Hash starts at the current position | Documented contract: caller passes a stream positioned at the start. The verifier does not seek. | Doc |
| A10 | File replaced after verification (TOCTOU) | Verified bytes differ from installed bytes | Not preventable in the verifier. Caller must verify the same bytes it installs (a private copy or the same handle). | Doc |

## B. Signature text and envelope

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| B1 | Signature from another file used for this file | WA | Reject | `Sig_From_Other_File_Rejected` |
| B2 | Prehash signature bytes tampered | WA | Reject | `Signature_Bytes_Tampered_Rejected` |
| B3 | Global signature bytes tampered | WA | Reject | `Global_Signature_Tampered_Rejected` |
| B4 | Trusted comment text changed, signature kept | WA (the comment is what callers trust) | Reject (global signature fails) | `TrustedComment_Tampered_Rejected` |
| B5 | Trusted comment with tabs, spaces, UTF-8 | WR if tabs or non-ASCII are rejected. The real Zinnia comment has tabs. | Accept, and return the exact text | `TrustedComment_Tabs_Unicode_RoundTrips` |
| B6 | Untrusted comment changed, signature kept | Caller shows it as trusted | Accept (the line is not signed, by design). The verifier returns only the trusted comment. | `UntrustedComment_Changed_StillVerifies` |
| B7 | Raw `.sig` text vs base64 envelope; `Stream` API vs `VerifyFile` | WR if one form is refused | Accept both | `Node_Signed_File_Verifies_Via_Envelope`, `Node_Signed_File_Verifies_Via_RawSigText`, `Stream_Api_Verifies_Same_As_File_Api` |
| B8 | Malformed base64 (illegal characters, bad length, misplaced `=`) | Throw, or accept garbage | Reject with a Failure reason, no throw | `Malformed_Base64_Rejected` |
| B9 | Non-canonical base64 (same bytes, different padding bits) | Text-level malleability: two strings for one signature | Reject (canonical round-trip check) | `NonCanonical_Base64_Rejected` |
| B10 | Envelope wrapped with line breaks (e.g. the `base64` tool's 76-column wrap) | WR if we strip whitespace; WA if we accept silently | Reject. Strict parse; no whitespace inside the envelope. | `Wrapped_Base64_Envelope_Rejected` |
| B11 | Wrong line count (3 lines, 5 lines, extra blank line) | WA if extra lines are ignored | Reject | `Wrong_Line_Count_Rejected` |
| B12 | No final newline after the last line | WR | Accept (exactly one final newline is optional) | `Missing_Final_Newline_Accepted` |
| B13 | CRLF line endings in raw `.sig` text (git autocrlf, Windows editors) | WR if CR is kept in the trusted comment (global signature fails) | Accept. One CR per line is stripped before the signed bytes are taken. | `CRLF_Raw_Sig_Accepted` |
| B14 | CRLF inside the base64 envelope | WR | Accept (the decoded text has CRLF, same rule as B13) | `CRLF_Envelope_Accepted` |
| B15 | Stray CR in the middle of a line | WA if a CR is hidden inside a field | Reject | `Extra_Whitespace_Rejected` |
| B16 | Extra whitespace: leading space or blank line before the header, space after a base64 line, space or tab inside the envelope | WA if trimmed silently; WR if a single trailing newline is refused | Reject, except one final newline (B12) | `Extra_Whitespace_Rejected` |
| B17 | UTF-8 byte-order mark before `untrusted comment:` | WR if BOM is stripped silently, WA if it is treated as part of the header | Reject. The parser never strips a BOM. How a `.sig` file is decoded is the caller's choice: `File.ReadAllText` drops a BOM, `Encoding.UTF8.GetString` keeps it, and the verifier then rejects it. | `Bom_Prefixed_Rejected` |
| B18 | Empty or whitespace-only signature | Throw | Reject | `Empty_Signature_Text_Rejected` |
| B19 | Packet length 73 or 75, global signature length 63 or 65 | WA (extra bytes ignored) or throw | Reject | `Signature_Length_Wrong_Rejected` |
| B20 | Unknown algorithm id (not `ED` or `Ed`) | WA if treated as legacy | Reject | `Unknown_Algorithm_Rejected` |
| B21 | Legacy `Ed` signature, default options | WA: legacy signs raw bytes and is not prehashed. We do not accept it unless asked. | Reject, with reason mentioning legacy | `Legacy_Ed_Rejected_By_Default` |
| B22 | Legacy `Ed` signature with `allowLegacy = true` | WR if the valid legacy signature is refused | Accept | `Legacy_Ed_Accepted_When_Allowed` |
| B23 | Tampered file with a legacy signature and `allowLegacy = true` | WA | Reject | `Legacy_Ed_Tampered_Rejected_When_Allowed` |
| B24 | Legacy buffering | Legacy needs the whole file in memory (Ed25519 has no streaming verify in BouncyCastle). Limit is about 2 GB. | Opt-in only. Documented in the README. | Doc |
| B25 | Trusted comment that is not valid UTF-8 | WR if decoded with replacement chars (then the bytes differ); WA if the string is used unchecked | Reject (strict decode). Needs a signer that writes non-UTF-8 comments, so not tested. | Doc |
| B26 | Arbitrary garbage text and random base64 (500 inputs) | Throw instead of Failure | Reject without throwing | `Garbage_Input_Never_Throws` |

## C. Key identity and trust

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| C1 | Signature by a key that is not trusted | WA | Reject | `Wrong_Key_Rejected` |
| C2 | Attacker key that carries the trusted key's 8-byte key id | WA if the key id is treated as the identity | Reject. The key id only filters candidates; the trusted key must verify both signatures. | `SameKeyId_WrongKey_Rejected` |
| C3 | Trusted key signed, but the packet key id is changed | WA if the key id is not checked | Reject | `WrongKeyId_WithRightKey_Rejected` |
| C4 | Two trusted keys share a key id, the second signed | WR if only the first candidate is tried | Accept (try each candidate) | `KeyId_Collision_Between_Trusted_Keys_Still_Accepts` |
| C5 | Rotation: two trusted keys, the second signed | WR | Accept, and return the trusted comment | `Rotation_Second_Key_Signed_Accepted` |
| C6 | Rotation: a third key not in the list signed | WA | Reject | `Rotation_Unlisted_Key_Rejected` |
| C7 | Empty trusted-key list | WA if "no keys" means "accept anything" | Reject (fail closed) | `Empty_Trusted_Key_List_Rejected` |
| C8 | One malformed key in the list, another valid one signed | WA if the bad key is skipped silently | Reject the whole call (fail closed). A config error must not pass quietly. | `Malformed_Trusted_Key_Fails_Closed` |
| C9 | Trusted key as raw minisign text, not base64 | WR | Accept both forms | `Trusted_Key_Raw_And_Base64_Both_Accepted` |
| C10 | Public-key packet alg `ED` (not `Ed`), or a point not on the curve | WA if the key is treated as valid, WR if a real key is refused | Reject at parse time | `Malformed_Trusted_Key_Fails_Closed` |
| C11 | Real Zinnia release (key from `tauri.conf.json`, manifest sig) | WR if the byte-order of the key id is wrong; WA if a flipped byte passes | Verifies, and the same file with one flipped byte is rejected | `Zinnia_Release_Artifact_Verifies` |

## D. Not enforced by the verifier (caller contract)

| ID | Case | Note |
|----|------|------|
| D1 | Replay or downgrade: an old, valid release still verifies | The verifier cannot see time or versions. The updater must refuse versions lower than installed, using the signed version data. |
| D2 | The trusted comment is authenticated but its contents are not checked | Callers must check the `file:` and `version:` fields against what they expect. Example: the Zinnia comment names `Zinnia_0.6.6_x64-setup.exe`, but the manifest URL points to `Zinnia-Windows-x64.exe`. Naming policy is a caller decision. |
| D3 | The untrusted comment line | Never display it as authenticated (B6). |
