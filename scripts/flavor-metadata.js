/**
 * Verify the BwcFlavor assembly metadata in a publish folder.
 *
 * Directory.Build.props records each assembly's flavor as
 * [assembly: AssemblyMetadata("BwcFlavor", "<value>")]. The compiler stores that as a
 * custom attribute blob whose strings are length-prefixed UTF-8 (SerString), so the
 * byte sequences searched here identify the attribute without a metadata reader.
 * Native apphost executables carry no managed metadata and are reported as skipped.
 *
 * BwcUpdate.exe is a NativeAOT executable (update-elevation.md HN13, HN14): it has no managed assembly beside it. Its
 * flavor is proved by a marker string the exe uses (BwcUpdateFlavor=<ship|E2E>), and that it is NativeAOT is proved by
 * the PE file (see nativeAotProblems).
 *
 * Each exe head's embedded application manifest (RT_MANIFEST) must declare the expected
 * requestedExecutionLevel: asInvoker in the e2e folder, and the level in the project's source
 * manifest in the ship folder. The manifest XML is stored as ASCII or UTF-16LE, so both are searched.
 *
 * Run as a script, it checks one publish folder:
 *   node scripts/flavor-metadata.js --rid win-arm64 --flavor e2e
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isDirectExecutionOf } from "./direct-execution.mjs";

const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const ATTRIBUTE_NAME = "BwcFlavor";
// Attribute value written for each build flavor (see Directory.Build.props).
const FLAVOR_VALUES = new Map([
  ["ship", "ship"],
  ["e2e", "E2E"],
]);
const PROJECT_PREFIXES = ["Bwc", "BetterWindowsConfig"];
const ASSEMBLY_EXTENSIONS = [".dll", ".exe"];
// Main assembly of each managed project that dotnet-build publishes (Bwc.Hub, Bwc.Regedit.App). A folder without them
// is incomplete, whatever the other files say. Bwc.Update has none: BwcUpdate.exe is NativeAOT (see UPDATE_EXE).
const REQUIRED_ASSEMBLIES = [
  "BetterWindowsConfig.dll",
  "BwcRegedit.dll",
];
// The self-contained runtime of the Hub and Registry Editor. A folder without one of these cannot start
// BetterWindowsConfig.exe (the system ".NET Desktop Runtime required" dialog), so a publish that lost one is refused
// (update-elevation.md HN25, HN26).
export const RUNTIME_FILES = Object.freeze([
  "hostfxr.dll",
  "hostpolicy.dll",
  "coreclr.dll",
  "System.Private.CoreLib.dll",
  "BetterWindowsConfig.runtimeconfig.json",
]);

/** The RUNTIME_FILES that the given file names lack, compared without regard to case, in RUNTIME_FILES order. */
export function missingRuntimeFiles(fileNames) {
  const present = new Set(fileNames.map((name) => name.toLowerCase()));
  return RUNTIME_FILES.filter((name) => !present.has(name.toLowerCase()));
}

// The NativeAOT helper. Beside it there must be no managed assembly, deps file, or runtime config of the same name.
export const UPDATE_EXE = "BwcUpdate.exe";
export const UPDATE_STALE_FILES = Object.freeze(["BwcUpdate.dll", "BwcUpdate.deps.json", "BwcUpdate.runtimeconfig.json"]);
// A string the exe uses (the unexpected-error log line), so the trimmer keeps it. src/Bwc.Update/Cli/BuildFlavor.cs.
export const UPDATE_FLAVOR_MARKER = "BwcUpdateFlavor=";
// Strings of the .NET host and of CoreCLR's loaders. A NativeAOT exe has none of them: it starts no host, loads no
// CoreCLR, reads no profiler, host trace, or startup hook variable, and has no standalone GC loader.
export const UPDATE_FORBIDDEN_STRINGS = Object.freeze([
  "hostfxr",
  "hostpolicy",
  "coreclr",
  "DOTNET_ROOT",
  "CORECLR_ENABLE_PROFILING",
  "COREHOST_TRACE",
  "DOTNET_STARTUP_HOOKS",
  "GC_Initialize",
  "GC_VersionInfo",
]);
// The data symbol every NativeAOT image exports for debuggers (an apphost or a managed exe never has it).
export const NATIVE_AOT_EXPORT = "DotNetRuntimeDebugHeader";
// The metadata root signature "BSJB" is present in every managed assembly, including
// ReadyToRun images. Native apphosts do not contain it.
const METADATA_SIGNATURE = Buffer.from("BSJB", "ascii");
// Exe heads and the source manifest each one is built from. Bwc.Hub and Bwc.Update, like
// Bwc.Regedit.App, pick app.e2e.manifest through a BwcFlavor condition in their csproj.
const EXE_HEADS = [
  { name: "BwcRegedit.exe", sourceManifest: "src/Bwc.Regedit.App/app.manifest" },
  { name: "BetterWindowsConfig.exe", sourceManifest: "src/Bwc.Hub/app.manifest" },
  { name: "BwcUpdate.exe", sourceManifest: "src/Bwc.Update/app.manifest" },
];

const SEAM_PREFIX = "BWC_E2E_UPDATE_";
// The Hub's own E2E seams: the legacy per-user cleanup redirect (hub.md, LC21). A ship Hub must not hold them.
export const HUB_SEAM_PREFIX = "BWC_E2E_LEGACY_";
const SHIP_HUB_SEAM_FILES = ["BetterWindowsConfig.dll", "BetterWindowsConfig.exe"];
const E2E_EXECUTION_LEVEL = "asInvoker";
const REQUESTED_LEVEL_NAME = "requestedExecutionLevel";
const LEVEL_ATTRIBUTE = /\blevel\s*=\s*["']([A-Za-z]+)["']/;

export const FLAVORS = [...FLAVOR_VALUES.keys()];

/** True when the bytes contain an E2E seam prefix as UTF-8 or as UTF-16LE (a .NET string literal is UTF-16). */
export function containsSeamName(bytes, prefix = SEAM_PREFIX) {
  return bytes.includes(Buffer.from(prefix, "utf8")) || bytes.includes(Buffer.from(prefix, "utf16le"));
}

// A ship Hub must not contain the E2E legacy-cleanup seam names: with them a process could point the cleanup at another
// key.
function checkHubSeams(dir, flavor, report) {
  if (flavor !== "ship") {
    return;
  }
  for (const name of SHIP_HUB_SEAM_FILES) {
    let bytes;
    try {
      bytes = fs.readFileSync(path.join(dir, name));
    } catch {
      continue; // A missing file is reported by the required-assembly check.
    }
    if (containsSeamName(bytes, HUB_SEAM_PREFIX)) {
      report.problems.push(`${name}: a ship build contains "${HUB_SEAM_PREFIX}" (an E2E seam is compiled in)`);
    }
  }
}

/** True when the bytes contain the text as UTF-8 or as UTF-16LE (a .NET string literal is UTF-16). */
export function containsText(bytes, text) {
  return bytes.includes(Buffer.from(text, "utf8")) || bytes.includes(Buffer.from(text, "utf16le"));
}

/**
 * The exported symbol names of a PE image, or [] when the bytes are not a PE file or have no export table. Reads the
 * section table to turn relative addresses into file offsets, and never reads outside the buffer.
 */
export function readExportNames(bytes) {
  try {
    if (bytes.length < 0x40 || bytes.readUInt16LE(0) !== 0x5a4d) {
      return [];
    }
    const pe = bytes.readUInt32LE(0x3c);
    if (bytes.readUInt32LE(pe) !== 0x00004550) {
      return [];
    }
    const sectionCount = bytes.readUInt16LE(pe + 6);
    const optionalSize = bytes.readUInt16LE(pe + 20);
    const magic = bytes.readUInt16LE(pe + 24);
    const directories = pe + 24 + (magic === 0x20b ? 112 : 96);
    const exportRva = bytes.readUInt32LE(directories);
    if (exportRva === 0) {
      return [];
    }
    const sections = [];
    let at = pe + 24 + optionalSize;
    for (let index = 0; index < sectionCount; index += 1) {
      sections.push({
        size: bytes.readUInt32LE(at + 8),
        rva: bytes.readUInt32LE(at + 12),
        raw: bytes.readUInt32LE(at + 20),
      });
      at += 40;
    }
    const toOffset = (rva) => {
      for (const section of sections) {
        if (rva >= section.rva && rva < section.rva + Math.max(section.size, 1)) {
          return rva - section.rva + section.raw;
        }
      }
      return -1;
    };
    const table = toOffset(exportRva);
    if (table < 0) {
      return [];
    }
    const nameCount = bytes.readUInt32LE(table + 24);
    const namesAt = toOffset(bytes.readUInt32LE(table + 32));
    if (namesAt < 0 || nameCount > 100000) {
      return [];
    }
    const names = [];
    for (let index = 0; index < nameCount; index += 1) {
      const start = toOffset(bytes.readUInt32LE(namesAt + index * 4));
      if (start < 0) {
        continue;
      }
      let end = start;
      while (end < bytes.length && bytes[end] !== 0) {
        end += 1;
      }
      names.push(bytes.toString("latin1", start, end));
    }
    return names;
  } catch {
    return [];
  }
}

/** The flavor values the exe names after UPDATE_FLAVOR_MARKER, as UTF-8 or UTF-16LE, each once. */
export function readUpdateFlavorMarkers(bytes) {
  const values = new Set();
  for (const encoding of ["utf8", "utf16le"]) {
    const needle = Buffer.from(UPDATE_FLAVOR_MARKER, encoding);
    const unit = encoding === "utf8" ? 1 : 2;
    let at = bytes.indexOf(needle);
    while (at !== -1) {
      let end = at + needle.length;
      while (end + unit <= bytes.length) {
        const code = unit === 1 ? bytes[end] : bytes.readUInt16LE(end);
        const word = (code >= 0x30 && code <= 0x39) || (code >= 0x41 && code <= 0x5a) || (code >= 0x61 && code <= 0x7a);
        if (!word) {
          break;
        }
        end += unit;
      }
      values.add(bytes.toString(encoding, at + needle.length, end));
      at = bytes.indexOf(needle, at + 1);
    }
  }
  return [...values].sort();
}

/**
 * Why the exe is not proven to be the NativeAOT BwcUpdate.exe. [] means it is. A CoreCLR apphost cannot pass, because
 * it needs a managed assembly next to it and loads hostfxr and CoreCLR (the profiler, host trace, and startup hook
 * variables are read there, before Main: update-elevation.md HE5). siblingNames are the file names in the exe's folder.
 */
export function nativeAotProblems(bytes, siblingNames) {
  const problems = [];
  const present = new Set(siblingNames.map((name) => name.toLowerCase()));
  for (const stale of UPDATE_STALE_FILES) {
    if (present.has(stale.toLowerCase())) {
      problems.push(`${stale} is beside ${UPDATE_EXE}: a managed helper (CoreCLR), not NativeAOT`);
    }
  }
  if (!readExportNames(bytes).includes(NATIVE_AOT_EXPORT)) {
    problems.push(`${UPDATE_EXE} does not export ${NATIVE_AOT_EXPORT}: it is not a NativeAOT image`);
  }
  for (const text of UPDATE_FORBIDDEN_STRINGS) {
    if (containsText(bytes, text)) {
      problems.push(`${UPDATE_EXE} contains "${text}": it can load the .NET host or CoreCLR`);
    }
  }
  return problems;
}

/** The flavor of the exe from its marker. Returns { values, problem }: problem is null when it is exactly expected. */
export function updateFlavorProblem(bytes, expected) {
  const values = readUpdateFlavorMarkers(bytes);
  if (values.length === 0) {
    return { values, problem: `${UPDATE_EXE}: no ${UPDATE_FLAVOR_MARKER} marker, expected ${expected}` };
  }
  if (values.length > 1) {
    return { values, problem: `${UPDATE_EXE}: mixed ${UPDATE_FLAVOR_MARKER} values ${values.join(", ")}, expected only ${expected}` };
  }
  if (values[0] !== expected) {
    return { values, problem: `${UPDATE_EXE}: ${UPDATE_FLAVOR_MARKER}${values[0]}, expected ${expected} for this folder` };
  }
  return { values, problem: null };
}

// BwcUpdate checks on a publish folder (update-elevation.md HB3, HE1, HE5, HN13 to HN15). The exe must be NativeAOT
// with the folder's flavor marker, and a ship exe must not contain the E2E seam names.
function checkUpdateHelper(dir, flavor, expected, siblingNames, report) {
  let bytes;
  try {
    bytes = fs.readFileSync(path.join(dir, UPDATE_EXE));
  } catch {
    return; // A missing exe is reported by the exe manifest check.
  }
  // The exe is the only file with the helper's code (NativeAOT), so it is the only file a seam name can hide in.
  if (flavor === "ship" && containsSeamName(bytes)) {
    report.problems.push(`${UPDATE_EXE}: a ship build contains "${SEAM_PREFIX}" (an E2E seam is compiled in)`);
  }
  const aot = nativeAotProblems(bytes, siblingNames);
  const marker = updateFlavorProblem(bytes, expected);
  report.native.push({ name: UPDATE_EXE, aot: aot.length === 0, values: marker.values, ok: aot.length === 0 && marker.problem === null });
  report.problems.push(...aot);
  if (marker.problem !== null) {
    report.problems.push(marker.problem);
  }
}
export function publishFolderName(rid, flavor) {
  return flavor === "e2e" ? `${rid}-e2e` : rid;
}

function serString(text) {
  const bytes = Buffer.from(text, "utf8");
  if (bytes.length > 0x7f) {
    throw new RangeError(`SerString longer than 127 bytes: ${text}`);
  }
  return Buffer.concat([Buffer.from([bytes.length]), bytes]);
}

const NAME_PREFIX = serString(ATTRIBUTE_NAME);

// Attribute blob bytes for one value, for example 0x09 "BwcFlavor" 0x04 "ship".
function attributePattern(value) {
  return Buffer.concat([NAME_PREFIX, serString(value)]);
}

// Every value written after the attribute name, known or not.
function readAttributeValues(bytes) {
  const values = [];
  let at = bytes.indexOf(NAME_PREFIX);
  while (at !== -1) {
    const lengthAt = at + NAME_PREFIX.length;
    const length = lengthAt < bytes.length ? bytes[lengthAt] : -1;
    const end = lengthAt + 1 + length;
    if (length >= 0 && length < 0x80 && end <= bytes.length) {
      values.push(bytes.toString("utf8", lengthAt + 1, end));
    } else {
      values.push("(unreadable value)");
    }
    at = bytes.indexOf(NAME_PREFIX, at + 1);
  }
  return values;
}

export function inspectAssembly(bytes) {
  if (bytes.indexOf(METADATA_SIGNATURE) === -1) {
    return { managed: false, found: [], unknown: [] };
  }
  const found = [...FLAVOR_VALUES.values()].filter(
    (value) => bytes.indexOf(attributePattern(value)) !== -1,
  );
  const known = new Set(FLAVOR_VALUES.values());
  const unknown = [
    ...new Set(readAttributeValues(bytes).filter((value) => !known.has(value))),
  ];
  return { managed: true, found, unknown };
}

// Every opening requestedExecutionLevel element in the file, as the distinct level values it
// declares. Closing tags (</requestedExecutionLevel>) are skipped. An element without a readable
// level is reported as "(unreadable level)".
export function readRequestedLevels(bytes) {
  const levels = new Set();
  const encodings = [
    { encoding: "latin1", unit: 1, span: 512 },
    { encoding: "utf16le", unit: 2, span: 1024 },
  ];
  for (const { encoding, unit, span } of encodings) {
    const needle = Buffer.from(REQUESTED_LEVEL_NAME, encoding);
    let at = bytes.indexOf(needle);
    while (at !== -1) {
      const opening = at >= unit && bytes.toString(encoding, at - unit, at) === "<";
      if (opening) {
        const text = bytes.toString(encoding, at, Math.min(bytes.length, at + span));
        const close = text.indexOf(">");
        const element = close === -1 ? text : text.slice(0, close);
        const match = LEVEL_ATTRIBUTE.exec(element);
        levels.add(match ? match[1] : "(unreadable level)");
      }
      at = bytes.indexOf(needle, at + 1);
    }
  }
  return [...levels].sort();
}

// The level the built setup EXE must request, by flavor. Ship installs per machine (Program Files, HKLM), so it
// asks for elevation. The E2E setup never asks, so no test or dev build can show a prompt.
export const SETUP_EXECUTION_LEVELS = Object.freeze({ ship: "requireAdministrator", e2e: "asInvoker" });

// Checks the embedded manifest of a built setup EXE (bytes) against its flavor. NSIS stores the manifest as plain text
// in the resource section, so readRequestedLevels finds it. Returns { ok, levels, expected, problem }.
export function checkSetupManifestLevel(bytes, flavor) {
  const expected = SETUP_EXECUTION_LEVELS[flavor];
  if (expected === undefined) {
    return { ok: false, levels: [], expected: null, problem: `unknown flavor "${flavor}"` };
  }
  const levels = readRequestedLevels(bytes);
  if (levels.length === 0) {
    return { ok: false, levels, expected, problem: `no requestedExecutionLevel in the setup manifest, expected ${expected}` };
  }
  if (levels.length > 1 || levels[0] !== expected) {
    return {
      ok: false,
      levels,
      expected,
      problem: `setup requestedExecutionLevel=${levels.join(", ")}, expected ${expected} for the ${flavor} flavor`,
    };
  }
  return { ok: true, levels, expected, problem: null };
}

// The level a project's source manifest declares, or null when the file has no such element.
export function readSourceManifestLevel(filePath) {
  const xml = fs.readFileSync(filePath, "utf8");
  const match = /<requestedExecutionLevel\b[^>]*?\blevel\s*=\s*"([A-Za-z]+)"/.exec(xml);
  return match ? match[1] : null;
}

function displayPath(absolute) {
  return path.relative(REPO_ROOT, absolute).split(path.sep).join("/") || ".";
}

function isProjectAssemblyName(name) {
  return (
    PROJECT_PREFIXES.some((prefix) => name.startsWith(prefix)) &&
    ASSEMBLY_EXTENSIONS.includes(path.extname(name).toLowerCase())
  );
}

// Each exe head's requestedExecutionLevel must match the folder: asInvoker in e2e, and the level
// declared in its source manifest in ship. Problems are appended to report.problems.
function checkExeManifests(dir, flavor, names, report) {
  for (const head of EXE_HEADS) {
    let expected;
    try {
      expected =
        flavor === "e2e"
          ? E2E_EXECUTION_LEVEL
          : readSourceManifestLevel(path.join(REPO_ROOT, head.sourceManifest));
    } catch (error) {
      report.problems.push(
        `${head.sourceManifest}: could not read source manifest (${error.message})`,
      );
      continue;
    }
    if (expected === null) {
      report.problems.push(
        `${head.sourceManifest}: no requestedExecutionLevel in the source manifest`,
      );
      continue;
    }
    if (!names.includes(head.name)) {
      report.problems.push(`${head.name}: published executable is missing from ${report.folder}`);
      continue;
    }

    let levels;
    try {
      levels = readRequestedLevels(fs.readFileSync(path.join(dir, head.name)));
    } catch (error) {
      report.problems.push(`${head.name}: could not read (${error.message})`);
      continue;
    }

    const actual = levels.length === 1 ? levels[0] : null;
    report.manifests.push({ name: head.name, levels, expected, ok: actual === expected });
    if (levels.length === 0) {
      report.problems.push(
        `${head.name}: no requestedExecutionLevel in the embedded manifest, expected ${expected}`,
      );
    } else if (levels.length > 1) {
      report.problems.push(
        `${head.name}: mixed requestedExecutionLevel values ${levels.join(", ")}, expected only ${expected}`,
      );
    } else if (actual !== expected) {
      report.problems.push(
        `${head.name}: requestedExecutionLevel=${actual}, expected ${expected} for the ${flavor} folder`,
      );
    }
  }
}

// Checks the project assemblies in the root of dir. Every managed one must carry exactly
// the flavor's value and no other BwcFlavor value.
export function checkPublishFlavor(dir, flavor) {
  const expected = FLAVOR_VALUES.get(flavor);
  const report = {
    folder: displayPath(dir),
    flavor,
    expected: expected ?? null,
    ok: false,
    checked: [],
    manifests: [],
    native: [],
    skipped: [],
    problems: [],
  };
  if (expected === undefined) {
    report.problems.push(
      `unknown flavor "${flavor}"; expected one of ${FLAVORS.join(", ")}`,
    );
    return report;
  }

  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (error) {
    const reason = error.code === "ENOENT" ? "not found" : `unreadable (${error.message})`;
    report.problems.push(`publish folder ${reason}: ${report.folder}`);
    return report;
  }

  const names = entries
    .filter((entry) => entry.isFile() && isProjectAssemblyName(entry.name))
    .map((entry) => entry.name)
    .sort();
  const siblingNames = entries.filter((entry) => entry.isFile()).map((entry) => entry.name);

  for (const name of names) {
    if (name === UPDATE_EXE) {
      continue; // NativeAOT: proved by checkUpdateHelper, with no managed metadata to read.
    }
    let inspected;
    try {
      inspected = inspectAssembly(fs.readFileSync(path.join(dir, name)));
    } catch (error) {
      report.problems.push(`${name}: could not read (${error.message})`);
      continue;
    }
    if (!inspected.managed) {
      report.skipped.push(name);
      continue;
    }

    const values = [...inspected.found, ...inspected.unknown];
    let problem = null;
    if (inspected.unknown.length > 0) {
      problem = `${name}: unrecognized BwcFlavor value ${inspected.unknown.map((value) => `"${value}"`).join(", ")}, expected ${expected}`;
    } else if (inspected.found.length === 0) {
      problem = `${name}: missing BwcFlavor metadata, expected ${expected}`;
    } else if (inspected.found.length > 1) {
      problem = `${name}: mixed BwcFlavor values ${inspected.found.join(", ")}, expected only ${expected}`;
    } else if (inspected.found[0] !== expected) {
      problem = `${name}: BwcFlavor=${inspected.found[0]}, expected ${expected} for the ${flavor} folder`;
    }

    report.checked.push({ name, values, ok: problem === null });
    if (problem !== null) {
      report.problems.push(problem);
    }
  }

  for (const name of REQUIRED_ASSEMBLIES) {
    if (!names.includes(name)) {
      report.problems.push(`${name}: published assembly is missing from ${report.folder}`);
    }
  }
  for (const name of missingRuntimeFiles(siblingNames)) {
    report.problems.push(`${name}: runtime file is missing from ${report.folder} (the Hub cannot start without it)`);
  }
  checkExeManifests(dir, flavor, names, report);
  checkUpdateHelper(dir, flavor, expected, siblingNames, report);
  checkHubSeams(dir, flavor, report);
  if (report.checked.length === 0 && report.problems.length === 0) {
    report.problems.push(
      `no managed ${PROJECT_PREFIXES.join(" or ")} assemblies found in ${report.folder}`,
    );
  }
  report.ok = report.problems.length === 0;
  return report;
}

export function formatFlavorReport(report) {
  const lines = [
    `flavor-metadata: ${report.folder} expects BwcFlavor=${report.expected ?? "?"} (${report.flavor})`,
  ];
  for (const item of report.checked) {
    const values = item.values.length > 0 ? item.values.join(", ") : "none";
    lines.push(`  ${item.name}: BwcFlavor=${values} ${item.ok ? "ok" : "MISMATCH"}`);
  }
  for (const item of report.manifests) {
    const levels = item.levels.length > 0 ? item.levels.join(", ") : "none";
    lines.push(
      `  ${item.name}: requestedExecutionLevel=${levels} (expected ${item.expected}) ${item.ok ? "ok" : "MISMATCH"}`,
    );
  }
  for (const item of report.native) {
    const values = item.values.length > 0 ? item.values.join(", ") : "none";
    lines.push(
      `  ${item.name}: NativeAOT ${item.aot ? "yes" : "NO"}, ${UPDATE_FLAVOR_MARKER}${values} ${item.ok ? "ok" : "MISMATCH"}`,
    );
  }
  for (const name of report.skipped) {
    lines.push(`  ${name}: native executable, no managed metadata`);
  }
  lines.push(
    `flavor-metadata: ${report.ok ? "PASS" : "FAIL"}: ${report.checked.length} managed assembly(ies) checked, ${report.native.length} NativeAOT exe(s) checked, ${report.manifests.length} exe manifest(s) checked, ${report.skipped.length} native file(s) skipped`,
  );
  for (const problem of report.problems) {
    lines.push(`  problem: ${problem}`);
  }
  return lines;
}

function parseCliArgs(argv) {
  const options = { rid: null, flavor: null };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    const equals = arg.indexOf("=");
    const key = equals === -1 ? arg : arg.slice(0, equals);
    if (key !== "--rid" && key !== "--flavor") {
      throw new Error(`unrecognized argument: ${arg}`);
    }
    let value;
    if (equals === -1) {
      value = argv[index + 1];
      index += 1;
    } else {
      value = arg.slice(equals + 1);
    }
    if (value === undefined || value === "" || value.startsWith("--")) {
      throw new Error(`${key} requires a value`);
    }
    options[key.slice(2)] = value;
  }
  if (!["win-x64", "win-arm64"].includes(options.rid)) {
    throw new Error(`--rid must be win-x64 or win-arm64; received ${options.rid ?? "(none)"}`);
  }
  if (!FLAVORS.includes(options.flavor)) {
    throw new Error(`--flavor must be ${FLAVORS.join(" or ")}; received ${options.flavor ?? "(none)"}`);
  }
  return options;
}

function main(argv) {
  let options;
  try {
    options = parseCliArgs(argv);
  } catch (error) {
    console.error(`flavor-metadata: ${error.message}`);
    console.error("Usage: node scripts/flavor-metadata.js --rid win-x64|win-arm64 --flavor ship|e2e");
    return 2;
  }
  const folder = path.join(REPO_ROOT, "publish", publishFolderName(options.rid, options.flavor));
  const report = checkPublishFlavor(folder, options.flavor);
  for (const line of formatFlavorReport(report)) {
    console.log(line);
  }
  return report.ok ? 0 : 1;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exit(main(process.argv.slice(2)));
}
