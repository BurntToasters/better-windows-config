#!/usr/bin/env node
/**
 * Executable self-test for the release tooling. It creates temporary keys and manifests,
 * runs the real CLI scripts as child processes, and proves the named refusal reasons.
 *
 * No network, no gh, no gpg, no NSIS. The result is artifacts/release/selftest/result.json.
 */
import { execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { EXIT, repoRootFrom, writeJson } from "./lib/cli.js";
import { envelope, generateKeyPair, signFile } from "./lib/minisign.js";
import { buildManifest, serializeManifest } from "./lib/update-manifest.js";

const SCRIPT = "scripts/release-selftest.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const RESULT_PATH = path.join(REPO_ROOT, "artifacts", "release", "selftest", "result.json");
const VERSION = "0.1.0-beta.1";
const OTHER_VERSION = "0.2.0-beta.1";
const PUBDATE = "2026-11-11T00:00:00Z";

function run(script, args, { cwd = REPO_ROOT } = {}) {
  const result = spawnSync(process.execPath, [path.join(REPO_ROOT, script), ...args], {
    cwd,
    encoding: "utf8",
    windowsHide: true,
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

async function buildFixture(temp) {
  const keyA = generateKeyPair({ comment: "untrusted comment: BWC selftest key A" });
  const keyB = generateKeyPair({ comment: "untrusted comment: BWC selftest key B" });
  const keys = path.join(temp, "keys");
  writeText(path.join(keys, "a.pub"), keyA.publicKeyText);
  writeText(path.join(keys, "b.pub"), keyB.publicKeyText);
  writeText(path.join(keys, "placeholder.pub"), "untrusted comment: PLACEHOLDER replace with the release public key\nPLACEHOLDER-NOT-A-KEY\n");

  const installerBytes = Buffer.from(Array.from({ length: 4096 }, (_, index) => (index * 31 + 7) & 0xff));
  const installerName = `BetterWindowsConfig-${VERSION}-win-x64-setup.exe`;
  const installerPath = path.join(temp, "fixtures", installerName);
  write(installerPath, installerBytes);
  const installerSignature = await signFile(
    installerPath,
    keyA.secretKeyText,
    `timestamp:1794102823 file:${installerName} version:${VERSION} arch:win-x64`,
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

  async function writeManifest(directory, value) {
    fs.mkdirSync(directory, { recursive: true });
    const manifestPath = path.join(directory, "manifest.json");
    fs.writeFileSync(manifestPath, serializeManifest(value));
    const signatureText = await signFile(manifestPath, keyA.secretKeyText, `version:${value.version}`);
    fs.writeFileSync(`${manifestPath}.minisig`, signatureText, "utf8");
    return { manifestPath, signaturePath: `${manifestPath}.minisig` };
  }

  const primary = await writeManifest(path.join(temp, "fixtures", "primary"), manifest);

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
  const expired = await writeManifest(path.join(temp, "fixtures", "expired"), expiredManifest);

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
  const wrongArch = await writeManifest(path.join(temp, "fixtures", "wrong-arch"), wrongArchManifest);

  const malformed = path.join(temp, "fixtures", "malformed");
  fs.mkdirSync(malformed, { recursive: true });
  writeText(path.join(malformed, "manifest.json"), "{ this is not json\n");
  writeText(path.join(malformed, "manifest.json.minisig"), "untrusted comment: not a signature\n");

  const unsigned = path.join(temp, "fixtures", "unsigned");
  fs.mkdirSync(unsigned, { recursive: true });
  fs.copyFileSync(primary.manifestPath, path.join(unsigned, "manifest.json"));

  return {
    keyA,
    keyB,
    keys,
    installerPath,
    installerSignature,
    manifest,
    primary,
    badHashPath,
    shortPath,
    expired,
    wrongArch,
    malformed,
    unsigned,
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
      const keygen = run("scripts/sign-update-manifest.js", ["keygen", "--out", path.join(temp, "keygen"), "--test-key"]);
      if (keygen.status !== 0) return keygen;
      const input = path.join(temp, "signer-input.txt");
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
    "downgrade rejected",
    run(
      "scripts/validate-updater-manifest.js",
      validatorArgs(fixture, { extra: ["--current-version", OTHER_VERSION] }),
    ),
    ["downgrade"],
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

function writeRepository(temp, { placeholder = false } = {}) {
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
  writeText(
    path.join(temp, "src", "Bwc.Update", "Signing", "TrustedKeys.cs"),
    `namespace Bwc.Update;\n\npublic static class TrustedKeys\n{\n    public const bool IsPlaceholder = ${placeholder ? "true" : "false"};\n}\n`,
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
  writeRepository(repo);
  commitAll(repo, "selftest fixture");

  const common = ["--root", repo, "--version", VERSION, "--channel", "beta", "--trusted-key", path.join(fixture.keys, "a.pub")];
  expectPass(
    "preflight passes on a clean experimental tree",
    run("scripts/release-preflight.js", common),
    cases,
    "clean tree, version, changelog, branch, key",
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
  fs.copyFileSync(fixture.primary.manifestPath, path.join(x64Artifact, "manifest.json"));
  fs.copyFileSync(fixture.primary.signaturePath, path.join(x64Artifact, "manifest.json.minisig"));
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

  git(repo, ["checkout", "-q", "-b", "main"]);
  expectRefusal(
    "wrong branch rejected",
    run("scripts/release-preflight.js", common),
    ["branch-mismatch"],
    cases,
  );
  git(repo, ["checkout", "-q", "experimental"]);

  writeRepository(repo, { placeholder: true });
  commitAll(repo, "placeholder key");
  expectRefusal(
    "placeholder key source rejected",
    run("scripts/release-preflight.js", common),
    ["placeholder-key"],
    cases,
  );
  writeRepository(repo);
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
