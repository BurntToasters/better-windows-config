#!/usr/bin/env node
/**
 * Release preflight: clean tree, version agreement, changelog section, branch, trusted key,
 * and optionally a complete D5 artifact directory. No network.
 *
 * --require-trusted-key also runs the signing readiness checks (update key, GPG probe, AFTER_PACK_LOC,
 * signing variables and tools, makensis, Node engines, origin ancestry, and then gh auth and the
 * release list). The network checks run only when every local check passes.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import { EXIT, describeError, exitCodeForErrors, parseArguments, printRefusals, repoRootFrom } from "./lib/cli.js";
import { listReleases, ghEnvironment, resolveRepo } from "./lib/github-release.js";
import { runPreflight } from "./lib/release-policy.js";
import { publicKeyFromEnvironment } from "./embed-update-key.js";
import { findMakensis } from "./build-installer.js";
import { runGpgProcess } from "./gpg-sign.js";
import { isDirectExecutionOf } from "./direct-execution.mjs";

const SCRIPT = "scripts/release-preflight.js";
const REPO_ROOT = repoRootFrom(import.meta.url);

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [options]

Checks the release rules for the current checkout. It never writes a release session.
Without --require-trusted-key it never contacts the network.

Options:
  --root <dir>            Repository root. Defaults to the parent of scripts/.
  --version <semver>      Release version. Defaults to package.json.
  --channel <stable|beta> Expected channel. Defaults to the version.
  --allow-detached        Allow a detached HEAD contained in the expected branch.
  --trusted-key <path>    Public release key to parse and report.
  --require-trusted-key   Refuse when there is no trusted key, and run the signing readiness checks:
                          update key, GPG probe, AFTER_PACK_LOC, signing tools, makensis, Node engines,
                          origin ancestry, gh auth, and the release list. The readiness checks read
                          the environment (.env through the npm script) and never print secrets.
  --artifact-dir <dir>    Validate a D5 artifact directory as part of preflight.
  --json                  Print one JSON line.
  --help                  Show this help.

Exit codes: 0 pass, 1 refused, 2 usage, 3 missing input.`);
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    const parsed = parseArguments(argv, {
      values: ["root", "version", "channel", "trusted-key", "artifact-dir"],
      flags: ["allow-detached", "require-trusted-key", "json", "help"],
    });
    if (parsed.has("help")) {
      printHelp();
      return EXIT.OK;
    }
    if (parsed.positionals.length > 0) {
      throw new Error(`unexpected argument: ${parsed.positionals[0]}`);
    }
    const channel = parsed.get("channel");
    if (channel !== undefined && channel !== "stable" && channel !== "beta") {
      throw new Error("--channel must be stable or beta");
    }
    options = {
      root: path.resolve(parsed.get("root") ?? REPO_ROOT),
      version: parsed.get("version"),
      channel,
      allowDetached: parsed.has("allow-detached"),
      trustedKey: parsed.get("trusted-key"),
      requireTrustedKey: parsed.has("require-trusted-key"),
      artifactDir: parsed.get("artifact-dir") ? path.resolve(parsed.get("artifact-dir")) : null,
      json: parsed.has("json"),
    };
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }

  return run(options);
}

/** The real readiness dependencies. Each one reads the machine; tests inject fakes instead. */
export function realReadinessDeps(root) {
  return {
    loadUpdateKey(env) {
      try {
        return { ok: true, text: publicKeyFromEnvironment(env).text };
      } catch (error) {
        return { ok: false, reason: error?.reason ?? "key-malformed", detail: describeError(error) };
      }
    },
    runGpg: runGpgProcess,
    probeDirectory: probeWritableFolder,
    findMakensis: (env) => findMakensis({ env }),
    checkSigningTools,
    nodeVersion: process.version,
    refExists: (ref) => gitSucceeds(root, ["rev-parse", "--verify", "--quiet", `${ref}^{commit}`]),
    isAncestor: (commit, ref) => gitSucceeds(root, ["merge-base", "--is-ancestor", commit, ref]),
    ghAuthStatus,
    listReleases: () => listReleases(resolveRepo(undefined, process.env), { cwd: root }),
  };
}

function gitSucceeds(root, args) {
  const result = spawnSync("git", args, { cwd: root, windowsHide: true, stdio: ["ignore", "ignore", "ignore"] });
  return result.status === 0;
}

/**
 * The folder must exist, be a real folder (not a link), and accept a file. A missing or unmapped drive
 * is a reason, never an exception.
 */
export function probeWritableFolder(value) {
  let info;
  try {
    info = fs.lstatSync(value);
  } catch (error) {
    return {
      ok: false,
      reason: "after-pack-loc-unavailable",
      detail: `${value} does not exist or its drive is not mapped (${error?.code ?? "unavailable"})`,
    };
  }
  if (info.isSymbolicLink()) return { ok: false, reason: "after-pack-loc-invalid", detail: `${value} is a symbolic link` };
  if (!info.isDirectory()) return { ok: false, reason: "after-pack-loc-invalid", detail: `${value} is not a folder` };
  const probe = path.join(value, `.bwc-readiness-${process.pid}-${Date.now()}.tmp`);
  try {
    fs.writeFileSync(probe, "readiness\n", { flag: "wx" });
    fs.rmSync(probe, { force: true });
    return { ok: true };
  } catch (error) {
    return { ok: false, reason: "after-pack-loc-unwritable", detail: `${value} cannot be written (${error?.code ?? "error"})` };
  }
}

/** Resolves SignTool and the Artifact Signing dlib through the existing check-only run. */
export function checkSigningTools() {
  const script = path.join(REPO_ROOT, "scripts", "windows-artifact-sign.ps1");
  const result = spawnSync(
    "powershell.exe",
    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-CheckOnly"],
    { encoding: "utf8", windowsHide: true, timeout: 120_000, stdio: ["ignore", "pipe", "pipe"] },
  );
  if (result.error || result.status !== 0) {
    return {
      ok: false,
      detail: `SignTool or the Artifact Signing dlib did not resolve (windows-artifact-sign.ps1 -CheckOnly exit ${result.status ?? "none"})`,
    };
  }
  const line = String(result.stdout ?? "").split(/\r?\n/).find((text) => text.startsWith("SignTool:")) ?? "SignTool resolved";
  return { ok: true, detail: line.trim() };
}

/** gh runs on its own keyring login (ghEnvironment drops the token variables). */
export function ghAuthStatus() {
  const result = spawnSync("gh", ["auth", "status"], {
    encoding: "utf8",
    windowsHide: true,
    env: ghEnvironment(process.env),
    timeout: 60_000,
    stdio: ["ignore", "pipe", "pipe"],
  });
  if (result.error?.code === "ENOENT") return { ok: false, reason: "gh-missing", detail: "gh was not found on PATH" };
  if (result.error) return { ok: false, reason: "gh-failed", detail: describeError(result.error) };
  if (result.status !== 0) {
    return { ok: false, reason: "gh-auth-missing", detail: "gh is not logged in on this machine (run gh auth login)" };
  }
  return { ok: true };
}

async function run(options) {
  let result;
  try {
    result = await runPreflight(options.root, {
      ...options,
      readiness: options.requireTrustedKey ? { env: process.env, deps: realReadinessDeps(options.root) } : null,
    });
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    return EXIT.REFUSED;
  }

  const report = {
    schema: 1,
    tool: SCRIPT,
    ok: result.ok,
    root: options.root,
    version: result.release.ok ? result.release.version : options.version ?? null,
    channel: result.release.ok ? result.release.channel : options.channel ?? null,
    commit: result.branch.commit,
    branch: result.branch.branch,
    detached: result.branch.detached,
    dirtyPaths: result.clean.dirtyPaths,
    trustedKey: result.trusted.key
      ? { path: result.trusted.key.path, keyId: result.trusted.key.keyId, fingerprint: result.trusted.key.fingerprint }
      : null,
    errors: result.errors,
    checks: result.checks,
  };

  if (options.json) {
    console.log(JSON.stringify(report));
  } else if (result.ok) {
    console.log(
      `release-preflight: ok (${report.version}, ${report.channel}, ${report.branch ?? "detached"}, ` +
        `${String(report.commit).slice(0, 12)})`,
    );
    for (const check of result.checks) {
      console.log(`  PASS ${check.name}${check.detail ? `: ${check.detail}` : ""}`);
    }
  } else {
    printRefusals("release-preflight", result.errors);
    for (const error of result.errors) {
      console.error(`  detail: [${error.reason}] ${error.detail}`);
    }
    console.error("release-preflight: no release session was written.");
  }

  return exitCodeForErrors(result.errors);
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = await main();
}
