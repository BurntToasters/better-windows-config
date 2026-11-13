#!/usr/bin/env node
/**
 * Regenerates the classic regedit oracle on this Windows desktop.
 *
 * Steps, in order:
 *   1. Build tools/Bwc.Oracle.Resources and tools/Bwc.Oracle.Capture (Release, host platform).
 *   2. resources: dump C:\Windows\regedit.exe and its en-US MUI into docs/parity/oracle (untracked).
 *   3. One capture per command: export, metrics, dialogs, menus, import, find, sort (tracked numbers
 *      and goldens; screenshots and control text are untracked).
 *   4. cleanup: delete HKCU\Software\BWC-Oracle if anything left it behind.
 *
 * Usage:
 *   npm run oracle
 *   npm run oracle -- --only dialogs
 *   npm run oracle -- --only metrics,menus --skip-build
 *   npm run oracle -- --list
 *
 * Safety rules, enforced by the tools this script runs (see docs/failure-modes/oracle.md):
 *   - Classic regedit is started only by the capture tool, with __COMPAT_LAYER=RunAsInvoker set on the
 *     child process only, so no UAC prompt can appear.
 *   - Each capture runs under the named mutex Local\BwcGuiTestLock (the same mutex as the E2E tests).
 *     The mutex is taken inside the tool, per command, and is not held by this script, because a
 *     nested acquire in a child process would wait on its parent forever.
 *   - Regedit's Applets\Regedit state is snapshotted before each command and restored exactly after it.
 *   - Writes go only under HKCU\Software\BWC-Oracle, plus the Applets\Regedit restore.
 *
 * The run record (step names, exit codes, timings) is written to artifacts/oracle/last-run.json.
 * Tracked outputs carry no timestamps, so a second run produces identical tracked files.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT = "scripts/oracle.js";
const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const CAPTURES = ["export", "metrics", "dialogs", "menus", "import", "find", "sort", "permissions"];
const ALL_STEPS = ["resources", ...CAPTURES];
const RUN_RECORD = path.join(ROOT, "artifacts", "oracle", "last-run.json");

const RESOURCES_PROJECT = "tools/Bwc.Oracle.Resources/Bwc.Oracle.Resources.csproj";
const CAPTURE_PROJECT = "tools/Bwc.Oracle.Capture/Bwc.Oracle.Capture.csproj";

function printHelp() {
  console.log(`Usage: node ${SCRIPT} [--only <step>[,<step>...]] [--skip-build] [--list]

Steps: ${ALL_STEPS.join(", ")}
  --only <steps>  run only these steps (comma separated or repeated)
  --skip-build    use the existing tool binaries
  --list          print the steps and exit`);
}

function parseArgs(argv) {
  const options = { only: null, skipBuild: false, list: false, help: false };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
    } else if (arg === "--list") {
      options.list = true;
    } else if (arg === "--skip-build") {
      options.skipBuild = true;
    } else if (arg === "--only") {
      const value = argv[i + 1];
      if (!value) {
        throw new Error("--only needs a value, for example --only dialogs");
      }
      options.only = [...(options.only ?? []), ...value.split(",").map((s) => s.trim()).filter(Boolean)];
      i += 1;
    } else {
      throw new Error(`unknown argument '${arg}'`);
    }
  }

  if (options.only) {
    const unknown = options.only.filter((name) => !ALL_STEPS.includes(name));
    if (unknown.length > 0) {
      throw new Error(`unknown step(s): ${unknown.join(", ")}. Valid: ${ALL_STEPS.join(", ")}`);
    }
  }

  return options;
}

function hostPlatform() {
  switch (os.arch()) {
    case "arm64":
      return "ARM64";
    case "x64":
      return "x64";
    default:
      throw new Error(`unsupported architecture '${os.arch()}'`);
  }
}

function run(command, args, label) {
  const started = Date.now();
  const result = spawnSync(command, args, {
    cwd: ROOT,
    stdio: "inherit",
    windowsHide: true,
    shell: false,
  });
  const seconds = ((Date.now() - started) / 1000).toFixed(1);
  if (result.error) {
    return { label, status: -1, seconds, error: result.error.message };
  }

  return { label, status: result.status ?? -1, seconds };
}

function dotnetBuild(project, platform) {
  return run(
    "dotnet",
    ["build", project, "-c", "Release", `-p:Platform=${platform}`, "--nologo"],
    `build ${path.basename(project)}`,
  );
}

function toolPath(project, tfm, name) {
  const dir = path.dirname(project);
  const platform = hostPlatform();
  return path.join(ROOT, dir, "bin", platform, "Release", tfm, `${name}.exe`);
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    printHelp();
    return 0;
  }

  if (options.list) {
    console.log(ALL_STEPS.join("\n"));
    return 0;
  }

  if (process.platform !== "win32") {
    throw new Error("the oracle needs classic regedit.exe and runs only on Windows");
  }

  const steps = options.only ?? ALL_STEPS;
  const platform = hostPlatform();
  const record = { script: SCRIPT, startedAt: new Date().toISOString(), platform, steps: [], result: "running" };
  const results = [];

  if (!options.skipBuild) {
    for (const project of [RESOURCES_PROJECT, CAPTURE_PROJECT]) {
      const build = dotnetBuild(project, platform);
      results.push(build);
      if (build.status !== 0) {
        return finish(record, results, 1);
      }
    }
  }

  const resourcesExe = toolPath(RESOURCES_PROJECT, "net10.0-windows", "Bwc.Oracle.Resources");
  const captureExe = toolPath(CAPTURE_PROJECT, "net10.0-windows10.0.26100.0", "Bwc.Oracle.Capture");

  for (const step of steps) {
    if (step === "resources") {
      const outcome = run(resourcesExe, ["--out", "docs/parity/oracle"], "resources");
      results.push(outcome);
      if (outcome.status !== 0) {
        return finish(record, results, 1);
      }
      continue;
    }

    // --allow-dpi lets the captures run on a desktop that is not at 100 percent. The tool records the DPI
    // in every output, so a non-96 value is visible in the data rather than hidden.
    const outcome = run(captureExe, [step, "--allow-dpi"], `capture ${step}`);
    results.push(outcome);
    if (outcome.status !== 0) {
      // Keep going: a failed capture does not stop the others, and the cleanup below still runs.
      console.error(`oracle: step '${step}' failed with status ${outcome.status}`);
    }
  }

  const cleanup = run(captureExe, ["cleanup"], "cleanup");
  results.push(cleanup);

  const failed = results.filter((r) => r.status !== 0);
  return finish(record, results, failed.length === 0 ? 0 : 1);
}

function finish(record, results, exitCode) {
  record.steps = results;
  record.finishedAt = new Date().toISOString();
  record.result = exitCode === 0 ? "pass" : "fail";
  fs.mkdirSync(path.dirname(RUN_RECORD), { recursive: true });
  fs.writeFileSync(RUN_RECORD, `${JSON.stringify(record, null, 2)}\n`, "utf8");
  console.log(`oracle: ${record.result}; run record at ${RUN_RECORD}`);
  for (const r of results) {
    console.log(`  ${r.status === 0 ? "ok  " : "FAIL"} ${r.label} (${r.seconds}s${r.error ? `, ${r.error}` : ""})`);
  }
  return exitCode;
}

try {
  process.exitCode = main();
} catch (error) {
  console.error(`oracle: ${error.message}`);
  process.exitCode = 1;
}
