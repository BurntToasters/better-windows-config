#!/usr/bin/env node
/**
 * Regenerates the classic oracle on this Windows desktop, for one tool at a time (--tool regedit, the default,
 * or --tool gpedit).
 *
 * Tool regedit. Steps, in order:
 *   1. Build tools/Bwc.Oracle.Resources and tools/Bwc.Oracle.Capture (Release, host platform).
 *   2. resources: dump C:\Windows\regedit.exe and its en-US MUI into docs/parity/oracle (untracked).
 *   3. One capture per command: export, metrics, dialogs, menus, import, find, sort, permissions, keys, context,
 * binary, print, security, remote, manifest (tracked numbers and goldens; screenshots and control text are untracked).
 *   4. cleanup: delete HKCU\Software\BWC-Oracle if anything left it behind.
 *
 * Tool gpedit. Steps:
 *   resources: dump gpedit.dll, admtmpl.dll, mmc.exe, mmcndmgr.dll, mmcbase.dll and their en-US MUI, the managed
 *     WinForms resources of the policy-setting dialog and gpedit.msc into docs/parity/oracle/gpedit. Needs no
 *     administrator rights. The full dumps are untracked; gpedit-resource-index.json is the one tracked file.
 *   probe: run `Bwc.Oracle.Capture gpedit probe`. It needs administrator rights and drives GUI, so run it by hand
 *     from an administrator terminal (see docs/failure-modes/oracle-gpedit-static.md).
 *   Only the projects that the chosen steps need are built. The run record goes to artifacts/oracle/gpedit/last-run.json.
 *
 * Usage:
 *   npm run oracle
 *   npm run oracle -- --only dialogs
 *   npm run oracle -- --only metrics,menus --skip-build
 *   npm run oracle -- --list
 *   npm run oracle -- --tool gpedit --only resources
 *   npm run oracle -- --tool gpedit --list
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
const CAPTURES = [
  "export",
  "metrics",
  "dialogs",
  "menus",
  "import",
  "find",
  "sort",
  "permissions",
  "keys",
  "context",
  "binary",
  "print",
  "security",
  "remote",
  "manifest",
  "export-range",
  "export-text",
  "resource-viewers",
  "tree-order",
];
const TOOLS = {
  regedit: {
    steps: ["resources", ...CAPTURES],
    runRecord: path.join(ROOT, "artifacts", "oracle", "last-run.json"),
    resourcesOut: "docs/parity/oracle",
  },
  gpedit: {
    steps: ["resources", "probe"],
    runRecord: path.join(ROOT, "artifacts", "oracle", "gpedit", "last-run.json"),
    resourcesOut: "docs/parity/oracle/gpedit",
  },
};

const RESOURCES_PROJECT = "tools/Bwc.Oracle.Resources/Bwc.Oracle.Resources.csproj";
const CAPTURE_PROJECT = "tools/Bwc.Oracle.Capture/Bwc.Oracle.Capture.csproj";

function printHelp() {
  const stepLines = Object.entries(TOOLS).map(([name, tool]) => `  ${name}: ${tool.steps.join(", ")}`);
  console.log(`Usage: node ${SCRIPT} [--tool regedit|gpedit] [--only <step>[,<step>...]] [--skip-build] [--list]

Tools: ${Object.keys(TOOLS).join(", ")} (default regedit)
Steps:
${stepLines.join("\n")}
  --tool <name>   which classic tool to capture
  --only <steps>  run only these steps (comma separated or repeated)
  --skip-build    use the existing tool binaries
  --list          print the steps of the chosen tool and exit`);
}

function parseArgs(argv) {
  const options = { tool: "regedit", only: null, skipBuild: false, list: false, help: false };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
    } else if (arg === "--list") {
      options.list = true;
    } else if (arg === "--skip-build") {
      options.skipBuild = true;
    } else if (arg === "--tool") {
      const value = argv[i + 1];
      if (!value || !Object.hasOwn(TOOLS, value)) {
        throw new Error(`--tool needs one of: ${Object.keys(TOOLS).join(", ")}`);
      }
      options.tool = value;
      i += 1;
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
    const valid = TOOLS[options.tool].steps;
    const unknown = options.only.filter((name) => !valid.includes(name));
    if (unknown.length > 0) {
      throw new Error(`unknown step(s) for tool ${options.tool}: ${unknown.join(", ")}. Valid: ${valid.join(", ")}`);
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

  const tool = TOOLS[options.tool];
  if (options.list) {
    console.log(tool.steps.join("\n"));
    return 0;
  }

  if (process.platform !== "win32") {
    throw new Error("the oracle needs the classic Windows tools and runs only on Windows");
  }

  const steps = options.only ?? tool.steps;
  const platform = hostPlatform();
  const record = { script: SCRIPT, tool: options.tool, startedAt: new Date().toISOString(), platform, steps: [], result: "running" };
  const results = [];
  const finishRun = (exitCode) => finish(record, results, exitCode, tool.runRecord);

  if (!options.skipBuild) {
    // regedit builds both projects as before. gpedit builds only what its chosen steps run.
    const projects =
      options.tool === "regedit"
        ? [RESOURCES_PROJECT, CAPTURE_PROJECT]
        : [RESOURCES_PROJECT, ...(steps.some((step) => step !== "resources") ? [CAPTURE_PROJECT] : [])];
    for (const project of projects) {
      const build = dotnetBuild(project, platform);
      results.push(build);
      if (build.status !== 0) {
        return finishRun(1);
      }
    }
  }

  const resourcesExe = toolPath(RESOURCES_PROJECT, "net10.0-windows", "Bwc.Oracle.Resources");
  const captureExe = toolPath(CAPTURE_PROJECT, "net10.0-windows10.0.26100.0", "Bwc.Oracle.Capture");

  for (const step of steps) {
    if (step === "resources") {
      const toolArgs = options.tool === "regedit" ? [] : ["--tool", options.tool];
      const outcome = run(resourcesExe, [...toolArgs, "--out", tool.resourcesOut], "resources");
      results.push(outcome);
      if (outcome.status !== 0) {
        return finishRun(1);
      }
      continue;
    }

    if (options.tool === "gpedit") {
      // The command is written in the capture tool. It needs administrator rights, so it is run by hand.
      const outcome = run(captureExe, ["gpedit", step], `capture gpedit ${step}`);
      results.push(outcome);
      if (outcome.status !== 0) {
        console.error(`oracle: step '${step}' failed with status ${outcome.status}`);
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

  if (options.tool === "regedit") {
    const cleanup = run(captureExe, ["cleanup"], "cleanup");
    results.push(cleanup);
  }

  const failed = results.filter((r) => r.status !== 0);
  return finishRun(failed.length === 0 ? 0 : 1);
}

function finish(record, results, exitCode, runRecord) {
  record.steps = results;
  record.finishedAt = new Date().toISOString();
  record.result = exitCode === 0 ? "pass" : "fail";
  fs.mkdirSync(path.dirname(runRecord), { recursive: true });
  fs.writeFileSync(runRecord, `${JSON.stringify(record, null, 2)}\n`, "utf8");
  console.log(`oracle: ${record.result}; run record at ${runRecord}`);
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
