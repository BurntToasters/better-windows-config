#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT = "scripts/dotnet-build.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const DOTNET_FALLBACK = "C:\\Program Files\\dotnet\\dotnet.exe";

const RID_PLATFORMS = new Map([
  ["win-x64", "x64"],
  ["win-arm64", "ARM64"],
]);
const FLAVORS = new Set(["ship", "e2e"]);

const PROJECTS = [
  { name: "Bwc.Hub", project: "src/Bwc.Hub/Bwc.Hub.csproj" },
  { name: "Bwc.Regedit.App", project: "src/Bwc.Regedit.App/Bwc.Regedit.App.csproj" },
  { name: "Bwc.Update", project: "src/Bwc.Update/Bwc.Update.csproj" },
];

const WARNING_LINE = /\bwarning\b/i;
// MSBuild writes a count such as "0 Warning(s)" even for a clean build. The
// per-diagnostic lines are the real signal, so skip only the summary count.
const WARNING_SUMMARY = /^\s*\d+\s+Warning\(s\)\s*$/i;

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} --rid win-x64|win-arm64 [--flavor ship|e2e]

Publishes Bwc.Hub, Bwc.Regedit.App, then Bwc.Update into
publish/<rid> (ship) or publish/<rid>-e2e (e2e).

Options:
  --rid <rid>        Required. win-x64 or win-arm64.
  --flavor <flavor>  ship (default) or e2e.
  --help             Show this help.`);
}

function parseArgs(argv) {
  const args = argv.slice(2);
  const options = { rid: null, flavor: "ship", help: false };
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--rid") {
      if (index + 1 >= args.length) {
        throw new Error("--rid requires a value");
      }
      options.rid = args[index + 1];
      index += 1;
      continue;
    }
    if (arg.startsWith("--rid=")) {
      options.rid = arg.slice("--rid=".length);
      if (!options.rid) {
        throw new Error("--rid requires a value");
      }
      continue;
    }
    if (arg === "--flavor") {
      if (index + 1 >= args.length) {
        throw new Error("--flavor requires a value");
      }
      options.flavor = args[index + 1];
      index += 1;
      continue;
    }
    if (arg.startsWith("--flavor=")) {
      options.flavor = arg.slice("--flavor=".length);
      if (!options.flavor) {
        throw new Error("--flavor requires a value");
      }
      continue;
    }
    throw new Error(`unrecognized argument: ${arg}`);
  }

  if (options.help) {
    return options;
  }

  if (!RID_PLATFORMS.has(options.rid)) {
    throw new Error(
      `--rid must be one of ${[...RID_PLATFORMS.keys()].join(", ")}; received ${options.rid ?? "(none)"}`,
    );
  }
  if (!FLAVORS.has(options.flavor)) {
    throw new Error(
      `--flavor must be one of ${[...FLAVORS].join(", ")}; received ${options.flavor}`,
    );
  }
  return options;
}

function readPackageVersion() {
  try {
    const parsed = JSON.parse(
      fs.readFileSync(path.join(REPO_ROOT, "package.json"), "utf8"),
    );
    return typeof parsed.version === "string" ? parsed.version : "unknown";
  } catch {
    return "unknown";
  }
}

function outputPath(rid, flavor) {
  const name = flavor === "e2e" ? `${rid}-e2e` : rid;
  return path.join(REPO_ROOT, "publish", name);
}

function relativeOutputPath(outDir) {
  return path.relative(REPO_ROOT, outDir).split(path.sep).join("/");
}

function artifactPath(rid, flavor) {
  const name = flavor === "e2e" ? `${rid}-e2e` : rid;
  return path.join(REPO_ROOT, "artifacts", "build", `${name}.json`);
}

function sha256File(filePath) {
  const hash = createHash("sha256");
  const descriptor = fs.openSync(filePath, "r");
  const buffer = Buffer.allocUnsafe(1024 * 1024);
  try {
    let bytesRead = 0;
    do {
      bytesRead = fs.readSync(descriptor, buffer, 0, buffer.length, null);
      if (bytesRead > 0) {
        hash.update(buffer.subarray(0, bytesRead));
      }
    } while (bytesRead > 0);
  } finally {
    fs.closeSync(descriptor);
  }
  return hash.digest("hex");
}

function snapshotOutput(outDir) {
  const files = new Map();
  if (!fs.existsSync(outDir)) return files;

  function walk(directory, relative) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const absolute = path.join(directory, entry.name);
      const rel = relative ? `${relative}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        walk(absolute, rel);
        continue;
      }
      if (entry.isFile()) {
        files.set(rel, sha256File(absolute));
        continue;
      }
      if (entry.isSymbolicLink()) {
        try {
          if (fs.statSync(absolute).isFile()) {
            files.set(rel, sha256File(absolute));
          }
        } catch {
          // A dangling link is not a published file.
        }
      }
    }
  }

  walk(outDir, "");
  return files;
}

function findWarnings(output) {
  return String(output || "")
    .split(/\r?\n/)
    .filter((line) => WARNING_LINE.test(line) && !WARNING_SUMMARY.test(line))
    .map((line) => line.trim())
    .filter(Boolean);
}

function invokeDotnet(args) {
  const options = {
    cwd: REPO_ROOT,
    encoding: "utf8",
    windowsHide: true,
    maxBuffer: 128 * 1024 * 1024,
    // Do not let a lingering MSBuild/compiler server inherit the publish pipe. Node's
    // spawnSync waits for the pipe to close, so node reuse turns a finished publish into a
    // hung build step.
    env: {
      ...process.env,
      MSBUILDDISABLENODEREUSE: "1",
      DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER: "1",
    },
  };
  let result = spawnSync("dotnet", args, options);
  let usedFallback = false;
  if (result.error && result.error.code === "ENOENT") {
    if (fs.existsSync(DOTNET_FALLBACK)) {
      usedFallback = true;
      result = spawnSync(DOTNET_FALLBACK, args, options);
    }
  }
  return { result, usedFallback };
}

function publishProject({ project, rid, platform, flavor, outDir }) {
  const args = [
    "publish",
    path.join(REPO_ROOT, project),
    "-c",
    "Release",
    "-r",
    rid,
    "-p:Platform=" + platform,
    "-p:PublishReadyToRun=true",
  ];
  if (flavor === "e2e") {
    args.push("-p:BwcFlavor=E2E");
  }
  args.push("-o", outDir, "--nologo");

  const { result, usedFallback } = invokeDotnet(args);
  if (result.error) {
    if (result.error.code === "ENOENT") {
      return {
        exitCode: null,
        output: "",
        warnings: [],
        usedFallback,
        error:
          "dotnet was not found on PATH and the fallback C:\\Program Files\\dotnet\\dotnet.exe is missing",
      };
    }
    return {
      exitCode: null,
      output: "",
      warnings: [],
      usedFallback,
      error: `could not start dotnet: ${result.error.message}`,
    };
  }

  const output = [String(result.stdout || ""), String(result.stderr || "")]
    .filter((part) => part.length > 0)
    .join("\n");
  const exitCode = result.status ?? 1;
  const warnings = findWarnings(output);
  return { exitCode, output, warnings, usedFallback, error: null };
}

function tailLines(text, limit) {
  const lines = String(text || "")
    .split(/\r?\n/)
    .filter((line) => line.trim() !== "");
  return lines.slice(Math.max(0, lines.length - limit));
}

function mergeSnapshot(known, current, projectName, collisions) {
  for (const [relative, hash] of current) {
    const prior = known.get(relative);
    if (!prior) {
      known.set(relative, { hash, project: projectName });
      continue;
    }
    if (prior.hash !== hash) {
      collisions.push({
        path: relative,
        firstProject: prior.project,
        secondProject: projectName,
        firstSha256: prior.hash,
        secondSha256: hash,
      });
    }
  }
}

function writeArtifact(artifact, filePath) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, `${JSON.stringify(artifact, null, 2)}\n`, "utf8");
}

function main(argv = process.argv) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(
      `dotnet-build: ${error instanceof Error ? error.message : String(error)}`,
    );
    console.error(`Try: node ${SCRIPT} --help`);
    return 2;
  }

  if (options.help) {
    printHelp();
    return 0;
  }

  const { rid, flavor } = options;
  const platform = RID_PLATFORMS.get(rid);
  const outDir = outputPath(rid, flavor);
  const displayOutDir = relativeOutputPath(outDir);
  const artifactFile = artifactPath(rid, flavor);
  const version = readPackageVersion();
  const startedMs = Date.now();
  const startedUtc = new Date(startedMs).toISOString();

  const projects = [];
  const collisions = [];
  const knownFiles = new Map();
  let fileCount = 0;
  let errorMessage = null;

  try {
    fs.rmSync(outDir, { recursive: true, force: true });
  } catch (error) {
    errorMessage = `could not remove ${displayOutDir}: ${error instanceof Error ? error.message : String(error)}`;
  }

  if (!errorMessage) {
    for (const project of PROJECTS) {
      const result = publishProject({
        project: project.project,
        rid,
        platform,
        flavor,
        outDir,
      });

      projects.push({
        name: project.name,
        exitCode: result.exitCode,
        warnings: result.warnings,
      });

      const status =
        result.exitCode === 0 && result.warnings.length === 0
          ? "ok"
          : "failed";
      console.log(
        `${project.name}: ${status} (exit ${result.exitCode ?? "null"}, ${result.warnings.length} warning(s))`,
      );

      if (result.warnings.length > 0) {
        for (const warning of result.warnings.slice(0, 20)) {
          console.error(`  ${warning}`);
        }
        if (result.warnings.length > 20) {
          console.error(
            `  ... ${result.warnings.length - 20} more warning line(s)`,
          );
        }
      }

      if (result.error) {
        errorMessage = `${project.name}: ${result.error}`;
      } else if (result.exitCode !== 0) {
        errorMessage = `${project.name} exited with code ${result.exitCode}`;
      } else if (result.warnings.length > 0) {
        errorMessage = `${project.name} produced ${result.warnings.length} build warning(s)`;
      }

      const current = snapshotOutput(outDir);
      mergeSnapshot(knownFiles, current, project.name, collisions);
      fileCount = current.size;
      if (collisions.length > 0 && errorMessage === null) {
        const collision = collisions[0];
        errorMessage = `file collision: ${collision.path} differs between ${collision.firstProject} and ${collision.secondProject}`;
      }

      if (errorMessage) {
        for (const line of tailLines(result.output, 40)) {
          console.error(`  ${line}`);
        }
        break;
      }
    }
  }

  const pass = errorMessage === null;
  const artifact = {
    schema: 1,
    version,
    rid,
    flavor,
    outputDir: displayOutDir,
    startedUtc,
    durationMs: Date.now() - startedMs,
    pass,
    projects,
    fileCount,
    collisions,
  };
  if (!pass) {
    artifact.error = errorMessage;
  }

  try {
    writeArtifact(artifact, artifactFile);
  } catch (error) {
    console.error(
      `dotnet-build: could not write ${artifactFile}: ${error instanceof Error ? error.message : String(error)}`,
    );
    return 1;
  }

  if (pass) {
    console.log(
      `dotnet-build: PASS ${rid} (${flavor}) -> ${displayOutDir}, ${fileCount} file(s), ${artifact.durationMs} ms`,
    );
    return 0;
  }

  console.error(`dotnet-build: FAIL ${rid} (${flavor}): ${errorMessage}`);
  console.error(`Artifact: ${artifactFile}`);
  return 1;
}

process.exit(main());
