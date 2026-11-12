#!/usr/bin/env node
/**
 * Minisign-compatible signing for BWC update manifests and installers.
 *
 * The algorithm is real minisign prehashed signing: Ed25519 over BLAKE2b-512(file).
 * The keygen here writes an unencrypted test key, so it is for dry runs only. A real
 * release key is created and protected by the user (minisign or GnuPG, D8).
 */
import fs from "node:fs";
import path from "node:path";

import { isDirectExecutionOf } from "./direct-execution.mjs";
import { EXIT, describeError, parseArguments } from "./lib/cli.js";
import {
  envelope,
  generateKeyPair,
  isPlaceholderKeyText,
  isTestKeyText,
  loadKeyMaterial,
  parsePublicKeyText,
  parseSecretKeyText,
  signFile,
  verifyFile,
} from "./lib/minisign.js";

const SCRIPT = "scripts/sign-update-manifest.js";

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} keygen --out <dir> [--force] [--test-key] [--help]
  node ${SCRIPT} sign <file> --key <dir|file> --trusted-comment <text>
                   [--out <sigfile>] [--envelope-out <path>] [--legacy] [--help]
  node ${SCRIPT} fingerprint --key <dir|file> [--help]
  node ${SCRIPT} verify <file> --signature <path> --trusted-key <dir|file> [--help]

Commands:
  keygen        Write an unencrypted minisign-format test key pair.
  sign          Write <file>.minisig (raw text) and <file>.sig (base64 envelope).
  fingerprint   Print a public key's key id and SHA-256 fingerprint.
  verify        Check one file against a signature and a trusted public key.

Options:
  --out <dir>             keygen: output folder. sign: signature output path.
  --force                 keygen: overwrite an existing key pair.
  --test-key              keygen: mark the public key as a dry-run test key.
  --key <dir|file>        Key folder (minisign.key or minisign.pub) or file.
  --trusted-comment <t>   Signed comment, for example "version:0.1.0-beta.1".
  --envelope-out <path>   sign: also write the base64 envelope here.
  --legacy                sign: legacy Ed over raw bytes. Release paths refuse it.
  --signature <path>      verify: raw .minisig or base64 envelope.
  --trusted-key <path>    verify: trusted minisign public key.
  --help                  Show this help.

Exit codes: 0 success, 1 refused, 2 usage, 3 input or key file missing.`);
}

function isTestKeyMarkerKey(text) {
  return isTestKeyText(text);
}

function keygen(parsed) {
  const out = parsed.get("out");
  if (!out) throw new Error("keygen needs --out <dir>");
  const directory = path.resolve(out);
  const publicPath = path.join(directory, "minisign.pub");
  const secretPath = path.join(directory, "minisign.key");
  if (!parsed.has("force") && (fs.existsSync(publicPath) || fs.existsSync(secretPath))) {
    console.error(`${SCRIPT}: REFUSED [key-exists]: ${directory} already has a key; pass --force to overwrite`);
    return EXIT.REFUSED;
  }

  const testKey = parsed.has("test-key");
  const keyIdPlaceholder = "DRY-RUN TEST KEY";
  const comment = testKey
    ? `untrusted comment: BWC RELEASE ${keyIdPlaceholder}`
    : undefined;
  const pair = generateKeyPair({ comment });
  fs.mkdirSync(directory, { recursive: true });
  fs.writeFileSync(publicPath, pair.publicKeyText, "utf8");
  fs.writeFileSync(secretPath, pair.secretKeyText, { encoding: "utf8", mode: 0o600 });
  console.log(`sign-update-manifest: keygen ok (${pair.keyIdHex})`);
  console.log(`  public: ${publicPath}`);
  console.log(`  secret: ${secretPath}`);
  console.log(`  fingerprint: ${pair.fingerprint}`);
  if (testKey) {
    console.log("  WARNING: this is an unencrypted dry-run test key. Never use it for a release.");
  } else {
    console.log("  WARNING: this key is unencrypted and is not a real release key.");
  }
  return EXIT.OK;
}

async function sign(parsed) {
  const file = parsed.positionals[0];
  if (!file) throw new Error("sign needs a file argument");
  const filePath = path.resolve(file);
  if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
    console.error(`${SCRIPT}: REFUSED [signature-input-missing]: ${filePath}`);
    return EXIT.MISSING;
  }
  const keyValue = parsed.get("key");
  if (!keyValue) throw new Error("sign needs --key <dir|file>");
  const trustedComment = parsed.get("trusted-comment");
  if (!trustedComment) throw new Error("sign needs --trusted-comment <text>");
  if (/[\r\n]/.test(trustedComment)) {
    console.error(`${SCRIPT}: REFUSED [trusted-comment-invalid]: comment must be one line`);
    return EXIT.REFUSED;
  }

  const loaded = loadKeyMaterial(keyValue, { kind: "secret" });
  if (!loaded.ok) {
    console.error(`${SCRIPT}: REFUSED [${loaded.reason}]: ${loaded.detail}`);
    return EXIT.MISSING;
  }
  if (isPlaceholderKeyText(loaded.text)) {
    console.error(`${SCRIPT}: REFUSED [placeholder-key]: the secret key is a placeholder`);
    return EXIT.REFUSED;
  }

  let signatureText;
  try {
    parseSecretKeyText(loaded.text);
    signatureText = await signFile(filePath, loaded.text, trustedComment, {
      legacy: parsed.has("legacy"),
    });
  } catch (error) {
    const reason = error?.reason ?? "sign-failed";
    console.error(`${SCRIPT}: REFUSED [${reason}]: ${describeError(error)}`);
    return EXIT.REFUSED;
  }

  const outputPath = parsed.get("out") ? path.resolve(parsed.get("out")) : `${filePath}.minisig`;
  const envelopePath = parsed.get("envelope-out")
    ? path.resolve(parsed.get("envelope-out"))
    : `${filePath}.sig`;
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, signatureText, "utf8");
  fs.writeFileSync(envelopePath, envelope(signatureText), "utf8");
  console.log(`sign-update-manifest: signed ${filePath}`);
  console.log(`  signature: ${outputPath}`);
  console.log(`  envelope: ${envelopePath}`);
  if (parsed.has("legacy")) {
    console.log("  WARNING: legacy Ed signature. The update verifier refuses it.");
  }
  return EXIT.OK;
}

function fingerprint(parsed) {
  const keyValue = parsed.get("key");
  if (!keyValue) throw new Error("fingerprint needs --key <dir|file>");
  const loaded = loadKeyMaterial(keyValue, { kind: "public" });
  if (!loaded.ok) {
    console.error(`${SCRIPT}: REFUSED [${loaded.reason}]: ${loaded.detail}`);
    return EXIT.MISSING;
  }
  if (isPlaceholderKeyText(loaded.text)) {
    console.error(`${SCRIPT}: REFUSED [placeholder-key]: the public key is a placeholder`);
    return EXIT.REFUSED;
  }
  const parsedKey = parsePublicKeyText(loaded.text);
  if (!parsedKey.ok) {
    console.error(`${SCRIPT}: REFUSED [${parsedKey.reason}]: ${parsedKey.detail}`);
    return EXIT.REFUSED;
  }
  console.log(`keyId=${parsedKey.keyIdHex}`);
  console.log(`fingerprint=${parsedKey.fingerprint}`);
  if (isTestKeyMarkerKey(loaded.text)) {
    console.log("testKey=true");
  }
  return EXIT.OK;
}

async function verify(parsed) {
  const file = parsed.positionals[0];
  if (!file) throw new Error("verify needs a file argument");
  const filePath = path.resolve(file);
  if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
    console.error(`${SCRIPT}: REFUSED [signature-input-missing]: ${filePath}`);
    return EXIT.MISSING;
  }
  const signaturePath = parsed.get("signature");
  const keyValue = parsed.get("trusted-key");
  if (!signaturePath) throw new Error("verify needs --signature <path>");
  if (!keyValue) throw new Error("verify needs --trusted-key <path>");
  if (!fs.existsSync(path.resolve(signaturePath))) {
    console.error(`${SCRIPT}: REFUSED [signature-input-missing]: ${path.resolve(signaturePath)}`);
    return EXIT.MISSING;
  }
  const key = loadKeyMaterial(keyValue, { kind: "public" });
  if (!key.ok) {
    console.error(`${SCRIPT}: REFUSED [${key.reason}]: ${key.detail}`);
    return EXIT.MISSING;
  }
  const result = await verifyFile(filePath, fs.readFileSync(path.resolve(signaturePath), "utf8"), [key.text]);
  if (!result.ok) {
    console.error(`${SCRIPT}: REFUSED [${result.reason}]: ${result.detail}`);
    return EXIT.REFUSED;
  }
  console.log(`sign-update-manifest: ok (${result.keyIdHex})`);
  console.log(`  trusted comment: ${result.trustedComment}`);
  return EXIT.OK;
}

async function main(argv = process.argv.slice(2)) {
  let parsed;
  try {
    parsed = parseArguments(argv, {
      values: ["out", "key", "trusted-comment", "envelope-out", "signature", "trusted-key"],
      flags: ["force", "test-key", "legacy", "help"],
    });
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }
  if (parsed.has("help")) {
    printHelp();
    return EXIT.OK;
  }

  const command = parsed.positionals[0];
  const rest = { ...parsed, positionals: parsed.positionals.slice(1) };
  try {
    switch (command) {
      case "keygen":
        return keygen(rest);
      case "sign":
        return await sign(rest);
      case "fingerprint":
        return fingerprint(rest);
      case "verify":
        return await verify(rest);
      default:
        console.error(`${SCRIPT}: expected keygen, sign, fingerprint, or verify`);
        console.error(`Try: node ${SCRIPT} --help`);
        return EXIT.USAGE;
    }
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    return EXIT.USAGE;
  }
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = await main();
}
