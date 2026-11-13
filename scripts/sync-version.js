#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  parsePackageVersion,
  readDirectoryBuildPropsVersion,
  syncChangelogForVersion,
  updateDirectoryBuildProps,
} from "./sync-version-helpers.js";

const DEFAULT_ROOT = path.dirname(
  path.dirname(fileURLToPath(import.meta.url)),
);
const SCRIPT = "scripts/sync-version.js";

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--root <dir>] [--check]

Reads <root>/package.json and writes the version to:
  Directory.Build.props  Version and the four-part FileVersion.
  CHANGELOG.md           Download links (tag and file names), the beta
                         callout, and a "## Changes in \\\`vX.Y.Z:\\\`" section
                         with a placeholder when the version has none.

CHANGELOG.md is the GitHub release body in BCLS form. Replace the
placeholder before release; validate-changelog-version.js refuses it.

Options:
  --root <dir>  Repository root to update. Defaults to the parent of scripts/.
  --check       Make no writes. Exit 1 when a file is out of sync.
  --help        Show this help.`);
}

function parseArgs(argv) {
  const args = argv.slice(2);
  const options = { root: null, check: false, help: false };
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--check") {
      options.check = true;
      continue;
    }
    if (arg === "--root") {
      if (index + 1 >= args.length) {
        throw new Error("--root requires a directory");
      }
      options.root = args[index + 1];
      index += 1;
      continue;
    }
    if (arg.startsWith("--root=")) {
      options.root = arg.slice("--root=".length);
      if (!options.root) {
        throw new Error("--root requires a directory");
      }
      continue;
    }
    throw new Error(`unrecognized argument: ${arg}`);
  }
  return options;
}

function requireFile(root, name) {
  const file = path.join(root, name);
  let stat;
  try {
    stat = fs.statSync(file);
  } catch {
    throw new Error(`missing required file: ${file}`);
  }
  if (!stat.isFile()) {
    throw new Error(`required path is not a file: ${file}`);
  }
  return file;
}

function readPackageVersion(file) {
  const text = fs.readFileSync(file, "utf8");
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    throw new Error(`${file} is not valid JSON: ${message}`);
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error(`${file} must contain a JSON object`);
  }
  return parsePackageVersion(parsed.version);
}

function main(argv = process.argv) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(
      `sync-version: ${error instanceof Error ? error.message : String(error)}`,
    );
    return 2;
  }

  if (options.help) {
    printHelp();
    return 0;
  }

  const root = path.resolve(options.root ?? DEFAULT_ROOT);
  const changes = [];

  try {
    let rootStat;
    try {
      rootStat = fs.statSync(root);
    } catch {
      throw new Error(`root directory does not exist: ${root}`);
    }
    if (!rootStat.isDirectory()) {
      throw new Error(`root is not a directory: ${root}`);
    }

    const packageFile = requireFile(root, "package.json");
    const propsFile = requireFile(root, "Directory.Build.props");
    const changelogFile = requireFile(root, "CHANGELOG.md");

    const parsed = readPackageVersion(packageFile);

    const props = fs.readFileSync(propsFile, "utf8");
    const beforeProps = readDirectoryBuildPropsVersion(props);
    const nextProps = updateDirectoryBuildProps(props, parsed.version);
    if (nextProps !== props) {
      changes.push({
        file: "Directory.Build.props",
        path: propsFile,
        next: nextProps,
        detail: `Version ${beforeProps.version ?? "(missing)"} -> ${parsed.version}, FileVersion ${beforeProps.fileVersion ?? "(missing)"} -> ${parsed.fileVersion}`,
        verify: (text) => {
          const check = readDirectoryBuildPropsVersion(text);
          if (
            check.version !== parsed.version ||
            check.fileVersion !== parsed.fileVersion
          ) {
            throw new Error(
              "Directory.Build.props write verification failed",
            );
          }
        },
      });
    }

    const changelog = fs.readFileSync(changelogFile, "utf8");
    const nextChangelog = syncChangelogForVersion(changelog, parsed.version);
    if (nextChangelog !== changelog) {
      changes.push({
        file: "CHANGELOG.md",
        path: changelogFile,
        next: nextChangelog,
        detail: `v${parsed.version} (download links, beta callout, section)`,
        verify: (text) => {
          if (syncChangelogForVersion(text, parsed.version) !== text) {
            throw new Error("CHANGELOG.md write verification failed");
          }
        },
      });
    }
  } catch (error) {
    console.error(
      `sync-version: ${error instanceof Error ? error.message : String(error)}`,
    );
    return 1;
  }

  if (options.check) {
    if (changes.length === 0) {
      console.log("in sync");
      return 0;
    }
    for (const change of changes) {
      console.log(`would update ${change.file}: ${change.detail}`);
    }
    return 1;
  }

  if (changes.length === 0) {
    console.log("in sync");
    return 0;
  }

  try {
    for (const change of changes) {
      fs.writeFileSync(change.path, change.next, "utf8");
      const reread = fs.readFileSync(change.path, "utf8");
      if (reread !== change.next) {
        throw new Error(`${change.file} write verification failed`);
      }
      change.verify(reread);
      console.log(`${change.file}: ${change.detail}`);
    }
  } catch (error) {
    console.error(
      `sync-version: ${error instanceof Error ? error.message : String(error)}`,
    );
    return 1;
  }

  return 0;
}

process.exit(main());
