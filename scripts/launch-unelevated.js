#!/usr/bin/env node
/**
 * Start one .exe without a UAC prompt.
 *
 * The child gets __COMPAT_LAYER=RunAsInvoker, which tells Windows to start a highestAvailable or
 * requireAdministrator executable with the caller's token instead of asking for elevation. The
 * variable is set on the child only. The helper runs the file directly (shell: false) and never
 * through cmd.exe or PowerShell.
 *
 * RunAsInvoker keeps the caller's token, so the child is elevated when the caller is. The helper
 * refuses to start from an elevated shell for that reason.
 */
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { elevationState } from "./elevated-runner/elevation.mjs";

const SCRIPT = "scripts/launch-unelevated.js";
const COMPAT_LAYER = "RunAsInvoker";

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--wait] <file.exe> [args...]

Starts <file.exe> with __COMPAT_LAYER=RunAsInvoker on the child process, so no UAC
prompt appears. Arguments after the file are passed to it unchanged.

Options:
  --wait   Wait for the program to exit and return its exit code. Without it the
           program starts detached and the helper returns at once.
  --help   Show this help.

Refuses a path that is not an existing .exe file, and refuses to run from an
elevated shell, because RunAsInvoker would start the program elevated.`);
}

function parseArgs(argv) {
  const options = { wait: false, help: false, exe: null, args: [] };
  let index = 0;
  for (; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      return options;
    }
    if (arg === "--wait") {
      options.wait = true;
      continue;
    }
    if (arg.startsWith("-")) {
      throw new Error(`unrecognized option before the executable: ${arg}`);
    }
    break;
  }
  if (index >= argv.length) {
    throw new Error("an executable path is required");
  }
  options.exe = argv[index];
  options.args = argv.slice(index + 1);
  return options;
}

function resolveExecutable(exePath) {
  const resolved = path.resolve(exePath);
  if (path.extname(resolved).toLowerCase() !== ".exe") {
    throw new Error(`not an .exe file: ${exePath}`);
  }
  let stat;
  try {
    stat = fs.statSync(resolved);
  } catch {
    throw new Error(`file not found: ${resolved}`);
  }
  if (!stat.isFile()) {
    throw new Error(`not a file: ${resolved}`);
  }
  return resolved;
}

function launch(exe, args, wait) {
  const options = {
    cwd: path.dirname(exe),
    env: { ...process.env, __COMPAT_LAYER: COMPAT_LAYER },
    shell: false,
    windowsHide: false,
  };

  return new Promise((resolve, reject) => {
    if (wait) {
      const child = spawn(exe, args, { ...options, stdio: "inherit" });
      child.once("error", reject);
      child.once("spawn", () => {
        console.log(`started ${exe} (pid ${child.pid}), waiting for exit`);
      });
      child.once("exit", (code) => resolve(code ?? 1));
      return;
    }

    const child = spawn(exe, args, { ...options, detached: true, stdio: "ignore" });
    child.once("error", reject);
    child.once("spawn", () => {
      child.unref();
      console.log(`started ${exe} (pid ${child.pid}) with __COMPAT_LAYER=${COMPAT_LAYER}`);
      resolve(0);
    });
  });
}

async function main(argv) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(`launch-unelevated: ${error.message}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return 2;
  }
  if (options.help) {
    printHelp();
    return 0;
  }

  let exe;
  try {
    exe = resolveExecutable(options.exe);
  } catch (error) {
    console.error(`launch-unelevated: ${error.message}`);
    return 2;
  }

  const elevation = elevationState();
  if (!elevation.known) {
    console.error(`launch-unelevated: refusing to start, the elevation check failed (${elevation.detail})`);
    return 1;
  }
  if (elevation.elevated) {
    console.error("launch-unelevated: refusing to start from an elevated shell.");
    console.error("RunAsInvoker would start the program with this elevated token. Start it from a normal shell.");
    return 1;
  }

  try {
    return await launch(exe, options.args, options.wait);
  } catch (error) {
    console.error(`launch-unelevated: could not start ${exe}: ${error.message}`);
    return 1;
  }
}

process.exitCode = await main(process.argv.slice(2));
