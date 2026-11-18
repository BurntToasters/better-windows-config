#!/usr/bin/env node
/**
 * Rehearses the real updater client against the live stable feed. It runs the built ship
 * publish/<rid>/BwcUpdate.exe with --check, then --download, with --current-version 0.0.0. Each run goes through
 * scripts/run-restricted.ps1, so it is unelevated even from an elevated SSH shell. Each run must
 * exit 0 and print a final JSON line with ok:true and the expected version. The download's staging folder
 * must exist, and this run deletes it (only that folder). The verdict goes to
 * artifacts/release/client-live/result.json. Failure modes: docs/failure-modes/quality-gate.md.
 *
 * The expected version comes from --expect-version, or else from the manifest JSON on the feed.
 * --url-base replaces the feed base for tests. Only https, or loopback http, is accepted.
 */
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { isDirectExecutionOf } from "./direct-execution.mjs";
import { EXIT, describeError, parseArguments, printRefusals, repoRootFrom, writeJson } from "./lib/cli.js";
import { resolveRepo } from "./lib/github-release.js";
import { channelForVersion, tryParseSemVer } from "./lib/semver.js";
import { manifestFileName } from "./lib/update-manifest.js";
import { isLoopbackUrl } from "./lib/url-policy.js";

const SCRIPT = "scripts/updater-client-live.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const RIDS = Object.freeze(["win-x64", "win-arm64"]);
const CHANNEL = "stable";
const CURRENT_VERSION = "0.0.0";
const CLIENT_TIMEOUT_MS = 5 * 60_000;
const WRAPPER_MARGIN_MS = 60_000;
const MANIFEST_TIMEOUT_MS = 60_000;
const STDERR_TAIL_CHARS = 800;
const USER_AGENT = "bwc-release-client-check";
const STAGING_NAME_PATTERN = /^[0-9a-f]{32}$/;
const RESULT_RELATIVE = "artifacts/release/client-live/result.json";

/** A failure with the reason code and exit code the result and the caller report. */
export class RehearsalError extends Error {
  constructor(reason, detail, exitCode = EXIT.REFUSED) {
    super(detail);
    this.reason = reason;
    this.exitCode = exitCode;
  }
}

/** The RID for this host, from process.arch. Null when the host is neither x64 nor arm64. */
export function defaultRid(arch = process.arch) {
  if (arch === "arm64") return "win-arm64";
  if (arch === "x64") return "win-x64";
  return null;
}

export function clientExePath(root, rid) {
  return path.join(root, "publish", rid, "BwcUpdate.exe");
}

export function stagingRootFor(tempDir = os.tmpdir()) {
  return path.join(tempDir, "BWC-Update");
}

function isStableVersion(value) {
  return typeof value === "string" && tryParseSemVer(value) !== null && channelForVersion(value) === CHANNEL;
}

/** The feed base: the GitHub latest-download URL, or --url-base when it is https or loopback http. */
export function feedBaseFor({ urlBase, repo, env }) {
  if (urlBase !== undefined) {
    const text = String(urlBase).trim();
    const https = /^https:\/\//i.test(text);
    if (!https && !isLoopbackUrl(text)) {
      throw new RehearsalError("base-url-refused", "--url-base must be https, or loopback http for tests", EXIT.USAGE);
    }
    return text.replace(/\/+$/, "");
  }
  let resolved;
  try {
    resolved = resolveRepo(repo, env);
  } catch (error) {
    throw new RehearsalError(error.reason ?? "repo-invalid", describeError(error), error.exitCode ?? EXIT.USAGE);
  }
  return `https://github.com/${resolved.slug}/releases/latest/download`;
}

/** The version the live feed should serve: the argument, or the version in the manifest JSON. */
async function expectedVersion({ expectVersion, manifestUrl }, state) {
  if (expectVersion !== undefined) {
    if (!isStableVersion(expectVersion)) {
      throw new RehearsalError("version-invalid", `--expect-version is not a stable SemVer: ${expectVersion}`, EXIT.USAGE);
    }
    state.expectedSource = "argument";
    return expectVersion;
  }
  state.expectedSource = "manifest";
  let manifest;
  try {
    const response = await fetch(manifestUrl, {
      redirect: "follow",
      signal: AbortSignal.timeout(MANIFEST_TIMEOUT_MS),
      headers: { "User-Agent": USER_AGENT },
    });
    if (!response.ok) {
      throw new RehearsalError("expect-unavailable", `${manifestUrl}: HTTP ${response.status}`, EXIT.EXTERNAL);
    }
    manifest = JSON.parse(Buffer.from(await response.arrayBuffer()).toString("utf8"));
  } catch (error) {
    if (error instanceof RehearsalError) throw error;
    throw new RehearsalError("expect-unavailable", `${manifestUrl}: ${describeError(error)}`, EXIT.EXTERNAL);
  }
  if (!isStableVersion(manifest?.version)) {
    throw new RehearsalError("expect-unavailable", `manifest version is not a stable SemVer: ${String(manifest?.version)}`, EXIT.EXTERNAL);
  }
  return manifest.version;
}

/**
 * Runs the client once through scripts/run-restricted.ps1, which starts it with a restricted, medium-integrity copy
 * of this process's token (C21 to C26). The release VM is reached over SSH, which gives an administrator the full
 * token, and the client refuses to check or download when elevated. Resolves with the exit code and output, or
 * rejects with deelevate-failed (the client was not run unelevated) or timeout.
 */
export function runClient(exe, args, timeoutMs = CLIENT_TIMEOUT_MS) {
  const work = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-client-run-"));
  const files = {
    stdout: path.join(work, "stdout.txt"),
    stderr: path.join(work, "stderr.txt"),
    result: path.join(work, "result.json"),
  };
  const wrapperArgs = [
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    path.join(REPO_ROOT, "scripts", "run-restricted.ps1"),
    "-Exe",
    exe,
    "-ArgsBase64",
    args.map((arg) => Buffer.from(String(arg), "utf8").toString("base64")).join(","),
    "-WorkingDirectory",
    process.cwd(),
    "-StdoutPath",
    files.stdout,
    "-StderrPath",
    files.stderr,
    "-ResultPath",
    files.result,
    "-TimeoutMs",
    String(timeoutMs),
  ];
  const step = args[0];
  return new Promise((resolve, reject) => {
    const finish = (settle) => {
      fs.rmSync(work, { recursive: true, force: true });
      settle();
    };
    let wrapper;
    try {
      wrapper = spawn(windowsPowerShell(), wrapperArgs, { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    } catch (error) {
      finish(() => reject(new RehearsalError("deelevate-failed", `${step}: could not start the wrapper: ${describeError(error)}`, EXIT.EXTERNAL)));
      return;
    }
    const wrapperErr = [];
    wrapper.stdout.resume();
    wrapper.stderr.on("data", (chunk) => wrapperErr.push(chunk));
    // C24: the wrapper ends the client at timeoutMs. This later kill is only for a wrapper that hangs itself.
    let killed = false;
    const timer = setTimeout(() => {
      killed = true;
      wrapper.kill();
    }, timeoutMs + WRAPPER_MARGIN_MS);
    wrapper.on("error", (error) => {
      clearTimeout(timer);
      finish(() => reject(new RehearsalError("deelevate-failed", `${step}: could not start the wrapper: ${describeError(error)}`, EXIT.EXTERNAL)));
    });
    wrapper.on("close", (code) => {
      clearTimeout(timer);
      if (killed) {
        finish(() => reject(new RehearsalError("timeout", `${step} did not finish within ${timeoutMs} ms`, EXIT.EXTERNAL)));
        return;
      }
      let outcome;
      try {
        outcome = JSON.parse(fs.readFileSync(files.result, "utf8").replace(/^\uFEFF/, ""));
      } catch (error) {
        // C26: no result file means the wrapper itself failed, so the client cannot be said to have run unelevated.
        const tail = stderrTail(Buffer.concat(wrapperErr).toString("utf8"));
        finish(() => reject(new RehearsalError(
          "deelevate-failed",
          `${step}: the wrapper exited ${code} with no result (${describeError(error)})${tail ? ` ${tail}` : ""}`,
          EXIT.EXTERNAL,
        )));
        return;
      }
      if (!outcome.started) {
        // C22 and C23: the client never ran. There is no fallback to starting it any other way.
        finish(() => reject(new RehearsalError(
          "deelevate-failed",
          `${step}: ${outcome.failedStep ?? "unknown step"}: ${outcome.error ?? "the client was not started"}`,
          EXIT.EXTERNAL,
        )));
        return;
      }
      if (outcome.timedOut) {
        finish(() => reject(new RehearsalError("timeout", `${step} did not finish within ${timeoutMs} ms`, EXIT.EXTERNAL)));
        return;
      }
      const read = (file) => (fs.existsSync(file) ? fs.readFileSync(file, "utf8") : "");
      const run = {
        code: outcome.exitCode,
        stdout: read(files.stdout),
        stderr: read(files.stderr),
        launch: { mode: outcome.launch, elevated: outcome.elevated, integrity: outcome.integrityRid },
      };
      finish(() => resolve(run));
    });
  });
}

function windowsPowerShell() {
  const systemRoot = process.env.SystemRoot ?? "C:\\Windows";
  return path.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
}

/** The last stdout line that is a JSON object with a boolean ok. Progress lines carry no ok, so they never count. */
export function finalVerdict(stdout) {
  let verdict = null;
  for (const line of stdout.split(/\r?\n/)) {
    const text = line.trim();
    if (!text.startsWith("{")) continue;
    try {
      const value = JSON.parse(text);
      if (value && typeof value.ok === "boolean") verdict = value;
    } catch {
      // Not JSON: a progress or diagnostic line.
    }
  }
  return verdict;
}

function stderrTail(text) {
  const trimmed = text.trim();
  return trimmed.length > STDERR_TAIL_CHARS ? `...${trimmed.slice(-STDERR_TAIL_CHARS)}` : trimmed;
}

/** Throws unless one client run exited 0, printed a verdict with ok:true, and reported the expected version. */
export function verdictOf(step, run, expected) {
  const verdict = finalVerdict(run.stdout);
  if (run.code !== 0) {
    // C20: the client prints its refusal on stdout, so name it along with the stderr tail.
    const reason = verdict ? ` failure ${String(verdict.failure)}${verdict.detail ? ` (${verdict.detail})` : ""}` : "";
    const tail = stderrTail(run.stderr);
    throw new RehearsalError("client-failed", `${step} exited ${run.code}:${reason}${tail ? ` ${tail}` : ""}`, EXIT.REFUSED);
  }
  if (!verdict) {
    throw new RehearsalError("output-unparsed", `${step} printed no final JSON line with a boolean ok`, EXIT.REFUSED);
  }
  if (verdict.ok !== true) {
    throw new RehearsalError("client-refused", `${step}: failure ${String(verdict.failure)} ${verdict.detail ?? ""}`.trim(), EXIT.REFUSED);
  }
  if (verdict.version !== expected) {
    throw new RehearsalError("version-mismatch", `${step}: client reports ${verdict.version}, expected ${expected}`, EXIT.REFUSED);
  }
  return verdict;
}

/**
 * True only for a folder directly under the staging root with a 32-hex staging name. Anything else is
 * never deleted, whatever the client printed.
 */
export function isSafeStagingFolder(folder, stagingRoot) {
  const resolved = path.resolve(folder);
  const parent = path.dirname(resolved).toLowerCase();
  return parent === path.resolve(stagingRoot).toLowerCase() && STAGING_NAME_PATTERN.test(path.basename(resolved));
}

/** Deletes the download's staging folder, only when this run created it and the path passes the guard. */
function cleanupStaging(folder, stagingRoot, before, state) {
  const name = path.basename(folder);
  if (!isSafeStagingFolder(folder, stagingRoot)) {
    state.staging.cleanup = "path-refused";
    return;
  }
  if (before.has(name)) {
    state.staging.cleanup = "preexisting";
    return;
  }
  try {
    fs.rmSync(folder, { recursive: true, force: true });
  } catch (error) {
    state.staging.cleanup = "failed";
    state.cleanupError = new RehearsalError("cleanup-failed", `${folder}: ${describeError(error)}`, EXIT.REFUSED);
    return;
  }
  if (fs.existsSync(folder)) {
    state.staging.cleanup = "failed";
    state.cleanupError = new RehearsalError("cleanup-failed", `${folder} still exists after delete`, EXIT.REFUSED);
    return;
  }
  state.staging.cleanup = "removed";
}

function stagingNames(stagingRoot) {
  return fs.existsSync(stagingRoot) ? new Set(fs.readdirSync(stagingRoot)) : new Set();
}

/** Staging folders under stagingRoot that were not there before this run and pass the path guard. */
function createdStagingFolders(stagingRoot, before) {
  return [...stagingNames(stagingRoot)]
    .filter((name) => !before.has(name))
    .map((name) => path.join(stagingRoot, name))
    .filter((folder) => isSafeStagingFolder(folder, stagingRoot) && fs.statSync(folder).isDirectory());
}

async function rehearse(options, state) {
  const rid = options.rid ?? defaultRid();
  if (!RIDS.includes(rid)) {
    throw new RehearsalError("usage", `--rid must be win-x64 or win-arm64; received ${rid ?? "(unsupported host arch)"}`, EXIT.USAGE);
  }
  const exe = clientExePath(options.root, rid);
  state.rid = rid;
  state.exe = path.relative(options.root, exe).split(path.sep).join("/");
  if (!fs.existsSync(exe)) {
    throw new RehearsalError("binary-missing", `${state.exe} is missing; run npm run build:win:${rid.slice(4)} first`, EXIT.MISSING);
  }

  const base = feedBaseFor({ urlBase: options.urlBase, repo: options.repo, env: options.env });
  const manifestUrl = `${base}/${manifestFileName(CHANNEL)}`;
  state.manifestUrl = manifestUrl;
  state.expected = await expectedVersion({ expectVersion: options.expectVersion, manifestUrl }, state);

  const clientArgs = [
    "--manifest-url",
    manifestUrl,
    "--signature-url",
    `${manifestUrl}.minisig`,
    "--channel",
    CHANNEL,
    "--current-version",
    CURRENT_VERSION,
  ];

  const check = await runClient(exe, ["--check", ...clientArgs], options.timeoutMs);
  const checkVerdict = verdictOf("check", check, state.expected);
  state.launch = check.launch;
  state.checks.push({ name: "check", pass: true, detail: `ok, version ${checkVerdict.version}` });

  const stagingRoot = stagingRootFor(options.tempDir);
  const before = stagingNames(stagingRoot);
  const download = await runClient(exe, ["--download", ...clientArgs], options.timeoutMs);
  // The verdict is read even when the exit code is nonzero, so a refused download can still name its folder.
  const verdict = finalVerdict(download.stdout);
  state.staging = { folder: null, preexisting: false, cleanup: "not-created" };
  try {
    verdictOf("download", download, state.expected);
    const folder = verdict?.stagingFolder;
    if (typeof folder !== "string" || folder.length === 0) {
      throw new RehearsalError("staging-missing", "download printed no stagingFolder", EXIT.REFUSED);
    }
    if (!isSafeStagingFolder(folder, stagingRoot)) {
      throw new RehearsalError("staging-path-refused", `stagingFolder is not a staging folder under ${stagingRoot}`, EXIT.REFUSED);
    }
    state.staging.folder = folder;
    state.staging.preexisting = before.has(path.basename(folder));
    if (!fs.existsSync(folder) || !fs.statSync(folder).isDirectory()) {
      throw new RehearsalError("staging-missing", `staging folder does not exist: ${folder}`, EXIT.REFUSED);
    }
    state.checks.push({ name: "download", pass: true, detail: `ok, version ${verdict.version}, staged` });
  } finally {
    // A failed download may print no folder. Then the folders this run created are the ones to remove.
    const reported = typeof verdict?.stagingFolder === "string" && verdict.stagingFolder.length > 0;
    const folders = reported ? [verdict.stagingFolder] : createdStagingFolders(stagingRoot, before);
    for (const folder of folders) {
      state.staging.folder = folder;
      cleanupStaging(folder, stagingRoot, before, state);
    }
  }
  if (state.cleanupError) throw state.cleanupError;
}

function writeResult(root, result) {
  const file = path.join(root, ...RESULT_RELATIVE.split("/"));
  try {
    writeJson(file, result);
  } catch (error) {
    return new RehearsalError("artifact-write-failed", `${RESULT_RELATIVE}: ${describeError(error)}`, EXIT.EXTERNAL);
  }
  return null;
}

/**
 * Runs the client check and download. Resolves with { ok, exitCode, error, result }, and writes the result
 * file even on a refusal. Options: rid, root, urlBase, repo, expectVersion, env, tempDir, timeoutMs.
 */
export async function runClientRehearsal(options = {}) {
  const root = options.root ?? REPO_ROOT;
  const env = options.env ?? process.env;
  const started = Date.now();
  const state = { rid: null, exe: null, manifestUrl: null, expected: null, expectedSource: null, launch: null, checks: [], staging: null };
  let failure = null;
  try {
    await rehearse({ ...options, root, env, timeoutMs: options.timeoutMs ?? CLIENT_TIMEOUT_MS }, state);
  } catch (error) {
    failure = error instanceof RehearsalError ? error : new RehearsalError("client-failed", describeError(error), EXIT.EXTERNAL);
  }
  const result = {
    schema: 1,
    tool: SCRIPT,
    ok: failure === null,
    reason: failure ? failure.reason : "ok",
    detail: failure ? failure.message : `client check and download passed for ${state.expected}`,
    rid: state.rid,
    exe: state.exe,
    channel: CHANNEL,
    version: state.expected,
    expectedSource: state.expectedSource,
    launch: state.launch,
    manifestUrl: state.manifestUrl,
    checks: state.checks,
    staging: state.staging,
    durationMs: Date.now() - started,
    finishedAt: new Date().toISOString(),
  };
  const writeError = writeResult(root, result);
  const error = failure ?? writeError;
  return { ok: error === null, exitCode: error ? error.exitCode : EXIT.OK, error, result };
}

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--rid win-x64|win-arm64] [--expect-version <stable semver>] [--url-base <url>]
                 [--repo <owner/name>] [--help]

Runs publish/<rid>/BwcUpdate.exe --check and --download against the live stable feed, then deletes the
staging folder that the download created. Writes ${RESULT_RELATIVE}.

Options:
  --rid <rid>              Default from process.arch. Pass it explicitly on a mixed-arch host.
  --expect-version <v>     Stable version the feed must serve. Default: the version in the manifest JSON.
  --url-base <url>         Feed base for tests. https, or loopback http (127.0.0.1 or localhost).
  --repo <owner/name>      Default GH_REPO, then GH_REPO_OWNER and GH_REPO_NAME, then the BWC default.
  --help                   Show this help.

Exit codes: 0 passed, 1 refused, 2 usage, 3 binary missing, 4 network, timeout, or write failure.`);
}

export async function main(argv = process.argv.slice(2), env = process.env) {
  let parsed;
  try {
    parsed = parseArguments(argv, {
      values: ["rid", "url-base", "expect-version", "repo"],
      flags: ["help"],
    });
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }
  if (parsed.has("help")) {
    printHelp();
    return EXIT.OK;
  }
  if (parsed.positionals.length > 0) {
    console.error(`${SCRIPT}: unexpected argument: ${parsed.positionals[0]}`);
    return EXIT.USAGE;
  }
  const outcome = await runClientRehearsal({
    rid: parsed.get("rid"),
    urlBase: parsed.get("url-base"),
    expectVersion: parsed.get("expect-version"),
    repo: parsed.get("repo"),
    env,
    root: REPO_ROOT,
  });
  if (!outcome.ok) {
    printRefusals(SCRIPT, [{ reason: outcome.error.reason, detail: outcome.error.message }]);
    return outcome.exitCode;
  }
  console.log(`${SCRIPT}: ok (${outcome.result.version}, ${outcome.result.rid}, ${outcome.result.exe})`);
  console.log(`  manifest: ${outcome.result.manifestUrl}`);
  console.log(`  staging: ${outcome.result.staging.cleanup}`);
  console.log(`  result: ${RESULT_RELATIVE}`);
  return EXIT.OK;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = await main();
}
