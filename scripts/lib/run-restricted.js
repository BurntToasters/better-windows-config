/**
 * Synchronous launcher that runs a single Windows executable unelevated through
 * `scripts/run-restricted.ps1` (Medium-integrity LUA token, Administrators deny-only, private
 * Medium window station/desktop, and pre-resume identity verification). Used by `scripts/test-all.js`
 * so `dotnet` test/build steps work from an elevated OpenSSH release shell without weakening
 * `Log.Initialize`, `SafeDataFolder`, or `test-e2e.js --flavor ship`.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const REPO_ROOT = path.dirname(path.dirname(path.dirname(fileURLToPath(import.meta.url))));
export const DEFAULT_RESTRICTED_SCRIPT = path.join(REPO_ROOT, "scripts", "run-restricted.ps1");
export const STATUS_DLL_INIT_FAILED = -1073741502;
export const WRAPPER_MARGIN_MS = 30_000;
const WRAPPER_MAX_BUFFER = 16 * 1024 * 1024;

export function windowsPowerShell() {
  const systemRoot = process.env.SystemRoot ?? "C:\\Windows";
  return path.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
}

function isFile(candidate) {
  try {
    return fs.statSync(candidate).isFile();
  } catch {
    return false;
  }
}

/**
 * Resolves `name` to an existing file path. `CreateProcessAsUserW` inside `run-restricted.ps1`
 * passes the path as `lpApplicationName`, which does not search `PATH`.
 */
export function resolveExecutable(name, fallbacks = []) {
  if (!name || typeof name !== "string") return null;
  if (path.isAbsolute(name) || /[\\/]/.test(name)) {
    const resolved = path.resolve(name);
    return isFile(resolved) ? resolved : null;
  }

  const hasExtension = path.extname(name) !== "";
  const extensions =
    hasExtension || process.platform !== "win32" ? [""] : [".exe", ".cmd", ".bat", ""];
  const pathValue = process.env.PATH ?? "";
  for (const directory of pathValue.split(path.delimiter)) {
    if (!directory) continue;
    for (const extension of extensions) {
      const candidate = path.join(directory, `${name}${extension}`);
      if (isFile(candidate)) return path.resolve(candidate);
    }
  }

  for (const fallback of fallbacks) {
    if (fallback && isFile(fallback)) return path.resolve(fallback);
  }
  return null;
}

export function buildRestrictedWrapperArgs({
  exe,
  args = [],
  cwd = process.cwd(),
  stdoutPath,
  stderrPath,
  resultPath,
  timeoutMs,
  scriptPath = DEFAULT_RESTRICTED_SCRIPT,
}) {
  const argsBase64 = args
    .map((arg) => Buffer.from(String(arg), "utf8").toString("base64"))
    .join(",");
  return [
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    scriptPath,
    "-Exe",
    exe,
    "-ArgsBase64",
    argsBase64,
    "-WorkingDirectory",
    cwd,
    "-StdoutPath",
    stdoutPath,
    "-StderrPath",
    stderrPath,
    "-ResultPath",
    resultPath,
    "-TimeoutMs",
    String(timeoutMs),
  ];
}

function readTextIfPresent(filePath) {
  try {
    return fs.existsSync(filePath) ? fs.readFileSync(filePath, "utf8") : "";
  } catch {
    return "";
  }
}

function stderrTail(text) {
  return String(text ?? "")
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .slice(-6)
    .join(" | ");
}

/**
 * Runs `exe` synchronously with a restricted copy of the caller's token via `run-restricted.ps1`.
 * Returns `{ ok: true, status, stdout, stderr, launch }` on child completion, or
 * `{ ok: false, reason, detail, status: null, stdout, stderr, launch }`.
 */
export function runRestrictedSync(
  exe,
  args = [],
  {
    cwd = process.cwd(),
    env = process.env,
    timeoutMs = 5 * 60 * 1000,
    scriptPath = DEFAULT_RESTRICTED_SCRIPT,
  } = {},
) {
  const work = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-restricted-run-"));
  const files = {
    stdout: path.join(work, "stdout.txt"),
    stderr: path.join(work, "stderr.txt"),
    result: path.join(work, "result.json"),
  };

  try {
    const wrapperArgs = buildRestrictedWrapperArgs({
      exe,
      args,
      cwd,
      stdoutPath: files.stdout,
      stderrPath: files.stderr,
      resultPath: files.result,
      timeoutMs,
      scriptPath,
    });
    const wrapper = spawnSync(windowsPowerShell(), wrapperArgs, {
      cwd,
      env,
      encoding: "utf8",
      windowsHide: true,
      maxBuffer: WRAPPER_MAX_BUFFER,
      timeout: timeoutMs + WRAPPER_MARGIN_MS,
      stdio: ["ignore", "pipe", "pipe"],
    });

    if (wrapper.error) {
      const reason = wrapper.error.code === "ETIMEDOUT" ? "timeout" : "deelevate-failed";
      const detail =
        reason === "timeout"
          ? `restricted wrapper did not finish within ${timeoutMs + WRAPPER_MARGIN_MS} ms`
          : `could not start restricted wrapper: ${wrapper.error.message}`;
      return {
        ok: false,
        reason,
        detail,
        status: null,
        stdout: "",
        stderr: String(wrapper.stderr ?? ""),
        launch: null,
      };
    }

    let outcome;
    try {
      const raw = fs.readFileSync(files.result, "utf8").replace(/^\uFEFF/, "");
      outcome = JSON.parse(raw);
    } catch (error) {
      const tail = stderrTail(wrapper.stderr);
      const detail = error instanceof Error ? error.message : String(error);
      return {
        ok: false,
        reason: "deelevate-failed",
        detail: `restricted wrapper exited ${wrapper.status ?? "null"} with no result (${detail})${tail ? ` ${tail}` : ""}`,
        status: null,
        stdout: "",
        stderr: String(wrapper.stderr ?? ""),
        launch: null,
      };
    }

    const launch = {
      mode: outcome.launch ?? "restricted-token",
      elevated: outcome.elevated,
      integrity: outcome.integrityRid ?? null,
      desktop: outcome.desktop ?? null,
    };
    const stdout = readTextIfPresent(files.stdout);
    const stderr = readTextIfPresent(files.stderr);

    if (!outcome.started) {
      return {
        ok: false,
        reason: "deelevate-failed",
        detail: `${outcome.failedStep ?? "unknown step"}: ${outcome.error ?? "child was not started"}`,
        status: null,
        stdout,
        stderr,
        launch,
      };
    }

    if (outcome.timedOut) {
      return {
        ok: false,
        reason: "timeout",
        detail: `command did not finish within ${timeoutMs} ms`,
        status: null,
        stdout,
        stderr,
        launch,
      };
    }

    if (outcome.exitCode === STATUS_DLL_INIT_FAILED) {
      return {
        ok: false,
        reason: "deelevate-failed",
        detail: `restricted child could not start on desktop (${outcome.desktop ?? "unknown"}): exit 0xC0000142`,
        status: null,
        stdout,
        stderr,
        launch,
      };
    }

    return {
      ok: true,
      status: outcome.exitCode,
      stdout,
      stderr,
      launch,
    };
  } finally {
    fs.rmSync(work, { recursive: true, force: true });
  }
}
