#!/usr/bin/env node
/**
 * Mirrors the release folder to <AFTER_PACK_LOC>/<version>/ and checks every copied file by size and
 * SHA-256. Stable versions require AFTER_PACK_LOC. Beta versions (X.Y.Z-beta.N) are skipped unless
 * OVERRIDE_BETA_MIRROR_SKIP=1. Each file is copied to a hidden sibling, verified, and swapped in.
 * Only a same-name file of the same version is ever replaced or removed.
 */
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import { EXIT, describeError, parseArguments, printRefusals, relativeTo, repoRootFrom, sha256FileStream } from "./lib/cli.js";
import { artifactDirectory } from "./lib/release-context.js";
import { readPackageVersion } from "./lib/release-policy.js";
import { channelForVersion, tryParseSemVer } from "./lib/semver.js";
import { isDirectExecutionOf } from "./direct-execution.mjs";

const SCRIPT = "scripts/finalize-release-assets.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const CASE_INSENSITIVE = process.platform === "win32";

function refusal(reason, detail, exitCode = EXIT.REFUSED) {
  return { reason, detail, exitCode };
}

/** A failure raised inside the copy. Carries the reason and exit code the caller reports. */
class MirrorError extends Error {
  constructor(reason, detail, exitCode = EXIT.EXTERNAL) {
    super(detail);
    this.reason = reason;
    this.exitCode = exitCode;
  }
}

function comparable(value) {
  return CASE_INSENSITIVE ? value.toLowerCase() : value;
}

/** True when `child` is `parent` or lies under it. Both are resolved; Windows compares case-insensitively. */
export function sameOrInside(child, parent) {
  const c = comparable(path.resolve(child));
  const p = comparable(path.resolve(parent));
  return c === p || c.startsWith(p.endsWith(path.sep) ? p : `${p}${path.sep}`);
}

/**
 * Decides what the run does before any path is touched. Beta is skipped unless the override is
 * exactly "1"; stable and an enabled beta need AFTER_PACK_LOC.
 */
export function mirrorDecision({ version, env = process.env }) {
  if (!tryParseSemVer(version)) {
    return { action: "refuse", error: refusal("version-invalid", `${version} is not SemVer`, EXIT.USAGE) };
  }
  const channel = channelForVersion(version);
  if (channel === "beta" && String(env.OVERRIDE_BETA_MIRROR_SKIP ?? "").trim() !== "1") {
    return { action: "skip", channel };
  }
  const destination = String(env.AFTER_PACK_LOC ?? "").trim();
  if (!destination) {
    return {
      action: "refuse",
      error: refusal("after-pack-loc-missing", `${version} requires AFTER_PACK_LOC, a folder outside the repository`),
    };
  }
  return { action: "mirror", channel, destination };
}

/** Regular, non-hidden files of the release folder. Symlinks, folders, and other entries are refused. */
export function listReleaseFiles(releaseDir) {
  const files = [];
  const errors = [];
  for (const name of fs.readdirSync(releaseDir).sort()) {
    if (name.startsWith(".")) continue;
    const source = path.join(releaseDir, name);
    const info = fs.lstatSync(source);
    if (info.isSymbolicLink()) {
      errors.push(refusal("symlink-refused", `${name} is a symbolic link or junction`));
    } else if (info.isDirectory()) {
      errors.push(refusal("unexpected-directory", `${name} is a folder; release assets are flat files`));
    } else if (info.isFile()) {
      files.push({ name, source, size: info.size });
    } else {
      errors.push(refusal("unexpected-entry", `${name} is not a regular file`));
    }
  }
  return { files, errors };
}

/** Resolves the longest existing ancestor with realpath and appends the missing segments. */
function resolveThroughMissing(requested) {
  const missing = [];
  let existing = requested;
  while (!fs.existsSync(existing)) {
    missing.unshift(path.basename(existing));
    const parent = path.dirname(existing);
    if (parent === existing) break;
    existing = parent;
  }
  return path.join(fs.realpathSync.native(existing), ...missing);
}

/**
 * Checks the destination against every rule and returns the target folder <destination>/<version>.
 * Nothing is created here.
 */
export function resolveDestination({ releaseDir, destination, version, repoRoot = REPO_ROOT }) {
  const fail = (error) => ({ ok: false, error });
  if (!path.isAbsolute(destination)) {
    return fail(refusal("destination-relative", `AFTER_PACK_LOC must be an absolute path: ${destination}`, EXIT.USAGE));
  }
  const requested = path.resolve(destination);
  if (path.parse(requested).root === requested) {
    return fail(refusal("destination-root", "AFTER_PACK_LOC cannot be a drive or filesystem root"));
  }
  const stat = fs.existsSync(requested) || isLink(requested) ? fs.lstatSync(requested) : null;
  if (stat?.isSymbolicLink()) {
    return fail(refusal("destination-symlink", `AFTER_PACK_LOC must not be a symbolic link or junction: ${requested}`));
  }
  if (stat && !stat.isDirectory()) {
    return fail(refusal("destination-not-directory", `AFTER_PACK_LOC is not a folder: ${requested}`));
  }

  const resolvedRelease = fs.realpathSync.native(releaseDir);
  const resolvedRepo = fs.realpathSync.native(repoRoot);
  const resolvedDestination = resolveThroughMissing(requested);
  if (sameOrInside(resolvedDestination, resolvedRepo)) {
    return fail(refusal("destination-in-repo", `AFTER_PACK_LOC must be outside the repository: ${resolvedRepo}`));
  }
  if (sameOrInside(resolvedDestination, resolvedRelease)) {
    return fail(refusal("destination-in-release", "AFTER_PACK_LOC cannot be the release folder or inside it"));
  }

  const target = path.join(resolvedDestination, version);
  if (isLink(target)) {
    return fail(refusal("destination-symlink", `${version} under AFTER_PACK_LOC is a symbolic link or junction`));
  }
  if (fs.existsSync(target) && !fs.statSync(target).isDirectory()) {
    return fail(refusal("destination-not-directory", `${version} under AFTER_PACK_LOC is a file`));
  }
  if (sameOrInside(target, resolvedRelease)) {
    return fail(refusal("destination-in-release", "the version folder under AFTER_PACK_LOC would be the release folder"));
  }
  if (sameOrInside(resolvedRelease, target)) {
    return fail(refusal("destination-overlaps-release", "the release folder lies inside the version folder being written"));
  }
  return { ok: true, target };
}

function isLink(filePath) {
  try {
    return fs.lstatSync(filePath).isSymbolicLink();
  } catch {
    return false;
  }
}

/** A plain copy, with Zinnia's fallback for SMB or mapped drives that reject permission bits. */
export function copyFileForMirror(source, destination) {
  try {
    fs.copyFileSync(source, destination);
  } catch (error) {
    if (error?.code !== "EPERM" && error?.code !== "EACCES") throw error;
    fs.writeFileSync(destination, fs.readFileSync(source));
  }
}

function siblingPath(destination, label) {
  return path.join(path.dirname(destination), `.${path.basename(destination)}.mirror-${label}-${crypto.randomBytes(6).toString("hex")}`);
}

function removeQuietly(filePath) {
  try {
    fs.rmSync(filePath, { force: true });
  } catch {
    // A hidden leftover is named in the output of the run that failed; nothing reads it later.
  }
}

/** Size and SHA-256 of a copy must equal the source. Returns the SHA-256 of the source. */
async function verifyCopy(source, destination) {
  const name = path.basename(destination);
  if (!fs.existsSync(destination)) {
    throw new MirrorError("verify-failed", `${name} is missing after the copy`);
  }
  const expected = fs.statSync(source).size;
  const actual = fs.statSync(destination).size;
  if (actual !== expected) {
    throw new MirrorError("verify-failed", `${name}: ${actual} bytes after the copy, expected ${expected}`);
  }
  const [sourceHash, destinationHash] = await Promise.all([sha256FileStream(source), sha256FileStream(destination)]);
  if (sourceHash !== destinationHash) {
    throw new MirrorError("verify-failed", `${name}: SHA-256 differs from the source`);
  }
  return sourceHash;
}

/**
 * Copies one file to a hidden staging sibling, verifies it, then swaps it in. The previous file is
 * kept until the new one is verified in place, and put back if the new one fails.
 */
async function replaceFile({ source, destination, copyFile }) {
  const name = path.basename(destination);
  const staging = siblingPath(destination, "new");
  let hash;
  try {
    copyFile(source, staging);
    hash = await verifyCopy(source, staging);
  } catch (error) {
    removeQuietly(staging);
    if (error instanceof MirrorError) throw error;
    throw new MirrorError("copy-failed", `${name}: ${describeError(error)}`);
  }

  const previous = fs.existsSync(destination);
  const rollback = previous ? siblingPath(destination, "old") : null;
  try {
    if (previous) fs.renameSync(destination, rollback);
    fs.renameSync(staging, destination);
  } catch (error) {
    if (rollback && fs.existsSync(rollback) && !fs.existsSync(destination)) {
      try {
        fs.renameSync(rollback, destination);
      } catch {
        // The previous file stays hidden beside the destination; the detail below names the failure.
      }
    }
    removeQuietly(staging);
    throw new MirrorError("copy-failed", `${name}: could not replace the file: ${describeError(error)}`);
  }

  try {
    await verifyCopy(source, destination);
  } catch (error) {
    removeQuietly(destination);
    if (rollback && fs.existsSync(rollback)) fs.renameSync(rollback, destination);
    throw error;
  }
  if (rollback) removeQuietly(rollback);
  return hash;
}

function failed(error) {
  return { ok: false, reason: error.reason, detail: error.detail, exitCode: error.exitCode };
}

/**
 * Mirrors the release folder into <destination>/<version>/. Returns { ok, target, copied, files } or
 * { ok: false, reason, detail, exitCode }. Never deletes anything except a same-name file it replaces.
 */
export async function mirrorReleaseAssets({
  releaseDir,
  destination,
  version,
  repoRoot = REPO_ROOT,
  copyFile = copyFileForMirror,
}) {
  if (!fs.existsSync(releaseDir) || !fs.statSync(releaseDir).isDirectory()) {
    return failed(refusal("release-dir-missing", `release folder is missing: ${releaseDir}`, EXIT.MISSING));
  }
  if (isLink(releaseDir)) {
    return failed(refusal("symlink-refused", `the release folder is a symbolic link or junction: ${releaseDir}`));
  }
  const listing = listReleaseFiles(releaseDir);
  if (listing.errors.length > 0) return failed(listing.errors[0]);
  if (listing.files.length === 0) {
    return failed(refusal("release-dir-empty", `the release folder has no regular file: ${releaseDir}`, EXIT.MISSING));
  }
  const resolved = resolveDestination({ releaseDir, destination, version, repoRoot });
  if (!resolved.ok) return failed(resolved.error);

  const { target } = resolved;
  fs.mkdirSync(target, { recursive: true });
  const files = [];
  try {
    for (const file of listing.files) {
      const hash = await replaceFile({ source: file.source, destination: path.join(target, file.name), copyFile });
      files.push({ name: file.name, size: file.size, sha256: hash });
    }
  } catch (error) {
    if (error instanceof MirrorError) return failed(error);
    return failed(new MirrorError("copy-failed", describeError(error)));
  }
  return { ok: true, target, copied: files.length, files };
}

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--dir <path>] [--version <semver>] [--json] [--help]

Copies the release folder to <AFTER_PACK_LOC>/<version>/ and checks each copy by size and SHA-256.
Stable versions require AFTER_PACK_LOC. Beta versions (X.Y.Z-beta.N) are skipped unless
OVERRIDE_BETA_MIRROR_SKIP=1. Nothing is deleted except a same-name file of the same version.

Environment (read from .env by the npm script, or from the shell):
  AFTER_PACK_LOC              Absolute folder outside this repository. Not a symlink, not the release folder.
  OVERRIDE_BETA_MIRROR_SKIP   Set to 1 to mirror a beta version.

Options:
  --dir <path>      Release folder. Default: artifacts/release/<version>/<stable or beta>.
  --version <semver> Default: package.json version.
  --json            Print one JSON line.
  --help            Show this help.

Exit codes: 0 mirrored and verified, or a beta skip; 1 refused; 2 usage; 3 missing input; 4 copy failed.`);
}

export async function main(argv = process.argv.slice(2), env = process.env) {
  let parsed;
  try {
    parsed = parseArguments(argv, { values: ["dir", "version"], flags: ["json", "help"] });
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

  const version = parsed.get("version") ?? readPackageVersion(REPO_ROOT);
  const json = parsed.has("json");
  const decision = mirrorDecision({ version, env });
  if (decision.action === "refuse") {
    printRefusals(SCRIPT, [decision.error]);
    return decision.error.exitCode;
  }
  if (decision.action === "skip") {
    console.log(`${SCRIPT}: skipped-beta: ${version} is a beta; set OVERRIDE_BETA_MIRROR_SKIP=1 to mirror it`);
    if (json) console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: true, skipped: "beta", version }));
    return EXIT.OK;
  }

  const releaseDir = parsed.get("dir")
    ? path.resolve(parsed.get("dir"))
    : artifactDirectory(REPO_ROOT, version, decision.channel);
  const result = await mirrorReleaseAssets({
    releaseDir,
    destination: decision.destination,
    version,
    repoRoot: REPO_ROOT,
  });
  if (!result.ok) {
    printRefusals(SCRIPT, [result]);
    if (json) console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: false, reason: result.reason }));
    return result.exitCode;
  }
  if (json) {
    console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: true, version, target: result.target, files: result.files }));
  } else {
    console.log(`${SCRIPT}: PASS ${result.copied} file(s) mirrored and verified in ${result.target}`);
    console.log(`  source: ${relativeTo(REPO_ROOT, releaseDir)}`);
  }
  return EXIT.OK;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = await main();
}
