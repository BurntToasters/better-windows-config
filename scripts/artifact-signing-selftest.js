#!/usr/bin/env node
/**
 * Offline self-test for Authenticode signing. It needs no network, no Azure account, no certificate,
 * and it changes no certificate store. The PowerShell checks run only on Windows.
 *
 * Checks the signing policy, the variable list, the signing target list, the NSIS defines, the dry
 * run, and the refusals of the PowerShell scripts (missing variables, tool overrides, verification).
 *
 * Run: node scripts/artifact-signing-selftest.js
 * Writes artifacts/signing-selftest.json. Exit 0 when no check fails (skips are printed), 1 otherwise.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  auditNsisSource,
  buildPayloadNsh,
  publishSigningTargets,
  SIGNING_DEFINE_NAMES,
  signingDefines,
  signingPathProblem,
} from "./build-installer.js";
import {
  bwcSigningTargets,
  missingSigningVariables,
  signingPolicy,
  SIGNING_REQUIRED_VARIABLES,
} from "./release-policy.js";

const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const SCRIPTS = path.join(REPO_ROOT, "scripts");
const SIGN_PS1 = path.join(SCRIPTS, "windows-artifact-sign.ps1");
const VERIFY_PS1 = path.join(SCRIPTS, "verify-windows-authenticode.ps1");
const BUILD_INSTALLER = path.join(SCRIPTS, "build-installer.js");
const NSIS_SCRIPT = path.join(REPO_ROOT, "installer", "bwc.nsi");
const RESULT_FILE = path.join(REPO_ROOT, "artifacts", "signing-selftest.json");
const SETUP_HINT = "npm run setup:win:artifact-signing";
const SKIP_MESSAGE = "SKIP_WIN_CODESIGN=1; leaving Windows artifact unsigned";
const VERIFY_SKIP_MESSAGE = "SKIP_WIN_CODESIGN=1; skipping Authenticode verification";
const DUMMY_VALUES = {
  AZURE_CLIENT_ID: "00000000-0000-0000-0000-000000000001",
  AZURE_TENANT_ID: "00000000-0000-0000-0000-000000000002",
  AZURE_CLIENT_SECRET: "selftest-not-a-secret",
  AZURE_ARTIFACT_SIGNING_ENDPOINT: "https://example.invalid/",
  AZURE_ARTIFACT_SIGNING_ACCOUNT: "selftest-account",
  AZURE_ARTIFACT_SIGNING_PROFILE: "selftest-profile",
  AZURE_ARTIFACT_SIGNING_PUBLISHER: "Selftest Publisher",
  AZURE_ARTIFACT_SIGNING_PUBLISHER_DN: "CN=Selftest Publisher, O=Selftest, C=US",
};
// The root-level BWC binaries of a publish folder, as dotnet-build produces them.
const PUBLISH_FIXTURE = [
  "BetterWindowsConfig.exe",
  "BetterWindowsConfig.dll",
  "BetterWindowsConfig.deps.json",
  "BetterWindowsConfig.runtimeconfig.json",
  "BwcRegedit.exe",
  "BwcRegedit.dll",
  "BwcUpdate.exe",
  "BwcUpdate.dll",
  "Bwc.Core.dll",
  "Bwc.Registry.dll",
  "Bwc.Regedit.dll",
  "Bwc.UI.dll",
  "Microsoft.UI.Xaml.dll",
  "Microsoft.WindowsAppRuntime.Bootstrap.dll",
  "hostfxr.dll",
  "coreclr.dll",
  "System.Text.Json.dll",
  "Newtonsoft.Json.dll",
];
const EXPECTED_TARGETS = [
  "BetterWindowsConfig.dll",
  "BetterWindowsConfig.exe",
  "Bwc.Core.dll",
  "Bwc.Regedit.dll",
  "Bwc.Registry.dll",
  "Bwc.UI.dll",
  "BwcRegedit.dll",
  "BwcRegedit.exe",
  "BwcUpdate.dll",
  "BwcUpdate.exe",
];
const NSIS_REQUIRED_DEFINES = [
  "BWC_VERSION", "BWC_FILE_VERSION", "BWC_RID", "BWC_ARCH", "BWC_OUTPUT", "BWC_PAYLOAD_NSH",
  "BWC_SCRIPTS_DIR", "BWC_UPDATER_SRC", "BWC_LICENSE", "BWC_NOTICES", "BWC_ICON", "BWC_ESTIMATED_KB",
];

const results = [];

function pass(name, detail) {
  results.push({ name, status: "pass", detail });
}

function fail(name, detail) {
  results.push({ name, status: "fail", detail });
}

function skip(name, detail) {
  results.push({ name, status: "skip", detail });
}

/** A copy of the environment with every AZURE_* variable and SKIP_WIN_CODESIGN removed. */
function cleanEnv(overrides = {}) {
  const env = { ...process.env };
  for (const key of Object.keys(env)) {
    if (key.startsWith("AZURE_") || key === "SKIP_WIN_CODESIGN") delete env[key];
  }
  return { ...env, ...overrides };
}

function fullAzureEnv(extra = {}) {
  return cleanEnv({ ...DUMMY_VALUES, ...extra });
}

function runPowerShell(script, args, env) {
  const result = spawnSync(
    "powershell.exe",
    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, ...args],
    { encoding: "utf8", windowsHide: true, env, timeout: 180_000 },
  );
  return {
    status: result.status,
    output: `${result.stdout ?? ""}${result.stderr ?? ""}`,
    error: result.error ?? null,
  };
}

function runNode(args, env) {
  const result = spawnSync(process.execPath, args, {
    encoding: "utf8",
    windowsHide: true,
    env,
    timeout: 120_000,
  });
  return { status: result.status, output: `${result.stdout ?? ""}${result.stderr ?? ""}`, error: result.error ?? null };
}

/** Looks for the Windows SDK SignTool that the dev VM has. Returns null when it is absent. */
function findSdkSignTool() {
  const kits = path.join(process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)", "Windows Kits", "10", "bin");
  if (!fs.existsSync(kits)) return null;
  const versions = fs.readdirSync(kits).filter((name) => /^10\.\d+\.\d+\.\d+$/.test(name)).sort().reverse();
  for (const version of versions) {
    const candidate = path.join(kits, version, "x64", "signtool.exe");
    if (fs.existsSync(candidate)) return candidate;
  }
  return null;
}

/** True when the PE header declares an Authenticode certificate table. Returns null for a non-PE file. */
function hasCertificateTable(buffer) {
  if (buffer.length < 0x40) return null;
  const peOffset = buffer.readUInt32LE(0x3c);
  if (peOffset + 24 > buffer.length || buffer.toString("latin1", peOffset, peOffset + 4) !== "PE\0\0") return null;
  const magic = buffer.readUInt16LE(peOffset + 24);
  const directories = peOffset + 24 + (magic === 0x20b ? 112 : 96);
  // Data directory 4 is the certificate table: its size is at offset 4 of the 8-byte entry.
  return buffer.readUInt32LE(directories + 4 * 8 + 4) > 0;
}

/** A minimal PE32+ header with no certificate table. Used only when no unsigned BWC binary exists. */
function minimalUnsignedPe() {
  const buffer = Buffer.alloc(0x148);
  buffer.write("MZ", 0, "latin1");
  buffer.writeUInt32LE(0x40, 0x3c);
  buffer.set([0x50, 0x45, 0x00, 0x00], 0x40);
  buffer.writeUInt16LE(0x8664, 0x44);
  buffer.writeUInt16LE(0xf0, 0x44 + 16);
  buffer.writeUInt16LE(0x0022, 0x44 + 18);
  buffer.writeUInt16LE(0x20b, 0x58);
  return buffer;
}

/**
 * Returns the path of an unsigned BWC-built EXE under publish/<rid>, or null. The file is not signed,
 * so the verify check cannot pass by accident.
 */
function findUnsignedPublishExe() {
  const publishRoot = path.join(REPO_ROOT, "publish");
  if (!fs.existsSync(publishRoot)) return null;
  for (const rid of fs.readdirSync(publishRoot)) {
    for (const name of ["BwcUpdate.exe", "BwcRegedit.exe", "BetterWindowsConfig.exe"]) {
      const candidate = path.join(publishRoot, rid, name);
      if (!fs.existsSync(candidate)) continue;
      if (hasCertificateTable(fs.readFileSync(candidate)) === false) return candidate;
    }
  }
  return null;
}

function checkPolicy() {
  const name = "policy: SKIP_WIN_CODESIGN semantics";
  const cases = [
    [{}, false, null],
    [{ SKIP_WIN_CODESIGN: "" }, false, null],
    [{ SKIP_WIN_CODESIGN: "0" }, false, null],
    [{ SKIP_WIN_CODESIGN: "1" }, true, null],
    [{ SKIP_WIN_CODESIGN: "true" }, false, "warning"],
    [{ SKIP_WIN_CODESIGN: " 1" }, false, "warning"],
  ];
  const bad = [];
  for (const [env, skips, warns] of cases) {
    const policy = signingPolicy(env);
    if (policy.skip !== skips) bad.push(`${JSON.stringify(env)} skip=${policy.skip}`);
    if (warns && !policy.warning) bad.push(`${JSON.stringify(env)} gave no warning`);
    if (!warns && policy.warning) bad.push(`${JSON.stringify(env)} warned unexpectedly`);
  }
  if (bad.length) fail(name, bad.join("; "));
  else pass(name, "only the exact value 1 skips; other values enforce and warn when they are not 0 or empty");
}

function checkMissingVariables() {
  const name = "policy: missing Azure variables are named";
  const all = missingSigningVariables({ ...DUMMY_VALUES });
  const withoutSecret = { ...DUMMY_VALUES };
  delete withoutSecret.AZURE_CLIENT_SECRET;
  const blank = { ...DUMMY_VALUES, AZURE_ARTIFACT_SIGNING_PROFILE: "   " };
  const problems = [];
  if (all.length !== 0) problems.push(`complete env reported ${all.join(",")}`);
  if (JSON.stringify(missingSigningVariables(withoutSecret)) !== JSON.stringify(["AZURE_CLIENT_SECRET"])) {
    problems.push("a removed secret was not the only name reported");
  }
  if (JSON.stringify(missingSigningVariables(blank)) !== JSON.stringify(["AZURE_ARTIFACT_SIGNING_PROFILE"])) {
    problems.push("a whitespace-only value was not reported as missing");
  }
  if (SIGNING_REQUIRED_VARIABLES.includes("AZURE_SUBSCRIPTION_ID")) {
    problems.push("AZURE_SUBSCRIPTION_ID must not be required (signing does not use it)");
  }
  if (problems.length) fail(name, problems.join("; "));
  else pass(name, `${SIGNING_REQUIRED_VARIABLES.length} required names, whitespace counts as missing`);
}

function checkTargets(fixtureDir) {
  const name = "targets: build-installer signs exactly the BWC binaries";
  const names = bwcSigningTargets(PUBLISH_FIXTURE);
  const fromFolder = publishSigningTargets(fixtureDir).map((file) => path.basename(file)).sort();
  const problems = [];
  if (JSON.stringify(names) !== JSON.stringify(EXPECTED_TARGETS)) {
    problems.push(`bwcSigningTargets gave ${names.join(", ")}`);
  }
  if (JSON.stringify(fromFolder) !== JSON.stringify(EXPECTED_TARGETS)) {
    problems.push(`publishSigningTargets gave ${fromFolder.join(", ")}`);
  }
  if (problems.length) fail(name, problems.join("; "));
  else pass(name, `${EXPECTED_TARGETS.length} root files; runtime, Microsoft, and subfolder files are left alone`);
}

function checkDefines() {
  const name = "defines: BWC_SIGN is passed only when signing is enforced";
  const enforced = signingDefines({
    enabled: true,
    scriptPath: SIGN_PS1,
    uninstallerCopy: path.join(os.tmpdir(), "uninstaller-signed.exe"),
  }).map(([defineName]) => defineName);
  const skipped = signingDefines({ enabled: false, scriptPath: SIGN_PS1, uninstallerCopy: "x" });
  const problems = [];
  if (JSON.stringify(enforced) !== JSON.stringify(["BWC_SIGN", "BWC_SIGN_SCRIPT", "BWC_SIGN_UNINST_COPY"])) {
    problems.push(`enforced defines were ${enforced.join(",")}`);
  }
  if (skipped.length !== 0) problems.push("skipped signing still passes defines");
  if (problems.length) fail(name, problems.join("; "));
  else pass(name, "BWC_SIGN, BWC_SIGN_SCRIPT, and BWC_SIGN_UNINST_COPY only when enforced");
}

function checkPathGuard() {
  const name = "paths: characters that break the NSIS string are refused";
  const problems = [];
  if (signingPathProblem(SIGN_PS1) !== null) problems.push("the repository path was refused");
  for (const bad of ['C:\\a"b\\x.ps1', "C:\\a'b\\x.ps1", "C:\\a$b\\x.ps1", "C:\\a%b\\x.ps1", "C:\\a`b\\x.ps1", "C:\\a\nb"]) {
    if (signingPathProblem(bad) === null) problems.push(`accepted ${JSON.stringify(bad)}`);
  }
  if (problems.length) fail(name, problems.join("; "));
  else pass(name, "quotes, dollar, percent, apostrophe, backtick, and line breaks are refused");
}

function checkNsisSource(fixtureDir) {
  const name = "nsis: bwc.nsi is consistent with the signing defines";
  const files = PUBLISH_FIXTURE.map((rel) => ({ rel, abs: path.join(fixtureDir, rel), size: 0 }));
  try {
    const problems = auditNsisSource({
      scriptPath: NSIS_SCRIPT,
      installerDir: path.dirname(NSIS_SCRIPT),
      defines: [...NSIS_REQUIRED_DEFINES, ...SIGNING_DEFINE_NAMES],
      payloadText: buildPayloadNsh(files, { rid: "win-x64", flavor: "ship" }),
    });
    if (problems.length) fail(name, problems.join("; "));
    else pass(name, "every ${BWC_SIGN*} use is defined in both signing modes");
  } catch (error) {
    fail(name, error instanceof Error ? error.message : String(error));
  }
}

function checkDryRuns() {
  for (const rid of ["win-x64", "win-arm64"]) {
    const enforce = runNode([BUILD_INSTALLER, "--rid", rid, "--dry-run"], cleanEnv());
    const name = `dry run: ${rid} with signing enforced`;
    if (enforce.status !== 0 || !enforce.output.includes("signing: ")) {
      fail(name, `exit ${enforce.status}; ${enforce.output.slice(-400)}`);
    } else if (!enforce.output.includes("AZURE_CLIENT_ID")) {
      fail(name, "the dry run did not name the missing variables");
    } else {
      pass(name, "exit 0 without NSIS; names the missing variables and the signing plan");
    }
  }
  const skipName = "dry run: win-arm64 with SKIP_WIN_CODESIGN=1";
  const skipped = runNode([BUILD_INSTALLER, "--rid", "win-arm64", "--dry-run"], cleanEnv({ SKIP_WIN_CODESIGN: "1" }));
  if (skipped.status !== 0 || !skipped.output.includes("skipped")) {
    fail(skipName, `exit ${skipped.status}; ${skipped.output.slice(-400)}`);
  } else {
    pass(skipName, "exit 0 and the plan says signing is skipped");
  }
}

function checkSignSkip(fixtureFile) {
  const name = "sign: SKIP_WIN_CODESIGN=1 skips without touching the file";
  const before = fs.readFileSync(fixtureFile);
  const result = runPowerShell(SIGN_PS1, ["-FilePath", fixtureFile], cleanEnv({ SKIP_WIN_CODESIGN: "1" }));
  const after = fs.readFileSync(fixtureFile);
  if (result.status !== 0 || !result.output.includes(SKIP_MESSAGE) || !before.equals(after)) {
    fail(name, `exit ${result.status}; ${result.output.slice(-300)}`);
  } else {
    pass(name, "exit 0, skip message printed, file bytes unchanged");
  }
}

function checkSignMissing(fixtureFile) {
  const name = "sign: unset and 0 both fail naming a missing variable";
  const problems = [];
  for (const value of [undefined, "0"]) {
    const env = cleanEnv(value === undefined ? {} : { SKIP_WIN_CODESIGN: value });
    const result = runPowerShell(SIGN_PS1, ["-FilePath", fixtureFile], env);
    if (result.status === 0 || !result.output.includes("AZURE_CLIENT_ID")) {
      problems.push(`SKIP=${value ?? "(unset)"} exit ${result.status}`);
    }
  }
  if (problems.length) fail(name, problems.join("; "));
  else pass(name, "exit non-zero and the message names AZURE_CLIENT_ID");
}

function checkSignNoTools() {
  const name = "sign: no client tools fails with the setup hint";
  const result = runPowerShell(SIGN_PS1, ["-CheckOnly"], fullAzureEnv());
  if (result.status === 0) {
    skip(name, "the Artifact Signing Client Tools are installed here, so the refusal cannot happen");
  } else if (!result.output.includes(SETUP_HINT)) {
    fail(name, `exit ${result.status}; ${result.output.slice(-300)}`);
  } else {
    pass(name, `exit ${result.status} and the message names ${SETUP_HINT}`);
  }
}

function checkOverrides() {
  const missingTool = path.join(os.tmpdir(), "bwc-selftest-missing", "signtool.exe");
  const missingDlib = path.join(os.tmpdir(), "bwc-selftest-missing", "Azure.CodeSigning.Dlib.dll");
  const signtool = runPowerShell(SIGN_PS1, ["-CheckOnly"], fullAzureEnv({
    AZURE_ARTIFACT_SIGNING_SIGNTOOL_PATH: missingTool,
  }));
  const signtoolName = "sign: SIGNTOOL override that does not exist is refused";
  if (signtool.status === 0 || !signtool.output.includes("Configured SignTool was not found")) {
    fail(signtoolName, `exit ${signtool.status}; ${signtool.output.slice(-300)}`);
  } else {
    pass(signtoolName, "refused with the configured path");
  }
  const dlib = runPowerShell(SIGN_PS1, ["-CheckOnly"], fullAzureEnv({
    AZURE_ARTIFACT_SIGNING_DLIB_PATH: missingDlib,
  }));
  const dlibName = "sign: DLIB override that does not exist is refused";
  if (dlib.status === 0 || !dlib.output.includes("Configured Artifact Signing dlib was not found")) {
    fail(dlibName, `exit ${dlib.status}; ${dlib.output.slice(-300)}`);
  } else {
    pass(dlibName, "refused with the configured path");
  }
  checkValidOverrides();
}

function checkValidOverrides() {
  const name = "sign: existing SIGNTOOL and DLIB overrides are accepted";
  const sdk = findSdkSignTool();
  if (!sdk) {
    skip(name, "no Windows SDK signtool.exe (x64) to use as the override");
    return;
  }
  const dlibDir = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-selftest-dlib-"));
  try {
    const dlib = path.join(dlibDir, "Azure.CodeSigning.Dlib.dll");
    fs.writeFileSync(dlib, "selftest placeholder, never loaded\n");
    const result = runPowerShell(SIGN_PS1, ["-CheckOnly"], fullAzureEnv({
      AZURE_ARTIFACT_SIGNING_SIGNTOOL_PATH: sdk,
      AZURE_ARTIFACT_SIGNING_DLIB_PATH: dlib,
    }));
    if (result.status !== 0 || !result.output.includes(sdk) || !result.output.includes(dlib)) {
      fail(name, `exit ${result.status}; ${result.output.slice(-300)}`);
    } else {
      pass(name, "-CheckOnly exits 0 and prints both paths; nothing is signed");
    }
  } finally {
    fs.rmSync(dlibDir, { recursive: true, force: true });
  }
}

function checkVerify(fixtureFile) {
  const skipName = "verify: SKIP_WIN_CODESIGN=1 skips";
  const skipped = runPowerShell(VERIFY_PS1, ["-Files", fixtureFile], cleanEnv({ SKIP_WIN_CODESIGN: "1" }));
  if (skipped.status !== 0 || !skipped.output.includes(VERIFY_SKIP_MESSAGE)) {
    fail(skipName, `exit ${skipped.status}; ${skipped.output.slice(-300)}`);
  } else {
    pass(skipName, "exit 0 with the skip message");
  }

  const envName = "verify: missing publisher variables are named";
  const noPublisher = runPowerShell(VERIFY_PS1, ["-Files", fixtureFile], cleanEnv());
  if (noPublisher.status === 0 || !noPublisher.output.includes("AZURE_ARTIFACT_SIGNING_PUBLISHER")) {
    fail(envName, `exit ${noPublisher.status}; ${noPublisher.output.slice(-300)}`);
  } else {
    pass(envName, "exit non-zero and the message names AZURE_ARTIFACT_SIGNING_PUBLISHER");
  }

  const emptyName = "verify: an empty file list is refused";
  const empty = runPowerShell(VERIFY_PS1, ["-Files", ""], fullAzureEnv());
  if (empty.status === 0 || empty.output.includes("Verified ")) {
    fail(emptyName, `exit ${empty.status}; ${empty.output.slice(-300)}`);
  } else {
    pass(emptyName, `exit ${empty.status}; nothing was reported as verified`);
  }
}

function checkUnsignedExe(workDir) {
  const name = "verify: an unsigned BWC exe is refused";
  const publishExe = findUnsignedPublishExe();
  const source = publishExe ?? path.join(workDir, "source-unsigned.exe");
  if (!publishExe) fs.writeFileSync(source, minimalUnsignedPe());
  const copy = path.join(workDir, "unsigned-copy", path.basename(source));
  fs.mkdirSync(path.dirname(copy), { recursive: true });
  fs.copyFileSync(source, copy);
  const origin = publishExe ? "a BWC-built file from publish/" : "a minimal unsigned PE (no publish/ folder)";
  const result = runPowerShell(VERIFY_PS1, ["-Files", copy], fullAzureEnv());
  if (result.status === 0 || !result.output.includes("Invalid or missing Authenticode signature")) {
    fail(name, `exit ${result.status}; ${result.output.slice(-300)}`);
  } else {
    pass(name, `exit ${result.status} for ${origin}`);
  }
}

function writeResult(overall) {
  fs.mkdirSync(path.dirname(RESULT_FILE), { recursive: true });
  const record = {
    schema: 1,
    pass: overall,
    platform: process.platform,
    counts: {
      pass: results.filter((r) => r.status === "pass").length,
      fail: results.filter((r) => r.status === "fail").length,
      skip: results.filter((r) => r.status === "skip").length,
    },
    checks: results,
  };
  fs.writeFileSync(RESULT_FILE, `${JSON.stringify(record, null, 2)}\n`, "utf8");
}

function main() {
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-signing-selftest-"));
  try {
    const publishDir = path.join(workDir, "publish");
    fs.mkdirSync(publishDir, { recursive: true });
    for (const name of PUBLISH_FIXTURE) fs.writeFileSync(path.join(publishDir, name), "");
    fs.mkdirSync(path.join(publishDir, "en-us"));
    fs.writeFileSync(path.join(publishDir, "en-us", "Bwc.UI.resources.dll"), "");
    const fixtureFile = path.join(workDir, "sample.exe");
    fs.writeFileSync(fixtureFile, "selftest fixture, not a real program\n");

    const checks = [
      checkPolicy,
      checkMissingVariables,
      () => checkTargets(publishDir),
      checkDefines,
      checkPathGuard,
      () => checkNsisSource(publishDir),
      checkDryRuns,
    ];
    for (const check of checks) {
      try {
        check();
      } catch (error) {
        fail(check.name || "check", error instanceof Error ? error.message : String(error));
      }
    }

    if (process.platform === "win32") {
      const psChecks = [
        () => checkSignSkip(fixtureFile),
        () => checkSignMissing(fixtureFile),
        checkSignNoTools,
        checkOverrides,
        () => checkVerify(fixtureFile),
        () => checkUnsignedExe(workDir),
      ];
      for (const check of psChecks) {
        try {
          check();
        } catch (error) {
          fail("powershell check", error instanceof Error ? error.message : String(error));
        }
      }
    } else {
      skip("powershell checks", "run on Windows only; the policy, target, define, and dry-run checks ran");
    }
  } finally {
    fs.rmSync(workDir, { recursive: true, force: true });
  }

  for (const result of results) {
    console.log(`${result.status.toUpperCase()} ${result.name}: ${result.detail}`);
  }
  const failed = results.filter((result) => result.status === "fail").length;
  const skipped = results.filter((result) => result.status === "skip").length;
  console.log(
    `artifact-signing-selftest: ${results.length - failed - skipped} passed, ${failed} failed, ${skipped} skipped.`,
  );
  writeResult(failed === 0);
  return failed === 0 ? 0 : 1;
}

process.exitCode = main();
