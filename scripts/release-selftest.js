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
import {
  assetFilesFromManifest,
  checkBranch,
  checkReleaseReadiness,
  malformedInstallerUrls,
  qualityGateProof,
  satisfiesEngineRange,
} from "./lib/release-policy.js";
import { publishIsGood } from "./build-installer.js";
import { envelope, generateKeyPair, signFile } from "./lib/minisign.js";
import { buildEnvironment } from "./lib/build-environment.js";
import {
  containsSeamName,
  missingRuntimeFiles,
  nativeAotProblems,
  NATIVE_AOT_EXPORT,
  readExportNames,
  readUpdateFlavorMarkers,
  RUNTIME_FILES,
  updateFlavorProblem,
  UPDATE_FLAVOR_MARKER,
  UPDATE_STALE_FILES,
} from "./flavor-metadata.js";
import { installUpdateOutputs, removeStaleFileLists, updateStagingDir } from "./lib/update-publish.js";
import { buildManifest, installerTrustedComment, manifestFileName, serializeManifest } from "./lib/update-manifest.js";
import { expectedAssetNames, loadReleaseBundle, unexpectedAssets } from "./lib/release-bundle.js";
import { SIGNING_REQUIRED_VARIABLES } from "./release-policy.js";
import { publishedReleaseMismatch } from "./release-publish.js";
import { runClient, verdictOf } from "./updater-client-live.js";

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
    `version:${VERSION} arch:win-x64 product:bwc channel:beta`,
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
    "beta/bwc-beta-v2.json and installer signatures",
  );

  const channelDir = path.join(outDir, "beta");
  const manifestPath = path.join(channelDir, "bwc-beta-v2.json");
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
    "installer trusted comment is exactly version, arch, product, and channel",
    installerLines[2] === `trusted comment: version:${VERSION} arch:win-x64 product:bwc channel:beta`,
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
/**
 * I2b hardening cases (update-elevation.md HI, HB, HE): the signed installer comment must carry product:bwc and the
 * manifest channel, the MSBuild environment drops DefineConstants, a ship binary may not hold the E2E seam names, and
 * BwcUpdate.exe must be proven NativeAOT with the folder's flavor marker (HN13, HN14).
 */
async function runHardeningCases(temp, fixture, cases) {
  expectCheck(
    "installer comment is version, arch, product, channel",
    installerTrustedComment({ version: VERSION, architecture: "win-x64", channel: "beta" }) ===
      `version:${VERSION} arch:win-x64 product:bwc channel:beta`,
    installerTrustedComment({ version: VERSION, architecture: "win-x64", channel: "beta" }),
    cases,
  );
  expectCheck(
    "installer comment refuses a channel that is not stable or beta",
    (() => {
      try {
        installerTrustedComment({ version: VERSION, architecture: "win-x64", channel: "nightly" });
        return false;
      } catch {
        return true;
      }
    })(),
    "a nightly comment was produced",
    cases,
  );

  const variants = [
    ["installer without product rejected", `version:${VERSION} arch:win-x64 channel:beta`, "installer-product-mismatch"],
    ["installer for another product rejected", `version:${VERSION} arch:win-x64 product:other channel:beta`, "installer-product-mismatch"],
    ["installer without channel rejected", `version:${VERSION} arch:win-x64 product:bwc`, "trusted-comment-invalid"],
    ["installer for another channel rejected", `version:${VERSION} arch:win-x64 product:bwc channel:stable`, "installer-channel-mismatch"],
  ];
  for (const [name, comment, reason] of variants) {
    const dir = path.join(temp, "fixtures", "hardening", reason + "-" + variants.findIndex((v) => v[0] === name));
    fs.mkdirSync(dir, { recursive: true });
    const installerFile = path.join(dir, "installer.exe");
    fs.copyFileSync(fixture.installerPath, installerFile);
    const signature = envelope(await signFile(installerFile, fixture.keyA.secretKeyText, comment));
    const entry = { ...fixture.manifest.installers["win-x64"], signature };
    const manifest = buildManifest({
      version: VERSION,
      channel: "beta",
      pubDate: PUBDATE,
      notesUrl: fixture.manifest.notesUrl,
      installers: { "win-x64": entry },
    });
    const manifestPath = path.join(dir, "manifest.json");
    fs.writeFileSync(manifestPath, serializeManifest(manifest));
    const manifestSig = await signFile(manifestPath, fixture.keyA.secretKeyText, manifestComment(VERSION, "beta", FUTURE_EXPIRES));
    fs.writeFileSync(`${manifestPath}.minisig`, manifestSig, "utf8");
    expectRefusal(
      name,
      run("scripts/validate-updater-manifest.js", [
        ...validatorArgs(fixture, { manifest: manifestPath, signature: `${manifestPath}.minisig` }),
        "--installer",
        `win-x64=${installerFile}`,
      ]),
      [reason],
      cases,
    );
  }

  const scrubbed = buildEnvironment({ DefineConstants: "BWC_E2E", defineconstants: "X", DEFINECONSTANTS: "Y", Path: "p", KEEP: "1" });
  expectCheck(
    "msbuild_environment_scrubbed",
    Object.keys(scrubbed).sort().join(",") === "KEEP,Path",
    Object.keys(scrubbed).join(","),
    cases,
  );

  const vswhereDir = "C:\\PF86\\Microsoft Visual Studio\\Installer";
  const withVswhere = buildEnvironment({ Path: "C:\\x", "ProgramFiles(x86)": "C:\\PF86" }, () => true);
  const alreadyThere = buildEnvironment({ Path: `C:\\x;${vswhereDir}\\`, "ProgramFiles(x86)": "C:\\PF86" }, () => true);
  const noVswhere = buildEnvironment({ Path: "C:\\x", "ProgramFiles(x86)": "C:\\PF86" }, () => false);
  expectCheck(
    "vswhere_on_path",
    withVswhere.Path === `C:\\x;${vswhereDir}` &&
      alreadyThere.Path === `C:\\x;${vswhereDir}\\` &&
      noVswhere.Path === "C:\\x" &&
      Object.keys(withVswhere).sort().join(",") === "Path,ProgramFiles(x86)",
    "the Visual Studio installer folder is added to this build's PATH only when vswhere.exe is there and PATH lacks it",
    cases,
  );
  const seam = Buffer.concat([Buffer.from("xx"), Buffer.from("BWC_E2E_UPDATE_ROOT", "utf16le"), Buffer.from("yy")]);
  const seamUtf8 = Buffer.from("zz BWC_E2E_UPDATE_ROOT zz", "utf8");
  expectCheck("ship_e2e_strings", containsSeamName(seam) && containsSeamName(seamUtf8) && !containsSeamName(Buffer.from("BWC_E2E_THEME")), "seam names found as UTF-16 and UTF-8, nothing else", cases);

  // A minimal PE32+ image with one section that holds an export table naming the given symbols, then the extra bytes.
  const makePe = (exports, extra = Buffer.alloc(0)) => {
    const names = Buffer.concat(exports.map((name) => Buffer.from(`${name}\0`, "latin1")));
    const table = Buffer.alloc(40 + exports.length * 8);
    const sectionRva = 0x1000;
    const sectionRaw = 0x200;
    const namesRva = sectionRva + table.length;
    const pointersRva = sectionRva + 40;
    table.writeUInt32LE(exports.length, 24); // number of names
    table.writeUInt32LE(pointersRva, 32); // address of names
    let at = 0;
    exports.forEach((name, index) => {
      table.writeUInt32LE(namesRva + at, 40 + index * 4);
      at += Buffer.byteLength(name) + 1;
    });
    const section = Buffer.concat([table, names, extra]);
    const image = Buffer.alloc(sectionRaw + section.length);
    image.writeUInt16LE(0x5a4d, 0);
    image.writeUInt32LE(0x80, 0x3c);
    image.writeUInt32LE(0x00004550, 0x80);
    image.writeUInt16LE(1, 0x80 + 6); // sections
    image.writeUInt16LE(240, 0x80 + 20); // optional header size
    image.writeUInt16LE(0x20b, 0x80 + 24); // PE32+
    if (exports.length > 0) {
      image.writeUInt32LE(sectionRva, 0x80 + 24 + 112); // export directory rva
      image.writeUInt32LE(table.length, 0x80 + 24 + 116);
    }
    const sectionAt = 0x80 + 24 + 240;
    image.write(".rdata", sectionAt, "latin1");
    image.writeUInt32LE(section.length, sectionAt + 8);
    image.writeUInt32LE(sectionRva, sectionAt + 12);
    image.writeUInt32LE(section.length, sectionAt + 16);
    image.writeUInt32LE(sectionRaw, sectionAt + 20);
    section.copy(image, sectionRaw);
    return image;
  };
  const marker = (value) => Buffer.from(`unexpected error in BwcUpdate (${UPDATE_FLAVOR_MARKER}${value})`, "utf16le");
  const aotImage = makePe(["other", NATIVE_AOT_EXPORT], marker("ship"));
  const apphost = makePe([], Buffer.from("hostfxr.dll", "utf16le"));
  expectCheck(
    "native_aot_probe",
    readExportNames(aotImage).join(",") === `other,${NATIVE_AOT_EXPORT}` &&
      readExportNames(apphost).length === 0 &&
      readExportNames(Buffer.from("not a pe file")).length === 0 &&
      nativeAotProblems(aotImage, ["BwcUpdate.exe", "BwcRegedit.dll", "BwcUpdate.pdb"]).length === 0 &&
      nativeAotProblems(apphost, ["BwcUpdate.exe"]).length === 2 &&
      nativeAotProblems(aotImage, ["BwcUpdate.exe", "BwcUpdate.dll"]).length === 1 &&
      nativeAotProblems(aotImage, ["BwcUpdate.exe", "bwcupdate.runtimeconfig.json"]).length === 1 &&
      nativeAotProblems(aotImage, ["BwcUpdate.exe", "BwcUpdate.deps.json"]).length === 1 &&
      nativeAotProblems(makePe([NATIVE_AOT_EXPORT], Buffer.from("coreclr", "utf8")), []).length === 1 &&
      nativeAotProblems(makePe([NATIVE_AOT_EXPORT], Buffer.from("CORECLR_ENABLE_PROFILING", "utf16le")), []).length === 1 &&
      nativeAotProblems(makePe([NATIVE_AOT_EXPORT], Buffer.from("DOTNET_STARTUP_HOOKS")), []).length === 1,
    "an image that exports the NativeAOT symbol and has no host or loader strings passes; an apphost, a managed file beside the exe, or a loader string fails",
    cases,
  );

  const markerUtf8 = Buffer.from(`xx ${UPDATE_FLAVOR_MARKER}E2E yy`, "utf8");
  expectCheck(
    "native_flavor_marker",
    readUpdateFlavorMarkers(marker("ship")).join(",") === "ship" &&
      readUpdateFlavorMarkers(markerUtf8).join(",") === "E2E" &&
      readUpdateFlavorMarkers(Buffer.concat([marker("ship"), markerUtf8])).join(",") === "E2E,ship" &&
      readUpdateFlavorMarkers(Buffer.from("nothing here")).length === 0 &&
      updateFlavorProblem(marker("ship"), "ship").problem === null &&
      updateFlavorProblem(marker("E2E"), "ship").problem !== null &&
      updateFlavorProblem(Buffer.concat([marker("ship"), markerUtf8]), "ship").problem !== null &&
      updateFlavorProblem(Buffer.from("nothing here"), "E2E").problem !== null,
    "exactly the folder's marker passes; a missing, other, or mixed marker fails (UTF-16LE and UTF-8)",
    cases,
  );

  // HN26: a folder without the runtime of the self-contained apps is refused.
  const everyRuntimeFile = [...RUNTIME_FILES, "BetterWindowsConfig.exe", "Bwc.UI.dll"];
  expectCheck(
    "runtime_files_guard",
    RUNTIME_FILES.join(",") ===
      "hostfxr.dll,hostpolicy.dll,coreclr.dll,System.Private.CoreLib.dll,BetterWindowsConfig.runtimeconfig.json" &&
      missingRuntimeFiles(everyRuntimeFile).length === 0 &&
      missingRuntimeFiles(everyRuntimeFile.map((name) => name.toUpperCase())).length === 0 &&
      RUNTIME_FILES.every(
        (gone) => missingRuntimeFiles(everyRuntimeFile.filter((name) => name !== gone)).join(",") === gone,
      ) &&
      missingRuntimeFiles([]).length === RUNTIME_FILES.length,
    "each of the five runtime files is required, by name and without regard to case, and a missing one is named",
    cases,
  );

  // HN17 and HN25: the staged helper adds only its exe; only the three stale managed names are removed.
  const publishTemp = path.join(temp, "update-publish");
  const staging = path.join(publishTemp, "staging");
  const shared = path.join(publishTemp, "shared");
  fs.mkdirSync(staging, { recursive: true });
  fs.mkdirSync(path.join(shared, "sub"), { recursive: true });
  const putFile = (folder, name, text) => fs.writeFileSync(path.join(folder, name), text);
  const readFile = (folder, name) => (fs.existsSync(path.join(folder, name)) ? fs.readFileSync(path.join(folder, name), "utf8") : null);
  putFile(staging, "BwcUpdate.exe", "aot");
  putFile(staging, "BwcUpdate.pdb", "symbols");
  putFile(staging, "Bwc.Core.pdb", "core-symbols");
  putFile(staging, "Bwc.Core.dll", "must-not-copy");
  for (const name of RUNTIME_FILES) {
    putFile(shared, name, `runtime ${name}`);
  }
  putFile(shared, "BetterWindowsConfig.exe", "hub");
  putFile(shared, "Bwc.Core.dll", "hub-core");
  putFile(shared, "sub\\keep.txt", "keep");
  putFile(shared, "BwcUpdate.exe", "old-coreclr-apphost");
  for (const name of UPDATE_STALE_FILES) {
    putFile(shared, name, "stale");
  }
  const installed = installUpdateOutputs(staging, shared);
  const before = RUNTIME_FILES.every((name) => readFile(shared, name) === `runtime ${name}`);
  expectCheck(
    "update_publish_install",
    installed.copied.join(",") === "BwcUpdate.exe,BwcUpdate.pdb" &&
      installed.removed.join(",") === UPDATE_STALE_FILES.join(",") &&
      readFile(shared, "BwcUpdate.exe") === "aot" &&
      readFile(shared, "BwcUpdate.pdb") === "symbols" &&
      UPDATE_STALE_FILES.every((name) => readFile(shared, name) === null) &&
      before &&
      readFile(shared, "BetterWindowsConfig.exe") === "hub" &&
      readFile(shared, "Bwc.Core.dll") === "hub-core" &&
      readFile(shared, "Bwc.Core.pdb") === null &&
      readFile(shared, "sub\\keep.txt") === "keep" &&
      fs.readdirSync(shared).length === RUNTIME_FILES.length + 5 &&
      updateStagingDir("C:\\r\\publish\\win-x64-e2e") === "C:\\r\\publish\\win-x64-e2e-bwcupdate-aot",
    "only BwcUpdate.exe (and its symbols) is copied in, exactly the three stale managed names are removed, nothing else changes",
    cases,
  );
  fs.rmSync(path.join(staging, "BwcUpdate.exe"));
  let missingExeError = null;
  try {
    installUpdateOutputs(staging, shared);
  } catch (error) {
    missingExeError = error;
  }
  expectCheck(
    "update_publish_missing_exe",
    missingExeError !== null && readFile(shared, "BwcUpdate.exe") === "aot" && readFile(shared, "hostfxr.dll") === "runtime hostfxr.dll",
    "a staging folder without BwcUpdate.exe is an error and changes nothing in the shared folder",
    cases,
  );

  // HN25: the previous publish's file list is what let the NativeAOT publish delete the runtime files.
  const projectDir = path.join(publishTemp, "project");
  const listName = "Bwc.Update.csproj.FileListAbsolute.txt";
  const listFolders = [
    path.join(projectDir, "obj", "x64", "Release", "net10.0-windows", "win-x64"),
    path.join(projectDir, "obj", "Release", "net10.0-windows", "win-x64"),
    path.join(projectDir, "obj", "x64", "Release", "net10.0-windows", "win-arm64"),
    path.join(projectDir, "obj", "ARM64", "Release", "net10.0-windows", "win-arm64"),
  ];
  for (const folder of listFolders) {
    fs.mkdirSync(folder, { recursive: true });
    fs.writeFileSync(path.join(folder, listName), "C:\\publish\\hostfxr.dll\n");
    fs.writeFileSync(path.join(folder, "Other.csproj.FileListAbsolute.txt"), "x");
    fs.writeFileSync(path.join(folder, "BwcUpdate.dll"), "x");
  }
  const removedLists = removeStaleFileLists({ projectDir, projectFile: "Bwc.Update.csproj", platform: "x64", rid: "win-x64" });
  const exists = (folder, name) => fs.existsSync(path.join(folder, name));
  expectCheck(
    "update_publish_stale_list",
    removedLists.length === 2 &&
      !exists(listFolders[0], listName) &&
      !exists(listFolders[1], listName) &&
      exists(listFolders[2], listName) &&
      exists(listFolders[3], listName) &&
      listFolders.every((folder) => exists(folder, "Other.csproj.FileListAbsolute.txt") && exists(folder, "BwcUpdate.dll")) &&
      removeStaleFileLists({ projectDir: path.join(publishTemp, "none"), projectFile: "Bwc.Update.csproj", platform: "x64", rid: "win-x64" })
        .length === 0,
    "only this project's file list for this platform and rid is removed, from the project's own obj folder",
    cases,
  );
}
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
    "beta/bwc-beta-v2.json with its .minisig",
  );
  expectCheck(
    "environment bundle is named by channel and carries the GitHub download URL",
    fs.existsSync(path.join(envBundle, "beta", "bwc-beta-v2.json.minisig")) &&
      !fs.existsSync(path.join(envBundle, "beta", "manifest.json")),
    "bwc-beta-v2.json.minisig present, manifest.json absent",
    cases,
  );
}

/**
 * No legacy feed cases (release.md, section LF). 0.1.x carries no migration code: a release writes bwc-stable-v2.json
 * only, takes no legacy option, and a bwc-stable.json asset on a reused draft is outside the expected set. The stable
 * bundles here are signed with the encrypted test key that the repo embeds.
 */
async function runNoLegacyFeedCases(temp, fixture, cases) {
  const VERSION_UNDER_TEST = "0.1.2";
  const HOURS = 87600;
  const STRAY_NAME = "bwc-stable.json";
  const repo = path.join(temp, "nolegacy-repo");
  writeRepository(repo, { embedded: [fixture.keyEPublic] });
  const publicPath = path.join(temp, "nolegacy-key.pub");
  writeText(publicPath, fixture.keyEPublic);

  const installerPath = path.join(temp, "nolegacy-fixtures", `BetterWindowsConfig-${VERSION_UNDER_TEST}-win-x64-setup.exe`);
  write(installerPath, Buffer.from(Array.from({ length: 2048 }, (_, index) => (index * 17 + 3) & 0xff)));

  expectCheck(
    "feed names carry the v2 suffix",
    manifestFileName("stable") === "bwc-stable-v2.json" && manifestFileName("beta") === "bwc-beta-v2.json",
    `${manifestFileName("stable")}, ${manifestFileName("beta")}`,
    cases,
  );

  const assemble = (name, extra, { version = VERSION_UNDER_TEST } = {}) =>
    run(
      "scripts/assemble-bundle.js",
      [
        "--root", repo,
        "--version", version,
        "--installer-win-x64", installerPath,
        "--secret-key", fixture.encryptedDir,
        "--password-env", PASSWORD_ENV,
        "--pub-date", PUBDATE,
        "--expires-hours", String(HOURS),
        "--out", path.join(temp, name),
        ...extra,
      ],
      { env: { [PASSWORD_ENV]: PASSWORD } },
    );

  // LF1: a stable assembly with no legacy option succeeds and writes the v2 files only.
  expectPass("LF1 stable assembly with no legacy option assembles", assemble("nolegacy-stable", []), cases);
  const stableDir = path.join(temp, "nolegacy-stable", "stable");
  expectCheck(
    "LF1 a stable assembly writes bwc-stable-v2.json and no bwc-stable.json",
    fs.existsSync(path.join(stableDir, "bwc-stable-v2.json")) &&
      fs.existsSync(path.join(stableDir, "bwc-stable-v2.json.minisig")) &&
      !fs.existsSync(path.join(stableDir, STRAY_NAME)) &&
      !fs.existsSync(path.join(stableDir, `${STRAY_NAME}.minisig`)),
    fs.existsSync(stableDir) ? fs.readdirSync(stableDir).join(", ") : "(no folder)",
    cases,
  );
  const stableAssembly = fs.existsSync(path.join(stableDir, "assembly.json"))
    ? JSON.parse(fs.readFileSync(path.join(stableDir, "assembly.json"), "utf8"))
    : {};
  expectCheck(
    "LF1 assembly.json has no legacy field",
    stableAssembly.ok === true && !Object.keys(stableAssembly).some((key) => /legacy/i.test(key)),
    Object.keys(stableAssembly).join(", "),
    cases,
  );

  // LF1: a stale option is an unknown option now, so it fails loudly and writes nothing.
  for (const stale of [["--legacy-feed", "none"], ["--legacy-source", path.join(temp, "gone.json")]]) {
    const outName = `nolegacy-stale-${stale[0].slice(2)}`;
    const refused = assemble(outName, stale);
    expectCheck(
      `LF1 stale ${stale[0]} is refused as an unknown option`,
      refused.status === EXIT.USAGE && refused.stderr.includes(stale[0]),
      `status ${refused.status}: ${refused.stderr.split("\n")[0].trim()}`,
      cases,
    );
    expectCheck(
      `LF1 the refused ${stale[0]} run wrote no bundle`,
      !fs.existsSync(path.join(temp, outName, "stable")),
      "no stable folder",
      cases,
    );
  }

  // LF1: the gate that draft, verify, and publish share loads the bundle with no legacy asset in the set.
  const bundle = await loadReleaseBundle({
    dir: stableDir,
    channel: "stable",
    version: VERSION_UNDER_TEST,
    trustedKeys: [publicPath],
    requireArches: ["win-x64"],
  });
  expectCheck(
    "LF1 a stable bundle with no legacy record loads, and no bwc-stable.json is required",
    bundle.ok && !bundle.requiredAssets.includes(STRAY_NAME) && !bundle.requiredAssets.includes(`${STRAY_NAME}.minisig`),
    JSON.stringify(bundle.errors),
    cases,
  );

  // LF1: a draft that already holds bwc-stable.json is refused asset-unexpected (release-draft and release-verify print
  // that reason for each name that unexpectedAssets returns).
  const asset = (name, id) => ({ id, name, state: "uploaded" });
  const names = expectedAssetNames(bundle);
  const release = { assets: names.map((name, index) => asset(name, index + 1)) };
  expectCheck(
    "LF1 the expected set lists the v2 manifest, its signature, and the setups, and no bwc-stable.json",
    names.includes(manifestFileName("stable")) &&
      names.includes(`${manifestFileName("stable")}.minisig`) &&
      names.some((name) => name.endsWith("-setup.exe")) &&
      !names.includes(STRAY_NAME),
    JSON.stringify(names),
    cases,
  );
  const sidecars = [manifestFileName("stable"), ...names.filter((name) => name.endsWith("-setup.exe"))].flatMap(
    (name) => [`${name}.minisig`, `${name}.sig`],
  );
  expectCheck(
    "LF1 the expected set holds every .minisig and .sig that release:assemble writes and release:draft uploads",
    sidecars.length > 2 && sidecars.every((name) => names.includes(name)),
    JSON.stringify(names),
    cases,
  );
  expectCheck(
    "LF1 a release that holds exactly the expected assets has nothing unexpected",
    unexpectedAssets(release, names).length === 0,
    "an unexpected asset was reported",
    cases,
  );
  const withStray = { assets: [...release.assets, asset(STRAY_NAME, 90), asset(`${STRAY_NAME}.minisig`, 91)] };
  expectCheck(
    "LF1 a draft that holds bwc-stable.json is refused asset-unexpected, naming it and its signature",
    unexpectedAssets(withStray, names).map((entry) => entry.name).join(",") === `${STRAY_NAME},${STRAY_NAME}.minisig`,
    JSON.stringify(unexpectedAssets(withStray, names)),
    cases,
  );
  expectCheck(
    "LF1 any other extra asset is unexpected, and a GPG .asc of an expected asset is a sidecar",
    unexpectedAssets({ assets: [...release.assets, asset("notes.txt", 92), asset("evil.exe", 93)] }, names).length === 2 &&
      unexpectedAssets({ assets: [...release.assets, asset(`${names[0]}.asc`, 94)] }, names).length === 0 &&
      unexpectedAssets({ assets: [...release.assets, asset("stray.asc", 95)] }, names).length === 1 &&
      unexpectedAssets({ assets: [...release.assets, asset(`${names[0]}.ASC.exe`, 96)] }, names).length === 1,
    "an extra asset was accepted or a sidecar refused",
    cases,
  );

  // C20: a nonzero client exit names the verdict that the client printed on stdout.
  const refusal = (run) => {
    try {
      verdictOf("check", run, "0.1.2");
      return null;
    } catch (error) {
      return error;
    }
  };
  const elevated = refusal({
    code: 1,
    stdout: '{"ok":false,"failure":"RunningElevated","detail":"run unelevated","version":null}\n',
    stderr: "",
  });
  expectCheck(
    "C20 a nonzero exit with a stdout verdict is client-failed and names the failure and detail",
    elevated?.reason === "client-failed" &&
      elevated.message.includes("exited 1") &&
      elevated.message.includes("RunningElevated") &&
      elevated.message.includes("run unelevated"),
    String(elevated?.message),
    cases,
  );
  const crashed = refusal({ code: 7, stdout: "", stderr: "boom" });
  expectCheck(
    "C20 a nonzero exit with no verdict still reports the exit code and the stderr tail",
    crashed?.reason === "client-failed" && crashed.message.includes("exited 7") && crashed.message.includes("boom"),
    String(crashed?.message),
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
    [
      "# ⬇️ Downloads",
      "",
      `[x64](https://github.com/example/bwc/releases/download/v${VERSION}/BetterWindowsConfig-${VERSION}-win-x64-setup.exe)`,
      "",
      `## Changes in \`v${VERSION}:\``,
      "",
      "- **Testing:** Release tooling self-test.",
      "",
    ].join("\n"),
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

const GATE_PATH = path.join("artifacts", "gate", "result.json");

/** The result of one e2e step of a full gate, shaped like a test-all.js step entry. */
function e2eGateStep(name, rid, flavor, overrides = {}) {
  return {
    name,
    rid,
    pass: true,
    exitCode: 0,
    e2eResult: { path: `artifacts/e2e/result-${rid}-${flavor}.json`, exists: true, pass: true, passed: 4, failed: 0, total: 4, error: null },
    ...overrides,
  };
}

/** The rids and steps a full `test-all.js --rid all` records, with a shared step and both e2e steps per rid. */
function fullGateRids(rids = ["win-arm64", "win-x64"]) {
  return {
    rids: rids.map((rid) => ({ rid, platform: "windows", status: "pass", steps: ["e2e-ship", "e2e-flavor"] })),
    steps: [
      { name: "validate", rid: null, pass: true, exitCode: 0, e2eResult: null },
      ...rids.flatMap((rid) => [e2eGateStep("e2e-ship", rid, "ship"), e2eGateStep("e2e-flavor", rid, "e2e")]),
    ],
  };
}

/** Writes artifacts/gate/result.json. Fields not given describe a passing full gate for both rids on HEAD. */
function writeGate(repo, fields = {}) {
  const head = git(repo, ["rev-parse", "HEAD"]);
  writeJson(path.join(repo, GATE_PATH), {
    schema: 1,
    rid: "all",
    quick: false,
    pass: true,
    git: { commit: head, dirty: false },
    ...fullGateRids(),
    ...fields,
  });
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

  writeGate(repo);
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
  fs.copyFileSync(fixture.primary.manifestPath, path.join(x64Artifact, "bwc-beta-v2.json"));
  fs.copyFileSync(fixture.primary.signaturePath, path.join(x64Artifact, "bwc-beta-v2.json.minisig"));
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

// ---- signing readiness (preflight --require-trusted-key), with injected dependencies -----------

const GPG_FPR = "0123456789ABCDEF0123456789ABCDEF01234567";
const READY_VERSION = "0.1.0";
const READY_BETA = VERSION;
const READY_ENV = Object.freeze({
  GPG_KEY_ID: GPG_FPR,
  GPG_PASSPHRASE: "selftest-only-passphrase",
  ...Object.fromEntries(SIGNING_REQUIRED_VARIABLES.map((name) => [name, "selftest-value"])),
});

/** A gpg stand-in for the readiness probe: --version, --list-secret-keys, --detach-sign, --verify. */
function fakeGpg({ version = "ok", keyList = "ok", sign = "ok", verify = "ok" } = {}) {
  return (args) => {
    const ok = { status: 0, stdout: "", stderr: "", error: null };
    if (args[0] === "--version") {
      if (version === "missing") return { ...ok, status: null, error: Object.assign(new Error("spawn gpg ENOENT"), { code: "ENOENT" }) };
      return { ...ok, stdout: "gpg (GnuPG) 2.4.9\n" };
    }
    if (args.includes("--list-secret-keys")) {
      if (keyList === "none") return { ...ok, status: 2, stderr: "gpg: no secret key" };
      const fingerprint = keyList === "other" ? "F".repeat(40) : GPG_FPR;
      return { ...ok, stdout: `sec:u:255:22:0000000000000000:0:::u:::scESC:::::::::\nfpr:::::::::${fingerprint}:\n` };
    }
    if (args.includes("--detach-sign")) {
      if (sign === "fail") return { ...ok, status: 2, stderr: "gpg: bad passphrase" };
      fs.writeFileSync(args[args.indexOf("--output") + 1], "-----BEGIN PGP SIGNATURE-----\n");
      return ok;
    }
    if (args.includes("--verify")) {
      if (verify === "bad") return { ...ok, status: 1 };
      return {
        ...ok,
        stdout: `[GNUPG:] GOODSIG 0 BWC\n[GNUPG:] VALIDSIG ${GPG_FPR} 2026-01-01 1767225600 0 4 0 1 8 00 ${GPG_FPR}\n`,
      };
    }
    return { ...ok, status: 1, stderr: "unexpected gpg call" };
  };
}

/** Runs checkReleaseReadiness. An env value of undefined removes the variable. */
function readiness(root, fixture, { env = {}, deps = {}, version = READY_BETA, channel = "beta" } = {}) {
  const merged = { ...READY_ENV, ...env };
  for (const name of Object.keys(merged)) if (merged[name] === undefined) delete merged[name];
  const calls = { gh: 0, listReleases: 0 };
  const base = {
    loadUpdateKey: () => ({ ok: true, text: fixture.keyA.publicKeyText }),
    runGpg: fakeGpg(),
    probeDirectory: () => ({ ok: true }),
    findMakensis: () => ({ path: "C:\\NSIS\\makensis.exe", searched: ["C:\\NSIS\\makensis.exe"] }),
    checkSigningTools: () => ({ ok: true, detail: "SignTool" }),
    nodeVersion: "v26.13.1",
    refExists: () => true,
    isAncestor: () => true,
    ghAuthStatus: () => {
      calls.gh += 1;
      return { ok: true };
    },
    listReleases: () => {
      calls.listReleases += 1;
      return [];
    },
  };
  const result = checkReleaseReadiness(root, { version, channel, commit: FAKE_COMMIT, env: merged }, { ...base, ...deps });
  return { ...result, calls, reasons: result.errors.map((entry) => entry.reason), text: JSON.stringify(result) };
}

function readinessRepo(temp, fixture, { placeholder = false } = {}) {
  const dir = path.join(temp, placeholder ? "ready-placeholder" : "ready");
  writeRepository(dir, { placeholder, embedded: [fixture.keyA.publicKeyText] });
  writeText(
    path.join(dir, "package.json"),
    `${JSON.stringify({ name: "ready", version: READY_BETA, engines: { node: "^26.13.1", npm: ">=12.2.2" } }, null, 2)}\n`,
  );
  return dir;
}

function expectReason(cases, name, result, reason, exitCode) {
  const found = result.errors.find((entry) => entry.reason === reason);
  const pass = Boolean(found) && (exitCode === undefined || found.exitCode === exitCode);
  cases.push({
    name,
    pass,
    exitCode: null,
    matchedReasons: found ? [found.reason] : [],
    detail: pass ? "" : `expected ${reason}${exitCode === undefined ? "" : ` exit ${exitCode}`}; got ${result.reasons.join(", ") || "no reason"}`,
  });
}

function expectNoReason(cases, name, result, reason) {
  expectCheck(name, !result.reasons.includes(reason), `${reason} was reported: ${result.reasons.join(", ")}`, cases);
}

function unmappedDriveRoot() {
  for (const letter of "QRSTUVWXYZ") {
    if (!fs.existsSync(`${letter}:\\`)) return `${letter}:\\BWC`;
  }
  return null;
}

async function runReadinessCases(temp, fixture, cases) {
  const root = readinessRepo(temp, fixture);
  const placeholderRoot = readinessRepo(temp, fixture, { placeholder: true });
  const abs = path.join(temp, "pack-loc");
  const stable = { channel: "stable", version: READY_VERSION };
  const withPack = (value, extra = {}) => ({ env: { AFTER_PACK_LOC: value }, ...extra });

  const good = readiness(root, fixture);
  expectCheck("readiness: every check passes for a beta with the embedded key", good.ok, JSON.stringify(good.errors), cases);
  expectCheck("readiness: a passing run calls gh once for the auth check and once for the releases", good.calls.gh === 1 && good.calls.listReleases === 1, JSON.stringify(good.calls), cases);

  // update key (RP1 to RP4)
  expectReason(cases, "readiness RP1: no update key refuses key-missing with exit 3", readiness(root, fixture, { deps: { loadUpdateKey: () => ({ ok: false, reason: "key-missing", detail: "TAURI_SIGNING_PRIVATE_KEY is not set" }) } }), "key-missing", 3);
  expectReason(cases, "readiness RP2: an encrypted key without its password refuses password-missing", readiness(root, fixture, { deps: { loadUpdateKey: () => ({ ok: false, reason: "password-missing", detail: "encrypted" }) } }), "password-missing", 1);
  expectReason(cases, "readiness RP3: a key that does not decrypt refuses with the parser reason", readiness(root, fixture, { deps: { loadUpdateKey: () => ({ ok: false, reason: "malformed-secret-key", detail: "checksum" }) } }), "malformed-secret-key", 1);
  expectReason(cases, "readiness RP4: an update key not embedded refuses embedded-key-mismatch", readiness(root, fixture, { deps: { loadUpdateKey: () => ({ ok: true, text: fixture.keyB.publicKeyText }) } }), "embedded-key-mismatch", 1);
  expectReason(cases, "readiness: a placeholder TrustedKeys.cs refuses placeholder-key", readiness(placeholderRoot, fixture), "placeholder-key", 1);

  // GPG (RP5 to RP9)
  expectReason(cases, "readiness RP5: GPG_KEY_ID unset refuses key-id-missing", readiness(root, fixture, { env: { GPG_KEY_ID: undefined } }), "key-id-missing", 2);
  expectReason(cases, "readiness RP5: GPG_KEY_ID that is not hex refuses key-id-invalid", readiness(root, fixture, { env: { GPG_KEY_ID: "NOT-A-KEY" } }), "key-id-invalid", 2);
  expectReason(cases, "readiness RP6: GPG_PASSPHRASE unset refuses passphrase-missing", readiness(root, fixture, { env: { GPG_PASSPHRASE: undefined } }), "passphrase-missing", 2);
  expectReason(cases, "readiness RP7: gpg not on PATH refuses gpg-missing with exit 4", readiness(root, fixture, { deps: { runGpg: fakeGpg({ version: "missing" }) } }), "gpg-missing", 4);
  expectReason(cases, "readiness RP8: no secret key for GPG_KEY_ID refuses key-not-found", readiness(root, fixture, { deps: { runGpg: fakeGpg({ keyList: "none" }) } }), "key-not-found", 3);
  expectReason(cases, "readiness RP8: a different primary fingerprint refuses key-mismatch", readiness(root, fixture, { deps: { runGpg: fakeGpg({ keyList: "other" }) } }), "key-mismatch", 1);
  expectReason(cases, "readiness RP9: a failing probe signature refuses sign-failed", readiness(root, fixture, { deps: { runGpg: fakeGpg({ sign: "fail" }) } }), "sign-failed", 4);
  expectReason(cases, "readiness RP9: a probe that does not verify refuses verify-failed", readiness(root, fixture, { deps: { runGpg: fakeGpg({ verify: "bad" }) } }), "verify-failed", 1);
  const gpgFailure = readiness(root, fixture, { deps: { runGpg: fakeGpg({ sign: "fail" }) } });
  expectCheck("readiness RP9: the passphrase never appears in the output", !gpgFailure.text.includes(READY_ENV.GPG_PASSPHRASE), "passphrase printed", cases);

  // AFTER_PACK_LOC (RP10 to RP13)
  expectReason(cases, "readiness RP10: stable without AFTER_PACK_LOC refuses after-pack-loc-missing", readiness(root, fixture, { ...stable, env: { AFTER_PACK_LOC: undefined } }), "after-pack-loc-missing", 1);
  expectReason(cases, "readiness RP10: beta with OVERRIDE_BETA_MIRROR_SKIP=1 needs AFTER_PACK_LOC", readiness(root, fixture, { env: { AFTER_PACK_LOC: undefined, OVERRIDE_BETA_MIRROR_SKIP: "1" } }), "after-pack-loc-missing", 1);
  expectNoReason(cases, "readiness RP10: beta without the override does not need AFTER_PACK_LOC", readiness(root, fixture, { env: { AFTER_PACK_LOC: undefined } }), "after-pack-loc-missing");
  expectReason(cases, "readiness RP13: a relative AFTER_PACK_LOC refuses after-pack-loc-invalid", readiness(root, fixture, { ...stable, ...withPack("mirror-out") }), "after-pack-loc-invalid", 1);
  expectReason(cases, "readiness RP13: a drive root refuses after-pack-loc-invalid", readiness(root, fixture, { ...stable, ...withPack(path.parse(temp).root) }), "after-pack-loc-invalid", 1);
  expectReason(cases, "readiness RP13: a folder inside the repository refuses after-pack-loc-invalid", readiness(root, fixture, { ...stable, ...withPack(path.join(root, "mirror")) }), "after-pack-loc-invalid", 1);
  const unmapped = unmappedDriveRoot();
  if (unmapped) {
    let threw = null;
    let unmappedResult = null;
    try {
      unmappedResult = readiness(root, fixture, {
        ...stable,
        ...withPack(unmapped),
        deps: { probeDirectory: (value) => ({ ok: false, reason: "after-pack-loc-unavailable", detail: `${value} is not mapped` }) },
      });
    } catch (error) {
      threw = error;
    }
    cases.push({
      name: "readiness RP11: an unmapped drive is reported as a reason, never thrown",
      pass: threw === null && unmappedResult?.reasons.includes("after-pack-loc-unavailable") === true,
      exitCode: null,
      detail: threw ? String(threw.message) : JSON.stringify(unmappedResult?.reasons ?? []),
    });
  } else {
    cases.push({ name: "readiness RP11: unmapped drive case (every drive letter Q to Z is mapped; skipped)", pass: true, exitCode: null, detail: "skipped" });
  }
  expectReason(cases, "readiness RP12: a folder that is not writable refuses after-pack-loc-unwritable", readiness(root, fixture, { ...stable, ...withPack(abs), deps: { probeDirectory: () => ({ ok: false, reason: "after-pack-loc-unwritable", detail: "denied" }) } }), "after-pack-loc-unwritable", 1);

  // signing environment and tools (RP14 to RP16)
  const azureMissing = readiness(root, fixture, { env: { AZURE_CLIENT_ID: undefined } });
  expectReason(cases, "readiness RP14: a missing Azure variable refuses signing-env-missing, naming the variable", azureMissing, "signing-env-missing", 1);
  expectCheck("readiness RP14: the report names the variable but not its value", azureMissing.text.includes("AZURE_CLIENT_ID") && !azureMissing.text.includes("selftest-value"), azureMissing.text, cases);
  expectNoReason(cases, "readiness RP14: SKIP_WIN_CODESIGN=1 (exactly) needs no Azure variables", readiness(root, fixture, { env: { AZURE_CLIENT_ID: undefined, SKIP_WIN_CODESIGN: "1" } }), "signing-env-missing");
  expectReason(cases, "readiness RP16: SignTool or the dlib that does not resolve refuses signing-tools-missing with exit 3", readiness(root, fixture, { deps: { checkSigningTools: () => ({ ok: false, detail: "SignTool not found" }) } }), "signing-tools-missing", 3);
  expectReason(cases, "readiness RP15: no makensis.exe refuses makensis-missing with exit 3", readiness(root, fixture, { deps: { findMakensis: () => ({ path: null, searched: [] }) } }), "makensis-missing", 3);

  // node engines (RP17)
  expectReason(cases, "readiness RP17: Node 26.13.0 does not satisfy ^26.13.1", readiness(root, fixture, { deps: { nodeVersion: "v26.13.0" } }), "node-version", 1);
  expectReason(cases, "readiness RP17: Node 27 does not satisfy ^26.13.1", readiness(root, fixture, { deps: { nodeVersion: "v27.0.0" } }), "node-version", 1);
  expectNoReason(cases, "readiness RP17: Node 26.14.0 satisfies ^26.13.1", readiness(root, fixture, { deps: { nodeVersion: "v26.14.0" } }), "node-version");

  // git (RP18, RP19)
  expectReason(cases, "readiness RP18: HEAD not contained in origin/experimental refuses head-not-pushed", readiness(root, fixture, { deps: { isAncestor: () => false } }), "head-not-pushed", 1);
  expectReason(cases, "readiness RP19: origin/experimental missing refuses remote-branch-missing", readiness(root, fixture, { deps: { refExists: () => false } }), "remote-branch-missing", 1);
  const stableGit = readiness(root, fixture, { ...stable, env: { AFTER_PACK_LOC: abs }, deps: { refExists: (ref) => ref === "origin/main" } });
  expectCheck("readiness: a stable release is checked against origin/main", stableGit.ok, JSON.stringify(stableGit.errors), cases);

  // local failures stop before any network call (RP24)
  const local = readiness(root, fixture, { env: { AZURE_CLIENT_ID: undefined } });
  expectCheck("readiness RP24: a failed local check makes no gh call", local.calls.gh === 0 && local.calls.listReleases === 0, JSON.stringify(local.calls), cases);

  // gh and releases (RP20 to RP23)
  expectReason(cases, "readiness RP20: gh not on PATH refuses gh-missing with exit 4", readiness(root, fixture, { deps: { ghAuthStatus: () => ({ ok: false, reason: "gh-missing", detail: "gh was not found" }) } }), "gh-missing", 4);
  expectReason(cases, "readiness RP21: gh not logged in refuses gh-auth-missing with exit 4", readiness(root, fixture, { deps: { ghAuthStatus: () => ({ ok: false, reason: "gh-auth-missing", detail: "not logged in" }) } }), "gh-auth-missing", 4);
  const releasedTag = [{ id: 9, tag_name: `v${READY_BETA}`, draft: false, prerelease: true, assets: [] }];
  expectReason(cases, "readiness RP22: a published release for the tag refuses release-published", readiness(root, fixture, { deps: { listReleases: () => releasedTag } }), "release-published", 1);
  expectReason(cases, "readiness RP23: a version not newer than the latest published release refuses version-not-newer", readiness(root, fixture, { deps: { listReleases: () => [{ id: 8, tag_name: "v0.1.0", draft: false, assets: [] }] } }), "version-not-newer", 1);
  expectNoReason(cases, "readiness RP23: a version newer than the latest published release passes", readiness(root, fixture, { deps: { listReleases: () => [{ id: 7, tag_name: "v0.0.9", draft: false, assets: [] }] } }), "version-not-newer");
  const draftOnly = readiness(root, fixture, { deps: { listReleases: () => [{ id: 3, tag_name: `v${READY_BETA}`, draft: true, assets: [] }] } });
  expectCheck("readiness: an existing draft for the tag is reused, not refused", draftOnly.ok, JSON.stringify(draftOnly.errors), cases);
  expectReason(cases, "readiness: two drafts for the tag refuse duplicate-drafts", readiness(root, fixture, { deps: { listReleases: () => [{ id: 1, tag_name: `v${READY_BETA}`, draft: true }, { id: 2, tag_name: `v${READY_BETA}`, draft: true }] } }), "duplicate-drafts", 1);
  expectReason(cases, "readiness: a failed releases listing refuses gh-failed with exit 4", readiness(root, fixture, { deps: { listReleases: () => { throw Object.assign(new Error("gh failed"), { reason: "gh-failed", exitCode: 4 }); } } }), "gh-failed", 4);

  // engines helper
  const engineCases = [
    ["v26.13.1", "^26.13.1", true],
    ["v26.13.0", "^26.13.1", false],
    ["v26.14.0", "^26.13.1", true],
    ["v27.0.0", "^26.13.1", false],
    ["26.13.1", "^26.13.1", true],
    ["v20.0.0", ">=20.0.0", true],
    ["v20.0.0", "not-a-range", null],
  ];
  for (const [version, range, expected] of engineCases) {
    expectCheck(`engines: ${version} against ${range} is ${expected}`, satisfiesEngineRange(version, range) === expected, String(satisfiesEngineRange(version, range)), cases);
  }
}

// ---- publish of an existing release (rerun) and the preflight CLI ---------------------------------

function runPublishDecisionCases(cases) {
  const release = { id: 5, target_commitish: FAKE_COMMIT, prerelease: false };
  expectCheck(
    "publish rerun: a published release on HEAD with the same prerelease flag is not a mismatch",
    publishedReleaseMismatch(release, { commit: FAKE_COMMIT, targetCommit: FAKE_COMMIT, prerelease: false }) === null,
    "mismatch",
    cases,
  );
  const other = publishedReleaseMismatch(release, { commit: FAKE_COMMIT, targetCommit: "f".repeat(40), prerelease: false });
  expectCheck("publish rerun: a published release on another commit refuses draft-target-mismatch", other?.reason === "draft-target-mismatch", JSON.stringify(other), cases);
  const pre = publishedReleaseMismatch(release, { commit: FAKE_COMMIT, targetCommit: FAKE_COMMIT, prerelease: true });
  expectCheck("publish rerun: a prerelease flag that differs refuses prerelease-mismatch", pre?.reason === "prerelease-mismatch", JSON.stringify(pre), cases);
}

function runReadinessCliCases(temp, fixture, cases) {
  const repo = path.join(temp, "readiness-cli");
  writeRepository(repo, { embedded: [fixture.keyA.publicKeyText] });
  git(repo, ["init", "-q", "-b", "experimental"]);
  commitAll(repo, "readiness cli fixture");
  // No update key, GPG, or Azure variables: the local checks report, and no gh or gpg runs.
  const result = run(
    "scripts/release-preflight.js",
    ["--root", repo, "--version", VERSION, "--channel", "beta", "--trusted-key", path.join(fixture.keys, "a.pub"), "--require-trusted-key"],
    { remove: ["GPG_KEY_ID", "GPG_PASSPHRASE", "SKIP_WIN_CODESIGN", "AFTER_PACK_LOC", ...SIGNING_REQUIRED_VARIABLES] },
  );
  expectRefusal("preflight --require-trusted-key reports the missing update key before any build", result, ["key-missing"], cases);
  expectCheck(
    "preflight --require-trusted-key with a missing key exits 3 (missing input)",
    result.status === EXIT.MISSING,
    `exit ${result.status}`,
    cases,
  );
  expectRefusal("preflight --require-trusted-key reports the missing GPG key in the same run", result, ["key-id-missing"], cases);
  const plain = run("scripts/release-preflight.js", ["--root", repo, "--version", VERSION, "--channel", "beta", "--trusted-key", path.join(fixture.keys, "a.pub")]);
  expectPass("preflight without --require-trusted-key keeps the existing checks only", plain, cases, "no readiness checks");
}

/** Session and gate cases. Every session refusal is listed in docs/failure-modes/release.md. */
async function runSessionGateCases(temp, fixture, cases) {
  const repo = path.join(temp, "gate-repo");
  fs.mkdirSync(repo, { recursive: true });
  git(repo, ["init", "-q", "-b", "experimental"]);
  writeRepository(repo, { embedded: [fixture.keyA.publicKeyText] });
  commitAll(repo, "gate fixture");
  const head = git(repo, ["rev-parse", "HEAD"]);
  const session = path.join(repo, "artifacts", "release", "session.json");
  const common = ["--root", repo, "--version", VERSION, "--channel", "beta", "--trusted-key", path.join(fixture.keys, "a.pub")];
  const create = (extra = []) => run("scripts/release-session.js", ["create", ...common, ...extra]);

  expectRefusal("session create refuses with no gate result (gate-missing), by default", create(), ["gate-missing"], cases);
  expectCheck("a refused session create writes no session file", !fs.existsSync(session), "", cases);
  writeGate(repo, { pass: false });
  expectRefusal("session create refuses a failing gate (gate-failed)", create(), ["gate-failed"], cases);
  writeGate(repo, { git: { commit: "f".repeat(40), dirty: false } });
  expectRefusal("session create refuses a gate for another commit (gate-commit-mismatch)", create(), ["gate-commit-mismatch"], cases);
  writeGate(repo, { git: { commit: head, dirty: true } });
  expectRefusal("session create refuses a gate recorded on a dirty tree (gate-dirty-tree)", create(), ["gate-dirty-tree"], cases);
  writeGate(repo, { quick: true, rid: "all", rids: fullGateRids().rids.map((entry) => ({ ...entry, status: "skipped" })), steps: [] });
  expectRefusal("session create refuses a quick gate (gate-quick)", create(), ["gate-quick"], cases);
  expectCheck("a refused quick gate writes no session file", !fs.existsSync(session), "", cases);
  writeGate(repo, { rid: "win-arm64", ...fullGateRids(["win-arm64"]) });
  expectRefusal("session create refuses a full gate for win-arm64 only (gate-rid-missing)", create(), ["gate-rid-missing"], cases);
  writeGate(repo, { rid: "win-x64", ...fullGateRids(["win-x64"]) });
  expectRefusal("session create refuses a full gate for win-x64 only (gate-rid-missing)", create(), ["gate-rid-missing"], cases);
  const noE2e = fullGateRids();
  noE2e.steps = noE2e.steps.filter((step) => !(step.rid === "win-x64" && step.name === "e2e-flavor"));
  writeGate(repo, noE2e);
  expectRefusal("session create refuses a gate with a missing e2e step (gate-e2e-not-run)", create(), ["gate-e2e-not-run"], cases);
  const failedStep = fullGateRids();
  failedStep.steps.find((step) => step.rid === "win-arm64" && step.name === "e2e-ship").pass = false;
  writeGate(repo, failedStep);
  expectRefusal("session create refuses a gate where one step failed (gate-failed)", create(), ["gate-failed"], cases);
  const notRun = fullGateRids();
  notRun.rids.find((entry) => entry.rid === "win-x64").status = "not-run";
  writeGate(repo, notRun);
  expectRefusal("session create refuses a rid that is not-run (gate-rid-missing)", create(), ["gate-rid-missing"], cases);
  const noResult = fullGateRids();
  noResult.steps.find((step) => step.rid === "win-x64" && step.name === "e2e-ship").e2eResult.exists = false;
  writeGate(repo, noResult);
  expectRefusal("session create refuses an e2e step whose result file is absent (gate-e2e-not-run)", create(), ["gate-e2e-not-run"], cases);
  const failingResult = fullGateRids();
  Object.assign(failingResult.steps.find((step) => step.rid === "win-arm64" && step.name === "e2e-flavor").e2eResult, { pass: false, failed: 1 });
  writeGate(repo, failingResult);
  expectRefusal("session create refuses an e2e step whose result has failures (gate-e2e-not-run)", create(), ["gate-e2e-not-run"], cases);
  writeGate(repo);
  expectPass("session create records a passing full gate for both rids without any flag", create(), cases, "quality gate recorded");
  const recorded = JSON.parse(fs.readFileSync(session, "utf8")).qualityGate;
  expectCheck(
    "the recorded gate is full, names both rids, and has e2e passed",
    recorded?.mode === "full" && recorded?.e2e === "passed" && JSON.stringify(recorded?.rids) === '["win-arm64","win-x64"]',
    JSON.stringify(recorded),
    cases,
  );
  expectPass("--require-gate is still accepted as a no-op", create(["--require-gate", "--force"]), cases, "accepted");
  expectPass("session verify passes with a recorded gate", run("scripts/release-session.js", ["verify", ...common]), cases, "gate present");

  const data = JSON.parse(fs.readFileSync(session, "utf8"));
  delete data.qualityGate;
  writeJson(session, data);
  expectRefusal(
    "session verify refuses a session with no quality gate (missing-quality-gate)",
    run("scripts/release-session.js", ["verify", ...common]),
    ["missing-quality-gate"],
    cases,
  );
}

/** A detached HEAD contained only in origin/<expected branch> passes --allow-detached (remote branches count). */
function runDetachedBranchCases(temp, cases) {
  const repo = path.join(temp, "detached-repo");
  fs.mkdirSync(repo, { recursive: true });
  git(repo, ["init", "-q", "-b", "experimental"]);
  writeText(path.join(repo, "file.txt"), "one\n");
  commitAll(repo, "detached fixture");
  git(repo, ["update-ref", "refs/remotes/origin/experimental", git(repo, ["rev-parse", "HEAD"])]);
  git(repo, ["checkout", "-q", "--detach"]);
  const contained = checkBranch(repo, { channel: "beta" }, { allowDetached: true });
  expectCheck("--allow-detached accepts HEAD contained in origin/experimental", contained.errors.length === 0, JSON.stringify(contained.errors), cases);
  writeText(path.join(repo, "file.txt"), "two\n");
  commitAll(repo, "unpushed detached commit");
  const unrelated = checkBranch(repo, { channel: "beta" }, { allowDetached: true });
  expectCheck(
    "--allow-detached still refuses a detached HEAD that no expected branch contains",
    unrelated.errors[0]?.reason === "branch-mismatch",
    JSON.stringify(unrelated.errors),
    cases,
  );
}

/** Pure gate-proof and manifest-URL cases: a dirty gate and a malformed escape are refusals, not throws. */
function runGateProofAndUrlCases(cases) {
  const fullGate = (git) => ({ pass: true, quick: false, git, ...fullGateRids() });
  const dirty = qualityGateProof(fullGate({ commit: FAKE_COMMIT, dirty: true }), FAKE_COMMIT);
  expectCheck("qualityGateProof refuses git.dirty true (gate-dirty-tree)", dirty.reason === "gate-dirty-tree", JSON.stringify(dirty), cases);
  const clean = qualityGateProof(fullGate({ commit: FAKE_COMMIT, dirty: false }), FAKE_COMMIT);
  expectCheck("qualityGateProof accepts a full gate for both rids on a clean HEAD", clean.ok === true && clean.record.mode === "full", JSON.stringify(clean), cases);
  const quick = qualityGateProof({ pass: true, quick: true, git: { commit: FAKE_COMMIT, dirty: false } }, FAKE_COMMIT);
  expectCheck("qualityGateProof refuses a quick gate (gate-quick)", quick.reason === "gate-quick", JSON.stringify(quick), cases);
  const noRids = qualityGateProof({ pass: true, quick: false, git: { commit: FAKE_COMMIT, dirty: false } }, FAKE_COMMIT);
  expectCheck("qualityGateProof refuses a full gate with no rids array (gate-rid-missing)", noRids.reason === "gate-rid-missing", JSON.stringify(noRids), cases);

  const base = `https://github.com/BurntToasters/better-windows-config/releases/download/v${VERSION}`;
  const manifest = {
    installers: {
      "win-x64": { url: `${base}/Bad%ZZ.exe` },
      "win-arm64": { url: `${base}/ok.exe` },
    },
  };
  const named = malformedInstallerUrls(manifest);
  expectCheck("a malformed percent escape names its architecture (asset-url-malformed)", JSON.stringify(named) === '["win-x64"]', JSON.stringify(named), cases);
  let thrown = null;
  let files = null;
  try {
    files = assetFilesFromManifest("C:/unused", manifest);
  } catch (error) {
    thrown = error;
  }
  expectCheck(
    "assetFilesFromManifest does not throw on a malformed escape",
    thrown === null && files !== null && !("win-x64" in files) && "win-arm64" in files,
    thrown ? String(thrown) : JSON.stringify(files),
    cases,
  );
}

/** Publish folder reuse: the build record must match this version and pass, and name HEAD when it names a commit. */
function runPublishReuseCases(temp, cases) {
  const build = path.join(temp, "reuse-build");
  const publish = path.join(temp, "reuse-publish", "win-x64");
  writeText(path.join(publish, "BetterWindowsConfig.exe"), "exe");
  const reuse = (fields, extra = {}) => {
    writeJson(path.join(build, "win-x64.json"), { schema: 2, version: VERSION, rid: "win-x64", flavor: "ship", pass: true, ...fields });
    return publishIsGood({ rid: "win-x64", flavor: "ship", publishDir: publish, buildArtifactDir: build, packageVersion: VERSION, ...extra });
  };
  expectCheck("publish reuse: a passing record for this version is reused", reuse({}) === true, "reused", cases);
  expectCheck("publish reuse: a record for another version is not reused", reuse({ version: OTHER_VERSION }) === false, "not reused", cases);
  expectCheck("publish reuse: a failed record is not reused", reuse({ pass: false }) === false, "not reused", cases);
  expectCheck("publish reuse: a record naming HEAD is reused", reuse({ commit: FAKE_COMMIT }, { head: FAKE_COMMIT }) === true, "reused", cases);
  expectCheck(
    "publish reuse: a record naming another commit is not reused",
    reuse({ commit: FAKE_COMMIT }, { head: "f".repeat(40) }) === false,
    "not reused",
    cases,
  );
}

/**
 * C21 to C25: the client rehearsal starts every run through scripts/run-restricted.ps1. node.exe stands in for the
 * client, so these cases need no build and no network. From an elevated shell they show the de-elevation itself.
 */
async function runRestrictedLaunchCases(cases) {
  const node = process.execPath;
  const tricky = ["a b", 'q"x', "tr\\", "end\\\\", "", "uni é", '--flag="v w"'];
  const echo = await runClient(node, ["-e", "console.log(JSON.stringify(process.argv.slice(1)))", ...tricky], 60_000);
  expectCheck(
    "C21 a client run is unelevated, at medium integrity, through the restricted token",
    echo.launch?.mode === "restricted-token" && echo.launch.elevated === false && echo.launch.integrity === "0x2000",
    JSON.stringify(echo.launch),
    cases,
  );
  expectCheck(
    "C25 arguments with spaces, quotes, trailing backslashes, empty, and non-ASCII text arrive unchanged",
    echo.code === 0 && echo.stdout.trim() === JSON.stringify(tricky),
    echo.stdout.trim(),
    cases,
  );
  const exited = await runClient(node, ["-e", "process.stderr.write('err-text');process.exit(5)"], 60_000);
  expectCheck(
    "C21 the client's exit code and stderr come back from the restricted run",
    exited.code === 5 && exited.stderr === "err-text",
    JSON.stringify({ code: exited.code, stderr: exited.stderr }),
    cases,
  );
  const missing = await runClient(path.join(os.tmpdir(), "bwc-no-such-client.exe"), ["--check"], 60_000).then(
    () => null,
    (error) => error,
  );
  expectCheck(
    "C22 a client that cannot be started is deelevate-failed, and nothing else is started",
    missing?.reason === "deelevate-failed" && missing.message.includes("create-process"),
    String(missing?.message),
    cases,
  );
  const dllInit = await runClient(node, ["-e", "process.exit(-1073741502)"], 60_000).then(
    () => null,
    (error) => error,
  );
  expectCheck(
    "C27 a client that dies at start with 0xC0000142 is deelevate-failed and names its desktop, not a client verdict",
    dllInit?.reason === "deelevate-failed" && dllInit.message.includes("0xC0000142") && dllInit.message.includes("desktop ("),
    String(dllInit?.message),
    cases,
  );
  const hung = await runClient(node, ["-e", "setTimeout(() => {}, 120000)"], 2_000).then(
    () => null,
    (error) => error,
  );
  expectCheck("C24 a client that hangs is ended at the timeout and refused timeout", hung?.reason === "timeout", String(hung?.message), cases);
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
    await runNoLegacyFeedCases(temp, fixture, cases);
    await runEnvironmentKeyCases(temp, fixture, cases);
    await runHardeningCases(temp, fixture, cases);
    await runPolicyCases(temp, fixture, cases);
    await runSessionGateCases(temp, fixture, cases);
    runDetachedBranchCases(temp, cases);
    runGateProofAndUrlCases(cases);
    runPublishReuseCases(temp, cases);
    await runReadinessCases(temp, fixture, cases);
    runPublishDecisionCases(cases);
    runReadinessCliCases(temp, fixture, cases);
    await runRestrictedLaunchCases(cases);
    expectCheck(
      "release-verify accepts --allow-published (the publish rerun check)",
      (() => {
        const result = run("scripts/release-verify.js", ["--allow-published", "--json"]);
        return result.status !== EXIT.USAGE && !result.stderr.includes("unrecognized option");
      })(),
      "the flag is refused as an unknown option",
      cases,
    );
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
