#!/usr/bin/env node
/**
 * Embeds the release public key in src/Bwc.Update/Signing/TrustedKeys.cs and sets IsPlaceholder to
 * false. The updater trusts exactly the keys in that file, and the release tooling refuses to sign
 * with any other key.
 *
 *   npm run release:embed-key -- --pub <file>     the .pub file from `tauri signer generate`
 *                                                 (base64 of a minisign public key) or raw minisign text
 *   npm run release:embed-key -- --from-env       derive it from TAURI_SIGNING_PRIVATE_KEY (+ _PASSWORD)
 *
 * Options: --check (print, write nothing), --rotate (keep the embedded keys and add this one),
 * --replace (embed only this key). Failure modes: docs/failure-modes/release.md, section M.
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { isDirectExecutionOf } from "./direct-execution.mjs";
import {
  isPlaceholderKeyText,
  isTestKeyText,
  parsePublicKeyText,
  parseSecretKeyText,
  samePublicKey,
  secretKeyNeedsPassword,
} from "./lib/minisign.js";
import { TRUSTED_KEYS_RELATIVE_PATH, parseTrustedKeysSource } from "./lib/trusted-keys.js";
import { TAURI_KEY_ENV, TAURI_KEY_PASSWORD_ENV, secretKeyFromEnvironmentValue } from "./sign-update-manifest.js";

const SCRIPT = "scripts/embed-update-key.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const MINISIGN_TEXT_START = "untrusted comment:";
const MAX_PUB_CHARS = 4096;

class EmbedError extends Error {
  constructor(reason, detail, exitCode = 1) {
    super(detail);
    this.reason = reason;
    this.exitCode = exitCode;
  }
}

function usage() {
  return [
    `usage: node ${SCRIPT} (--pub <file> | --from-env) [--check] [--rotate | --replace] [--root <dir>]`,
    "  --pub <file>   the public key: the .pub file from `tauri signer generate`, or minisign .pub text",
    `  --from-env     derive the public key from ${TAURI_KEY_ENV} and ${TAURI_KEY_PASSWORD_ENV}`,
    "  --check        print the key and the result; write nothing",
    "  --rotate       keep the keys already embedded and add this one (key rotation overlap)",
    "  --replace      embed only this key, dropping any other real key",
  ].join("\n");
}

export function parseArgs(argv) {
  const options = { pub: undefined, fromEnv: false, check: false, rotate: false, replace: false, root: REPO_ROOT };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    switch (arg) {
      case "--pub":
        options.pub = argv[++index];
        if (!options.pub) throw new EmbedError("usage", "--pub needs a file path", 2);
        break;
      case "--from-env": options.fromEnv = true; break;
      case "--check": options.check = true; break;
      case "--rotate": options.rotate = true; break;
      case "--replace": options.replace = true; break;
      case "--root":
        options.root = argv[++index];
        if (!options.root) throw new EmbedError("usage", "--root needs a folder", 2);
        break;
      case "--help": case "-h":
        options.help = true; break;
      default:
        throw new EmbedError("usage", `unknown argument: ${arg}\n${usage()}`, 2);
    }
  }
  if (options.help) return options;
  if ((options.pub === undefined) === !options.fromEnv) {
    throw new EmbedError("usage", `pass exactly one of --pub <file> or --from-env\n${usage()}`, 2);
  }
  if (options.rotate && options.replace) {
    throw new EmbedError("usage", "--rotate and --replace cannot be combined", 2);
  }
  return options;
}

/** Public key text from a .pub file: raw minisign text, or the base64 of it (Tauri). LF line endings. */
export function publicKeyTextFromFile(file) {
  if (!fs.existsSync(file) || !fs.statSync(file).isFile()) {
    throw new EmbedError("key-unreadable", `public key file is missing: ${file}`, 3);
  }
  const raw = fs.readFileSync(file, "utf8").replace(/^﻿/, "");
  if (raw.length > MAX_PUB_CHARS) {
    throw new EmbedError("malformed-key", "the public key file is too large to be a minisign public key");
  }
  return publicKeyTextFromValue(raw);
}

export function publicKeyTextFromValue(raw) {
  const trimmed = raw.trim();
  let text = trimmed;
  if (!trimmed.startsWith(MINISIGN_TEXT_START)) {
    const compact = trimmed.replace(/\s+/g, "");
    const decoded = Buffer.from(compact, "base64");
    if (compact.length === 0 || decoded.toString("base64") !== compact) {
      throw new EmbedError("malformed-key", "the public key is neither minisign text nor base64 of it");
    }
    text = decoded.toString("utf8").trim();
  }
  text = text.replace(/\r\n/g, "\n");
  return checkedPublicKey(text);
}

function checkedPublicKey(text) {
  if (isPlaceholderKeyText(text)) throw new EmbedError("placeholder-key", "that is the placeholder, not a key");
  if (isTestKeyText(text)) throw new EmbedError("test-key", "that is a BWC test key; create a real release key");
  const parsed = parsePublicKeyText(text);
  if (!parsed.ok) throw new EmbedError(parsed.reason ?? "malformed-key", parsed.detail);
  return { text, parsed };
}

/** The public key of the TAURI_SIGNING_PRIVATE_KEY secret key. Never prints the key or the password. */
export function publicKeyFromEnvironment(env = process.env) {
  const value = env[TAURI_KEY_ENV];
  if (value === undefined || value.trim() === "") {
    throw new EmbedError("key-missing", `${TAURI_KEY_ENV} is not set (put it in .env)`, 2);
  }
  const loaded = secretKeyFromEnvironmentValue(value);
  if (!loaded.ok) throw new EmbedError(loaded.reason, loaded.detail, loaded.exitCode ?? 1);
  let password;
  if (secretKeyNeedsPassword(loaded.text)) {
    password = env[TAURI_KEY_PASSWORD_ENV];
    if (password === undefined || password.length === 0) {
      throw new EmbedError("password-missing", `the key is encrypted and ${TAURI_KEY_PASSWORD_ENV} is not set`, 2);
    }
  }
  let secret;
  try {
    secret = parseSecretKeyText(loaded.text, { password });
  } catch (error) {
    throw new EmbedError(error.reason ?? "malformed-secret-key", error.message);
  }
  const packet = Buffer.concat([Buffer.from("Ed"), Buffer.from(secret.keyId), Buffer.from(secret.publicBytes)]);
  const provisional = `untrusted comment: minisign public key\n${packet.toString("base64")}`;
  const parsed = parsePublicKeyText(provisional);
  if (!parsed.ok) throw new EmbedError(parsed.reason ?? "malformed-key", parsed.detail);
  return checkedPublicKey(`untrusted comment: minisign public key ${parsed.keyIdHex}\n${packet.toString("base64")}`);
}

/** C# regular string literal body for key text. */
function csharpLiteral(text) {
  return text.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\n/g, "\\n");
}

/** The whole TrustedKeys.cs source for the given real keys (oldest first). */
export function renderTrustedKeys(keyTexts) {
  const constants = keyTexts
    .map((text, index) => `    private const string ReleaseKey${index} = "${csharpLiteral(text)}";`)
    .join("\n\n");
  const names = keyTexts.map((_, index) => `ReleaseKey${index}`).join(", ");
  return `namespace Bwc.Update;

/// <summary>
/// The minisign public keys this build trusts for manifests and installers. Written by
/// scripts/embed-update-key.js (npm run release:embed-key). The matching secret key is the release VM's
/// TAURI_SIGNING_PRIVATE_KEY. Rotation: run the script with --rotate so the old and the new key are both
/// listed during the overlap, then drop the old one with --replace in a later release.
/// </summary>
public static class TrustedKeys
{
    /// <summary>True while <see cref="PlaceholderKey"/> is in place. A release build must not ship with it.</summary>
    public const bool IsPlaceholder = false;

    private const string PlaceholderKey = "untrusted comment: PLACEHOLDER replace with the release public key\\nPLACEHOLDER-NOT-A-KEY";

${constants}

    public static readonly IReadOnlyList<string> Embedded = [${names}];

    /// <summary>True only for the shipped placeholder list, which means no release key is configured yet.</summary>
    public static bool IsPlaceholderList(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return IsPlaceholder && keys.Count == 1 && keys[0] == PlaceholderKey;
    }

    /// <summary>
    /// The failure for a key list that does not validate. The placeholder is "not configured" (the Hub
    /// says so); any other invalid list is <see cref="UpdateFailure.TrustedKeysInvalid"/>.
    /// </summary>
    internal static UpdateFailure FailureFor(IReadOnlyList<string> keys) =>
        IsPlaceholderList(keys) ? UpdateFailure.TrustedKeysNotConfigured : UpdateFailure.TrustedKeysInvalid;
}
`;
}

/** Decides the new key list. Returns { keys, changed, note }. */
export function plan(current, key, { rotate, replace }) {
  const realKeys = current.isPlaceholder ? [] : current.embedded.filter((text) => !isPlaceholderKeyText(text));
  const parsedExisting = realKeys.map((text) => ({ text, parsed: parsePublicKeyText(text) }));
  if (parsedExisting.some((entry) => entry.parsed.ok && samePublicKey(entry.parsed, key.parsed))) {
    if (replace && realKeys.length > 1) return { keys: [key.text], changed: true, note: "kept only this key" };
    return { keys: realKeys, changed: false, note: "already embedded" };
  }
  if (realKeys.length === 0 || replace) return { keys: [key.text], changed: true, note: "embedded" };
  if (rotate) return { keys: [...realKeys, key.text], changed: true, note: "added for rotation" };
  const ids = parsedExisting.map((entry) => (entry.parsed.ok ? entry.parsed.keyIdHex : "?")).join(", ");
  throw new EmbedError(
    "key-already-embedded",
    `TrustedKeys.cs already embeds key ${ids}. Use --rotate to add ${key.parsed.keyIdHex} beside it, or --replace.`,
  );
}

export function main(argv = process.argv.slice(2), env = process.env, log = console.log) {
  const options = parseArgs(argv);
  if (options.help) {
    log(usage());
    return 0;
  }
  const key = options.fromEnv ? publicKeyFromEnvironment(env) : publicKeyTextFromFile(path.resolve(options.pub));

  // M3: a .pub that differs from the signing key in .env would be refused later, on the release VM.
  if (!options.fromEnv && env[TAURI_KEY_ENV] && env[TAURI_KEY_ENV].trim() !== "") {
    let signing = null;
    try {
      signing = publicKeyFromEnvironment(env);
    } catch (error) {
      log(`note: could not read ${TAURI_KEY_ENV} to cross-check (${error.reason}); using the .pub file only.`);
    }
    if (signing && !samePublicKey(signing.parsed, key.parsed)) {
      throw new EmbedError(
        "embedded-key-mismatch",
        `the .pub key ${key.parsed.keyIdHex} is not the ${TAURI_KEY_ENV} key ${signing.parsed.keyIdHex}`,
      );
    }
  }

  const file = path.join(options.root, TRUSTED_KEYS_RELATIVE_PATH);
  const original = fs.readFileSync(file, "utf8");
  const current = parseTrustedKeysSource(original);
  if (!current.ok) throw new EmbedError(current.reason, current.detail);
  const decision = plan(current, key, options);

  log(`key id: ${key.parsed.keyIdHex}`);
  log(`fingerprint: ${key.parsed.fingerprint}`);
  log("public key (safe to share):");
  log(key.text);
  if (!decision.changed) {
    log(`TrustedKeys.cs: ${decision.note}; nothing to do.`);
    return 0;
  }
  const next = renderTrustedKeys(decision.keys).replace(/\n/g, original.includes("\r\n") ? "\r\n" : "\n");
  if (options.check) {
    log(`--check: would write ${TRUSTED_KEYS_RELATIVE_PATH} (${decision.note}, ${decision.keys.length} key(s)).`);
    return 0;
  }

  // M7: read the written file back with the release tooling's parser; restore on any doubt.
  fs.writeFileSync(file, next, "utf8");
  const reread = parseTrustedKeysSource(fs.readFileSync(file, "utf8"));
  const roundTrip =
    reread.ok &&
    !reread.isPlaceholder &&
    reread.embedded.length === decision.keys.length &&
    reread.embedded.every((text, index) => text === decision.keys[index]);
  if (!roundTrip) {
    fs.writeFileSync(file, original, "utf8");
    throw new EmbedError("trusted-keys-unreadable", "the written file did not read back; the old file is restored");
  }
  log(`wrote ${TRUSTED_KEYS_RELATIVE_PATH}: ${decision.note}, IsPlaceholder = false, ${decision.keys.length} key(s).`);
  log("Commit it, merge to main, then run npm run release:win on the release VM.");
  return 0;
}

if (isDirectExecutionOf(import.meta.url)) {
  try {
    process.exitCode = main();
  } catch (error) {
    if (error instanceof EmbedError) {
      console.error(`${SCRIPT}: ${error.reason}: ${error.message}`);
      process.exitCode = error.exitCode;
    } else {
      console.error(`${SCRIPT}: ${error instanceof Error ? error.message : String(error)}`);
      process.exitCode = 1;
    }
  }
}
