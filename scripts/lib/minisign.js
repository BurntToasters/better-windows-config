// Minisign-format keys and prehashed signatures for the release tooling.
//
// Signatures use the prehashed ED algorithm: Ed25519 over BLAKE2b-512(file), with a global
// signature over the signature bytes and the trusted comment. Public keys are the standard
// 42-byte minisign packet ("Ed", key id, Ed25519 public key).
//
// Secret keys come in two forms:
//   - Test keys written by this repository's keygen: a 42-byte packet with the 32-byte seed,
//     unencrypted. Dry runs only. The release tooling never trusts a key because of its comment.
//   - Minisign secret key files: a 158-byte packet ("Ed", kdf, checksum algorithm, salt,
//     opslimit, memlimit, and keynum_sk = key id, 64-byte secret key, 32-byte checksum). kdf "Sc"
//     derives a 32-byte key with scrypt and encrypts keynum_sk with XSalsa20 (zero nonce);
//     kdf "\0\0" is unencrypted. The checksum is BLAKE2b-256 over "Ed" || key id || secret key.
//
// The encrypted layout was written from the minisign format description and is checked here
// only against keys this module creates. It has not been checked against a key that the minisign
// program created on this machine. docs/RELEASE.md says so, and the first real-key test is part
// of the user's release checklist.
import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, randomBytes, scryptSync, sign, verify } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const ED25519_SPKI_PREFIX = Buffer.from("302a300506032b6570032100", "hex");
const ED25519_PKCS8_PREFIX = Buffer.from("302e020100300506032b657004220420", "hex");
const UNTRUSTED_SIGNATURE_COMMENT = "untrusted comment: signature from minisign secret key";
const DEFAULT_SECRET_COMMENT = "untrusted comment: BWC test secret key (unencrypted, never publish)";
const PUBLIC_KEY_PACKET_LENGTH = 42;
const LEGACY_SECRET_PACKET_LENGTH = 42;
const SECRET_PACKET_LENGTH = 158;
const KEYNUM_SK_LENGTH = 104;
const SIGNATURE_PACKET_LENGTH = 74;
const SIGNATURE_LENGTH = 64;
const TRUSTED_COMMENT_PREFIX = "trusted comment: ";
const PLACEHOLDER = /PLACEHOLDER/i;
const TEST_KEY_MARKER = /TEST KEY/i;
const MINISIGN_OPS_SENSITIVE = 33554432;
const MINISIGN_MEM_SENSITIVE = 1073741824;
const TEST_OPS = 32768;
const TEST_MEM = 8 * 1024 * 1024;
const SCRYPT_MAXMEM = 1610612736;

function failure(reason, message, exitCode = 1) {
  const error = new Error(message);
  error.reason = reason;
  error.exitCode = exitCode;
  return error;
}

function displayKeyId(keyId) {
  return Buffer.from(keyId).reverse().toString("hex").toUpperCase();
}

function strictUtf8(bytes, what) {
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    throw new Error(`${what} is not valid UTF-8`);
  }
}

function splitDocumentLines(text, what) {
  let normalized = text;
  if (normalized.endsWith("\r\n")) {
    normalized = normalized.slice(0, -2);
  } else if (normalized.endsWith("\n")) {
    normalized = normalized.slice(0, -1);
  }
  if (normalized.includes("\r")) {
    throw new Error(`${what} has a stray carriage return`);
  }
  return normalized.split("\n");
}

function decodeCanonicalBase64(text, what) {
  if (typeof text !== "string" || text.length === 0 || text.length % 4 !== 0) {
    throw new Error(`${what} is not base64`);
  }
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(text)) {
    throw new Error(`${what} contains a character outside base64`);
  }
  const bytes = Buffer.from(text, "base64");
  if (bytes.toString("base64") !== text) {
    throw new Error(`${what} is not canonical base64`);
  }
  return bytes;
}

// ---------------------------------------------------------------------------------------------
// BLAKE2b (RFC 7693), unkeyed, any output length 1..64. Node's built-in blake2b512 cannot
// produce a 256-bit digest, and minisign's secret key checksum is BLAKE2b-256. The module's
// self-check (in the selftest) compares the 512-bit output with Node's built-in digest.
// ---------------------------------------------------------------------------------------------

const B2_IV = [
  0x6a09e667f3bcc908n, 0xbb67ae8584caa73bn, 0x3c6ef372fe94f82bn, 0xa54ff53a5f1d36f1n,
  0x510e527fade682d1n, 0x9b05688c2b3e6c1fn, 0x1f83d9abfb41bd6bn, 0x5be0cd19137e2179n,
];
const B2_SIGMA = [
  [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
  [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
  [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4],
  [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
  [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13],
  [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
  [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11],
  [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
  [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5],
  [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0],
];
const MASK64 = (1n << 64n) - 1n;

function rotr64(x, n) {
  return ((x >> BigInt(n)) | (x << BigInt(64 - n))) & MASK64;
}

function b2Compress(h, block, t, last) {
  const m = [];
  for (let i = 0; i < 16; i += 1) {
    m.push(block.readBigUInt64LE(i * 8));
  }
  const v = [...h, ...B2_IV];
  v[12] ^= BigInt(t) & MASK64;
  v[13] ^= (BigInt(t) >> 64n) & MASK64;
  if (last) v[14] ^= MASK64;
  const g = (a, b, c, d, x, y) => {
    v[a] = (v[a] + v[b] + x) & MASK64;
    v[d] = rotr64(v[d] ^ v[a], 32);
    v[c] = (v[c] + v[d]) & MASK64;
    v[b] = rotr64(v[b] ^ v[c], 24);
    v[a] = (v[a] + v[b] + y) & MASK64;
    v[d] = rotr64(v[d] ^ v[a], 16);
    v[c] = (v[c] + v[d]) & MASK64;
    v[b] = rotr64(v[b] ^ v[c], 63);
  };
  for (let round = 0; round < 12; round += 1) {
    const s = B2_SIGMA[round % 10];
    g(0, 4, 8, 12, m[s[0]], m[s[1]]);
    g(1, 5, 9, 13, m[s[2]], m[s[3]]);
    g(2, 6, 10, 14, m[s[4]], m[s[5]]);
    g(3, 7, 11, 15, m[s[6]], m[s[7]]);
    g(0, 5, 10, 15, m[s[8]], m[s[9]]);
    g(1, 6, 11, 12, m[s[10]], m[s[11]]);
    g(2, 7, 8, 13, m[s[12]], m[s[13]]);
    g(3, 4, 9, 14, m[s[14]], m[s[15]]);
  }
  for (let i = 0; i < 8; i += 1) {
    h[i] ^= v[i] ^ v[i + 8];
  }
}

export function blake2b(input, outLength) {
  if (!Number.isInteger(outLength) || outLength < 1 || outLength > 64) {
    throw new Error("BLAKE2b output length must be 1 to 64 bytes");
  }
  const h = B2_IV.slice();
  h[0] ^= BigInt(0x01010000 | outLength);
  const data = Buffer.from(input);
  let offset = 0;
  if (data.length === 0) {
    b2Compress(h, Buffer.alloc(128), 0, true);
  } else {
    while (data.length - offset > 128) {
      b2Compress(h, data.subarray(offset, offset + 128), offset + 128, false);
      offset += 128;
    }
    const tail = Buffer.alloc(128);
    data.copy(tail, 0, offset);
    b2Compress(h, tail, data.length, true);
  }
  const out = Buffer.alloc(64);
  for (let i = 0; i < 8; i += 1) {
    out.writeBigUInt64LE(h[i], i * 8);
  }
  return out.subarray(0, outLength);
}

// ---------------------------------------------------------------------------------------------
// Salsa20 / XSalsa20 keystream (for the encrypted secret key layout only).
// ---------------------------------------------------------------------------------------------

function rotl32(value, count) {
  return ((value << count) | (value >>> (32 - count))) >>> 0;
}

function salsaRounds(x) {
  for (let round = 0; round < 10; round += 1) {
    x[4] ^= rotl32((x[0] + x[12]) >>> 0, 7);
    x[8] ^= rotl32((x[4] + x[0]) >>> 0, 9);
    x[12] ^= rotl32((x[8] + x[4]) >>> 0, 13);
    x[0] ^= rotl32((x[12] + x[8]) >>> 0, 18);
    x[9] ^= rotl32((x[5] + x[1]) >>> 0, 7);
    x[13] ^= rotl32((x[9] + x[5]) >>> 0, 9);
    x[1] ^= rotl32((x[13] + x[9]) >>> 0, 13);
    x[5] ^= rotl32((x[1] + x[13]) >>> 0, 18);
    x[14] ^= rotl32((x[10] + x[6]) >>> 0, 7);
    x[2] ^= rotl32((x[14] + x[10]) >>> 0, 9);
    x[6] ^= rotl32((x[2] + x[14]) >>> 0, 13);
    x[10] ^= rotl32((x[6] + x[2]) >>> 0, 18);
    x[3] ^= rotl32((x[15] + x[11]) >>> 0, 7);
    x[7] ^= rotl32((x[3] + x[15]) >>> 0, 9);
    x[11] ^= rotl32((x[7] + x[3]) >>> 0, 13);
    x[15] ^= rotl32((x[11] + x[7]) >>> 0, 18);
    x[1] ^= rotl32((x[0] + x[3]) >>> 0, 7);
    x[2] ^= rotl32((x[1] + x[0]) >>> 0, 9);
    x[3] ^= rotl32((x[2] + x[1]) >>> 0, 13);
    x[0] ^= rotl32((x[3] + x[2]) >>> 0, 18);
    x[6] ^= rotl32((x[5] + x[4]) >>> 0, 7);
    x[7] ^= rotl32((x[6] + x[5]) >>> 0, 9);
    x[4] ^= rotl32((x[7] + x[6]) >>> 0, 13);
    x[5] ^= rotl32((x[4] + x[7]) >>> 0, 18);
    x[11] ^= rotl32((x[10] + x[9]) >>> 0, 7);
    x[8] ^= rotl32((x[11] + x[10]) >>> 0, 9);
    x[9] ^= rotl32((x[8] + x[11]) >>> 0, 13);
    x[10] ^= rotl32((x[9] + x[8]) >>> 0, 18);
    x[12] ^= rotl32((x[15] + x[14]) >>> 0, 7);
    x[13] ^= rotl32((x[12] + x[15]) >>> 0, 9);
    x[14] ^= rotl32((x[13] + x[12]) >>> 0, 13);
    x[15] ^= rotl32((x[14] + x[13]) >>> 0, 18);
  }
}

const SALSA_CONSTANTS = [0x61707865, 0x3320646e, 0x79622d32, 0x6b206574];

function salsaState(key, nonce8, counter) {
  const x = new Array(16).fill(0);
  x[0] = SALSA_CONSTANTS[0];
  x[5] = SALSA_CONSTANTS[1];
  x[10] = SALSA_CONSTANTS[2];
  x[15] = SALSA_CONSTANTS[3];
  for (let i = 0; i < 4; i += 1) {
    x[1 + i] = key.readUInt32LE(i * 4);
    x[11 + i] = key.readUInt32LE(16 + i * 4);
  }
  x[6] = nonce8.readUInt32LE(0);
  x[7] = nonce8.readUInt32LE(4);
  x[8] = counter >>> 0;
  x[9] = Math.floor(counter / 0x100000000) >>> 0;
  return x;
}

function hsalsa20(key, nonce16) {
  const x = new Array(16).fill(0);
  x[0] = SALSA_CONSTANTS[0];
  x[5] = SALSA_CONSTANTS[1];
  x[10] = SALSA_CONSTANTS[2];
  x[15] = SALSA_CONSTANTS[3];
  for (let i = 0; i < 4; i += 1) {
    x[1 + i] = key.readUInt32LE(i * 4);
    x[11 + i] = key.readUInt32LE(16 + i * 4);
  }
  x[6] = nonce16.readUInt32LE(0);
  x[7] = nonce16.readUInt32LE(4);
  x[8] = nonce16.readUInt32LE(8);
  x[9] = nonce16.readUInt32LE(12);
  salsaRounds(x);
  const out = Buffer.alloc(32);
  [x[0], x[5], x[10], x[15], x[6], x[7], x[8], x[9]].forEach((word, index) => out.writeUInt32LE(word >>> 0, index * 4));
  return out;
}

/** XOR of data with the XSalsa20 keystream for key (32 bytes) and nonce (24 bytes). */
export function xsalsa20Xor(data, key, nonce) {
  if (key.length !== 32 || nonce.length !== 24) {
    throw new Error("XSalsa20 needs a 32-byte key and a 24-byte nonce");
  }
  const subkey = hsalsa20(key, nonce.subarray(0, 16));
  const nonce8 = nonce.subarray(16, 24);
  const out = Buffer.alloc(data.length);
  for (let block = 0; block * 64 < data.length; block += 1) {
    const x = salsaState(subkey, nonce8, block);
    const input = x.slice();
    salsaRounds(x);
    const stream = Buffer.alloc(64);
    for (let i = 0; i < 16; i += 1) {
      stream.writeUInt32LE((x[i] + input[i]) >>> 0, i * 4);
    }
    for (let i = 0; i < 64 && block * 64 + i < data.length; i += 1) {
      out[block * 64 + i] = data[block * 64 + i] ^ stream[i];
    }
  }
  return out;
}

/** Salsa20/20 keystream with an 8-byte nonce, exposed for the self-check against a known vector. */
export function salsa20Keystream(key, nonce8, length) {
  const out = Buffer.alloc(length);
  for (let block = 0; block * 64 < length; block += 1) {
    const x = salsaState(key, nonce8, block);
    const input = x.slice();
    salsaRounds(x);
    for (let i = 0; i < 16; i += 1) {
      const word = (x[i] + input[i]) >>> 0;
      for (let b = 0; b < 4; b += 1) {
        const at = block * 64 + i * 4 + b;
        if (at < length) out[at] = (word >>> (8 * b)) & 0xff;
      }
    }
  }
  return out;
}

// ---------------------------------------------------------------------------------------------
// Key derivation for minisign's scrypt kdf. The parameters are picked from opslimit and memlimit
// the way libsodium's crypto_pwhash_scryptsalsa208sha256 does (r = 8).
// ---------------------------------------------------------------------------------------------

export function scryptParamsFor(opslimit, memlimit) {
  const ops = Math.max(Number(opslimit), 32768);
  const mem = Number(memlimit);
  const r = 8;
  let p;
  let nLog2;
  if (ops < mem / 32) {
    p = 1;
    const maxN = Math.floor(ops / (r * 4));
    for (nLog2 = 1; nLog2 < 63; nLog2 += 1) {
      if (2 ** nLog2 > Math.floor(maxN / 2)) break;
    }
  } else {
    const maxN = Math.floor(mem / (r * 128));
    for (nLog2 = 1; nLog2 < 63; nLog2 += 1) {
      if (2 ** nLog2 > Math.floor(maxN / 2)) break;
    }
    const maxRp = Math.floor(ops / 4 / 2 ** nLog2);
    p = Math.max(1, Math.floor(maxRp / r));
  }
  return { N: 2 ** nLog2, r, p };
}

/**
 * The keynum_sk mask, exactly as minisign (C and Rust, which Tauri uses) derives it: scrypt writes as many bytes
 * as keynum_sk has (104), and the mask is XORed straight over keynum_sk. There is no stream cipher step.
 */
function deriveKeynumMask(password, salt, opslimit, memlimit) {
  const { N, r, p } = scryptParamsFor(opslimit, memlimit);
  const memoryBytes = 128 * N * r;
  if (memoryBytes > SCRYPT_MAXMEM) {
    throw failure("unsupported-key-kdf", "the key's scrypt memory cost is above the 1.5 GiB limit", 1);
  }
  return scryptSync(Buffer.from(password, "utf8"), salt, KEYNUM_SK_LENGTH, { N, r, p, maxmem: SCRYPT_MAXMEM });
}

function xorBuffers(data, mask) {
  const out = Buffer.alloc(data.length);
  for (let index = 0; index < data.length; index += 1) out[index] = data[index] ^ mask[index];
  return out;
}

// ---------------------------------------------------------------------------------------------
// Public keys and key text.
// ---------------------------------------------------------------------------------------------

export function isPlaceholderKeyText(text) {
  return typeof text === "string" && PLACEHOLDER.test(text);
}

/** Display only. Release decisions never use this; they compare key bytes with the embedded list. */
export function isTestKeyText(text) {
  return typeof text === "string" && TEST_KEY_MARKER.test(text);
}

export function publicKeyFingerprint(publicKeyText) {
  return `sha256:${createHash("sha256").update(publicKeyText, "utf8").digest("hex")}`;
}

export function parsePublicKeyText(text) {
  if (typeof text !== "string" || text.length === 0) {
    return { ok: false, reason: "malformed-key", detail: "public key text is empty" };
  }
  if (isPlaceholderKeyText(text)) {
    return { ok: false, reason: "placeholder-key", detail: "public key text is a placeholder" };
  }

  try {
    const lines = splitDocumentLines(text, "public key");
    if (lines.length !== 2) {
      throw new Error(`public key must have 2 lines, found ${lines.length}`);
    }
    if (!lines[0].startsWith("untrusted comment:")) {
      throw new Error("public key line 1 must start with 'untrusted comment:'");
    }
    const packet = decodeCanonicalBase64(lines[1], "public key packet");
    if (packet.length !== PUBLIC_KEY_PACKET_LENGTH) {
      throw new Error(`public key packet must be ${PUBLIC_KEY_PACKET_LENGTH} bytes, got ${packet.length}`);
    }
    if (packet.subarray(0, 2).toString() !== "Ed") {
      throw new Error("public key algorithm must be Ed");
    }
    const keyId = packet.subarray(2, 10);
    const publicBytes = Buffer.from(packet.subarray(10, 42));
    let keyObject;
    try {
      keyObject = createPublicKey({
        key: Buffer.concat([ED25519_SPKI_PREFIX, publicBytes]),
        format: "der",
        type: "spki",
      });
    } catch {
      throw new Error("public key is not a valid Ed25519 point");
    }
    return {
      ok: true,
      keyId,
      keyIdHex: displayKeyId(keyId),
      publicBytes,
      keyObject,
      publicKeyText: text,
      fingerprint: publicKeyFingerprint(text),
    };
  } catch (error) {
    return {
      ok: false,
      reason: "malformed-key",
      detail: error instanceof Error ? error.message : String(error),
    };
  }
}

/** True when two parsed public keys have the same key id and the same Ed25519 key bytes. */
export function samePublicKey(left, right) {
  return Boolean(
    left && right && left.ok && right.ok &&
      Buffer.from(left.keyId).equals(Buffer.from(right.keyId)) &&
      Buffer.from(left.publicBytes).equals(Buffer.from(right.publicBytes)),
  );
}

/**
 * Reads a secret key file. Returns { keyId, keyObject, publicBytes, encrypted }. A password is
 * needed for encrypted keys: without one the error reason is password-missing (exit 2), and with
 * a wrong one it is wrong-password (exit 1). The password is never logged.
 */
export function parseSecretKeyText(text, { password } = {}) {
  if (typeof text !== "string" || text.length === 0) {
    throw failure("malformed-secret-key", "secret key text is empty");
  }
  if (isPlaceholderKeyText(text)) {
    throw failure("placeholder-key", "secret key text is a placeholder");
  }
  const lines = splitDocumentLines(text, "secret key");
  if (lines.length !== 2) {
    throw failure("malformed-secret-key", `secret key must have 2 lines, found ${lines.length}`);
  }
  if (!lines[0].startsWith("untrusted comment:")) {
    throw failure("malformed-secret-key", "secret key line 1 must start with 'untrusted comment:'");
  }
  let packet;
  try {
    packet = decodeCanonicalBase64(lines[1], "secret key packet");
  } catch (error) {
    throw failure("malformed-secret-key", error.message);
  }
  if (packet.length < 2 || packet.subarray(0, 2).toString() !== "Ed") {
    throw failure("malformed-secret-key", "secret key algorithm must be Ed");
  }

  if (packet.length === LEGACY_SECRET_PACKET_LENGTH) {
    const seed = packet.subarray(10, 42);
    const keyId = Buffer.from(packet.subarray(2, 10));
    const keyObject = createPrivateKey({
      key: Buffer.concat([ED25519_PKCS8_PREFIX, seed]),
      format: "der",
      type: "pkcs8",
    });
    return { keyId, keyObject, publicBytes: publicBytesOf(keyObject), encrypted: false };
  }

  if (packet.length !== SECRET_PACKET_LENGTH) {
    throw failure("malformed-secret-key", `secret key packet is ${packet.length} bytes; expected 42 or ${SECRET_PACKET_LENGTH}`);
  }
  const kdf = packet.subarray(2, 4).toString("latin1");
  const checksumAlgorithm = packet.subarray(4, 6).toString("latin1");
  if (checksumAlgorithm !== "B2") {
    throw failure("unsupported-key-kdf", `secret key checksum algorithm ${JSON.stringify(checksumAlgorithm)} is not B2`);
  }
  const salt = packet.subarray(6, 38);
  const opslimit = Number(packet.readBigUInt64LE(38));
  const memlimit = Number(packet.readBigUInt64LE(46));
  const body = Buffer.from(packet.subarray(54, 54 + KEYNUM_SK_LENGTH));

  let plain;
  let encrypted;
  if (kdf === "\0\0") {
    plain = body;
    encrypted = false;
  } else if (kdf === "Sc") {
    encrypted = true;
    if (typeof password !== "string" || password.length === 0) {
      throw failure("password-missing", "this secret key is encrypted; set the password environment variable", 2);
    }
    if (!(opslimit >= 32768 && memlimit >= 1024 * 1024)) {
      throw failure("unsupported-key-kdf", "encrypted key has scrypt parameters outside the accepted range");
    }
    plain = xorBuffers(body, deriveKeynumMask(password, salt, opslimit, memlimit));
  } else {
    throw failure("unsupported-key-kdf", `secret key kdf ${JSON.stringify(kdf)} is not supported`);
  }

  const keynum = plain.subarray(0, 8);
  const secret = plain.subarray(8, 72);
  const checksum = plain.subarray(72, 104);
  const expected = blake2b(Buffer.concat([Buffer.from("Ed"), keynum, secret]), 32);
  if (!expected.equals(checksum)) {
    throw failure(
      encrypted ? "wrong-password" : "malformed-secret-key",
      encrypted
        ? "the password is wrong, or the key checksum does not match"
        : "the secret key checksum does not match",
    );
  }
  const keyObject = createPrivateKey({
    key: Buffer.concat([ED25519_PKCS8_PREFIX, secret.subarray(0, 32)]),
    format: "der",
    type: "pkcs8",
  });
  const publicBytes = publicBytesOf(keyObject);
  if (!publicBytes.equals(secret.subarray(32, 64))) {
    throw failure("malformed-secret-key", "the secret key's public half does not match its seed");
  }
  return { keyId: Buffer.from(keynum), keyObject, publicBytes, encrypted };
}

/** True for an encrypted minisign secret key (kdf "Sc"). Reads only the packet header. */
export function secretKeyNeedsPassword(text) {
  if (typeof text !== "string") return false;
  const lines = text.replace(/\r?\n$/, "").split("\n");
  if (lines.length !== 2) return false;
  try {
    const packet = decodeCanonicalBase64(lines[1], "secret key packet");
    return packet.length === SECRET_PACKET_LENGTH && packet.subarray(2, 4).toString("latin1") === "Sc";
  } catch {
    return false;
  }
}

function publicBytesOf(keyObject) {
  return Buffer.from(createPublicKey(keyObject).export({ format: "der", type: "spki" }).subarray(-32));
}

/** Encrypts a secret key in the 158-byte minisign layout. Used for keys this tool creates. */
export function encryptSecretKeyText({ keyId, seed, publicBytes, password, opslimit, memlimit, comment }) {
  if (typeof password !== "string" || password.length === 0) {
    throw failure("password-missing", "encrypting a key needs a password", 2);
  }
  const salt = randomBytes(32);
  const mask = deriveKeynumMask(password, salt, opslimit, memlimit);
  const secret = Buffer.concat([seed, publicBytes]);
  const plain = Buffer.concat([
    Buffer.from(keyId),
    secret,
    blake2b(Buffer.concat([Buffer.from("Ed"), keyId, secret]), 32),
  ]);
  const body = xorBuffers(plain, mask);
  const opsBytes = Buffer.alloc(8);
  opsBytes.writeBigUInt64LE(BigInt(opslimit));
  const memBytes = Buffer.alloc(8);
  memBytes.writeBigUInt64LE(BigInt(memlimit));
  const packet = Buffer.concat([Buffer.from("Ed"), Buffer.from("Sc"), Buffer.from("B2"), salt, opsBytes, memBytes, body]);
  return `${comment}\n${packet.toString("base64")}\n`;
}

export function generateKeyPair({ comment, password, kdfProfile = "sensitive" } = {}) {
  const { publicKey, privateKey } = generateKeyPairSync("ed25519");
  const keyId = randomBytes(8);
  const publicBytes = publicKey.export({ format: "der", type: "spki" }).subarray(-32);
  const seed = privateKey.export({ format: "der", type: "pkcs8" }).subarray(-32);
  const keyIdHex = displayKeyId(keyId);
  const publicComment = comment ?? `untrusted comment: BWC TEST KEY ${keyIdHex}`;
  const publicKeyText =
    `${publicComment}\n` +
    `${Buffer.concat([Buffer.from("Ed"), keyId, publicBytes]).toString("base64")}\n`;
  let secretKeyText;
  if (password !== undefined) {
    const kdf = kdfProfile === "test"
      ? { opslimit: TEST_OPS, memlimit: TEST_MEM }
      : { opslimit: MINISIGN_OPS_SENSITIVE, memlimit: MINISIGN_MEM_SENSITIVE };
    secretKeyText = encryptSecretKeyText({
      keyId,
      seed,
      publicBytes,
      password,
      ...kdf,
      comment: "untrusted comment: minisign encrypted secret key",
    });
  } else {
    secretKeyText =
      `${DEFAULT_SECRET_COMMENT}\n` +
      `${Buffer.concat([Buffer.from("Ed"), keyId, seed]).toString("base64")}\n`;
  }
  return {
    keyId,
    keyIdHex,
    publicKeyText,
    secretKeyText,
    encrypted: password !== undefined,
    fingerprint: publicKeyFingerprint(publicKeyText),
  };
}

export function envelope(signatureText) {
  return Buffer.from(signatureText, "utf8").toString("base64");
}

export function parseSignatureText(signature, { allowLegacy = false } = {}) {
  let document;
  try {
    if (typeof signature !== "string" || signature.length === 0) {
      throw new Error("signature is empty");
    }
    if (signature.startsWith("untrusted comment:")) {
      document = signature;
    } else {
      const trimmed = signature.endsWith("\r\n")
        ? signature.slice(0, -2)
        : signature.endsWith("\n")
          ? signature.slice(0, -1)
          : signature;
      document = strictUtf8(decodeCanonicalBase64(trimmed, "signature envelope"), "signature envelope");
      if (!document.startsWith("untrusted comment:")) {
        throw new Error("signature is not a minisign document");
      }
    }

    const lines = splitDocumentLines(document, "signature");
    if (lines.length !== 4) {
      throw new Error(`signature must have 4 lines, found ${lines.length}`);
    }
    if (!lines[0].startsWith("untrusted comment:")) {
      throw new Error("signature line 1 must start with 'untrusted comment:'");
    }
    const packet = decodeCanonicalBase64(lines[1], "signature packet");
    if (packet.length !== SIGNATURE_PACKET_LENGTH) {
      throw new Error(`signature packet must be ${SIGNATURE_PACKET_LENGTH} bytes, got ${packet.length}`);
    }

    let legacy;
    const algorithm = packet.subarray(0, 2).toString();
    if (algorithm === "ED") {
      legacy = false;
    } else if (algorithm === "Ed") {
      legacy = true;
      if (!allowLegacy) {
        throw new Error("legacy Ed signatures are refused");
      }
    } else {
      throw new Error("unsupported signature algorithm (expected ED or Ed)");
    }

    if (!lines[2].startsWith(TRUSTED_COMMENT_PREFIX)) {
      throw new Error("signature line 3 must start with 'trusted comment: '");
    }
    const trustedCommentBytes = Buffer.from(lines[2].slice(TRUSTED_COMMENT_PREFIX.length), "utf8");
    const trustedComment = strictUtf8(trustedCommentBytes, "trusted comment");
    const globalSignature = decodeCanonicalBase64(lines[3], "global signature");
    if (globalSignature.length !== SIGNATURE_LENGTH) {
      throw new Error(`global signature must be ${SIGNATURE_LENGTH} bytes, got ${globalSignature.length}`);
    }

    return {
      ok: true,
      legacy,
      keyId: packet.subarray(2, 10),
      keyIdHex: displayKeyId(packet.subarray(2, 10)),
      signature: packet.subarray(10, 74),
      trustedComment,
      trustedCommentBytes,
      globalSignature,
    };
  } catch (error) {
    return {
      ok: false,
      reason: "signature-format",
      detail: error instanceof Error ? error.message : String(error),
    };
  }
}

export function parseTrustedComment(comment) {
  if (typeof comment !== "string" || comment.length > 1024) {
    return { ok: false, detail: "trusted comment is too long" };
  }
  const fields = new Map();
  for (const token of comment.split(/[ \t]+/)) {
    if (token === "") continue;
    const colon = token.indexOf(":");
    if (colon <= 0 || colon === token.length - 1) {
      return { ok: false, detail: "trusted comment field is not key:value" };
    }
    const key = token.slice(0, colon);
    const value = token.slice(colon + 1);
    if (!/^[a-z]+$/.test(key)) {
      return { ok: false, detail: "trusted comment key is not lowercase letters" };
    }
    if (fields.has(key)) {
      return { ok: false, detail: `trusted comment repeats the key '${key}'` };
    }
    fields.set(key, value);
  }
  return { ok: true, fields };
}

export function isPlainFileName(value) {
  if (typeof value !== "string" || value.length === 0 || value.length > 255 || value === "." || value === "..") {
    return false;
  }
  for (const character of value) {
    const code = character.codePointAt(0);
    if (code < 0x20 || '/\\:"<>|*?'.includes(character)) {
      return false;
    }
  }
  return /^[^/\\]+$/.test(value);
}

export async function blake2b512File(filePath) {
  const hash = createHash("blake2b512");
  for await (const chunk of fs.createReadStream(filePath, { highWaterMark: 1 << 20 })) {
    hash.update(chunk);
  }
  return hash.digest();
}

export async function signFile(filePath, secretKeyText, trustedComment, { legacy = false, password } = {}) {
  if (/[\r\n]/.test(trustedComment)) {
    throw failure("trusted-comment-invalid", "trusted comment must be a single line");
  }
  const { keyId, keyObject } = parseSecretKeyText(secretKeyText, { password });
  const message = legacy ? fs.readFileSync(filePath) : await blake2b512File(filePath);
  const signature = sign(null, message, keyObject);
  const globalSignature = sign(
    null,
    Buffer.concat([signature, Buffer.from(trustedComment, "utf8")]),
    keyObject,
  );
  return (
    `${UNTRUSTED_SIGNATURE_COMMENT}\n` +
    `${Buffer.concat([Buffer.from(legacy ? "Ed" : "ED"), keyId, signature]).toString("base64")}\n` +
    `${TRUSTED_COMMENT_PREFIX}${trustedComment}\n` +
    `${globalSignature.toString("base64")}\n`
  );
}

function verifyMessage(keys, parsed, message) {
  const candidates = keys.filter((key) => key.keyId.equals(parsed.keyId));
  if (candidates.length === 0) {
    return {
      ok: false,
      reason: "wrong-key",
      detail: `signature key id ${parsed.keyIdHex} does not match any trusted key`,
    };
  }

  const globalSigners = candidates.filter((key) =>
    verify(
      null,
      Buffer.concat([parsed.signature, parsed.trustedCommentBytes]),
      key.keyObject,
      parsed.globalSignature,
    ),
  );
  if (globalSigners.length === 0) {
    return {
      ok: false,
      reason: "signature-invalid",
      detail: "global signature does not verify: the trusted comment or signature was altered",
    };
  }

  for (const key of globalSigners) {
    if (verify(null, message, key.keyObject, parsed.signature)) {
      return {
        ok: true,
        trustedComment: parsed.trustedComment,
        keyIdHex: parsed.keyIdHex,
        key,
      };
    }
  }

  return {
    ok: false,
    reason: "signature-invalid",
    detail: "the file does not match the signature",
  };
}

function verifyWithKeyTexts(message, signatureText, keyTexts, options) {
  if (!Array.isArray(keyTexts) || keyTexts.length === 0) {
    return { ok: false, reason: "trusted-key-missing", detail: "no trusted public key was given" };
  }

  const keys = [];
  for (let index = 0; index < keyTexts.length; index += 1) {
    const parsed = parsePublicKeyText(keyTexts[index]);
    if (!parsed.ok) {
      return {
        ok: false,
        reason: parsed.reason,
        detail: `trusted key ${index}: ${parsed.detail}`,
      };
    }
    keys.push(parsed);
  }

  const parsed = parseSignatureText(signatureText, options);
  if (!parsed.ok) return parsed;
  if (parsed.legacy) {
    return { ok: false, reason: "legacy-signature", detail: "legacy Ed signatures are not accepted for releases" };
  }
  return verifyMessage(keys, parsed, message);
}

export function verifyBuffer(buffer, signatureText, keyTexts, options = {}) {
  const message = options.legacyMessage
    ? buffer
    : createHash("blake2b512").update(buffer).digest();
  return verifyWithKeyTexts(message, signatureText, keyTexts, options);
}

export async function verifyFile(filePath, signatureText, keyTexts, options = {}) {
  const message = options.legacyMessage
    ? fs.readFileSync(filePath)
    : await blake2b512File(filePath);
  return verifyWithKeyTexts(message, signatureText, keyTexts, options);
}

export function loadKeyMaterial(value, { kind = "public" } = {}) {
  const defaultName = kind === "secret" ? "minisign.key" : "minisign.pub";
  if (typeof value !== "string" || value.length === 0) {
    return { ok: false, reason: "key-unreadable", detail: "key value is empty" };
  }
  try {
    const resolved = path.resolve(value);
    if (fs.existsSync(resolved)) {
      const stat = fs.statSync(resolved);
      const filePath = stat.isDirectory() ? path.join(resolved, defaultName) : resolved;
      if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
        return { ok: false, reason: "key-unreadable", detail: `key file is missing: ${filePath}` };
      }
      return { ok: true, text: fs.readFileSync(filePath, "utf8"), path: filePath };
    }
  } catch (error) {
    return {
      ok: false,
      reason: "key-unreadable",
      detail: error instanceof Error ? error.message : String(error),
    };
  }
  return { ok: true, text: value, path: null };
}

export function loadPublicKeys(values) {
  const texts = [];
  const paths = [];
  for (const value of values) {
    const loaded = loadKeyMaterial(value, { kind: "public" });
    if (!loaded.ok) return loaded;
    texts.push(loaded.text);
    paths.push(loaded.path);
  }
  return { ok: true, texts, paths };
}
