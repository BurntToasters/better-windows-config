#!/usr/bin/env node
/**
 * Verifies that a GitHub draft release holds the exact signed artifact from the local D5 run.
 * Default is a dry run: it prints the gh api commands and verifies the local artifact only.
 *
 * --apply reads the draft inventory and downloads every required asset through gh, then
 * re-validates the downloaded manifest, signature, sizes, and hashes. No publish.
 */
import fs from "node:fs";
import path from "node:path";

import { EXIT, describeError, parseArguments, printRefusals, relativeTo, sha256FileStream, writeJson } from "./lib/cli.js";
import {
  assetByName,
  createTempDirectory,
  displayGh,
  downloadAssetToFile,
  ghDownloadAssetArgs,
  ghGetReleaseArgs,
  ghListReleasesArgs,
  listReleases,
  removeDirectory,
  selectRelease,
} from "./lib/github-release.js";
import { loadReleaseBundle } from "./lib/release-bundle.js";
import { prepareReleaseContext } from "./lib/release-context.js";
import { readTrustedKeysFile } from "./lib/trusted-keys.js";
import { git } from "./lib/release-policy.js";

const SCRIPT = "scripts/release-verify.js";

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [options] [--dry-run | --apply]

Verifies the local D5 artifact and, with --apply, the matching GitHub draft assets.

Options:
  --version <semver>       Default package.json.
  --channel <stable|beta>  Default from the version.
  --dir <path>             D5 artifact directory. Default artifacts/release/<version>.
  --session <path>         Session file. Default artifacts/release/session.json.
  --trusted-key <path>     Public release key. Repeatable. Default: the keys embedded in TrustedKeys.cs.
  --repo <owner/name>      Default GH_REPO, then GH_REPO_OWNER and GH_REPO_NAME, then BurntToasters/better-windows-config.
  --commit <rev>           Default HEAD.
  --allow-detached         Allow a detached HEAD contained in the expected branch.
  --skip-preflight         Dry run only.
  --skip-session           Dry run only.
  --max-age-hours <n>      Session limit. Default 24.
  --json                   Print one JSON line.
  --help                   Show this help.

Exit codes: 0 verified or plan printed, 1 refused, 2 usage, 3 missing input,
4 gh failed. No gh process runs unless --apply is given.`);
}

function parseOptions(argv) {
  const parsed = parseArguments(argv, {
    values: ["version", "channel", "dir", "session", { name: "trusted-key", multi: true }, "repo", "commit", "max-age-hours"],
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

function verificationCommands(context) {
  if (!context.repo) return [];
  const commands = [ghListReleasesArgs(context.repo), ghGetReleaseArgs(context.repo, "<draft-id>")];
  for (const asset of context.bundle?.requiredAssets ?? []) {
    commands.push(ghDownloadAssetArgs(context.repo, "<asset-id>"));
  }
  return commands;
}

function printPlan(context) {
  console.log(`${SCRIPT}: DRY RUN, no gh process started`);
  console.log(`  tag: ${context.tag}`);
  console.log(`  repo: ${context.repo.slug}`);
  console.log(`  artifact: ${relativeTo(context.root, context.dir)}`);
  for (const warning of context.warnings) {
    console.log(`  WARNING [${warning.reason}]: ${warning.detail}`);
  }
  console.log("verify plan:");
  for (const command of verificationCommands(context)) {
    console.log(`  ${displayGh(command)}`);
  }
  console.log("  # the script writes each asset stream to a temporary folder, then re-validates it");
}

async function applyVerify(context, options) {
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
    console.error(`${SCRIPT}: REFUSED [already-published]: ${context.tag} is published, not a draft`);
    return EXIT.REFUSED;
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

  // The asset names are the local file names: bwc-<channel>.json, its .minisig, and the setup EXEs.
  const expected = [
    { name: path.basename(context.bundle.manifestPath), file: context.bundle.manifestPath },
    { name: path.basename(context.bundle.signaturePath), file: context.bundle.signaturePath },
    ...context.bundle.installers.map((installer) => ({ name: installer.fileName, file: installer.file })),
  ];
  const missing = expected.filter((entry) => !assetByName(release, entry.name));
  if (missing.length > 0) {
    for (const entry of missing) {
      console.error(`${SCRIPT}: REFUSED [asset-missing]: draft has no ${entry.name}`);
    }
    return EXIT.REFUSED;
  }

  const errors = [];
  for (const entry of expected) {
    const asset = assetByName(release, entry.name);
    const localSize = fs.statSync(entry.file).size;
    if (typeof asset.size === "number" && asset.size !== localSize) {
      errors.push({
        reason: "asset-size-mismatch",
        detail: `${entry.name}: draft is ${asset.size} bytes, local is ${localSize}`,
      });
    }
  }
  if (errors.length > 0) {
    printRefusals("release-verify", errors);
    return EXIT.REFUSED;
  }

  const temporary = createTempDirectory("bwc-draft-verify-");
  try {
    for (const entry of expected) {
      const asset = assetByName(release, entry.name);
      const destination = path.join(temporary, entry.name);
      const args = ghDownloadAssetArgs(context.repo, asset.id);
      console.log(`release-verify: running ${displayGh(args)}  # ${entry.name}`);
      await downloadAssetToFile(args, destination);
      const remoteHash = await sha256FileStream(destination);
      const localHash = await sha256FileStream(entry.file);
      if (remoteHash !== localHash) {
        errors.push({
          reason: "asset-hash-mismatch",
          detail: `${entry.name}: draft SHA-256 ${remoteHash} does not match local ${localHash}`,
        });
      }
    }
    if (errors.length > 0) {
      printRefusals("release-verify", errors);
      return EXIT.REFUSED;
    }

    const embedded = readTrustedKeysFile(context.root);
    const downloaded = await loadReleaseBundle({
      dir: temporary,
      version: context.version,
      channel: context.channel,
      trustedKeys: context.trustedKeys,
      embeddedKeys: embedded.ok ? embedded.embedded : undefined,
      requireArches: ["win-x64", "win-arm64"],
    });
    if (!downloaded.ok) {
      printRefusals("release-verify", downloaded.errors);
      writeJson(path.join(context.dir, "draft-verification.json"), {
        schema: 1,
        tool: SCRIPT,
        ok: false,
        tag: context.tag,
        version: context.version,
        channel: context.channel,
        commit: context.commit,
        releaseId: release.id,
        errors: downloaded.errors,
        checks: downloaded.checks,
        completedUtc: new Date().toISOString(),
      });
      return EXIT.REFUSED;
    }

    writeJson(path.join(context.dir, "draft-verification.json"), {
      schema: 1,
      tool: SCRIPT,
      ok: true,
      tag: context.tag,
      version: context.version,
      channel: context.channel,
      commit: context.commit,
      releaseId: release.id,
      assets: expected.map((entry) => assetByName(release, entry.name)?.name),
      checks: downloaded.checks,
      completedUtc: new Date().toISOString(),
    });
    console.log(
      `release-verify: PASS draft ${context.tag} id ${release.id} (${expected.length} assets, ` +
        `HEAD ${String(context.commit).slice(0, 12)})`,
    );
    return EXIT.OK;
  } finally {
    removeDirectory(temporary);
  }
}

async function run(options) {
  const context = await prepareReleaseContext({
    ...options,
    requireSession: options.apply,
    allowTestKey: !options.apply,
  });

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
        repo: context.repo?.slug ?? null,
        artifactDir: context.dir,
        errors: context.errors,
        warnings: context.warnings,
        commands: verificationCommands(context),
      }),
    );
  }

  if (!options.apply) {
    if (context.errors.length > 0) {
      printRefusals("release-verify", context.errors);
    }
    if (!options.json && context.repo) {
      printPlan(context);
    }
    return context.ok ? EXIT.OK : EXIT.REFUSED;
  }

  if (context.errors.length > 0) {
    printRefusals("release-verify", context.errors);
    return EXIT.REFUSED;
  }
  if (!context.repo) {
    console.error(`${SCRIPT}: REFUSED [repo-invalid]: no GitHub repository was resolved`);
    return EXIT.USAGE;
  }

  try {
    return await applyVerify(context, options);
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
