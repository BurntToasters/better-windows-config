#!/usr/bin/env node
/**
 * Builds the Better Windows Config setup EXE from publish/<rid> with NSIS (makensis).
 *
 * Steps: check for NSIS (exit 3 when missing, before any slow work), reuse or create publish/<rid>,
 * write THIRD-PARTY-NOTICES.txt for the shipped NuGet packages, generate the payload include for
 * installer/bwc.nsi, run makensis, and write artifacts/installer/<name>-setup.json with the size and
 * SHA-256 of the EXE.
 *
 * --dry-run prints the exact makensis command line and exits 0 without NSIS, a publish, or any write.
 */
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isDirectExecutionOf } from "./direct-execution.mjs";
import { publishFolderName } from "./flavor-metadata.js";
import { generateNugetNotices } from "./generate-nuget-licenses.js";
import { parsePackageVersion } from "./sync-version-helpers.js";

const SCRIPT = "scripts/build-installer.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const RID_ARCH = new Map([
  ["win-x64", "x64"],
  ["win-arm64", "arm64"],
]);
const FLAVORS = new Set(["ship", "e2e"]);
const NSIS_SCRIPT = path.join(REPO_ROOT, "installer", "bwc.nsi");
const SCRIPTS_DIR = path.join(REPO_ROOT, "installer", "scripts");
const LICENSE_FILE = path.join(REPO_ROOT, "LICENSE");
const ICON_FILE = path.join(REPO_ROOT, "resources", "bwc.ico");
const INSTALLER_DIR = path.join(REPO_ROOT, "artifacts", "installer");
const BUILD_ARTIFACT_DIR = path.join(REPO_ROOT, "artifacts", "build");
const MIN_NSIS = { major: 3, minor: 10 };
const NSIS_INSTALL_HINT = "Install NSIS 3.10+ from https://nsis.sourceforge.io and rerun.";
const EXIT_NSIS_MISSING = 3;
const MAKENSIS = "makensis.exe";
// The relative path inside the install folder is kept short enough that a typical per-user
// profile still stays under the classic 260-character MAX_PATH.
const MAX_RELATIVE_PATH = 140;
const RESERVED_ROOT_NAMES = new Set([
  "license",
  "third-party-notices.txt",
  "installed-files.txt",
  "uninstall.exe",
]);
const RESERVED_DIRECTORY = "bwc-tools";

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} --rid win-x64|win-arm64 [--flavor ship|e2e] [--version <semver>] [--dry-run]

Builds artifacts/installer/BetterWindowsConfig-<version>-<rid>-setup.exe (e2e: -<rid>-e2e-setup.exe)
from publish/<rid> (e2e: publish/<rid>-e2e). Publishes first when no good publish exists.

Options:
  --rid <rid>        Required. win-x64 or win-arm64.
  --flavor <flavor>  ship (default) or e2e. The e2e installer requires /RUNID and /SHORTCUTS.
  --version <semver> Default: package.json version. Accepts X.Y.Z or X.Y.Z-beta.N.
  --dry-run          Print the exact makensis command line and exit 0. NSIS is not needed.
  --help             Show this help.

Exit codes: 0 built (or dry run), 1 build or compile failure, 2 bad arguments,
${EXIT_NSIS_MISSING} NSIS not found or older than 3.10.

NSIS is searched in this order: NSIS_HOME, PATH, %ProgramFiles(x86)%\\NSIS, %ProgramFiles%\\NSIS.`);
}

export function parseArgs(argv, packageVersion) {
  const args = argv.slice(2);
  const options = {
    rid: null,
    flavor: "ship",
    version: packageVersion,
    dryRun: false,
    help: false,
  };
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--dry-run") {
      options.dryRun = true;
      continue;
    }
    const match = arg.match(/^--(rid|flavor|version)(?:=(.*))?$/);
    if (!match) {
      throw new Error(`unrecognized argument: ${arg}`);
    }
    const [, key, inline] = match;
    let value = inline;
    if (value === undefined) {
      if (index + 1 >= args.length) {
        throw new Error(`--${key} requires a value`);
      }
      value = args[index + 1];
      index += 1;
    }
    if (!value) {
      throw new Error(`--${key} requires a value`);
    }
    options[key] = value;
  }
  if (options.help) {
    return options;
  }
  if (!RID_ARCH.has(options.rid)) {
    throw new Error(
      `--rid must be one of ${[...RID_ARCH.keys()].join(", ")}; received ${options.rid ?? "(none)"}`,
    );
  }
  if (!FLAVORS.has(options.flavor)) {
    throw new Error(`--flavor must be one of ${[...FLAVORS].join(", ")}; received ${options.flavor}`);
  }
  return options;
}

/**
 * Finds makensis.exe. Returns { path, searched }, where path is null when no candidate exists.
 * The directory lookups are injected so the order can be checked without touching the machine.
 */
export function findMakensis({ env = process.env, exists = fs.existsSync } = {}) {
  const candidates = [];
  if (env.NSIS_HOME) {
    candidates.push(path.join(env.NSIS_HOME, MAKENSIS));
  }
  for (const entry of (env.PATH ?? env.Path ?? "").split(path.delimiter)) {
    // Windows PATH entries are sometimes quoted, for example "C:\Program Files\NSIS".
    const dir = entry.trim().replace(/^"(.*)"$/, "$1");
    if (dir) {
      candidates.push(path.join(dir, MAKENSIS));
    }
  }
  if (env["ProgramFiles(x86)"]) {
    candidates.push(path.join(env["ProgramFiles(x86)"], "NSIS", MAKENSIS));
  }
  if (env.ProgramFiles) {
    candidates.push(path.join(env.ProgramFiles, "NSIS", MAKENSIS));
  }
  const searched = [...new Set(candidates)];
  const found = searched.find((candidate) => exists(candidate)) ?? null;
  return { path: found, searched };
}

/** Parses the version from `makensis /VERSION`, for example "v3.10". Returns null when unreadable. */
export function readNsisVersion(makensisPath) {
  const result = spawnSync(makensisPath, ["/VERSION"], {
    encoding: "utf8",
    windowsHide: true,
  });
  if (result.error) return null;
  const text = `${result.stdout ?? ""}\n${result.stderr ?? ""}`;
  const match = text.match(/(\d+)\.(\d+)/);
  if (!match) return null;
  return { text: `${match[1]}.${match[2]}`, major: Number(match[1]), minor: Number(match[2]) };
}

export function isNsisTooOld(version) {
  if (!version) return false;
  if (version.major !== MIN_NSIS.major) return version.major < MIN_NSIS.major;
  return version.minor < MIN_NSIS.minor;
}

function sha256File(filePath) {
  return createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

/** Lists every file under dir, sorted by forward-slash relative path. Symlinks are refused. */
export function listPublishFiles(dir) {
  const files = [];
  function walk(current, relative) {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const absolute = path.join(current, entry.name);
      const rel = relative ? `${relative}/${entry.name}` : entry.name;
      if (entry.isSymbolicLink()) {
        throw new Error(`${rel} is a symbolic link; the installer payload must contain regular files only`);
      }
      if (entry.isDirectory()) {
        walk(absolute, rel);
      } else if (entry.isFile()) {
        files.push({ rel, abs: absolute, size: fs.statSync(absolute).size });
      }
    }
  }
  walk(dir, "");
  return files.sort((a, b) => (a.rel < b.rel ? -1 : a.rel > b.rel ? 1 : 0));
}

/** Refuses names that would collide with installer-owned files or break the NSIS source. */
export function validatePayload(files) {
  for (const file of files) {
    if (/["$*?\r\n]/.test(file.rel) || file.rel.includes("\\")) {
      throw new Error(`payload file name cannot be written into NSIS safely: ${file.rel}`);
    }
    if (file.rel.length > MAX_RELATIVE_PATH) {
      throw new Error(
        `payload path is ${file.rel.length} characters, over the ${MAX_RELATIVE_PATH} budget: ${file.rel}`,
      );
    }
    const lower = file.rel.toLowerCase();
    if (!lower.includes("/") && RESERVED_ROOT_NAMES.has(lower)) {
      throw new Error(`payload file ${file.rel} collides with an installer-owned file`);
    }
    if (lower === RESERVED_DIRECTORY || lower.startsWith(`${RESERVED_DIRECTORY}/`)) {
      throw new Error(`payload file ${file.rel} is inside the reserved folder ${RESERVED_DIRECTORY}`);
    }
  }
}

function nsisText(value) {
  return value.replace(/\$/g, "$$$$");
}

/** An NSIS path under $INSTDIR. Only the relative part is escaped; $INSTDIR must expand. */
function installedPath(rel) {
  return `$INSTDIR\\${nsisText(rel.replace(/\//g, "\\"))}`;
}

function directoriesOf(files) {
  const dirs = new Set();
  for (const file of files) {
    const parts = file.rel.split("/");
    for (let index = 1; index < parts.length; index += 1) {
      dirs.add(parts.slice(0, index).join("/"));
    }
  }
  return [...dirs];
}

/**
 * Builds the NSIS include. The file list is fixed at build time, so the installer, the upgrade
 * path, and the uninstaller all use the same list.
 */
export function buildPayloadNsh(files, { rid, flavor }) {
  const lines = [];
  const totalBytes = files.reduce((sum, file) => sum + file.size, 0);
  lines.push("; Generated by scripts/build-installer.js. Do not edit.");
  lines.push(`; ${rid} ${flavor}: ${files.length} file(s), ${totalBytes} byte(s).`);
  lines.push("");
  lines.push("!macro BWC_PAYLOAD_FILES");
  let currentDir = null;
  for (const file of files) {
    const dir = file.rel.includes("/") ? file.rel.slice(0, file.rel.lastIndexOf("/")) : "";
    if (dir !== currentDir) {
      lines.push(`  SetOutPath "${dir ? installedPath(dir) : "$INSTDIR"}"`);
      currentDir = dir;
    }
    const windowsRel = file.rel.replace(/\//g, "\\");
    lines.push(`  FileWrite $R9 "${nsisText(windowsRel)}$\\r$\\n"`);
    lines.push(`  File "${nsisText(file.abs)}"`);
  }
  lines.push("!macroend");
  lines.push("");
  lines.push("!macro BWC_PAYLOAD_DELETE");
  for (const file of files) {
    lines.push(`  Delete "${installedPath(file.rel)}"`);
  }
  lines.push("!macroend");
  lines.push("");
  lines.push("!macro BWC_PAYLOAD_RMDIRS");
  const dirs = directoriesOf(files).sort((a, b) => {
    const depth = b.split("/").length - a.split("/").length;
    if (depth !== 0) return depth;
    return a < b ? -1 : a > b ? 1 : 0;
  });
  for (const dir of dirs) {
    lines.push(`  RMDir "${installedPath(dir)}"`);
  }
  lines.push("!macroend");
  lines.push("");
  return lines.join("\n");
}

function quoteForDisplay(arg) {
  return /\s/.test(arg) ? `"${arg}"` : arg;
}

/** Exact makensis arguments. Each value is one argv element, so no shell quoting is involved. */
export function makensisArgs({ defines, scriptPath }) {
  return ["/V2", ...defines.map(([name, value]) => (value === true ? `/D${name}` : `/D${name}=${value}`)), scriptPath];
}

function buildDefines({ version, rid, flavor, outputPath, payloadPath, noticesPath, estimatedKb }) {
  const parsed = parsePackageVersion(version);
  const defines = [
    ["BWC_VERSION", version],
    ["BWC_FILE_VERSION", parsed.fileVersion],
    ["BWC_RID", rid],
    ["BWC_ARCH", RID_ARCH.get(rid)],
    ["BWC_OUTPUT", outputPath],
    ["BWC_PAYLOAD_NSH", payloadPath],
    ["BWC_SCRIPTS_DIR", SCRIPTS_DIR],
    ["BWC_LICENSE", LICENSE_FILE],
    ["BWC_NOTICES", noticesPath],
    ["BWC_ICON", ICON_FILE],
    ["BWC_ESTIMATED_KB", String(estimatedKb)],
  ];
  if (flavor === "e2e") {
    defines.push(["BWC_E2E", true]);
  }
  return defines;
}

function outputNames(version, rid, flavor) {
  const base = flavor === "e2e" ? `BetterWindowsConfig-${version}-${rid}-e2e` : `BetterWindowsConfig-${version}-${rid}`;
  return {
    base,
    setup: path.join(INSTALLER_DIR, `${base}-setup.exe`),
    record: path.join(INSTALLER_DIR, `${base}-setup.json`),
    work: path.join(INSTALLER_DIR, "work", base),
  };
}

function relativeToRepo(absolute) {
  const rel = path.relative(REPO_ROOT, absolute).split(path.sep).join("/");
  return rel && !rel.startsWith("..") ? rel : absolute;
}

/** True when artifacts/build says the publish for this flavor passed and the main EXE exists. */
export function publishIsGood({ rid, flavor, publishDir, buildArtifactDir = BUILD_ARTIFACT_DIR }) {
  const artifactPath = path.join(buildArtifactDir, `${publishFolderName(rid, flavor)}.json`);
  if (!fs.existsSync(artifactPath) || !fs.existsSync(path.join(publishDir, "BetterWindowsConfig.exe"))) {
    return false;
  }
  try {
    const record = JSON.parse(fs.readFileSync(artifactPath, "utf8"));
    return record.pass === true && record.flavor === flavor && record.rid === rid;
  } catch {
    return false;
  }
}

function ensurePublish({ rid, flavor, publishDir }) {
  if (publishIsGood({ rid, flavor, publishDir })) {
    console.log(`build-installer: reusing ${relativeToRepo(publishDir)} (last publish passed)`);
    return true;
  }
  console.log(`build-installer: publishing ${relativeToRepo(publishDir)} with dotnet-build`);
  const result = spawnSync(
    process.execPath,
    [path.join(REPO_ROOT, "scripts", "dotnet-build.js"), "--rid", rid, "--flavor", flavor],
    { cwd: REPO_ROOT, stdio: "inherit", windowsHide: true },
  );
  return result.status === 0;
}

function tailLines(text, limit) {
  return String(text || "")
    .split(/\r?\n/)
    .filter((line) => line.trim() !== "")
    .slice(-limit);
}

function printDryRun({ options, names, publishDir, makensis, nsisVersion, payloadPath, noticesPath }) {
  const publishPresent = fs.existsSync(path.join(publishDir, "BetterWindowsConfig.exe"));
  let estimatedKb = 0;
  if (publishPresent) {
    const files = listPublishFiles(publishDir);
    const bytes = files.reduce((sum, file) => sum + file.size, 0)
      + fs.statSync(LICENSE_FILE).size;
    estimatedKb = Math.ceil(bytes / 1024);
  }
  const defines = buildDefines({
    version: options.version,
    rid: options.rid,
    flavor: options.flavor,
    outputPath: names.setup,
    payloadPath,
    noticesPath,
    estimatedKb,
  });
  const args = makensisArgs({ defines, scriptPath: NSIS_SCRIPT });
  const command = [makensis.path ?? MAKENSIS, ...args].map(quoteForDisplay).join(" ");

  console.log(`build-installer: dry run for ${options.rid} (${options.flavor}), version ${options.version}`);
  console.log(`  publish folder: ${relativeToRepo(publishDir)} (${publishPresent ? "present" : "missing; a real run publishes it"})`);
  console.log(
    `  makensis: ${makensis.path ?? `not found (a real run exits ${EXIT_NSIS_MISSING})`}${nsisVersion ? ` version ${nsisVersion.text}` : ""}`,
  );
  console.log(`  output: ${relativeToRepo(names.setup)}`);
  if (!publishPresent) {
    console.log("  estimated size: 0 KiB placeholder (computed from the publish folder on a real run)");
  }
  console.log("makensis command:");
  console.log(command);
}

function main(argv = process.argv) {
  let packageVersion = "0.0.0";
  try {
    packageVersion = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, "package.json"), "utf8")).version;
  } catch (error) {
    console.error(`build-installer: cannot read package.json: ${error instanceof Error ? error.message : String(error)}`);
    return 1;
  }

  let options;
  try {
    options = parseArgs(argv, packageVersion);
    if (!options.help) {
      parsePackageVersion(options.version);
    }
  } catch (error) {
    console.error(`build-installer: ${error instanceof Error ? error.message : String(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return 2;
  }
  if (options.help) {
    printHelp();
    return 0;
  }

  const { rid, flavor, version } = options;
  const publishName = publishFolderName(rid, flavor);
  const publishDir = path.join(REPO_ROOT, "publish", publishName);
  const names = outputNames(version, rid, flavor);
  const payloadPath = path.join(names.work, "payload.nsh");
  const noticesPath = path.join(names.work, "THIRD-PARTY-NOTICES.txt");

  for (const required of [NSIS_SCRIPT, LICENSE_FILE, ICON_FILE]) {
    if (!fs.existsSync(required)) {
      console.error(`build-installer: required input is missing: ${relativeToRepo(required)}`);
      return 1;
    }
  }

  const makensis = findMakensis();
  if (options.dryRun) {
    const nsisVersion = makensis.path ? readNsisVersion(makensis.path) : null;
    printDryRun({ options, names, publishDir, makensis, nsisVersion, payloadPath, noticesPath });
    return 0;
  }

  if (!makensis.path) {
    console.error(`build-installer: ${MAKENSIS} was not found.`);
    console.error("Searched:");
    for (const candidate of makensis.searched) {
      console.error(`  ${candidate}`);
    }
    console.error(NSIS_INSTALL_HINT);
    console.error("To use an NSIS folder that is not on PATH, set NSIS_HOME to the folder that contains makensis.exe.");
    return EXIT_NSIS_MISSING;
  }
  const nsisVersion = readNsisVersion(makensis.path);
  if (!nsisVersion) {
    console.warn(`build-installer: could not read the NSIS version from ${makensis.path}; continuing.`);
  } else if (isNsisTooOld(nsisVersion)) {
    console.error(
      `build-installer: NSIS ${nsisVersion.text} at ${makensis.path} is older than 3.10. ${NSIS_INSTALL_HINT}`,
    );
    return EXIT_NSIS_MISSING;
  }

  const startedMs = Date.now();
  const startedUtc = new Date(startedMs).toISOString();
  const writeRecord = (record) => {
    fs.mkdirSync(INSTALLER_DIR, { recursive: true });
    fs.writeFileSync(names.record, `${JSON.stringify(record, null, 2)}\n`, "utf8");
  };
  const failRecord = (message) => ({
    schema: 1,
    product: "Better Windows Config",
    version,
    rid,
    flavor,
    pass: false,
    error: message,
    startedUtc,
    durationMs: Date.now() - startedMs,
  });

  // Remove the previous result first, so a failure below never leaves an old setup that looks new.
  fs.rmSync(names.setup, { force: true });
  fs.rmSync(names.record, { force: true });

  if (!ensurePublish({ rid, flavor, publishDir })) {
    const message = `dotnet-build failed for ${publishName}`;
    console.error(`build-installer: FAIL: ${message}`);
    return 1;
  }

  try {
    fs.mkdirSync(names.work, { recursive: true });
    generateNugetNotices({ rid, outPath: noticesPath });
    const files = listPublishFiles(publishDir);
    validatePayload(files);
    const payloadBytes = files.reduce((sum, file) => sum + file.size, 0);
    const noticesBytes = fs.statSync(noticesPath).size;
    const licenseBytes = fs.statSync(LICENSE_FILE).size;
    const estimatedKb = Math.ceil((payloadBytes + noticesBytes + licenseBytes) / 1024);
    fs.writeFileSync(payloadPath, buildPayloadNsh(files, { rid, flavor }), "utf8");

    const defines = buildDefines({
      version,
      rid,
      flavor,
      outputPath: names.setup,
      payloadPath,
      noticesPath,
      estimatedKb,
    });
    const args = makensisArgs({ defines, scriptPath: NSIS_SCRIPT });
    console.log(`build-installer: running ${relativeToRepo(makensis.path)} on installer/bwc.nsi`);
    const result = spawnSync(makensis.path, args, {
      cwd: REPO_ROOT,
      encoding: "utf8",
      maxBuffer: 64 * 1024 * 1024,
      windowsHide: true,
    });
    const output = `${result.stdout ?? ""}\n${result.stderr ?? ""}`;
    const warnings = output.split(/\r?\n/).filter((line) => /warning/i.test(line));
    for (const line of warnings.slice(0, 20)) {
      console.warn(`  ${line.trim()}`);
    }
    if (result.error || result.status !== 0 || !fs.existsSync(names.setup)) {
      const detail = result.error ? result.error.message : `makensis exited with ${result.status}`;
      for (const line of tailLines(output, 40)) {
        console.error(`  ${line}`);
      }
      writeRecord(failRecord(detail));
      console.error(`build-installer: FAIL: ${detail}`);
      return 1;
    }

    const stat = fs.statSync(names.setup);
    const record = {
      schema: 1,
      product: "Better Windows Config",
      version,
      fileVersion: parsePackageVersion(version).fileVersion,
      rid,
      arch: RID_ARCH.get(rid),
      flavor,
      pass: true,
      file: relativeToRepo(names.setup),
      size: stat.size,
      sha256: sha256File(names.setup),
      signed: false,
      publishDir: relativeToRepo(publishDir),
      payload: { files: files.length, bytes: payloadBytes },
      notices: {
        file: relativeToRepo(noticesPath),
        sha256: sha256File(noticesPath),
      },
      nsis: { path: makensis.path, version: nsisVersion?.text ?? null },
      startedUtc,
      durationMs: Date.now() - startedMs,
    };
    writeRecord(record);
    console.log(`build-installer: PASS ${relativeToRepo(names.setup)} (${stat.size} bytes, sha256 ${record.sha256})`);
    console.log(`build-installer: record ${relativeToRepo(names.record)}`);
    return 0;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    writeRecord(failRecord(message));
    console.error(`build-installer: FAIL: ${message}`);
    return 1;
  }
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = main();
}
