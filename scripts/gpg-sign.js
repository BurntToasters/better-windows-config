#!/usr/bin/env node
/**
 * Writes a detached, ASCII-armored GnuPG signature beside every file in the release folder, then
 * verifies each one with gpg --verify. It writes files only and never uploads.
 *
 * Signature name: <asset>.asc, the name Zinnia uses. BWC's own sidecars are <asset>.minisig (minisign
 * text) and <asset>.sig (base64 minisign envelope), so .asc cannot collide with either. Files that
 * already end in .asc are never signed.
 *
 * GPG_KEY_ID is 8, 16, or 40 hex digits. The matching secret key's primary fingerprint must match it.
 * GPG_PASSPHRASE reaches gpg on stdin only (--pinentry-mode loopback, --passphrase-fd 0). It is never
 * an argument, never logged, and never in an error message.
 */
import { spawnSync } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import { EXIT, describeError, parseArguments, printRefusals, relativeTo, repoRootFrom } from "./lib/cli.js";
import { artifactDirectory } from "./lib/release-context.js";
import { readPackageVersion } from "./lib/release-policy.js";
import { isDirectExecutionOf } from "./direct-execution.mjs";

const SCRIPT = "scripts/gpg-sign.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
export const SIGNATURE_SUFFIX = ".asc";
const KEY_ID_PATTERN = /^(?:[0-9A-F]{8}|[0-9A-F]{16}|[0-9A-F]{40})$/;
const FINGERPRINT_PATTERN = /^[0-9A-F]{40}$/;
const GPG_TIMEOUT_MS = 120_000;
const REDACTED = "[redacted]";

function refusal(reason, detail, exitCode = EXIT.REFUSED) {
  return { reason, detail, exitCode };
}

function failure(errors, signed = []) {
  return { ok: false, errors: Array.isArray(errors) ? errors : [errors], signed };
}

/** Normalizes GPG_KEY_ID. Accepts a key ID or a fingerprint, with or without spaces. */
export function parseKeyId(value) {
  const text = typeof value === "string" ? value.replace(/\s+/g, "").toUpperCase() : "";
  if (text.length === 0) {
    return { ok: false, error: refusal("key-id-missing", "GPG_KEY_ID is required", EXIT.USAGE) };
  }
  if (!KEY_ID_PATTERN.test(text)) {
    return {
      ok: false,
      error: refusal("key-id-invalid", "GPG_KEY_ID must be 8, 16, or 40 hex digits (a key ID or a fingerprint)", EXIT.USAGE),
    };
  }
  return { ok: true, keyId: text };
}

/** Files to sign: every regular, non-hidden file that is not already a .asc signature. */
export function listSignableFiles(dir) {
  const files = [];
  const errors = [];
  for (const name of fs.readdirSync(dir).sort()) {
    if (name.startsWith(".")) continue;
    const full = path.join(dir, name);
    const info = fs.lstatSync(full);
    if (info.isSymbolicLink()) {
      errors.push(refusal("symlink-refused", `${name} is a symbolic link or junction`));
    } else if (info.isDirectory()) {
      errors.push(refusal("unexpected-directory", `${name} is a folder; release assets are flat files`));
    } else if (!info.isFile()) {
      errors.push(refusal("unexpected-entry", `${name} is not a regular file`));
    } else if (!name.endsWith(SIGNATURE_SUFFIX)) {
      files.push(full);
    }
  }
  return { files, errors };
}

/**
 * Reads `gpg --with-colons --list-secret-keys --fingerprint <id>`. The primary fingerprint is the
 * first fpr record after the single sec record. It must match the key ID (equal for 40 hex digits,
 * a suffix for 8 or 16).
 */
export function parseSecretKeyListing(text, keyId) {
  const lines = String(text ?? "").split(/\r?\n/);
  const secIndexes = [];
  lines.forEach((line, index) => {
    if (line.startsWith("sec:")) secIndexes.push(index);
  });
  if (secIndexes.length === 0) {
    return { ok: false, error: refusal("key-not-found", "no secret key in the keyring matches GPG_KEY_ID", EXIT.MISSING) };
  }
  if (secIndexes.length > 1) {
    return { ok: false, error: refusal("key-ambiguous", `${secIndexes.length} secret keys match GPG_KEY_ID`) };
  }
  const fprLine = lines.slice(secIndexes[0] + 1).find((line) => line.startsWith("fpr:"));
  const fingerprint = fprLine ? (fprLine.split(":")[9] ?? "").toUpperCase() : "";
  if (!FINGERPRINT_PATTERN.test(fingerprint)) {
    return { ok: false, error: refusal("key-lookup-failed", "gpg did not report the key fingerprint", EXIT.EXTERNAL) };
  }
  const matches = keyId.length === 40 ? fingerprint === keyId : fingerprint.endsWith(keyId);
  if (!matches) {
    return { ok: false, error: refusal("key-mismatch", `the keyring key ${fingerprint} is not GPG_KEY_ID`) };
  }
  return { ok: true, fingerprint };
}

/** Reads `gpg --status-fd 1 --verify` output. The signature must be GOODSIG and made by the primary key. */
export function verdictFromVerify(result, fingerprint) {
  if (result.error || result.status !== 0) {
    return { ok: false, error: refusal("verify-failed", "gpg --verify did not accept the new signature", EXIT.REFUSED) };
  }
  const lines = String(result.stdout ?? "").split(/\r?\n/);
  if (!lines.some((line) => line.startsWith("[GNUPG:] GOODSIG "))) {
    return { ok: false, error: refusal("verify-failed", "gpg --verify reported no GOODSIG", EXIT.REFUSED) };
  }
  const valid = lines.find((line) => line.startsWith("[GNUPG:] VALIDSIG "));
  if (!valid) {
    return { ok: false, error: refusal("verify-failed", "gpg --verify reported no VALIDSIG", EXIT.REFUSED) };
  }
  // VALIDSIG field 11 is the primary key fingerprint; field 2 is the signing subkey.
  const parts = valid.split(" ");
  const primary = (parts[11] ?? parts[2] ?? "").toUpperCase();
  if (primary !== fingerprint) {
    return { ok: false, error: refusal("signer-mismatch", `the signature was made by ${primary}, not the keyring key`) };
  }
  return { ok: true };
}

/** Runs gpg with no shell. The passphrase is passed only through `input` (stdin). */
export function runGpgProcess(args, { input, env } = {}) {
  const result = spawnSync("gpg", args, {
    input,
    env,
    encoding: "utf8",
    stdio: ["pipe", "pipe", "pipe"],
    timeout: GPG_TIMEOUT_MS,
    maxBuffer: 16 * 1024 * 1024,
    windowsHide: true,
  });
  return {
    status: result.status,
    stdout: result.stdout ?? "",
    stderr: result.stderr ?? "",
    error: result.error ?? null,
  };
}

function firstLine(text, passphrase) {
  const scrubbed = passphrase ? String(text ?? "").split(passphrase).join(REDACTED) : String(text ?? "");
  return scrubbed.split(/\r?\n/).map((line) => line.trim()).filter(Boolean).slice(0, 3).join(" | ").slice(0, 400);
}

function removeQuietly(filePath) {
  try {
    fs.rmSync(filePath, { force: true });
  } catch {
    // A leftover hidden partial file is named in no output; the next run does not read it.
  }
}

function isNonEmptyFile(filePath) {
  try {
    return fs.statSync(filePath).size > 0;
  } catch {
    return false;
  }
}

/**
 * Signs and verifies every signable file in `dir`. Returns { ok, errors, signed, fingerprint }.
 * Fails fast: the first error stops the run. Signatures already written for earlier files were
 * verified against those files, so they remain valid.
 */
export function signReleaseAssets({ dir, keyId, passphrase, runGpg = runGpgProcess, log = () => {} }) {
  const key = parseKeyId(keyId);
  if (!key.ok) return failure(key.error);
  if (typeof passphrase !== "string" || passphrase.length === 0) {
    return failure(refusal("passphrase-missing", "GPG_PASSPHRASE is required", EXIT.USAGE));
  }
  const scrub = (text) => firstLine(text, passphrase);
  if (!dir || !fs.existsSync(dir) || !fs.statSync(dir).isDirectory()) {
    return failure(refusal("release-dir-missing", `release folder is missing: ${dir}`, EXIT.MISSING));
  }
  const listing = listSignableFiles(dir);
  if (listing.errors.length > 0) return failure(listing.errors);
  if (listing.files.length === 0) {
    return failure(refusal("nothing-to-sign", "the release folder has no regular file to sign", EXIT.MISSING));
  }

  // The gpg child never sees the passphrase in its environment; it gets stdin only.
  const env = { ...process.env };
  delete env.GPG_PASSPHRASE;

  const version = runGpg(["--version"], { env });
  if (version.error || version.status !== 0) {
    const missing = version.error?.code === "ENOENT";
    return failure(
      missing
        ? refusal("gpg-missing", "gpg was not found on PATH; install GnuPG and retry", EXIT.EXTERNAL)
        : refusal("gpg-failed", `gpg --version failed: ${scrub(version.stderr || version.error?.message)}`, EXIT.EXTERNAL),
    );
  }

  const lookup = runGpg(["--batch", "--with-colons", "--list-secret-keys", "--fingerprint", key.keyId], { env });
  if (lookup.error) {
    return failure(refusal("gpg-failed", `gpg could not list keys: ${scrub(lookup.error.message)}`, EXIT.EXTERNAL));
  }
  if (lookup.status !== 0) {
    return failure(refusal("key-not-found", `gpg found no secret key for GPG_KEY_ID: ${scrub(lookup.stderr)}`, EXIT.MISSING));
  }
  const found = parseSecretKeyListing(lookup.stdout, key.keyId);
  if (!found.ok) return failure(found.error);
  const { fingerprint } = found;

  const signed = [];
  for (const file of listing.files) {
    const name = path.basename(file);
    const signature = `${file}${SIGNATURE_SUFFIX}`;
    const partial = path.join(dir, `.${name}${SIGNATURE_SUFFIX}.${crypto.randomBytes(6).toString("hex")}.partial`);
    const sign = runGpg(
      [
        "--batch",
        "--yes",
        "--armor",
        "--detach-sign",
        "--local-user",
        fingerprint,
        "--pinentry-mode",
        "loopback",
        "--passphrase-fd",
        "0",
        "--output",
        partial,
        file,
      ],
      { input: `${passphrase}\n`, env },
    );
    if (sign.error || sign.status !== 0 || !isNonEmptyFile(partial)) {
      removeQuietly(partial);
      const detail = sign.error ? sign.error.message : sign.stderr;
      return failure(refusal("sign-failed", `gpg could not sign ${name}: ${scrub(detail)}`, EXIT.EXTERNAL), signed);
    }
    const verdict = verdictFromVerify(runGpg(["--batch", "--status-fd", "1", "--verify", partial, file], { env }), fingerprint);
    if (!verdict.ok) {
      removeQuietly(partial);
      return failure(verdict.error, signed);
    }
    fs.renameSync(partial, signature);
    signed.push({ file: name, signature: path.basename(signature), fingerprint });
    log(`  + ${path.basename(signature)}`);
  }
  return { ok: true, errors: [], signed, fingerprint };
}

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--dir <path>] [--json] [--help]

Writes <asset>.asc, a detached ASCII-armored GnuPG signature, beside every file in the release
folder, then verifies each one with gpg --verify. It writes files only and never uploads.

Environment (read from .env by the npm script, or from the shell):
  GPG_KEY_ID        Signing key: 8, 16, or 40 hex digits. Its primary fingerprint must match.
  GPG_PASSPHRASE    Key passphrase. Sent to gpg on stdin only; never printed.

Options:
  --dir <path>      Release folder. Default: artifacts/release/<package.json version>/stable.
  --json            Print one JSON line.
  --help            Show this help.

Exit codes: 0 signed and verified, 1 refused, 2 usage, 3 missing input, 4 gpg missing or failed.`);
}

function main(argv = process.argv.slice(2)) {
  let parsed;
  try {
    parsed = parseArguments(argv, { values: ["dir"], flags: ["json", "help"] });
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }
  if (parsed.has("help")) {
    printHelp();
    return EXIT.OK;
  }
  if (parsed.positionals.length > 0) {
    console.error(`${SCRIPT}: unexpected argument: ${parsed.positionals[0]}`);
    return EXIT.USAGE;
  }

  const version = readPackageVersion(REPO_ROOT);
  const dir = parsed.get("dir") ? path.resolve(parsed.get("dir")) : artifactDirectory(REPO_ROOT, version, "stable");
  const json = parsed.has("json");
  const result = signReleaseAssets({
    dir,
    keyId: process.env.GPG_KEY_ID,
    passphrase: process.env.GPG_PASSPHRASE,
    log: json ? () => {} : (line) => console.log(line),
  });
  const shownDir = relativeTo(REPO_ROOT, dir);
  if (!result.ok) {
    printRefusals(SCRIPT, result.errors);
    if (json) console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: false, errors: result.errors.map((e) => e.reason) }));
    return result.errors[0]?.exitCode ?? EXIT.REFUSED;
  }
  if (json) {
    console.log(
      JSON.stringify({ schema: 1, tool: SCRIPT, ok: true, dir: shownDir, fingerprint: result.fingerprint, signed: result.signed.map((e) => e.signature) }),
    );
  } else {
    console.log(`${SCRIPT}: PASS ${result.signed.length} signature(s) in ${shownDir} (key ${result.fingerprint})`);
  }
  return EXIT.OK;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = main();
}
