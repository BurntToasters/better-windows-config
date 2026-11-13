/**
 * Signing and release policy for the Windows installer build.
 *
 * Signing policy: SKIP_WIN_CODESIGN=1 (exactly) is the only value that skips Authenticode signing.
 * Unset, 0, or any other value enforces it, so a typo cannot ship an unsigned release. The variable
 * names match the ones Zinnia uses, so one .env works for both repositories. build-installer.js uses
 * only this part, so a local dev build may still skip signing.
 *
 * Stable policy: for a stable version (X.Y.Z, no suffix) the release flow refuses the beta recovery
 * overrides and any GitHub target other than BurntToasters/better-windows-config. Only the release
 * entry points call it. Beta versions are not restricted here.
 *
 * Run as `node --env-file-if-exists=.env scripts/release-policy.js` to check the stable policy for
 * package.json's version. The exit code is 0 when it passes, 1 when it refuses, and 2 for usage.
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { isDirectExecutionOf } from "./direct-execution.mjs";

const SCRIPT = "scripts/release-policy.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));

export const SKIP_ENV = "SKIP_WIN_CODESIGN";

// SignTool and the Artifact Signing dlib read these. AZURE_SUBSCRIPTION_ID may be in the .env, but
// signing does not use it, so it is not required here.
export const SIGNING_REQUIRED_VARIABLES = Object.freeze([
  "AZURE_CLIENT_ID",
  "AZURE_TENANT_ID",
  "AZURE_CLIENT_SECRET",
  "AZURE_ARTIFACT_SIGNING_ENDPOINT",
  "AZURE_ARTIFACT_SIGNING_ACCOUNT",
  "AZURE_ARTIFACT_SIGNING_PROFILE",
  "AZURE_ARTIFACT_SIGNING_PUBLISHER",
  "AZURE_ARTIFACT_SIGNING_PUBLISHER_DN",
]);

// BWC's own binaries in a publish folder root: the three apps (exe and dll) and every Bwc.*.dll.
// Runtime, Microsoft, and third-party files never match.
const BWC_BINARY = /^(?:Bwc\.[^\\/]+|BwcRegedit|BwcUpdate|BetterWindowsConfig)\.(?:dll|exe)$/i;

// Beta recovery overrides. A stable release must not use any of them.
export const STABLE_FORBIDDEN_ENV = Object.freeze([
  "SKIP_WIN_CODESIGN",
  "FORCE_UPLOAD",
  "SKIP_RELEASE_MIRROR",
  "ALLOW_ASSET_REPLACE",
  "SKIP_E2E",
]);

export const STABLE_REPOSITORY = Object.freeze({ owner: "BurntToasters", name: "better-windows-config" });

/**
 * Returns { skip, warning }. skip is true only for the exact value "1". A warning names any other
 * value that is neither empty nor "0", so a misspelled skip is visible.
 */
export function signingPolicy(env = process.env) {
  const raw = env[SKIP_ENV];
  const skip = raw === "1";
  const warning =
    raw === undefined || raw === "" || raw === "0" || skip
      ? null
      : `${SKIP_ENV}=${JSON.stringify(raw)} is not 1, so signing is enforced. Use exactly 1 to skip.`;
  return { skip, warning };
}

/** Names of the required variables that are unset or blank, in the order of SIGNING_REQUIRED_VARIABLES. */
export function missingSigningVariables(env = process.env) {
  return SIGNING_REQUIRED_VARIABLES.filter((name) => !String(env[name] ?? "").trim());
}

/** True when a file name in a publish folder root is one of BWC's own binaries. */
export function isBwcSigningTarget(fileName) {
  return BWC_BINARY.test(fileName);
}

/** The subset of fileNames that BWC signs, sorted. Only names are checked, not folders. */
export function bwcSigningTargets(fileNames) {
  return fileNames.filter(isBwcSigningTarget).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/** True for the values an operator writes to switch an override on: 1, true, yes, on (any case). */
export function isExplicitTruthy(value) {
  return /^(1|true|yes|on)$/i.test(String(value ?? "").trim());
}

/** True only for X.Y.Z with no pre-release suffix. */
export function isStableReleaseVersion(version) {
  return /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$/.test(String(version ?? ""));
}

export function readPackageVersion(root = REPO_ROOT) {
  const parsed = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  return typeof parsed.version === "string" ? parsed.version.trim() : "";
}

/**
 * The reasons a stable release must not run with this environment, one message per problem.
 * Empty for a beta or other non-stable version. Blank variables count as unset.
 */
export function stableReleaseOverrideProblems(env = process.env, version = readPackageVersion()) {
  if (!isStableReleaseVersion(version)) return [];
  const problems = [];

  const blocked = STABLE_FORBIDDEN_ENV.filter((name) => isExplicitTruthy(env[name]));
  if (blocked.length > 0) {
    problems.push(`Stable release ${version} refuses ${blocked.join(", ")}. Those overrides are beta recovery paths only.`);
  }

  const slug = `${STABLE_REPOSITORY.owner}/${STABLE_REPOSITORY.name}`;
  const mismatches = [];
  const repo = String(env.GH_REPO ?? "").trim();
  if (repo && repo !== slug) mismatches.push(`GH_REPO="${repo}"`);
  const owner = String(env.GH_REPO_OWNER ?? "").trim();
  if (owner && owner !== STABLE_REPOSITORY.owner) mismatches.push(`GH_REPO_OWNER="${owner}"`);
  const name = String(env.GH_REPO_NAME ?? "").trim();
  if (name && name !== STABLE_REPOSITORY.name) mismatches.push(`GH_REPO_NAME="${name}"`);

  const downloadBase = String(env.RELEASE_DOWNLOAD_BASE_URL ?? "").trim();
  if (downloadBase && downloadBase.replace(/\/+$/, "") !== `https://github.com/${slug}/releases/download/v${version}`) {
    mismatches.push(`RELEASE_DOWNLOAD_BASE_URL="${downloadBase}"`);
  }
  if (mismatches.length > 0) {
    problems.push(`Stable release ${version} refuses non-canonical GitHub targets: ${mismatches.join(", ")}.`);
  }
  return problems;
}

/** Throws with every problem joined into one message. Passes silently for a clean stable environment. */
export function assertStableReleaseOverridesAllowed(env = process.env, version = readPackageVersion()) {
  const problems = stableReleaseOverrideProblems(env, version);
  if (problems.length > 0) throw new Error(problems.join(" "));
}

function main(argv) {
  if (argv.length > 0) {
    console.error(`${SCRIPT}: unexpected argument: ${argv[0]}`);
    console.error(`Usage: node ${SCRIPT}   (no arguments; the version comes from package.json)`);
    return 2;
  }
  const version = readPackageVersion();
  try {
    assertStableReleaseOverridesAllowed(process.env, version);
  } catch (error) {
    console.error(`${SCRIPT}: REFUSED [stable-override]: ${error instanceof Error ? error.message : String(error)}`);
    return 1;
  }
  console.log(`${SCRIPT}: ok (${version}, ${isStableReleaseVersion(version) ? "stable" : "non-stable"})`);
  return 0;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
