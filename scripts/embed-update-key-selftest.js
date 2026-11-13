#!/usr/bin/env node
/**
 * Self-test for scripts/embed-update-key.js against docs/failure-modes/release.md section M. Every case
 * works on a temporary copy of TrustedKeys.cs; the real file is never written. No network, no real keys.
 */
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { main } from "./embed-update-key.js";
import { generateKeyPair } from "./lib/minisign.js";
import { TRUSTED_KEYS_RELATIVE_PATH, matchEmbeddedKey, readTrustedKeysFile } from "./lib/trusted-keys.js";

const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const results = [];

function check(name, fn) {
  try {
    fn();
    results.push({ name, pass: true });
  } catch (error) {
    results.push({ name, pass: false, detail: error instanceof Error ? error.message : String(error) });
  }
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function expectRefusal(fn, reason) {
  try {
    fn();
  } catch (error) {
    assert(error.reason === reason, `expected ${reason}, got ${error.reason}: ${error.message}`);
    return error;
  }
  throw new Error(`expected refusal ${reason}, but it succeeded`);
}

const temp = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-embed-key-"));
const placeholderSource = fs.readFileSync(path.join(REPO_ROOT, TRUSTED_KEYS_RELATIVE_PATH), "utf8");

function freshRoot(name) {
  const root = path.join(temp, name);
  fs.mkdirSync(path.join(root, path.dirname(TRUSTED_KEYS_RELATIVE_PATH)), { recursive: true });
  fs.writeFileSync(path.join(root, TRUSTED_KEYS_RELATIVE_PATH), placeholderSource, "utf8");
  return root;
}

function releaseLikeKey(password) {
  // A non-test comment, like the one minisign and Tauri write, so the test-key refusal does not apply.
  return generateKeyPair({ comment: "untrusted comment: minisign public key", password, kdfProfile: "test" });
}

function tauriPub(pair, dir, name) {
  const file = path.join(dir, name);
  fs.writeFileSync(file, Buffer.from(pair.publicKeyText, "utf8").toString("base64"), "utf8");
  return file;
}

const quiet = () => {};
const noEnv = {};

try {
  check("Tauri base64 .pub embeds, flips IsPlaceholder, and reads back", () => {
    const root = freshRoot("tauri");
    const pair = releaseLikeKey();
    const pub = tauriPub(pair, temp, "tauri.key.pub");
    assert(main(["--pub", pub, "--root", root], noEnv, quiet) === 0, "exit 0");
    const read = readTrustedKeysFile(root);
    assert(read.ok && read.isPlaceholder === false && read.embedded.length === 1, "one real key");
    assert(matchEmbeddedKey(pair.publicKeyText, read.embedded).ok, "embedded key matches the pair");
  });

  check("raw minisign .pub text embeds", () => {
    const root = freshRoot("raw");
    const pair = releaseLikeKey();
    const pub = path.join(temp, "raw.pub");
    fs.writeFileSync(pub, pair.publicKeyText.replace(/\n/g, "\r\n"), "utf8");
    assert(main(["--pub", pub, "--root", root], noEnv, quiet) === 0, "exit 0");
    assert(matchEmbeddedKey(pair.publicKeyText, readTrustedKeysFile(root).embedded).ok, "matches");
  });

  check("M1 malformed .pub is refused and the file is unchanged", () => {
    const root = freshRoot("malformed");
    const pub = path.join(temp, "bad.pub");
    fs.writeFileSync(pub, "not a key at all", "utf8");
    expectRefusal(() => main(["--pub", pub, "--root", root], noEnv, quiet), "malformed-key");
    assert(fs.readFileSync(path.join(root, TRUSTED_KEYS_RELATIVE_PATH), "utf8") === placeholderSource, "unchanged");
  });

  check("M2 a BWC test key is refused", () => {
    const root = freshRoot("testkey");
    const pair = generateKeyPair();
    const pub = path.join(temp, "test.pub");
    fs.writeFileSync(pub, pair.publicKeyText, "utf8");
    expectRefusal(() => main(["--pub", pub, "--root", root], noEnv, quiet), "test-key");
  });

  check("--from-env derives the key from an encrypted Tauri secret key and password", () => {
    const root = freshRoot("fromenv");
    const pair = releaseLikeKey("correct horse");
    const env = {
      TAURI_SIGNING_PRIVATE_KEY: Buffer.from(pair.secretKeyText, "utf8").toString("base64"),
      TAURI_SIGNING_PRIVATE_KEY_PASSWORD: "correct horse",
    };
    const lines = [];
    assert(main(["--from-env", "--root", root], env, (line) => lines.push(line)) === 0, "exit 0");
    assert(matchEmbeddedKey(pair.publicKeyText, readTrustedKeysFile(root).embedded).ok, "matches");
    const output = lines.join("\n");
    assert(!output.includes("correct horse"), "password never printed");
    assert(!output.includes(env.TAURI_SIGNING_PRIVATE_KEY), "secret never printed");
  });

  check("M4 --from-env with a wrong password is refused", () => {
    const root = freshRoot("wrongpw");
    const pair = releaseLikeKey("right");
    const env = { TAURI_SIGNING_PRIVATE_KEY: pair.secretKeyText, TAURI_SIGNING_PRIVATE_KEY_PASSWORD: "wrong" };
    const error = expectRefusal(() => main(["--from-env", "--root", root], env, quiet), "wrong-password");
    assert(!error.message.includes("wrong") || !error.message.includes(pair.secretKeyText), "no secret in message");
  });

  check("M4 --from-env with no password for an encrypted key is refused", () => {
    const root = freshRoot("nopw");
    const pair = releaseLikeKey("pw");
    expectRefusal(
      () => main(["--from-env", "--root", root], { TAURI_SIGNING_PRIVATE_KEY: pair.secretKeyText }, quiet),
      "password-missing",
    );
  });

  check("M3 a .pub that differs from the .env signing key is refused", () => {
    const root = freshRoot("mismatch");
    const embedded = releaseLikeKey();
    const signing = releaseLikeKey();
    const pub = tauriPub(embedded, temp, "mismatch.pub");
    const env = { TAURI_SIGNING_PRIVATE_KEY: signing.secretKeyText };
    expectRefusal(() => main(["--pub", pub, "--root", root], env, quiet), "embedded-key-mismatch");
  });

  check("M5 a second key is refused without --rotate, added with --rotate, alone with --replace", () => {
    const root = freshRoot("rotate");
    const first = releaseLikeKey();
    const second = releaseLikeKey();
    main(["--pub", tauriPub(first, temp, "first.pub"), "--root", root], noEnv, quiet);
    const secondPub = tauriPub(second, temp, "second.pub");
    expectRefusal(() => main(["--pub", secondPub, "--root", root], noEnv, quiet), "key-already-embedded");
    main(["--pub", secondPub, "--root", root, "--rotate"], noEnv, quiet);
    assert(readTrustedKeysFile(root).embedded.length === 2, "two keys after --rotate");
    main(["--pub", secondPub, "--root", root, "--replace"], noEnv, quiet);
    const read = readTrustedKeysFile(root);
    assert(read.embedded.length === 1 && matchEmbeddedKey(second.publicKeyText, read.embedded).ok, "only second");
  });

  check("M6 the same key again is a no-op", () => {
    const root = freshRoot("same");
    const pair = releaseLikeKey();
    const pub = tauriPub(pair, temp, "same.pub");
    main(["--pub", pub, "--root", root], noEnv, quiet);
    const before = fs.readFileSync(path.join(root, TRUSTED_KEYS_RELATIVE_PATH), "utf8");
    assert(main(["--pub", pub, "--root", root], noEnv, quiet) === 0, "exit 0");
    assert(fs.readFileSync(path.join(root, TRUSTED_KEYS_RELATIVE_PATH), "utf8") === before, "unchanged");
  });

  check("--check writes nothing", () => {
    const root = freshRoot("check");
    const pub = tauriPub(releaseLikeKey(), temp, "check.pub");
    assert(main(["--pub", pub, "--root", root, "--check"], noEnv, quiet) === 0, "exit 0");
    assert(fs.readFileSync(path.join(root, TRUSTED_KEYS_RELATIVE_PATH), "utf8") === placeholderSource, "unchanged");
  });

  check("usage errors: neither or both sources, --rotate with --replace, unknown flag", () => {
    expectRefusal(() => main([], noEnv, quiet), "usage");
    expectRefusal(() => main(["--from-env", "--pub", "x"], noEnv, quiet), "usage");
    expectRefusal(() => main(["--from-env", "--rotate", "--replace"], noEnv, quiet), "usage");
    expectRefusal(() => main(["--bogus"], noEnv, quiet), "usage");
  });
} finally {
  fs.rmSync(temp, { recursive: true, force: true });
}

const failed = results.filter((result) => !result.pass);
for (const result of failed) console.error(`FAIL ${result.name}: ${result.detail}`);
console.log(`embed-update-key-selftest: ${failed.length === 0 ? "PASS" : "FAIL"} (${results.length - failed.length}/${results.length})`);
process.exitCode = failed.length === 0 ? 0 : 1;
