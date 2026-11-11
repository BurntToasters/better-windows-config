/**
 * Verify the BwcFlavor assembly metadata in a publish folder.
 *
 * Directory.Build.props records each assembly's flavor as
 * [assembly: AssemblyMetadata("BwcFlavor", "<value>")]. The compiler stores that as a
 * custom attribute blob whose strings are length-prefixed UTF-8 (SerString), so the
 * byte sequences searched here identify the attribute without a metadata reader.
 * Native apphost executables carry no managed metadata and are reported as skipped.
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

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

function displayPath(absolute) {
  return path.relative(REPO_ROOT, absolute).split(path.sep).join("/") || ".";
}

function isProjectAssemblyName(name) {
  return (
    PROJECT_PREFIXES.some((prefix) => name.startsWith(prefix)) &&
    ASSEMBLY_EXTENSIONS.includes(path.extname(name).toLowerCase())
  );
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
  for (const name of report.skipped) {
    lines.push(`  ${name}: native executable without managed metadata, not checked`);
  }
  lines.push(
    `flavor-metadata: ${report.ok ? "PASS" : "FAIL"}: ${report.checked.length} managed assembly(ies) checked, ${report.skipped.length} native file(s) skipped`,
  );
  for (const problem of report.problems) {
    lines.push(`  problem: ${problem}`);
  }
  return lines;
}
