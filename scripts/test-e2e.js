#!/usr/bin/env node
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, readFileSync, rmSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { elevationState } from "./elevated-runner/elevation.mjs";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, "..");
const artifactsDir = path.join(repoRoot, "artifacts", "e2e");
const dotnetFallback = "C:\\Program Files\\dotnet\\dotnet.exe";
const NOT_RUN_EXIT_CODE = 3;
// dotnet test exits with this code when the filter selects no tests.
const DOTNET_ZERO_TESTS_EXIT_CODE = 8;
// The ship publish writes the real registry (ShippingWritePolicy), so it only runs tests that are
// neither mutating, e2e-only, nor elevated. A user --filter can narrow this further but never widen it.
const SHIP_FILTER = "TestCategory!=Mutating&TestCategory!=E2EOnly&TestCategory!=Elevated";
// Normal e2e runs skip Elevated tests so they never need an administrator token.
const E2E_FILTER = "TestCategory!=Elevated";
// A filter that names the Elevated category selects the elevated run. That run needs an elevated
// process, and it is never combined with the exclusion above.
const ELEVATED_SELECTOR = /TestCategory=Elevated(?![A-Za-z0-9_])/;

const usage = `Usage: node scripts/test-e2e.js [options]

Runs the Bwc.E2E test project against a publish folder. The test project always
builds and runs for the host architecture (process.arch). --rid only picks the
app: its publish folder and BWC_RID. An x64 app runs on an ARM64 machine under
emulation, so --rid win-x64 works there. An ARM64 app cannot run on x64 Windows.

Options:
  --rid win-x64|win-arm64   App runtime identifier (default from process.arch)
  --flavor ship|e2e         Publish flavor (default e2e)
  --build                   Run scripts/dotnet-build.js first
  --filter <pattern>        Pass a filter to dotnet test. With --flavor ship the
                            filter is combined with ${SHIP_FILTER}. Otherwise it is
                            combined with ${E2E_FILTER}, unless it names
                            TestCategory=Elevated.
  --help                    Show this help

Elevated tests: --filter TestCategory=Elevated runs only the tests tagged Elevated.
It needs --flavor e2e and an elevated process. Start it through
scripts/elevated-runner/run.js, not from an elevated shell by hand. A process that is
not elevated, or a run with no Elevated tests, exits ${NOT_RUN_EXIT_CODE} and reports NOT RUN.

Environment:
  BWC_PUBLISH_DIR           Override the publish folder (relative to the repo root)

Output: artifacts/e2e/result-<rid>-<flavor>.json
Exit codes: 0 pass, 1 fail, 2 usage error, ${NOT_RUN_EXIT_CODE} not run.
`;

function resultPathFor(rid, flavor) {
  return path.join(artifactsDir, `result-${rid}-${flavor}.json`);
}

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

// The test project runs on the host architecture, whatever the app rid is.
function hostPlatform() {
  if (process.arch === "arm64") {
    return "ARM64";
  }

  if (process.arch === "x64") {
    return "x64";
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

// Known gaps are listed in this order. Each one cites a work package from the parity plan.
const knownGapOrder = ["E1", "E2", "RF1", "RF4", "RF8", "UF7"];

function printResult(result) {
  const checks = Array.isArray(result.checks) ? result.checks : [];
  const notes = Array.isArray(result.notes) ? result.notes : [];
  const knownGaps = Array.isArray(result.knownGaps) ? result.knownGaps : [];
  const passed = checks.filter((check) => check.pass === true);
  const failed = checks.filter((check) => check.pass !== true);

  console.log("");
  console.log(`E2E ${result.pass === true ? "PASS" : "FAIL"}`);
  console.log(`runId: ${result.runId}`);
  console.log(`rid: ${result.rid}  flavor: ${result.flavor}`);
  console.log(`publishDir: ${result.publishDir}`);
  console.log(`checks: ${passed.length} passed, ${failed.length} failed, ${checks.length} total`);
  console.log(`known gaps: ${knownGaps.length} (documented differences from the oracle; not passes, do not fail the run)`);
  for (const workPackage of groupedWorkPackages(knownGaps)) {
    const gaps = knownGaps.filter((gap) => gap.workPackage === workPackage);
    console.log(`  ${workPackage}: ${gaps.length}`);
    for (const gap of gaps) {
      console.log(`    - ${gap.id}: ${String(gap.observed ?? "").replace(/\r?\n/g, " ")}`);
    }
  }
  console.log(`notes: ${notes.length} (information only)`);

  if (failed.length > 0) {
    console.log("failed checks:");
    for (const check of failed) {
      console.log(`  - ${check.id}: ${String(check.notes ?? "").replace(/\r?\n/g, " ")}`);
    }
  }

  console.log(`artifact: ${resultPath}`);
}

function groupedWorkPackages(knownGaps) {
  const present = new Set(knownGaps.map((gap) => String(gap.workPackage)));
  const known = knownGapOrder.filter((workPackage) => present.has(workPackage));
  const unknown = [...present].filter((workPackage) => !knownGapOrder.includes(workPackage)).sort();
  return [...known, ...unknown];
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

const testHost = hostPlatform();
if (testHost === undefined) {
  console.error(`Unsupported process.arch '${process.arch}'. The test host needs arm64 or x64.`);
  console.error(usage);
  process.exit(2);
}

const resultPath = resultPathFor(rid, flavor);
const defaultFolder = flavor === "e2e" ? `${rid}-e2e` : rid;
const configuredPublish = process.env.BWC_PUBLISH_DIR?.trim();
const publishDir = configuredPublish
  ? path.resolve(repoRoot, configuredPublish)
  : path.join(repoRoot, "publish", defaultFolder);

const runId = `${stamp(new Date())}-${randomBytes(4).toString("hex")}`;

// Elevated tests never run in a process that is not elevated. They report NOT RUN instead.
const elevatedRun = options.filter !== undefined && ELEVATED_SELECTOR.test(options.filter);
if (elevatedRun && flavor !== "e2e") {
  console.error("Elevated tests run only with --flavor e2e.");
  process.exit(2);
}
if (elevatedRun) {
  const elevation = elevationState();
  if (!elevation.known) {
    console.error(`NOT RUN: could not check whether this process is elevated (${elevation.detail}).`);
    process.exit(NOT_RUN_EXIT_CODE);
  }
  if (!elevation.elevated) {
    console.error("NOT RUN: this process is not elevated, so the Elevated E2E tests did not run.");
    console.error("Start them with: node scripts/elevated-runner/run.js");
    process.exit(NOT_RUN_EXIT_CODE);
  }
}

console.log(`E2E rid=${rid} testHost=${testHost} flavor=${flavor}`);
console.log(`runId=${runId}`);
console.log(`publishDir=${publishDir}`);
if (elevatedRun) {
  console.log("elevated: yes (TestCategory=Elevated run)");
}

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
  `-p:Platform=${testHost}`,
  "--nologo",
];
if (flavor === "e2e") {
  // Keep the test assembly on the same flavor as the app it drives, and keep
  // its bin/obj out of the shipping folders (see Directory.Build.props).
  testArgs.push("-p:BwcFlavor=E2E");
}
let filter = options.filter;
if (flavor === "ship") {
  filter = options.filter === undefined
    ? SHIP_FILTER
    : `(${options.filter})&${SHIP_FILTER}`;
} else if (!elevatedRun) {
  filter = options.filter === undefined
    ? E2E_FILTER
    : `(${options.filter})&${E2E_FILTER}`;
}
if (filter !== undefined) {
  testArgs.push("--filter", filter);
}
console.log(`filter: ${filter ?? "(none)"}`);

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

if (elevatedRun && testRun.code === DOTNET_ZERO_TESTS_EXIT_CODE) {
  console.error("NOT RUN: no E2E test is tagged TestCategory=Elevated, so no elevated test ran.");
  process.exit(NOT_RUN_EXIT_CODE);
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

if (result.schema !== 2) {
  console.error(`Result artifact schema is ${JSON.stringify(result.schema)}, expected 2.`);
  process.exit(1);
}

printResult(result);

if (testRun.code !== 0) {
  console.error(`dotnet test exited with code ${testRun.code}.`);
  process.exit(testRun.code ?? 1);
}

if (!Array.isArray(result.checks) || result.checks.length === 0) {
  console.error("Result artifact has no checks. A run with no checks is not evidence.");
  process.exit(1);
}

if (result.pass !== true) {
  console.error("Result artifact is not marked pass.");
  process.exit(1);
}

process.exit(0);
