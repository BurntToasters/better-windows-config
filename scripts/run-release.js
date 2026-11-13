#!/usr/bin/env node
/**
 * Windows release VM entry point (`npm run release:win`, `npm run release:win:continue`).
 *
 * release:win      stable policy, preflight, the quick quality gate (no GUI E2E on a signing VM),
 *                  a release session that records that gate, a clean release folder, then continue.
 * --continue       session check, build both RIDs (ship), the installers, the signed bundle, GPG
 *                  signatures, Authenticode, the single draft with its assets, draft verification,
 *                  and release:finalize last.
 *
 * Every step is an npm script, except release:clean, which removes only artifacts/release/<version>.
 * Steps run through an injected runner, so scripts/run-release-selftest.js records the order without
 * running anything. Secrets come from .env, which the package.json entries load with
 * node --env-file-if-exists=.env. Failure cases: docs/failure-modes/release.md, "Release VM flow".
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { isDirectExecutionOf } from "./direct-execution.mjs";
import { EXIT, describeError } from "./lib/cli.js";
import { embeddedReleaseKeys, readPackageVersion } from "./lib/release-policy.js";
import { channelForVersion, tryParseSemVer } from "./lib/semver.js";
import { isExplicitTruthy } from "./release-policy.js";

const SCRIPT = "scripts/run-release.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const RIDS = Object.freeze(["win-x64", "win-arm64"]);
const FINALIZE_STEP = "release:finalize";
const CLEAN_STEP = "release:clean";
// The signed expiry of a release manifest, in hours (one year). Installed copies refuse an expired feed, and
// releases are not re-signed between versions, so a short expiry would break update checks for everyone.
export const RELEASE_EXPIRES_HOURS = 8760;

function usageError(message, exitCode = EXIT.USAGE) {
  const error = new Error(message);
  error.exitCode = exitCode;
  return error;
}

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT}              Stable policy, preflight, quick gate, session, clean, then continue.
  node ${SCRIPT} --continue   Build, sign, bundle, upload the draft, verify it, then finalize.

The trusted release key is the one embedded in src/Bwc.Update/Signing/TrustedKeys.cs. A placeholder
there refuses the run (docs/RELEASE.md, "User-owned steps"). No flag other than --continue and --help.`);
}

/** The run mode from argv. Unknown flags and positional arguments are usage errors. */
export function parseReleaseArgs(argv) {
  const flags = argv.filter((arg) => arg.startsWith("-"));
  const positionals = argv.filter((arg) => !arg.startsWith("-"));
  const known = new Set(["--continue", "--help", "-h"]);
  const unknown = flags.filter((flag) => !known.has(flag));
  if (unknown.length > 0) {
    throw usageError(`Unknown release flag: ${unknown.join(", ")}`);
  }
  if (positionals.length > 0) {
    throw usageError(`Unexpected argument: ${positionals[0]}. Usage: node ${SCRIPT} [--continue]`);
  }
  return {
    help: flags.includes("--help") || flags.includes("-h"),
    mode: flags.includes("--continue") ? "continue" : "win",
  };
}

/** Relative folder that holds one channel's assembled bundle: artifacts/release/<version>/<channel>. */
export function releaseFolder(version) {
  return `artifacts/release/${version}/${channelForVersion(version)}`;
}

/** The setup EXE that build-installer.js writes for one RID (ship flavor). */
export function installerPath(version, rid) {
  return `artifacts/installer/BetterWindowsConfig-${version}-${rid}-setup.exe`;
}

/**
 * The draft arguments. A stable release never replaces an asset. A non-stable release replaces one
 * only when ALLOW_ASSET_REPLACE is truthy.
 */
export function draftArgs(version, env = process.env) {
  const args = ["--", "--apply"];
  if (channelForVersion(version) !== "stable" && isExplicitTruthy(env.ALLOW_ASSET_REPLACE)) {
    args.push("--replace-assets");
  }
  return args;
}

/** The folders release:clean removes. The session file (artifacts/release/session.json) is never one. */
export function releaseCleanTargets(root, version) {
  return [path.join(root, "artifacts", "release", version)];
}

/** The ordered steps of release:win (mode "win") or release:win:continue (mode "continue"). */
export function releaseSteps(mode, { version, env = process.env }) {
  if (mode === "win") {
    return [
      { name: "release:policy", args: [] },
      { name: "release:preflight", args: ["--", "--require-trusted-key"] },
      { name: "test:all", args: ["--", "--quick", "--rid", "all"] },
      // --force replaces a session left by an earlier attempt, so the proof is for this run's gate.
      { name: "release:session:create", args: ["--", "--require-gate", "--force"] },
      { name: CLEAN_STEP, args: [] },
      { name: "release:win:continue", args: [] },
    ];
  }
  const folder = releaseFolder(version);
  const [x64, arm64] = RIDS.map((rid) => path.basename(installerPath(version, rid)));
  return [
    { name: "release:policy", args: [] },
    { name: "release:session:verify", args: [] },
    { name: "build:win:x64", args: [] },
    { name: "build:win:arm64", args: [] },
    { name: "installer:x64", args: [] },
    { name: "installer:arm64", args: [] },
    {
      name: "release:assemble",
      args: [
        "--",
        "--version",
        version,
        "--installer-win-x64",
        installerPath(version, "win-x64"),
        "--installer-win-arm64",
        installerPath(version, "win-arm64"),
        "--expires-hours",
        String(RELEASE_EXPIRES_HOURS),
      ],
    },
    { name: "release:sign:gpg", args: ["--", "--dir", folder] },
    { name: "release:authenticode", args: ["--", "-Files", `${folder}/${x64}`] },
    { name: "release:authenticode", args: ["--", "-Files", `${folder}/${arm64}`] },
    { name: "release:draft", args: draftArgs(version, env) },
    { name: "release:verify:draft", args: [] },
    { name: FINALIZE_STEP, args: [] },
  ];
}

function printFinalizeWarning() {
  console.warn(
    [
      "",
      "WARNING: release:finalize mirrors the release folder, then runs",
      '  git fetch origin && git reset --hard @{u} && git clean -fd',
      "That discards every local change and untracked file in this checkout. Run it only on the",
      "disposable release VM. Git-ignored artifacts/ survive, and the draft is already verified.",
      "",
    ].join("\n"),
  );
}

function cleanRelease(root, version) {
  if (!tryParseSemVer(version)) {
    throw usageError(`refusing to clean for a non-SemVer version: ${version}`);
  }
  for (const target of releaseCleanTargets(root, version)) {
    fs.rmSync(target, { recursive: true, force: true });
    console.log(`release:clean: removed ${path.relative(root, target).split(path.sep).join("/")}`);
  }
}

/** The default runner: npm run <name> [args], or the internal clean step. Stops on any failure. */
function npmRunner(root, version) {
  return (name, args = []) => {
    if (name === CLEAN_STEP) {
      cleanRelease(root, version);
      return;
    }
    const npm = process.platform === "win32" ? "npm.cmd" : "npm";
    const result = spawnSync(npm, ["run", name, ...args], {
      cwd: root,
      stdio: "inherit",
      windowsHide: true,
      shell: process.platform === "win32",
      env: process.env,
    });
    if (result.error) {
      throw usageError(`could not start npm run ${name}: ${result.error.message}`, EXIT.EXTERNAL);
    }
    if (result.status !== 0) {
      throw usageError(`npm run ${name} failed with exit ${result.status ?? 1}`, result.status ?? EXIT.REFUSED);
    }
  };
}

/**
 * Runs the release flow. Throws on the first failing step. The runner and the root (the repository
 * whose package.json and TrustedKeys.cs are read) are injected for tests. env is read for
 * ALLOW_ASSET_REPLACE only.
 */
export function main(argv = process.argv.slice(2), runner = null, { root = REPO_ROOT, env = process.env } = {}) {
  const options = parseReleaseArgs(argv);
  if (options.help) {
    printHelp();
    return EXIT.OK;
  }
  const version = readPackageVersion(root);
  if (!tryParseSemVer(version)) {
    throw usageError(`package.json version is not SemVer: ${version}`);
  }
  // Refuse before any step when the built-in release key is still the placeholder (or unreadable).
  const keys = embeddedReleaseKeys(root);
  if (!keys.ok) {
    throw usageError(keys.detail, keys.reason === "missing-trusted-key-source" ? EXIT.MISSING : EXIT.REFUSED);
  }
  const run = runner ?? npmRunner(root, version);
  for (const step of releaseSteps(options.mode, { version, env })) {
    // An injected runner (the selftest) runs nothing, so the warning is for real runs only.
    if (step.name === FINALIZE_STEP && runner === null) printFinalizeWarning();
    run(step.name, step.args);
  }
  return EXIT.OK;
}

if (isDirectExecutionOf(import.meta.url)) {
  try {
    process.exitCode = main();
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    process.exitCode = error?.exitCode ?? EXIT.REFUSED;
  }
}
