#!/usr/bin/env node
/**
 * Executable self-test for the release asset tools: gpg-sign.js, finalize-release-assets.js, and
 * validate-updater-live.js. Creates temporary folders, keys, and manifests, runs the real CLIs as
 * child processes, and proves the named refusal reasons.
 *
 * No network except 127.0.0.1, no gh, no real key, no keyring import. gpg is never run: the signer
 * is driven through a fake runner, except the missing-gpg case, which runs the real CLI with an
 * empty PATH. The result is written to artifacts/release/selftest/release-assets-result.json.
 */
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";

import { EXIT, repoRootFrom, writeJson } from "./lib/cli.js";
import { envelope, generateKeyPair, signFile } from "./lib/minisign.js";
import { buildManifest, serializeManifest } from "./lib/update-manifest.js";
import { isDirectExecutionOf } from "./direct-execution.mjs";
import { signReleaseAssets } from "./gpg-sign.js";
import { mirrorReleaseAssets } from "./finalize-release-assets.js";

const SCRIPT = "scripts/release-assets-selftest.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const RESULT_PATH = path.join(REPO_ROOT, "artifacts", "release", "selftest", "release-assets-result.json");
// The fixtures use the package version, so "--expected-version current" matches them after a version bump.
const VERSION = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, "package.json"), "utf8")).version;
const BETA_VERSION = `${VERSION}-beta.1`;
const FUTURE_EXPIRES = "2099-01-01T00:00:00Z";
const PAST_EXPIRES = "2020-01-02T00:00:00Z";
const PUBDATE = "2026-11-11T00:00:00Z";
const GPG_PASSPHRASE = "selftest-gpg-passphrase-never-printed";
const FAKE_FPR = "0123456789ABCDEF0123456789ABCDEF01234567";
const FAKE_SUBKEY_FPR = "1111111111111111111111111111111111111111";
const FAKE_KEY_ID16 = "89ABCDEF01234567";
const SETUP_X64 = `BetterWindowsConfig-${VERSION}-win-x64-setup.exe`;
const SETUP_ARM64 = `BetterWindowsConfig-${VERSION}-win-arm64-setup.exe`;
const RELEASE_ENV = [
  "GPG_KEY_ID",
  "GPG_PASSPHRASE",
  "AFTER_PACK_LOC",
  "OVERRIDE_BETA_MIRROR_SKIP",
  "UPDATER_LIVE_BASE_URL",
  "GH_REPO",
  "GH_REPO_OWNER",
  "GH_REPO_NAME",
];

/** The caller's environment minus release settings. `extra` adds exactly what a case sets. */
function childEnv(extra = {}, { emptyPath = null } = {}) {
  const env = {};
  for (const [name, value] of Object.entries(process.env)) {
    if (RELEASE_ENV.includes(name)) continue;
    if (emptyPath !== null && /^path$/i.test(name)) continue;
    env[name] = value;
  }
  if (emptyPath !== null) env.PATH = emptyPath;
  Object.assign(env, extra);
  return env;
}

/** Runs a CLI as a child process without blocking this event loop, so the local HTTP server can answer. */
function runChild(script, args, { cwd = REPO_ROOT, env = childEnv() } = {}) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [path.join(REPO_ROOT, script), ...args], {
      cwd,
      env,
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    const timer = setTimeout(() => child.kill(), 120_000);
    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });
    child.on("error", (error) => {
      clearTimeout(timer);
      resolve({ status: null, stdout, stderr: `${stderr}${error.message}\n` });
    });
    child.on("close", (status) => {
      clearTimeout(timer);
      resolve({ status, stdout, stderr });
    });
  });
}

function tail(result) {
  const text = `${result.stdout}\n${result.stderr}`;
  return text.split(/\r?\n/).filter(Boolean).slice(-4).join(" | ");
}

function record(cases, name, pass, detail = "", exitCode = null, matchedReasons = []) {
  cases.push({ name, pass: Boolean(pass), exitCode, matchedReasons, detail: pass ? "" : detail });
  return Boolean(pass);
}

/** A CLI case: the exit code is exact and every named reason appears in the output. */
function expectExit(cases, name, result, { status, reasons = [] }) {
  const text = `${result.stdout}\n${result.stderr}`;
  const matched = reasons.filter((reason) => text.includes(`[${reason}]`));
  const pass = result.status === status && matched.length === reasons.length;
  return record(cases, name, pass, `exit ${result.status} (want ${status}); ${tail(result)}`, result.status, matched);
}

function write(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, value);
}

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function sha256File(filePath) {
  return sha256(fs.readFileSync(filePath));
}

/** Names of hidden temporary siblings left in a folder. A clean run leaves none. */
function hiddenEntries(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory).filter((name) => name.startsWith("."));
}

// ---------------------------------------------------------------------------------------------
// gpg-sign.js
// ---------------------------------------------------------------------------------------------

function secretListing(fingerprint) {
  return [
    "sec:u:4096:1:89ABCDEF01234567:1762905600:::u:::scESC:::::::::0:",
    `fpr:::::::::${fingerprint}:`,
    "uid:u::::1762905600::HASH::selftest <selftest@example.invalid>::::::::::0:",
    "",
  ].join("\n");
}

/**
 * A fake gpg. Records every call (args, stdin, env) and answers each subcommand from the options.
 * It writes a placeholder signature file where the real gpg would, so nothing here is a signature.
 */
function makeFakeGpg(options = {}) {
  const calls = [];
  const primary = options.primary ?? FAKE_FPR;
  const runGpg = (args, extra = {}) => {
    calls.push({ args: [...args], input: extra.input, env: extra.env });
    if (args[0] === "--version") {
      if (options.missing) {
        return { status: null, stdout: "", stderr: "", error: Object.assign(new Error("spawn gpg ENOENT"), { code: "ENOENT" }) };
      }
      return { status: 0, stdout: "gpg (fake)\n", stderr: "", error: null };
    }
    if (args.includes("--list-secret-keys")) {
      if (options.listStatus !== undefined) {
        return { status: options.listStatus, stdout: options.listOutput ?? "", stderr: "gpg: no secret key\n", error: null };
      }
      const listing = options.listOutput ?? secretListing(options.fingerprint ?? FAKE_FPR);
      return { status: 0, stdout: listing, stderr: "", error: null };
    }
    if (args.includes("--detach-sign")) {
      if (options.signStatus !== undefined) {
        return { status: options.signStatus, stdout: "", stderr: options.signStderr ?? "", error: null };
      }
      if (!options.signNoOutput) {
        const output = args[args.indexOf("--output") + 1];
        fs.writeFileSync(output, `-----BEGIN PGP SIGNATURE-----\nfake ${path.basename(args.at(-1))}\n-----END PGP SIGNATURE-----\n`);
      }
      return { status: 0, stdout: "", stderr: "", error: null };
    }
    if (args.includes("--verify")) {
      if (options.verifyOutput !== undefined) {
        return { status: options.verifyStatus ?? 1, stdout: options.verifyOutput, stderr: "", error: null };
      }
      const status = [
        "[GNUPG:] GOODSIG 89ABCDEF01234567 selftest <selftest@example.invalid>",
        `[GNUPG:] VALIDSIG ${FAKE_SUBKEY_FPR} 2026-11-12 1762905600 0 4 0 1 8 00 ${primary}`,
        "",
      ].join("\n");
      return { status: 0, stdout: status, stderr: "", error: null };
    }
    return { status: 2, stdout: "", stderr: "unexpected fake gpg call", error: null };
  };
  return { runGpg, calls };
}

/** A release folder with the files assemble-bundle writes, plus two stale signatures. */
function gpgFixture(root) {
  const dir = path.join(root, "release");
  write(path.join(dir, SETUP_X64), "installer bytes\n");
  write(path.join(dir, "bwc-stable.json"), "{\"schema\":1}\n");
  write(path.join(dir, "bwc-stable.json.minisig"), "untrusted comment: signature\n");
  write(path.join(dir, `${SETUP_X64}.sig`), "c2lnCg==\n");
  write(path.join(dir, "assembly.json"), "{}\n");
  write(path.join(dir, "bwc-stable.json.asc"), "OLD SIGNATURE\n");
  write(path.join(dir, "stale.exe.asc"), "STALE\n");
  return dir;
}

const SIGNABLE = [SETUP_X64, "bwc-stable.json", "bwc-stable.json.minisig", `${SETUP_X64}.sig`, "assembly.json"];

async function runGpgCases(temp, cases) {
  // Real CLI runs: missing gpg, missing or invalid environment, help, unknown option.
  const dir = gpgFixture(path.join(temp, "gpg-cli"));
  const emptyPath = path.join(temp, "empty-path");
  fs.mkdirSync(emptyPath, { recursive: true });
  const withKey = { GPG_KEY_ID: FAKE_FPR, GPG_PASSPHRASE };

  expectExit(
    cases,
    "CLI: gpg missing from PATH refuses with gpg-missing",
    await runChild("scripts/gpg-sign.js", ["--dir", dir], { cwd: emptyPath, env: childEnv(withKey, { emptyPath }) }),
    { status: EXIT.EXTERNAL, reasons: ["gpg-missing"] },
  );
  record(
    cases,
    "CLI: gpg missing leaves no signature behind",
    !fs.existsSync(path.join(dir, `${SETUP_X64}.asc`)) && hiddenEntries(dir).length === 0,
    "a signature or temporary file was written with no gpg",
  );
  expectExit(
    cases,
    "CLI: GPG_KEY_ID missing refuses with key-id-missing",
    await runChild("scripts/gpg-sign.js", ["--dir", dir], { env: childEnv({ GPG_PASSPHRASE }) }),
    { status: EXIT.USAGE, reasons: ["key-id-missing"] },
  );
  expectExit(
    cases,
    "CLI: GPG_KEY_ID as an email is refused with key-id-invalid",
    await runChild("scripts/gpg-sign.js", ["--dir", dir], { env: childEnv({ GPG_KEY_ID: "selftest@example.invalid", GPG_PASSPHRASE }) }),
    { status: EXIT.USAGE, reasons: ["key-id-invalid"] },
  );
  expectExit(
    cases,
    "CLI: GPG_PASSPHRASE missing refuses with passphrase-missing",
    await runChild("scripts/gpg-sign.js", ["--dir", dir], { env: childEnv({ GPG_KEY_ID: FAKE_FPR }) }),
    { status: EXIT.USAGE, reasons: ["passphrase-missing"] },
  );
  const help = await runChild("scripts/gpg-sign.js", ["--help"], { env: childEnv() });
  record(cases, "CLI: --help prints usage and exits 0", help.status === 0 && help.stdout.includes("Usage"), tail(help), help.status);
  expectExit(
    cases,
    "CLI: unknown option is a usage error",
    await runChild("scripts/gpg-sign.js", ["--bogus"], { env: childEnv() }),
    { status: EXIT.USAGE },
  );

  // Fake runner: the passphrase never reaches argv, logs, or errors.
  const happyRoot = path.join(temp, "gpg-happy");
  const happyDir = gpgFixture(happyRoot);
  const happy = makeFakeGpg();
  const logLines = [];
  const happyResult = await signReleaseAssets({
    dir: happyDir,
    keyId: FAKE_KEY_ID16,
    passphrase: GPG_PASSPHRASE,
    runGpg: happy.runGpg,
    log: (line) => logLines.push(line),
  });
  const signedNames = (happyResult.signed ?? []).map((entry) => entry.file).sort();
  record(
    cases,
    "gpg: every non-signature file in the folder is signed and verified",
    happyResult.ok && JSON.stringify(signedNames) === JSON.stringify([...SIGNABLE].sort()),
    `ok=${happyResult.ok} signed=${signedNames.join(",")} errors=${JSON.stringify(happyResult.errors)}`,
  );
  record(
    cases,
    "gpg: each signature is <asset>.asc and holds the signer output",
    SIGNABLE.every((name) => fs.existsSync(path.join(happyDir, `${name}.asc`))) &&
      fs.readFileSync(path.join(happyDir, "bwc-stable.json.asc"), "utf8").includes("BEGIN PGP SIGNATURE"),
    "a .asc file is missing or does not hold the signer output",
  );
  record(
    cases,
    "gpg: stale .asc files are not signed and the ones for signed assets are replaced",
    fs.readFileSync(path.join(happyDir, "stale.exe.asc"), "utf8") === "STALE\n" &&
      !fs.existsSync(path.join(happyDir, "stale.exe.asc.asc")) &&
      !fs.readFileSync(path.join(happyDir, "bwc-stable.json.asc"), "utf8").includes("OLD"),
    "stale signature handling is wrong",
  );
  record(cases, "gpg: no temporary files remain after a clean run", hiddenEntries(happyDir).length === 0, hiddenEntries(happyDir).join(","));

  const signCalls = happy.calls.filter((call) => call.args.includes("--detach-sign"));
  const verifyCalls = happy.calls.filter((call) => call.args.includes("--verify"));
  record(
    cases,
    "gpg: signing uses loopback pinentry, stdin passphrase, armored detached output",
    signCalls.length === SIGNABLE.length &&
      signCalls.every((call) =>
        ["--armor", "--batch", "--yes"].every((flag) => call.args.includes(flag)) &&
        call.args.join(" ").includes("--pinentry-mode loopback --passphrase-fd 0") &&
        call.args[call.args.indexOf("--local-user") + 1] === FAKE_FPR &&
        call.input === `${GPG_PASSPHRASE}\n`,
      ),
    "a sign call lacks a required flag, the signing key, or the stdin passphrase",
  );
  record(
    cases,
    "gpg: every signature is verified with --status-fd and the primary fingerprint is checked",
    verifyCalls.length === SIGNABLE.length && verifyCalls.every((call) => call.args.includes("--status-fd")),
    `verify calls=${verifyCalls.length}`,
  );
  record(
    cases,
    "gpg: the passphrase is in no argv, no environment, and no log line",
    happy.calls.every((call) => call.args.every((arg) => !arg.includes(GPG_PASSPHRASE))) &&
      happy.calls.every((call) => !call.env || !("GPG_PASSPHRASE" in call.env)) &&
      logLines.every((line) => !line.includes(GPG_PASSPHRASE)) &&
      !JSON.stringify(happyResult).includes(GPG_PASSPHRASE),
    "the passphrase appeared in argv, the child environment, a log line, or the result",
  );

  async function refusal(name, options, keyId, reason, status, { passphrase = GPG_PASSPHRASE, dirOverride } = {}) {
    const root = path.join(temp, `gpg-${name.replace(/\W+/g, "-")}`);
    const target = dirOverride ?? gpgFixture(root);
    const fake = makeFakeGpg(options);
    const result = await signReleaseAssets({ dir: target, keyId, passphrase, runGpg: fake.runGpg, log: () => {} });
    const first = result.errors?.[0] ?? {};
    const pass = !result.ok && first.reason === reason && first.exitCode === status;
    record(
      cases,
      name,
      pass,
      `ok=${result.ok} reason=${first.reason} exit=${first.exitCode} (want ${reason}/${status})`,
      first.exitCode ?? null,
      first.reason ? [first.reason] : [],
    );
    return { result, fake, dir: target, first };
  }

  await refusal("gpg: missing key id is refused before gpg runs", {}, undefined, "key-id-missing", EXIT.USAGE);
  await refusal("gpg: malformed key id is refused", {}, "NOTHEX", "key-id-invalid", EXIT.USAGE);
  await refusal("gpg: missing passphrase is refused", {}, FAKE_KEY_ID16, "passphrase-missing", EXIT.USAGE, { passphrase: "" });
  const missing = await refusal("gpg: gpg missing is refused with gpg-missing", { missing: true }, FAKE_KEY_ID16, "gpg-missing", EXIT.EXTERNAL);
  record(cases, "gpg: gpg missing never reaches the key lookup", missing.fake.calls.length === 1, `calls=${missing.fake.calls.length}`);
  await refusal("gpg: no secret key for the key id is key-not-found", { listStatus: 2, listOutput: "" }, FAKE_KEY_ID16, "key-not-found", EXIT.MISSING);
  await refusal(
    "gpg: two secret keys are key-ambiguous",
    { listOutput: `${secretListing(FAKE_FPR)}${secretListing("ABCDEF0123456789ABCDEF0123456789ABCDEF01")}` },
    FAKE_KEY_ID16,
    "key-ambiguous",
    EXIT.REFUSED,
  );
  const mismatch = await refusal("gpg: a key whose fingerprint does not match GPG_KEY_ID is key-mismatch", {}, "FFFFFFFFFFFFFFFF", "key-mismatch", EXIT.REFUSED);
  record(cases, "gpg: key-mismatch signs nothing", !mismatch.fake.calls.some((call) => call.args.includes("--detach-sign")), "a sign call ran");

  const leakRoot = path.join(temp, "gpg-leak");
  const leakDir = gpgFixture(leakRoot);
  const leakFake = makeFakeGpg({ signStatus: 2, signStderr: `bad passphrase ${GPG_PASSPHRASE} for key\n` });
  const leak = await signReleaseAssets({ dir: leakDir, keyId: FAKE_KEY_ID16, passphrase: GPG_PASSPHRASE, runGpg: leakFake.runGpg, log: () => {} });
  record(
    cases,
    "gpg: a gpg error that echoes the passphrase is redacted",
    !leak.ok && !JSON.stringify(leak).includes(GPG_PASSPHRASE) && JSON.stringify(leak).includes("[redacted]"),
    `result=${JSON.stringify(leak)}`,
  );
  record(
    cases,
    "gpg: a failed sign keeps the older .asc and leaves no temporary file",
    fs.readFileSync(path.join(leakDir, "bwc-stable.json.asc"), "utf8") === "OLD SIGNATURE\n" && hiddenEntries(leakDir).length === 0,
    "older signature changed or a temporary file remained",
  );
  await refusal("gpg: a sign that exits non-zero is sign-failed", { signStatus: 2, signStderr: "boom" }, FAKE_KEY_ID16, "sign-failed", EXIT.EXTERNAL);
  await refusal("gpg: a sign that exits 0 but writes nothing is sign-failed", { signNoOutput: true }, FAKE_KEY_ID16, "sign-failed", EXIT.EXTERNAL);
  const badVerify = await refusal(
    "gpg: a BADSIG verification is verify-failed",
    { verifyStatus: 1, verifyOutput: "[GNUPG:] BADSIG 89ABCDEF01234567 selftest\n" },
    FAKE_KEY_ID16,
    "verify-failed",
    EXIT.REFUSED,
  );
  record(
    cases,
    "gpg: a failed verify never creates the final .asc name",
    !fs.existsSync(path.join(badVerify.dir, `${SETUP_X64}.asc`)) && hiddenEntries(badVerify.dir).length === 0,
    "a final signature or temp file remains after a failed verify",
  );
  await refusal("gpg: a signature by another primary key is signer-mismatch", { primary: "ABCDEF0123456789ABCDEF0123456789ABCDEF01" }, FAKE_KEY_ID16, "signer-mismatch", EXIT.REFUSED);

  await refusal("gpg: a missing release folder is release-dir-missing", {}, FAKE_KEY_ID16, "release-dir-missing", EXIT.MISSING, {
    dirOverride: path.join(temp, "no-such-folder"),
  });
  const emptyDir = path.join(temp, "gpg-empty", "release");
  fs.mkdirSync(emptyDir, { recursive: true });
  await refusal("gpg: a folder with nothing to sign is nothing-to-sign", {}, FAKE_KEY_ID16, "nothing-to-sign", EXIT.MISSING, { dirOverride: emptyDir });

  const linkRoot = path.join(temp, "gpg-symlink");
  const linkDir = path.join(linkRoot, "release");
  fs.mkdirSync(linkDir, { recursive: true });
  write(path.join(linkRoot, "outside.txt"), "outside\n");
  fs.symlinkSync(path.join(linkRoot, "outside.txt"), path.join(linkDir, "linked.txt"), "file");
  await refusal("gpg: a symlink in the folder is symlink-refused", {}, FAKE_KEY_ID16, "symlink-refused", EXIT.REFUSED, { dirOverride: linkDir });

  const subRoot = path.join(temp, "gpg-subdir", "release");
  fs.mkdirSync(path.join(subRoot, "nested"), { recursive: true });
  write(path.join(subRoot, "top.exe"), "x");
  await refusal("gpg: a subdirectory in the folder is unexpected-directory", {}, FAKE_KEY_ID16, "unexpected-directory", EXIT.REFUSED, { dirOverride: subRoot });
}

// ---------------------------------------------------------------------------------------------
// finalize-release-assets.js
// ---------------------------------------------------------------------------------------------

/** A release folder for VERSION with the files a bundle has, to be mirrored. */
function releaseFixture(root, version = VERSION) {
  const dir = path.join(root, "src", version, "stable");
  write(path.join(dir, SETUP_X64), "x64 installer bytes for the mirror\n");
  write(path.join(dir, "bwc-stable.json"), `${JSON.stringify({ schema: 1, version }, null, 2)}\n`);
  write(path.join(dir, "bwc-stable.json.minisig"), "untrusted comment: mirror test\n");
  write(path.join(dir, `${SETUP_X64}.sig`), "bWlycm9yCg==\n");
  write(path.join(dir, "assembly.json"), "{\"ok\":true}\n");
  return dir;
}

const RELEASE_FILES = [SETUP_X64, "bwc-stable.json", "bwc-stable.json.minisig", `${SETUP_X64}.sig`, "assembly.json"];

async function runMirrorCases(temp, cases) {
  const src = releaseFixture(path.join(temp, "mirror-src"));
  const srcRoot = path.dirname(path.dirname(src));
  const freshDest = () => path.join(temp, `dest-${Math.random().toString(16).slice(2, 10)}`);
  const withDest = (destination, extra = {}) => ({ AFTER_PACK_LOC: destination, ...extra });

  // Stable needs AFTER_PACK_LOC; beta is skipped unless the override is exactly 1.
  expectExit(
    cases,
    "mirror: stable without AFTER_PACK_LOC refuses with after-pack-loc-missing",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], { env: childEnv() }),
    { status: EXIT.REFUSED, reasons: ["after-pack-loc-missing"] },
  );
  const betaDest = freshDest();
  const betaSkip = await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", BETA_VERSION], {
    env: childEnv(withDest(betaDest)),
  });
  record(
    cases,
    "mirror: beta without OVERRIDE_BETA_MIRROR_SKIP=1 is skipped with exit 0",
    betaSkip.status === 0 && betaSkip.stdout.includes("skipped-beta") && !fs.existsSync(betaDest),
    tail(betaSkip),
    betaSkip.status,
  );
  const betaNotOne = await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", BETA_VERSION], {
    env: childEnv(withDest(betaDest, { OVERRIDE_BETA_MIRROR_SKIP: "true" })),
  });
  record(
    cases,
    "mirror: beta with OVERRIDE_BETA_MIRROR_SKIP=true (not 1) is still skipped",
    betaNotOne.status === 0 && betaNotOne.stdout.includes("skipped-beta") && !fs.existsSync(betaDest),
    tail(betaNotOne),
    betaNotOne.status,
  );
  const betaOverrideDest = freshDest();
  const betaOverride = await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", BETA_VERSION], {
    env: childEnv(withDest(betaOverrideDest, { OVERRIDE_BETA_MIRROR_SKIP: "1" })),
  });
  record(
    cases,
    "mirror: beta with OVERRIDE_BETA_MIRROR_SKIP=1 mirrors into <dest>/<beta version>",
    betaOverride.status === 0 && RELEASE_FILES.every((name) => fs.existsSync(path.join(betaOverrideDest, BETA_VERSION, name))),
    tail(betaOverride),
    betaOverride.status,
  );

  // Destination refusals, each as a real CLI run that must not create anything.
  expectExit(
    cases,
    "mirror: relative AFTER_PACK_LOC refuses with destination-relative",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], { env: childEnv(withDest("mirror-out")) }),
    { status: EXIT.USAGE, reasons: ["destination-relative"] },
  );
  const inRepo = path.join(REPO_ROOT, "artifacts", "selftest-mirror-refused");
  expectExit(
    cases,
    "mirror: destination inside the repository refuses with destination-in-repo",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], { env: childEnv(withDest(inRepo)) }),
    { status: EXIT.REFUSED, reasons: ["destination-in-repo"] },
  );
  record(cases, "mirror: a refused in-repo destination is not created", !fs.existsSync(inRepo), "the refused folder exists");

  const realDest = path.join(temp, "real-dest");
  fs.mkdirSync(realDest, { recursive: true });
  const linkDest = path.join(temp, "link-dest");
  fs.symlinkSync(realDest, linkDest, "junction");
  expectExit(
    cases,
    "mirror: a symlink or junction destination refuses with destination-symlink",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], { env: childEnv(withDest(linkDest)) }),
    { status: EXIT.REFUSED, reasons: ["destination-symlink"] },
  );
  record(cases, "mirror: a refused symlink destination receives no copy", !fs.existsSync(path.join(realDest, VERSION)), "a copy reached the link target");

  expectExit(
    cases,
    "mirror: destination equal to the release folder refuses with destination-in-release",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], { env: childEnv(withDest(src)) }),
    { status: EXIT.REFUSED, reasons: ["destination-in-release"] },
  );
  expectExit(
    cases,
    "mirror: destination inside the release folder refuses with destination-in-release",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], { env: childEnv(withDest(path.join(src, "inner"))) }),
    { status: EXIT.REFUSED, reasons: ["destination-in-release"] },
  );
  expectExit(
    cases,
    "mirror: destination whose <version> folder would hold the release folder refuses with destination-overlaps-release",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], { env: childEnv(withDest(srcRoot)) }),
    { status: EXIT.REFUSED, reasons: ["destination-overlaps-release"] },
  );
  const fileDest = path.join(temp, "dest-is-a-file");
  write(fileDest, "not a folder\n");
  expectExit(
    cases,
    "mirror: destination that is a file refuses with destination-not-directory",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], { env: childEnv(withDest(fileDest)) }),
    { status: EXIT.REFUSED, reasons: ["destination-not-directory"] },
  );
  expectExit(
    cases,
    "mirror: a filesystem root destination refuses with destination-root",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], { env: childEnv(withDest(path.parse(temp).root)) }),
    { status: EXIT.REFUSED, reasons: ["destination-root"] },
  );
  expectExit(
    cases,
    "mirror: a version that is not SemVer refuses with version-invalid",
    await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", "v0.1.0"], { env: childEnv(withDest(freshDest())) }),
    { status: EXIT.USAGE, reasons: ["version-invalid"] },
  );
  expectExit(
    cases,
    "mirror: a missing release folder refuses with release-dir-missing",
    await runChild("scripts/finalize-release-assets.js", ["--dir", path.join(temp, "missing-src"), "--version", VERSION], {
      env: childEnv(withDest(freshDest())),
    }),
    { status: EXIT.MISSING, reasons: ["release-dir-missing"] },
  );

  const linkSrc = releaseFixture(path.join(temp, "mirror-linked"));
  fs.symlinkSync(path.join(temp, "outside-target.txt"), path.join(linkSrc, "linked.txt"), "file");
  write(path.join(temp, "outside-target.txt"), "outside\n");
  expectExit(
    cases,
    "mirror: a symlink in the release folder refuses with symlink-refused",
    await runChild("scripts/finalize-release-assets.js", ["--dir", linkSrc, "--version", VERSION], { env: childEnv(withDest(freshDest())) }),
    { status: EXIT.REFUSED, reasons: ["symlink-refused"] },
  );

  // An unmapped drive letter is a refusal with a reason, never an unhandled exception.
  const unmappedDrive = ["Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"].map((letter) => `${letter}:\\`).find((root) => !fs.existsSync(root));
  if (unmappedDrive) {
    const unmappedDest = `${unmappedDrive}BWC-mirror`;
    const unmapped = await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION], {
      env: childEnv(withDest(unmappedDest)),
    });
    expectExit(cases, "mirror: an unmapped drive in AFTER_PACK_LOC refuses with after-pack-loc-unavailable", unmapped, {
      status: EXIT.REFUSED,
      reasons: ["after-pack-loc-unavailable"],
    });
    record(cases, "mirror: a refused unmapped destination is not created", !fs.existsSync(unmappedDest), "a folder was created");
    const direct = await mirrorReleaseAssets({ releaseDir: src, destination: unmappedDest, version: VERSION, repoRoot: REPO_ROOT });
    record(
      cases,
      "mirror: mirrorReleaseAssets returns the refusal instead of throwing for an unmapped drive",
      direct.ok === false && direct.reason === "after-pack-loc-unavailable",
      JSON.stringify(direct),
    );
  } else {
    record(cases, "mirror: unmapped drive case skipped (Q: to Z: are all mapped on this machine)", true, "skipped");
  }

  // A real mirror: copies, verifies, replaces only the same-name file, keeps everything else.
  const dest = freshDest();
  write(path.join(dest, VERSION, "bwc-stable.json"), "OLD MANIFEST\n");
  write(path.join(dest, VERSION, "unrelated.txt"), "keep me\n");
  write(path.join(dest, "0.0.9", "keep.txt"), "older release\n");
  const copy = await runChild("scripts/finalize-release-assets.js", ["--dir", src, "--version", VERSION, "--json"], {
    env: childEnv(withDest(dest)),
  });
  const copyOk = copy.status === 0 && RELEASE_FILES.every((name) => {
    const target = path.join(dest, VERSION, name);
    return fs.existsSync(target) && sha256File(target) === sha256File(path.join(src, name));
  });
  record(cases, "mirror: a real mirror copies every file with matching SHA-256", copyOk, tail(copy), copy.status);
  record(
    cases,
    "mirror: the same-name file is replaced and unrelated files in the same and other versions are kept",
    fs.readFileSync(path.join(dest, VERSION, "unrelated.txt"), "utf8") === "keep me\n" &&
      fs.readFileSync(path.join(dest, "0.0.9", "keep.txt"), "utf8") === "older release\n" &&
      fs.readFileSync(path.join(dest, VERSION, "bwc-stable.json"), "utf8") !== "OLD MANIFEST\n",
    "an unrelated file was lost or the same-name file was not replaced",
  );
  record(cases, "mirror: no hidden staging file remains after a real mirror", hiddenEntries(path.join(dest, VERSION)).length === 0, "a .mirror staging file remains");

  // In-process: the copy-verify-swap keeps the old file when the copy is wrong or fails.
  const guardDest = freshDest();
  write(path.join(guardDest, VERSION, SETUP_X64), "previous good copy\n");
  const corrupt = await mirrorReleaseAssets({
    releaseDir: src,
    destination: guardDest,
    version: VERSION,
    repoRoot: REPO_ROOT,
    copyFile: (from, to) => fs.writeFileSync(to, "corrupted bytes\n"),
  });
  record(
    cases,
    "mirror: a copy that does not verify is verify-failed and the previous file survives",
    !corrupt.ok &&
      corrupt.reason === "verify-failed" &&
      fs.readFileSync(path.join(guardDest, VERSION, SETUP_X64), "utf8") === "previous good copy\n" &&
      hiddenEntries(path.join(guardDest, VERSION)).length === 0,
    `result=${JSON.stringify({ ok: corrupt.ok, reason: corrupt.reason })}`,
    null,
    corrupt.reason ? [corrupt.reason] : [],
  );
  const failingDest = freshDest();
  const failing = await mirrorReleaseAssets({
    releaseDir: src,
    destination: failingDest,
    version: VERSION,
    repoRoot: REPO_ROOT,
    copyFile: () => {
      throw Object.assign(new Error("simulated EIO"), { code: "EIO" });
    },
  });
  record(
    cases,
    "mirror: a copy error is copy-failed with exit 4 and nothing is left behind",
    !failing.ok && failing.reason === "copy-failed" && failing.exitCode === EXIT.EXTERNAL && hiddenEntries(path.join(failingDest, VERSION)).length === 0,
    `result=${JSON.stringify({ ok: failing.ok, reason: failing.reason, exitCode: failing.exitCode })}`,
    failing.exitCode ?? null,
    failing.reason ? [failing.reason] : [],
  );

  const help = await runChild("scripts/finalize-release-assets.js", ["--help"], { env: childEnv() });
  record(cases, "mirror: --help prints usage and exits 0", help.status === 0 && help.stdout.includes("Usage"), tail(help), help.status);
}

// ---------------------------------------------------------------------------------------------
// validate-updater-live.js (local HTTP server on 127.0.0.1 only)
// ---------------------------------------------------------------------------------------------

/** A TrustedKeys.cs that embeds the given keys (or the placeholder list) under a temporary root. */
function writeTrustedKeysRoot(root, { placeholder = false, embedded = [] } = {}) {
  const csLiteral = (text) => `"${text.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\r/g, "\\r").replace(/\n/g, "\\n")}"`;
  const constants = embedded.map((text, index) => `    private const string Key${index} = ${csLiteral(text)};`);
  const list = embedded.map((_, index) => `Key${index}`).join(", ");
  write(
    path.join(root, "src", "Bwc.Update", "Signing", "TrustedKeys.cs"),
    [
      "namespace Bwc.Update;",
      "",
      "public static class TrustedKeys",
      "{",
      `    public const bool IsPlaceholder = ${placeholder ? "true" : "false"};`,
      ...constants,
      `    public static readonly IReadOnlyList<string> Embedded = [${list}];`,
      "}",
      "",
    ].join("\n"),
  );
  return root;
}

function startRouteServer() {
  const routes = new Map();
  const server = http.createServer((request, response) => {
    const route = routes.get(decodeURIComponent(request.url.split("?")[0]));
    if (!route) {
      response.writeHead(404, { "content-type": "text/plain" });
      response.end("not found");
      return;
    }
    response.writeHead(route.status ?? 200, { "content-type": "application/octet-stream", "content-length": route.body.length });
    response.end(route.body);
  });
  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => resolve({ server, routes, port: server.address().port }));
  });
}

async function runLiveCases(temp, cases) {
  const { server, routes, port } = await startRouteServer();
  const origin = `http://127.0.0.1:${port}`;
  const latest = `${origin}/latest`;
  const download = `${origin}/download/v${VERSION}`;
  try {
    const keyA = generateKeyPair({ comment: "untrusted comment: BWC selftest live key A" });
    const keyB = generateKeyPair({ comment: "untrusted comment: BWC selftest live key B" });
    const rootA = writeTrustedKeysRoot(path.join(temp, "live-root-a"), { embedded: [keyA.publicKeyText] });
    const rootPlaceholder = writeTrustedKeysRoot(path.join(temp, "live-root-placeholder"), {
      placeholder: true,
      embedded: ["untrusted comment: PLACEHOLDER replace with the release public key\nPLACEHOLDER-NOT-A-KEY"],
    });

    const x64Bytes = Buffer.from(Array.from({ length: 4096 }, (_, index) => (index * 17 + 3) & 0xff));
    const arm64Bytes = Buffer.from(Array.from({ length: 2048 }, (_, index) => (index * 29 + 11) & 0xff));
    const stage = path.join(temp, "live-stage");
    const x64Path = path.join(stage, SETUP_X64);
    const arm64Path = path.join(stage, SETUP_ARM64);
    write(x64Path, x64Bytes);
    write(arm64Path, arm64Bytes);
    const x64Sig = envelope(await signFile(x64Path, keyA.secretKeyText, `version:${VERSION} arch:win-x64`));
    const arm64Sig = envelope(await signFile(arm64Path, keyA.secretKeyText, `version:${VERSION} arch:win-arm64`));

    const installer = (name, bytes, sig, arch) => ({
      url: `${download}/${encodeURIComponent(name)}`,
      size: bytes.length,
      sha256: sha256(bytes),
      signature: sig,
      arch,
    });
    const both = {
      "win-x64": installer(SETUP_X64, x64Bytes, x64Sig, "win-x64"),
      "win-arm64": installer(SETUP_ARM64, arm64Bytes, arm64Sig, "win-arm64"),
    };
    const stripArch = (entries) =>
      Object.fromEntries(Object.entries(entries).map(([arch, entry]) => [arch, { url: entry.url, size: entry.size, sha256: entry.sha256, signature: entry.signature }]));

    async function signedManifest(installers, { version = VERSION, expires = FUTURE_EXPIRES, key = keyA } = {}) {
      const manifest = buildManifest({ version, channel: "stable", pubDate: PUBDATE, installers: stripArch(installers) });
      const bytes = serializeManifest(manifest);
      const manifestPath = path.join(stage, `bwc-stable-${Math.random().toString(16).slice(2, 8)}.json`);
      write(manifestPath, bytes);
      const sig = await signFile(manifestPath, key.secretKeyText, `version:${version} channel:stable expires:${expires}`);
      return { bytes, sig };
    }

    const good = await signedManifest(both);
    function publish(manifest, { x64 = x64Bytes, arm64 = arm64Bytes, status = 200 } = {}) {
      routes.set("/latest/bwc-stable.json", { body: manifest.bytes, status });
      routes.set("/latest/bwc-stable.json.minisig", { body: Buffer.from(manifest.sig, "utf8") });
      routes.set(`/download/v${VERSION}/${SETUP_X64}`, { body: x64 });
      routes.set(`/download/v${VERSION}/${SETUP_ARM64}`, { body: arm64 });
    }

    const baseEnv = (extra = {}) => childEnv({ UPDATER_LIVE_BASE_URL: latest, ...extra });
    const liveArgs = (root, expected = VERSION) => ["--root", root, "--expected-version", expected];
    const TRUSTED_A_ROOT = rootA;

    publish(good);
    const pass = await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() });
    record(cases, "live: a good bundle passes with exit 0", pass.status === 0 && pass.stdout.includes("ok"), tail(pass), pass.status);

    const current = await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT, "current"), { env: baseEnv() });
    record(cases, `live: --expected-version current reads package.json (${VERSION}) and passes`, current.status === 0, tail(current), current.status);

    const jsonPass = await runChild("scripts/validate-updater-live.js", [...liveArgs(TRUSTED_A_ROOT), "--json"], { env: baseEnv() });
    let jsonOk = false;
    try {
      jsonOk = JSON.parse(jsonPass.stdout.trim().split(/\r?\n/).at(-1)).ok === true;
    } catch {
      jsonOk = false;
    }
    record(cases, "live: --json prints one object with ok true", jsonPass.status === 0 && jsonOk, tail(jsonPass), jsonPass.status);

    expectExit(
      cases,
      "live: a wrong expected version refuses with version-mismatch",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT, "9.9.9"), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["version-mismatch"] },
    );

    const tampered = Buffer.from(good.bytes.toString("utf8").replace(PUBDATE, "2026-11-10T00:00:00Z"), "utf8");
    publish({ bytes: tampered, sig: good.sig });
    expectExit(
      cases,
      "live: a manifest changed after signing refuses with signature-invalid",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["signature-invalid"] },
    );

    const untrusted = await signedManifest(both, { key: keyB });
    publish(untrusted);
    expectExit(
      cases,
      "live: a manifest signed by a key that is not embedded refuses with wrong-key",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["wrong-key"] },
    );

    const badHash = Buffer.from(x64Bytes);
    badHash[badHash.length - 1] ^= 0xff;
    publish(good, { x64: badHash });
    expectExit(
      cases,
      "live: an installer whose bytes differ from the manifest hash refuses with installer-hash-mismatch",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["installer-hash-mismatch"] },
    );

    publish(good, { x64: Buffer.concat([x64Bytes, Buffer.from("X")]) });
    expectExit(
      cases,
      "live: an installer longer than the manifest size refuses with installer-size-mismatch",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["installer-size-mismatch"] },
    );

    const expired = await signedManifest(both, { expires: PAST_EXPIRES });
    publish(expired);
    expectExit(
      cases,
      "live: a manifest whose signed expiry has passed refuses with expired",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["expired"] },
    );

    const x64Only = await signedManifest({ "win-x64": both["win-x64"] });
    publish(x64Only);
    expectExit(
      cases,
      "live: a missing architecture refuses with missing-architecture",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["missing-architecture"] },
    );

    const plainHttpInstaller = await signedManifest({
      "win-x64": { ...both["win-x64"], url: `http://example.invalid/download/v${VERSION}/${SETUP_X64}` },
      "win-arm64": both["win-arm64"],
    });
    publish(plainHttpInstaller);
    expectExit(
      cases,
      "live: a plain http installer URL outside loopback is refused before any download with url-rejected",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["url-rejected"] },
    );

    publish(good, { status: 404 });
    expectExit(
      cases,
      "live: a manifest that answers 404 refuses with http-status",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["http-status"] },
    );

    publish(good);
    routes.delete("/latest/bwc-stable.json.minisig");
    expectExit(
      cases,
      "live: a missing .minisig refuses with http-status",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["http-status"] },
    );

    publish(good);
    expectExit(
      cases,
      "live: placeholder trusted keys refuse with placeholder-key before any fetch",
      await runChild("scripts/validate-updater-live.js", liveArgs(rootPlaceholder), { env: baseEnv() }),
      { status: EXIT.REFUSED, reasons: ["placeholder-key"] },
    );

    expectExit(
      cases,
      "live: a non-loopback UPDATER_LIVE_BASE_URL is refused as usage",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT), {
        env: childEnv({ UPDATER_LIVE_BASE_URL: "https://example.invalid/latest" }),
      }),
      { status: EXIT.USAGE },
    );
    expectExit(
      cases,
      "live: a prerelease expected version is refused as usage (stable channel only)",
      await runChild("scripts/validate-updater-live.js", liveArgs(TRUSTED_A_ROOT, BETA_VERSION), { env: baseEnv() }),
      { status: EXIT.USAGE },
    );
    const help = await runChild("scripts/validate-updater-live.js", ["--help"], { env: childEnv() });
    record(cases, "live: --help prints usage and exits 0", help.status === 0 && help.stdout.includes("Usage"), tail(help), help.status);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

// ---------------------------------------------------------------------------------------------
// Driver
// ---------------------------------------------------------------------------------------------

async function main() {
  const started = Date.now();
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-release-assets-selftest-"));
  const cases = [];
  let fatal = null;
  try {
    await runGpgCases(temp, cases);
    await runMirrorCases(temp, cases);
    await runLiveCases(temp, cases);
  } catch (error) {
    fatal = error instanceof Error ? `${error.message}\n${error.stack ?? ""}` : String(error);
    cases.push({ name: "self-test harness", pass: false, detail: fatal });
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }

  const ok = fatal === null && cases.every((entry) => entry.pass);
  const report = {
    schema: 1,
    tool: SCRIPT,
    ok,
    network: "127.0.0.1 only",
    version: VERSION,
    startedUtc: new Date(started).toISOString(),
    durationMs: Date.now() - started,
    passed: cases.filter((entry) => entry.pass).length,
    failed: cases.filter((entry) => !entry.pass).length,
    cases,
    fatal,
  };
  writeJson(RESULT_PATH, report);

  for (const entry of cases) {
    const reasons = entry.matchedReasons?.length ? ` (${entry.matchedReasons.join(", ")})` : "";
    console.log(`${entry.pass ? "PASS" : "FAIL"} ${entry.name}${reasons}`);
    if (!entry.pass) console.log(`  ${entry.detail}`);
  }
  console.log(`release-assets-selftest: ${ok ? "PASS" : "FAIL"} (${report.passed}/${cases.length})`);
  console.log("  result: artifacts/release/selftest/release-assets-result.json");
  return ok ? EXIT.OK : EXIT.REFUSED;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = await main();
}
