/**
 * Verify the BwcFlavor assembly metadata in a publish folder.
 *
 * Directory.Build.props records each assembly's flavor as
 * [assembly: AssemblyMetadata("BwcFlavor", "<value>")]. The compiler stores that as a
 * custom attribute blob whose strings are length-prefixed UTF-8 (SerString), so the
 * byte sequences searched here identify the attribute without a metadata reader.
 * Native apphost executables carry no managed metadata and are reported as skipped.
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
// Main assembly of each project that dotnet-build publishes (Bwc.Hub, Bwc.Regedit.App,
// Bwc.Update). A folder without them is incomplete, whatever the other files say.
const REQUIRED_ASSEMBLIES = [
  "BetterWindowsConfig.dll",
  "BwcRegedit.dll",
  "BwcUpdate.dll",
];
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
const E2E_EXECUTION_LEVEL = "asInvoker";
const REQUESTED_LEVEL_NAME = "requestedExecutionLevel";
const LEVEL_ATTRIBUTE = /\blevel\s*=\s*["']([A-Za-z]+)["']/;

export const FLAVORS = [...FLAVOR_VALUES.keys()];

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

  for (const name of names) {
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
  checkExeManifests(dir, flavor, names, report);
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
  for (const name of report.skipped) {
    lines.push(`  ${name}: native executable, no managed metadata`);
  }
  lines.push(
    `flavor-metadata: ${report.ok ? "PASS" : "FAIL"}: ${report.checked.length} managed assembly(ies) checked, ${report.manifests.length} exe manifest(s) checked, ${report.skipped.length} native file(s) skipped`,
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
