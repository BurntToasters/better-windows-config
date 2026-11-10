#!/usr/bin/env node
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, readFileSync, rmSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, "..");
const artifactsDir = path.join(repoRoot, "artifacts", "e2e");
const resultPath = path.join(artifactsDir, "result.json");
const dotnetFallback = "C:\\Program Files\\dotnet\\dotnet.exe";

const usage = `Usage: node scripts/test-e2e.js [options]

Runs the Bwc.E2E test project against a publish folder.

Options:
  --rid win-x64|win-arm64   Target runtime identifier (default from process.arch)
  --flavor ship|e2e         Publish flavor (default e2e)
  --build                   Run scripts/dotnet-build.js first
  --filter <pattern>        Pass a filter to dotnet test
  --help                    Show this help

Environment:
  BWC_PUBLISH_DIR           Override the publish folder (relative to the repo root)
`;

function parseArgs(argv) {
  const options = {
    rid: undefined,
    flavor: undefined,
    build: false,
    filter: undefined,
    help: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }

    if (arg === "--build") {
      options.build = true;
      continue;
    }

    if (arg === "--rid" || arg === "--flavor" || arg === "--filter") {
      const value = argv[i + 1];
      if (value === undefined || value.startsWith("--")) {
        throw new Error(`Missing value for ${arg}.`);
      }

      i += 1;
      if (arg === "--rid") {
        options.rid = value;
      } else if (arg === "--flavor") {
        options.flavor = value;
      } else {
        options.filter = value;
      }

      continue;
    }

    throw new Error(`Unknown argument '${arg}'.`);
  }

  return options;
}

function defaultRid() {
  if (process.arch === "arm64") {
    return "win-arm64";
  }

  if (process.arch === "x64") {
    return "win-x64";
  }

  return undefined;
}

function findOnPath(name) {
  const pathValue = process.env.PATH ?? "";
  const extensions = process.platform === "win32" ? [".exe", ".cmd", ".bat", ""] : [""];
  for (const directory of pathValue.split(path.delimiter)) {
    if (directory.length === 0) {
      continue;
    }

    for (const extension of extensions) {
      const candidate = path.join(directory, `${name}${extension}`);
      if (existsSync(candidate)) {
        return candidate;
      }
    }
  }

  return undefined;
}

function stamp(date) {
  return `${date.toISOString().replace(/[-:]/g, "").replace(/\.\d{3}Z$/, "Z")}`;
}

function runStreaming(command, args, options) {
  return new Promise((resolve, reject) => {
    let output = "";
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: options.env ?? process.env,
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
    });

    child.stdout.on("data", (chunk) => {
      process.stdout.write(chunk);
      output += chunk.toString();
    });
    child.stderr.on("data", (chunk) => {
      process.stderr.write(chunk);
      output += chunk.toString();
    });
    child.on("error", reject);
    child.on("close", (code) => resolve({ code, output }));
  });
}

function tail(text, lines = 60) {
  const all = text.trimEnd().split(/\r?\n/);
  return all.slice(Math.max(0, all.length - lines)).join("\n");
}

function printResult(result) {
  const checks = Array.isArray(result.checks) ? result.checks : [];
  const passed = checks.filter((check) => check.pass === true);
  const failed = checks.filter((check) => check.pass !== true);

  console.log("");
  console.log(`E2E ${result.pass === true ? "PASS" : "FAIL"}`);
  console.log(`runId: ${result.runId}`);
  console.log(`rid: ${result.rid}  flavor: ${result.flavor}`);
  console.log(`publishDir: ${result.publishDir}`);
  console.log(`checks: ${passed.length} passed, ${failed.length} failed, ${checks.length} total`);

  if (failed.length > 0) {
    console.log("failed checks:");
    for (const check of failed) {
      console.log(`  - ${check.id}: ${String(check.notes ?? "").replace(/\r?\n/g, " ")}`);
    }
  }

  console.log(`artifact: ${resultPath}`);
}

let options;
try {
  options = parseArgs(process.argv.slice(2));
} catch (error) {
  console.error(error.message);
  console.error(usage);
  process.exit(2);
}

if (options.help) {
  console.log(usage);
  process.exit(0);
}

const rid = options.rid ?? defaultRid();
if (rid !== "win-x64" && rid !== "win-arm64") {
  console.error(`Invalid or unsupported --rid '${rid}'. Expected win-x64 or win-arm64.`);
  console.error(usage);
  process.exit(2);
}

const flavor = options.flavor ?? "e2e";
if (flavor !== "ship" && flavor !== "e2e") {
  console.error(`Invalid --flavor '${flavor}'. Expected ship or e2e.`);
  console.error(usage);
  process.exit(2);
}

const platform = rid === "win-arm64" ? "ARM64" : "x64";
const defaultFolder = flavor === "e2e" ? `${rid}-e2e` : rid;
const configuredPublish = process.env.BWC_PUBLISH_DIR?.trim();
const publishDir = configuredPublish
  ? path.resolve(repoRoot, configuredPublish)
  : path.join(repoRoot, "publish", defaultFolder);

const runId = `${stamp(new Date())}-${randomBytes(4).toString("hex")}`;

console.log(`E2E rid=${rid} flavor=${flavor} platform=${platform}`);
console.log(`runId=${runId}`);
console.log(`publishDir=${publishDir}`);

if (options.build) {
  const buildScript = path.join(repoRoot, "scripts", "dotnet-build.js");
  if (!existsSync(buildScript)) {
    console.error(`Build script not found: ${buildScript}`);
    process.exit(1);
  }

  console.log("");
  console.log(`Building: node scripts/dotnet-build.js --rid ${rid} --flavor ${flavor}`);
  let build;
  try {
    build = await runStreaming(process.execPath, [buildScript, "--rid", rid, "--flavor", flavor], {
      cwd: repoRoot,
    });
  } catch (error) {
    console.error(`Could not start the build: ${error.message}`);
    process.exit(1);
  }

  if (build.code !== 0) {
    console.error(`Build failed with exit code ${build.code}.`);
    process.exit(build.code ?? 1);
  }
}

const dotnet = findOnPath("dotnet") ?? (existsSync(dotnetFallback) ? dotnetFallback : undefined);
if (dotnet === undefined) {
  console.error("dotnet was not found on PATH and C:\\Program Files\\dotnet\\dotnet.exe does not exist.");
  process.exit(1);
}

try {
  rmSync(resultPath, { force: true });
} catch (error) {
  console.error(`Could not remove the previous result artifact: ${error.message}`);
  process.exit(1);
}

const testArgs = [
  "test",
  "tests/Bwc.E2E/Bwc.E2E.csproj",
  "-c",
  "Release",
  `-p:Platform=${platform}`,
  "--nologo",
];
if (flavor === "e2e") {
  // Keep the test assembly on the same flavor as the app it drives, and keep
  // its bin/obj out of the shipping folders (see Directory.Build.props).
  testArgs.push("-p:BwcFlavor=E2E");
}
if (options.filter !== undefined) {
  testArgs.push("--filter", options.filter);
}

const childEnv = {
  ...process.env,
  BWC_PUBLISH_DIR: publishDir,
  BWC_RID: rid,
  BWC_FLAVOR: flavor,
  BWC_RUN_ID: runId,
};

console.log("");
console.log(`Running: ${dotnet} ${testArgs.join(" ")}`);
let testRun;
try {
  testRun = await runStreaming(dotnet, testArgs, { cwd: repoRoot, env: childEnv });
} catch (error) {
  console.error(`Could not start dotnet test: ${error.message}`);
  process.exit(1);
}

let result;
try {
  result = JSON.parse(readFileSync(resultPath, "utf8"));
} catch (error) {
  console.error(`Result artifact is missing or unreadable at ${resultPath}: ${error.message}`);
  console.error("Test output tail:");
  console.error(tail(testRun.output));
  process.exit(1);
}

printResult(result);

if (testRun.code !== 0) {
  console.error(`dotnet test exited with code ${testRun.code}.`);
  process.exit(testRun.code ?? 1);
}

if (result.pass !== true) {
  console.error("Result artifact is not marked pass.");
  process.exit(1);
}

process.exit(0);
