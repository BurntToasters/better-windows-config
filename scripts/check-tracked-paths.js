#!/usr/bin/env node
// Fails when a tracked text file holds an absolute user-profile path (a drive, a colon, Users, and a name segment).
// Such a path carries the Windows account name of whoever wrote the file, and it makes captured output depend on
// the machine that produced it. docs/failure-modes/quality-gate.md (PRIV-1) lists the cases.
//
// Allowed name segments: a placeholder (starts with <, % or $), Public, Default, All Users, and the neutral example
// names in exampleNames. The check never prints file content, only path:line.
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const repoRoot = path.resolve(path.dirname(scriptPath), "..");

const exampleNames = new Set(["user", "x", "someone", "test"]);
const systemNames = new Set(["public", "default"]);
const BINARY_SAMPLE_BYTES = 8000;
const MAX_BUFFER = 256 * 1024 * 1024;

const usage = `Usage: node scripts/check-tracked-paths.js [options]

Lists the files git tracks, skips binary files, and fails when a text file holds an absolute user-profile path:
a drive letter, a colon, Users, and a name segment, with \\, \\\\ (JSON-escaped), or / separators, in any case.
Allowed name segments: a placeholder that starts with <, %, or $, Public, Default, All Users, and the neutral
example names user, x, someone, and test. Each hit prints as path:line.

Options:
  --root <repo>   Repository root (default: the script's parent folder)
  --self-test     Plant a profile path in each separator style in a temporary tree, require the scan to fail on
                  each, and require the allowed forms to pass.
  --help          Show this help

Exit codes: 0 no hit, 1 a hit or a failed self-test, 2 usage error.
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

// Returns the 1-based line numbers of text that hold a profile path with a name that is not allowed.
function findProfilePathLines(text) {
  const lines = [];
  text.split(/\r?\n/).forEach((line, index) => {
    for (const match of line.matchAll(profilePathPattern)) {
      if (!isAllowedName(match[1], match[2] !== undefined)) {
        lines.push(index + 1);
      }
    }
  });
  return lines;
}

// Captures the name segment after Users. A separator is one or more of \, \\ (JSON-escaped), or /. The second group
// holds the " Users" of "All Users", whose name segment has a space.
const profilePathPattern =
  /(?<![A-Za-z0-9])[A-Za-z]:(?:\\|\/)+users(?:\\|\/)+([^\\/"'`\s,;)\]}]+)(\s+users(?![A-Za-z0-9]))?/gi;

function isAllowedName(name, followedByUsers) {
  const lower = name.toLowerCase();
  return (
    /^[<%$]/.test(name) ||
    systemNames.has(lower) ||
    exampleNames.has(lower) ||
    (lower === "all" && followedByUsers)
  );
}

// Returns { hits: [{ file, line }], scanned } for the relative paths under root. A file with a NUL byte near the
// start is binary and is skipped.
function scanFiles(root, relatives) {
  const hits = [];
  let scanned = 0;
  for (const relative of relatives) {
    const buffer = readFileSync(path.join(root, ...relative.split("/")));
    if (buffer.subarray(0, BINARY_SAMPLE_BYTES).includes(0)) {
      continue;
    }
    scanned += 1;
    for (const line of findProfilePathLines(buffer.toString("utf8"))) {
      hits.push({ file: relative, line });
    }
  }
  return { hits, scanned };
}

function trackedFiles(root) {
  const result = spawnSync("git", ["ls-files", "-z"], {
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
  return String(result.stdout).split("\0").filter((entry) => entry.length > 0);
}

// Builds a profile path from parts so this file never holds one itself.
function profilePath(separator, name, drive = "C") {
  return `${drive}:${separator}Users${separator}${name}${separator}Documents`;
}

function runSelfTest() {
  const temporaryRoot = mkdtempSync(path.join(tmpdir(), "bwc-tracked-paths-"));
  try {
    const failing = {
      "fail/backslash.txt": ["ok line", `see ${profilePath("\\", "alice")}\\file.txt`],
      "fail/escaped.json": [`{ "path": "${profilePath("\\\\", "alice")}\\\\file.txt" }`],
      "fail/forward.md": ["a", "b", `open ${profilePath("/", "alice")}/file.txt`],
      "fail/lowercase.txt": [`${profilePath("\\", "alice", "d").replace("Users", "users")}`],
      "fail/uppercase.txt": [`${profilePath("/", "alice").replace("Users", "USERS")}`],
      "fail/quoted.cs": [`var p = "${profilePath("\\\\", "bob")}";`],
      "fail/twice.txt": [`${profilePath("\\", "alice")} and ${profilePath("/", "bob")}`],
      // A name outside the example list still fails, including a short 8.3 form.
      "fail/short.txt": [`${profilePath("\\", "ABCDEF~1")}`],
    };
    const expectedLines = new Map([
      ["fail/backslash.txt", [2]],
      ["fail/escaped.json", [1]],
      ["fail/forward.md", [3]],
      ["fail/lowercase.txt", [1]],
      ["fail/uppercase.txt", [1]],
      ["fail/quoted.cs", [1]],
      ["fail/twice.txt", [1, 1]],
      ["fail/short.txt", [1]],
    ]);
    const passing = {
      "pass/repo.txt": ["<repo>\\artifacts\\oracle\\export-range\\x.reg", "<repo>/artifacts/oracle"],
      "pass/placeholder.txt": [
        "%USERPROFILE%\\Documents",
        "%USERNAME%",
        `${profilePath("\\", "<user>")}`,
        `${profilePath("\\\\", "<user>")}`,
        `${profilePath("/", "$env:USERNAME")}`,
        `${profilePath("\\", "%USERNAME%")}`,
      ],
      "pass/system.txt": [
        `${profilePath("\\", "Public")}`,
        `${profilePath("\\", "Default")}`,
        `${profilePath("/", "public")}`,
        `${profilePath("\\", "All Users")}`,
        `${profilePath("\\\\", "all users")}`,
      ],
      "pass/example.txt": [
        `${profilePath("\\", "user")}`,
        `${profilePath("/", "User")}`,
        `${profilePath("\\", "x")}`,
        `${profilePath("\\\\", "someone")}`,
        `${profilePath("/", "test")}`,
      ],
      "pass/bare.txt": ["C:\\Users\\", "C:\\Users", "Users\\alice is not rooted at a drive", "Users"],
    };

    const relatives = [];
    const write = (relative, lines) => {
      const filePath = path.join(temporaryRoot, ...relative.split("/"));
      mkdirSync(path.dirname(filePath), { recursive: true });
      writeFileSync(filePath, lines.join("\n") + "\n", "utf8");
      relatives.push(relative);
    };
    for (const [relative, lines] of Object.entries({ ...failing, ...passing })) {
      write(relative, lines);
    }
    // A binary file with a profile path in it is skipped.
    const binaryRelative = "pass/binary.bin";
    mkdirSync(path.join(temporaryRoot, "pass"), { recursive: true });
    writeFileSync(
      path.join(temporaryRoot, "pass", "binary.bin"),
      Buffer.concat([Buffer.from([0, 1, 2]), Buffer.from(profilePath("\\", "alice"))]),
    );
    relatives.push(binaryRelative);

    const { hits, scanned } = scanFiles(temporaryRoot, relatives);
    const problems = [];
    if (scanned !== relatives.length - 1) {
      problems.push(`expected ${relatives.length - 1} text files scanned, got ${scanned}`);
    }
    const byFile = new Map();
    for (const hit of hits) {
      byFile.set(hit.file, [...(byFile.get(hit.file) ?? []), hit.line]);
    }
    for (const [relative, lines] of expectedLines) {
      const actual = byFile.get(relative) ?? [];
      if (JSON.stringify(actual) !== JSON.stringify(lines)) {
        problems.push(`${relative}: expected hit lines [${lines}], got [${actual}]`);
      }
    }
    for (const relative of Object.keys(passing).concat([binaryRelative])) {
      if (byFile.has(relative)) {
        problems.push(`${relative}: expected no hit, got line(s) [${byFile.get(relative)}]`);
      }
    }
    if (problems.length > 0) {
      for (const problem of problems) {
        console.error(`check-tracked-paths self-test: ${problem}`);
      }
      return 1;
    }

    console.log(
      `check-tracked-paths self-test: ${Object.keys(failing).length} planted profile paths detected in backslash, JSON-escaped, and slash styles; placeholders, Public, Default, All Users, the example name, and a binary file passed.`,
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
      console.error(`check-tracked-paths self-test failed: ${error.message}`);
      return 1;
    }
  }

  let result;
  try {
    result = scanFiles(options.root, trackedFiles(options.root));
  } catch (error) {
    console.error(`check-tracked-paths: ${error.message}`);
    return 1;
  }

  if (result.hits.length > 0) {
    for (const hit of result.hits) {
      console.error(`${hit.file}:${hit.line}`);
    }
    console.error(
      `check-tracked-paths: ${result.hits.length} absolute user-profile path(s) in tracked files. Use <repo>, %USERPROFILE%, or <user> instead.`,
    );
    return 1;
  }

  console.log(`check-tracked-paths: ok (${result.scanned} tracked text file(s), no absolute user-profile path).`);
  return 0;
}

process.exitCode = main();
