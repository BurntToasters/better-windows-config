#!/usr/bin/env node
/**
 * Run the local quality gate in a fixed order and record the result.
 * Steps are sequential; the first failure stops the run so later steps cannot
 * hide the cause. The full mode adds the ship publish and E2E steps.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT = "scripts/test-all.js";
const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const ARTIFACT = path.join(ROOT, "artifacts", "gate", "result.json");
const DOTNET_FALLBACK = "C:\\Program Files\\dotnet\\dotnet.exe";
const MAX_BUFFER = 256 * 1024 * 1024;
const STEP_TIMEOUT_MS = 30 * 60 * 1000;
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

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--rid win-x64|win-arm64] [--quick] [--help]

Runs the quality gate steps in order and writes artifacts/gate/result.json.
The default RID comes from process.arch, never PROCESSOR_ARCHITECTURE.

Options:
  --rid <rid>  Target runtime: win-x64 or win-arm64. Default from process.arch.
  --quick      Run both architecture builds and all three test projects, but
               skip the ship publish and E2E steps.
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
  if (!options.rid) {
    throw new Error(
      `unsupported process.arch "${process.arch}"; pass --rid win-x64 or --rid win-arm64`,
    );
  }
  if (!RID_PLATFORMS.has(options.rid)) {
    throw new Error(
      `--rid must be one of ${[...RID_PLATFORMS.keys()].join(", ")}; received ${options.rid}`,
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

function buildSteps(options) {
  const platform = RID_PLATFORMS.get(options.rid);
  const steps = [
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
        `-p:Platform=${platform}`,
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
        `-p:Platform=${platform}`,
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
        `-p:Platform=${platform}`,
        "--nologo",
      ]),
      project: "tests/Bwc.Registry.Shipping.Tests/Bwc.Registry.Shipping.Tests.csproj",
    },
  ];

  if (!options.quick) {
    steps.push(
      nodeStep("publish-ship", "scripts/dotnet-build.js", [
        "--rid",
        options.rid,
        "--flavor",
        "ship",
      ]),
      nodeStep("e2e-ship", "scripts/test-e2e.js", [
        "--rid",
        options.rid,
        "--flavor",
        "ship",
      ]),
      nodeStep("publish-e2e", "scripts/dotnet-build.js", [
        "--rid",
        options.rid,
        "--flavor",
        "e2e",
      ]),
      nodeStep("e2e-flavor", "scripts/test-e2e.js", [
        "--rid",
        options.rid,
        "--flavor",
        "e2e",
      ]),
    );
  }

  return steps;
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
    env: process.env,
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

function runStep(step) {
  const startedMs = Date.now();

  if (step.project) {
    const projectPath = path.join(ROOT, step.project);
    if (!fs.existsSync(projectPath)) {
      return {
        name: step.name,
        command: step.display,
        exitCode: 1,
        durationMs: Date.now() - startedMs,
        summary: `test project not found: ${step.project}`,
        output: "",
        pass: false,
      };
    }
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

  const output = [
    stripAnsi(result.stdout),
    stripAnsi(result.stderr),
  ]
    .filter((part) => part.length > 0)
    .join("\n");

  let summary = pickSummary(output);
  if (result.error) {
    summary =
      result.error.code === "ETIMEDOUT"
        ? `timed out after ${STEP_TIMEOUT_MS} ms`
        : `could not start ${step.executable}: ${result.error.message}`;
  } else if (!summary) {
    summary = result.status === 0 ? "ok" : "no output";
  }

  const pass = !result.error && result.status === 0;
  return {
    name: step.name,
    command: step.display,
    exitCode: typeof result.status === "number" ? result.status : null,
    durationMs: Date.now() - startedMs,
    summary,
    output,
    pass,
  };
}

function writeArtifact(artifact) {
  fs.mkdirSync(path.dirname(ARTIFACT), { recursive: true });
  fs.writeFileSync(ARTIFACT, `${JSON.stringify(artifact, null, 2)}\n`, "utf8");
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(
      `test-all: ${error instanceof Error ? error.message : String(error)}`,
    );
    console.error(`Try: node ${SCRIPT} --help`);
    return 2;
  }

  if (options.help) {
    printHelp();
    return 0;
  }

  const platform = RID_PLATFORMS.get(options.rid);
  const startedMs = Date.now();
  const startedUtc = new Date(startedMs).toISOString();
  const steps = buildSteps(options);
  const results = [];
  let pass = true;

  console.log(
    `test-all: rid=${options.rid} platform=${platform} mode=${
      options.quick ? "quick" : "full"
    }`,
  );

  for (let index = 0; index < steps.length; index += 1) {
    const result = runStep(steps[index]);
    results.push(result);
    const status = result.pass ? "PASS" : "FAIL";
    console.log(
      `[${index + 1}/${steps.length}] ${result.name}: ${status} (${result.durationMs} ms)`,
    );

    if (!result.pass) {
      console.error(`  summary: ${result.summary}`);
      printTail(result.output);
      pass = false;
      break;
    }
  }

  const durationMs = Date.now() - startedMs;
  const artifact = {
    schema: 1,
    startedUtc,
    durationMs,
    rid: options.rid,
    quick: options.quick,
    pass,
    steps: results.map((result) => ({
      name: result.name,
      command: result.command,
      exitCode: result.exitCode,
      durationMs: result.durationMs,
      summary: result.summary,
    })),
  };

  try {
    writeArtifact(artifact);
  } catch (error) {
    console.error(
      `test-all: could not write ${ARTIFACT}: ${
        error instanceof Error ? error.message : String(error)
      }`,
    );
    return 1;
  }

  console.log(
    `test-all: ${pass ? "PASS" : "FAIL"} (${results.length}/${steps.length} steps, ${durationMs} ms)`,
  );
  console.log("artifact: artifacts/gate/result.json");
  return pass ? 0 : 1;
}

process.exitCode = main();
