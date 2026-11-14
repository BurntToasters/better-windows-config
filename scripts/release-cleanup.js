#!/usr/bin/env node
/**
 * Resets this checkout to its upstream after `npm run release:publish`, so the next release starts from
 * the published commit. It is the only script that runs git reset, and it refuses unless:
 *   - the working tree is clean (git-ignored artifacts/ do not count),
 *   - artifacts/release/<version>/<channel>/publish-result.json records a published release for v<version>,
 *   - the current branch has an upstream, and `git fetch` of its remote succeeds,
 *   - HEAD is contained in that upstream, so no local commit is discarded.
 * The version comes from package.json. The upstream is resolved by git. No npm script expands either.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import { isDirectExecutionOf } from "./direct-execution.mjs";
import { EXIT, describeError, parseArguments, printRefusals, repoRootFrom } from "./lib/cli.js";
import { porcelainPaths, readPackageVersion } from "./lib/release-policy.js";
import { channelForVersion, tryParseSemVer } from "./lib/semver.js";

const SCRIPT = "scripts/release-cleanup.js";
const REPO_ROOT = repoRootFrom(import.meta.url);

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--root <dir>] [--json] [--help]

Run after npm run release:publish. Resets this checkout to its upstream (git reset --hard, then git clean -fd).
Refuses a dirty tree, a release that is not published, no upstream, a failed fetch, or HEAD not contained in
the upstream. Nothing is reset on a refusal.

Options:
  --root <dir>   Repository root. Default: the checkout this script is in.
  --json         Print one JSON line.
  --help         Show this help.

Exit codes: 0 reset, 1 refused, 2 usage, 4 git failed.`);
}

function refusal(reason, detail, exitCode = EXIT.REFUSED) {
  return { reason, detail, exitCode };
}

function gitRun(root, args) {
  return spawnSync("git", args, { cwd: root, encoding: "utf8", windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
}

function gitText(root, args) {
  const result = gitRun(root, args);
  return result.status === 0 ? String(result.stdout ?? "").trim() : null;
}

/** The publish record for this version and channel, or a refusal when it is missing or not published. */
export function publishRecordProblem(root, version) {
  const channel = channelForVersion(version);
  const relative = `artifacts/release/${version}/${channel}/publish-result.json`;
  let record;
  try {
    record = JSON.parse(fs.readFileSync(path.join(root, relative), "utf8"));
  } catch {
    return refusal("not-published", `no publish record at ${relative}; run npm run release:publish first`);
  }
  if (record?.ok !== true || record?.published !== true || record?.version !== version || record?.tag !== `v${version}`) {
    return refusal("not-published", `${relative} does not record v${version} as published; run npm run release:publish first`);
  }
  return null;
}

/**
 * Resets the checkout. Returns { ok: true, version, upstream, commit } or { ok: false, error }. The checks run in
 * order, and the reset is the last step, so a refusal changes nothing.
 */
export function runCleanup(root) {
  const version = readPackageVersion(root);
  if (!tryParseSemVer(version)) {
    return { ok: false, error: refusal("version-invalid", `package.json version is not SemVer: ${version}`, EXIT.USAGE) };
  }

  let dirty;
  try {
    dirty = porcelainPaths(root);
  } catch (error) {
    return { ok: false, error: refusal("git-failed", describeError(error), EXIT.EXTERNAL) };
  }
  if (dirty.length > 0) {
    return {
      ok: false,
      error: refusal("dirty-tree", `worktree has ${dirty.length} changed path(s): ${dirty.join(", ")}`),
    };
  }

  const published = publishRecordProblem(root, version);
  if (published) return { ok: false, error: published };

  const upstream = gitText(root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"]);
  if (!upstream) {
    return { ok: false, error: refusal("no-upstream", "the current branch has no upstream; set one, then run again") };
  }
  const remote = upstream.split("/")[0];

  const fetched = gitRun(root, ["fetch", remote]);
  if (fetched.error || fetched.status !== 0) {
    const detail = String(fetched.stderr || fetched.error?.message || "git fetch failed").trim().split(/\r?\n/)[0];
    return { ok: false, error: refusal("fetch-failed", `git fetch ${remote} failed: ${detail}`, EXIT.EXTERNAL) };
  }

  const target = gitText(root, ["rev-parse", "--verify", "--quiet", `${upstream}^{commit}`]);
  if (!target) {
    return { ok: false, error: refusal("no-upstream", `${upstream} does not resolve to a commit after the fetch`) };
  }
  if (gitRun(root, ["merge-base", "--is-ancestor", "HEAD", target]).status !== 0) {
    return {
      ok: false,
      error: refusal("head-not-upstream", `HEAD is not contained in ${upstream}; push or discard the local commits first`),
    };
  }

  const reset = gitRun(root, ["reset", "--hard", target]);
  if (reset.status !== 0) {
    return { ok: false, error: refusal("reset-failed", `git reset --hard failed: ${String(reset.stderr).trim()}`, EXIT.EXTERNAL) };
  }
  const clean = gitRun(root, ["clean", "-fd"]);
  if (clean.status !== 0) {
    return { ok: false, error: refusal("reset-failed", `git clean -fd failed: ${String(clean.stderr).trim()}`, EXIT.EXTERNAL) };
  }
  return { ok: true, version, upstream, commit: target };
}

function main(argv = process.argv.slice(2)) {
  let parsed;
  try {
    parsed = parseArguments(argv, { values: ["root"], flags: ["json", "help"] });
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

  const root = path.resolve(parsed.get("root") ?? REPO_ROOT);
  const result = runCleanup(root);
  if (!result.ok) {
    printRefusals(SCRIPT, [result.error]);
    if (parsed.has("json")) console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: false, reason: result.error.reason }));
    return result.error.exitCode;
  }
  if (parsed.has("json")) {
    console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: true, version: result.version, upstream: result.upstream, commit: result.commit }));
  } else {
    console.log(`${SCRIPT}: PASS v${result.version} reset to ${result.upstream} (${result.commit.slice(0, 12)})`);
  }
  return EXIT.OK;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = main();
}
