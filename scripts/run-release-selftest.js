#!/usr/bin/env node
/**
 * Self-test for the Windows release VM flow. No network, no gh, no builds, no npm run.
 * Covers the stable policy, the ordered step lists of release:win and release:win:continue, the
 * trusted set read from TrustedKeys.cs (a placeholder refuses), the quality-gate proof, and the
 * package.json entry points. Failure cases are listed in docs/failure-modes/release.md
 * (section "Release VM flow").
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
import { draftArgs, main, parseReleaseArgs, releaseCleanTargets, releaseSteps } from "./run-release.js";

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
          VERSION,
          "--installer-win-x64",
          `artifacts/installer/BetterWindowsConfig-${VERSION}-win-x64-setup.exe`,
          "--installer-win-arm64",
          `artifacts/installer/BetterWindowsConfig-${VERSION}-win-arm64-setup.exe`,
        ],
      },
      { name: "release:sign:gpg", args: ["--", "--dir", folder] },
      { name: "release:authenticode", args: ["--", "-Files", `${folder}/BetterWindowsConfig-${VERSION}-win-x64-setup.exe`] },
      { name: "release:authenticode", args: ["--", "-Files", `${folder}/BetterWindowsConfig-${VERSION}-win-arm64-setup.exe`] },
      { name: "release:draft", args: ["--", "--apply"] },
      { name: "release:verify:draft", args: [] },
      { name: "release:finalize", args: [] },
    ],
    "continue",
  );
});

check("continue: finalize is last, and the quality gate never runs GUI E2E", () => {
  const steps = releaseSteps("continue", { version: VERSION, env: cleanEnv() });
  assertEqual(steps.at(-1).name, "release:finalize", "last");
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
    "release:finalize",
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

if (failures.length > 0) {
  for (const failure of failures) console.error(`FAIL ${failure}`);
  console.error(`${SCRIPT}: ${passed} passed, ${failures.length} failed`);
  process.exitCode = EXIT.REFUSED;
} else {
  console.log(`${SCRIPT}: ${passed}/${passed} passed`);
}
