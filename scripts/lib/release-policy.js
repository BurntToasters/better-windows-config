import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { EXIT, describeError, isPathInside } from "./cli.js";
import { isPlaceholderKeyText, loadKeyMaterial, parsePublicKeyText } from "./minisign.js";
import { matchEmbeddedKey, readTrustedKeysFile } from "./trusted-keys.js";
import { selectRelease, tagForVersion } from "./github-release.js";
import { channelForVersion, compareSemVer, tryParseSemVer } from "./semver.js";
import { missingSigningVariables, signingPolicy } from "../release-policy.js";
import { signReleaseAssets } from "../gpg-sign.js";
import {
  CHANGELOG_PLACEHOLDER,
  findChangelogSection,
  parsePackageVersion,
  readDirectoryBuildPropsVersion,
} from "../sync-version-helpers.js";
import { manifestFileName, validateManifestBuffer } from "./update-manifest.js";
import { validateUrl } from "./url-policy.js";

export const RELEASE_BRANCHES = Object.freeze({ stable: "main", beta: "experimental" });
export const SESSION_RELATIVE_PATH = "artifacts/release/session.json";

export function expectedBranch(channel) {
  return RELEASE_BRANCHES[channel] ?? null;
}

export function git(root, args, { allowFailure = false } = {}) {
  try {
    return execFileSync("git", args, {
      cwd: root,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    }).trimEnd();
  } catch (error) {
    if (allowFailure) return null;
    const detail = String(error?.stderr || error?.message || error).trim();
    throw new Error(`git ${args.join(" ")} failed: ${detail}`);
  }
}

export function currentCommit(root) {
  const commit = git(root, ["rev-parse", "HEAD"]);
  if (!/^[0-9a-f]{40}$/i.test(commit)) {
    throw new Error("could not resolve an exact release commit from git HEAD");
  }
  return commit.toLowerCase();
}

export function currentBranch(root) {
  return git(root, ["rev-parse", "--abbrev-ref", "HEAD"], { allowFailure: true });
}

export function branchContains(root, commit, branch) {
  // -a lists remote-tracking branches too, so a detached HEAD pushed to origin/<branch> matches.
  const names = git(root, ["branch", "-a", "--contains", commit, "--format=%(refname:short)"], {
    allowFailure: true,
  });
  if (!names) return false;
  return names
    .split(/\r?\n/)
    .map((name) => name.trim())
    .some((name) => name === branch || name === `remotes/origin/${branch}` || name === `origin/${branch}`);
}

export function porcelainPaths(root) {
  const output = execFileSync("git", ["status", "--porcelain=v1", "-z"], {
    cwd: root,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
  return output
    .split("\0")
    .filter((entry) => entry.length > 0)
    .map((entry) => (entry.length > 3 ? entry.slice(3) : entry))
    .filter((entry) => entry.length > 0);
}

export function readPackageVersion(root) {
  const file = path.join(root, "package.json");
  const parsed = JSON.parse(fs.readFileSync(file, "utf8"));
  return typeof parsed.version === "string" ? parsed.version.trim() : "";
}

export function readBuildVersion(root) {
  const file = path.join(root, "Directory.Build.props");
  return readDirectoryBuildPropsVersion(fs.readFileSync(file, "utf8"));
}

export function readChangelogSection(root, version) {
  const file = path.join(root, "CHANGELOG.md");
  if (!fs.existsSync(file)) {
    return { ok: false, reason: "changelog-missing", detail: "CHANGELOG.md is missing" };
  }
  const text = fs.readFileSync(file, "utf8");
  const section = findChangelogSection(text, version);
  if (section.count === 0) {
    return {
      ok: false,
      reason: "changelog-missing",
      detail: `CHANGELOG.md has no heading "${section.heading}"`,
    };
  }
  if (section.count > 1) {
    return {
      ok: false,
      reason: "changelog-missing",
      detail: `CHANGELOG.md has ${section.count} "${section.heading}" headings; keep exactly one`,
    };
  }
  if (section.body.length === 0 || section.body.includes(CHANGELOG_PLACEHOLDER)) {
    return {
      ok: false,
      reason: "changelog-empty",
      detail: `CHANGELOG.md section for ${version} is empty or still has the sync-version placeholder`,
    };
  }
  // CHANGELOG.md is the BCLS release body (downloads, callouts, carried-forward
  // sections, Release Info), so the GitHub notes are the whole file.
  return {
    ok: true,
    file,
    heading: section.heading,
    body: text.trim(),
  };
}

export function resolveReleaseVersion(root, requestedVersion) {
  const packageVersion = readPackageVersion(root);
  const version = requestedVersion ?? packageVersion;
  if (!tryParseSemVer(version)) {
    return { ok: false, reason: "version-invalid", detail: `version is not SemVer: ${version}` };
  }
  let parsed;
  try {
    parsed = parsePackageVersion(version);
  } catch (error) {
    return {
      ok: false,
      reason: "version-invalid",
      detail: error instanceof Error ? error.message : String(error),
    };
  }
  return {
    ok: true,
    version,
    channel: channelForVersion(version),
    prerelease: parsed.prerelease,
    packageVersion,
  };
}

export function checkVersionConsistency(root, release) {
  const errors = [];
  const checks = [];

  if (!release.ok) {
    errors.push({ reason: release.reason, detail: release.detail, exitCode: EXIT.REFUSED });
    return { errors, checks };
  }

  if (release.packageVersion !== release.version) {
    errors.push({
      reason: "version-mismatch",
      detail: `package.json version is ${release.packageVersion}, release version is ${release.version}`,
      exitCode: EXIT.REFUSED,
    });
  } else {
    checks.push({ name: "package-version", pass: true, detail: release.version });
  }

  try {
    const build = readBuildVersion(root);
    const expected = parsePackageVersion(release.version);
    if (build.version !== release.version || build.fileVersion !== expected.fileVersion) {
      errors.push({
        reason: "version-mismatch",
        detail:
          `Directory.Build.props has Version ${build.version ?? "(missing)"} and FileVersion ` +
          `${build.fileVersion ?? "(missing)"}; expected ${release.version} and ${expected.fileVersion}`,
        exitCode: EXIT.REFUSED,
      });
    } else {
      checks.push({ name: "build-version", pass: true, detail: expected.fileVersion });
    }
  } catch (error) {
    errors.push({
      reason: "version-mismatch",
      detail: `could not read Directory.Build.props: ${error instanceof Error ? error.message : String(error)}`,
      exitCode: EXIT.REFUSED,
    });
  }

  const changelog = readChangelogSection(root, release.version);
  if (!changelog.ok) {
    errors.push({ reason: changelog.reason, detail: changelog.detail, exitCode: EXIT.REFUSED });
  } else {
    checks.push({ name: "changelog", pass: true, detail: changelog.heading });
  }

  return { errors, checks, changelog };
}

export function checkBranch(root, release, { allowDetached = false } = {}) {
  const errors = [];
  const checks = [];
  const expected = expectedBranch(release.channel);
  if (!expected) {
    return {
      errors: [{ reason: "channel-invalid", detail: `no branch for channel ${release.channel}`, exitCode: EXIT.REFUSED }],
      checks,
      branch: null,
      detached: false,
    };
  }

  const branch = currentBranch(root);
  const commit = currentCommit(root);
  if (branch === "HEAD") {
    if (!allowDetached) {
      errors.push({
        reason: "detached-head",
        detail: `HEAD is detached; release from ${expected} or pass --allow-detached for a verified detached checkout`,
        exitCode: EXIT.REFUSED,
      });
    } else if (!branchContains(root, commit, expected)) {
      errors.push({
        reason: "branch-mismatch",
        detail: `detached HEAD ${commit.slice(0, 12)} is not contained in ${expected}`,
        exitCode: EXIT.REFUSED,
      });
    } else {
      checks.push({ name: "branch", pass: true, detail: `detached HEAD contained in ${expected}` });
    }
    return { errors, checks, branch: null, detached: true, commit, expected };
  }

  if (branch !== expected) {
    errors.push({
      reason: "branch-mismatch",
      detail: `${release.version} must be released from ${expected}, not ${branch || "detached HEAD"}`,
      exitCode: EXIT.REFUSED,
    });
  } else {
    checks.push({ name: "branch", pass: true, detail: branch });
  }
  return { errors, checks, branch, detached: false, commit, expected };
}

export function checkCleanTree(root) {
  const dirtyPaths = porcelainPaths(root);
  if (dirtyPaths.length > 0) {
    return {
      errors: [
        {
          reason: "dirty-tree",
          detail: `worktree has ${dirtyPaths.length} changed path(s): ${dirtyPaths.join(", ")}`,
          exitCode: EXIT.REFUSED,
        },
      ],
      checks: [],
      dirtyPaths,
    };
  }
  return { errors: [], checks: [{ name: "clean-tree", pass: true }], dirtyPaths };
}

export function readTrustedKeysSource(root) {
  const source = readTrustedKeysFile(root);
  if (!source.ok) {
    return { ok: false, reason: source.reason, detail: source.detail ?? source.file, file: source.file };
  }
  return { ok: true, file: source.file, isPlaceholder: source.isPlaceholder, embedded: source.embedded };
}

export function checkTrustedKey(root, trustedKeyValue, { requireKey = false } = {}) {
  const errors = [];
  const checks = [];
  const source = readTrustedKeysSource(root);

  if (!source.ok) {
    errors.push({ reason: source.reason, detail: source.detail, exitCode: EXIT.REFUSED });
  } else if (source.isPlaceholder || source.embedded.some((text) => isPlaceholderKeyText(text))) {
    errors.push({
      reason: "placeholder-key",
      detail: `src/Bwc.Update/Signing/TrustedKeys.cs still carries the placeholder release key; ` +
        `embed the real public key first (${USER_STEPS_POINTER})`,
      exitCode: EXIT.REFUSED,
    });
  } else {
    checks.push({ name: "trusted-key-source", pass: true, detail: `${source.embedded.length} embedded key(s)` });
  }

  let key = null;
  if (trustedKeyValue) {
    const loaded = loadKeyMaterial(trustedKeyValue, { kind: "public" });
    if (!loaded.ok) {
      errors.push({ reason: loaded.reason, detail: loaded.detail, exitCode: EXIT.MISSING });
    } else if (isPlaceholderKeyText(loaded.text)) {
      errors.push({ reason: "placeholder-key", detail: "the trusted public key is a placeholder", exitCode: EXIT.REFUSED });
    } else if (source.ok) {
      // A key is trusted only when its id and bytes equal an embedded key. Comments are ignored.
      const matched = matchEmbeddedKey(loaded.text, source.embedded);
      if (!matched.ok) {
        errors.push({ reason: matched.reason, detail: matched.detail, exitCode: EXIT.REFUSED });
      } else {
        key = {
          text: loaded.text,
          path: loaded.path,
          keyId: matched.keyIdHex,
          fingerprint: matched.fingerprint,
          testKey: false,
        };
        checks.push({ name: "trusted-key", pass: true, detail: matched.keyIdHex });
      }
    }
  } else if (requireKey && source.ok && source.embedded.length === 0) {
    // Without --trusted-key the embedded keys are the trusted set. Only an empty list refuses here.
    errors.push({
      reason: "missing-trusted-key",
      detail: "no --trusted-key was given and TrustedKeys.cs embeds no release key",
      exitCode: EXIT.REFUSED,
    });
  }

  return { errors, checks, key, source };
}

/** The decoded file name at the end of a URL path, "" when there is none, or null when an escape is malformed. */
function decodedAssetName(url) {
  const last = new URL(url).pathname.split("/").filter(Boolean).at(-1) ?? "";
  try {
    return decodeURIComponent(last);
  } catch {
    return null;
  }
}

export function assetFilesFromManifest(root, manifest) {
  const files = {};
  for (const [architecture, entry] of Object.entries(manifest?.installers ?? {})) {
    if (!entry || typeof entry.url !== "string") continue;
    const validated = validateUrl(entry.url, { requireHttps: true });
    if (!validated.ok) continue;
    const name = decodedAssetName(entry.url);
    if (!name) continue;
    files[architecture] = path.join(root, name);
  }
  return files;
}

/** The architectures whose installer URL has a malformed percent escape. Such a manifest is refused. */
export function malformedInstallerUrls(manifest) {
  const named = [];
  for (const [architecture, entry] of Object.entries(manifest?.installers ?? {})) {
    if (!entry || typeof entry.url !== "string") continue;
    const validated = validateUrl(entry.url, { requireHttps: true });
    if (!validated.ok) continue;
    if (decodedAssetName(entry.url) === null) named.push(architecture);
  }
  return named;
}

export async function checkArtifactDirectory(root, artifactDir, release, trustedKeyTexts) {
  const errors = [];
  const checks = [];
  const directory = path.resolve(artifactDir);
  if (!fs.existsSync(directory) || !fs.statSync(directory).isDirectory()) {
    return {
      errors: [
        {
          reason: "missing-artifact",
          detail: `artifact directory is missing: ${directory}`,
          exitCode: EXIT.MISSING,
        },
      ],
      checks,
    };
  }

  const manifestPath = path.join(directory, manifestFileName(release.channel));
  if (!fs.existsSync(manifestPath) || !fs.statSync(manifestPath).isFile()) {
    return {
      errors: [
        {
          reason: "missing-artifact",
          detail: `manifest is missing: ${manifestPath}`,
          exitCode: EXIT.MISSING,
        },
      ],
      checks,
    };
  }

  const signaturePath = `${manifestPath}.minisig`;
  if (!fs.existsSync(signaturePath) || !fs.statSync(signaturePath).isFile()) {
    return {
      errors: [
        {
          reason: "signature-missing",
          detail: `manifest signature is missing: ${signaturePath}`,
          exitCode: EXIT.MISSING,
        },
      ],
      checks,
    };
  }

  const bytes = fs.readFileSync(manifestPath);
  let parsedJson = null;
  try {
    parsedJson = JSON.parse(bytes.toString("utf8"));
  } catch {
    // The D4 validator below reports the exact JSON reason.
  }
  const malformed = malformedInstallerUrls(parsedJson ?? {});
  if (malformed.length > 0) {
    return {
      errors: [
        {
          reason: "asset-url-malformed",
          detail: `manifest installer URL has a malformed percent escape for: ${malformed.join(", ")}`,
          exitCode: EXIT.REFUSED,
        },
      ],
      checks,
    };
  }
  const installers = assetFilesFromManifest(directory, parsedJson ?? {});
  const validation = await validateManifestBuffer(bytes, {
    signature: fs.readFileSync(signaturePath, "utf8"),
    trustedKeys: trustedKeyTexts,
    channel: release.channel,
    version: release.version,
    requireArches: ["win-x64", "win-arm64"],
    installers,
  });
  errors.push(...validation.errors);
  checks.push(...validation.checks, { name: "artifact-directory", pass: validation.ok, detail: directory });
  return { errors, checks, validation };
}

export async function runPreflight(root, options = {}) {
  const release = resolveReleaseVersion(root, options.version);
  const errors = [];
  const checks = [];

  const clean = checkCleanTree(root);
  errors.push(...clean.errors);
  checks.push(...clean.checks);

  const versions = checkVersionConsistency(root, release);
  errors.push(...versions.errors);
  checks.push(...versions.checks);

  if (release.ok && options.channel && options.channel !== release.channel) {
    errors.push({
      reason: "channel-mismatch",
      detail: `version ${release.version} is ${release.channel}, not ${options.channel}`,
      exitCode: EXIT.REFUSED,
    });
  }

  const branch = checkBranch(root, release, { allowDetached: Boolean(options.allowDetached) });
  errors.push(...branch.errors);
  checks.push(...branch.checks);

  const trusted = checkTrustedKey(root, options.trustedKey, {
    requireKey: Boolean(options.requireTrustedKey),
  });
  errors.push(...trusted.errors);
  checks.push(...trusted.checks);

  // The signing readiness checks run only when the caller injects dependencies (the signing VM).
  if (options.readiness && release.ok) {
    const ready = checkReleaseReadiness(
      root,
      { version: release.version, channel: release.channel, commit: currentCommit(root), env: options.readiness.env },
      options.readiness.deps,
    );
    errors.push(...ready.errors);
    checks.push(...ready.checks);
  }

  let artifact = null;
  if (options.artifactDir && release.ok) {
    const keyTexts = trusted.key ? [trusted.key.text] : [];
    if (keyTexts.length === 0) {
      errors.push({
        reason: "missing-trusted-key",
        detail: "--artifact-dir needs --trusted-key to verify the manifest",
        exitCode: EXIT.REFUSED,
      });
    } else {
      artifact = await checkArtifactDirectory(root, options.artifactDir, release, keyTexts);
      errors.push(...artifact.errors);
      checks.push(...artifact.checks);
    }
  }

  return {
    ok: errors.length === 0,
    errors,
    checks,
    release,
    branch,
    clean,
    trusted,
    artifact,
    changelog: versions.changelog ?? null,
  };
}

export function sessionPath(root) {
  return path.join(root, SESSION_RELATIVE_PATH);
}

export function verifySessionFile(root, filePath, { version, channel, maxAgeHours = 24 } = {}) {
  const errors = [];
  const checks = [];
  if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
    return {
      ok: false,
      errors: [
        {
          reason: "missing-session",
          detail: `no release session at ${filePath}; run release-session.js create first`,
          exitCode: EXIT.MISSING,
        },
      ],
      checks,
      session: null,
    };
  }

  let session;
  try {
    session = JSON.parse(fs.readFileSync(filePath, "utf8"));
  } catch (error) {
    return {
      ok: false,
      errors: [
        {
          reason: "missing-session",
          detail: `release session is not valid JSON: ${error instanceof Error ? error.message : String(error)}`,
          exitCode: EXIT.MISSING,
        },
      ],
      checks,
      session: null,
    };
  }

  if (session.schema !== 1 || typeof session.version !== "string" || typeof session.channel !== "string") {
    errors.push({ reason: "session-mismatch", detail: "session schema, version, or channel is invalid", exitCode: EXIT.REFUSED });
  }
  if (version && session.version !== version) {
    errors.push({
      reason: "session-mismatch",
      detail: `session is for ${session.version}, not ${version}`,
      exitCode: EXIT.REFUSED,
    });
  }
  if (channel && session.channel !== channel) {
    errors.push({
      reason: "session-mismatch",
      detail: `session is for ${session.channel}, not ${channel}`,
      exitCode: EXIT.REFUSED,
    });
  }

  const head = currentCommit(root);
  if (session.commit !== head) {
    errors.push({
      reason: "session-commit-mismatch",
      detail: `session commit ${String(session.commit).slice(0, 12)} is not HEAD ${head.slice(0, 12)}`,
      exitCode: EXIT.REFUSED,
    });
  } else {
    checks.push({ name: "session-commit", pass: true, detail: head.slice(0, 12) });
  }

  if (!session.qualityGate || typeof session.qualityGate !== "object") {
    errors.push({
      reason: "missing-quality-gate",
      detail: "session has no quality gate proof; run the gate, then release-session.js create",
      exitCode: EXIT.REFUSED,
    });
  }

  const started = Date.parse(session.startedUtc);
  const limit = maxAgeHours ?? session.maxAgeHours ?? 24;
  if (!Number.isFinite(started)) {
    errors.push({ reason: "session-mismatch", detail: "session has no valid startedUtc", exitCode: EXIT.REFUSED });
  } else {
    const ageHours = (Date.now() - started) / (60 * 60 * 1000);
    if (ageHours < 0) {
      errors.push({ reason: "session-mismatch", detail: "session start time is in the future", exitCode: EXIT.REFUSED });
    } else if (ageHours > limit) {
      errors.push({
        reason: "session-expired",
        detail: `session is ${ageHours.toFixed(1)} hours old, over the ${limit} hour limit`,
        exitCode: EXIT.REFUSED,
      });
    } else {
      checks.push({ name: "session-age", pass: true, detail: `${ageHours.toFixed(1)}h` });
    }
  }

  return { ok: errors.length === 0, errors, checks, session };
}

export function isSessionPath(root, filePath) {
  return path.resolve(filePath) === path.resolve(sessionPath(root)) || isPathInside(path.join(root, "artifacts"), filePath);
}

export const QUALITY_GATE_RESULT_RELATIVE_PATH = "artifacts/gate/result.json";

export const USER_STEPS_POINTER = 'docs/RELEASE.md, section "User-owned steps"';

/**
 * The trusted set when no --trusted-key is given: the non-placeholder keys embedded in
 * TrustedKeys.cs. Returns { ok, keys } or { ok: false, reason, detail }. Nothing is read from the
 * environment, so the release VM's .env needs no BWC-only variable.
 */
export function embeddedReleaseKeys(root) {
  const source = readTrustedKeysSource(root);
  if (!source.ok) {
    return { ok: false, reason: source.reason, detail: String(source.detail ?? source.file) };
  }
  if (source.isPlaceholder || source.embedded.some((text) => isPlaceholderKeyText(text))) {
    return {
      ok: false,
      reason: "placeholder-key",
      detail: `src/Bwc.Update/Signing/TrustedKeys.cs still carries the placeholder release key; ` +
        `embed the real public key first (${USER_STEPS_POINTER})`,
    };
  }
  if (source.embedded.length === 0) {
    return { ok: false, reason: "missing-trusted-key", detail: "TrustedKeys.cs embeds no release key" };
  }
  for (let index = 0; index < source.embedded.length; index += 1) {
    const parsed = parsePublicKeyText(source.embedded[index]);
    if (!parsed.ok) {
      return { ok: false, reason: "malformed-key", detail: `embedded key ${index}: ${parsed.detail}` };
    }
  }
  return { ok: true, keys: source.embedded };
}

/** The parsed artifacts/gate/result.json under root, or null when it is missing or unreadable. */
export function readQualityGateResult(root) {
  const file = path.join(root, QUALITY_GATE_RESULT_RELATIVE_PATH);
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return null;
  }
}

/**
 * Checks a quality-gate result for the release commit. A passing quick gate records e2e as
 * "skipped" (the release VM runs no GUI E2E); a passing full gate records "passed".
 */
export function qualityGateProof(gate, head) {
  if (!gate || typeof gate !== "object") {
    return { ok: false, reason: "gate-missing", detail: `no passing quality gate at ${QUALITY_GATE_RESULT_RELATIVE_PATH}` };
  }
  if (gate.pass !== true) {
    return { ok: false, reason: "gate-failed", detail: "the quality gate did not pass" };
  }
  const gateCommit = gate.git?.commit ?? null;
  if (gateCommit !== head) {
    return {
      ok: false,
      reason: "gate-commit-mismatch",
      detail: `the quality gate ran on ${String(gateCommit).slice(0, 12)}, not HEAD ${String(head).slice(0, 12)}`,
    };
  }
  // test-all.js records git.dirty. A gate that ran on uncommitted files does not describe HEAD.
  if (gate.git?.dirty === true) {
    return {
      ok: false,
      reason: "gate-dirty-tree",
      detail: "the quality gate ran on a dirty tree; rerun it on a clean checkout of HEAD",
    };
  }
  const quick = gate.quick === true;
  return {
    ok: true,
    record: {
      source: QUALITY_GATE_RESULT_RELATIVE_PATH,
      commit: gateCommit,
      pass: true,
      mode: quick ? "quick" : "full",
      e2e: quick ? "skipped" : "passed",
    },
  };
}

// ---- signing readiness (preflight --require-trusted-key) ----------------------------------------
// Every dependency is injected: the signing VM passes the real ones (scripts/release-preflight.js),
// and scripts/release-selftest.js passes fakes. Local checks all run; gh runs only when they pass.

/** Exit code per reason. Other reasons refuse with EXIT.REFUSED. Missing inputs use EXIT.MISSING. */
const READINESS_EXIT = Object.freeze({
  "key-missing": EXIT.MISSING,
  "key-not-found": EXIT.MISSING,
  "missing-trusted-key-source": EXIT.MISSING,
  "makensis-missing": EXIT.MISSING,
  "signing-tools-missing": EXIT.MISSING,
  "gpg-missing": EXIT.EXTERNAL,
  "gpg-failed": EXIT.EXTERNAL,
  "sign-failed": EXIT.EXTERNAL,
  "gh-missing": EXIT.EXTERNAL,
  "gh-auth-missing": EXIT.EXTERNAL,
  "gh-failed": EXIT.EXTERNAL,
  "key-id-missing": EXIT.USAGE,
  "key-id-invalid": EXIT.USAGE,
  "passphrase-missing": EXIT.USAGE,
});

const ENGINE_VERSION = /^v?(\d+)\.(\d+)\.(\d+)/;
const ENGINE_RANGE = /^(\^|>=)?v?(\d+)\.(\d+)\.(\d+)$/;

/**
 * True when `version` satisfies a "^X.Y.Z" or ">=X.Y.Z" range (the forms package.json engines use).
 * Null when either value is not in one of those forms.
 */
export function satisfiesEngineRange(version, range) {
  const have = String(version ?? "").match(ENGINE_VERSION);
  const want = String(range ?? "").trim().match(ENGINE_RANGE);
  if (!have || !want) return null;
  const actual = [Number(have[1]), Number(have[2]), Number(have[3])];
  const wanted = [Number(want[2]), Number(want[3]), Number(want[4])];
  const order = actual[0] - wanted[0] || actual[1] - wanted[1] || actual[2] - wanted[2];
  if (order < 0) return false;
  return want[1] === "^" ? actual[0] === wanted[0] : true;
}

function readEnginesNode(root) {
  const parsed = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  const range = parsed.engines?.node;
  return typeof range === "string" && range.trim() ? range.trim() : null;
}

/** Null when the AFTER_PACK_LOC text is an absolute folder outside the repository; else the reason. */
export function afterPackLocProblem(root, value) {
  if (!path.isAbsolute(value)) return `AFTER_PACK_LOC must be an absolute path: ${value}`;
  const resolved = path.resolve(value);
  if (path.parse(resolved).root === resolved) return "AFTER_PACK_LOC cannot be a drive or filesystem root";
  const fold = (text) => (process.platform === "win32" ? text.toLowerCase() : text);
  const relative = path.relative(fold(path.resolve(root)), fold(resolved));
  if (relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative))) {
    return "AFTER_PACK_LOC must be outside the repository";
  }
  return null;
}

/** Signs a probe file in a temporary folder with the release GPG key. Never touches a release asset. */
function gpgProbe(env, runGpg) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-readiness-"));
  try {
    fs.writeFileSync(path.join(dir, "probe.txt"), "BWC readiness probe\n");
    const result = signReleaseAssets({ dir, keyId: env.GPG_KEY_ID, passphrase: env.GPG_PASSPHRASE, runGpg });
    return result.ok ? { ok: true, fingerprint: result.fingerprint } : { ok: false, errors: result.errors };
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

/**
 * Checks everything a signing run needs before the first build. Returns { errors, checks }. Each
 * error has reason, detail, and exitCode. deps: loadUpdateKey(env), runGpg, probeDirectory(value),
 * findMakensis(env), checkSigningTools(), nodeVersion, refExists(ref), isAncestor(commit, ref),
 * ghAuthStatus(), listReleases(). Secrets are read from env and never copied into the result.
 */
export function checkReleaseReadiness(root, options, deps) {
  const result = readinessChecks(root, options, deps);
  return { ...result, ok: result.errors.length === 0 };
}

function readinessChecks(root, { version, channel, commit, env = process.env }, deps) {
  const errors = [];
  const checks = [];
  const refuse = (reason, detail, exitCode = READINESS_EXIT[reason] ?? EXIT.REFUSED) => {
    errors.push({ reason, detail, exitCode });
  };
  const pass = (name, detail = "") => checks.push({ name, pass: true, detail });

  // Local checks. Every one runs, so a single run lists each local gap.
  const embedded = embeddedReleaseKeys(root);
  if (!embedded.ok) {
    refuse(embedded.reason, embedded.detail, embedded.reason === "missing-trusted-key-source" ? EXIT.MISSING : EXIT.REFUSED);
  }
  const key = deps.loadUpdateKey(env);
  if (!key.ok) {
    refuse(key.reason, key.detail);
  } else if (embedded.ok) {
    const matched = matchEmbeddedKey(key.text, embedded.keys);
    if (matched.ok) pass("update-key", matched.keyIdHex);
    else refuse(matched.reason, matched.detail);
  }

  const probe = gpgProbe(env, deps.runGpg);
  if (probe.ok) pass("gpg-signing", probe.fingerprint);
  else for (const error of probe.errors) refuse(error.reason, error.detail, error.exitCode);

  const stable = channel === "stable";
  if (stable || String(env.OVERRIDE_BETA_MIRROR_SKIP ?? "").trim() === "1") {
    const destination = String(env.AFTER_PACK_LOC ?? "").trim();
    if (!destination) {
      refuse("after-pack-loc-missing", `${version} requires AFTER_PACK_LOC, a folder outside the repository`);
    } else {
      const problem = afterPackLocProblem(root, destination);
      if (problem) {
        refuse("after-pack-loc-invalid", problem);
      } else {
        const usable = deps.probeDirectory(destination);
        if (usable.ok) pass("after-pack-loc", destination);
        else refuse(usable.reason, usable.detail);
      }
    }
  }

  if (!signingPolicy(env).skip) {
    const missing = missingSigningVariables(env);
    if (missing.length > 0) {
      refuse("signing-env-missing", `unset or blank: ${missing.join(", ")}`);
    } else {
      const tools = deps.checkSigningTools();
      if (tools.ok) pass("signing-tools", tools.detail);
      else refuse("signing-tools-missing", tools.detail);
    }
  }

  const nsis = deps.findMakensis(env);
  if (nsis.path) pass("makensis", nsis.path);
  else refuse("makensis-missing", `makensis.exe was not found (${nsis.searched.length} folder(s) searched); set NSIS_HOME`);

  const range = readEnginesNode(root);
  if (range) {
    if (satisfiesEngineRange(deps.nodeVersion, range) === true) pass("node-version", deps.nodeVersion);
    else refuse("node-version", `Node ${deps.nodeVersion} does not satisfy package.json engines.node ${range}`);
  }

  const branch = expectedBranch(channel);
  if (!branch) {
    refuse("channel-invalid", `no branch for channel ${channel}`, EXIT.REFUSED);
  } else if (!deps.refExists(`origin/${branch}`)) {
    refuse("remote-branch-missing", `origin/${branch} is not present; run git fetch origin first`);
  } else if (!deps.isAncestor(commit, `origin/${branch}`)) {
    refuse("head-not-pushed", `HEAD ${String(commit).slice(0, 12)} is not contained in origin/${branch}; push it first`);
  } else {
    pass("pushed", `origin/${branch}`);
  }

  if (errors.length > 0) return { errors, checks, networkSkipped: true };

  // Network checks, only after every local check passed.
  const auth = deps.ghAuthStatus();
  if (!auth.ok) {
    refuse(auth.reason ?? "gh-auth-missing", auth.detail, EXIT.EXTERNAL);
    return { errors, checks, networkSkipped: false };
  }
  let releases;
  try {
    releases = deps.listReleases();
  } catch (error) {
    refuse(error?.reason ?? "gh-failed", describeError(error), EXIT.EXTERNAL);
    return { errors, checks, networkSkipped: false };
  }
  const tag = tagForVersion(version);
  const selected = selectRelease(releases, tag);
  if (!selected.ok) {
    refuse(selected.reason, selected.detail);
  } else if (selected.kind === "published") {
    refuse("release-published", `${tag} is already published (release ${selected.release.id}); a published release is never replaced`);
  } else {
    pass("tag-state", selected.kind === "draft" ? `draft ${selected.release.id} exists and is reused` : `${tag} is not released`);
  }
  let latest = null;
  for (const release of releases ?? []) {
    if (!release || release.draft || typeof release.tag_name !== "string") continue;
    const candidate = release.tag_name.replace(/^v/, "");
    if (!tryParseSemVer(candidate)) continue;
    if (latest === null || compareSemVer(candidate, latest) > 0) latest = candidate;
  }
  if (latest !== null && compareSemVer(version, latest) <= 0) {
    refuse("version-not-newer", `${version} is not newer than the latest published release ${latest}`);
  } else {
    pass("version-order", latest === null ? "no published release yet" : `after ${latest}`);
  }
  return { errors, checks, networkSkipped: false };
}

