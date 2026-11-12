// Same packet format and prehashed ED algorithm as tests/Bwc.Update.Tests/Support/minisign.mjs.
// The test helper signs only; this module also parses keys and verifies signatures.
import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, randomBytes, sign, verify } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const ED25519_SPKI_PREFIX = Buffer.from("302a300506032b6570032100", "hex");
const ED25519_PKCS8_PREFIX = Buffer.from("302e020100300506032b657004220420", "hex");
const UNTRUSTED_SIGNATURE_COMMENT = "untrusted comment: signature from minisign secret key";
const DEFAULT_SECRET_COMMENT = "untrusted comment: BWC test secret key (unencrypted, never publish)";
const PUBLIC_KEY_PACKET_LENGTH = 42;
const SIGNATURE_PACKET_LENGTH = 74;
const SIGNATURE_LENGTH = 64;
const TRUSTED_COMMENT_PREFIX = "trusted comment: ";
const PLACEHOLDER = /PLACEHOLDER/i;
const TEST_KEY_MARKER = /TEST KEY/i;

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

export function isPlaceholderKeyText(text) {
  return typeof text === "string" && PLACEHOLDER.test(text);
}

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
    const publicBytes = packet.subarray(10, 42);
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

export function parseSecretKeyText(text) {
  if (typeof text !== "string" || text.length === 0) {
    throw new Error("secret key text is empty");
  }
  if (isPlaceholderKeyText(text)) {
    const error = new Error("secret key text is a placeholder");
    error.reason = "placeholder-key";
    throw error;
  }
  const lines = splitDocumentLines(text, "secret key");
  if (lines.length !== 2) {
    const error = new Error(`secret key must have 2 lines, found ${lines.length}`);
    error.reason = "malformed-secret-key";
    throw error;
  }
  if (!lines[0].startsWith("untrusted comment:")) {
    const error = new Error("secret key line 1 must start with 'untrusted comment:'");
    error.reason = "malformed-secret-key";
    throw error;
  }
  const packet = decodeCanonicalBase64(lines[1], "secret key packet");
  if (packet.length !== 42 || packet.subarray(0, 2).toString() !== "Ed") {
    const error = new Error("secret key packet is malformed");
    error.reason = "malformed-secret-key";
    throw error;
  }
  return {
    keyId: packet.subarray(2, 10),
    keyObject: createPrivateKey({
      key: Buffer.concat([ED25519_PKCS8_PREFIX, packet.subarray(10, 42)]),
      format: "der",
      type: "pkcs8",
    }),
  };
}

export function generateKeyPair({ comment } = {}) {
  const { publicKey, privateKey } = generateKeyPairSync("ed25519");
  const keyId = randomBytes(8);
  const publicBytes = publicKey.export({ format: "der", type: "spki" }).subarray(-32);
  const seed = privateKey.export({ format: "der", type: "pkcs8" }).subarray(-32);
  const keyIdHex = displayKeyId(keyId);
  const publicComment = comment ?? `untrusted comment: BWC TEST KEY ${keyIdHex}`;
  const publicKeyText =
    `${publicComment}\n` +
    `${Buffer.concat([Buffer.from("Ed"), keyId, publicBytes]).toString("base64")}\n`;
  const secretKeyText =
    `${DEFAULT_SECRET_COMMENT}\n` +
    `${Buffer.concat([Buffer.from("Ed"), keyId, seed]).toString("base64")}\n`;
  return {
    keyId,
    keyIdHex,
    publicKeyText,
    secretKeyText,
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

export async function signFile(filePath, secretKeyText, trustedComment, { legacy = false } = {}) {
  if (/[\r\n]/.test(trustedComment)) {
    const error = new Error("trusted comment must be a single line");
    error.reason = "trusted-comment-invalid";
    throw error;
  }
  const { keyId, keyObject } = parseSecretKeyText(secretKeyText);
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
