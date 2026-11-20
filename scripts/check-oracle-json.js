#!/usr/bin/env node
// Fails when a git-tracked *.json file under docs/parity does not parse. A generator once wrote invalid JSON into a
// tracked oracle file and nothing noticed for weeks, so every reader of it failed or read a wrong value.
// docs/failure-modes/quality-gate.md (ORA-1) lists the cases.
//
// Each failure prints the file and the parser message. The message never carries file content: the excerpt that the
// JSON parser puts into some messages is removed.
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const repoRoot = path.resolve(path.dirname(scriptPath), "..");

const ORACLE_ROOT = "docs/parity/";
const MAX_BUFFER = 256 * 1024 * 1024;
const BYTE_ORDER_MARK = "﻿";

const usage = `Usage: node scripts/check-oracle-json.js [options]

Lists the files git tracks and fails when a *.json file under docs/parity does not parse with JSON.parse.
A leading byte order mark is ignored. A tracked file that is missing in the working tree (a pending delete) is
skipped. A repository with no tracked JSON file under docs/parity fails, because the check would prove nothing.
Each failure prints as path: parser message, without file content.

Options:
  --root <repo>   Repository root (default: the script's parent folder)
  --self-test     In a temporary folder that the script creates and removes, require that valid files pass and
                  that each kind of invalid file fails without printing its content.
  --help          Show this help

Exit codes: 0 every file parses, 1 a parse failure or a failed self-test, 2 usage error.
`;

function parseArgs(argv) {
  const options = { root: repoRoot, selfTest: false, help: false };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
    } else if (arg === "--self-test") {
      options.selfTest = true;
    } else if (arg === "--root") {
      const value = argv[index + 1];
      if (value === undefined || value.startsWith("--")) {
        throw new Error("Missing value for --root.");
      }
      options.root = path.resolve(value);
      index += 1;
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }
  return options;
}

function isOracleJson(relative) {
  return relative.startsWith(ORACLE_ROOT) && relative.toLowerCase().endsWith(".json");
}

// Removes the source excerpt and the offending character that some parser messages quote from the file.
function sanitizeMessage(message) {
  return String(message)
    .replace(/Unexpected token '[\s\S]*?', "[\s\S]*"(?:\.\.\.)? is not valid JSON/, "Unexpected token (excerpt omitted)")
    .replace(/"[\s\S]*"(?:\.\.\.)? is not valid JSON/, "(excerpt omitted) is not valid JSON");
}

// Returns { failures: [{ file, message }], checked } for the relative paths under root.
function checkFiles(root, relatives) {
  const failures = [];
  let checked = 0;
  for (const relative of relatives) {
    const filePath = path.join(root, ...relative.split("/"));
    if (!existsSync(filePath)) {
      continue;
    }
    checked += 1;
    let text = readFileSync(filePath, "utf8");
    if (text.startsWith(BYTE_ORDER_MARK)) {
      text = text.slice(BYTE_ORDER_MARK.length);
    }
    try {
      JSON.parse(text);
    } catch (error) {
      failures.push({ file: relative, message: sanitizeMessage(error.message) });
    }
  }
  return { failures, checked };
}

function trackedOracleJson(root) {
  const result = spawnSync("git", ["ls-files", "-z", "--", "docs/parity"], {
    cwd: root,
    encoding: "utf8",
    shell: false,
    windowsHide: true,
    maxBuffer: MAX_BUFFER,
  });
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(`git ls-files exited with ${result.status}: ${String(result.stderr).trim()}`);
  }
  return String(result.stdout).split("\0").filter((entry) => entry.length > 0 && isOracleJson(entry));
}

function runSelfTest() {
  const temporaryRoot = mkdtempSync(path.join(tmpdir(), "bwc-oracle-json-"));
  try {
    const secret = "secretword";
    // Invalid files first: each must fail, and no message may carry a word of the file.
    const failing = {
      "docs/parity/fail/lost-backslash.json": `{ "pattern": [s*(-?d+)], "word": "${secret}" }`,
      "docs/parity/fail/trailing-comma.json": `{ "a": 1, "b": "${secret}", }`,
      "docs/parity/fail/truncated.json": `{ "a": [1, 2, "${secret}"`,
      "docs/parity/fail/empty.json": "",
      "docs/parity/fail/not-json.json": `${secret} is not json`,
      "docs/parity/fail/two-values.json": `{ "a": 1 } { "b": "${secret}" }`,
      "docs/parity/fail/single-quotes.json": `{ 'a': '${secret}' }`,
    };
    const passing = {
      "docs/parity/pass/object.json": '{ "a": 1, "b": [true, null, "text"] }',
      "docs/parity/pass/array.json": "[]",
      "docs/parity/pass/with-bom.json": `${BYTE_ORDER_MARK}{ "a": 1 }`,
      "docs/parity/pass/escapes.json": '{ "pattern": "\\\\s*(-?\\\\d+)" }',
    };
    const outside = {
      // Not checked: not under docs/parity, or not a .json file.
      "other/broken.json": "{ not json",
      "docs/parity/notes.md": "{ not json",
    };

    const write = (relative, content) => {
      const filePath = path.join(temporaryRoot, ...relative.split("/"));
      mkdirSync(path.dirname(filePath), { recursive: true });
      writeFileSync(filePath, content, "utf8");
    };
    for (const [relative, content] of Object.entries({ ...failing, ...passing, ...outside })) {
      write(relative, content);
    }

    const problems = [];
    const selected = Object.keys({ ...failing, ...passing, ...outside }).filter(isOracleJson).sort();
    const expectedSelected = Object.keys({ ...failing, ...passing }).sort();
    if (JSON.stringify(selected) !== JSON.stringify(expectedSelected)) {
      problems.push(`expected the selection [${expectedSelected}], got [${selected}]`);
    }

    // A tracked file that is missing in the working tree is skipped, not a failure.
    const missingRelative = "docs/parity/pass/deleted.json";
    const { failures, checked } = checkFiles(temporaryRoot, [...selected, missingRelative]);
    if (checked !== selected.length) {
      problems.push(`expected ${selected.length} files checked, got ${checked}`);
    }
    const failedFiles = new Set(failures.map((failure) => failure.file));
    for (const relative of Object.keys(failing)) {
      if (!failedFiles.has(relative)) {
        problems.push(`${relative}: expected a failure, got a pass`);
      }
    }
    for (const relative of [...Object.keys(passing), missingRelative]) {
      if (failedFiles.has(relative)) {
        problems.push(`${relative}: expected a pass, got a failure`);
      }
    }
    for (const failure of failures) {
      if (failure.message.length === 0) {
        problems.push(`${failure.file}: the failure carries no parser message`);
      }
      if (failure.message.includes(secret)) {
        problems.push(`${failure.file}: the failure message carries file content`);
      }
    }

    if (problems.length > 0) {
      for (const problem of problems) {
        console.error(`check-oracle-json self-test: ${problem}`);
      }
      return 1;
    }

    console.log(
      `check-oracle-json self-test: ${Object.keys(failing).length} invalid files failed without file content; ${Object.keys(passing).length} valid files (one with a byte order mark), a deleted file, a file outside docs/parity and a non-JSON file passed.`,
    );
    return 0;
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true });
  }
}

function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    console.error(usage);
    return 2;
  }

  if (options.help) {
    console.log(usage);
    return 0;
  }

  if (options.selfTest) {
    try {
      return runSelfTest();
    } catch (error) {
      console.error(`check-oracle-json self-test failed: ${error.message}`);
      return 1;
    }
  }

  let result;
  try {
    const relatives = trackedOracleJson(options.root);
    if (relatives.length === 0) {
      console.error("check-oracle-json: no tracked *.json file under docs/parity; the check would prove nothing.");
      return 1;
    }
    result = checkFiles(options.root, relatives);
  } catch (error) {
    console.error(`check-oracle-json: ${error.message}`);
    return 1;
  }

  if (result.failures.length > 0) {
    for (const failure of result.failures) {
      console.error(`${failure.file}: ${failure.message}`);
    }
    console.error(`check-oracle-json: ${result.failures.length} tracked oracle JSON file(s) do not parse.`);
    return 1;
  }

  console.log(`check-oracle-json: ok (${result.checked} tracked JSON file(s) under docs/parity parse).`);
  return 0;
}

process.exitCode = main();
