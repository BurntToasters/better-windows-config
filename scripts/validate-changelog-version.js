#!/usr/bin/env node
/**
 * Fail when package.json's version has no matching `## [<version>]` heading in
 * CHANGELOG.md, or when that heading appears more than once.
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT = "scripts/validate-changelog-version.js";
const DEFAULT_ROOT = path.dirname(
  path.dirname(fileURLToPath(import.meta.url)),
);

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--root <dir>] [--help]

Reads <root>/package.json and <root>/CHANGELOG.md. Requires exactly one
heading of the form "## [<package version>]" (a trailing date is optional).

Options:
  --root <dir>  Repository root to check. Defaults to the parent of scripts/.
  --help        Show this help.

Exit codes: 0 match, 1 missing or duplicate heading, 2 argument error.`);
}

function parseArgs(argv) {
  const options = { root: DEFAULT_ROOT, help: false };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--root") {
      const value = argv[index + 1];
      if (value === undefined || value.startsWith("--")) {
        throw new Error("--root requires a directory");
      }
      options.root = path.resolve(value);
      index += 1;
      continue;
    }
    if (arg.startsWith("--root=")) {
      const value = arg.slice("--root=".length);
      if (!value) {
        throw new Error("--root requires a directory");
      }
      options.root = path.resolve(value);
      continue;
    }
    throw new Error(`unrecognized argument: ${arg}`);
  }

  return options;
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function readTextFile(filePath) {
  try {
    return fs.readFileSync(filePath, "utf8");
  } catch (error) {
    throw new Error(
      `could not read ${filePath}: ${
        error instanceof Error ? error.message : String(error)
      }`,
    );
  }
}

function readPackageVersion(root) {
  const packageFile = path.join(root, "package.json");
  const text = readTextFile(packageFile);
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    throw new Error(
      `${packageFile} is not valid JSON: ${
        error instanceof Error ? error.message : String(error)
      }`,
    );
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error(`${packageFile} must contain a JSON object`);
  }
  if (typeof parsed.version !== "string" || parsed.version.trim() === "") {
    throw new Error(`${packageFile} has no non-empty version string`);
  }
  return parsed.version.trim();
}

function checkChangelog(root, version) {
  const changelogFile = path.join(root, "CHANGELOG.md");
  const changelog = readTextFile(changelogFile);
  const pattern = new RegExp(
    `^## \\[${escapeRegExp(version)}\\]`,
    "gm",
  );
  const matches = [...changelog.matchAll(pattern)];

  if (matches.length === 0) {
    throw new Error(
      `${changelogFile} is missing heading "## [${version}]" for package.json version ${version}`,
    );
  }
  if (matches.length > 1) {
    const lines = matches
      .map(
        (match) =>
          changelog.slice(0, match.index).split("\n").length,
      )
      .join(", ");
    throw new Error(
      `${changelogFile} has ${matches.length} headings for version ${version} (lines ${lines}); keep exactly one`,
    );
  }

  return changelogFile;
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(
      `validate-changelog-version: ${
        error instanceof Error ? error.message : String(error)
      }`,
    );
    console.error(`Try: node ${SCRIPT} --help`);
    return 2;
  }

  if (options.help) {
    printHelp();
    return 0;
  }

  try {
    if (
      !fs.existsSync(options.root) ||
      !fs.statSync(options.root).isDirectory()
    ) {
      throw new Error(`root is not a directory: ${options.root}`);
    }
    const version = readPackageVersion(options.root);
    checkChangelog(options.root, version);
    console.log(`validate-changelog-version: ok (${version}).`);
    return 0;
  } catch (error) {
    console.error(
      `validate-changelog-version: ${
        error instanceof Error ? error.message : String(error)
      }`,
    );
    return 1;
  }
}

process.exitCode = main();
