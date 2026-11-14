#!/usr/bin/env node
/**
 * Proves the real in-app update with the real NSIS installer (not the stand-in).
 *
 * Builds two unsigned E2E-flavor installers: A at the package.json version, and B at the next patch version. B's
 * payload is published with the higher version into publish/<rid>-e2e-next, so package.json and the tracked version
 * files are never changed. Then runs the Installer tests (tests/Bwc.E2E/Installer) with the paths in the environment.
 * Those tests install A silently, press Download and Install in the Hub, and check the update.
 *
 * Usage: node scripts/test-installer-e2e.js --rid win-arm64|win-x64
 *
 * Output: artifacts/e2e/installer-update.json. Exit 0 only when that file says pass. Failure modes:
 * docs/failure-modes/installer-e2e.md. Needs NSIS (makensis) and must run unelevated.
 */
import { spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isDirectExecutionOf } from "./direct-execution.mjs";
import { elevationState } from "./elevated-runner/elevation.mjs";
import {
  auditNsisSource,
  findMakensis,
  isNsisTooOld,
  listPublishFiles,
  makensisArgs,
  readNsisVersion,
  SIGNING_DEFINE_NAMES,
  validatePayload,
  buildPayloadNsh,
} from "./build-installer.js";
import { generateNugetNotices } from "./generate-nuget-licenses.js";
import { checkPublishFlavor, formatFlavorReport } from "./flavor-metadata.js";
import { parsePackageVersion } from "./sync-version-helpers.js";

const SCRIPT = "scripts/test-installer-e2e.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const RID_PLATFORMS = new Map([
  ["win-x64", "x64"],
  ["win-arm64", "ARM64"],
]);
const RID_ARCH = new Map([
  ["win-x64", "x64"],
  ["win-arm64", "arm64"],
]);
const PUBLISH_PROJECTS = [
  "src/Bwc.Hub/Bwc.Hub.csproj",
  "src/Bwc.Regedit.App/Bwc.Regedit.App.csproj",
  "src/Bwc.Update/Bwc.Update.csproj",
];
const REQUIRED_PUBLISH_FILES = ["BetterWindowsConfig.dll", "BetterWindowsConfig.exe", "BwcUpdate.exe"];
const NSIS_SCRIPT = path.join(REPO_ROOT, "installer", "bwc.nsi");
const SCRIPTS_DIR = path.join(REPO_ROOT, "installer", "scripts");
const LICENSE_FILE = path.join(REPO_ROOT, "LICENSE");
const ICON_FILE = path.join(REPO_ROOT, "resources", "bwc.ico");
const INSTALLER_DIR = path.join(REPO_ROOT, "artifacts", "installer");
const E2E_DIR = path.join(REPO_ROOT, "artifacts", "e2e");
const RESULT_PATH = path.join(E2E_DIR, "installer-update.json");
const DOTNET_FALLBACK = "C:\\Program Files\\dotnet\\dotnet.exe";
const NSIS_X86_HOME = path.join(process.env["ProgramFiles(x86)"] ?? "", "NSIS");
const EXIT_USAGE = 2;
const EXIT_NSIS_MISSING = 3;

const usage = `Usage: node ${SCRIPT} --rid win-arm64|win-x64

Builds unsigned E2E installers A (package.json version) and B (next patch), then runs the Installer E2E tests:
an in-app update from A to B through the Hub, a case with BwcRegedit open, and an uninstall.

Options:
  --rid win-x64|win-arm64   Required. The app runtime identifier.
  --help                    Show this help.

Output: artifacts/e2e/installer-update.json
Exit codes: 0 pass, 1 fail, 2 usage or environment error, ${EXIT_NSIS_MISSING} NSIS (makensis) missing.
`;

function parseArgs(argv) {
  const options = { rid: undefined, help: false };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--rid") {
      const value = argv[index + 1];
      if (value === undefined || value.startsWith("--")) {
        throw new Error("--rid requires win-x64 or win-arm64.");
      }
      options.rid = value;
      index += 1;
      continue;
    }
    throw new Error(`Unknown argument '${arg}'.`);
  }
  return options;
}

/** The next patch version, for example 0.1.0 -> 0.1.1. Prerelease versions are refused. */
export function nextPatchVersion(version) {
  parsePackageVersion(version);
  if (version.includes("-")) {
    throw new Error(`The installer E2E needs a plain X.Y.Z version; package.json has ${version}.`);
  }
  const [major, minor, patch] = version.split(".").map(Number);
  return `${major}.${minor}.${patch + 1}`;
}

function stamp(date) {
  return date.toISOString().replace(/[-:]/g, "").replace(/\.\d{3}Z$/, "Z");
}

function readPackageVersion() {
  return JSON.parse(readFileSync(path.join(REPO_ROOT, "package.json"), "utf8")).version;
}

function dotnetCommand() {
  if (existsSync(DOTNET_FALLBACK)) {
    const probe = spawnSync("dotnet", ["--version"], { encoding: "utf8", windowsHide: true });
    if (probe.error && probe.error.code === "ENOENT") {
      return DOTNET_FALLBACK;
    }
  }
  return "dotnet";
}

/** Finds makensis on PATH, through NSIS_HOME, or in the 32-bit Program Files folder where the NSIS installer puts it. */
function locateMakensis() {
  let found = findMakensis();
  if (!found.path && existsSync(path.join(NSIS_X86_HOME, "makensis.exe"))) {
    process.env.NSIS_HOME = NSIS_X86_HOME;
    found = findMakensis();
  }
  return found;
}

function runOrFail(command, args, options, label) {
  const result = spawnSync(command, args, { windowsHide: true, ...options });
  if (result.error || result.status !== 0) {
    const detail = result.error ? result.error.message : `exit code ${result.status}`;
    throw new Error(`${label} failed: ${detail}`);
  }
  return result;
}

/** Builds A with the repository's own installer script (publish reused or made by dotnet-build.js). */
function buildInstallerA(rid) {
  console.log(`installer-e2e: building installer A (${rid}, e2e) with scripts/build-installer.js`);
  runOrFail(
    process.execPath,
    [path.join(REPO_ROOT, "scripts", "build-installer.js"), "--rid", rid, "--flavor", "e2e"],
    { cwd: REPO_ROOT, stdio: "inherit", env: { ...process.env, SKIP_WIN_CODESIGN: "1" } },
    "building installer A",
  );
}

/**
 * Publishes B's payload with the next version, into publish/<rid>-e2e-next. The same projects and flags as
 * dotnet-build.js, with -p:Version and -p:FileVersion on the command line, so no tracked file changes.
 */
function publishB(rid, version, outDir) {
  const platform = RID_PLATFORMS.get(rid);
  const fileVersion = parsePackageVersion(version).fileVersion;
  rmSync(outDir, { recursive: true, force: true });
  const dotnet = dotnetCommand();
  for (const project of PUBLISH_PROJECTS) {
    console.log(`installer-e2e: publishing ${project} as ${version} into ${path.relative(REPO_ROOT, outDir)}`);
    runOrFail(
      dotnet,
      [
        "publish",
        path.join(REPO_ROOT, project),
        "-c",
        "Release",
        "-r",
        rid,
        `-p:Platform=${platform}`,
        "-p:PublishReadyToRun=true",
        "-p:BwcFlavor=E2E",
        `-p:Version=${version}`,
        `-p:FileVersion=${fileVersion}`,
        "-o",
        outDir,
        "--nologo",
      ],
      {
        cwd: REPO_ROOT,
        stdio: "inherit",
        env: { ...process.env, MSBUILDDISABLENODEREUSE: "1", DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER: "1" },
      },
      `publishing ${project}`,
    );
  }

  const flavor = checkPublishFlavor(outDir, "e2e");
  for (const line of formatFlavorReport(flavor)) {
    console.log(line);
  }
  if (!flavor.ok) {
    throw new Error(`flavor check failed for ${path.basename(outDir)}: ${flavor.problems[0]}`);
  }
  for (const name of REQUIRED_PUBLISH_FILES) {
    if (!existsSync(path.join(outDir, name))) {
      throw new Error(`the publish folder is missing ${name}`);
    }
  }
  const dllVersion = productVersionOf(path.join(outDir, "BetterWindowsConfig.dll"));
  if (dllVersion !== version) {
    throw new Error(`the B payload reports version ${dllVersion}, expected ${version}`);
  }
}

/** The ProductVersion of a file, read with PowerShell (no native reader in Node). */
function productVersionOf(filePath) {
  const script = `(Get-Item -LiteralPath '${filePath.replace(/'/g, "''")}').VersionInfo.ProductVersion`;
  const result = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
    encoding: "utf8",
    windowsHide: true,
  });
  const text = String(result.stdout ?? "").trim();
  return text.split("+")[0].trim();
}

/** Builds B with the same NSIS source and defines as build-installer.js, from the B payload folder, unsigned. */
function buildInstallerB(rid, version, publishDir, makensis) {
  const base = `BetterWindowsConfig-${version}-${rid}-e2e`;
  const setup = path.join(INSTALLER_DIR, `${base}-setup.exe`);
  const work = path.join(INSTALLER_DIR, "work", base);
  rmSync(setup, { force: true });
  mkdirSync(work, { recursive: true });

  const noticesPath = path.join(work, "THIRD-PARTY-NOTICES.txt");
  generateNugetNotices({ rid, outPath: noticesPath });
  const files = listPublishFiles(publishDir);
  validatePayload(files);
  const payloadText = buildPayloadNsh(files, { rid, flavor: "e2e" });
  const payloadPath = path.join(work, "payload.nsh");
  writeFileSync(payloadPath, payloadText, "utf8");

  const payloadBytes = files.reduce((sum, file) => sum + file.size, 0);
  const estimatedKb = Math.ceil((payloadBytes + statSync(noticesPath).size + statSync(LICENSE_FILE).size) / 1024);
  const defines = [
    ["BWC_VERSION", version],
    ["BWC_FILE_VERSION", parsePackageVersion(version).fileVersion],
    ["BWC_RID", rid],
    ["BWC_ARCH", RID_ARCH.get(rid)],
    ["BWC_OUTPUT", setup],
    ["BWC_PAYLOAD_NSH", payloadPath],
    ["BWC_SCRIPTS_DIR", SCRIPTS_DIR],
    ["BWC_UPDATER_SRC", path.join(publishDir, "BwcUpdate.exe")],
    ["BWC_LICENSE", LICENSE_FILE],
    ["BWC_NOTICES", noticesPath],
    ["BWC_ICON", ICON_FILE],
    ["BWC_ESTIMATED_KB", String(estimatedKb)],
    ["BWC_E2E", true],
  ];

  const problems = auditNsisSource({
    scriptPath: NSIS_SCRIPT,
    installerDir: path.dirname(NSIS_SCRIPT),
    defines: [...defines.map(([name]) => name), ...SIGNING_DEFINE_NAMES],
    payloadText,
  });
  if (problems.length > 0) {
    throw new Error(`the NSIS source is not internally consistent: ${problems.join("; ")}`);
  }

  console.log(`installer-e2e: building installer B (${version}, ${rid}, e2e) with ${path.basename(makensis.path)}`);
  const result = spawnSync(makensis.path, makensisArgs({ defines, scriptPath: NSIS_SCRIPT }), {
    cwd: REPO_ROOT,
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
    windowsHide: true,
  });
  if (result.error || result.status !== 0 || !existsSync(setup)) {
    const output = `${result.stdout ?? ""}\n${result.stderr ?? ""}`.split(/\r?\n/).slice(-40).join("\n");
    throw new Error(`makensis failed for installer B: ${result.error?.message ?? `exit ${result.status}`}\n${output}`);
  }
  return setup;
}

/** Runs the Installer tests. The result file is the verdict, so a missing or stale file is a failure. */
function runInstallerTests({ rid, publishA, setupA, setupB, versionA, versionB, runId }) {
  rmSync(RESULT_PATH, { force: true });
  const dotnet = dotnetCommand();
  const args = [
    "test",
    "tests/Bwc.E2E/Bwc.E2E.csproj",
    "-c",
    "Release",
    `-p:Platform=${process.arch === "arm64" ? "ARM64" : "x64"}`,
    "-p:BwcFlavor=E2E",
    "--filter",
    "TestCategory=Installer",
    "--nologo",
  ];
  const env = {
    ...process.env,
    BWC_PUBLISH_DIR: publishA,
    BWC_RID: rid,
    BWC_FLAVOR: "e2e",
    BWC_RUN_ID: runId,
    BWC_INSTALLER_TEST: "1",
    BWC_INSTALLER_A: setupA,
    BWC_INSTALLER_B: setupB,
    BWC_INSTALLER_VERSION_A: versionA,
    BWC_INSTALLER_VERSION_B: versionB,
    BWC_INSTALLER_RESULT: RESULT_PATH,
  };
  console.log(`installer-e2e: ${dotnet} ${args.join(" ")}`);
  const result = spawnSync(dotnet, args, { cwd: REPO_ROOT, env, stdio: "inherit", windowsHide: true });
  return result.status ?? 1;
}

/** Reads the result file and returns its verdict and the failed checks, or null when it is missing or unreadable. */
function readVerdict() {
  let document;
  try {
    document = JSON.parse(readFileSync(RESULT_PATH, "utf8"));
  } catch (error) {
    console.error(`installer-e2e: no readable result at ${path.relative(REPO_ROOT, RESULT_PATH)}: ${error.message}`);
    return null;
  }
  if (document.schema !== 1) {
    console.error(`installer-e2e: result schema is ${JSON.stringify(document.schema)}, expected 1.`);
    return null;
  }
  return document;
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(error.message);
    console.error(usage);
    return EXIT_USAGE;
  }
  if (options.help) {
    console.log(usage);
    return 0;
  }
  const rid = options.rid;
  if (!RID_PLATFORMS.has(rid)) {
    console.error(`--rid must be win-x64 or win-arm64. Received ${rid ?? "(none)"}.`);
    console.error(usage);
    return EXIT_USAGE;
  }
  if (rid === "win-arm64" && process.arch !== "arm64") {
    console.error("An ARM64 app cannot run on this x64 host. Use --rid win-x64.");
    return EXIT_USAGE;
  }

  const elevation = elevationState();
  if (elevation.known && elevation.elevated) {
    console.error("installer-e2e: run this from an unelevated shell. The Hub must run asInvoker, never elevated.");
    return EXIT_USAGE;
  }

  const makensis = locateMakensis();
  if (!makensis.path) {
    console.error(`installer-e2e: makensis.exe was not found. Install NSIS 3.10 or newer: choco install nsis -y`);
    return EXIT_NSIS_MISSING;
  }
  const nsisVersion = readNsisVersion(makensis.path);
  if (nsisVersion && isNsisTooOld(nsisVersion)) {
    console.error(`installer-e2e: NSIS ${nsisVersion.text} is older than 3.10 at ${makensis.path}.`);
    return EXIT_NSIS_MISSING;
  }

  const versionA = readPackageVersion();
  const versionB = nextPatchVersion(versionA);
  const publishA = path.join(REPO_ROOT, "publish", `${rid}-e2e`);
  const publishNext = path.join(REPO_ROOT, "publish", `${rid}-e2e-next`);
  const runId = `${stamp(new Date())}-${randomBytes(4).toString("hex")}`;
  console.log(`installer-e2e: rid=${rid} A=${versionA} B=${versionB} runId=${runId}`);

  let setupA;
  let setupB;
  try {
    buildInstallerA(rid);
    setupA = path.join(INSTALLER_DIR, `BetterWindowsConfig-${versionA}-${rid}-e2e-setup.exe`);
    if (!existsSync(setupA)) {
      throw new Error(`installer A is missing at ${path.relative(REPO_ROOT, setupA)}`);
    }
    publishB(rid, versionB, publishNext);
    setupB = buildInstallerB(rid, versionB, publishNext, makensis);
  } catch (error) {
    console.error(`installer-e2e: FAIL while building: ${error.message}`);
    return 1;
  }

  mkdirSync(E2E_DIR, { recursive: true });
  const testExit = runInstallerTests({ rid, publishA, setupA, setupB, versionA, versionB, runId });
  const verdict = readVerdict();
  if (verdict === null) {
    console.error(`installer-e2e: FAIL: dotnet test exited ${testExit} and wrote no verdict.`);
    return 1;
  }

  const checks = Array.isArray(verdict.checks) ? verdict.checks : [];
  const failed = checks.filter((check) => check.pass !== true);
  const pass = testExit === 0 && verdict.pass === true && checks.length > 0 && failed.length === 0;
  console.log("");
  console.log(`installer-e2e: ${pass ? "PASS" : "FAIL"} ${rid} ${versionA} -> ${versionB}`);
  console.log(`installer-e2e: ${checks.length - failed.length} of ${checks.length} checks passed`);
  for (const check of failed) {
    console.log(`  FAILED ${check.id}: ${check.notes}`);
  }
  console.log(`installer-e2e: result ${path.relative(REPO_ROOT, RESULT_PATH)}`);
  return pass ? 0 : 1;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = main();
}
