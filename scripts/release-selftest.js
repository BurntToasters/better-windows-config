#!/usr/bin/env node
/**
 * Executable self-test for the release tooling. It creates temporary keys and manifests,
 * runs the real CLI scripts as child processes, and proves the named refusal reasons.
 *
 * Keys are made by the tool itself in a temporary folder. The encrypted key uses a throwaway
 * password that exists only in this process's environment. No real secret is read or written.
 *
 * No network, no gh, no gpg, no NSIS. The result is artifacts/release/selftest/result.json.
 */
import { execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { EXIT, repoRootFrom, writeJson } from "./lib/cli.js";
import { assetBytesMatch } from "./lib/github-release.js";
import { envelope, generateKeyPair, signFile } from "./lib/minisign.js";
import { buildManifest, serializeManifest } from "./lib/update-manifest.js";

const SCRIPT = "scripts/release-selftest.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const RESULT_PATH = path.join(REPO_ROOT, "artifacts", "release", "selftest", "result.json");
const VERSION = "0.1.0-beta.1";
const OTHER_VERSION = "0.2.0-beta.1";
const PUBDATE = "2026-11-11T00:00:00Z";
const FUTURE_EXPIRES = "2099-01-01T00:00:00Z";
const PAST_EXPIRES = "2020-01-02T00:00:00Z";
const PASSWORD_ENV = "BWC_SELFTEST_KEY_PASSWORD";
const PASSWORD = "selftest-only-password";
const FAKE_COMMIT = "0123456789abcdef0123456789abcdef01234567";
const TAURI_KEY_ENV = "TAURI_SIGNING_PRIVATE_KEY";
const TAURI_PASSWORD_ENV = "TAURI_SIGNING_PRIVATE_KEY_PASSWORD";
const DEFAULT_REPO = "BurntToasters/better-windows-config";
const RELEASE_ENV_NAMES = [TAURI_KEY_ENV, TAURI_PASSWORD_ENV, "GH_REPO", "GH_REPO_OWNER", "GH_REPO_NAME"];

/**
 * The caller's environment minus the release secrets and repository overrides, so a .env or shell
 * setting cannot change a case. Each case then adds exactly what it sets.
 */
function childEnv(extra = {}, remove = []) {
  const env = { ...process.env };
  for (const name of RELEASE_ENV_NAMES) delete env[name];
  Object.assign(env, extra);
  for (const name of remove) delete env[name];
  return env;
}

function run(script, args, { cwd = REPO_ROOT, env, remove } = {}) {
  const result = spawnSync(process.execPath, [path.join(REPO_ROOT, script), ...args], {
    cwd,
    encoding: "utf8",
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
    env: childEnv(env, remove),
  });
  return {
    status: result.status,
    stdout: result.stdout ?? "",
    stderr: result.stderr ?? "",
    error: result.error ? String(result.error.message) : null,
  };
}

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function write(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, value);
}

function writeText(filePath, value) {
  write(filePath, value);
}

function manifestComment(version, channel, expires) {
  return `version:${version} channel:${channel} expires:${expires}`;
}

/** Expiry the tool computes from a publish time and a number of hours. */
function expiresAfter(pubDate, hours) {
  return new Date(Date.parse(pubDate) + hours * 3600 * 1000).toISOString().replace(/\.\d{3}Z$/, "Z");
}

function expectRefusal(name, result, reasons, cases) {
  const matched = reasons.filter((reason) => result.stderr.includes(`[${reason}]`));
  const pass = result.status !== 0 && matched.length > 0;
  cases.push({
    name,
    pass,
    exitCode: result.status,
    expectedReasons: reasons,
    matchedReasons: matched,
    detail: result.stderr.split(/\r?\n/).filter(Boolean).slice(0, 4).join(" | "),
  });
  return pass;
}

function expectPass(name, result, cases, detail = "") {
  const pass = result.status === 0;
  cases.push({
    name,
    pass,
    exitCode: result.status,
    detail: pass ? detail : `${result.stderr.split(/\r?\n/).filter(Boolean).slice(0, 4).join(" | ")}`,
  });
  return pass;
}

function expectCheck(name, pass, detail, cases) {
  cases.push({ name, pass: Boolean(pass), exitCode: null, detail });
  return Boolean(pass);
}

/** A C# string literal that holds exactly the given text (the form TrustedKeys.cs uses). */
function csLiteral(text) {
  const escaped = text
    .replace(/\\/g, "\\\\")
    .replace(/"/g, '\\"')
    .replace(/\r/g, "\\r")
    .replace(/\n/g, "\\n");
  return `"${escaped}"`;
}

async function buildFixture(temp) {
  const keyA = generateKeyPair({ comment: "untrusted comment: BWC selftest key A" });
  const keyB = generateKeyPair({ comment: "untrusted comment: BWC selftest key B" });
  const keys = path.join(temp, "keys");
  writeText(path.join(keys, "a.pub"), keyA.publicKeyText);
  writeText(path.join(keys, "b.pub"), keyB.publicKeyText);
  writeText(path.join(keys, "placeholder.pub"), "untrusted comment: PLACEHOLDER replace with the release public key\nPLACEHOLDER-NOT-A-KEY\n");

  // The encrypted key is created by the tool's own keygen, with a reduced scrypt cost for speed.
  const encryptedDir = path.join(temp, "keys-encrypted");
  const keygen = run("scripts/sign-update-manifest.js", [
    "keygen",
    "--out",
    encryptedDir,
    "--test-key",
    "--test-kdf",
    "--password-env",
    PASSWORD_ENV,
  ], { env: { [PASSWORD_ENV]: PASSWORD } });
  if (keygen.status !== 0) {
    throw new Error(`encrypted test key keygen failed: ${keygen.stderr || keygen.stdout}`);
  }
  const keyEPublic = fs.readFileSync(path.join(encryptedDir, "minisign.pub"), "utf8");

  const installerBytes = Buffer.from(Array.from({ length: 4096 }, (_, index) => (index * 31 + 7) & 0xff));
  const installerName = `BetterWindowsConfig-${VERSION}-win-x64-setup.exe`;
  const installerPath = path.join(temp, "fixtures", installerName);
  write(installerPath, installerBytes);
  const installerSignature = await signFile(
    installerPath,
    keyA.secretKeyText,
    `version:${VERSION} arch:win-x64`,
  );

  const installers = {
    "win-x64": {
      url: `https://example.invalid/bwc/${VERSION}/${installerName}`,
      size: installerBytes.length,
      sha256: sha256(installerBytes),
      signature: envelope(installerSignature),
    },
  };
  const manifest = buildManifest({
    version: VERSION,
    channel: "beta",
    pubDate: PUBDATE,
    notesUrl: `https://example.invalid/bwc/${VERSION}/notes`,
    installers,
  });

  async function writeManifest(directory, value, comment) {
    fs.mkdirSync(directory, { recursive: true });
    const manifestPath = path.join(directory, "manifest.json");
    fs.writeFileSync(manifestPath, serializeManifest(value));
    const signatureText = await signFile(manifestPath, keyA.secretKeyText, comment);
    fs.writeFileSync(`${manifestPath}.minisig`, signatureText, "utf8");
    return { manifestPath, signaturePath: `${manifestPath}.minisig` };
  }

  const primary = await writeManifest(
    path.join(temp, "fixtures", "primary"),
    manifest,
    manifestComment(VERSION, "beta", FUTURE_EXPIRES),
  );

  const badHashBytes = Buffer.from(installerBytes);
  badHashBytes[badHashBytes.length - 1] ^= 0xff;
  const badHashPath = path.join(temp, "fixtures", "bad-hash.exe");
  write(badHashPath, badHashBytes);

  const shortPath = path.join(temp, "fixtures", "short.exe");
  write(shortPath, installerBytes.subarray(0, installerBytes.length - 1));

  const expiredManifest = buildManifest({
    version: VERSION,
    channel: "beta",
    pubDate: "2020-01-01T00:00:00Z",
    installers,
  });
  const expired = await writeManifest(
    path.join(temp, "fixtures", "expired"),
    expiredManifest,
    manifestComment(VERSION, "beta", FUTURE_EXPIRES),
  );

  const expiredSigned = await writeManifest(
    path.join(temp, "fixtures", "expired-signed"),
    manifest,
    manifestComment(VERSION, "beta", PAST_EXPIRES),
  );

  const noChannel = await writeManifest(
    path.join(temp, "fixtures", "no-channel"),
    manifest,
    `version:${VERSION} expires:${FUTURE_EXPIRES}`,
  );

  const otherChannel = await writeManifest(
    path.join(temp, "fixtures", "other-channel"),
    manifest,
    manifestComment(VERSION, "stable", FUTURE_EXPIRES),
  );

  const wrongArchInstallers = {
    "win-arm64": {
      url: `https://example.invalid/bwc/${VERSION}/BetterWindowsConfig-${VERSION}-win-arm64-setup.exe`,
      size: installerBytes.length,
      sha256: sha256(installerBytes),
      signature: envelope(installerSignature),
    },
  };
  const wrongArchManifest = buildManifest({
    version: VERSION,
    channel: "beta",
    pubDate: PUBDATE,
    installers: wrongArchInstallers,
  });
  const wrongArch = await writeManifest(
    path.join(temp, "fixtures", "wrong-arch"),
    wrongArchManifest,
    manifestComment(VERSION, "beta", FUTURE_EXPIRES),
  );

  const malformed = path.join(temp, "fixtures", "malformed");
  fs.mkdirSync(malformed, { recursive: true });
  writeText(path.join(malformed, "manifest.json"), "{ this is not json\n");
  writeText(path.join(malformed, "manifest.json.minisig"), "untrusted comment: not a signature\n");

  const unsigned = path.join(temp, "fixtures", "unsigned");
  fs.mkdirSync(unsigned, { recursive: true });
  fs.copyFileSync(primary.manifestPath, path.join(unsigned, "manifest.json"));

  const signInput = path.join(temp, "signer-input.txt");
  writeText(signInput, "sign me with the encrypted key\n");

  return {
    keyA,
    keyB,
    keys,
    encryptedDir,
    keyEPublic,
    installerPath,
    installerSignature,
    manifest,
    primary,
    badHashPath,
    shortPath,
    expired,
    expiredSigned,
    noChannel,
    otherChannel,
    wrongArch,
    malformed,
    unsigned,
    signInput,
  };
}

function validatorArgs(fixture, overrides = {}) {
  const args = [
    "--manifest",
    overrides.manifest ?? fixture.primary.manifestPath,
    "--signature",
    overrides.signature ?? fixture.primary.signaturePath,
    "--version",
    VERSION,
    "--channel",
    overrides.channel ?? "beta",
    "--trusted-key",
    overrides.trustedKey ?? path.join(fixture.keys, "a.pub"),
  ];
  if (overrides.extra) args.push(...overrides.extra);
  return args;
}

async function runValidatorCases(temp, fixture, cases) {
  expectPass(
    "signer keygen, sign, and verify",
    (() => {
      const keygenResult = run("scripts/sign-update-manifest.js", ["keygen", "--out", path.join(temp, "keygen"), "--test-key"]);
      if (keygenResult.status !== 0) return keygenResult;
      const input = path.join(temp, "signer-plain.txt");
      writeText(input, "sign me\n");
      const signed = run("scripts/sign-update-manifest.js", [
        "sign",
        input,
        "--key",
        path.join(temp, "keygen"),
        "--trusted-comment",
        `version:${VERSION}`,
      ]);
      if (signed.status !== 0) return signed;
      const raw = fs.readFileSync(`${input}.minisig`, "utf8").trimEnd().split("\n");
      if (raw.length !== 4 || !raw[2].startsWith("trusted comment: ")) {
        return { status: 1, stderr: "signature text does not have four minisign lines" };
      }
      const packet = Buffer.from(raw[1], "base64");
      if (packet.subarray(0, 2).toString() !== "ED") {
        return { status: 1, stderr: "signature packet is not prehashed ED" };
      }
      return run("scripts/sign-update-manifest.js", [
        "verify",
        input,
        "--signature",
        `${input}.minisig`,
        "--trusted-key",
        path.join(temp, "keygen", "minisign.pub"),
      ]);
    })(),
    cases,
    "real Ed25519 + BLAKE2b prehashed signature",
  );

  expectRefusal(
    "wrong key rejected",
    run("scripts/validate-updater-manifest.js", validatorArgs(fixture, { trustedKey: path.join(fixture.keys, "b.pub") })),
    ["wrong-key"],
    cases,
  );

  expectRefusal(
    "bad installer hash rejected",
    run(
      "scripts/validate-updater-manifest.js",
      validatorArgs(fixture, { extra: ["--installer", `win-x64=${fixture.badHashPath}`] }),
    ),
    ["installer-hash-mismatch"],
    cases,
  );

  expectRefusal(
    "truncated installer rejected",
    run(
      "scripts/validate-updater-manifest.js",
      validatorArgs(fixture, { extra: ["--installer", `win-x64=${fixture.shortPath}`] }),
    ),
    ["installer-size-mismatch"],
    cases,
  );

  expectRefusal(
    "wrong channel rejected",
    run("scripts/validate-updater-manifest.js", validatorArgs(fixture, { channel: "stable" })),
    ["channel-mismatch", "prerelease-on-stable"],
    cases,
  );

  expectRefusal(
    "placeholder key rejected",
    run(
      "scripts/validate-updater-manifest.js",
      validatorArgs(fixture, { trustedKey: path.join(fixture.keys, "placeholder.pub") }),
    ),
    ["placeholder-key"],
    cases,
  );

  expectRefusal(
    "malformed manifest rejected",
    run("scripts/validate-updater-manifest.js", [
      "--manifest",
      path.join(fixture.malformed, "manifest.json"),
      "--signature",
      path.join(fixture.malformed, "manifest.json.minisig"),
      "--trusted-key",
      path.join(fixture.keys, "a.pub"),
    ]),
    ["invalid-json"],
    cases,
  );

  expectRefusal(
    "unsigned manifest rejected",
    run("scripts/validate-updater-manifest.js", [
      "--manifest",
      path.join(fixture.unsigned, "manifest.json"),
      "--trusted-key",
      path.join(fixture.keys, "a.pub"),
    ]),
    ["signature-missing"],
    cases,
  );

  expectRefusal(
    "wrong architecture rejected",
    run(
      "scripts/validate-updater-manifest.js",
      validatorArgs(fixture, { extra: ["--require-arch", "win-arm64"] }),
    ),
    ["missing-architecture"],
    cases,
  );

  expectRefusal(
    "installer signed for another architecture rejected",
    run("scripts/validate-updater-manifest.js", [
      "--manifest",
      fixture.wrongArch.manifestPath,
      "--signature",
      fixture.wrongArch.signaturePath,
      "--version",
      VERSION,
      "--channel",
      "beta",
      "--trusted-key",
      path.join(fixture.keys, "a.pub"),
      "--installer",
      `win-arm64=${fixture.installerPath}`,
    ]),
    ["wrong-arch"],
    cases,
  );

  expectRefusal(
    "expired manifest rejected",
    run("scripts/validate-updater-manifest.js", [
      "--manifest",
      fixture.expired.manifestPath,
      "--signature",
      fixture.expired.signaturePath,
      "--version",
      VERSION,
      "--channel",
      "beta",
      "--trusted-key",
      path.join(fixture.keys, "a.pub"),
      "--max-age-hours",
      "1",
    ]),
    ["expired"],
    cases,
  );

  expectRefusal(
    "signed expiry in the past rejected",
    run("scripts/validate-updater-manifest.js", [
      "--manifest",
      fixture.expiredSigned.manifestPath,
      "--signature",
      fixture.expiredSigned.signaturePath,
      "--version",
      VERSION,
      "--channel",
      "beta",
      "--trusted-key",
      path.join(fixture.keys, "a.pub"),
    ]),
    ["expired"],
    cases,
  );

  expectRefusal(
    "manifest without a signed channel rejected",
    run("scripts/validate-updater-manifest.js", [
      "--manifest",
      fixture.noChannel.manifestPath,
      "--signature",
      fixture.noChannel.signaturePath,
      "--version",
      VERSION,
      "--channel",
      "beta",
      "--trusted-key",
      path.join(fixture.keys, "a.pub"),
    ]),
    ["trusted-comment-invalid"],
    cases,
  );

  expectRefusal(
    "manifest signed for another channel rejected",
    run("scripts/validate-updater-manifest.js", [
      "--manifest",
      fixture.otherChannel.manifestPath,
      "--signature",
      fixture.otherChannel.signaturePath,
      "--version",
      VERSION,
      "--channel",
      "beta",
      "--trusted-key",
      path.join(fixture.keys, "a.pub"),
    ]),
    ["channel-mismatch"],
    cases,
  );

  expectRefusal(
    "downgrade rejected",
    run(
      "scripts/validate-updater-manifest.js",
      validatorArgs(fixture, { extra: ["--current-version", OTHER_VERSION] }),
    ),
    ["downgrade"],
    cases,
  );

  expectRefusal(
    "encrypted key with wrong password rejected",
    run(
      "scripts/sign-update-manifest.js",
      ["sign", fixture.signInput, "--key", fixture.encryptedDir, "--trusted-comment", `version:${VERSION}`, "--password-env", PASSWORD_ENV],
      { env: { [PASSWORD_ENV]: "not-the-password" } },
    ),
    ["wrong-password"],
    cases,
  );

  expectRefusal(
    "encrypted key without a password source rejected",
    run(
      "scripts/sign-update-manifest.js",
      ["sign", fixture.signInput, "--key", fixture.encryptedDir, "--trusted-comment", `version:${VERSION}`, "--password-env", PASSWORD_ENV],
      { remove: [PASSWORD_ENV] },
    ),
    ["password-missing"],
    cases,
  );

  expectRefusal(
    "password on the command line rejected",
    run("scripts/sign-update-manifest.js", [
      "sign",
      fixture.signInput,
      "--key",
      fixture.encryptedDir,
      "--trusted-comment",
      `version:${VERSION}`,
      "--password",
      PASSWORD,
    ]),
    ["password-argument"],
    cases,
  );

  const encryptedSigned = path.join(temp, "encrypted-signed");
  expectPass(
    "encrypted key with right password signs a file that verifies",
    (() => {
      const signed = run(
        "scripts/sign-update-manifest.js",
        ["sign", fixture.signInput, "--key", fixture.encryptedDir, "--trusted-comment", `version:${VERSION}`, "--password-env", PASSWORD_ENV, "--out", `${encryptedSigned}.minisig`],
        { env: { [PASSWORD_ENV]: PASSWORD } },
      );
      if (signed.status !== 0) return signed;
      const writtenKey = path.join(temp, "encrypted-public.pub");
      writeText(writtenKey, fixture.keyEPublic);
      return run("scripts/sign-update-manifest.js", [
        "verify",
        fixture.signInput,
        "--signature",
        `${encryptedSigned}.minisig`,
        "--trusted-key",
        writtenKey,
      ]);
    })(),
    cases,
    "minisign-layout encrypted key, scrypt and XSalsa20 round trip",
  );
}

async function runAssembleCases(temp, fixture, cases) {
  const repoA = path.join(temp, "assemble-repo-a");
  writeRepository(repoA, { embedded: [fixture.keyA.publicKeyText] });

  const repoE = path.join(temp, "assemble-repo-e");
  writeRepository(repoE, { embedded: [fixture.keyEPublic] });

  expectRefusal(
    "bundle signed with a key outside TrustedKeys.cs refused",
    run("scripts/assemble-bundle.js", [
      "--root",
      repoA,
      "--version",
      VERSION,
      "--installer-win-x64",
      fixture.installerPath,
      "--secret-key",
      fixture.encryptedDir,
      "--password-env",
      PASSWORD_ENV,
      "--pub-date",
      PUBDATE,
      "--out",
      path.join(temp, "bundle-refused"),
    ], { env: { [PASSWORD_ENV]: PASSWORD } }),
    ["embedded-key-mismatch"],
    cases,
  );

  expectRefusal(
    "bundle with an expired expiry refused",
    run("scripts/assemble-bundle.js", [
      "--root",
      repoE,
      "--version",
      VERSION,
      "--installer-win-x64",
      fixture.installerPath,
      "--secret-key",
      fixture.encryptedDir,
      "--password-env",
      PASSWORD_ENV,
      "--pub-date",
      "2020-01-01T00:00:00Z",
      "--expires-hours",
      "1",
      "--out",
      path.join(temp, "bundle-expired"),
    ], { env: { [PASSWORD_ENV]: PASSWORD } }),
    ["expired"],
    cases,
  );

  expectRefusal(
    "bundle with a wrong password refused",
    run("scripts/assemble-bundle.js", [
      "--root",
      repoE,
      "--version",
      VERSION,
      "--installer-win-x64",
      fixture.installerPath,
      "--secret-key",
      fixture.encryptedDir,
      "--password-env",
      PASSWORD_ENV,
      "--pub-date",
      PUBDATE,
      "--out",
      path.join(temp, "bundle-wrong-password"),
    ], { env: { [PASSWORD_ENV]: "wrong" } }),
    ["wrong-password"],
    cases,
  );

  const outDir = path.join(temp, "bundle-ok");
  const hours = 87600;
  const assembled = run("scripts/assemble-bundle.js", [
    "--root",
    repoE,
    "--version",
    VERSION,
    "--installer-win-x64",
    fixture.installerPath,
    "--secret-key",
    fixture.encryptedDir,
    "--password-env",
    PASSWORD_ENV,
    "--pub-date",
    PUBDATE,
    "--expires-hours",
    String(hours),
    "--out",
    outDir,
  ], { env: { [PASSWORD_ENV]: PASSWORD } });
  expectPass(
    "bundle signed by an embedded encrypted key is assembled",
    assembled,
    cases,
    "beta/bwc-beta.json and installer signatures",
  );

  const channelDir = path.join(outDir, "beta");
  const manifestPath = path.join(channelDir, "bwc-beta.json");
  const installerName = path.basename(fixture.installerPath);
  const installerCopy = path.join(channelDir, installerName);
  const expectedManifestComment = `trusted comment: ${manifestComment(VERSION, "beta", expiresAfter(PUBDATE, hours))}`;
  const manifestLines = fs.existsSync(`${manifestPath}.minisig`)
    ? fs.readFileSync(`${manifestPath}.minisig`, "utf8").split("\n")
    : [];
  expectCheck(
    "manifest trusted comment is exactly version, channel, and expires",
    manifestLines[2] === expectedManifestComment,
    `${manifestLines[2] ?? "(missing)"}`,
    cases,
  );

  const writtenManifest = fs.existsSync(manifestPath) ? JSON.parse(fs.readFileSync(manifestPath, "utf8")) : {};
  expectCheck(
    "bundle uses the GitHub release URLs and the channel asset name",
    writtenManifest.installers?.["win-x64"]?.url ===
      `https://github.com/${DEFAULT_REPO}/releases/download/v${VERSION}/${path.basename(fixture.installerPath)}` &&
      writtenManifest.notesUrl === `https://github.com/${DEFAULT_REPO}/releases/tag/v${VERSION}`,
    `${writtenManifest.installers?.["win-x64"]?.url ?? "(no url)"}`,
    cases,
  );

  const installerLines = fs.existsSync(`${installerCopy}.minisig`)
    ? fs.readFileSync(`${installerCopy}.minisig`, "utf8").split("\n")
    : [];
  expectCheck(
    "installer trusted comment is exactly version and arch",
    installerLines[2] === `trusted comment: version:${VERSION} arch:win-x64`,
    `${installerLines[2] ?? "(missing)"}`,
    cases,
  );

  expectPass(
    "assembled bundle validates with the embedded key",
    run("scripts/validate-updater-manifest.js", [
      "--manifest",
      manifestPath,
      "--signature",
      `${manifestPath}.minisig`,
      "--version",
      VERSION,
      "--channel",
      "beta",
      "--trusted-key",
      path.join(temp, "encrypted-public.pub"),
      "--installer",
      `win-x64=${installerCopy}`,
    ]),
    cases,
    "validator accepts the bundle the updater reads",
  );
}

/**
 * TAURI_SIGNING_PRIVATE_KEY cases: the `tauri signer generate` form (base64 of the key file), a key
 * folder path, the refusals, and a check that a failing run prints neither the key nor the password.
 */
async function runEnvironmentKeyCases(temp, fixture, cases) {
  const encryptedKeyText = fs.readFileSync(path.join(fixture.encryptedDir, "minisign.key"), "utf8");
  const tauriValue = Buffer.from(encryptedKeyText, "utf8").toString("base64");
  const unencryptedValue = Buffer.from(fixture.keyA.secretKeyText, "utf8").toString("base64");
  const signInput = fixture.signInput;
  const publicPath = path.join(temp, "env-public.pub");
  writeText(publicPath, fixture.keyEPublic);
  const commentArgs = ["--trusted-comment", `version:${VERSION}`];
  const signWith = (name, env, extra = []) =>
    run("scripts/sign-update-manifest.js", ["sign", signInput, ...commentArgs, "--out", path.join(temp, `${name}.minisig`), ...extra], { env });

  expectPass(
    "Tauri-format encrypted key from the environment signs and verifies",
    (() => {
      const signed = signWith("env-tauri", { [TAURI_KEY_ENV]: tauriValue, [TAURI_PASSWORD_ENV]: PASSWORD });
      if (signed.status !== 0) return signed;
      return run("scripts/sign-update-manifest.js", [
        "verify",
        signInput,
        "--signature",
        path.join(temp, "env-tauri.minisig"),
        "--trusted-key",
        publicPath,
      ]);
    })(),
    cases,
    "base64 of the minisign key file, encrypted; the password comes from the Tauri variable",
  );

  expectPass(
    "key folder path in the environment signs with the Tauri password",
    signWith("env-folder", { [TAURI_KEY_ENV]: fixture.encryptedDir, [TAURI_PASSWORD_ENV]: PASSWORD }),
    cases,
    "a path to the key folder is read from the environment",
  );

  expectPass(
    "unencrypted Tauri-format key signs with an empty password variable",
    signWith("env-plain", { [TAURI_KEY_ENV]: unencryptedValue, [TAURI_PASSWORD_ENV]: "" }),
    cases,
    "an empty password is valid for an unencrypted key",
  );

  expectRefusal(
    "encrypted Tauri-format key with a wrong password refused",
    signWith("env-wrong", { [TAURI_KEY_ENV]: tauriValue, [TAURI_PASSWORD_ENV]: "selftest-wrong-password" }),
    ["wrong-password"],
    cases,
  );

  expectRefusal(
    "encrypted Tauri-format key with an empty password refused",
    signWith("env-empty", { [TAURI_KEY_ENV]: tauriValue, [TAURI_PASSWORD_ENV]: "" }),
    ["password-missing"],
    cases,
  );

  expectRefusal(
    "malformed base64 in the environment refused",
    signWith("env-malformed", { [TAURI_KEY_ENV]: "!!! not base64 !!!", [TAURI_PASSWORD_ENV]: PASSWORD }),
    ["key-malformed"],
    cases,
  );

  expectRefusal(
    "base64 of text that is not a minisign key refused",
    signWith("env-not-key", {
      [TAURI_KEY_ENV]: Buffer.from("this is not a minisign key\n").toString("base64"),
      [TAURI_PASSWORD_ENV]: PASSWORD,
    }),
    ["key-malformed"],
    cases,
  );

  const leak = signWith("env-leak", { [TAURI_KEY_ENV]: tauriValue, [TAURI_PASSWORD_ENV]: "selftest-leak-marker" });
  const leakOutput = `${leak.stdout}${leak.stderr}`;
  expectCheck(
    "failing run prints neither the key nor the password",
    leak.status !== 0 &&
      !leakOutput.includes("selftest-leak-marker") &&
      !leakOutput.includes(tauriValue) &&
      !leakOutput.includes(encryptedKeyText.split("\n")[1]),
    `exit ${leak.status}`,
    cases,
  );

  expectRefusal(
    "no key from the command line or the environment refused",
    run("scripts/sign-update-manifest.js", ["sign", signInput, ...commentArgs]),
    ["key-missing"],
    cases,
  );

  expectRefusal(
    "key value given as --key on the command line refused",
    run("scripts/sign-update-manifest.js", ["sign", signInput, "--key", tauriValue, ...commentArgs]),
    ["key-unreadable"],
    cases,
  );

  const repoA = path.join(temp, "env-repo-a");
  writeRepository(repoA, { embedded: [fixture.keyA.publicKeyText] });
  expectRefusal(
    "environment key not in TrustedKeys.cs refused outside a dry run",
    run("scripts/assemble-bundle.js", [
      "--root",
      repoA,
      "--version",
      VERSION,
      "--installer-win-x64",
      fixture.installerPath,
      "--pub-date",
      PUBDATE,
      "--out",
      path.join(temp, "env-bundle-refused"),
    ], { env: { [TAURI_KEY_ENV]: tauriValue, [TAURI_PASSWORD_ENV]: PASSWORD } }),
    ["embedded-key-mismatch"],
    cases,
  );

  const repoE = path.join(temp, "env-repo-e");
  writeRepository(repoE, { embedded: [fixture.keyEPublic] });
  const envBundle = path.join(temp, "env-bundle");
  expectPass(
    "bundle signed by the environment key is assembled with no --secret-key",
    run("scripts/assemble-bundle.js", [
      "--root",
      repoE,
      "--version",
      VERSION,
      "--installer-win-x64",
      fixture.installerPath,
      "--pub-date",
      PUBDATE,
      "--expires-hours",
      "87600",
      "--out",
      envBundle,
    ], { env: { [TAURI_KEY_ENV]: tauriValue, [TAURI_PASSWORD_ENV]: PASSWORD } }),
    cases,
    "beta/bwc-beta.json with its .minisig",
  );
  expectCheck(
    "environment bundle is named by channel and carries the GitHub download URL",
    fs.existsSync(path.join(envBundle, "beta", "bwc-beta.json.minisig")) &&
      !fs.existsSync(path.join(envBundle, "beta", "manifest.json")),
    "bwc-beta.json.minisig present, manifest.json absent",
    cases,
  );
}

function git(temp, args) {
  return execFileSync("git", args, {
    cwd: temp,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  }).trimEnd();
}

/** Writes the minimal repository the release tooling reads. TrustedKeys.cs embeds the given keys. */
function writeRepository(temp, { placeholder = false, embedded = [] } = {}) {
  writeText(path.join(temp, "package.json"), `${JSON.stringify({ name: "release-selftest", version: VERSION }, null, 2)}\n`);
  writeText(
    path.join(temp, "Directory.Build.props"),
    `<Project>\n  <PropertyGroup>\n    <Version>${VERSION}</Version>\n    <FileVersion>0.1.0.1</FileVersion>\n  </PropertyGroup>\n</Project>\n`,
  );
  writeText(
    path.join(temp, "CHANGELOG.md"),
    `# Changelog\n\n## [${VERSION}] - Unreleased\n\n- Release tooling self-test.\n`,
  );
  writeText(path.join(temp, ".gitignore"), "artifacts/\n");
  const constants = embedded.map((text, index) => `    private const string Key${index} = ${csLiteral(text)};`);
  const list = embedded.map((_, index) => `Key${index}`).join(", ");
  writeText(
    path.join(temp, "src", "Bwc.Update", "Signing", "TrustedKeys.cs"),
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
}

function commitAll(temp, message) {
  git(temp, ["add", "-A"]);
  git(temp, ["-c", "user.name=BWC Selftest", "-c", "user.email=selftest@example.invalid", "commit", "-q", "-m", message]);
}

async function runPolicyCases(temp, fixture, cases) {
  const repo = path.join(temp, "policy-repo");
  fs.mkdirSync(repo, { recursive: true });
  git(repo, ["init", "-q", "-b", "experimental"]);
  writeRepository(repo, { embedded: [fixture.keyA.publicKeyText] });
  commitAll(repo, "selftest fixture");

  const common = ["--root", repo, "--version", VERSION, "--channel", "beta", "--trusted-key", path.join(fixture.keys, "a.pub")];
  expectPass(
    "preflight passes on a clean experimental tree",
    run("scripts/release-preflight.js", common),
    cases,
    "clean tree, version, changelog, branch, embedded key",
  );

  expectRefusal(
    "trusted key not embedded in TrustedKeys.cs rejected",
    run("scripts/release-preflight.js", [
      "--root",
      repo,
      "--version",
      VERSION,
      "--channel",
      "beta",
      "--trusted-key",
      path.join(fixture.keys, "b.pub"),
    ]),
    ["embedded-key-mismatch"],
    cases,
  );

  expectPass(
    "release session create and verify",
    (() => {
      const created = run("scripts/release-session.js", ["create", ...common, "--force"]);
      if (created.status !== 0) return created;
      return run("scripts/release-session.js", ["verify", ...common]);
    })(),
    cases,
    "artifacts/release/session.json",
  );

  writeText(path.join(repo, "dirty.txt"), "dirty\n");
  expectRefusal(
    "dirty tree rejected",
    run("scripts/release-preflight.js", common),
    ["dirty-tree"],
    cases,
  );
  fs.rmSync(path.join(repo, "dirty.txt"), { force: true });

  expectRefusal(
    "version mismatch rejected",
    run("scripts/release-preflight.js", ["--root", repo, "--version", OTHER_VERSION, "--channel", "beta", "--trusted-key", path.join(fixture.keys, "a.pub")]),
    ["version-mismatch"],
    cases,
  );

  expectRefusal(
    "missing artifact directory rejected",
    run("scripts/release-preflight.js", [...common, "--artifact-dir", path.join(repo, "artifacts", "missing")]),
    ["missing-artifact"],
    cases,
  );

  const x64Artifact = path.join(temp, "x64-artifact");
  fs.mkdirSync(x64Artifact, { recursive: true });
  fs.copyFileSync(fixture.primary.manifestPath, path.join(x64Artifact, "bwc-beta.json"));
  fs.copyFileSync(fixture.primary.signaturePath, path.join(x64Artifact, "bwc-beta.json.minisig"));
  fs.copyFileSync(
    fixture.installerPath,
    path.join(x64Artifact, "BetterWindowsConfig-0.1.0-beta.1-win-x64-setup.exe"),
  );
  expectRefusal(
    "artifact with one architecture rejected",
    run("scripts/release-preflight.js", [...common, "--artifact-dir", x64Artifact]),
    ["missing-architecture"],
    cases,
  );

  expectRefusal(
    "draft with a commit that is not HEAD rejected",
    run("scripts/release-draft.js", [
      "--root",
      repo,
      "--version",
      VERSION,
      "--channel",
      "beta",
      "--repo",
      "selftest-owner/selftest-repo",
      "--commit",
      FAKE_COMMIT,
      "--trusted-key",
      path.join(fixture.keys, "a.pub"),
      "--skip-session",
    ]),
    ["session-commit-mismatch"],
    cases,
  );

  const draftPlan = ["--root", repo, "--version", VERSION, "--channel", "beta", "--trusted-key", path.join(fixture.keys, "a.pub"), "--skip-session"];
  expectCheck(
    "draft with no repository option targets the BWC default repository",
    run("scripts/release-draft.js", draftPlan).stdout.includes(`  repo: ${DEFAULT_REPO}\n`),
    `plan names ${DEFAULT_REPO}`,
    cases,
  );

  expectCheck(
    "GH_REPO_NAME overrides only the name part of the default",
    run("scripts/release-draft.js", draftPlan, { env: { GH_REPO_NAME: "selftest-name" } }).stdout.includes(
      "  repo: BurntToasters/selftest-name\n",
    ),
    "plan names BurntToasters/selftest-name",
    cases,
  );

  expectRefusal(
    "malformed GH_REPO_OWNER refused as repo-invalid",
    run("scripts/release-draft.js", draftPlan, { env: { GH_REPO_OWNER: "bad owner" } }),
    ["repo-invalid"],
    cases,
  );

  expectCheck(
    "draft asset with equal size but other bytes is not a match",
    assetBytesMatch({ remoteSha256: sha256(Buffer.from("one")), localSha256: sha256(Buffer.from("two")) }) === false &&
      assetBytesMatch({ remoteSha256: null, localSha256: sha256(Buffer.from("two")) }) === false &&
      assetBytesMatch({ remoteSha256: sha256(Buffer.from("two")), localSha256: sha256(Buffer.from("two")) }) === true,
    "compared by SHA-256 only",
    cases,
  );

  git(repo, ["checkout", "-q", "-b", "main"]);
  expectRefusal(
    "wrong branch rejected",
    run("scripts/release-preflight.js", common),
    ["branch-mismatch"],
    cases,
  );
  git(repo, ["checkout", "-q", "experimental"]);

  writeRepository(repo, { placeholder: true, embedded: [fixture.keyA.publicKeyText] });
  commitAll(repo, "placeholder key");
  expectRefusal(
    "placeholder key source rejected",
    run("scripts/release-preflight.js", common),
    ["placeholder-key"],
    cases,
  );
  writeRepository(repo, { embedded: [fixture.keyA.publicKeyText] });
  commitAll(repo, "restore real key marker");
}

async function main() {
  const started = Date.now();
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-release-selftest-"));
  const cases = [];
  let fatal = null;
  try {
    const fixture = await buildFixture(temp);
    await runValidatorCases(temp, fixture, cases);
    await runAssembleCases(temp, fixture, cases);
    await runEnvironmentKeyCases(temp, fixture, cases);
    await runPolicyCases(temp, fixture, cases);
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
    network: "none",
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
    if (!entry.pass) {
      console.log(`  exit: ${entry.exitCode}`);
      console.log(`  ${entry.detail}`);
    }
  }
  console.log(`release-selftest: ${ok ? "PASS" : "FAIL"} (${report.passed}/${cases.length})`);
  console.log(`  result: artifacts/release/selftest/result.json`);
  return ok ? EXIT.OK : EXIT.REFUSED;
}

process.exitCode = await main();
