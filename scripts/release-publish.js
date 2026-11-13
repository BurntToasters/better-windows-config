#!/usr/bin/env node
/**
 * Publishes a verified draft release. Default is a dry run: it validates the local artifact
 * and prints the exact gh api commands without starting gh.
 *
 * --apply re-runs the draft verification against downloaded assets, then PATCHes draft=false.
 * It never creates a draft.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import {
  EXIT,
  describeError,
  formatCommand,
  parseArguments,
  printRefusals,
  relativeTo,
  repoRootFrom,
  sha256FileStream,
  writeJson,
} from "./lib/cli.js";
import {
  displayGh,
  getRelease,
  ghGetReleaseArgs,
  ghListReleasesArgs,
  listReleases,
  publishBody,
  runGhJson,
  selectRelease,
  ghRequestDir,
  writeGhBody,
} from "./lib/github-release.js";
import { prepareReleaseContext } from "./lib/release-context.js";
import { git } from "./lib/release-policy.js";

const SCRIPT = "scripts/release-publish.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const VERIFY_SCRIPT = path.join(REPO_ROOT, "scripts", "release-verify.js");

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [options] [--dry-run | --apply]

Publishes an existing draft only after verifying every downloaded asset. Default is a dry run.

Options:
  --version <semver>       Default package.json.
  --channel <stable|beta>  Default from the version.
  --dir <path>             D5 artifact directory. Default artifacts/release/<version>.
  --session <path>         Session file. Default artifacts/release/session.json.
  --trusted-key <path>     Public release key. Repeatable. Default: the keys embedded in TrustedKeys.cs.
  --root <dir>             Repository root. Default: the checkout this script is in.
  --repo <owner/name>      Default GH_REPO, then GH_REPO_OWNER and GH_REPO_NAME, then BurntToasters/better-windows-config.
  --commit <rev>           Default HEAD.
  --allow-detached         Allow a detached HEAD contained in the expected branch.
  --skip-preflight         Dry run only.
  --skip-session           Dry run only.
  --max-age-hours <n>      Session limit. Default 24.
  --json                   Print one JSON line.
  --help                   Show this help.

Exit codes: 0 published, no-op, or plan printed; 1 refused; 2 usage; 3 missing input;
4 gh failed. No gh process runs unless --apply is given.`);
}

function parseOptions(argv) {
  const parsed = parseArguments(argv, {
    values: ["root", "version", "channel", "dir", "session", { name: "trusted-key", multi: true }, "repo", "commit", "max-age-hours"],
    flags: ["apply", "dry-run", "skip-preflight", "skip-session", "allow-detached", "json", "help"],
  });
  if (parsed.has("help")) return { help: true };
  if (parsed.positionals.length > 0) throw new Error(`unexpected argument: ${parsed.positionals[0]}`);
  const apply = parsed.has("apply");
  if (apply && parsed.has("dry-run")) throw new Error("--apply and --dry-run cannot be combined");
  if (apply && parsed.has("skip-preflight")) throw new Error("--skip-preflight is dry-run only");
  if (apply && parsed.has("skip-session")) throw new Error("--skip-session is dry-run only");
  const channel = parsed.get("channel");
  if (channel !== undefined && channel !== "stable" && channel !== "beta") {
    throw new Error("--channel must be stable or beta");
  }
  const maxAgeHours = parsed.get("max-age-hours");
  if (maxAgeHours !== undefined && (!Number.isFinite(Number(maxAgeHours)) || Number(maxAgeHours) <= 0)) {
    throw new Error("--max-age-hours must be a positive number");
  }
  return {
    version: parsed.get("version"),
    channel,
    dir: parsed.get("dir"),
    session: parsed.get("session"),
    trustedKeys: parsed.all("trusted-key"),
    repo: parsed.get("repo"),
    commit: parsed.get("commit"),
    maxAgeHours: maxAgeHours === undefined ? 24 : Number(maxAgeHours),
    apply,
    skipPreflight: parsed.has("skip-preflight"),
    root: parsed.get("root"),
    skipSession: parsed.has("skip-session"),
    allowDetached: parsed.has("allow-detached"),
    json: parsed.has("json"),
  };
}

function resolveCommitish(root, value) {
  if (typeof value !== "string" || value.length === 0) return null;
  if (/^[0-9a-f]{40}$/i.test(value)) return value.toLowerCase();
  const resolved = git(root, ["rev-parse", `${value}^{commit}`], { allowFailure: true });
  return resolved && /^[0-9a-f]{40}$/i.test(resolved) ? resolved.toLowerCase() : null;
}

function verifyArgs(context, options) {
  const args = [VERIFY_SCRIPT];
  if (options.version) args.push("--version", options.version);
  if (options.channel) args.push("--channel", options.channel);
  args.push("--dir", context.dir);
  args.push("--session", context.sessionFile);
  // The verify child reads the embedded keys itself. Only an explicit --trusted-key is passed on.
  if (context.trustedKeySource === "explicit") {
    for (const key of context.trustedKeys) args.push("--trusted-key", key);
  }
  if (options.repo) args.push("--repo", options.repo);
  if (options.commit) args.push("--commit", options.commit);
  if (options.maxAgeHours !== undefined) args.push("--max-age-hours", String(options.maxAgeHours));
  if (options.allowDetached) args.push("--allow-detached");
  args.push("--apply");
  return args;
}

function publishPlan(context, options, publishFile) {
  const commands = [];
  if (context.repo) {
    commands.push(ghListReleasesArgs(context.repo));
    commands.push(ghGetReleaseArgs(context.repo, "<draft-id>"));
  }
  return {
    verifyCommand: formatCommand(process.execPath, verifyArgs(context, options)),
    commands,
    publishBody: publishFile,
  };
}

function printPlan(context, plan) {
  console.log(`${SCRIPT}: DRY RUN, no gh process started`);
  console.log(`  tag: ${context.tag}`);
  console.log(`  repo: ${context.repo?.slug ?? "(unresolved)"}`);
  console.log(`  artifact: ${relativeTo(context.root, context.dir)}`);
  console.log(`  commit: ${context.commit ?? "(unresolved)"}`);
  for (const warning of context.warnings) {
    console.log(`  WARNING [${warning.reason}]: ${warning.detail}`);
  }
  console.log("publish plan:");
  console.log(`  ${plan.verifyCommand}`);
  for (const command of plan.commands) {
    console.log(`  ${displayGh(command)}`);
  }
  console.log(`  gh api --method PATCH ${context.repo.api}/releases/<draft-id> --input ${plan.publishBody}`);
  console.log(`  gh api ${context.repo.api}/releases/<draft-id>  # read back the published release`);
  const body = JSON.parse(fs.readFileSync(plan.publishBody, "utf8"));
  console.log(`  # PATCH body: ${JSON.stringify(body)}`);
}

function runVerification(context, options) {
  const args = verifyArgs(context, options);
  console.log(`release-publish: running ${formatCommand(process.execPath, args)}`);
  const result = spawnSync(process.execPath, args, { cwd: context.root, stdio: "inherit", windowsHide: true });
  if (result.error) {
    console.error(`${SCRIPT}: REFUSED [verification-failed]: ${result.error.message}`);
    return EXIT.EXTERNAL;
  }
  if (result.status !== 0) {
    console.error(`${SCRIPT}: REFUSED [verification-failed]: release-verify exited ${result.status}`);
    return result.status ?? EXIT.REFUSED;
  }
  return EXIT.OK;
}

/** Asset ids, names, sizes, and digests of a release, as one comparable string. */
function assetSnapshot(release) {
  const lines = (release?.assets ?? [])
    .map((asset) => `${asset.id}|${asset.name}|${asset.size}|${asset.digest ?? ""}`)
    .sort();
  return { id: release?.id ?? null, text: lines.join("\n") };
}

function uniqueAssetPaths(bundle) {
  const paths = [bundle.manifestPath, bundle.signaturePath, ...bundle.installers.map((installer) => installer.file)];
  return [...new Set(paths.map((file) => path.resolve(file)))];
}

async function applyPublish(context, options, publishFile) {
  // Snapshot the draft before verification. The re-check right before the PATCH must match it.
  const beforeVerify = selectRelease(listReleases(context.repo, { cwd: context.root }), context.tag);
  const baseline =
    beforeVerify.ok && beforeVerify.kind === "draft"
      ? assetSnapshot(getRelease(context.repo, beforeVerify.release.id, { cwd: context.root }))
      : null;

  const verification = runVerification(context, options);
  if (verification !== EXIT.OK) return verification;

  const releases = listReleases(context.repo, { cwd: context.root });
  const selected = selectRelease(releases, context.tag);
  if (!selected.ok) {
    console.error(`${SCRIPT}: REFUSED [${selected.reason}]: ${selected.detail}`);
    return EXIT.REFUSED;
  }
  if (selected.kind === "none") {
    console.error(`${SCRIPT}: REFUSED [draft-missing]: no draft exists for ${context.tag}`);
    return EXIT.REFUSED;
  }

  if (selected.kind === "published") {
    const publishedTarget = resolveCommitish(context.root, selected.release.target_commitish);
    if (publishedTarget !== context.commit) {
      console.error(
        `${SCRIPT}: REFUSED [draft-target-mismatch]: published release targets ` +
          `${selected.release.target_commitish ?? "(unknown)"}, not HEAD ${context.commit}`,
      );
      return EXIT.REFUSED;
    }
    if (Boolean(selected.release.prerelease) !== context.prerelease) {
      console.error(
        `${SCRIPT}: REFUSED [prerelease-mismatch]: published prerelease=${selected.release.prerelease}, ` +
          `expected ${context.prerelease}`,
      );
      return EXIT.REFUSED;
    }
    console.log(`release-publish: already published ${context.tag} (id ${selected.release.id}); no-op`);
    return EXIT.OK;
  }

  const release = selected.release;
  const target = resolveCommitish(context.root, release.target_commitish);
  if (target !== context.commit) {
    console.error(
      `${SCRIPT}: REFUSED [draft-target-mismatch]: draft targets ${release.target_commitish ?? "(unknown)"}, ` +
        `not HEAD ${context.commit}`,
    );
    return EXIT.REFUSED;
  }
  if (Boolean(release.prerelease) !== context.prerelease) {
    console.error(
      `${SCRIPT}: REFUSED [prerelease-mismatch]: draft prerelease=${release.prerelease}, expected ${context.prerelease}`,
    );
    return EXIT.REFUSED;
  }

  // Re-read immediately before the PATCH. The asset ids, names, sizes, and digests must equal the
  // snapshot taken before verification, and every local asset with a digest must match it.
  const latestRelease = getRelease(context.repo, release.id, { cwd: context.root });
  const latest = assetSnapshot(latestRelease);
  if (baseline === null || baseline.id !== release.id || baseline.text !== latest.text) {
    console.error(`${SCRIPT}: REFUSED [asset-changed]: the draft's assets changed after verification; nothing was published`);
    return EXIT.REFUSED;
  }
  for (const assetPath of uniqueAssetPaths(context.bundle)) {
    const name = path.basename(assetPath);
    const remote = (latestRelease?.assets ?? []).find((asset) => asset.name === name);
    if (!remote) {
      console.error(`${SCRIPT}: REFUSED [asset-changed]: ${name} is no longer on the draft; nothing was published`);
      return EXIT.REFUSED;
    }
    if (typeof remote.digest === "string" && remote.digest.startsWith("sha256:")) {
      const local = await sha256FileStream(assetPath);
      if (remote.digest.slice("sha256:".length) !== local) {
        console.error(`${SCRIPT}: REFUSED [asset-hash-mismatch]: ${name} digest on the draft does not match the local file`);
        return EXIT.REFUSED;
      }
    }
  }

  const args = ["api", "--method", "PATCH", `${context.repo.api}/releases/${release.id}`, "--input", publishFile];
  console.log(`release-publish: running ${displayGh(args)}`);
  const published = runGhJson(args, { cwd: context.root });
  writeJson(path.join(context.dir, "publish-result.json"), {
    schema: 1,
    tool: SCRIPT,
    ok: true,
    tag: context.tag,
    version: context.version,
    channel: context.channel,
    prerelease: context.prerelease,
    commit: context.commit,
    releaseId: release.id,
    htmlUrl: published?.html_url ?? release.html_url ?? null,
    published: true,
    completedUtc: new Date().toISOString(),
  });
  console.log(
    `release-publish: PASS ${context.tag} id ${release.id} (${published?.html_url ?? "no URL returned"})`,
  );
  return EXIT.OK;
}

async function run(options) {
  const context = await prepareReleaseContext({
    ...options,
    requireSession: true,
    allowTestKey: !options.apply,
  });

  if (!context.repo) {
    printRefusals("release-publish", context.errors);
    return EXIT.USAGE;
  }
  fs.mkdirSync(context.dir, { recursive: true });
  const publishFile = writeGhBody(
    path.join(ghRequestDir(context.dir), "publish.json"),
    publishBody({ prerelease: context.prerelease }),
  );
  const plan = publishPlan(context, options, publishFile);

  if (options.json && !options.apply) {
    console.log(
      JSON.stringify({
        schema: 1,
        tool: SCRIPT,
        ok: context.ok,
        apply: false,
        version: context.version,
        channel: context.channel,
        tag: context.tag,
        repo: context.repo.slug,
        artifactDir: context.dir,
        errors: context.errors,
        warnings: context.warnings,
        verifyCommand: plan.verifyCommand,
        commands: plan.commands,
        publishBody: publishFile,
      }),
    );
  }

  if (!options.apply) {
    if (context.errors.length > 0) {
      printRefusals("release-publish", context.errors);
    }
    if (!options.json) {
      printPlan(context, plan);
    }
    return context.ok ? EXIT.OK : EXIT.REFUSED;
  }

  if (context.errors.length > 0) {
    printRefusals("release-publish", context.errors);
    return EXIT.REFUSED;
  }

  try {
    return await applyPublish(context, options, publishFile);
  } catch (error) {
    const reason = error?.reason ?? "gh-failed";
    const exitCode = error?.exitCode ?? EXIT.EXTERNAL;
    console.error(`${SCRIPT}: REFUSED [${reason}]: ${describeError(error)}`);
    return exitCode;
  }
}

async function main(argv = process.argv.slice(2)) {
  let options;
  try {
    options = parseOptions(argv);
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }
  if (options.help) {
    printHelp();
    return EXIT.OK;
  }
  return run(options);
}

process.exitCode = await main();
