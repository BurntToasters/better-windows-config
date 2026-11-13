#!/usr/bin/env node
/**
 * Writes THIRD-PARTY-NOTICES.txt for the NuGet packages that ship in the published apps.
 *
 * Inputs: obj/project.assets.json of Bwc.Hub, Bwc.Regedit.App, and Bwc.Update (written by restore
 * or build). A package ships when its target for the chosen RID has runtime, native, resource, or
 * runtimeTargets assets. Build-only and compile-only packages are left out.
 *
 * License text: read from the local NuGet package cache (NUGET_PACKAGES if set, otherwise
 * %USERPROFILE%\.nuget\packages). The nuspec <license> element names the expression or the file.
 * License files in the package root are added when present. No network access.
 *
 * Output is deterministic: packages sort by lowercase id, then by version. The file has no
 * timestamp and no local paths.
 */
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isDirectExecutionOf } from "./direct-execution.mjs";

const SCRIPT = "scripts/generate-nuget-licenses.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const DEFAULT_OUTPUT = path.join(REPO_ROOT, "artifacts", "notices", "THIRD-PARTY-NOTICES.txt");
const RID_VALUES = ["win-x64", "win-arm64"];
const PROJECTS = [
  { name: "Bwc.Hub", dir: "src/Bwc.Hub" },
  { name: "Bwc.Regedit.App", dir: "src/Bwc.Regedit.App" },
  { name: "Bwc.Update", dir: "src/Bwc.Update" },
];
const LICENSE_FILE_PATTERN = /^(licen[cs]e|copying|notice|third[-_ ]?party)/i;
const RULE = "-".repeat(80);

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--rid win-x64|win-arm64] [--out <file>] [--help]

Reads obj/project.assets.json of ${PROJECTS.map((p) => p.name).join(", ")} and writes the
third-party notices for the packages that ship in the published app folder.

Options:
  --rid <rid>     Only packages that ship for this RID. Default: packages for both RIDs.
  --out <file>    Output file. Default: artifacts/notices/THIRD-PARTY-NOTICES.txt.
  --help          Show this help.

Exit codes: 0 written, 1 missing assets or license data, 2 argument error.`);
}

export function parseArgs(argv) {
  const options = { rid: null, out: null, help: false };
  const args = argv.slice(2);
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--rid" || arg === "--out") {
      if (index + 1 >= args.length || args[index + 1].startsWith("--")) {
        throw new Error(`${arg} requires a value`);
      }
      options[arg.slice(2)] = args[index + 1];
      index += 1;
      continue;
    }
    throw new Error(`unrecognized argument: ${arg}`);
  }
  if (options.rid !== null && !RID_VALUES.includes(options.rid)) {
    throw new Error(`--rid must be one of ${RID_VALUES.join(", ")}; received ${options.rid}`);
  }
  return options;
}

/** NuGet package cache root. NUGET_PACKAGES wins when set, as NuGet itself does. */
export function packagesRoot(env = process.env, homeDir = os.homedir()) {
  const override = typeof env.NUGET_PACKAGES === "string" ? env.NUGET_PACKAGES.trim() : "";
  return override ? path.resolve(override) : path.join(homeDir, ".nuget", "packages");
}

function hasRuntimeAssets(entry) {
  const nonEmpty = (value) => value && typeof value === "object" && Object.keys(value).length > 0;
  return (
    nonEmpty(entry.runtime) ||
    nonEmpty(entry.native) ||
    nonEmpty(entry.resource) ||
    nonEmpty(entry.runtimeTargets)
  );
}

/**
 * Packages that ship for one RID, read from a project.assets.json object.
 * Returns [{ id, version, dirName }] in the assets' own spelling of the id.
 */
export function shippedPackages(assets, rid) {
  const libraries = assets?.libraries ?? {};
  const targets = assets?.targets ?? {};
  const suffix = `/${rid}`;
  const found = new Map();
  for (const [targetKey, entries] of Object.entries(targets)) {
    if (!targetKey.endsWith(suffix)) continue;
    for (const [libraryKey, entry] of Object.entries(entries ?? {})) {
      if (!entry || typeof entry !== "object" || !hasRuntimeAssets(entry)) continue;
      const library = libraries[libraryKey];
      if (!library || library.type !== "package") continue;
      const slash = libraryKey.indexOf("/");
      if (slash <= 0) continue;
      const id = libraryKey.slice(0, slash);
      const version = libraryKey.slice(slash + 1);
      found.set(libraryKey, { id, version });
    }
  }
  return [...found.values()];
}

function decodeXml(text) {
  return text
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&");
}

function xmlText(xml, tag) {
  const match = xml.match(new RegExp(`<${tag}>\\s*([\\s\\S]*?)\\s*</${tag}>`, "i"));
  return match ? decodeXml(match[1].trim()) : null;
}

/** Parses the fields the notice needs from a nuspec. Returns null when there is no nuspec. */
export function parseNuspec(xml) {
  const licenseMatch = xml.match(/<license\b([^>]*)>([\s\S]*?)<\/license>/i);
  let license = null;
  if (licenseMatch) {
    const type = (licenseMatch[1].match(/\btype\s*=\s*"([^"]+)"/i) ?? [])[1] ?? "";
    const value = decodeXml(licenseMatch[2].trim());
    if (type.toLowerCase() === "expression" && value) {
      license = { expression: value, file: null };
    } else if (type.toLowerCase() === "file" && value) {
      license = { expression: null, file: value };
    }
  }
  return {
    license,
    licenseUrl: xmlText(xml, "licenseUrl"),
    projectUrl: xmlText(xml, "projectUrl"),
    authors: xmlText(xml, "authors"),
  };
}

function findNuspec(dir, id) {
  const preferred = path.join(dir, `${id.toLowerCase()}.nuspec`);
  if (existsSync(preferred)) return preferred;
  const other = readdirSync(dir).filter((name) => name.toLowerCase().endsWith(".nuspec")).sort();
  return other.length > 0 ? path.join(dir, other[0]) : null;
}

/** License text from files in the package root. Returns null when none exist. */
function readLicenseFiles(dir, declaredFile) {
  const names = new Set(
    readdirSync(dir).filter((name) => {
      const full = path.join(dir, name);
      return LICENSE_FILE_PATTERN.test(name) && statSync(full).isFile();
    }),
  );
  if (declaredFile) {
    const normalized = path.basename(declaredFile.replace(/\\/g, "/"));
    if (normalized === declaredFile.replace(/\\/g, "/") && existsSync(path.join(dir, normalized))) {
      names.add(normalized);
    }
  }
  const sections = [];
  for (const name of [...names].sort()) {
    const text = readFileSync(path.join(dir, name), "utf8").replace(/\r\n?/g, "\n").trim();
    if (text) sections.push({ name, text });
  }
  return sections.length > 0 ? sections : null;
}

/**
 * Reads one package from the cache. Throws when the package is missing or has no license
 * expression and no license file, because a shipped notice cannot be complete without one.
 */
export function readPackageLicense(cacheRoot, id, version) {
  const dir = path.join(cacheRoot, id.toLowerCase(), version);
  if (!existsSync(dir)) {
    throw new Error(
      `package ${id} ${version} is not in the NuGet cache (${id.toLowerCase()}\\${version}). Restore the solution first.`,
    );
  }
  const nuspecPath = findNuspec(dir, id);
  if (!nuspecPath) {
    throw new Error(`package ${id} ${version} has no .nuspec in the NuGet cache`);
  }
  const meta = parseNuspec(readFileSync(nuspecPath, "utf8"));
  if (!meta.license) {
    throw new Error(
      `package ${id} ${version} declares no <license> expression or file in its nuspec`,
    );
  }
  const files = readLicenseFiles(dir, meta.license.file);
  if (meta.license.file && !files) {
    throw new Error(
      `package ${id} ${version} declares license file ${meta.license.file}, which is not in the cache`,
    );
  }
  return {
    id,
    version,
    expression: meta.license.expression,
    licenseUrl: meta.licenseUrl,
    projectUrl: meta.projectUrl,
    authors: meta.authors,
    files,
  };
}

function compareStrings(a, b) {
  if (a < b) return -1;
  if (a > b) return 1;
  return 0;
}

/** Renders the notices text. Pure: the same input always gives the same bytes. */
export function renderNotices(packages, { rids }) {
  const lines = [];
  lines.push("Better Windows Config - third-party notices");
  lines.push("==========================================");
  lines.push("");
  lines.push(
    `This file lists the NuGet packages that ship in the published application folder (${rids.join(", ")}).`,
  );
  lines.push(
    "Better Windows Config is licensed under the Mozilla Public License 2.0 (see LICENSE).",
  );
  lines.push(
    "Each package below keeps its own license. The text is copied from the package, or the",
  );
  lines.push("declared license is listed when the package ships no license file.");
  lines.push("");
  lines.push(`Packages: ${packages.length}`);
  lines.push("");
  for (const pkg of packages) {
    lines.push(RULE);
    lines.push(`Package: ${pkg.id} ${pkg.version}`);
    lines.push(`License: ${pkg.expression ?? "see license text below"}`);
    if (pkg.licenseUrl) lines.push(`License URL: ${pkg.licenseUrl}`);
    if (pkg.projectUrl) lines.push(`Project URL: ${pkg.projectUrl}`);
    if (pkg.authors) lines.push(`Authors: ${pkg.authors}`);
    lines.push(`Used by: ${pkg.usedBy.join(", ")}`);
    lines.push("");
    if (pkg.files) {
      for (const section of pkg.files) {
        if (pkg.files.length > 1) {
          lines.push(`--- ${section.name} ---`);
        }
        lines.push(section.text);
        lines.push("");
      }
    } else {
      lines.push(
        "No license file ships in this package. The declared license expression above applies.",
      );
      lines.push("");
    }
  }
  return `${lines.join("\n").replace(/\n+$/, "")}\n`;
}

/** The .NET runtime version the published app ships, from its runtimeconfig. Null when not published. */
export function publishedRuntimeVersion(root, rid) {
  const configPath = path.join(root, "publish", rid, "BetterWindowsConfig.runtimeconfig.json");
  if (!existsSync(configPath)) return null;
  const config = JSON.parse(readFileSync(configPath, "utf8"));
  const frameworks = config?.runtimeOptions?.includedFrameworks ?? [];
  const netcore = frameworks.find((framework) => framework?.name === "Microsoft.NETCore.App");
  return typeof netcore?.version === "string" ? netcore.version : null;
}

/**
 * Collects the shipped packages across the three apps, reads their licenses, and writes the
 * notices file. Returns the summary. Throws with a message a user can act on.
 */
export function generateNugetNotices({
  root = REPO_ROOT,
  rid = null,
  outPath = DEFAULT_OUTPUT,
  cacheRoot = packagesRoot(),
} = {}) {
  const rids = rid ? [rid] : RID_VALUES;
  const byKey = new Map();
  const windowsAppSdkVersions = new Map();
  const addEntry = (id, version, usedBy) => {
    const key = `${id.toLowerCase()}/${version}`;
    const existing = byKey.get(key) ?? { id, version, usedBy: new Set() };
    existing.usedBy.add(usedBy);
    byKey.set(key, existing);
  };
  for (const project of PROJECTS) {
    const assetsPath = path.join(root, project.dir, "obj", "project.assets.json");
    if (!existsSync(assetsPath)) {
      throw new Error(
        `${project.name}: ${path.relative(root, assetsPath)} is missing. Run node scripts/dotnet-build.js first, or dotnet restore.`,
      );
    }
    const assets = JSON.parse(readFileSync(assetsPath, "utf8"));
    for (const libraryKey of Object.keys(assets.libraries ?? {})) {
      const match = libraryKey.match(/^Microsoft\.WindowsAppSDK\/(.+)$/);
      if (match) {
        const users = windowsAppSdkVersions.get(match[1]) ?? new Set();
        users.add(project.name);
        windowsAppSdkVersions.set(match[1], users);
      }
    }
    for (const targetRid of rids) {
      const packages = shippedPackages(assets, targetRid);
      if (packages.length === 0 && !Object.keys(assets.targets ?? {}).some((key) => key.endsWith(`/${targetRid}`))) {
        throw new Error(
          `${project.name}: no ${targetRid} target in ${path.relative(root, assetsPath)}. Restore with --runtime ${targetRid}.`,
        );
      }
      for (const pkg of packages) {
        addEntry(pkg.id, pkg.version, project.name);
      }
    }
  }

  // Shipped but not library entries in project.assets.json. The Windows App SDK runtime package
  // only contributes build targets, and the self-contained .NET runtime pack is a known framework
  // that the SDK applies at publish time. Their versions come from the shipped output instead.
  for (const [version, users] of windowsAppSdkVersions) {
    addEntry("Microsoft.WindowsAppSDK.Runtime", version, [...users].sort(compareStrings).join(", "));
  }
  const runtimePackNotes = [];
  for (const targetRid of rids) {
    const version = publishedRuntimeVersion(root, targetRid);
    if (version) {
      addEntry(`Microsoft.NETCore.App.Runtime.${targetRid}`, version, `${targetRid} published output`);
    } else if (rid) {
      throw new Error(
        `publish/${targetRid}/BetterWindowsConfig.runtimeconfig.json is missing, so the .NET runtime version is unknown. Run node scripts/dotnet-build.js --rid ${targetRid} first.`,
      );
    } else {
      runtimePackNotes.push(`${targetRid}: .NET runtime pack skipped (publish/${targetRid} not built)`);
    }
  }

  const packages = [...byKey.values()]
    .sort((a, b) => compareStrings(a.id.toLowerCase(), b.id.toLowerCase()) || compareStrings(a.version, b.version))
    .map((entry) => ({
      ...readPackageLicense(cacheRoot, entry.id, entry.version),
      usedBy: [...entry.usedBy].sort(compareStrings),
    }));

  const publishedRids = rids.filter((targetRid) => publishedRuntimeVersion(root, targetRid));
  const text = renderNotices(packages, { rids: publishedRids.length > 0 ? publishedRids : rids });
  mkdirSync(path.dirname(outPath), { recursive: true });
  writeFileSync(outPath, text, "utf8");
  return {
    outPath,
    runtimePackNotes,
    packageCount: packages.length,
    withoutLicenseFile: packages.filter((pkg) => !pkg.files).map((pkg) => `${pkg.id} ${pkg.version}`),
    sha256: createHash("sha256").update(text, "utf8").digest("hex"),
  };
}

function main(argv = process.argv) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(`generate-nuget-licenses: ${error instanceof Error ? error.message : String(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return 2;
  }
  if (options.help) {
    printHelp();
    return 0;
  }
  try {
    const outPath = options.out ? path.resolve(options.out) : DEFAULT_OUTPUT;
    const summary = generateNugetNotices({ rid: options.rid, outPath });
    console.log(
      `generate-nuget-licenses: wrote ${summary.packageCount} package(s) to ${path.relative(REPO_ROOT, summary.outPath) || summary.outPath}`,
    );
    console.log(`generate-nuget-licenses: sha256 ${summary.sha256}`);
    for (const note of summary.runtimePackNotes) {
      console.log(`generate-nuget-licenses: ${note}`);
    }
    if (summary.withoutLicenseFile.length > 0) {
      console.log(
        `generate-nuget-licenses: ${summary.withoutLicenseFile.length} package(s) declare a license but ship no license file: ${summary.withoutLicenseFile.join(", ")}`,
      );
    }
    return 0;
  } catch (error) {
    console.error(`generate-nuget-licenses: FAIL: ${error instanceof Error ? error.message : String(error)}`);
    return 1;
  }
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = main();
}
