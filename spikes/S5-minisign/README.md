# S5: minisign envelope spike (Tauri updater format)

Question: can Node sign and .NET verify the Tauri updater's minisign envelope, rejecting every tampering case?

Answer: yes. 64 tests pass with 0 skipped and 0 build warnings. A Node-signed file verifies in .NET. The real Zinnia v0.6.6 release verifies with Zinnia's updater public key.

## Layout

- `node/minisign.mjs`: Node signer (Node built-ins only). Commands: `keygen`, `sign`.
- `dotnet/Bwc.Spike.Minisign`: verify-only library (`MinisignVerifier`), uses BouncyCastle.Cryptography 2.6.2.
- `dotnet/Bwc.Spike.Minisign.Tests`: MSTest suite. It drives the real Node CLI to make keys and signatures.
- `FAILURE-MODES.md`: every wrong-accept and wrong-reject case, with the test that covers it. Written before the verifier.

## Run

Needs the .NET SDK from `global.json`, `node` on PATH, and about 400 MB of free temp space (the 300 MB test file).

```
dotnet test spikes/S5-minisign/dotnet/Bwc.Spike.Minisign.Tests
```

About a minute. The Zinnia test downloads from GitHub. If GitHub is unreachable, it reports Inconclusive with a skip message.

Sign by hand:

```
node spikes/S5-minisign/node/minisign.mjs keygen <dir>
node spikes/S5-minisign/node/minisign.mjs sign <file> <dir> --trusted-comment "timestamp:1 file:x"
```

`sign` writes `<file>.minisig` (raw minisign text) and `<file>.sig` (base64 envelope, the Tauri form). `--legacy` writes an `Ed` packet; the tests use it, and the release flow should not.

## API

```csharp
VerificationResult MinisignVerifier.VerifyFile(string path, string signature, IReadOnlyList<string> trustedPublicKeys, bool allowLegacy = false);
VerificationResult MinisignVerifier.Verify(Stream file, string signature, IReadOnlyList<string> trustedPublicKeys, bool allowLegacy = false);
// VerificationResult(bool Ok, string? TrustedComment, string Failure)
```

- `Stream` instead of `ReadOnlySpan<byte>`: a span needs the whole file in memory. The stream is hashed in 64 KB chunks.
- `signature` is raw minisign text or the base64 envelope. `trustedPublicKeys` entries are minisign text or its base64 (the `tauri.conf.json` form).
- Bad input never throws; it returns `Ok = false` with a reason. Null arguments throw.
- Fail closed: one malformed trusted key rejects the whole call.

## Results

- `dotnet test`: 64 passed, 0 failed, 0 skipped (about 61 s, Windows 11 ARM64).
- Build: 0 warnings, 0 errors (`TreatWarningsAsErrors` is on).
- Real vector: `Zinnia-Windows-x64.exe` (v0.6.6, 4,811,896 bytes) from the manifest's `platforms.windows-x86_64` verifies with Zinnia's updater key. Trusted comment: `timestamp:1794102823`, tab, `file:Zinnia_0.6.6_x64-setup.exe`, tab, `version:0.6.6`. The signed file name differs from the asset name, but the bytes match. One flipped byte in that file is rejected.

## BCL finding

.NET 10 (runtime 10.0.14, SDK 10.0.403) has neither Ed25519 nor BLAKE2b:

- `System.Security.Cryptography` exports no Ed25519 type, and `Oid.FromFriendlyName("Ed25519", ...)` fails. The only "Ed25519" strings in the assembly are composite ML-DSA names.
- `IncrementalHash.CreateHash(new HashAlgorithmName("BLAKE2b-512"))` throws `CryptographicException`.

So the verifier uses BouncyCastle: `Ed25519PublicKeyParameters`, `Ed25519Signer`, `Ed25519.ValidatePublicKeyFull`, and `Blake2bDigest(512)`.

## Format decisions

- Strict parsing. Base64 must be canonical: no whitespace, and unused padding bits must be zero. One final newline is optional. CRLF is accepted (one CR per line is stripped). A stray CR and a UTF-8 BOM are refused.
- The key id only selects candidate keys. The signature must verify under a trusted key. The global signature is checked before the file is read.
- Legacy `Ed` is refused unless `allowLegacy` is set. Legacy buffers the whole file, because BouncyCastle's Ed25519 has no streaming verify.
- Key ids are compared as raw bytes. minisign prints the id byte-reversed (Zinnia comment `F9E00CF2F584CB6C`, packet bytes `6CCB84F5F20CE0F9`).

## Limits

- Spike keys are unencrypted and their secret-key file is not readable by the minisign CLI. The public key files are standard minisign text.
- Not run against the minisign CLI. Node output was cross-checked once by hand against Zinnia's `verifyMinisignBytes` in a scratch run, outside the suite.
- Not checked by the verifier (caller duties, see `FAILURE-MODES.md` section D): downgrade and replay, the contents of the trusted comment (`file:`, `version:`), and file replacement after verification.
- The Zinnia test is a canary. It will fail if Zinnia rotates its key or re-signs the release with a different key.
