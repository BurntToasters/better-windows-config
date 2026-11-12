#!/usr/bin/env node
/**
 * Creates or reuses one GitHub draft release. Default is a dry run: it validates the
 * local artifact and prints the exact gh api commands without starting gh.
 *
 * --apply creates a draft only. It never publishes.
 */
import fs from "node:fs";
import path from "node:path";

import {
  EXIT,
  describeError,
  parseArguments,
  printRefusals,
  relativeTo,
  sha256FileStream,
  writeJson,
} from "./lib/cli.js";
import {
  assetByName,
  assetBytesMatch,
  createTempDirectory,
  displayGh,
  downloadAssetToFile,
  draftBody,
  ghCreateReleaseArgs,
  ghDownloadAssetArgs,
  ghEditReleaseArgs,
  ghGetReleaseArgs,
  ghListReleasesArgs,
  ghUploadAssetArgs,
  ghDeleteAssetArgs,
  getRelease,
  listReleases,
  removeDirectory,
  runGhJson,
  selectRelease,
  writeGhBody,
} from "./lib/github-release.js";
import { prepareReleaseContext } from "./lib/release-context.js";
import { git } from "./lib/release-policy.js";

const SCRIPT = "scripts/release-draft.js";

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [options] [--dry-run | --apply]

Creates or reuses the single draft release for one version. The default is a dry run.

Options:
  --version <semver>       Default package.json.
  --channel <stable|beta>  Default from the version.
  --dir <path>             D5 artifact directory. Default artifacts/release/<version>.
  --session <path>         Session file. Default artifacts/release/session.json.
  --trusted-key <path>     Public release key. Repeatable.
  --repo <owner/name>      Default GH_REPO or the origin remote.
  --root <dir>             Repository root. Default: the checkout this script is in.
  --commit <rev>           Default HEAD. Any other commit is refused (session-commit-mismatch).
  --replace-assets         Delete an existing same-name asset before upload.
  --allow-detached         Allow a detached HEAD contained in the expected branch.
  --skip-preflight         Dry run only. Print the plan without preflight checks.
  --skip-session           Dry run only. Print the plan without a session.
  --allow-test-key         Dry run only. Accept a DRY-RUN TEST KEY.
  --max-age-hours <n>      Session limit. Default 24.
  --json                   Print one JSON line.
  --help                   Show this help.

Exit codes: 0 plan printed or draft created, 1 refused, 2 usage, 3 missing input,
4 gh failed. No gh process runs unless --apply is given.`);
}

function parseOptions(argv) {
  const parsed = parseArguments(argv, {
    values: [
      "root",
      "version",
      "channel",
      "dir",
      "session",
      { name: "trusted-key", multi: true },
      "repo",
      "commit",
      "max-age-hours",
    ],
    flags: [
      "apply",
      "dry-run",
      "skip-preflight",
      "skip-session",
      "allow-test-key",
      "replace-assets",
      "allow-detached",
      "json",
      "help",
    ],
  });
  if (parsed.has("help")) return { help: true };
  if (parsed.positionals.length > 0) throw new Error(`unexpected argument: ${parsed.positionals[0]}`);
  const apply = parsed.has("apply");
  if (apply && parsed.has("dry-run")) throw new Error("--apply and --dry-run cannot be combined");
  if (apply && parsed.has("skip-preflight")) throw new Error("--skip-preflight is dry-run only");
  if (apply && parsed.has("skip-session")) throw new Error("--skip-session is dry-run only");
  if (apply && parsed.has("allow-test-key")) throw new Error("--allow-test-key is dry-run only");
  const channel = parsed.get("channel");
  if (channel !== undefined && channel !== "stable" && channel !== "beta") {
    throw new Error("--channel must be stable or beta");
  }
  const maxAgeHours = parsed.get("max-age-hours");
  if (maxAgeHours !== undefined && (!Number.isFinite(Number(maxAgeHours)) || Number(maxAgeHours) <= 0)) {
    throw new Error("--max-age-hours must be a positive number");
  }
  return {
    root: parsed.get("root"),
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
    allowTestKey: parsed.has("allow-test-key"),
    replaceAssets: parsed.has("replace-assets"),
    allowDetached: parsed.has("allow-detached"),
    json: parsed.has("json"),
  };
}

/** SHA-256 of a draft asset's bytes, from a fresh download into a temporary folder. */
async function downloadedSha256(context, asset) {
  const temporary = createTempDirectory("bwc-draft-compare-");
  try {
    const destination = path.join(temporary, "asset");
    await downloadAssetToFile(ghDownloadAssetArgs(context.repo, asset.id), destination);
    return await sha256FileStream(destination);
  } finally {
    removeDirectory(temporary);
  }
}

function resolveCommitish(root, value) {
  if (typeof value !== "string" || value.length === 0) return null;
  if (/^[0-9a-f]{40}$/i.test(value)) return value.toLowerCase();
  const resolved = git(root, ["rev-parse", `${value}^{commit}`], { allowFailure: true });
  return resolved && /^[0-9a-f]{40}$/i.test(resolved) ? resolved.toLowerCase() : null;
}

function uniqueAssetPaths(bundle) {
  const paths = [bundle.manifestPath, bundle.signaturePath, ...bundle.installers.map((installer) => installer.file)];
  return [...new Set(paths.map((file) => path.resolve(file)))];
}

function commandsForPlan(context, options, bodyFiles) {
  if (!context.repo) {
    return { list: [], create: [], edit: [], upload: [] };
  }
  const assetPaths = context.bundle ? uniqueAssetPaths(context.bundle) : [];
  return {
    list: ghListReleasesArgs(context.repo),
    create: ghCreateReleaseArgs(context.repo, bodyFiles.create),
    edit: ghEditReleaseArgs(context.repo, "<draft-id>", bodyFiles.edit),
    upload: assetPaths.map((file) => ghUploadAssetArgs(context.repo, "<draft-id>", file)),
  };
}

function printPlan(context, commands, options) {
  console.log(`${SCRIPT}: DRY RUN, no gh process started`);
  console.log(`  tag: ${context.tag}`);
  console.log(`  repo: ${context.repo.slug}`);
  console.log(`  artifact: ${relativeTo(context.root, context.dir)}`);
  console.log(`  commit: ${context.commit ?? "(unresolved)"}`);
  console.log(`  prerelease: ${context.prerelease}`);
  for (const warning of context.warnings) {
    console.log(`  WARNING [${warning.reason}]: ${warning.detail}`);
  }
  console.log("draft plan:");
  console.log(`  ${displayGh(commands.list)}`);
  console.log("  # if no draft exists:");
  console.log(`  ${displayGh(commands.create)}`);
  console.log("  # if a draft exists, patch it by id:");
  console.log(`  ${displayGh(commands.edit)}`);
  console.log("  # upload only assets that are missing or have another size:");
  for (const upload of commands.upload) {
    console.log(`  ${displayGh(upload)}`);
  }
  if (options.replaceAssets) {
    console.log("  # --replace-assets also deletes a same-name asset before upload");
    for (const assetPath of uniqueAssetPaths(context.bundle)) {
      console.log(`  ${displayGh(ghDeleteAssetArgs(context.repo, "<asset-id>"))}  # ${path.basename(assetPath)}`);
    }
  }
}

async function applyDraft(context, options, bodyFiles) {
  const attempts = 2;
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    const releases = listReleases(context.repo, { cwd: context.root });
    const selected = selectRelease(releases, context.tag);
    if (!selected.ok) {
      console.error(`${SCRIPT}: REFUSED [${selected.reason}]: ${selected.detail}`);
      return EXIT.REFUSED;
    }

    let release = selected.release;
    if (selected.kind === "published") {
      console.error(`${SCRIPT}: REFUSED [already-published]: ${context.tag} is already published`);
      return EXIT.REFUSED;
    }

    if (selected.kind === "draft") {
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
      const editArgs = ghEditReleaseArgs(context.repo, release.id, bodyFiles.edit);
      console.log(`release-draft: running ${displayGh(editArgs)}`);
      release = runGhJson(editArgs, { cwd: context.root });
      console.log(`release-draft: reused draft ${release.id} (${context.tag})`);
    } else {
      const createArgs = ghCreateReleaseArgs(context.repo, bodyFiles.create);
      console.log(`release-draft: running ${displayGh(createArgs)}`);
      try {
        release = runGhJson(createArgs, { cwd: context.root });
        console.log(`release-draft: created draft ${release.id} (${context.tag})`);
      } catch (error) {
        if (attempt < attempts && /422|already_exists|already exists/i.test(String(error.message))) {
          console.log("release-draft: create raced another run; re-reading the draft list");
          continue;
        }
        throw error;
      }
    }

    const fresh = getRelease(context.repo, release.id, { cwd: context.root });
    const assetPaths = uniqueAssetPaths(context.bundle);
    const localByName = new Map();
    for (const assetPath of assetPaths) {
      localByName.set(path.basename(assetPath), {
        path: assetPath,
        size: fs.statSync(assetPath).size,
        sha256: await sha256FileStream(assetPath),
      });
    }
    const conflicts = [];
    const existingByName = new Map(
      (fresh.assets ?? []).map((asset) => [asset.name, asset]),
    );

    for (const assetPath of assetPaths) {
      const name = path.basename(assetPath);
      const local = localByName.get(name);
      const existing = existingByName.get(name) ?? assetByName(fresh, name);
      if (!existing) continue;
      // Compared by the SHA-256 of the downloaded draft asset. Size alone never decides a match.
      const remoteSha256 = await downloadedSha256(context, existing);
      const sameBytes = assetBytesMatch({ remoteSha256, localSha256: local.sha256 });
      if (sameBytes) {
        console.log(`release-draft: asset ${name} already matches (SHA-256); skipping`);
        continue;
      }
      if (!options.replaceAssets) {
        conflicts.push({
          name,
          detail: `existing asset SHA-256 ${remoteSha256 ?? "unknown"} differs from local ${local.sha256}`,
        });
        continue;
      }
      const deleteArgs = ghDeleteAssetArgs(context.repo, existing.id);
      console.log(`release-draft: running ${displayGh(deleteArgs)}  # ${name}`);
      runGhJson(deleteArgs, { cwd: context.root });
      existingByName.delete(name);
    }

    if (conflicts.length > 0) {
      for (const conflict of conflicts) {
        console.error(`${SCRIPT}: REFUSED [asset-conflict]: ${conflict.name}: ${conflict.detail}`);
      }
      console.error("release-draft: pass --replace-assets to delete and re-upload those assets.");
      return EXIT.REFUSED;
    }

    let uploaded = 0;
    for (const assetPath of assetPaths) {
      const name = path.basename(assetPath);
      if (existingByName.has(name)) continue;
      const uploadArgs = ghUploadAssetArgs(context.repo, release.id, assetPath);
      console.log(`release-draft: running ${displayGh(uploadArgs)}`);
      runGhJson(uploadArgs, { cwd: context.root });
      uploaded += 1;
    }

    const result = {
      schema: 1,
      tool: SCRIPT,
      ok: true,
      tag: context.tag,
      version: context.version,
      channel: context.channel,
      prerelease: context.prerelease,
      commit: context.commit,
      releaseId: release.id,
      htmlUrl: release.html_url ?? null,
      draft: true,
      uploaded,
      assets: assetPaths.map((file) => path.basename(file)),
      completedUtc: new Date().toISOString(),
    };
    writeJson(path.join(context.dir, "draft-result.json"), result);
    console.log(
      `release-draft: PASS draft ${context.tag} id ${release.id} (${uploaded} uploaded, ` +
        `${assetPaths.length - uploaded} already present)`,
    );
    return EXIT.OK;
  }

  console.error(`${SCRIPT}: REFUSED [gh-failed]: draft create did not settle after a retry`);
  return EXIT.EXTERNAL;
}

async function run(options) {
  const context = await prepareReleaseContext({
    ...options,
    requireSession: true,
  });

  if (!context.repo) {
    printRefusals("release-draft", context.errors);
    return EXIT.USAGE;
  }

  fs.mkdirSync(context.dir, { recursive: true });
  const bodyFiles = {
    create: writeGhBody(
      path.join(context.dir, "gh", "create-draft.json"),
      draftBody({
        tag: context.tag,
        version: context.version,
        commit: context.commit,
        notesBody: context.notes,
        prerelease: context.prerelease,
      }),
    ),
    edit: writeGhBody(
      path.join(context.dir, "gh", "edit-draft.json"),
      draftBody({
        tag: context.tag,
        version: context.version,
        commit: context.commit,
        notesBody: context.notes,
        prerelease: context.prerelease,
      }),
    ),
  };
  fs.writeFileSync(path.join(context.dir, "release-notes.md"), `${context.notes}\n`, "utf8");

  const commands = commandsForPlan(context, options, bodyFiles);
  if (options.json) {
    console.log(
      JSON.stringify({
        schema: 1,
        tool: SCRIPT,
        ok: context.ok,
        apply: options.apply,
        version: context.version,
        channel: context.channel,
        tag: context.tag,
        repo: context.repo?.slug ?? null,
        artifactDir: context.dir,
        errors: context.errors,
        warnings: context.warnings,
        commands: options.apply
          ? null
          : {
              list: commands.list,
              create: commands.create,
              edit: commands.edit,
              upload: commands.upload,
            },
      }),
    );
  }

  if (!options.apply) {
    if (context.errors.length > 0) {
      printRefusals("release-draft", context.errors);
    }
    if (!options.json) {
      printPlan(context, commands, options);
    }
    return context.ok ? EXIT.OK : EXIT.REFUSED;
  }

  if (context.errors.length > 0) {
    printRefusals("release-draft", context.errors);
    return EXIT.REFUSED;
  }

  try {
    return await applyDraft(context, options, bodyFiles);
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
