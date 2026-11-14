#!/usr/bin/env node
/**
 * Windows release VM entry point (`npm run release:win`, `npm run release:win:continue`,
 * `npm run release:win:upload`).
 *
 * release:win      stable policy, preflight, the quick quality gate (no GUI E2E on a signing VM),
 *                  a release session that records that gate, a clean release folder, then continue.
 * --continue       the gate (policy, preflight with the signing readiness checks, session verify),
 *                  then build both RIDs, the live updater client rehearsal (release:rehearse:client, before any
 *                  paid signing), the installers, the signed bundle, GPG signatures, Authenticode, the single
 *                  draft with its assets, draft verification, and the mirror. The flow ends
 *                  at the mirror and never runs git. Publish, then `npm run release:cleanup`.
 * --from <step>    with --continue: run the gate, then the steps from <step> on. A gate step name starts
 *                  the flow there. `release:win:upload` is `--continue --from release:draft`.
 *
 * Every step is an npm script, except release:clean, which removes only artifacts/release/<version>.
 * Steps run through an injected runner, so scripts/run-release-selftest.js records the order without
 * running anything. Secrets come from .env, which the package.json entries load with
 * node --env-file-if-exists=.env. Each step gets only the credentials in STEP_ENV (stepEnv blanks the rest).
 * Failure cases: docs/failure-modes/release.md, "Release VM flow".
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { isDirectExecutionOf } from "./direct-execution.mjs";
import { EXIT, describeError } from "./lib/cli.js";
import { embeddedReleaseKeys, readPackageVersion } from "./lib/release-policy.js";
import { channelForVersion, tryParseSemVer } from "./lib/semver.js";
import { isExplicitTruthy, SIGNING_REQUIRED_VARIABLES } from "./release-policy.js";

const SCRIPT = "scripts/run-release.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const RIDS = Object.freeze(["win-x64", "win-arm64"]);
const MIRROR_STEP = "release:mirror";
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
  node ${SCRIPT}                       Stable policy, preflight, quick gate, session, clean, then continue.
  node ${SCRIPT} --continue            Gate, build, sign, bundle, upload the draft, verify it, mirror.
  node ${SCRIPT} --continue --from <step>
                                       The gate, then the steps from <step> on (release:win:upload is
                                       --from release:draft). Valid steps: ${continueStepNames().join(", ")}.

The trusted release key is the one embedded in src/Bwc.Update/Signing/TrustedKeys.cs. A placeholder
there refuses the run (docs/RELEASE.md, "User-owned steps"). Flags: --continue, --from, --help.`);
}

/** The run mode and the resume step from argv. Unknown flags and positional arguments are usage errors. */
export function parseReleaseArgs(argv) {
  const known = new Set(["--continue", "--help", "-h"]);
  const positionals = [];
  let continueFlag = false;
  let help = false;
  let from = null;
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--from") {
      const value = argv[index + 1];
      if (value === undefined || value.startsWith("-")) {
        throw usageError(`--from needs a step name. Valid steps: ${continueStepNames().join(", ")}`);
      }
      from = value;
      index += 1;
    } else if (arg.startsWith("--from=")) {
      from = arg.slice("--from=".length);
    } else if (arg.startsWith("-")) {
      if (!known.has(arg)) throw usageError(`Unknown release flag: ${arg}`);
      if (arg === "--continue") continueFlag = true;
      if (arg === "--help" || arg === "-h") help = true;
    } else {
      positionals.push(arg);
    }
  }
  if (positionals.length > 0) {
    throw usageError(`Unexpected argument: ${positionals[0]}. Usage: node ${SCRIPT} [--continue] [--from <step>]`);
  }
  if (help) return { help: true, mode: continueFlag ? "continue" : "win", from: null };
  if (from !== null) {
    if (!continueFlag) throw usageError("--from needs --continue");
    if (from === "") throw usageError(`--from needs a step name. Valid steps: ${continueStepNames().join(", ")}`);
    if (!continueStepNames().includes(from)) {
      throw usageError(`Unknown --from step: ${from}. Valid steps: ${continueStepNames().join(", ")}`);
    }
  }
  return { help: false, mode: continueFlag ? "continue" : "win", from };
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

/** The gate every continue run starts with. It runs before any build, so a missing credential fails first. */
function gateSteps() {
  return [
    { name: "release:policy", args: [] },
    { name: "release:preflight", args: ["--", "--require-trusted-key"] },
    { name: "release:session:verify", args: [] },
  ];
}

/** The names release:win:continue accepts for --from: every step of the continue flow, once each. */
export function continueStepNames() {
  return [...new Set(releaseSteps("continue", { version: "0.0.0", env: {} }).map((step) => step.name))];
}

/** The ordered steps of release:win (mode "win") or release:win:continue (mode "continue"). */
export function releaseSteps(mode, { version, env = process.env, from = null }) {
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
  const gate = gateSteps();
  const build = [
    { name: "build:win:x64", args: [] },
    { name: "build:win:arm64", args: [] },
    // The shipped client checks the live feed before any paid signing step runs.
    { name: "release:rehearse:client", args: [] },
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
    { name: MIRROR_STEP, args: ["--", "--dir", folder] },
  ];
  if (from === null || from === undefined) return [...gate, ...build];
  // A build step: the gate, then from that step. The first match is used for a repeated name.
  const inBuild = build.findIndex((step) => step.name === from);
  if (inBuild >= 0) return [...gate, ...build.slice(inBuild)];
  // A gate step: from that gate step through the whole build.
  const inGate = gate.findIndex((step) => step.name === from);
  if (inGate >= 0) return [...gate.slice(inGate), ...build];
  throw usageError(`Unknown --from step: ${from}. Valid steps: ${continueStepNames().join(", ")}`);
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

/** One cmd.exe argument. Plain tokens pass through; anything else is double-quoted with inner quotes doubled. */
export function quoteForCmd(arg) {
  const text = String(arg);
  if (/^[A-Za-z0-9_\-.:/\\=@,+]+$/.test(text)) return text;
  if (/[\r\n]/.test(text)) throw new Error("a release step argument cannot contain a line break");
  return `"${text.replace(/"/g, '""')}"`;
}

const UPDATE_KEY_ENV = Object.freeze(["TAURI_SIGNING_PRIVATE_KEY", "TAURI_SIGNING_PRIVATE_KEY_PASSWORD"]);
const AZURE_PUBLISHER_ENV = Object.freeze(["AZURE_ARTIFACT_SIGNING_PUBLISHER", "AZURE_ARTIFACT_SIGNING_PUBLISHER_DN"]);

/** Every credential name. A step that is not granted one gets it blank (see stepEnv). Any AZURE_* name counts too. */
export const CREDENTIAL_ENV = Object.freeze(["GPG_PASSPHRASE", ...UPDATE_KEY_ENV, ...SIGNING_REQUIRED_VARIABLES]);

/**
 * The credentials each npm step reads, from the step's script. Config such as GH_REPO, AFTER_PACK_LOC, and
 * SKIP_WIN_CODESIGN is not a credential and reaches every step. The preflight is granted the readiness inputs it
 * checks (update key, GPG passphrase, Azure presence). A step missing from this table gets no credential.
 */
export const STEP_ENV = Object.freeze({
  "release:policy": Object.freeze([]),
  "release:preflight": Object.freeze([...UPDATE_KEY_ENV, "GPG_PASSPHRASE", ...SIGNING_REQUIRED_VARIABLES]),
  "release:session:create": Object.freeze([]),
  "release:session:verify": Object.freeze([]),
  "test:all": Object.freeze([]),
  "build:win:x64": Object.freeze([]),
  "build:win:arm64": Object.freeze([]),
  "release:rehearse:client": Object.freeze([]),
  // build-installer.js runs windows-artifact-sign.ps1 (Authenticode through Azure Artifact Signing).
  "installer:x64": Object.freeze([...SIGNING_REQUIRED_VARIABLES]),
  "installer:arm64": Object.freeze([...SIGNING_REQUIRED_VARIABLES]),
  // assemble-bundle.js and sign-update-manifest.js read the update key (TAURI_SIGNING_*).
  "release:assemble": UPDATE_KEY_ENV,
  "release:sign:gpg": Object.freeze(["GPG_PASSPHRASE"]),
  // verify-windows-authenticode.ps1 reads the publisher name and subject only.
  "release:authenticode": AZURE_PUBLISHER_ENV,
  "release:draft": Object.freeze([]),
  "release:verify:draft": Object.freeze([]),
  "release:mirror": Object.freeze([]),
});

/** Runners start other release steps, which filter their own environment. They get the full environment. */
export const RUNNER_STEPS = Object.freeze(["release:win:continue"]);

function isCredentialName(name) {
  return CREDENTIAL_ENV.includes(name) || name.toUpperCase().startsWith("AZURE_");
}

/**
 * The environment for one release step. A credential the step is not granted is set to "". A defined variable
 * is never overwritten by a --env-file, so the step's own `node --env-file-if-exists=.env` cannot refill it.
 */
export function stepEnv(name, env = process.env) {
  const out = { ...env };
  if (RUNNER_STEPS.includes(name)) return out;
  const granted = new Set(STEP_ENV[name] ?? []);
  for (const key of Object.keys(out)) {
    if (isCredentialName(key) && !granted.has(key)) out[key] = "";
  }
  for (const key of CREDENTIAL_ENV) {
    if (!granted.has(key)) out[key] = "";
  }
  return out;
}

/** The default runner: npm run <name> [args], or the internal clean step. Stops on any failure. */
function npmRunner(root, version) {
  return (name, args = []) => {
    if (name === CLEAN_STEP) {
      cleanRelease(root, version);
      return;
    }
    const options = { cwd: root, stdio: "inherit", windowsHide: true, env: stepEnv(name, process.env) };
    // npm.cmd needs a shell on Windows. Node warns (DEP0190) when an argument array is passed with shell: true,
    // so the command line is built here, quoting each argument for cmd.exe.
    const result =
      process.platform === "win32"
        ? spawnSync(["npm.cmd", "run", name, ...args].map(quoteForCmd).join(" "), { ...options, shell: true })
        : spawnSync("npm", ["run", name, ...args], options);
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
  for (const step of releaseSteps(options.mode, { version, env, from: options.from })) {
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
