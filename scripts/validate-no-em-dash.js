#!/usr/bin/env node
/**
 * Fail when a tracked text file contains U+2014 EM DASH.
 * The repository uses ASCII punctuation (hyphen, comma, colon) everywhere.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT = "scripts/validate-no-em-dash.js";
const SCRIPT_PATH = fileURLToPath(import.meta.url);
const DEFAULT_ROOT = path.dirname(path.dirname(SCRIPT_PATH));
const EM_DASH = "\u2014";
const EM_DASH_LABEL = "U+2014 em dash";

const BINARY_EXTENSIONS = new Set([
  ".7z",
  ".app",
  ".bin",
  ".bmp",
  ".dll",
  ".dmg",
  ".dylib",
  ".eot",
  ".exe",
  ".gif",
  ".gz",
  ".ico",
  ".icns",
  ".jpeg",
  ".jpg",
  ".lock",
  ".pdb",
  ".pdf",
  ".png",
  ".pri",
  ".so",
  ".tar",
  ".ttf",
  ".webp",
  ".woff",
  ".woff2",
  ".xbf",
  ".zip",
]);

const SKIP_DIRECTORIES = new Set([
  ".git",
  "artifacts",
  "bin",
  "node_modules",
  "obj",
  "publish",
  "TestResults",
]);

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--root <dir>] [--self-test] [--help]

Scans tracked text files for U+2014 EM DASH and prints path:line:column.
Skipped directories: bin, obj, publish, artifacts, TestResults, .git,
node_modules. Skipped binary extensions and files containing NUL bytes.

Options:
  --root <dir>  Scan this directory by walking the tree instead of asking git.
  --self-test   Create a temporary fixture, require the scanner to reject it,
                and remove the fixture. Does not touch the repository.
  --help        Show this help.

Exit codes: 0 clean, 1 em dash found, 2 argument error.`);
}

function parseArgs(argv) {
  const options = {
    root: DEFAULT_ROOT,
    explicitRoot: false,
    selfTest: false,
    help: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    if (arg === "--self-test") {
      options.selfTest = true;
      continue;
    }
    if (arg === "--root") {
      const value = argv[index + 1];
      if (value === undefined || value.startsWith("--")) {
        throw new Error("--root requires a directory");
      }
      options.root = path.resolve(value);
      options.explicitRoot = true;
      index += 1;
      continue;
    }
    if (arg.startsWith("--root=")) {
      const value = arg.slice("--root=".length);
      if (!value) {
        throw new Error("--root requires a directory");
      }
      options.root = path.resolve(value);
      options.explicitRoot = true;
      continue;
    }
    throw new Error(`unrecognized argument: ${arg}`);
  }

  return options;
}

function isBinaryExtension(relativePath) {
  return BINARY_EXTENSIONS.has(path.extname(relativePath).toLowerCase());
}

function shouldSkip(relativePath) {
  if (isBinaryExtension(relativePath)) return true;
  const segments = relativePath.split(/[\\/]+/);
  return segments.slice(0, -1).some((segment) => SKIP_DIRECTORIES.has(segment));
}

function walkTree(root) {
  const files = [];

  function visit(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        if (!SKIP_DIRECTORIES.has(entry.name)) {
          visit(absolute);
        }
        continue;
      }
      if (!entry.isFile() && !entry.isSymbolicLink()) continue;
      const relative = path
        .relative(root, absolute)
        .split(path.sep)
        .join("/");
      if (!shouldSkip(relative)) {
        files.push(relative);
      }
    }
  }

  visit(root);
  return files;
}

function listGitFiles(root) {
  const result = spawnSync("git", ["ls-files", "-z"], {
    cwd: root,
    windowsHide: true,
    maxBuffer: 64 * 1024 * 1024,
  });
  if (result.error || result.status !== 0) {
    return null;
  }

  return String(result.stdout || "")
    .split("\0")
    .filter((entry) => entry.length > 0);
}

function scanFile(root, relativePath) {
  const absolute = path.join(root, relativePath);
  let buffer;
  try {
    buffer = fs.readFileSync(absolute);
  } catch {
    return null;
  }

  // Binary files and UTF-16 text both contain NUL bytes. Decoding them as
  // UTF-8 would produce false positives, so leave them out of the scan.
  if (buffer.includes(0)) {
    return [];
  }

  const text = buffer.toString("utf8");
  if (!text.includes(EM_DASH)) {
    return [];
  }

  const hits = [];
  const lines = text.split(/\r?\n/);
  for (let index = 0; index < lines.length; index += 1) {
    const column = lines[index].indexOf(EM_DASH);
    if (column !== -1) {
      hits.push({ file: relativePath, line: index + 1, column: column + 1 });
    }
  }
  return hits;
}

function scanRoot(root, explicitRoot) {
  let files = null;
  if (!explicitRoot && fs.existsSync(path.join(root, ".git"))) {
    files = listGitFiles(root);
  }
  if (files === null) {
    files = walkTree(root);
  }

  const hits = [];
  let scanned = 0;
  for (const relativePath of files.sort()) {
    if (shouldSkip(relativePath)) continue;
    const fileHits = scanFile(root, relativePath);
    if (fileHits === null) continue;
    scanned += 1;
    hits.push(...fileHits);
  }

  return { hits, scanned };
}

function runSelfTest() {
  const temporaryRoot = fs.mkdtempSync(
    path.join(os.tmpdir(), "bwc-em-dash-self-test-"),
  );

  try {
    fs.writeFileSync(
      path.join(temporaryRoot, "em-dash.txt"),
      `clean line\nbad ${EM_DASH} dash\n`,
      "utf8",
    );

    const result = spawnSync(
      process.execPath,
      [SCRIPT_PATH, "--root", temporaryRoot],
      { encoding: "utf8", windowsHide: true, timeout: 60_000 },
    );
    const output = `${result.stdout || ""}${result.stderr || ""}`;

    if (result.error) {
      throw new Error(`could not run the scanner: ${result.error.message}`);
    }
    if (result.status !== 1) {
      throw new Error(
        `expected exit code 1 from the fixture, received ${result.status}.\n${output}`,
      );
    }

    const expected = "em-dash.txt:2:5";
    if (!output.includes(expected)) {
      throw new Error(
        `expected the scanner to report ${expected}.\n${output}`,
      );
    }
    if (output.includes("em-dash.txt:1:")) {
      throw new Error(`scanner reported the clean fixture line.\n${output}`);
    }

    console.log(
      `validate-no-em-dash self-test: detected ${expected} and ignored line 1.`,
    );
    return 0;
  } catch (error) {
    console.error(
      `validate-no-em-dash self-test failed: ${
        error instanceof Error ? error.message : String(error)
      }`,
    );
    return 1;
  } finally {
    fs.rmSync(temporaryRoot, { recursive: true, force: true });
  }
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(
      `validate-no-em-dash: ${
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

  if (options.selfTest) {
    return runSelfTest();
  }

  if (!fs.existsSync(options.root) || !fs.statSync(options.root).isDirectory()) {
    console.error(
      `validate-no-em-dash: root is not a directory: ${options.root}`,
    );
    return 1;
  }

  // An explicit --root always walks the tree. The default root prefers git so
  // ignored build output is skipped without reading it.
  const result = scanRoot(options.root, options.explicitRoot);

  if (result.hits.length > 0) {
    console.error(
      `validate-no-em-dash: ${EM_DASH_LABEL} is not allowed in tracked text files. Use ASCII "-", ",", or ":" instead.`,
    );
    for (const hit of result.hits) {
      console.error(`  ${hit.file}:${hit.line}:${hit.column}`);
    }
    console.error(
      `validate-no-em-dash: ${result.hits.length} occurrence(s) in ${result.scanned} scanned file(s).`,
    );
    return 1;
  }

  console.log(
    `validate-no-em-dash: ok (${result.scanned} scanned file(s)).`,
  );
  return 0;
}

process.exitCode = main();
