import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import { EXIT, isPathInside } from "./cli.js";
import { isPlaceholderKeyText, loadKeyMaterial, parsePublicKeyText } from "./minisign.js";
import { matchEmbeddedKey, readTrustedKeysFile } from "./trusted-keys.js";
import { channelForVersion, tryParseSemVer } from "./semver.js";
import { parsePackageVersion, readDirectoryBuildPropsVersion } from "../sync-version-helpers.js";
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
  const names = git(root, ["branch", "--contains", commit, "--format=%(refname:short)"], {
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

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

export function readChangelogSection(root, version) {
  const file = path.join(root, "CHANGELOG.md");
  if (!fs.existsSync(file)) {
    return { ok: false, reason: "changelog-missing", detail: "CHANGELOG.md is missing" };
  }
  const text = fs.readFileSync(file, "utf8");
  const headingPattern = new RegExp(`^## \\[${escapeRegExp(version)}\\](?:[ \\t]|$)`, "gm");
  const matches = [...text.matchAll(headingPattern)];
  if (matches.length === 0) {
    return {
      ok: false,
      reason: "changelog-missing",
      detail: `CHANGELOG.md has no heading "## [${version}]"`,
    };
  }
  if (matches.length > 1) {
    return {
      ok: false,
      reason: "changelog-missing",
      detail: `CHANGELOG.md has ${matches.length} headings for ${version}; keep exactly one`,
    };
  }

  const match = matches[0];
  const headingEnd = text.indexOf("\n", match.index);
  const bodyStart = headingEnd === -1 ? text.length : headingEnd + 1;
  const rest = text.slice(bodyStart);
  const nextHeading = rest.search(/^## /m);
  const body = (nextHeading === -1 ? rest : rest.slice(0, nextHeading)).trim();
  if (body.length === 0) {
    return {
      ok: false,
      reason: "changelog-empty",
      detail: `CHANGELOG.md section for ${version} is empty`,
    };
  }
  return {
    ok: true,
    file,
    heading: match[0].trim(),
    body,
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

export function assetFilesFromManifest(root, manifest) {
  const files = {};
  for (const [architecture, entry] of Object.entries(manifest?.installers ?? {})) {
    if (!entry || typeof entry.url !== "string") continue;
    const validated = validateUrl(entry.url, { requireHttps: true });
    if (!validated.ok) continue;
    const name = decodeURIComponent(new URL(entry.url).pathname.split("/").filter(Boolean).at(-1) ?? "");
    if (!name) continue;
    files[architecture] = path.join(root, name);
  }
  return files;
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

