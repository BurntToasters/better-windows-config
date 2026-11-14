#!/usr/bin/env node
/**
 * Self-test for the Windows release VM flow. No network, no gh, no builds, no npm run.
 * Covers the stable policy, the ordered step lists of release:win and release:win:continue, --from
 * resume, the trusted set read from TrustedKeys.cs (a placeholder refuses), the quality-gate proof,
 * the package.json entry points, and release:cleanup in temporary git repositories. Failure cases are
 * listed in docs/failure-modes/release.md (sections "Release VM flow" and "Resume and cleanup").
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { EXIT } from "./lib/cli.js";
import { generateKeyPair } from "./lib/minisign.js";
import { embeddedReleaseKeys, qualityGateProof } from "./lib/release-policy.js";
import {
  assertStableReleaseOverridesAllowed,
  bwcSigningTargets,
  isStableReleaseVersion,
  signingPolicy,
  STABLE_FORBIDDEN_ENV,
} from "./release-policy.js";
import {
  CREDENTIAL_ENV,
  draftArgs,
  main,
  parseReleaseArgs,
  RUNNER_STEPS,
  releaseCleanTargets,
  releaseSteps,
  STEP_ENV,
  stepEnv,
} from "./run-release.js";

const SCRIPT = "scripts/run-release-selftest.js";
const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const VERSION = JSON.parse(fs.readFileSync(path.join(ROOT, "package.json"), "utf8")).version;
const HEAD = "0123456789abcdef0123456789abcdef01234567";
const CANONICAL_URL = `https://github.com/BurntToasters/better-windows-config/releases/download/v${VERSION}`;
const POLICY_ENV_NAMES = [
  ...STABLE_FORBIDDEN_ENV,
  "GH_REPO",
  "GH_REPO_OWNER",
  "GH_REPO_NAME",
  "RELEASE_DOWNLOAD_BASE_URL",
  "ALLOW_ASSET_REPLACE",
];

let passed = 0;
const failures = [];

function check(name, fn) {
  try {
    fn();
    passed += 1;
  } catch (error) {
    failures.push(`${name}: ${error instanceof Error ? error.message : String(error)}`);
  }
}

function assertEqual(actual, expected, label) {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) throw new Error(`${label}: expected ${e}, got ${a}`);
}

function assertThrows(fn, pattern, label) {
  try {
    fn();
  } catch (error) {
    const text = error instanceof Error ? error.message : String(error);
    if (!pattern.test(text)) throw new Error(`${label}: message "${text}" does not match ${pattern}`);
    return;
  }
  throw new Error(`${label}: expected a throw`);
}

/** A copy of the environment without the release variables, so the host shell cannot leak in. */
function cleanEnv(extra = {}) {
  const env = { ...process.env };
  for (const name of POLICY_ENV_NAMES) delete env[name];
  return { ...env, ...extra };
}

function runNode(args, env) {
  return spawnSync(process.execPath, args, {
    cwd: ROOT,
    encoding: "utf8",
    env,
    windowsHide: true,
    timeout: 60_000,
  });
}

const CANONICAL_FOR_STABLE = cleanEnv();

// ---- stable policy ----------------------------------------------------------------------------

check("policy: clean environment accepted for the stable version", () => {
  assertStableReleaseOverridesAllowed(cleanEnv(), VERSION);
});

for (const name of STABLE_FORBIDDEN_ENV) {
  for (const value of ["1", "true", "YES", "on", " 1 "]) {
    check(`policy: ${name}=${JSON.stringify(value)} refused for stable`, () => {
      assertThrows(
        () => assertStableReleaseOverridesAllowed(cleanEnv({ [name]: value }), VERSION),
        new RegExp(name),
        name,
      );
    });
  }
  for (const value of ["0", "false", "", "no"]) {
    check(`policy: ${name}=${JSON.stringify(value)} accepted for stable`, () => {
      assertStableReleaseOverridesAllowed(cleanEnv({ [name]: value }), VERSION);
    });
  }
}

check("policy: canonical repository variables accepted", () => {
  assertStableReleaseOverridesAllowed(
    cleanEnv({ GH_REPO: "BurntToasters/better-windows-config", GH_REPO_OWNER: "BurntToasters", GH_REPO_NAME: "better-windows-config" }),
    VERSION,
  );
});

check("policy: GH_REPO for a fork refused", () => {
  assertThrows(() => assertStableReleaseOverridesAllowed(cleanEnv({ GH_REPO: "someone/better-windows-config" }), VERSION), /GH_REPO="someone/, "GH_REPO");
});

check("policy: GH_REPO_OWNER other than BurntToasters refused", () => {
  assertThrows(() => assertStableReleaseOverridesAllowed(cleanEnv({ GH_REPO_OWNER: "someone" }), VERSION), /GH_REPO_OWNER="someone"/, "owner");
});

check("policy: GH_REPO_NAME other than better-windows-config refused", () => {
  assertThrows(() => assertStableReleaseOverridesAllowed(cleanEnv({ GH_REPO_NAME: "zinnia" }), VERSION), /GH_REPO_NAME="zinnia"/, "name");
});

check("policy: canonical RELEASE_DOWNLOAD_BASE_URL accepted, with and without a trailing slash", () => {
  assertStableReleaseOverridesAllowed(cleanEnv({ RELEASE_DOWNLOAD_BASE_URL: CANONICAL_URL }), VERSION);
  assertStableReleaseOverridesAllowed(cleanEnv({ RELEASE_DOWNLOAD_BASE_URL: `${CANONICAL_URL}/` }), VERSION);
});

check("policy: RELEASE_DOWNLOAD_BASE_URL on another host refused", () => {
  assertThrows(
    () => assertStableReleaseOverridesAllowed(cleanEnv({ RELEASE_DOWNLOAD_BASE_URL: "https://example.com/releases" }), VERSION),
    /RELEASE_DOWNLOAD_BASE_URL/,
    "host",
  );
});

check("policy: RELEASE_DOWNLOAD_BASE_URL for another version refused", () => {
  assertThrows(
    () => assertStableReleaseOverridesAllowed(cleanEnv({ RELEASE_DOWNLOAD_BASE_URL: CANONICAL_URL.replace(VERSION, "9.9.9") }), VERSION),
    /RELEASE_DOWNLOAD_BASE_URL/,
    "version",
  );
});

check("policy: beta version accepts the stable-only overrides", () => {
  assertStableReleaseOverridesAllowed(
    cleanEnv({ SKIP_WIN_CODESIGN: "1", ALLOW_ASSET_REPLACE: "1", GH_REPO_OWNER: "fork" }),
    "0.1.0-beta.1",
  );
});

check("policy: stable version detection", () => {
  assertEqual(isStableReleaseVersion("0.1.0"), true, "0.1.0");
  assertEqual(isStableReleaseVersion("0.1.0-beta.1"), false, "beta");
  assertEqual(isStableReleaseVersion("01.1.0"), false, "leading zero");
});

check("policy: existing exports keep working; local dev may still skip signing", () => {
  assertEqual(signingPolicy({ SKIP_WIN_CODESIGN: "1" }).skip, true, "signingPolicy skip");
  assertEqual(typeof bwcSigningTargets, "function", "bwcSigningTargets");
});

check("policy CLI: clean environment exits 0", () => {
  const result = runNode(["scripts/release-policy.js"], CANONICAL_FOR_STABLE);
  assertEqual(result.status, EXIT.OK, "exit");
});

check("policy CLI: SKIP_WIN_CODESIGN=1 exits 1 and names the variable", () => {
  const result = runNode(["scripts/release-policy.js"], cleanEnv({ SKIP_WIN_CODESIGN: "1" }));
  assertEqual(result.status, EXIT.REFUSED, "exit");
  if (!String(result.stderr).includes("SKIP_WIN_CODESIGN")) throw new Error(`stderr: ${result.stderr}`);
});

check("policy CLI: unknown argument exits 2", () => {
  const result = runNode(["scripts/release-policy.js", "--bogus"], cleanEnv());
  assertEqual(result.status, EXIT.USAGE, "exit");
});

// ---- argument parsing and step lists ----------------------------------------------------------

check("parseReleaseArgs: no argument is the win flow, --continue is the continuation", () => {
  assertEqual(parseReleaseArgs([]).mode, "win", "win");
  assertEqual(parseReleaseArgs(["--continue"]).mode, "continue", "continue");
});

check("parseReleaseArgs: unknown flag and positional argument fail", () => {
  assertThrows(() => parseReleaseArgs(["--skip-tests"]), /Unknown release flag/, "flag");
  assertThrows(() => parseReleaseArgs(["win"]), /Usage|unexpected/, "positional");
  assertThrows(() => parseReleaseArgs(["--continue", "--skip-e2e"]), /Unknown release flag/, "skip-e2e");
});

check("release:win step list is exact", () => {
  assertEqual(
    releaseSteps("win", { version: VERSION, env: cleanEnv() }),
    [
      { name: "release:policy", args: [] },
      { name: "release:preflight", args: ["--", "--require-trusted-key"] },
      { name: "test:all", args: ["--", "--quick", "--rid", "all"] },
      { name: "release:session:create", args: ["--", "--require-gate", "--force"] },
      { name: "release:clean", args: [] },
      { name: "release:win:continue", args: [] },
    ],
    "win",
  );
});

check("release:win:continue step list is exact for stable", () => {
  const folder = `artifacts/release/${VERSION}/stable`;
  assertEqual(
    releaseSteps("continue", { version: VERSION, env: cleanEnv() }),
    [
      { name: "release:policy", args: [] },
      { name: "release:preflight", args: ["--", "--require-trusted-key"] },
      { name: "release:session:verify", args: [] },
      { name: "build:win:x64", args: [] },
      { name: "build:win:arm64", args: [] },
      { name: "release:rehearse:client", args: [] },
      { name: "installer:x64", args: [] },
      { name: "installer:arm64", args: [] },
      {
        name: "release:assemble",
        args: [
          "--",
          "--version",
          VERSION,
          "--installer-win-x64",
          `artifacts/installer/BetterWindowsConfig-${VERSION}-win-x64-setup.exe`,
          "--installer-win-arm64",
          `artifacts/installer/BetterWindowsConfig-${VERSION}-win-arm64-setup.exe`,
          "--expires-hours",
          "8760",
        ],
      },
      { name: "release:sign:gpg", args: ["--", "--dir", folder] },
      { name: "release:authenticode", args: ["--", "-Files", `${folder}/BetterWindowsConfig-${VERSION}-win-x64-setup.exe`] },
      { name: "release:authenticode", args: ["--", "-Files", `${folder}/BetterWindowsConfig-${VERSION}-win-arm64-setup.exe`] },
      { name: "release:draft", args: ["--", "--apply"] },
      { name: "release:verify:draft", args: [] },
      { name: "release:mirror", args: ["--", "--dir", folder] },
    ],
    "continue",
  );
});

check("continue: mirror is last, the automatic flow never runs git, and no GUI E2E step runs", () => {
  const steps = releaseSteps("continue", { version: VERSION, env: cleanEnv() });
  assertEqual(steps.at(-1).name, "release:mirror", "last");
  for (const step of steps) {
    if (/^git\b/.test(step.name) || step.args.some((arg) => /\bgit\b|reset|clean/.test(arg))) {
      throw new Error(`git step found: ${step.name} ${step.args.join(" ")}`);
    }
  }
  if (steps.some((step) => step.name === "release:finalize" || step.name === "release:cleanup")) {
    throw new Error("the automatic flow still names release:finalize or release:cleanup");
  }
  const all = releaseSteps("win", { version: VERSION, env: cleanEnv() }).concat(steps);
  for (const step of all) {
    if (/e2e/i.test(step.name) || step.args.some((arg) => /e2e/i.test(arg))) {
      throw new Error(`GUI E2E step found: ${step.name} ${step.args.join(" ")}`);
    }
  }
});

check("draftArgs: stable never replaces assets, even with ALLOW_ASSET_REPLACE=1", () => {
  assertEqual(draftArgs(VERSION, cleanEnv()), ["--", "--apply"], "clean");
  assertEqual(draftArgs(VERSION, cleanEnv({ ALLOW_ASSET_REPLACE: "1" })), ["--", "--apply"], "forced");
});

check("draftArgs: non-stable replaces assets only with ALLOW_ASSET_REPLACE", () => {
  assertEqual(draftArgs("0.1.0-beta.1", cleanEnv()), ["--", "--apply"], "no env");
  assertEqual(draftArgs("0.1.0-beta.1", cleanEnv({ ALLOW_ASSET_REPLACE: "1" })), ["--", "--apply", "--replace-assets"], "env");
});

check("release:clean targets only artifacts/release/<version>, never the session file", () => {
  const targets = releaseCleanTargets(ROOT, VERSION);
  assertEqual(targets.length, 1, "count");
  if (path.basename(targets[0]) !== VERSION) throw new Error(`target ${targets[0]}`);
  if (targets.some((target) => target.endsWith("session.json"))) throw new Error("session.json targeted");
});

check("quality gate proof: missing, failed, or wrong-commit gates are refused", () => {
  assertEqual(qualityGateProof(null, HEAD).reason, "gate-missing", "missing");
  assertEqual(qualityGateProof({ pass: false, quick: true, git: { commit: HEAD } }, HEAD).reason, "gate-failed", "failed");
  assertEqual(
    qualityGateProof({ pass: true, quick: true, git: { commit: "f".repeat(40) } }, HEAD).reason,
    "gate-commit-mismatch",
    "commit",
  );
});

check("quality gate proof: a quick pass records e2e skipped; a full pass records e2e passed", () => {
  const quick = qualityGateProof({ pass: true, quick: true, git: { commit: HEAD } }, HEAD);
  assertEqual(quick.ok, true, "quick ok");
  assertEqual(quick.record.mode, "quick", "quick mode");
  assertEqual(quick.record.e2e, "skipped", "quick e2e");
  const full = qualityGateProof({ pass: true, quick: false, git: { commit: HEAD } }, HEAD);
  assertEqual(full.record.e2e, "passed", "full e2e");
});

// ---- trusted set from TrustedKeys.cs ----------------------------------------------------------

/** A C# string literal that holds exactly the given text (the form TrustedKeys.cs uses). */
function csLiteral(text) {
  const escaped = text
    .replace(/\\/g, "\\\\")
    .replace(/"/g, '\\"')
    .replace(/\r/g, "\\r")
    .replace(/\n/g, "\\n");
  return `"${escaped}"`;
}

/** Writes a minimal repository: package.json and TrustedKeys.cs, placeholder or with embedded keys. */
function writeRepo(dir, { placeholder = false, embedded = [] } = {}) {
  fs.mkdirSync(path.join(dir, "src", "Bwc.Update", "Signing"), { recursive: true });
  fs.writeFileSync(
    path.join(dir, "package.json"),
    `${JSON.stringify({ name: "run-release-selftest", version: VERSION }, null, 2)}\n`,
  );
  const constants = placeholder
    ? ['    private const string PlaceholderKey = "untrusted comment: PLACEHOLDER replace with the release public key\\nPLACEHOLDER-NOT-A-KEY";']
    : embedded.map((text, index) => `    private const string Key${index} = ${csLiteral(text)};`);
  const names = placeholder ? ["PlaceholderKey"] : embedded.map((_, index) => `Key${index}`);
  fs.writeFileSync(
    path.join(dir, "src", "Bwc.Update", "Signing", "TrustedKeys.cs"),
    [
      "namespace Bwc.Update;",
      "",
      "public static class TrustedKeys",
      "{",
      `    public const bool IsPlaceholder = ${placeholder ? "true" : "false"};`,
      ...constants,
      `    public static readonly IReadOnlyList<string> Embedded = [${names.join(", ")}];`,
      "}",
      "",
    ].join("\n"),
  );
}

const work = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-run-release-selftest-"));
try {
  const placeholderRepo = path.join(work, "placeholder");
  writeRepo(placeholderRepo, { placeholder: true });
  const testKey = generateKeyPair({ comment: "untrusted comment: BWC run-release selftest key" });
  const keyRepo = path.join(work, "embedded");
  writeRepo(keyRepo, { embedded: [testKey.publicKeyText] });
  const malformedRepo = path.join(work, "malformed");
  writeRepo(malformedRepo, { embedded: ["untrusted comment: broken\nnot-a-key\n"] });
  const noSourceRepo = path.join(work, "no-source");
  fs.mkdirSync(noSourceRepo, { recursive: true });
  fs.writeFileSync(path.join(noSourceRepo, "package.json"), `${JSON.stringify({ name: "x", version: VERSION })}\n`);

  check("embedded keys: a placeholder TrustedKeys.cs is refused, pointing at the user steps", () => {
    const result = embeddedReleaseKeys(placeholderRepo);
    assertEqual(result.ok, false, "ok");
    assertEqual(result.reason, "placeholder-key", "reason");
    if (!/docs\/RELEASE\.md/.test(result.detail) || !/User-owned steps/.test(result.detail)) {
      throw new Error(`detail does not point at docs/RELEASE.md: ${result.detail}`);
    }
  });

  check("embedded keys: a TrustedKeys.cs with a real key is the trusted set, byte for byte", () => {
    const result = embeddedReleaseKeys(keyRepo);
    assertEqual(result.ok, true, "ok");
    assertEqual(result.keys, [testKey.publicKeyText], "keys");
  });

  check("embedded keys: a missing TrustedKeys.cs is refused", () => {
    assertEqual(embeddedReleaseKeys(noSourceRepo).reason, "missing-trusted-key-source", "reason");
  });

  check("embedded keys: a malformed embedded key is refused", () => {
    const result = embeddedReleaseKeys(malformedRepo);
    assertEqual(result.ok, false, "ok");
    if (!result.reason) throw new Error("no reason");
  });

  check("main: a placeholder TrustedKeys.cs refuses before any step", () => {
    const calls = [];
    assertThrows(
      () => main(["--continue"], (name, args) => calls.push([name, args]), { root: placeholderRepo, env: cleanEnv() }),
      /User-owned steps/,
      "placeholder",
    );
    assertEqual(calls, [], "calls");
  });

  check("main --continue records exactly the continue step list when a real key is embedded", () => {
    const calls = [];
    main(["--continue"], (name, args) => calls.push({ name, args }), { root: keyRepo, env: cleanEnv() });
    assertEqual(calls, releaseSteps("continue", { version: VERSION, env: cleanEnv() }), "recorded");
  });

  check("main with no flag records release:win steps and hands off to release:win:continue", () => {
    const calls = [];
    main([], (name, args) => calls.push({ name, args }), { root: keyRepo, env: cleanEnv() });
    assertEqual(calls, releaseSteps("win", { version: VERSION, env: cleanEnv() }), "recorded");
  });

  check("main stops at the first failing step and starts no later step", () => {
    const calls = [];
    const failing = (name) => {
      calls.push(name);
      if (name === "release:preflight") throw new Error("preflight refused");
    };
    assertThrows(() => main([], failing, { root: keyRepo, env: cleanEnv() }), /preflight refused/, "failure");
    assertEqual(calls, ["release:policy", "release:preflight"], "calls");
  });
} finally {
  fs.rmSync(work, { recursive: true, force: true });
}

check("run-release CLI: unknown flag exits 2 and names it", () => {
  const result = runNode(["scripts/run-release.js", "--skip-tests"], cleanEnv());
  assertEqual(result.status, EXIT.USAGE, "exit");
  if (!String(result.stderr).includes("Unknown release flag")) throw new Error(`stderr: ${result.stderr}`);
});

// ---- package.json entry points ----------------------------------------------------------------

const pkgScripts = JSON.parse(fs.readFileSync(path.join(ROOT, "package.json"), "utf8")).scripts;

check("package.json: the release entry points exist", () => {
  for (const name of [
    "release:win",
    "release:win:continue",
    "release:win:resume",
    "release:win:upload",
    "release:cleanup",
    "release:mirror",
    "release:sign:gpg",
    "release:verify:draft",
    "release:verify:published",
    "release:publish",
  ]) {
    if (typeof pkgScripts[name] !== "string") throw new Error(`missing ${name}`);
  }
});

check("package.json: the given commands are exact", () => {
  assertEqual(pkgScripts["release:sign:gpg"], "node --env-file-if-exists=.env scripts/gpg-sign.js", "sign");
  assertEqual(pkgScripts["release:mirror"], "node --env-file-if-exists=.env scripts/finalize-release-assets.js", "mirror");
  assertEqual(
    pkgScripts["release:verify:published"],
    "node --env-file-if-exists=.env scripts/validate-updater-live.js --expected-version=current",
    "published",
  );
});

check("package.json: existing script names are kept", () => {
  for (const name of [
    "release:session",
    "release:draft",
    "release:verify",
    "release:preflight",
    "release:assemble",
    "release:sign",
    "release:validate",
    "release:dry-run",
    "release:selftest",
    "build:win:x64",
    "build:win:arm64",
    "release:rehearse:client",
    "installer:x64",
    "installer:arm64",
    "setup:win:artifact-signing",
    "signing:selftest",
    "test:all",
    "validate",
  ]) {
    if (typeof pkgScripts[name] !== "string") throw new Error(`missing ${name}`);
  }
});

check("package.json: release:win runs the runner without a platform argument", () => {
  assertEqual(pkgScripts["release:win"], "node --env-file-if-exists=.env scripts/run-release.js", "win");
  assertEqual(pkgScripts["release:win:continue"], "node --env-file-if-exists=.env scripts/run-release.js --continue", "continue");
});

check("package.json: no script reads a BWC-only release key variable", () => {
  for (const [name, command] of Object.entries(pkgScripts)) {
    if (/RELEASE_TRUSTED_KEY/.test(command)) throw new Error(`${name} names RELEASE_TRUSTED_KEY`);
  }
});

// ---- per-step credentials: stepEnv and STEP_ENV -------------------------------------------------
// Sentinel values stand in for secrets. No real credential is read, and no value is printed.

const SENTINEL = Object.fromEntries(CREDENTIAL_ENV.map((name) => [name, `sentinel-${name.toLowerCase()}`]));
const SOURCE_ENV = { ...cleanEnv(), ...SENTINEL, GH_REPO: "BurntToasters/better-windows-config", AFTER_PACK_LOC: "D:/mirror" };
const STEP_NAMES = new Set(
  [...releaseSteps("continue", { version: VERSION, env: {} }), ...releaseSteps("win", { version: VERSION, env: {} })]
    .map((step) => step.name)
    .filter((name) => name !== "release:clean"),
);

check("env table: every release step has a STEP_ENV entry or is a runner", () => {
  for (const name of STEP_NAMES) {
    if (!(name in STEP_ENV) && !RUNNER_STEPS.includes(name)) throw new Error(`no STEP_ENV entry for ${name}`);
  }
});

check("env table: each step gets exactly its granted credentials, others blank", () => {
  for (const name of Object.keys(STEP_ENV)) {
    const env = stepEnv(name, SOURCE_ENV);
    for (const key of CREDENTIAL_ENV) {
      const want = STEP_ENV[name].includes(key) ? SENTINEL[key] : "";
      if (env[key] !== want) throw new Error(`${name} ${key}: expected ${want === "" ? "blank" : "its value"}`);
    }
  }
});

check("env table: a step not in the table gets no credential", () => {
  const env = stepEnv("not:a-step", SOURCE_ENV);
  for (const key of CREDENTIAL_ENV) assertEqual(env[key], "", `unknown step ${key}`);
});

check("env table: runner steps keep the full environment", () => {
  const env = stepEnv("release:win:continue", SOURCE_ENV);
  for (const key of CREDENTIAL_ENV) assertEqual(env[key], SENTINEL[key], `runner ${key}`);
});

check("env table: config reaches every step and the source environment is not changed", () => {
  for (const name of ["build:win:x64", "release:draft", "release:mirror"]) {
    const env = stepEnv(name, SOURCE_ENV);
    assertEqual(env.GH_REPO, SOURCE_ENV.GH_REPO, `${name} GH_REPO`);
    assertEqual(env.AFTER_PACK_LOC, SOURCE_ENV.AFTER_PACK_LOC, `${name} AFTER_PACK_LOC`);
  }
  assertEqual(SOURCE_ENV.GPG_PASSPHRASE, SENTINEL.GPG_PASSPHRASE, "source untouched");
});

check("env table: a blank credential is not refilled by the step's own --env-file", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-run-release-envfile-"));
  try {
    const file = path.join(dir, "probe.env");
    fs.writeFileSync(file, "GPG_PASSPHRASE=from-file\nGH_REPO_NAME=from-file\n");
    const probe = "process.stdout.write(JSON.stringify([process.env.GPG_PASSPHRASE, process.env.GH_REPO_NAME]))";
    const read = (step) => {
      const result = spawnSync(process.execPath, [`--env-file-if-exists=${file}`, "-e", probe], {
        encoding: "utf8",
        env: stepEnv(step, { ...cleanEnv(), GPG_PASSPHRASE: "granted-or-not" }),
        windowsHide: true,
      });
      if (result.status !== 0) throw new Error(`probe exited ${result.status}: ${result.stderr}`);
      return JSON.parse(result.stdout);
    };
    assertEqual(read("build:win:x64"), ["", "from-file"], "blank credential stays blank");
    assertEqual(read("release:sign:gpg"), ["granted-or-not", "from-file"], "granted credential is kept");
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ---- --from: resume at a named step -------------------------------------------------------------

const PROLOGUE = [
  { name: "release:policy", args: [] },
  { name: "release:preflight", args: ["--", "--require-trusted-key"] },
  { name: "release:session:verify", args: [] },
];

function stepNames(steps) {
  return steps.map((step) => step.name);
}

const fromWork = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-run-release-from-"));
const fromRoot = path.join(fromWork, "repo");
writeRepo(fromRoot, { embedded: [generateKeyPair({ comment: "untrusted comment: BWC run-release from key" }).publicKeyText] });

check("--from: parsed with a separate value or with =, and the mode stays continue", () => {
  assertEqual(parseReleaseArgs(["--continue", "--from", "release:draft"]).from, "release:draft", "separate");
  assertEqual(parseReleaseArgs(["--continue", "--from=release:draft"]).from, "release:draft", "equals");
  assertEqual(parseReleaseArgs(["--continue", "--from", "release:draft"]).mode, "continue", "mode");
});

check("--from: without --continue, or without a value, is a usage error (exit 2)", () => {
  for (const argv of [["--from", "release:draft"], ["--continue", "--from"], ["--continue", "--from", "--continue"]]) {
    try {
      parseReleaseArgs(argv);
    } catch (error) {
      assertEqual(error.exitCode, EXIT.USAGE, `exit for ${argv.join(" ")}`);
      continue;
    }
    throw new Error(`accepted ${argv.join(" ")}`);
  }
});

check("--from: an unknown step is refused with exit 2 and lists the valid names; nothing runs", () => {
  const calls = [];
  try {
    main(["--continue", "--from", "release:nope"], (name) => calls.push(name), { root: fromRoot, env: cleanEnv() });
  } catch (error) {
    assertEqual(error.exitCode, EXIT.USAGE, "exit");
    for (const name of ["release:draft", "release:mirror", "release:sign:gpg", "release:authenticode"]) {
      if (!error.message.includes(name)) throw new Error(`valid name ${name} not listed: ${error.message}`);
    }
    assertEqual(calls, [], "calls");
    return;
  }
  throw new Error("an unknown step was accepted");
});

check("--from release:draft: the gate runs, then draft, verify, and mirror only", () => {
  assertEqual(
    stepNames(releaseSteps("continue", { version: VERSION, env: cleanEnv(), from: "release:draft" })),
    [...stepNames(PROLOGUE), "release:draft", "release:verify:draft", "release:mirror"],
    "steps",
  );
});

check("--from release:sign:gpg: the gate, then signing, Authenticode, draft, verify, and mirror", () => {
  assertEqual(
    stepNames(releaseSteps("continue", { version: VERSION, env: cleanEnv(), from: "release:sign:gpg" })),
    [
      ...stepNames(PROLOGUE),
      "release:sign:gpg",
      "release:authenticode",
      "release:authenticode",
      "release:draft",
      "release:verify:draft",
      "release:mirror",
    ],
    "steps",
  );
});

check("--from release:authenticode starts at the first Authenticode step", () => {
  const steps = releaseSteps("continue", { version: VERSION, env: cleanEnv(), from: "release:authenticode" });
  assertEqual(steps.filter((step) => step.name === "release:authenticode").length, 2, "both signing steps");
  assertEqual(steps.find((step) => step.name === "release:authenticode").args[1], "-Files", "args");
});

check("--from a gate step: the flow runs from that gate through every build step", () => {
  const steps = releaseSteps("continue", { version: VERSION, env: cleanEnv(), from: "release:preflight" });
  assertEqual(stepNames(steps).slice(0, 2), ["release:preflight", "release:session:verify"], "gate");
  assertEqual(steps.at(-1).name, "release:mirror", "last");
  assertEqual(steps.length, releaseSteps("continue", { version: VERSION, env: cleanEnv() }).length - 1, "one policy step less");
});

check("--from through main records the same steps as releaseSteps", () => {
  const calls = [];
  main(["--continue", "--from", "release:draft"], (name, args) => calls.push({ name, args }), {
    root: fromRoot,
    env: cleanEnv(),
  });
  assertEqual(
    calls,
    releaseSteps("continue", { version: VERSION, env: cleanEnv(), from: "release:draft" }),
    "recorded",
  );
});

check("release:win:upload is the continue flow from release:draft, and release:finalize is gone", () => {
  assertEqual(
    pkgScripts["release:win:upload"],
    "node --env-file-if-exists=.env scripts/run-release.js --continue --from release:draft",
    "upload",
  );
  if ("release:finalize" in pkgScripts) throw new Error("release:finalize is still in package.json");
});

check("release:cleanup is a plain node script, with no npm_package_version or @{u} expansion", () => {
  assertEqual(pkgScripts["release:cleanup"], "node scripts/release-cleanup.js", "cleanup");
  for (const [name, command] of Object.entries(pkgScripts)) {
    if (/%npm_package_version%|@\{u\}|git reset/.test(command)) throw new Error(`${name} has a forbidden expansion or git reset`);
  }
});

// ---- release:cleanup, run against temporary git repositories (no network, no BWC remote) -----

const CLEANUP_SCRIPT = path.join(ROOT, "scripts", "release-cleanup.js");
const GIT_IDENTITY = ["-c", "user.name=BWC Selftest", "-c", "user.email=selftest@example.invalid"];

function gitIn(cwd, args) {
  const result = spawnSync("git", [...GIT_IDENTITY, ...args], {
    cwd,
    encoding: "utf8",
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
  });
  if (result.status !== 0) throw new Error(`git ${args.join(" ")} failed in ${cwd}: ${result.stderr}`);
  return result.stdout.trim();
}

function writeIn(root, relative, text) {
  fs.mkdirSync(path.dirname(path.join(root, relative)), { recursive: true });
  fs.writeFileSync(path.join(root, relative), text);
}

function runCleanup(root) {
  return spawnSync(process.execPath, [CLEANUP_SCRIPT, "--root", root], {
    cwd: ROOT,
    encoding: "utf8",
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
    env: cleanEnv(),
    timeout: 120_000,
  });
}

/** A bare origin and a work clone on main, pushed. publish-result.json records a published release. */
function cleanupFixture(base, { published = true } = {}) {
  const origin = path.join(base, "origin.git");
  gitIn(base, ["init", "--bare", "-q", "-b", "main", origin]);
  const work = path.join(base, "work");
  fs.mkdirSync(work);
  gitIn(work, ["init", "-q", "-b", "main"]);
  writeIn(work, ".gitignore", "artifacts/\n");
  writeIn(work, "package.json", `${JSON.stringify({ name: "cleanup-selftest", version: VERSION }, null, 2)}\n`);
  writeIn(work, "README.md", "one\n");
  gitIn(work, ["add", "-A"]);
  gitIn(work, ["commit", "-q", "-m", "release"]);
  gitIn(work, ["remote", "add", "origin", origin]);
  gitIn(work, ["push", "-q", "-u", "origin", "main"]);
  if (published) {
    writeIn(
      work,
      `artifacts/release/${VERSION}/stable/publish-result.json`,
      `${JSON.stringify({ schema: 1, ok: true, tag: `v${VERSION}`, version: VERSION, channel: "stable", published: true })}\n`,
    );
  }
  return { origin, work };
}

/** Pushes one commit to origin from a second clone, so the work clone falls behind. */
function pushFromSecondClone(base, origin, relative) {
  const other = path.join(base, "other");
  gitIn(base, ["clone", "-q", origin, other]);
  writeIn(other, relative, "upstream change\n");
  gitIn(other, ["add", "-A"]);
  gitIn(other, ["commit", "-q", "-m", "upstream change"]);
  gitIn(other, ["push", "-q", "origin", "main"]);
}

const cleanupBases = [];
function freshBase() {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-cleanup-selftest-"));
  cleanupBases.push(base);
  return base;
}

cleanupBases.push(fromWork);

try {
  check("cleanup: a clean tree behind its upstream is reset to it; git-ignored artifacts survive", () => {
    const base = freshBase();
    const { origin, work } = cleanupFixture(base);
    pushFromSecondClone(base, origin, "upstream.txt");
    writeIn(work, "artifacts/keep.txt", "ignored, must survive\n");
    const result = runCleanup(work);
    assertEqual(result.status, EXIT.OK, `exit (${result.stderr})`);
    assertEqual(gitIn(work, ["rev-parse", "HEAD"]), gitIn(origin, ["rev-parse", "main"]), "HEAD is upstream");
    if (!fs.existsSync(path.join(work, "upstream.txt"))) throw new Error("upstream file missing after reset");
    if (!fs.existsSync(path.join(work, "artifacts", "keep.txt"))) throw new Error("ignored artifact was removed");
  });

  check("cleanup: a modified tracked file refuses with dirty-tree and resets nothing", () => {
    const base = freshBase();
    const { work } = cleanupFixture(base);
    const before = gitIn(work, ["rev-parse", "HEAD"]);
    writeIn(work, "README.md", "edited\n");
    const result = runCleanup(work);
    assertEqual(result.status, EXIT.REFUSED, "exit");
    if (!result.stderr.includes("[dirty-tree]")) throw new Error(`stderr: ${result.stderr}`);
    assertEqual(fs.readFileSync(path.join(work, "README.md"), "utf8"), "edited\n", "edit kept");
    assertEqual(gitIn(work, ["rev-parse", "HEAD"]), before, "HEAD unchanged");
  });

  check("cleanup: an untracked file refuses with dirty-tree and is kept", () => {
    const base = freshBase();
    const { work } = cleanupFixture(base);
    writeIn(work, "scratch.txt", "scratch\n");
    const result = runCleanup(work);
    assertEqual(result.status, EXIT.REFUSED, "exit");
    if (!result.stderr.includes("[dirty-tree]")) throw new Error(`stderr: ${result.stderr}`);
    if (!fs.existsSync(path.join(work, "scratch.txt"))) throw new Error("untracked file removed");
  });

  check("cleanup: no publish record refuses with not-published", () => {
    const base = freshBase();
    const { work } = cleanupFixture(base, { published: false });
    const result = runCleanup(work);
    assertEqual(result.status, EXIT.REFUSED, "exit");
    if (!result.stderr.includes("[not-published]")) throw new Error(`stderr: ${result.stderr}`);
  });

  check("cleanup: a publish record that is not published refuses with not-published", () => {
    const base = freshBase();
    const { work } = cleanupFixture(base);
    writeIn(
      work,
      `artifacts/release/${VERSION}/stable/publish-result.json`,
      `${JSON.stringify({ ok: true, tag: `v${VERSION}`, version: VERSION, channel: "stable", published: false })}\n`,
    );
    const result = runCleanup(work);
    if (!result.stderr.includes("[not-published]")) throw new Error(`stderr: ${result.stderr}`);
  });

  check("cleanup: a local commit that is not on the upstream refuses with head-not-upstream and keeps it", () => {
    const base = freshBase();
    const { work } = cleanupFixture(base);
    writeIn(work, "local.txt", "unpushed\n");
    gitIn(work, ["add", "-A"]);
    gitIn(work, ["commit", "-q", "-m", "local only"]);
    const before = gitIn(work, ["rev-parse", "HEAD"]);
    const result = runCleanup(work);
    assertEqual(result.status, EXIT.REFUSED, "exit");
    if (!result.stderr.includes("[head-not-upstream]")) throw new Error(`stderr: ${result.stderr}`);
    assertEqual(gitIn(work, ["rev-parse", "HEAD"]), before, "HEAD unchanged");
  });

  check("cleanup: no upstream refuses with no-upstream", () => {
    const base = freshBase();
    const { work } = cleanupFixture(base);
    gitIn(work, ["branch", "--unset-upstream"]);
    const result = runCleanup(work);
    assertEqual(result.status, EXIT.REFUSED, "exit");
    if (!result.stderr.includes("[no-upstream]")) throw new Error(`stderr: ${result.stderr}`);
  });

  check("cleanup: a failed fetch refuses with fetch-failed (exit 4) and resets nothing", () => {
    const base = freshBase();
    const { work } = cleanupFixture(base);
    gitIn(work, ["remote", "set-url", "origin", path.join(base, "missing.git")]);
    const before = gitIn(work, ["rev-parse", "HEAD"]);
    const result = runCleanup(work);
    assertEqual(result.status, 4, "exit");
    if (!result.stderr.includes("[fetch-failed]")) throw new Error(`stderr: ${result.stderr}`);
    assertEqual(gitIn(work, ["rev-parse", "HEAD"]), before, "HEAD unchanged");
  });

  check("cleanup: a package.json version that is not SemVer refuses with exit 2", () => {
    const base = freshBase();
    const { work } = cleanupFixture(base);
    writeIn(work, "package.json", `${JSON.stringify({ name: "x", version: "v9" })}\n`);
    const result = runCleanup(work);
    assertEqual(result.status, EXIT.USAGE, "exit");
  });
} finally {
  for (const base of cleanupBases) fs.rmSync(base, { recursive: true, force: true });
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`FAIL ${failure}`);
  console.error(`${SCRIPT}: ${passed} passed, ${failures.length} failed`);
  process.exitCode = EXIT.REFUSED;
} else {
  console.log(`${SCRIPT}: ${passed}/${passed} passed`);
}
