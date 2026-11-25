#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  parsePackageVersion,
  readAppManifestVersion,
  readDirectoryBuildPropsVersion,
  syncChangelogForVersion,
  updateAppManifest,
  updateDirectoryBuildProps,
} from "./sync-version-helpers.js";

const SELF_PATH = fileURLToPath(import.meta.url);
const DEFAULT_ROOT = path.dirname(
  path.dirname(SELF_PATH),
);
const SCRIPT = "scripts/sync-version.js";

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--root <dir>] [--check]

Reads <root>/package.json and writes the version to:
  Directory.Build.props  Version and the four-part FileVersion.
  src/*/app.manifest     assemblyIdentity version as the four-part FileVersion.
  CHANGELOG.md           Download links (tag and file names), the beta
                         callout, and a "## Changes in \\\`vX.Y.Z:\\\`" section
                         with a placeholder when the version has none.

CHANGELOG.md is the GitHub release body in BCLS form. Replace the
placeholder before release; validate-changelog-version.js refuses it.

Options:
  --root <dir>  Repository root to update. Defaults to the parent of scripts/.
  --check       Make no writes. Exit 1 when a file is out of sync.
  --self-test   Run the built-in cases against temporary fixtures and exit.
  --help        Show this help.`);
}

function parseArgs(argv) {
  const args = argv.slice(2);
  const options = { root: null, check: false, help: false, selfTest: false };
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--self-test") {
      options.selfTest = true;
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

/** Every src/<project>/app.manifest, as absolute paths in sorted order. */
function findAppManifests(root) {
  const srcDir = path.join(root, "src");
  let entries;
  try {
    entries = fs.readdirSync(srcDir, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((entry) => entry.isDirectory())
    .map((entry) => path.join(srcDir, entry.name, "app.manifest"))
    .filter((file) => fs.existsSync(file) && fs.statSync(file).isFile())
    .sort();
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

const SELF_TEST_CHANGELOG = [
  "# ⬇️ Downloads",
  "",
  "| Platform | File |",
  "| --- | --- |",
  "| x64 | [setup](/releases/download/v0.2.0/BetterWindowsConfig-0.2.0-win-x64-setup.exe) |",
  "",
  "## Changes in `v0.2.0:`",
  "",
  "- **Misc:** self-test fixture.",
  "",
].join("\n");

const SELF_TEST_MANIFEST = [
  '<?xml version="1.0" encoding="utf-8"?>',
  '<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">',
  "  <!-- a comment that mentions version=\"9.9.9.9\" must survive -->",
  '  <assemblyIdentity version="1.0.0.0" name="BetterWindowsConfig.app" />',
  "</assembly>",
  "",
].join("\n");

/**
 * Write a minimal, in-sync fixture repository. `manifests` maps a repo-relative
 * path to the manifest text. The fixture starts in sync for `packageVersion`.
 */
function writeSelfTestRoot(root, packageVersion, manifests) {
  fs.mkdirSync(root, { recursive: true });
  fs.writeFileSync(
    path.join(root, "package.json"),
    `${JSON.stringify({ name: "fixture", version: packageVersion }, null, 2)}\n`,
  );
  const parsed = parsePackageVersion(packageVersion);
  fs.writeFileSync(
    path.join(root, "Directory.Build.props"),
    `<Project>\n  <PropertyGroup>\n    <Version>${parsed.version}</Version>\n    <FileVersion>${parsed.fileVersion}</FileVersion>\n  </PropertyGroup>\n</Project>\n`,
  );
  fs.writeFileSync(path.join(root, "CHANGELOG.md"), SELF_TEST_CHANGELOG);
  for (const [relative, text] of Object.entries(manifests)) {
    const file = path.join(root, relative);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text);
  }
}

function runSelfTestCli(root, extra) {
  const result = spawnSync(
    process.execPath,
    [SELF_PATH, "--root", root, ...extra],
    { encoding: "utf8" },
  );
  return {
    status: result.status,
    output: `${result.stdout ?? ""}${result.stderr ?? ""}`,
  };
}

function hubManifest(root) {
  return fs.readFileSync(path.join(root, "src/Bwc.Hub/app.manifest"), "utf8");
}

/** Each case returns null on success or a failure message. */
const SELF_TEST_CASES = [
  {
    name: "manifest at the package fileVersion passes --check",
    run(root) {
      writeSelfTestRoot(root, "0.2.0", {
        "src/Bwc.Hub/app.manifest": SELF_TEST_MANIFEST.replace(
          'version="1.0.0.0"',
          'version="0.2.0.0"',
        ),
      });
      const result = runSelfTestCli(root, ["--check"]);
      return result.status === 0
        ? null
        : `expected exit 0, got ${result.status}: ${result.output.trim()}`;
    },
  },
  {
    name: "manifest at 1.0.0.0 is reported by --check",
    run(root) {
      writeSelfTestRoot(root, "0.2.0", {
        "src/Bwc.Hub/app.manifest": SELF_TEST_MANIFEST,
      });
      const result = runSelfTestCli(root, ["--check"]);
      if (result.status !== 1) {
        return `expected exit 1, got ${result.status}`;
      }
      return result.output.includes("src/Bwc.Hub/app.manifest")
        ? null
        : `output does not name the manifest: ${result.output.trim()}`;
    },
  },
  {
    name: "prerelease 0.2.0-beta.3 writes a four-part manifest version 0.2.0.3",
    run(root) {
      writeSelfTestRoot(root, "0.2.0-beta.3", {
        "src/Bwc.Hub/app.manifest": SELF_TEST_MANIFEST,
      });
      const write = runSelfTestCli(root, []);
      if (write.status !== 0) {
        return `write run exited ${write.status}: ${write.output.trim()}`;
      }
      if (!hubManifest(root).includes('version="0.2.0.3"')) {
        return "manifest does not carry version 0.2.0.3 after the write";
      }
      const check = runSelfTestCli(root, ["--check"]);
      return check.status === 0
        ? null
        : `--check after write exited ${check.status}: ${check.output.trim()}`;
    },
  },
  {
    name: "manifest with no assemblyIdentity is reported",
    run(root) {
      writeSelfTestRoot(root, "0.2.0", {
        "src/Bwc.Hub/app.manifest":
          '<?xml version="1.0" encoding="utf-8"?>\n<assembly manifestVersion="1.0" />\n',
      });
      const result = runSelfTestCli(root, ["--check"]);
      if (result.status !== 1) {
        return `expected exit 1, got ${result.status}`;
      }
      return result.output.includes("assemblyIdentity")
        ? null
        : `output does not mention assemblyIdentity: ${result.output.trim()}`;
    },
  },
  {
    name: "only the assemblyIdentity version attribute changes",
    run(root) {
      const original = SELF_TEST_MANIFEST.replace(/\n/g, "\r\n");
      writeSelfTestRoot(root, "0.2.0", {
        "src/Bwc.Hub/app.manifest": original,
      });
      const write = runSelfTestCli(root, []);
      if (write.status !== 0) {
        return `write run exited ${write.status}: ${write.output.trim()}`;
      }
      const expected = original.replace(
        '<assemblyIdentity version="1.0.0.0"',
        '<assemblyIdentity version="0.2.0.0"',
      );
      const actual = hubManifest(root);
      return actual === expected
        ? null
        : `manifest bytes differ beyond the version attribute`;
    },
  },
];

function runSelfTest() {
  let failures = 0;
  for (const testCase of SELF_TEST_CASES) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "sync-version-selftest-"));
    let problem;
    try {
      problem = testCase.run(root);
    } catch (error) {
      problem = error instanceof Error ? error.message : String(error);
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
    if (problem === null) {
      console.log(`PASS ${testCase.name}`);
    } else {
      failures += 1;
      console.log(`FAIL ${testCase.name}: ${problem}`);
    }
  }
  console.log(
    failures === 0
      ? `sync-version self-test: ${SELF_TEST_CASES.length} passed`
      : `sync-version self-test: ${failures} of ${SELF_TEST_CASES.length} failed`,
  );
  return failures === 0 ? 0 : 1;
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

  if (options.selfTest) {
    return runSelfTest();
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

    for (const manifestFile of findAppManifests(root)) {
      const relative = path.relative(root, manifestFile).split(path.sep).join("/");
      const manifest = fs.readFileSync(manifestFile, "utf8");
      let nextManifest;
      let beforeVersion;
      try {
        beforeVersion = readAppManifestVersion(manifest);
        nextManifest = updateAppManifest(manifest, parsed.version);
      } catch (error) {
        throw new Error(
          `${relative}: ${error instanceof Error ? error.message : String(error)}`,
        );
      }
      if (nextManifest !== manifest) {
        changes.push({
          file: relative,
          path: manifestFile,
          next: nextManifest,
          detail: `version ${beforeVersion ?? "(missing)"} -> ${parsed.fileVersion}`,
          verify: (text) => {
            if (readAppManifestVersion(text) !== parsed.fileVersion) {
              throw new Error(`${relative} write verification failed`);
            }
          },
        });
      }
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
