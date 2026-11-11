#!/usr/bin/env node
/**
 * Run the local quality gate in a fixed order and record the result.
 * Steps are sequential; the first failure stops the run so later steps cannot
 * hide the cause. The full mode adds the ship publish and E2E steps.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  checkPublishFlavor,
  formatFlavorReport,
  publishFolderName,
} from "./flavor-metadata.js";

const SCRIPT = "scripts/test-all.js";
const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const GATE_DIR = path.join(ROOT, "artifacts", "gate");
const ARTIFACT = path.join(GATE_DIR, "result.json");
const LOG_DIR = path.join(GATE_DIR, "logs");
const E2E_RESULT_DIR = path.join(ROOT, "artifacts", "e2e");
const ARTIFACT_SCHEMA = 2;
const ALL_RIDS = "all";
const ALL_RID_ORDER = ["win-arm64", "win-x64"];
const DOTNET_FALLBACK = "C:\\Program Files\\dotnet\\dotnet.exe";
const MAX_BUFFER = 256 * 1024 * 1024;
const STEP_TIMEOUT_MS = 30 * 60 * 1000;
const PROBE_TIMEOUT_MS = 60 * 1000;
const ANSI_PATTERN = /\x1B\[[0-?]*[ -/]*[@-~]/g;

const RID_PLATFORMS = new Map([
  ["win-x64", "x64"],
  ["win-arm64", "ARM64"],
]);

function defaultRid() {
  if (process.arch === "arm64") return "win-arm64";
  if (process.arch === "x64") return "win-x64";
  return null;
}

// The test projects always build and run for the host architecture, whatever --rid is.
// --rid only picks the app RID for the publish and E2E steps. An x64 app runs under emulation
// on ARM64 Windows, but the x64 test host cannot start there without an x64 runtime.
function hostPlatform() {
  if (process.arch === "arm64") return "ARM64";
  if (process.arch === "x64") return "x64";
  return null;
}

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--rid win-x64|win-arm64|all] [--quick] [--help]

Runs the quality gate steps in order and writes artifacts/gate/result.json
(schema 2). Each step's full stdout and stderr goes to
artifacts/gate/logs/<NN>-<step-name>.log; logs from earlier runs are removed first.
The default RID comes from process.arch, never PROCESSOR_ARCHITECTURE.

Steps: sync-version, registry-writer, no-em-dash, changelog-version, comment-lint,
build-arm64, build-x64, test-core, test-registry-e2e, test-registry-shipping.
Unless --quick, each RID then runs publish-ship, verify-flavor-ship, e2e-ship,
publish-e2e, verify-flavor-e2e, e2e-flavor. verify-flavor-* checks the BwcFlavor
metadata of the publish folder. E2E steps also record the counts from
artifacts/e2e/result-<rid>-<flavor>.json when that file exists.

Options:
  --rid <rid>  win-x64, win-arm64, or all. Default from process.arch.
               The three test projects always use the host architecture, so
               win-x64 on an ARM64 machine still runs them as ARM64. all runs
               the builds and the three test projects once, and the publish and
               E2E steps once for each RID.
  --quick      Run both architecture builds and all three test projects, but
               skip the publish, flavor, and E2E steps.
  --help       Show this help.

Exit codes: 0 every step passed, 1 a step failed, 2 argument error.`);
}

function parseArgs(argv) {
  const options = {
    rid: defaultRid(),
    quick: false,
    help: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--quick") {
      options.quick = true;
      continue;
    }
    if (arg === "--rid") {
      const value = argv[index + 1];
      if (value === undefined || value.startsWith("--")) {
        throw new Error("--rid requires a value");
      }
      options.rid = value;
      index += 1;
      continue;
    }
    if (arg.startsWith("--rid=")) {
      const value = arg.slice("--rid=".length);
      if (!value) {
        throw new Error("--rid requires a value");
      }
      options.rid = value;
      continue;
    }
    throw new Error(`unrecognized argument: ${arg}`);
  }

  if (options.help) return options;
  if (hostPlatform() === null) {
    throw new Error(
      `unsupported process.arch "${process.arch}"; the test projects need arm64 or x64`,
    );
  }
  if (options.rid === ALL_RIDS) return options;
  if (!options.rid) {
    throw new Error(
      `unsupported process.arch "${process.arch}"; pass --rid win-x64 or --rid win-arm64`,
    );
  }
  if (!RID_PLATFORMS.has(options.rid)) {
    throw new Error(
      `--rid must be one of ${[...RID_PLATFORMS.keys(), ALL_RIDS].join(", ")}; received ${options.rid}`,
    );
  }
  return options;
}

function nodeStep(name, script, args) {
  return {
    name,
    executable: process.execPath,
    args: [script, ...args],
    display: ["node", script, ...args].join(" "),
  };
}

function dotnetStep(name, args) {
  return {
    name,
    executable: "dotnet",
    args,
    display: ["dotnet", ...args].join(" "),
  };
}

// Runs in this process rather than a child, so its log holds the checker's report.
function flavorStep(rid, flavor) {
  const folder = publishFolderName(rid, flavor);
  return {
    name: `verify-flavor-${flavor}`,
    display: `checkPublishFlavor publish/${folder} ${flavor} (in process)`,
    run() {
      try {
        const report = checkPublishFlavor(
          path.join(ROOT, "publish", folder),
          flavor,
        );
        return {
          status: report.ok ? 0 : 1,
          stdout: `${formatFlavorReport(report).join("\n")}\n`,
          stderr: "",
          error: null,
          summary: report.ok
            ? `${report.checked.length} managed assembly(ies) carry BwcFlavor=${report.expected}`
            : report.problems[0],
        };
      } catch (error) {
        return {
          status: 1,
          stdout: "",
          stderr: "",
          error: null,
          summary: `flavor check threw: ${errorText(error)}`,
        };
      }
    },
  };
}

function e2eStep(name, rid, flavor) {
  return {
    ...nodeStep(name, "scripts/test-e2e.js", ["--rid", rid, "--flavor", flavor]),
    e2eResultPath: path.join(E2E_RESULT_DIR, `result-${rid}-${flavor}.json`),
  };
}

function sharedSteps(testPlatform) {
  return [
    nodeStep("sync-version", "scripts/sync-version.js", ["--check"]),
    nodeStep("registry-writer", "scripts/check-registry-writer.js", []),
    // Walk the working tree, not only git-tracked files, so new files are checked before commit.
    nodeStep("no-em-dash", "scripts/validate-no-em-dash.js", ["--root", ROOT]),
    nodeStep("changelog-version", "scripts/validate-changelog-version.js", []),
    nodeStep("comment-lint", "scripts/lint-comments.mjs", []),
    dotnetStep("build-arm64", [
      "build",
      "BetterWindowsConfig.slnx",
      "-c",
      "Release",
      "-p:Platform=ARM64",
      "--nologo",
    ]),
    dotnetStep("build-x64", [
      "build",
      "BetterWindowsConfig.slnx",
      "-c",
      "Release",
      "-p:Platform=x64",
      "--nologo",
    ]),
    {
      ...dotnetStep("test-core", [
        "test",
        "tests/Bwc.Core.Tests/Bwc.Core.Tests.csproj",
        "-c",
        "Release",
        `-p:Platform=${testPlatform}`,
        "--nologo",
      ]),
      project: "tests/Bwc.Core.Tests/Bwc.Core.Tests.csproj",
    },
    {
      ...dotnetStep("test-registry-e2e", [
        "test",
        "tests/Bwc.Registry.Tests/Bwc.Registry.Tests.csproj",
        "-c",
        "Release",
        `-p:Platform=${testPlatform}`,
        "-p:BwcFlavor=E2E",
        "--nologo",
      ]),
      project: "tests/Bwc.Registry.Tests/Bwc.Registry.Tests.csproj",
    },
    {
      ...dotnetStep("test-registry-shipping", [
        "test",
        "tests/Bwc.Registry.Shipping.Tests/Bwc.Registry.Shipping.Tests.csproj",
        "-c",
        "Release",
        `-p:Platform=${testPlatform}`,
        "--nologo",
      ]),
      project: "tests/Bwc.Registry.Shipping.Tests/Bwc.Registry.Shipping.Tests.csproj",
    },
  ].map((step) => ({ ...step, rid: null }));
}

function ridSteps(rid) {
  return [
    nodeStep("publish-ship", "scripts/dotnet-build.js", [
      "--rid",
      rid,
      "--flavor",
      "ship",
    ]),
    flavorStep(rid, "ship"),
    e2eStep("e2e-ship", rid, "ship"),
    nodeStep("publish-e2e", "scripts/dotnet-build.js", [
      "--rid",
      rid,
      "--flavor",
      "e2e",
    ]),
    flavorStep(rid, "e2e"),
    e2eStep("e2e-flavor", rid, "e2e"),
  ].map((step) => ({ ...step, rid }));
}

// Shared steps run once. Each RID then gets its own publish, flavor, and E2E steps.
function buildPlan(options) {
  const everyRid = options.rid === ALL_RIDS;
  const shared = sharedSteps(hostPlatform());
  const rids = (everyRid ? ALL_RID_ORDER : [options.rid]).map(
    (rid) => ({
      rid,
      platform: RID_PLATFORMS.get(rid),
      steps: options.quick ? [] : ridSteps(rid),
    }),
  );
  return {
    steps: [...shared, ...rids.flatMap((entry) => entry.steps)],
    rids: rids.map(({ rid, platform, steps }) => ({
      rid,
      platform,
      stepNames: steps.map((step) => step.name),
    })),
  };
}

function errorText(error) {
  return error instanceof Error ? error.message : String(error);
}

function toRepoPath(absolute) {
  return path.relative(ROOT, absolute).split(path.sep).join("/");
}

function stripAnsi(value) {
  return String(value || "").replace(ANSI_PATTERN, "");
}

function truncate(value, limit = 500) {
  const text = String(value || "");
  return text.length > limit ? `${text.slice(0, limit - 3)}...` : text;
}

function pickSummary(output) {
  const lines = stripAnsi(output)
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
  if (lines.length === 0) return "";

  const preferred = [
    /^Build succeeded/i,
    /^Build FAILED/i,
    /^Passed!/i,
    /^Failed!/i,
    /^(dotnet-build|flavor-metadata): (PASS|FAIL)\b/i,
    /\berror [A-Z]{2,}\d+/i,
    /\bwarning [A-Z]{2,}\d+/i,
    /\bfailed:\s*\d+/i,
  ];
  for (let index = lines.length - 1; index >= 0; index -= 1) {
    if (preferred.some((pattern) => pattern.test(lines[index]))) {
      return truncate(lines[index]);
    }
  }
  return truncate(lines[lines.length - 1]);
}

function spawnOptions() {
  return {
    cwd: ROOT,
    encoding: "utf8",
    shell: false,
    windowsHide: true,
    maxBuffer: MAX_BUFFER,
    timeout: STEP_TIMEOUT_MS,
    env: {
      ...process.env,
      // A lingering build server can hold the child's output pipe open after the command
      // itself finishes, which makes spawnSync look hung. Disable node reuse for the gate.
      MSBUILDDISABLENODEREUSE: "1",
      DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER: "1",
    },
  };
}

function printTail(output) {
  const lines = stripAnsi(output)
    .split(/\r?\n/)
    .filter((line) => line.trim().length > 0);
  for (const line of lines.slice(-40)) {
    console.error(`  ${line}`);
  }
}

function runGit(args) {
  const result = spawnSync("git", args, {
    cwd: ROOT,
    encoding: "utf8",
    shell: false,
    windowsHide: true,
    maxBuffer: MAX_BUFFER,
    timeout: PROBE_TIMEOUT_MS,
  });
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(
      `git ${args.join(" ")} exited with ${result.status}: ${String(result.stderr).trim()}`,
    );
  }
  return result.stdout;
}

// Parses `git status --porcelain -z`. Renames and copies are followed by their origin path.
function parseStatusZ(text) {
  const entries = text.split("\0");
  const paths = [];
  for (let index = 0; index < entries.length; index += 1) {
    const entry = entries[index];
    if (entry.length < 4) continue;
    const code = entry.slice(0, 2);
    const target = entry.slice(3);
    if (/[RC]/.test(code)) {
      const origin = entries[index + 1] ?? "";
      index += 1;
      paths.push(`${target} (from ${origin})`);
    } else {
      paths.push(target);
    }
  }
  return paths;
}

function collectGitState() {
  try {
    const commit = runGit(["rev-parse", "HEAD"]).trim();
    const branch = runGit(["rev-parse", "--abbrev-ref", "HEAD"]).trim();
    const dirtyPaths = parseStatusZ(runGit(["status", "--porcelain", "-z"]));
    return {
      commit,
      branch,
      dirty: dirtyPaths.length > 0,
      dirtyPaths,
      error: null,
    };
  } catch (error) {
    return {
      commit: null,
      branch: null,
      dirty: null,
      dirtyPaths: [],
      error: errorText(error),
    };
  }
}

function probeDotnetVersion() {
  const options = {
    encoding: "utf8",
    shell: false,
    windowsHide: true,
    timeout: PROBE_TIMEOUT_MS,
  };
  let result = spawnSync("dotnet", ["--version"], options);
  if (
    result.error &&
    result.error.code === "ENOENT" &&
    fs.existsSync(DOTNET_FALLBACK)
  ) {
    result = spawnSync(DOTNET_FALLBACK, ["--version"], options);
  }
  if (result.error) {
    return { version: null, error: errorText(result.error) };
  }
  if (result.status !== 0) {
    return { version: null, error: `exited with ${result.status}` };
  }
  return { version: String(result.stdout).trim() || null, error: null };
}

function collectEnvironment() {
  return {
    node: process.version,
    dotnet: probeDotnetVersion(),
    os: {
      type: os.type(),
      version: os.version(),
      release: os.release(),
    },
    arch: process.arch,
  };
}

function isCount(value) {
  return Number.isInteger(value) && value >= 0;
}

// Reads counts from an E2E result file. Explicit passed/failed/total fields win over
// a checks array, which counts each entry with pass === true as passed.
function readE2eResult(filePath) {
  const result = {
    path: toRepoPath(filePath),
    exists: fs.existsSync(filePath),
    pass: null,
    passed: null,
    failed: null,
    total: null,
    error: null,
  };
  if (!result.exists) {
    return result;
  }
  try {
    const data = JSON.parse(fs.readFileSync(filePath, "utf8"));
    if (typeof data.pass === "boolean") {
      result.pass = data.pass;
    }
    if (isCount(data.total) && isCount(data.passed) && isCount(data.failed)) {
      result.total = data.total;
      result.passed = data.passed;
      result.failed = data.failed;
    } else if (Array.isArray(data.checks)) {
      result.total = data.checks.length;
      result.passed = data.checks.filter((check) => check?.pass === true).length;
      result.failed = result.total - result.passed;
    } else {
      result.error = "no checks array or passed/failed/total counts";
    }
  } catch (error) {
    result.error = `unreadable: ${errorText(error)}`;
  }
  return result;
}

function executeStep(step) {
  if (step.run) {
    return step.run();
  }
  if (step.project && !fs.existsSync(path.join(ROOT, step.project))) {
    return {
      status: 1,
      stdout: "",
      stderr: "",
      error: null,
      summary: `test project not found: ${step.project}`,
    };
  }
  if (step.e2eResultPath) {
    // The gate owns this run, so a result file left by an earlier run must not count.
    fs.rmSync(step.e2eResultPath, { force: true });
  }

  let result = spawnSync(step.executable, step.args, spawnOptions());
  if (
    result.error &&
    result.error.code === "ENOENT" &&
    step.executable === "dotnet" &&
    fs.existsSync(DOTNET_FALLBACK)
  ) {
    result = spawnSync(DOTNET_FALLBACK, step.args, spawnOptions());
  }
  return {
    status: typeof result.status === "number" ? result.status : null,
    stdout: String(result.stdout ?? ""),
    stderr: String(result.stderr ?? ""),
    error: result.error ?? null,
    summary: null,
  };
}

function withTrailingNewline(text) {
  return text.length === 0 || text.endsWith("\n") ? text : `${text}\n`;
}

function runStep(step, number) {
  const startedMs = Date.now();
  const startedUtc = new Date(startedMs).toISOString();
  const logPath = path.join(
    LOG_DIR,
    `${String(number).padStart(2, "0")}-${step.name}.log`,
  );

  let outcome;
  try {
    outcome = executeStep(step);
  } catch (error) {
    outcome = {
      status: null,
      stdout: "",
      stderr: "",
      error: null,
      summary: `step could not run: ${errorText(error)}`,
    };
  }
  const durationMs = Date.now() - startedMs;
  const pass = !outcome.error && outcome.status === 0;

  const output = [stripAnsi(outcome.stdout), stripAnsi(outcome.stderr)]
    .filter((part) => part.length > 0)
    .join("\n");

  let summary = outcome.summary;
  if (!summary && outcome.error) {
    summary =
      outcome.error.code === "ETIMEDOUT"
        ? `timed out after ${STEP_TIMEOUT_MS} ms`
        : `could not start ${step.executable}: ${outcome.error.message}`;
  }
  if (!summary) {
    summary = pickSummary(output) || (pass ? "ok" : "no output");
  }

  const logText = [
    `# gate step ${number}: ${step.name}`,
    `# rid: ${step.rid ?? "(shared)"}`,
    `# command: ${step.display}`,
    `# started: ${startedUtc}`,
    "# ----- stdout -----",
    withTrailingNewline(outcome.stdout),
    "# ----- stderr -----",
    withTrailingNewline(outcome.stderr),
    "# ----- result -----",
    `exitCode: ${outcome.status ?? "null"}`,
    `error: ${outcome.error ? `${outcome.error.code ?? "error"}: ${outcome.error.message}` : "none"}`,
    `durationMs: ${durationMs}`,
    `pass: ${pass}`,
    "",
  ].join("\n");

  let logError = null;
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    fs.writeFileSync(logPath, logText, "utf8");
  } catch (error) {
    logError = errorText(error);
  }

  return {
    name: step.name,
    rid: step.rid ?? null,
    command: step.display,
    pass: pass && logError === null,
    exitCode: outcome.status ?? null,
    durationMs,
    summary: logError === null ? summary : `could not write log: ${logError}`,
    log: toRepoPath(logPath),
    e2eResult: step.e2eResultPath ? readE2eResult(step.e2eResultPath) : null,
    output,
  };
}

// A RID is "not-run" when fail-fast stopped before its steps finished, and "skipped" in quick mode.
function summarizeRids(plan, results, quick) {
  return plan.rids.map(({ rid, platform, stepNames }) => {
    const executed = results.filter((result) => result.rid === rid);
    let status = "pass";
    if (quick) {
      status = "skipped";
    } else if (executed.some((result) => !result.pass)) {
      status = "fail";
    } else if (executed.length < stepNames.length) {
      status = "not-run";
    }
    return { rid, platform, status, steps: stepNames };
  });
}

function writeArtifact(artifact) {
  fs.mkdirSync(GATE_DIR, { recursive: true });
  fs.writeFileSync(ARTIFACT, `${JSON.stringify(artifact, null, 2)}\n`, "utf8");
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(`test-all: ${errorText(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return 2;
  }

  if (options.help) {
    printHelp();
    return 0;
  }

  const plan = buildPlan(options);
  const testPlatform = hostPlatform();
  const startedMs = Date.now();
  const startedUtc = new Date(startedMs).toISOString();

  try {
    // Remove the previous outputs first, so a run that stops early cannot leave a passing artifact behind.
    fs.rmSync(ARTIFACT, { force: true });
    fs.rmSync(LOG_DIR, { recursive: true, force: true });
    fs.mkdirSync(LOG_DIR, { recursive: true });
  } catch (error) {
    console.error(
      `test-all: could not reset ${toRepoPath(GATE_DIR)}: ${errorText(error)}`,
    );
    return 1;
  }

  const git = collectGitState();
  const environment = collectEnvironment();
  const results = [];
  let pass = true;

  console.log(
    `test-all: rid=${options.rid} test-platform=${testPlatform} mode=${
      options.quick ? "quick" : "full"
    }`,
  );

  for (let index = 0; index < plan.steps.length; index += 1) {
    const result = runStep(plan.steps[index], index + 1);
    results.push(result);
    const label = result.rid ? `${result.name} (${result.rid})` : result.name;
    const status = result.pass ? "PASS" : "FAIL";
    console.log(
      `[${index + 1}/${plan.steps.length}] ${label}: ${status} (${result.durationMs} ms)`,
    );

    if (!result.pass) {
      console.error(`  summary: ${result.summary}`);
      console.error(`  log: ${result.log}`);
      printTail(result.output);
      pass = false;
      break;
    }
  }

  const durationMs = Date.now() - startedMs;
  const artifact = {
    schema: ARTIFACT_SCHEMA,
    startedUtc,
    durationMs,
    rid: options.rid,
    testPlatform,
    quick: options.quick,
    pass,
    git,
    environment,
    rids: summarizeRids(plan, results, options.quick),
    steps: results.map((result) => ({
      name: result.name,
      rid: result.rid,
      command: result.command,
      pass: result.pass,
      exitCode: result.exitCode,
      durationMs: result.durationMs,
      summary: result.summary,
      log: result.log,
      e2eResult: result.e2eResult,
    })),
  };

  try {
    writeArtifact(artifact);
  } catch (error) {
    console.error(
      `test-all: could not write ${toRepoPath(ARTIFACT)}: ${errorText(error)}`,
    );
    return 1;
  }

  console.log(
    `test-all: ${pass ? "PASS" : "FAIL"} (${results.length}/${plan.steps.length} steps, ${durationMs} ms)`,
  );
  console.log("artifact: artifacts/gate/result.json");
  console.log("logs: artifacts/gate/logs/");
  return pass ? 0 : 1;
}

process.exitCode = main();
