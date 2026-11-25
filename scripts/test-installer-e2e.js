#!/usr/bin/env node
/**
 * Proves the real in-app update with the real NSIS installer (not the stand-in).
 *
 * Builds two unsigned E2E-flavor installers: A at the package.json version, and B at the next patch version. B's
 * payload is published with the higher version into publish/<rid>-e2e-next, so package.json and the tracked version
 * files are never changed. Then runs the Installer tests (tests/Bwc.E2E/Installer) with the paths in the environment.
 * Those tests install A silently, press Download and Install in the Hub, and check the update.
 *
 * A third setup, the "old layout" (R1, docs/failure-modes/installer-e2e.md 107 to 124), stands in for a 0.1.x install: the
 * current tree published as 0.1.99 into publish/<rid>-e2e-old, with every BwcGpedit file left out of the NSIS file list and
 * a copy of installer/bwc.nsi (in artifacts/installer/work, never written back) without the Gpedit shortcut and its
 * AppUserModelID. It exists only in this script and only in the E2E flavor; build-installer.js and the release build never
 * see it. The test updates it to B and requires the three-tool layout. It is not built by --prepare.
 *
 * Usage: node scripts/test-installer-e2e.js --rid win-arm64|win-x64 [--prepare]
 *
 * --prepare builds A and B the same way and stops: it writes artifacts/e2e/installer-inputs.json (paths, versions,
 * sizes, SHA-256, rid) for the Elevated machine-mode tests (docs/elevated-runner.md) and runs no test. Those tests read
 * the file and report NOT RUN when it is missing, stale, or for another rid.
 *
 * Output: artifacts/e2e/installer-update.json. Exit 0 only when it says pass. Failure modes:
 * docs/failure-modes/installer-e2e.md. Needs NSIS (makensis) and must run unelevated.
 */
import { spawnSync } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
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
  REQUIRED_PUBLISH_FILES as BUILD_REQUIRED_PUBLISH_FILES,
  SIGNING_DEFINE_NAMES,
  validatePayload,
  buildPayloadNsh,
} from "./build-installer.js";
import { generateNugetNotices } from "./generate-nuget-licenses.js";
import { checkPublishFlavor, checkSetupManifestLevel, formatFlavorReport } from "./flavor-metadata.js";
import { buildEnvironment } from "./lib/build-environment.js";
import { installUpdateOutputs, removeStaleFileLists, updateStagingDir } from "./lib/update-publish.js";
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
  "src/Bwc.Gpedit.App/Bwc.Gpedit.App.csproj",
  "src/Bwc.Update/Bwc.Update.csproj",
];
const UPDATE_PROJECT = "src/Bwc.Update/Bwc.Update.csproj";
const REQUIRED_PUBLISH_FILES = ["BetterWindowsConfig.dll", ...BUILD_REQUIRED_PUBLISH_FILES];
const NSIS_SCRIPT = path.join(REPO_ROOT, "installer", "bwc.nsi");
// The old layout (R1): a fixed version below every 0.2.x, and the files it leaves out of the payload list.
const OLD_LAYOUT_VERSION = "0.1.99";
const OLD_LAYOUT_DROPPED_FILE = /^BwcGpedit\./i;
const OLD_LAYOUT_KEPT_EXES = ["BetterWindowsConfig.exe", "BwcRegedit.exe", "BwcUpdate.exe"];
// Checks the result must hold and pass, so a run where a required case was skipped or filtered out cannot pass (121, 125, 126).
const REQUIRED_PROOF_CHECKS = [
  "old.a.noGpeditExe",
  "old.after.shortcuts",
  "old.after.gpedit.starts",
  "old.after.userData",
  "upfail.filesRestored",
  "upstale.staleFileGone",
];
const LICENSE_FILE = path.join(REPO_ROOT, "LICENSE");
const ICON_FILE = path.join(REPO_ROOT, "resources", "bwc.ico");
const INSTALLER_DIR = path.join(REPO_ROOT, "artifacts", "installer");
const E2E_DIR = path.join(REPO_ROOT, "artifacts", "e2e");
const RESULT_PATH = path.join(E2E_DIR, "installer-update.json");
const INPUTS_PATH = path.join(E2E_DIR, "installer-inputs.json");
const INPUTS_SCHEMA = 1;
const DOTNET_FALLBACK = "C:\\Program Files\\dotnet\\dotnet.exe";
const NSIS_X86_HOME = path.join(process.env["ProgramFiles(x86)"] ?? "", "NSIS");
const EXIT_USAGE = 2;
const EXIT_NSIS_MISSING = 3;

const usage = `Usage: node ${SCRIPT} --rid win-arm64|win-x64 [--prepare]

Builds unsigned E2E installers A (package.json version) and B (next patch), plus an old-layout installer (0.1.99, no
Local Group Policy Editor, two shortcuts), then runs the Installer E2E tests: an update from the old layout to B
that must end with three tools, an in-app update from A to B through the Hub, a case with BwcRegedit and BwcGpedit open, an uninstall, the refusal to install
over a newer version, and the refusal of the E2E /MACHINE switch without an elevated token. The E2E installer is the
per-user sandbox, so everything here runs unelevated; the per-machine proof is the elevated runner (docs/installer.md).

Options:
  --rid win-x64|win-arm64   Required. The app runtime identifier.
  --prepare                 Build A and B (not the old layout), write artifacts/e2e/installer-inputs.json, run no test. Run this
                            (unelevated) right before the elevated runner: the Elevated installer tests read the file.
  --help                    Show this help.

Output: artifacts/e2e/installer-update.json (--prepare: artifacts/e2e/installer-inputs.json)
Exit codes: 0 pass, 1 fail, 2 usage or environment error, ${EXIT_NSIS_MISSING} NSIS (makensis) missing.
`;

function parseArgs(argv) {
  const options = { rid: undefined, help: false, prepare: false };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--prepare") {
      options.prepare = true;
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

/** Compares two plain X.Y.Z versions: negative, zero, or positive like a - b. */
function compareVersions(a, b) {
  const left = a.split(".").map(Number);
  const right = b.split(".").map(Number);
  for (let index = 0; index < 3; index += 1) {
    if (left[index] !== right[index]) {
      return left[index] - right[index];
    }
  }
  return 0;
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
    // The NativeAOT helper is published away from the shared folder: a publish with -o set to it deletes the runtime
    // files of the Hub that an earlier CoreCLR publish of the helper had written (update-elevation.md HN25).
    const staged = project === UPDATE_PROJECT;
    const target = staged ? updateStagingDir(outDir) : outDir;
    if (staged) {
      rmSync(target, { recursive: true, force: true });
      removeStaleFileLists({
        projectDir: path.dirname(path.join(REPO_ROOT, project)),
        projectFile: path.basename(project),
        platform,
        rid,
      });
    }
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
        target,
        "--nologo",
      ],
      {
        cwd: REPO_ROOT,
        stdio: "inherit",
        // vswhere.exe on PATH, for the NativeAOT link of BwcUpdate (update-elevation.md HN23).
        env: { ...buildEnvironment(process.env), MSBUILDDISABLENODEREUSE: "1", DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER: "1" },
      },
      `publishing ${project}`,
    );
    if (staged) {
      installUpdateOutputs(target, outDir);
      rmSync(target, { recursive: true, force: true });
    }
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
    throw new Error(`the ${path.basename(outDir)} payload reports version ${dllVersion}, expected ${version}`);
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

/**
 * The NSIS source of the old layout: a copy of installer/bwc.nsi without the statements that write the Local Group Policy
 * Editor shortcut and its AppUserModelID, and with BWC_ROOT pointing at the real installer folder (the copy lives elsewhere).
 * Each of the three lines must occur exactly once, or this throws, so a changed bwc.nsi fails the build instead of producing
 * an old setup that quietly keeps three shortcuts (installer-e2e.md 108, 109). The source file is never written.
 */
export function oldLayoutNsisSource(text, installerDir) {
  const shortcut = /^\s*CreateShortCut\b.*\$\{BWC_GPEDIT_SHORTCUT\}/;
  const aumid = /^\s*!insertmacro\s+BWC_SET_AUMID_BODY\b.*\$\{BWC_GPEDIT_AUMID\}/;
  const root = /^\s*!define\s+BWC_ROOT\s+"\$\{__FILEDIR__\}"\s*$/;
  const lines = text.split("\n");
  for (const [name, pattern] of [
    ["the Gpedit shortcut", shortcut],
    ["the Gpedit AppUserModelID", aumid],
    ["the BWC_ROOT define", root],
  ]) {
    const hits = lines.filter((line) => pattern.test(line)).length;
    if (hits !== 1) {
      throw new Error(`the Gpedit statements were not found in bwc.nsi: ${name} matched ${hits} line(s), expected 1`);
    }
  }
  const kept = [];
  for (const line of lines) {
    if (shortcut.test(line) || aumid.test(line)) {
      continue;
    }
    kept.push(root.test(line) ? `!define BWC_ROOT "${installerDir}"${line.endsWith("\r") ? "\r" : ""}` : line);
  }
  return kept.join("\n");
}

/**
 * The payload list of the old layout: every file but the BwcGpedit files. Throws when nothing was dropped, when a Gpedit
 * exe is left, or when the filter took one of the other apps (installer-e2e.md 107).
 */
export function oldLayoutFiles(files) {
  const kept = files.filter((file) => !OLD_LAYOUT_DROPPED_FILE.test(file.rel));
  if (kept.length === files.length) {
    throw new Error("the old layout drops no file: the publish folder has no BwcGpedit.* file");
  }
  if (kept.some((file) => file.rel.toLowerCase() === "bwcgpedit.exe")) {
    throw new Error("the old layout still lists BwcGpedit.exe");
  }
  for (const name of OLD_LAYOUT_KEPT_EXES) {
    if (!kept.some((file) => file.rel === name)) {
      throw new Error(`the old layout lost ${name}`);
    }
  }
  return kept;
}

/**
 * Builds an E2E setup with the same NSIS source and defines as build-installer.js, from a payload folder, unsigned. The old
 * layout (oldLayout true) drops the BwcGpedit files and compiles the patched copy of bwc.nsi; B takes neither path.
 */
function buildInstallerB(rid, version, publishDir, makensis, { oldLayout = false } = {}) {
  const label = oldLayout ? "the old layout" : "B";
  const base = `BetterWindowsConfig-${version}-${rid}-e2e${oldLayout ? "-old-layout" : ""}`;
  const setup = path.join(INSTALLER_DIR, `${base}-setup.exe`);
  const work = path.join(INSTALLER_DIR, "work", base);
  rmSync(setup, { force: true });
  mkdirSync(work, { recursive: true });

  const noticesPath = path.join(work, "THIRD-PARTY-NOTICES.txt");
  generateNugetNotices({ rid, outPath: noticesPath });
  const allFiles = listPublishFiles(publishDir);
  const files = oldLayout ? oldLayoutFiles(allFiles) : allFiles;
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
    ["BWC_UPDATER_SRC", path.join(publishDir, "BwcUpdate.exe")],
    ["BWC_LICENSE", LICENSE_FILE],
    ["BWC_NOTICES", noticesPath],
    ["BWC_ICON", ICON_FILE],
    ["BWC_ESTIMATED_KB", String(estimatedKb)],
    ["BWC_E2E", true],
  ];

  let scriptPath = NSIS_SCRIPT;
  if (oldLayout) {
    scriptPath = path.join(work, "bwc-old-layout.nsi");
    writeFileSync(scriptPath, oldLayoutNsisSource(readFileSync(NSIS_SCRIPT, "utf8"), path.dirname(NSIS_SCRIPT)), "utf8");
  }

  const problems = auditNsisSource({
    scriptPath,
    installerDir: path.dirname(NSIS_SCRIPT),
    defines: [...defines.map(([name]) => name), ...SIGNING_DEFINE_NAMES],
    payloadText,
  });
  if (problems.length > 0) {
    throw new Error(`the NSIS source is not internally consistent: ${problems.join("; ")}`);
  }

  console.log(`installer-e2e: building installer ${label} (${version}, ${rid}, e2e) with ${path.basename(makensis.path)}`);
  const result = spawnSync(makensis.path, makensisArgs({ defines, scriptPath }), {
    cwd: REPO_ROOT,
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
    windowsHide: true,
  });
  if (result.error || result.status !== 0 || !existsSync(setup)) {
    const output = `${result.stdout ?? ""}\n${result.stderr ?? ""}`.split(/\r?\n/).slice(-40).join("\n");
    throw new Error(`makensis failed for installer ${label}: ${result.error?.message ?? `exit ${result.status}`}\n${output}`);
  }
  // B and the old layout are E2E setups like A, so they must never ask for elevation (the same audit build-installer.js runs).
  const manifest = checkSetupManifestLevel(readFileSync(setup), "e2e");
  if (!manifest.ok) {
    rmSync(setup, { force: true });
    throw new Error(`installer ${label}: ${manifest.problem}`);
  }
  return setup;
}

/** Runs the Installer tests. The result file is the verdict, so a missing or stale file is a failure. */
function runInstallerTests({ rid, publishA, setupA, setupB, setupOld, versionA, versionB, versionOld, runId }) {
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
    BWC_INSTALLER_OLD: setupOld,
    BWC_INSTALLER_VERSION_OLD: versionOld,
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

/** Size and SHA-256 of one setup, for the inputs file. */
function describeSetup(file, version) {
  const bytes = readFileSync(file);
  return {
    path: file,
    version,
    size: bytes.length,
    sha256: createHash("sha256").update(bytes).digest("hex"),
  };
}

/**
 * Writes artifacts/e2e/installer-inputs.json for the Elevated tests. It is written last, after both builds passed, and
 * the old file is removed before the build starts, so a failed prepare never leaves inputs that look current.
 */
function writeInputs({ rid, setupA, setupB, versionA, versionB }) {
  const document = {
    schema: INPUTS_SCHEMA,
    rid,
    createdUtc: new Date().toISOString(),
    installerA: describeSetup(setupA, versionA),
    installerB: describeSetup(setupB, versionB),
  };
  mkdirSync(E2E_DIR, { recursive: true });
  writeFileSync(INPUTS_PATH, `${JSON.stringify(document, null, 2)}
`, "utf8");
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
  const versionOld = OLD_LAYOUT_VERSION;
  if (compareVersions(versionOld, versionA) >= 0) {
    console.error(`installer-e2e: the old layout version ${versionOld} must be below package.json ${versionA}.`);
    return EXIT_USAGE;
  }
  const publishA = path.join(REPO_ROOT, "publish", `${rid}-e2e`);
  const publishNext = path.join(REPO_ROOT, "publish", `${rid}-e2e-next`);
  const publishOld = path.join(REPO_ROOT, "publish", `${rid}-e2e-old`);
  const runId = `${stamp(new Date())}-${randomBytes(4).toString("hex")}`;
  console.log(`installer-e2e: rid=${rid} A=${versionA} B=${versionB} runId=${runId}`);

  if (options.prepare) {
    rmSync(INPUTS_PATH, { force: true });
  }

  let setupA;
  let setupB;
  let setupOld;
  try {
    buildInstallerA(rid);
    setupA = path.join(INSTALLER_DIR, `BetterWindowsConfig-${versionA}-${rid}-e2e-setup.exe`);
    if (!existsSync(setupA)) {
      throw new Error(`installer A is missing at ${path.relative(REPO_ROOT, setupA)}`);
    }
    publishB(rid, versionB, publishNext);
    setupB = buildInstallerB(rid, versionB, publishNext, makensis);
    if (!options.prepare) {
      publishB(rid, versionOld, publishOld);
      setupOld = buildInstallerB(rid, versionOld, publishOld, makensis, { oldLayout: true });
    }
  } catch (error) {
    console.error(`installer-e2e: FAIL while building: ${error.message}`);
    return 1;
  }

  if (options.prepare) {
    const written = writeInputs({ rid, setupA, setupB, versionA, versionB });
    console.log(`installer-e2e: prepared ${rid} A=${versionA} B=${versionB}`);
    console.log(`installer-e2e:   A ${written.installerA.size} bytes sha256 ${written.installerA.sha256}`);
    console.log(`installer-e2e:   B ${written.installerB.size} bytes sha256 ${written.installerB.sha256}`);
    console.log(`installer-e2e: inputs ${path.relative(REPO_ROOT, INPUTS_PATH)}`);
    console.log("installer-e2e: run the elevated runner next (docs/elevated-runner.md). A later build makes these inputs stale.");
    return 0;
  }

  mkdirSync(E2E_DIR, { recursive: true });
  const testExit = runInstallerTests({ rid, publishA, setupA, setupB, setupOld, versionA, versionB, versionOld, runId });
  const verdict = readVerdict();
  if (verdict === null) {
    console.error(`installer-e2e: FAIL: dotnet test exited ${testExit} and wrote no verdict.`);
    return 1;
  }

  const checks = Array.isArray(verdict.checks) ? verdict.checks : [];
  const failed = checks.filter((check) => check.pass !== true);
  const missingProof = REQUIRED_PROOF_CHECKS.filter((id) => !checks.some((check) => check.id === id && check.pass === true));
  const pass =
    testExit === 0 && verdict.pass === true && checks.length > 0 && failed.length === 0 && missingProof.length === 0;
  console.log("");
  console.log(`installer-e2e: ${pass ? "PASS" : "FAIL"} ${rid} ${versionA} -> ${versionB}, old layout ${versionOld} -> ${versionB}`);
  for (const id of missingProof) {
    console.log(`  MISSING proof check ${id}: a required installer proof case did not record it as passed`);
  }
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
