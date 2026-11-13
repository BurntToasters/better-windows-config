#!/usr/bin/env node

import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT = "scripts/run-app.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));

const APPS = new Map([
  ["hub", "BetterWindowsConfig.exe"],
  ["regedit", "BwcRegedit.exe"],
]);
const RIDS = new Set(["win-x64", "win-arm64"]);
const FLAVORS = new Set(["ship", "e2e"]);

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} <hub|regedit> [--build] [--elevated] [--rid win-x64|win-arm64] [--flavor ship|e2e]

Options:
  --build            Run scripts/dotnet-build.js first.
  --elevated         Launch through PowerShell Start-Process -Verb RunAs.
                     Manual use only; automated runs must not prompt for UAC.
  --rid <rid>        Defaults from process.arch: arm64 -> win-arm64, x64 -> win-x64.
  --flavor <flavor>  ship (default) or e2e.
  --help             Show this help.`);
}

function defaultRid() {
  if (process.arch === "arm64") return "win-arm64";
  if (process.arch === "x64") return "win-x64";
  throw new Error(
    `cannot derive a RID from process.arch "${process.arch}"; pass --rid`,
  );
}

function parseArgs(argv) {
  const args = argv.slice(2);
  const options = {
    app: null,
    build: false,
    elevated: false,
    rid: null,
    flavor: "ship",
    help: false,
  };
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--build") {
      options.build = true;
      continue;
    }
    if (arg === "--elevated") {
      options.elevated = true;
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
    if (arg.startsWith("-")) {
      throw new Error(`unrecognized argument: ${arg}`);
    }
    if (options.app !== null) {
      throw new Error(`unexpected extra argument: ${arg}`);
    }
    options.app = arg;
  }
  return options;
}

function validate(options) {
  if (!APPS.has(options.app)) {
    throw new Error(
      `app must be one of ${[...APPS.keys()].join(", ")}; received ${options.app ?? "(none)"}`,
    );
  }
  if (options.rid === null) {
    options.rid = defaultRid();
  }
  if (!RIDS.has(options.rid)) {
    throw new Error(
      `--rid must be one of ${[...RIDS].join(", ")}; received ${options.rid}`,
    );
  }
  if (!FLAVORS.has(options.flavor)) {
    throw new Error(
      `--flavor must be one of ${[...FLAVORS].join(", ")}; received ${options.flavor}`,
    );
  }
}

function publishDirectory(rid, flavor) {
  const name = flavor === "e2e" ? `${rid}-e2e` : rid;
  return path.join(REPO_ROOT, "publish", name);
}

function runBuild(rid, flavor) {
  const buildScript = path.join(REPO_ROOT, "scripts", "dotnet-build.js");
  const result = spawnSync(
    process.execPath,
    [buildScript, "--rid", rid, "--flavor", flavor],
    { cwd: REPO_ROOT, stdio: "inherit", windowsHide: true },
  );
  if (result.error) {
    console.error(`run-app: could not start the build: ${result.error.message}`);
    return 1;
  }
  return result.status ?? 1;
}

function powershellQuote(value) {
  return `'${value.replace(/'/g, "''")}'`;
}

function launchElevated(exePath, workingDirectory) {
  const command =
    `Start-Process -FilePath ${powershellQuote(exePath)} ` +
    `-WorkingDirectory ${powershellQuote(workingDirectory)} -Verb RunAs`;
  const env = { ...process.env };
  delete env.__COMPAT_LAYER;
  const result = spawnSync(
    "powershell.exe",
    ["-NoProfile", "-NonInteractive", "-Command", command],
    {
      cwd: REPO_ROOT,
      stdio: "inherit",
      env,
      windowsHide: true,
    },
  );
  if (result.error) {
    console.error(
      `run-app: could not start PowerShell: ${result.error.message}`,
    );
    return 1;
  }
  if (result.status !== 0) {
    return result.status ?? 1;
  }
  console.log(exePath);
  return 0;
}

function main(argv = process.argv) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(
      `run-app: ${error instanceof Error ? error.message : String(error)}`,
    );
    console.error(`Try: node ${SCRIPT} --help`);
    return 2;
  }

  if (options.help) {
    printHelp();
    return 0;
  }

  try {
    validate(options);
  } catch (error) {
    console.error(
      `run-app: ${error instanceof Error ? error.message : String(error)}`,
    );
    console.error(`Try: node ${SCRIPT} --help`);
    return 2;
  }

  const { app, rid, flavor } = options;
  if (options.build) {
    const buildStatus = runBuild(rid, flavor);
    if (buildStatus !== 0) {
      console.error(`run-app: build failed with exit code ${buildStatus}`);
      return buildStatus;
    }
  }

  const workingDirectory = publishDirectory(rid, flavor);
  const exePath = path.join(workingDirectory, APPS.get(app));
  if (!fs.existsSync(exePath) || !fs.statSync(exePath).isFile()) {
    console.error(`Executable not found: ${exePath}`);
    console.error(
      `Build it with: node scripts/dotnet-build.js --rid ${rid} --flavor ${flavor}`,
    );
    return 1;
  }

  if (options.elevated) {
    return launchElevated(exePath, workingDirectory);
  }

  const env = { ...process.env, __COMPAT_LAYER: "RunAsInvoker" };
  try {
    const child = spawn(exePath, [], {
      cwd: workingDirectory,
      detached: true,
      stdio: "ignore",
      env,
      windowsHide: false,
    });
    child.unref();
  } catch (error) {
    console.error(
      `run-app: could not start ${exePath}: ${error instanceof Error ? error.message : String(error)}`,
    );
    return 1;
  }

  console.log(exePath);
  return 0;
}

process.exit(main());
