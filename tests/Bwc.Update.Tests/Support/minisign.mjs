#!/usr/bin/env node
// BWC spike S5: minisign-format signer for the Tauri-style updater envelope. Node built-ins only.
//
//   node minisign.mjs keygen <dir>
//   node minisign.mjs sign <file> <keydir> [--trusted-comment <text>] [--legacy]
//
//   node minisign.mjs sign-manifest <file> <keydir> --version <semver> --channel stable|beta --expires <yyyy-MM-ddTHH:mm:ssZ>
//
// sign writes two files next to <file>:
//   <file>.minisig  raw minisign signature text (4 lines, LF endings, trailing newline)
//   <file>.sig      base64 of that text: the envelope Tauri writes and puts in latest.json "signature"
//
// sign-manifest signs a manifest and writes the trusted comment the updater requires, exactly:
//   version:<semver> channel:<stable|beta> expires:<yyyy-MM-ddTHH:mm:ssZ>
//
// Signing is prehashed ("ED"): Ed25519 over BLAKE2b-512(file), streamed in 1 MB chunks.
// --legacy writes an "Ed" packet over the raw file bytes instead (only for tests of rejection).
//
// SPIKE-ONLY KEY FORMAT. The secret key is NOT encrypted (no scrypt password), and its file
// (base64 of "Ed" + 8-byte key id + 32-byte Ed25519 seed) is NOT readable by the minisign CLI.
// The public key file is standard minisign format. Do not use these keys for a real release.
//
// Ed25519 and BLAKE2b logic follows Zinnia's scripts/e2e-release-fixtures/minisign.mjs.

import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ED25519_SPKI_PREFIX = Buffer.from("302a300506032b6570032100", "hex");
const ED25519_PKCS8_PREFIX = Buffer.from("302e020100300506032b657004220420", "hex");
const UNTRUSTED_SIGNATURE_COMMENT = "untrusted comment: signature from minisign secret key";
const UNTRUSTED_SECRET_COMMENT =
  "untrusted comment: BWC SPIKE UNENCRYPTED secret key (test only, not minisign-compatible)";

// minisign prints the key id byte-reversed in comment lines; the packet stores it in this order.
function displayKeyId(keyId) {
  return Buffer.from(keyId).reverse().toString("hex").toUpperCase();
}

// Tauri stores the public key file text, base64-encoded, in tauri.conf.json.
export function tauriPublicKey(publicKeyText) {
  return Buffer.from(publicKeyText, "utf8").toString("base64");
}

export function generateKeyPair() {
  const { publicKey, privateKey } = crypto.generateKeyPairSync("ed25519");
  const keyId = crypto.randomBytes(8);
  const publicRaw = publicKey.export({ format: "der", type: "spki" }).subarray(-32);
  const seed = privateKey.export({ format: "der", type: "pkcs8" }).subarray(-32);
  const publicKeyText =
    `untrusted comment: minisign public key: ${displayKeyId(keyId)}\n` +
    `${Buffer.concat([Buffer.from("Ed"), keyId, publicRaw]).toString("base64")}\n`;
  const secretKeyText =
    `${UNTRUSTED_SECRET_COMMENT}\n` +
    `${Buffer.concat([Buffer.from("Ed"), keyId, seed]).toString("base64")}\n`;
  return {
    keyId: displayKeyId(keyId),
    publicKeyText,
    secretKeyText,
    tauriPublicKey: tauriPublicKey(publicKeyText),
  };
}

function parseSecretKey(text) {
  const lines = text.trim().split(/\r?\n/);
  if (lines.length !== 2 || !lines[0].startsWith("untrusted comment:")) {
    throw new Error("secret key file is not a BWC spike secret key");
  }
  const packet = Buffer.from(lines[1], "base64");
  if (packet.length !== 42 || packet.subarray(0, 2).toString() !== "Ed") {
    throw new Error("secret key packet is malformed");
  }
  return {
    keyId: packet.subarray(2, 10),
    privateKey: crypto.createPrivateKey({
      key: Buffer.concat([ED25519_PKCS8_PREFIX, packet.subarray(10, 42)]),
      format: "der",
      type: "pkcs8",
    }),
  };
}

// Streams the file, so a 300 MB installer never sits in memory.
export async function blake2b512File(filePath) {
  const hash = crypto.createHash("blake2b512");
  for await (const chunk of fs.createReadStream(filePath, { highWaterMark: 1 << 20 })) {
    hash.update(chunk);
  }
  return hash.digest();
}

// Returns the raw minisign signature text for filePath.
export async function signFile(filePath, secretKeyText, trustedComment, { legacy = false } = {}) {
  if (/[\r\n]/.test(trustedComment)) {
    throw new Error("trusted comment must be a single line");
  }
  const { keyId, privateKey } = parseSecretKey(secretKeyText);
  const message = legacy ? fs.readFileSync(filePath) : await blake2b512File(filePath);
  const signature = crypto.sign(null, message, privateKey);
  const packet = Buffer.concat([Buffer.from(legacy ? "Ed" : "ED"), keyId, signature]);
  const globalSignature = crypto.sign(
    null,
    Buffer.concat([signature, Buffer.from(trustedComment, "utf8")]),
    privateKey,
  );
  return (
    `${UNTRUSTED_SIGNATURE_COMMENT}\n` +
    `${packet.toString("base64")}\n` +
    `trusted comment: ${trustedComment}\n` +
    `${globalSignature.toString("base64")}\n`
  );
}

// Tauri .sig files and latest.json "signature" hold base64 of the minisign text.
export function envelope(signatureText) {
  return Buffer.from(signatureText, "utf8").toString("base64");
}

// The manifest trusted comment. The updater accepts exactly this form (docs/contracts/update-manifest.md).
const MANIFEST_COMMENT_PATTERN =
  /^version:(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)? channel:(stable|beta) expires:\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/;

export function manifestTrustedComment({ version, channel, expires }) {
  const comment = `version:${version} channel:${channel} expires:${expires}`;
  if (!MANIFEST_COMMENT_PATTERN.test(comment)) {
    throw new Error(`manifest trusted comment is not in the required form: ${comment}`);
  }
  return comment;
}

function usage() {
  return [
    "usage:",
    "  node minisign.mjs keygen <dir>",
    "  node minisign.mjs sign <file> <keydir> [--trusted-comment <text>] [--legacy]",
    "  node minisign.mjs sign-manifest <file> <keydir> --version <semver> --channel stable|beta --expires <yyyy-MM-ddTHH:mm:ssZ>",
  ].join("\n");
}

async function writeSignature(file, keyDir, trustedComment, legacy) {
  const secretKeyText = fs.readFileSync(path.join(keyDir, "minisign.key"), "utf8");
  const signatureText = await signFile(file, secretKeyText, trustedComment, { legacy });
  fs.writeFileSync(`${file}.minisig`, signatureText);
  fs.writeFileSync(`${file}.sig`, envelope(signatureText));
  console.log(`${file}.minisig`);
  console.log(`${file}.sig`);
}

async function main(argv) {
  const [command, ...rest] = argv;

  if (command === "keygen" && rest.length === 1) {
    const dir = rest[0];
    const secretPath = path.join(dir, "minisign.key");
    if (fs.existsSync(secretPath)) {
      throw new Error(`refusing to overwrite ${secretPath}`);
    }
    fs.mkdirSync(dir, { recursive: true });
    const keys = generateKeyPair();
    fs.writeFileSync(path.join(dir, "minisign.pub"), keys.publicKeyText);
    fs.writeFileSync(secretPath, keys.secretKeyText, { mode: 0o600 });
    console.log(`keyId=${keys.keyId}`);
    console.log(`tauriPublicKey=${keys.tauriPublicKey}`);
    return;
  }

  if (command === "sign" && rest.length >= 2) {
    const [file, keyDir, ...options] = rest;
    let trustedComment = `timestamp:${Math.floor(Date.now() / 1000)} file:${path.basename(file)}`;
    let legacy = false;
    for (let i = 0; i < options.length; i++) {
      if (options[i] === "--trusted-comment") {
        if (options[i + 1] === undefined) throw new Error("--trusted-comment needs a value");
        trustedComment = options[++i];
      } else if (options[i] === "--legacy") {
        legacy = true;
      } else {
        throw new Error(`unknown option ${options[i]}`);
      }
    }
    await writeSignature(file, keyDir, trustedComment, legacy);
    return;
  }

  if (command === "sign-manifest" && rest.length >= 2) {
    const [file, keyDir, ...options] = rest;
    const fields = {};
    for (let i = 0; i < options.length; i += 2) {
      const flag = options[i];
      if (!["--version", "--channel", "--expires"].includes(flag)) throw new Error(`unknown option ${flag}`);
      if (options[i + 1] === undefined) throw new Error(`${flag} needs a value`);
      fields[flag.slice(2)] = options[i + 1];
    }
    for (const name of ["version", "channel", "expires"]) {
      if (fields[name] === undefined) throw new Error(`--${name} is required`);
    }
    await writeSignature(file, keyDir, manifestTrustedComment(fields), false);
    return;
  }

  throw new Error(usage());
}

const isMain = process.argv[1] !== undefined && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  main(process.argv.slice(2)).catch((error) => {
    console.error(`minisign: ${error.message}`);
    process.exitCode = 1;
  });
}
