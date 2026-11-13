#!/usr/bin/env node
/**
 * Minisign-compatible signing for BWC update manifests and installers.
 *
 * The algorithm is real minisign prehashed signing: Ed25519 over BLAKE2b-512(file). Keys are
 * minisign-format. The keygen here writes test keys for dry runs: unencrypted by default, or
 * encrypted with --password-env. A real release key is created and protected by the user (D8).
 * Passwords are read from an environment variable or a terminal prompt, never from an argument.
 */
import fs from "node:fs";
import path from "node:path";

import { isDirectExecutionOf } from "./direct-execution.mjs";
import { EXIT, describeError, parseArguments } from "./lib/cli.js";
import {
  envelope,
  generateKeyPair,
  isPlaceholderKeyText,
  loadKeyMaterial,
  parsePublicKeyText,
  parseSecretKeyText,
  secretKeyNeedsPassword,
  signFile,
  verifyFile,
} from "./lib/minisign.js";
import { passwordArgumentGiven, passwordFromEnvironment, resolvePassword } from "./lib/secret-input.js";

const SCRIPT = "scripts/sign-update-manifest.js";

/** The release key from the environment, in the format `tauri signer generate` writes (or the raw file). */
export const TAURI_KEY_ENV = "TAURI_SIGNING_PRIVATE_KEY";
export const TAURI_KEY_PASSWORD_ENV = "TAURI_SIGNING_PRIVATE_KEY_PASSWORD";
const MINISIGN_TEXT_START = "untrusted comment:";
const MAX_ENV_KEY_CHARS = 16 * 1024;
const BASE64_PATTERN = /^[A-Za-z0-9+/]+={0,2}$/;

function keyFailure(reason, detail, exitCode) {
  return { ok: false, reason, detail, exitCode };
}

/** A CRLF pair becomes LF. A lone CR is left in place, and the parser refuses it. */
function normalizeLineEndings(text) {
  return text.replace(/\r\n/g, "\n");
}

/**
 * Secret key text from a --key or --secret-key path: a key file, or a folder holding minisign.key.
 * A value that is not an existing path is refused, so key text never travels on the command line.
 */
export function secretKeyFromPath(value) {
  if (typeof value !== "string" || value.length === 0) {
    return keyFailure("key-unreadable", "the key path is empty", EXIT.MISSING);
  }
  if (!fs.existsSync(path.resolve(value))) {
    return keyFailure(
      "key-unreadable",
      `the key path does not exist; keys are never command-line arguments. Use a path, or set ${TAURI_KEY_ENV}`,
      EXIT.MISSING,
    );
  }
  const loaded = loadKeyMaterial(value, { kind: "secret" });
  if (!loaded.ok) return keyFailure(loaded.reason, loaded.detail, EXIT.MISSING);
  return { ok: true, text: normalizeLineEndings(loaded.text), source: "file" };
}

/**
 * Secret key text from the TAURI_SIGNING_PRIVATE_KEY value: minisign key text, the base64 of that
 * text (what `tauri signer generate` prints), or a key path. A failure never echoes the value.
 */
export function secretKeyFromEnvironmentValue(value) {
  if (value.length > MAX_ENV_KEY_CHARS) {
    return keyFailure("key-malformed", `${TAURI_KEY_ENV} is longer than 16 KiB`, EXIT.REFUSED);
  }
  const trimmed = value.trim();
  if (trimmed.startsWith(MINISIGN_TEXT_START)) {
    return { ok: true, text: normalizeLineEndings(trimmed), source: "env" };
  }
  if (!/[\r\n]/.test(trimmed) && fs.existsSync(path.resolve(trimmed))) {
    const fromPath = secretKeyFromPath(trimmed);
    return fromPath.ok ? { ...fromPath, source: "env" } : fromPath;
  }
  const compact = trimmed.replace(/\s+/g, "");
  if (compact.length > 0 && compact.length % 4 === 0 && BASE64_PATTERN.test(compact)) {
    const decoded = Buffer.from(compact, "base64");
    if (decoded.toString("base64") === compact) {
      let text = null;
      try {
        text = new TextDecoder("utf-8", { fatal: true }).decode(decoded);
      } catch {
        text = null;
      }
      if (text !== null && text.trim().startsWith(MINISIGN_TEXT_START)) {
        return { ok: true, text: normalizeLineEndings(text.trim()), source: "env" };
      }
    }
  }
  return keyFailure(
    "key-malformed",
    `${TAURI_KEY_ENV} is not minisign key text, a key path, or base64 of a minisign key file`,
    EXIT.REFUSED,
  );
}

/**
 * The signing key for one command: the --key or --secret-key path when given, else
 * TAURI_SIGNING_PRIVATE_KEY. Returns { ok, text, source: "file" | "env" } or a refusal.
 */
export function resolveSigningKey({ keyPath, env = process.env }) {
  if (keyPath !== undefined) return secretKeyFromPath(keyPath);
  const value = env[TAURI_KEY_ENV];
  if (value === undefined || value.trim() === "") {
    return keyFailure("key-missing", `pass --key or --secret-key <path>, or set ${TAURI_KEY_ENV}`, EXIT.USAGE);
  }
  return secretKeyFromEnvironmentValue(value);
}

/**
 * The password for key text. An unencrypted key needs none. An encrypted key takes --password-env
 * when given, else TAURI_SIGNING_PRIVATE_KEY_PASSWORD for an env key (empty is passed on and refused
 * by the parser), else a terminal prompt.
 */
export async function resolveKeyPassword({ text, source, passwordEnv, env = process.env, prompt }) {
  if (!secretKeyNeedsPassword(text)) return { ok: true, password: undefined };
  if (passwordEnv !== undefined) return resolvePassword({ envName: passwordEnv, env, prompt });
  if (source === "env") return { ok: true, password: env[TAURI_KEY_PASSWORD_ENV] ?? "" };
  return resolvePassword({ envName: undefined, env, prompt });
}

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} keygen --out <dir> [--force] [--test-key] [--test-kdf] [--password-env <NAME>] [--help]
  node ${SCRIPT} sign <file> [--key <dir|file>] --trusted-comment <text>
                   [--password-env <NAME>] [--out <sigfile>] [--envelope-out <path>] [--legacy] [--help]
  node ${SCRIPT} fingerprint --key <dir|file> [--help]
  node ${SCRIPT} verify <file> --signature <path> --trusted-key <dir|file> [--help]

Commands:
  keygen        Write a minisign-format key pair. Unencrypted unless --password-env is given.
  sign          Write <file>.minisig (raw text) and <file>.sig (base64 envelope).
  fingerprint   Print a public key's key id and SHA-256 fingerprint.
  verify        Check one file against a signature and a trusted public key.

Options:
  --out <dir>             keygen: output folder. sign: signature output path.
  --force                 keygen: overwrite an existing key pair.
  --test-key              keygen: mark the public key as a dry-run test key (test use only).
  --test-kdf              keygen: reduced scrypt cost. Only with --test-key and --password-env.
  --password-env <NAME>   keygen and sign: read the key password from this environment variable.
                          The password is never accepted as an argument. Without this option, an
                          encrypted key asks on a terminal, and fails in a script.
  --key <dir|file>        sign: key folder or file. Without it, sign reads TAURI_SIGNING_PRIVATE_KEY
                          (base64 of the key file, the raw key text, or a path). An env key takes
                          TAURI_SIGNING_PRIVATE_KEY_PASSWORD unless --password-env is given.
                          Key text is never accepted as an argument.
  --key <dir|file>        fingerprint: key folder (minisign.pub) or file.
  --trusted-comment <t>   Signed comment, for example "version:0.1.0-beta.1".
  --envelope-out <path>   sign: also write the base64 envelope here.
  --legacy                sign: legacy Ed over raw bytes. Release paths refuse it.
  --signature <path>      verify: raw .minisig or base64 envelope.
  --trusted-key <path>    verify: trusted minisign public key.
  --help                  Show this help.

Exit codes: 0 success, 1 refused, 2 usage (includes password-missing), 3 input or key file missing.`);
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
  if (parsed.has("test-kdf") && !(parsed.has("test-key") && parsed.has("password-env"))) {
    console.error(`${SCRIPT}: REFUSED [usage]: --test-kdf needs --test-key and --password-env`);
    return EXIT.USAGE;
  }

  const testKey = parsed.has("test-key");
  const comment = testKey ? "untrusted comment: BWC RELEASE DRY-RUN TEST KEY" : undefined;
  let password;
  if (parsed.has("password-env")) {
    const fromEnvironment = passwordFromEnvironment(parsed.get("password-env"));
    if (!fromEnvironment.ok) {
      console.error(`${SCRIPT}: REFUSED [${fromEnvironment.reason}]: ${fromEnvironment.detail}`);
      return EXIT.USAGE;
    }
    password = fromEnvironment.password;
  }
  const pair = generateKeyPair({
    comment,
    password,
    kdfProfile: parsed.has("test-kdf") ? "test" : "sensitive",
  });
  fs.mkdirSync(directory, { recursive: true });
  fs.writeFileSync(publicPath, pair.publicKeyText, "utf8");
  fs.writeFileSync(secretPath, pair.secretKeyText, { encoding: "utf8", mode: 0o600 });
  console.log(`sign-update-manifest: keygen ok (${pair.keyIdHex})`);
  console.log(`  public: ${publicPath}`);
  console.log(`  secret: ${secretPath}`);
  console.log(`  fingerprint: ${pair.fingerprint}`);
  console.log(`  encrypted: ${pair.encrypted ? "yes" : "no"}`);
  if (testKey) {
    console.log("  WARNING: this is a dry-run test key. Never use it for a release.");
  } else {
    console.log("  WARNING: this key is not a real release key. The release key is created by the user.");
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
  const trustedComment = parsed.get("trusted-comment");
  if (!trustedComment) throw new Error("sign needs --trusted-comment <text>");
  if (/[\r\n]/.test(trustedComment)) {
    console.error(`${SCRIPT}: REFUSED [trusted-comment-invalid]: comment must be one line`);
    return EXIT.REFUSED;
  }

  const key = resolveSigningKey({ keyPath: parsed.get("key") });
  if (!key.ok) {
    console.error(`${SCRIPT}: REFUSED [${key.reason}]: ${key.detail}`);
    return key.exitCode;
  }
  if (isPlaceholderKeyText(key.text)) {
    console.error(`${SCRIPT}: REFUSED [placeholder-key]: the secret key is a placeholder`);
    return EXIT.REFUSED;
  }

  const resolved = await resolveKeyPassword({
    text: key.text,
    source: key.source,
    passwordEnv: parsed.get("password-env"),
  });
  if (!resolved.ok) {
    console.error(`${SCRIPT}: REFUSED [${resolved.reason}]: ${resolved.detail}`);
    return EXIT.USAGE;
  }
  const password = resolved.password;

  let signatureText;
  try {
    signatureText = await signFile(filePath, key.text, trustedComment, {
      legacy: parsed.has("legacy"),
      password,
    });
  } catch (error) {
    const reason = error?.reason ?? "sign-failed";
    console.error(`${SCRIPT}: REFUSED [${reason}]: ${describeError(error)}`);
    return error?.exitCode ?? EXIT.REFUSED;
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
  if (passwordArgumentGiven(argv)) {
    console.error(`${SCRIPT}: REFUSED [password-argument]: passwords are never command-line arguments; use --password-env <NAME>`);
    return EXIT.USAGE;
  }
  let parsed;
  try {
    parsed = parseArguments(argv, {
      values: ["out", "key", "trusted-comment", "envelope-out", "signature", "trusted-key", "password-env"],
      flags: ["force", "test-key", "test-kdf", "legacy", "help"],
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
