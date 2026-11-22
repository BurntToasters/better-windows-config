#!/usr/bin/env node
// Fails when a git-tracked *.json file under docs/parity does not parse. A generator once wrote invalid JSON into a
// tracked oracle file and nothing noticed for weeks, so every reader of it failed or read a wrong value.
// docs/failure-modes/quality-gate.md (ORA-1) lists the cases.
//
// Each failure prints the file and the parser message. The message never carries file content: the excerpt that the
// JSON parser puts into some messages is removed.
//
// Second rule: every folder below docs/parity/oracle/gpedit/goldens that holds a complete.json on disk must have its
// complete.json and case.json tracked by git and not matched by an ignore rule. A broad ignore rule (*.user matched
// capture folders named *.User) once kept finished captures out of the repository with no warning. Only the goldens
// folder is walked. A failing folder prints as its path alone.
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const repoRoot = path.resolve(path.dirname(scriptPath), "..");

const ORACLE_ROOT = "docs/parity/";
const GOLDENS_ROOT = "docs/parity/oracle/gpedit/goldens";
const CAPTURE_FILES = ["complete.json", "case.json"];
const MAX_BUFFER = 256 * 1024 * 1024;
const BYTE_ORDER_MARK = "﻿";

const usage = `Usage: node scripts/check-oracle-json.js [options]

Lists the files git tracks and fails when a *.json file under docs/parity does not parse with JSON.parse.
A leading byte order mark is ignored. A tracked file that is missing in the working tree (a pending delete) is
skipped. A repository with no tracked JSON file under docs/parity fails, because the check would prove nothing.
Each failure prints as path: parser message, without file content.

Also walks docs/parity/oracle/gpedit/goldens and requires that every folder holding a complete.json on disk has its
complete.json and case.json tracked by git and not ignored. A folder that is untracked or ignored fails and prints as
its path alone. A folder with no complete.json is not required to be tracked.

Options:
  --root <repo>   Repository root (default: the script's parent folder)
  --self-test     In temporary folders that the script creates and removes, require that valid files pass, that
                  each kind of invalid file fails without printing its content, and that complete capture folders
                  pass only when tracked and not ignored.
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

function runGit(root, args, input) {
  const result = spawnSync("git", args, {
    cwd: root,
    encoding: "utf8",
    input,
    shell: false,
    windowsHide: true,
    maxBuffer: MAX_BUFFER,
  });
  if (result.error) {
    throw result.error;
  }
  return result;
}

// Returns the folders (relative, forward slashes) below the goldens root that hold a complete.json on disk.
// Only the goldens folder is walked.
function completeCaptureFolders(root) {
  const start = path.join(root, ...GOLDENS_ROOT.split("/"));
  const found = [];
  if (!existsSync(start)) {
    return found;
  }
  const walk = (directory, relative) => {
    const entries = readdirSync(directory, { withFileTypes: true });
    if (entries.some((entry) => entry.isFile() && entry.name === "complete.json")) {
      found.push(relative);
    }
    for (const entry of entries) {
      if (entry.isDirectory()) {
        walk(path.join(directory, entry.name), `${relative}/${entry.name}`);
      }
    }
  };
  walk(start, GOLDENS_ROOT);
  return found.sort();
}

// Returns { folders, failing }: every complete capture folder, and those whose complete.json or case.json is not
// tracked or that git ignores. The ignore check passes --no-index, so a rule that matches a tracked file also fails:
// it would hide the next capture.
function untrackedCaptureFolders(root) {
  const folders = completeCaptureFolders(root);
  if (folders.length === 0) {
    return { folders, failing: [] };
  }
  const listed = runGit(root, ["ls-files", "-z", "--", GOLDENS_ROOT]);
  if (listed.status !== 0) {
    throw new Error(`git ls-files exited with ${listed.status}: ${String(listed.stderr).trim()}`);
  }
  const tracked = new Set(String(listed.stdout).split("\0").filter((entry) => entry.length > 0));
  const candidates = folders.flatMap((folder) => [folder, ...CAPTURE_FILES.map((name) => `${folder}/${name}`)]);
  const ignoredRun = runGit(root, ["check-ignore", "--no-index", "-z", "--stdin"], `${candidates.join("\0")}\0`);
  if (ignoredRun.status !== 0 && ignoredRun.status !== 1) {
    throw new Error(`git check-ignore exited with ${ignoredRun.status}: ${String(ignoredRun.stderr).trim()}`);
  }
  const ignored = new Set(String(ignoredRun.stdout).split("\0").filter((entry) => entry.length > 0));
  const failing = folders.filter(
    (folder) =>
      ignored.has(folder) ||
      CAPTURE_FILES.some((name) => !tracked.has(`${folder}/${name}`) || ignored.has(`${folder}/${name}`)),
  );
  return { folders, failing };
}

// A failing capture folder prints as its path alone.
function captureFailureLines(failing) {
  return failing.map((folder) => folder);
}

// Builds a git repository under the temporary root and requires the capture tracking rule to pass or fail each case.
// Returns the number of cases. A broken expectation is appended to problems.
function runCaptureSelfTest(temporaryRoot, problems) {
  const repository = path.join(temporaryRoot, "capture-repo");
  mkdirSync(repository, { recursive: true });
  const git = (args) => {
    const result = runGit(repository, args);
    if (result.status !== 0) {
      throw new Error(`git ${args.join(" ")} exited with ${result.status}: ${String(result.stderr).trim()}`);
    }
  };
  git(["init", "--quiet"]);
  git(["config", "core.ignorecase", "true"]);
  const write = (relative, content) => {
    const filePath = path.join(repository, ...relative.split("/"));
    mkdirSync(path.dirname(filePath), { recursive: true });
    writeFileSync(filePath, content, "utf8");
  };
  write(".gitignore", "*.user\n");

  const complete = (folder) => {
    write(`${GOLDENS_ROOT}/${folder}/complete.json`, "{}");
    write(`${GOLDENS_ROOT}/${folder}/case.json`, "{}");
  };

  // Each entry: the folder below the goldens root, whether the check must fail for it, and why.
  const cases = [
    { folder: "A/Tracked_Complete", fails: false, why: "a tracked complete folder" },
    { folder: "A/Untracked_Complete", fails: true, why: "an untracked complete folder" },
    { folder: "B/Pol_Ignored.User", fails: true, why: "an ignored complete folder (name ends in .User, rule *.user)" },
    { folder: "A/Case_Json_Untracked", fails: true, why: "a folder whose complete.json is tracked and case.json is not" },
    { folder: "B/Forced.User", fails: true, why: "a complete folder tracked with add -f under an ignore rule" },
    { folder: "A/No_Complete", fails: false, why: "an untracked folder with no complete.json" },
    { folder: "A/Tracked_Complete/step-01", fails: false, why: "an untracked step folder with no complete.json" },
  ];
  for (const entry of cases) {
    if (entry.folder === "A/No_Complete" || entry.folder === "A/Tracked_Complete/step-01") {
      write(`${GOLDENS_ROOT}/${entry.folder}/case.json`, "{}");
      write(`${GOLDENS_ROOT}/${entry.folder}/notes.txt`, "x");
    } else {
      complete(entry.folder);
    }
  }
  // Outside the goldens root: an untracked complete.json there is not this rule's business and is not walked.
  write("docs/parity/oracle/other/Outside_Untracked/complete.json", "{}");

  git(["add", "--", `${GOLDENS_ROOT}/A/Tracked_Complete/complete.json`, `${GOLDENS_ROOT}/A/Tracked_Complete/case.json`]);
  git(["add", "--", `${GOLDENS_ROOT}/A/Case_Json_Untracked/complete.json`]);
  git(["add", "-f", "--", `${GOLDENS_ROOT}/B/Forced.User/complete.json`, `${GOLDENS_ROOT}/B/Forced.User/case.json`]);

  const { folders, failing } = untrackedCaptureFolders(repository);
  const expectedFolders = cases.filter((entry) => !entry.folder.endsWith("No_Complete") && !entry.folder.endsWith("step-01"));
  if (folders.length !== expectedFolders.length) {
    problems.push(`capture walk: expected ${expectedFolders.length} complete folders, got ${folders.length}`);
  }
  const failed = new Set(failing);
  for (const entry of cases) {
    const full = `${GOLDENS_ROOT}/${entry.folder}`;
    if (entry.fails && !failed.has(full)) {
      problems.push(`capture: expected a failure for ${entry.why}, got a pass`);
    }
    if (!entry.fails && failed.has(full)) {
      problems.push(`capture: expected a pass for ${entry.why}, got a failure`);
    }
  }
  if (failed.has("docs/parity/oracle/other/Outside_Untracked") || folders.some((folder) => folder.includes("/other/"))) {
    problems.push("capture: a folder outside the goldens root was walked");
  }

  // The printed text for a failure is the path alone.
  const lines = captureFailureLines(failing);
  if (lines.some((line) => line.includes(" ") || line.includes(":"))) {
    problems.push("capture: a failure line carries more than a path");
  }

  // Once the failing folders are tracked and the ignore rule is gone, every folder passes.
  writeFileSync(path.join(repository, ".gitignore"), "", "utf8");
  git(["add", "--", GOLDENS_ROOT]);
  const after = untrackedCaptureFolders(repository);
  if (after.failing.length !== 0) {
    problems.push(`capture: expected no failure after tracking every folder, got ${after.failing.length}`);
  }
  return cases.length + 3;
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

    const captureCases = runCaptureSelfTest(temporaryRoot, problems);

    if (problems.length > 0) {
      for (const problem of problems) {
        console.error(`check-oracle-json self-test: ${problem}`);
      }
      return 1;
    }

    console.log(
      `check-oracle-json self-test: ${Object.keys(failing).length} invalid files failed without file content; ${Object.keys(passing).length} valid files (one with a byte order mark), a deleted file, a file outside docs/parity and a non-JSON file passed; ${captureCases} capture folder cases behaved as required.`,
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
  let capture;
  try {
    const relatives = trackedOracleJson(options.root);
    if (relatives.length === 0) {
      console.error("check-oracle-json: no tracked *.json file under docs/parity; the check would prove nothing.");
      return 1;
    }
    result = checkFiles(options.root, relatives);
    capture = untrackedCaptureFolders(options.root);
  } catch (error) {
    console.error(`check-oracle-json: ${error.message}`);
    return 1;
  }

  let failed = false;
  if (result.failures.length > 0) {
    for (const failure of result.failures) {
      console.error(`${failure.file}: ${failure.message}`);
    }
    console.error(`check-oracle-json: ${result.failures.length} tracked oracle JSON file(s) do not parse.`);
    failed = true;
  }
  if (capture.failing.length > 0) {
    for (const line of captureFailureLines(capture.failing)) {
      console.error(line);
    }
    console.error(
      `check-oracle-json: ${capture.failing.length} complete capture folder(s) under ${GOLDENS_ROOT} are untracked or ignored.`,
    );
    failed = true;
  }
  if (failed) {
    return 1;
  }

  console.log(
    `check-oracle-json: ok (${result.checked} tracked JSON file(s) under docs/parity parse; ${capture.folders.length} complete capture folder(s) are tracked).`,
  );
  return 0;
}

process.exitCode = main();
