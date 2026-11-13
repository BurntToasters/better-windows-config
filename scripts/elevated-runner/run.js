#!/usr/bin/env node
/**
 * Start the "BWC Elevated E2E" scheduled task and wait for its result. Run this from a normal
 * (non-elevated) shell. Starting a task with schtasks /Run does not show a UAC prompt. The
 * elevated work happens inside the task, which runs scripts/elevated-runner/run-elevated.ps1.
 *
 * The task runs the checkout recorded by install.ps1. This script refuses when that is not the
 * checkout it lives in, so a run never reports results from a different tree.
 *
 * Exit codes: 0 pass, 1 fail, 2 not set up or refused before the tests ran, 3 not run.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isDirectExecutionOf } from "../direct-execution.mjs";
import {
  checkPublishFlavor,
  formatFlavorReport,
  publishFolderName,
} from "../flavor-metadata.js";

const SCRIPT = "scripts/elevated-runner/run.js";
const TASK_NAME = "BWC Elevated E2E";
const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const RUNNER_ROOT = path.join(process.env.ProgramData ?? "C:\\ProgramData", "BwcElevatedRunner");
const CONFIG_PATH = path.join(RUNNER_ROOT, "config.json");
const LAST_RUN_PATH = path.join(RUNNER_ROOT, "last-run.txt");
const POWERSHELL = path.join(
  process.env.SystemRoot ?? "C:\\Windows",
  "System32",
  "WindowsPowerShell",
  "v1.0",
  "powershell.exe",
);
const POLL_MS = 3000;
// The task must reach Running (or write a record) within this window after schtasks /Run.
const START_GRACE_MS = 120_000;
const TIMEOUT_MS = 30 * 60_000;
const EXIT_PASS = 0;
const EXIT_FAIL = 1;
const EXIT_SETUP = 2;
const EXIT_NOT_RUN = 3;

const INSTALL_COMMAND = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\elevated-runner\\install.ps1";

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT}

Starts the scheduled task "${TASK_NAME}", waits for it to finish, and prints the
result. Run it from a normal shell, not an elevated one. It takes no arguments.

Setup (once, from an elevated PowerShell, in this checkout):
  ${INSTALL_COMMAND}
  ${INSTALL_COMMAND} -Enable   (the task is installed disabled)

Exit codes: 0 pass, 1 fail, 2 not set up or refused, 3 not run.`);
}

function hostRid() {
  if (process.arch === "arm64") return "win-arm64";
  if (process.arch === "x64") return "win-x64";
  return null;
}

function runPowerShell(script) {
  const encoded = Buffer.from(script, "utf16le").toString("base64");
  return spawnSync(POWERSHELL, ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 60_000,
  });
}

// "MISSING", a task State such as Ready, Running, or Disabled, or null when the query failed.
function taskState() {
  const result = runPowerShell(
    `$t = Get-ScheduledTask -TaskName '${TASK_NAME}' -ErrorAction SilentlyContinue; ` +
      "if ($null -eq $t) { 'MISSING' } else { [string]$t.State }",
  );
  if (result.error || result.status !== 0) return null;
  const state = String(result.stdout ?? "").trim();
  return state === "" ? null : state;
}

function startTask() {
  return spawnSync("schtasks.exe", ["/Run", "/TN", TASK_NAME], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 60_000,
  });
}

function readJsonFile(filePath) {
  const text = fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, "");
  return JSON.parse(text);
}

// last-run.txt holds key=value lines written by run-elevated.ps1.
function readLastRun() {
  let text;
  try {
    text = fs.readFileSync(LAST_RUN_PATH, "utf8").replace(/^\uFEFF/, "");
  } catch {
    return null;
  }
  const record = {};
  for (const line of text.split(/\r?\n/)) {
    const equals = line.indexOf("=");
    if (equals > 0) {
      record[line.slice(0, equals)] = line.slice(equals + 1);
    }
  }
  return record.runId ? record : null;
}

function sameDirectory(a, b) {
  return path.normalize(a).toLowerCase() === path.normalize(b).toLowerCase();
}

function printArtifactSummary(artifactPath) {
  if (!artifactPath || !fs.existsSync(artifactPath)) {
    console.log("result artifact: none was written");
    return;
  }
  console.log(`result artifact: ${artifactPath}`);
  let data;
  try {
    data = readJsonFile(artifactPath);
  } catch (error) {
    console.log(`  could not read the artifact: ${error.message}`);
    return;
  }
  const checks = Array.isArray(data.checks) ? data.checks : [];
  const passed = checks.filter((check) => check?.pass === true).length;
  console.log(
    `  E2E ${data.pass === true ? "PASS" : "FAIL"}: ${passed} passed, ${checks.length - passed} failed, ${checks.length} total`,
  );
  for (const check of checks.filter((item) => item?.pass !== true)) {
    console.log(`  failed: ${check.id}: ${String(check.notes ?? "").replace(/\r?\n/g, " ")}`);
  }
}

function reportRecord(record) {
  const status = record.status;
  const headline = {
    pass: "PASS",
    fail: "FAIL",
    refused: "REFUSED (no tests ran)",
    "not-run": "NOT RUN",
  }[status] ?? `UNKNOWN (${status})`;

  console.log("");
  console.log(`elevated E2E: ${headline}`);
  console.log(`runId: ${record.runId}`);
  console.log(`rid: ${record.rid || "(none)"}  repo: ${record.repo || "(none)"}`);
  console.log(`message: ${record.message || "(none)"}`);
  console.log(`log: ${record.log || "(none)"}`);
  printArtifactSummary(record.resultArtifact);

  const exitCodes = { pass: EXIT_PASS, fail: EXIT_FAIL, refused: EXIT_SETUP, "not-run": EXIT_NOT_RUN };
  return exitCodes[status] ?? EXIT_FAIL;
}

function printInstallHelp(reason) {
  console.error(`NOT RUN: ${reason}`);
  console.error("Install it once from an elevated PowerShell in this checkout:");
  console.error(`  ${INSTALL_COMMAND}`);
  console.error("Enable it for a run, and disable it afterwards:");
  console.error(`  ${INSTALL_COMMAND} -Enable`);
  console.error(`  ${INSTALL_COMMAND} -Disable`);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitForResult(startedRunId) {
  const begin = Date.now();
  let sawRunning = false;

  for (;;) {
    await sleep(POLL_MS);
    const record = readLastRun();
    const fresh = record !== null && record.runId !== startedRunId;
    const state = taskState();
    if (state === "Running") {
      sawRunning = true;
    }

    if (fresh && record.status !== "running") {
      return reportRecord(record);
    }

    const elapsed = Date.now() - begin;
    if (fresh && state !== null && state !== "Running") {
      console.error("NOT RUN: the task stopped without writing a final result.");
      console.error(`See the log: ${record.log || LAST_RUN_PATH}`);
      return EXIT_NOT_RUN;
    }
    if (!fresh && !sawRunning && state !== null && state !== "Running" && elapsed > START_GRACE_MS) {
      console.error(`NOT RUN: the task did not start (state: ${state}).`);
      console.error("Check that you are logged on at the desktop, then the task history in Task Scheduler.");
      return EXIT_NOT_RUN;
    }
    if (elapsed > TIMEOUT_MS) {
      console.error(`NOT RUN: no result after ${Math.round(TIMEOUT_MS / 60_000)} minutes.`);
      console.error(`The task may still be running. Check later in ${LAST_RUN_PATH}`);
      return EXIT_NOT_RUN;
    }
  }
}

async function main(argv) {
  if (argv.includes("--help") || argv.includes("-h")) {
    printHelp();
    return EXIT_PASS;
  }
  if (argv.length > 0) {
    console.error(`${SCRIPT} takes no arguments: ${argv.join(" ")}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT_SETUP;
  }
  if (process.platform !== "win32") {
    console.error("The elevated runner is Windows only.");
    return EXIT_SETUP;
  }
  const rid = hostRid();
  if (rid === null) {
    console.error(`Unsupported process.arch "${process.arch}"; the runner needs arm64 or x64.`);
    return EXIT_SETUP;
  }

  const state = taskState();
  if (state === null) {
    console.error("NOT RUN: could not query Task Scheduler through PowerShell.");
    return EXIT_SETUP;
  }
  if (state === "MISSING") {
    printInstallHelp(`the task "${TASK_NAME}" is not installed.`);
    return EXIT_SETUP;
  }
  if (state === "Disabled") {
    printInstallHelp(`the task "${TASK_NAME}" is disabled.`);
    return EXIT_SETUP;
  }

  if (!fs.existsSync(CONFIG_PATH)) {
    printInstallHelp(`${CONFIG_PATH} is missing, so the task was not installed by install.ps1.`);
    return EXIT_SETUP;
  }
  let config;
  try {
    config = readJsonFile(CONFIG_PATH);
  } catch (error) {
    console.error(`NOT RUN: ${CONFIG_PATH} is unreadable: ${error.message}`);
    return EXIT_SETUP;
  }
  const recordedRepo = typeof config.repo === "string" ? config.repo : "";
  if (recordedRepo === "" || !sameDirectory(recordedRepo, REPO_ROOT)) {
    console.error(`NOT RUN: the task runs the checkout ${recordedRepo || "(none recorded)"}.`);
    console.error(`This checkout is ${REPO_ROOT}.`);
    console.error("Run install.ps1 from this checkout in an elevated shell to point the task here.");
    return EXIT_SETUP;
  }

  // Check the publish folder before the elevated run, so a stale or missing build fails here.
  const publish = checkPublishFlavor(
    path.join(REPO_ROOT, "publish", publishFolderName(rid, "e2e")),
    "e2e",
  );
  if (!publish.ok) {
    for (const line of formatFlavorReport(publish)) {
      console.error(line);
    }
    console.error(`NOT RUN: build the E2E publish first: node scripts/dotnet-build.js --rid ${rid} --flavor e2e`);
    return EXIT_SETUP;
  }

  const previous = readLastRun();
  const startedRunId = previous?.runId ?? "";
  const started = startTask();
  if (started.error || started.status !== 0) {
    const output = `${started.stdout ?? ""}${started.stderr ?? ""}`.trim();
    console.error(`NOT RUN: schtasks /Run "${TASK_NAME}" failed${output ? `: ${output}` : "."}`);
    return EXIT_SETUP;
  }
  console.log(`started the task "${TASK_NAME}" for ${REPO_ROOT} (rid ${rid}); waiting for the result...`);

  return waitForResult(startedRunId);
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = await main(process.argv.slice(2));
}
