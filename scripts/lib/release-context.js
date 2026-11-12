import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { EXIT } from "./cli.js";
import { resolveRepo, tagForVersion, isPrereleaseVersion } from "./github-release.js";
import { loadReleaseBundle } from "./release-bundle.js";
import {
  currentCommit,
  git,
  readChangelogSection,
  readPackageVersion,
  runPreflight,
  sessionPath,
  verifySessionFile,
} from "./release-policy.js";
import { readTrustedKeysFile } from "./trusted-keys.js";
import { channelForVersion, tryParseSemVer } from "./semver.js";

const DEFAULT_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");

/** One manifest per channel: artifacts/release/<version>/<channel>/. */
export function artifactDirectory(root, version, channel) {
  return path.join(root, "artifacts", "release", version, channel);
}

function resolveUnder(root, value) {
  return value ? path.resolve(root, value) : null;
}

/**
 * Resolves --commit to a full SHA. Without --commit the commit is HEAD. A resolved commit that is
 * not HEAD is returned as-is and refused by the caller, so a release never targets another tree.
 */
function resolveCommit(root, value) {
  if (!value) return currentCommit(root);
  if (/^[0-9a-f]{40}$/i.test(value)) return value.toLowerCase();
  const resolved = git(root, ["rev-parse", `${value}^{commit}`], { allowFailure: true });
  if (!resolved || !/^[0-9a-f]{40}$/i.test(resolved)) {
    return null;
  }
  return resolved.toLowerCase();
}

/**
 * Shared D7 preparation: version and channel, preflight, session, bundle validation,
 * and the test-key and dry-run-artifact policy. No network. Callers add gh commands
 * only when --apply is set.
 */
export async function prepareReleaseContext(options) {
  const root = path.resolve(options.root ?? DEFAULT_ROOT);
  const errors = [];
  const checks = [];
  const warnings = [];

  let version = options.version ?? readPackageVersion(root);
  const sessionFile = options.session ? resolveUnder(root, options.session) : sessionPath(root);
  const trustedKeys = (options.trustedKeys ?? []).map((value) => {
    const candidate = resolveUnder(root, value);
    return candidate && fs.existsSync(candidate) ? candidate : value;
  });
  if (!tryParseSemVer(version)) {
    return {
      ok: false,
      errors: [{ reason: "version-invalid", detail: `release version is not SemVer: ${version}`, exitCode: EXIT.USAGE }],
      checks,
      warnings,
      version,
      channel: null,
      tag: null,
      commit: null,
      dir: resolveUnder(root, options.dir),
      session: null,
      sessionFile,
      bundle: null,
      repo: null,
      trustedKeys,
    };
  }

  const channel = options.channel ?? channelForVersion(version);
  if (channel !== "stable" && channel !== "beta") {
    errors.push({ reason: "channel-invalid", detail: `no supported channel for ${version}`, exitCode: EXIT.REFUSED });
  } else if (options.channel && options.channel !== channelForVersion(version)) {
    errors.push({
      reason: "channel-mismatch",
      detail: `version ${version} is ${channelForVersion(version)}, not ${options.channel}`,
      exitCode: EXIT.REFUSED,
    });
  }

  const head = currentCommit(root);
  const commit = resolveCommit(root, options.commit);
  if (!commit) {
    errors.push({ reason: "session-commit-mismatch", detail: `--commit does not resolve to a commit`, exitCode: EXIT.REFUSED });
  } else if (commit !== head) {
    errors.push({
      reason: "session-commit-mismatch",
      detail: `--commit ${commit.slice(0, 12)} is not HEAD ${head.slice(0, 12)}`,
      exitCode: EXIT.REFUSED,
    });
  }

  let repo = null;
  try {
    repo = resolveRepo(root, options.repo);
  } catch (error) {
    errors.push({ reason: "repo-invalid", detail: error instanceof Error ? error.message : String(error), exitCode: EXIT.USAGE });
  }

  if (options.skipPreflight) {
    if (options.apply) {
      errors.push({ reason: "preflight-skipped", detail: "--skip-preflight is dry-run only", exitCode: EXIT.USAGE });
    } else {
      warnings.push({ reason: "preflight-skipped", detail: "preflight was skipped by request" });
    }
  } else {
    const preflight = await runPreflight(root, {
      version,
      channel,
      allowDetached: options.allowDetached,
      trustedKey: trustedKeys[0],
      requireTrustedKey: true,
    });
    errors.push(...preflight.errors);
    checks.push(...preflight.checks);
    if (preflight.trusted.key?.testKey) {
      warnings.push({ reason: "test-key", detail: "the preflight trusted key is a dry-run test key" });
    }
  }

  let session = null;
  if (options.skipSession) {
    if (options.apply) {
      errors.push({ reason: "missing-session", detail: "--skip-session is dry-run only", exitCode: EXIT.USAGE });
    } else {
      warnings.push({ reason: "session-skipped", detail: "release session was skipped by request" });
    }
  } else {
    const verified = verifySessionFile(root, sessionFile, {
      version,
      channel,
      maxAgeHours: options.maxAgeHours ?? 24,
    });
    if (!verified.ok && !options.requireSession && !options.apply) {
      warnings.push(...verified.errors);
    } else {
      errors.push(...verified.errors);
      checks.push(...verified.checks);
    }
    session = verified.session;
  }

  let bundle = null;
  if (options.loadBundle !== false) {
    try {
      const embedded = readTrustedKeysFile(root);
      bundle = await loadReleaseBundle({
        dir: resolveUnder(root, options.dir) ?? artifactDirectory(root, version, channel),
        version,
        channel,
        trustedKeys,
        embeddedKeys: embedded.ok ? embedded.embedded : undefined,
        requireArches: options.requireArches ?? ["win-x64", "win-arm64"],
        maxAgeHours: options.maxAgeHours,
      });
      if (!bundle.ok) {
        errors.push(...bundle.errors);
      } else {
        checks.push(...bundle.checks);
      }

      if (bundle.testKey) {
        if (options.apply) {
          errors.push({ reason: "test-key", detail: "a dry-run test key cannot sign a real release", exitCode: EXIT.REFUSED });
        } else if (!options.allowTestKey) {
          errors.push({ reason: "test-key", detail: "pass --allow-test-key to use a dry-run test key in a dry run", exitCode: EXIT.REFUSED });
        } else {
          warnings.push({ reason: "test-key", detail: "dry-run test key accepted because --allow-test-key was given" });
        }
      }
      if (bundle.dryRun) {
        if (options.apply) {
          errors.push({ reason: "dry-run-artifact", detail: "a .dry-run.json installer cannot be published", exitCode: EXIT.REFUSED });
        } else {
          warnings.push({ reason: "dry-run-artifact", detail: "the plan uses a .dry-run.json installer stand-in" });
        }
      }
      if (bundle.placeholder) {
        errors.push({ reason: "placeholder-key", detail: "a placeholder key cannot sign a release", exitCode: EXIT.REFUSED });
      }
    } catch (error) {
      errors.push({
        reason: "missing-artifact",
        detail: error instanceof Error ? error.message : String(error),
        exitCode: EXIT.MISSING,
      });
    }
  }

  const changelog = readChangelogSection(root, version);
  if (!changelog.ok) {
    if (options.apply) {
      errors.push({ reason: changelog.reason, detail: changelog.detail, exitCode: EXIT.REFUSED });
    } else {
      warnings.push({ reason: changelog.reason, detail: changelog.detail });
    }
  }

  return {
    ok: errors.length === 0,
    errors,
    checks,
    warnings,
    root,
    version,
    channel,
    tag: tagForVersion(version),
    prerelease: isPrereleaseVersion(version),
    commit,
    dir: resolveUnder(root, options.dir) ?? artifactDirectory(root, version, channel),
    session,
    sessionFile,
    bundle,
    repo,
    trustedKeys,
    notes: changelog.ok ? changelog.body : `Release ${version}.`,
    notesMissing: !changelog.ok,
  };
}
