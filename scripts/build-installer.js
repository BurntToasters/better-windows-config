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

/** Sorts directories deepest first, so a child is removed before its parent. */
function deepestFirst(dirs) {
  return [...dirs].sort((a, b) => {
    const depth = b.split("/").length - a.split("/").length;
    if (depth !== 0) return depth;
    return a < b ? -1 : a > b ? 1 : 0;
  });
}

/**
 * Builds the NSIS include. The file list is fixed at build time, so the staging copy, the swap,
 * the rollback, and the uninstaller all use the same list.
 *
 * BWC_PAYLOAD_FILES ROOT   copies each file into ROOT (the staging folder) and lists it in the manifest.
 * BWC_PAYLOAD_MOVE_IN      moves each staged file into the install folder. A failed move jumps to
 *                          BwcCommit_rollback (defined in installer/bwc.nsi).
 * BWC_PAYLOAD_DELETE       deletes each payload file from the install folder.
 * BWC_PAYLOAD_RMDIRS       removes each payload folder that is empty, deepest first.
 */
export function buildPayloadNsh(files, { rid, flavor }) {
  const lines = [];
  const totalBytes = files.reduce((sum, file) => sum + file.size, 0);
  const dirs = directoriesOf(files);
  lines.push("; Generated by scripts/build-installer.js. Do not edit.");
  lines.push(`; ${rid} ${flavor}: ${files.length} file(s), ${totalBytes} byte(s).`);
  lines.push("");
  lines.push("!macro BWC_PAYLOAD_FILES ROOT");
  let currentDir = null;
  for (const file of files) {
    const dir = file.rel.includes("/") ? file.rel.slice(0, file.rel.lastIndexOf("/")) : "";
    if (dir !== currentDir) {
      const relWindows = dir ? `\\${dir.replace(/\//g, "\\")}` : "";
      lines.push(`  SetOutPath "$\{ROOT}${nsisText(relWindows)}"`);
      currentDir = dir;
    }
    const windowsRel = file.rel.replace(/\//g, "\\");
    lines.push(`  FileWrite $R9 "${nsisText(windowsRel)}$\\r$\\n"`);
    lines.push(`  File "${nsisText(file.abs)}"`);
  }
  lines.push("!macroend");
  lines.push("");
  lines.push("!macro BWC_PAYLOAD_MOVE_IN");
  for (const dir of [...dirs].sort()) {
    lines.push(`  CreateDirectory "${installedPath(dir)}"`);
  }
  for (const file of files) {
    // "${BWC_STAGE_SUFFIX}" stays literal here: NSIS expands it when it compiles the include.
    const staged = "$INSTDIR${BWC_STAGE_SUFFIX}\\" + nsisText(file.rel.replace(/\//g, "\\"));
    lines.push(`  Delete "${installedPath(file.rel)}"`);
    lines.push("  ClearErrors");
    lines.push(`  Rename "${staged}" "${installedPath(file.rel)}"`);
    lines.push("  IfErrors BwcCommit_rollback 0");
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
  for (const dir of deepestFirst(dirs)) {
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

/**
 * Files the installer cannot work without. BwcUpdate.exe is the one the installer runs for the close
 * step, so a publish without it cannot produce a working upgrade.
 */
export const REQUIRED_PUBLISH_FILES = Object.freeze(["BetterWindowsConfig.exe", "BwcRegedit.exe", "BwcUpdate.exe"]);

/**
 * Checks installer/bwc.nsi and the files it includes, without NSIS. Reports: an include that does
 * not exist, a ${BWC_...} define that nothing defines, an !insertmacro BWC_... with no !macro, a
 * Call to a function that is not defined, and a label the payload jumps to that bwc.nsi lacks.
 * Returns a list of problem strings (empty when consistent).
 */
export function auditNsisSource({ scriptPath, installerDir, defines, payloadText }) {
  const problems = [];
  const sources = [{ name: relativeToRepo(scriptPath), text: fs.readFileSync(scriptPath, "utf8") }];
  const includeRegex = /^\s*!include\s+"([^"]+)"/gm;
  for (const match of sources[0].text.matchAll(includeRegex)) {
    const raw = match[1];
    // NSIS ships MUI2.nsh, LogicLib.nsh, and FileFunc.nsh with makensis. Only this repo's includes
    // are checked here. The generated payload is checked as text below.
    if (raw.includes("${BWC_PAYLOAD_NSH}")) continue;
    if (!raw.includes("${BWC_ROOT}")) continue;
    const resolved = raw.replace("${BWC_ROOT}", installerDir);
    if (resolved.includes("${")) {
      problems.push(`${sources[0].name}: include path uses an unknown define: ${raw}`);
      continue;
    }
    if (!fs.existsSync(resolved)) {
      problems.push(`${sources[0].name}: included file is missing: ${raw}`);
      continue;
    }
    sources.push({ name: relativeToRepo(resolved), text: fs.readFileSync(resolved, "utf8") });
  }
  sources.push({ name: "<generated payload>", text: payloadText });

  const defined = new Set(defines);
  const macros = new Set();
  const functions = new Set();
  for (const source of sources) {
    for (const match of source.text.matchAll(/^\s*!define\s+(\w+)/gm)) defined.add(match[1]);
    for (const match of source.text.matchAll(/^\s*!macro\s+(\w+)/gm)) macros.add(match[1]);
    for (const match of source.text.matchAll(/^\s*Function\s+((?:un\.)?\w+)/gm)) functions.add(match[1]);
  }
  for (const source of sources) {
    const used = new Set([...source.text.matchAll(/\$\{(BWC_\w+)\}/g)].map((match) => match[1]));
    for (const name of used) {
      if (!defined.has(name)) problems.push(`${source.name}: \${${name}} is used but never defined`);
    }
    for (const match of source.text.matchAll(/!insertmacro\s+(BWC_\w+)/g)) {
      if (!macros.has(match[1])) problems.push(`${source.name}: !insertmacro ${match[1]} has no !macro`);
    }
    for (const match of source.text.matchAll(/^\s*Call\s+((?:un\.)?\w+)/gm)) {
      if (!functions.has(match[1])) problems.push(`${source.name}: Call ${match[1]} has no Function`);
    }
  }
  const scriptText = sources[0].text;
  if (!/^\s*Function\s+\.onInit/m.test(scriptText)) {
    problems.push(`${sources[0].name}: no .onInit function`);
  }
  if (!/^\s*BwcCommit_rollback:/m.test(scriptText)) {
    problems.push("the generated payload jumps to BwcCommit_rollback, which installer/bwc.nsi does not define");
  }
  return problems;
}

function buildDefines({ version, rid, flavor, outputPath, payloadPath, noticesPath, estimatedKb, updaterPath }) {
  const parsed = parsePackageVersion(version);
  const defines = [
    ["BWC_VERSION", version],
    ["BWC_FILE_VERSION", parsed.fileVersion],
    ["BWC_RID", rid],
    ["BWC_ARCH", RID_ARCH.get(rid)],
    ["BWC_OUTPUT", outputPath],
    ["BWC_PAYLOAD_NSH", payloadPath],
    ["BWC_SCRIPTS_DIR", SCRIPTS_DIR],
    ["BWC_UPDATER_SRC", updaterPath],
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
    updaterPath: path.join(publishDir, "BwcUpdate.exe"),
  });
  const args = makensisArgs({ defines, scriptPath: NSIS_SCRIPT });
  const command = [makensis.path ?? MAKENSIS, ...args].map(quoteForDisplay).join(" ");

  // The source check needs no NSIS. A failure here is printed and exits 1, before any command line.
  const problems = auditNsisSource({
    scriptPath: NSIS_SCRIPT,
    installerDir: path.dirname(NSIS_SCRIPT),
    defines: defines.map(([name]) => name),
    // With a publish folder, the real file list is checked. Without one, one placeholder file is
    // enough to exercise every macro the payload defines.
    payloadText: buildPayloadNsh(
      publishPresent
        ? listPublishFiles(publishDir)
        : [{ rel: "BetterWindowsConfig.exe", abs: path.join(publishDir, "BetterWindowsConfig.exe"), size: 0 }],
      { rid: options.rid, flavor: options.flavor },
    ),
  });
  if (problems.length > 0) {
    for (const problem of problems) console.error(`build-installer: ${problem}`);
    console.error("build-installer: FAIL: the NSIS source is not internally consistent (dry run)");
    return 1;
  }

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
  return 0;
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
    return printDryRun({ options, names, publishDir, makensis, nsisVersion, payloadPath, noticesPath });
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
    const missingFiles = REQUIRED_PUBLISH_FILES.filter((name) => !files.some((file) => file.rel === name));
    if (missingFiles.length > 0) {
      throw new Error(`the publish folder is missing ${missingFiles.join(", ")}; run scripts/dotnet-build.js again`);
    }
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
      updaterPath: path.join(publishDir, "BwcUpdate.exe"),
    });
    const problems = auditNsisSource({
      scriptPath: NSIS_SCRIPT,
      installerDir: path.dirname(NSIS_SCRIPT),
      defines: defines.map(([name]) => name),
      payloadText: buildPayloadNsh(files, { rid, flavor }),
    });
    if (problems.length > 0) {
      throw new Error(`the NSIS source is not internally consistent: ${problems.join("; ")}`);
    }
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
