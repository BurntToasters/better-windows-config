#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const scriptDir = path.dirname(scriptPath);
const repoRoot = path.resolve(scriptDir, "..");

const forbiddenIdentifiers = [
  "RegCreateKeyEx",
  "RegSetValueEx",
  "RegDeleteKeyEx",
  "RegDeleteValue",
  "RegDeleteTree",
  "RegRenameKey",
  "RegRestoreKey",
  "RegLoadKey",
  "RegUnLoadKey",
  "RegSetKeySecurity",
  "RegReplaceKey",
  "RegCopyTree",
  "RegSaveKey",
  "NtSetValueKey",
];

const identifierPattern = new RegExp(`\\b(${forbiddenIdentifiers.join("|")})\\b`, "g");

const usage = `Usage: node scripts/check-registry-writer.js [options]

Scans every .cs file under src/ (skipping bin/ and obj/) and fails when a file
other than src/Bwc.Registry/RegistryWriter*.cs references a mutating registry
API. Comments are stripped before matching.

Options:
  --root <repo>   Repository root to scan (default: the script's parent folder)
  --self-test     Create a temporary violating tree, require the scan to fail,
                  then clean up. Exits 0 when the guard works.
  --help          Show this help
`;

function parseArgs(argv) {
  const options = {
    root: repoRoot,
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
        throw new Error("Missing value for --root.");
      }

      options.root = path.resolve(value);
      index += 1;
      continue;
    }

    throw new Error(`Unknown argument: ${arg}`);
  }

  return options;
}

function listSourceFiles(directory, results) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);

    if (entry.isDirectory()) {
      if (entry.name === "bin" || entry.name === "obj") {
        continue;
      }

      listSourceFiles(fullPath, results);
      continue;
    }

    if (entry.isFile() && entry.name.endsWith(".cs")) {
      results.push(fullPath);
    }
  }
}

function isExcludedWriter(filePath, root) {
  const writerDirectory = path.resolve(root, "src", "Bwc.Registry");
  const fileDirectory = path.resolve(path.dirname(filePath));

  if (fileDirectory.toLowerCase() !== writerDirectory.toLowerCase()) {
    return false;
  }

  return /^RegistryWriter.*\.cs$/.test(path.basename(filePath));
}

function skipQuoted(source, start, quote, doubledQuoteEscapes) {
  let index = start + 1;

  while (index < source.length) {
    const character = source[index];

    if (doubledQuoteEscapes) {
      if (character === quote) {
        if (source[index + 1] === quote) {
          index += 2;
          continue;
        }

        return index + 1;
      }

      index += 1;
      continue;
    }

    if (character === "\\") {
      index += 2;
      continue;
    }

    if (character === quote) {
      return index + 1;
    }

    index += 1;
  }

  return index;
}

function skipRawString(source, start) {
  let quoteCount = 0;
  while (source[start + quoteCount] === '"') {
    quoteCount += 1;
  }

  let index = start + quoteCount;
  while (index < source.length) {
    if (source[index] !== '"') {
      index += 1;
      continue;
    }

    let runLength = 0;
    while (source[index + runLength] === '"') {
      runLength += 1;
    }

    if (runLength >= quoteCount) {
      return index + runLength;
    }

    index += runLength;
  }

  return index;
}

function skipStringAt(source, start) {
  if (source[start] === "@" && source[start + 1] === '"') {
    return skipQuoted(source, start + 1, '"', true);
  }

  if (source[start] === "$") {
    if (source[start + 1] === "@" && source[start + 2] === '"') {
      return skipQuoted(source, start + 2, '"', true);
    }

    if (source[start + 1] === '"') {
      let quoteCount = 0;
      while (source[start + 1 + quoteCount] === '"') {
        quoteCount += 1;
      }

      if (quoteCount >= 3) {
        return skipRawString(source, start + 1);
      }

      return skipQuoted(source, start + 1, '"', false);
    }
  }

  if (source[start] === '"') {
    let quoteCount = 0;
    while (source[start + quoteCount] === '"') {
      quoteCount += 1;
    }

    if (quoteCount >= 3) {
      return skipRawString(source, start);
    }

    return skipQuoted(source, start, '"', false);
  }

  return start;
}

function stripComments(source) {
  const output = source.split("");
  let index = 0;

  const blank = (start, end) => {
    for (let position = start; position < end; position += 1) {
      if (output[position] !== "\n" && output[position] !== "\r") {
        output[position] = " ";
      }
    }
  };

  while (index < source.length) {
    const character = source[index];

    if (character === "/" && source[index + 1] === "/") {
      const start = index;
      index += 2;
      while (index < source.length && source[index] !== "\n") {
        index += 1;
      }

      blank(start, index);
      continue;
    }

    if (character === "/" && source[index + 1] === "*") {
      const start = index;
      index += 2;
      while (index < source.length && !(source[index] === "*" && source[index + 1] === "/")) {
        index += 1;
      }

      index = index < source.length ? index + 2 : source.length;
      blank(start, index);
      continue;
    }

    if (character === "'") {
      index = skipQuoted(source, index, "'", false);
      continue;
    }

    if (character === '"' || character === "@" || character === "$") {
      const nextIndex = skipStringAt(source, index);
      if (nextIndex > index) {
        index = nextIndex;
        continue;
      }
    }

    index += 1;
  }

  return output.join("");
}

function scan(root) {
  const sourceRoot = path.join(root, "src");
  const files = [];

  listSourceFiles(sourceRoot, files);
  files.sort();

  const violations = [];

  for (const filePath of files) {
    if (isExcludedWriter(filePath, root)) {
      continue;
    }

    const source = readFileSync(filePath, "utf8");
    const stripped = stripComments(source);
    const lines = stripped.split("\n");

    for (let lineIndex = 0; lineIndex < lines.length; lineIndex += 1) {
      const line = lines[lineIndex].replace(/\r$/, "");
      const seen = new Set();

      for (const match of line.matchAll(identifierPattern)) {
        const identifier = match[1];
        if (seen.has(identifier)) {
          continue;
        }

        seen.add(identifier);
        violations.push({
          file: path.relative(root, filePath),
          line: lineIndex + 1,
          identifier,
        });
      }
    }
  }

  return violations;
}

function printViolations(violations) {
  for (const violation of violations) {
    console.error(`${violation.file}:${violation.line}: ${violation.identifier}`);
  }
}

function runSelfTest() {
  const temporaryRoot = mkdtempSync(path.join(tmpdir(), "bwc-registry-writer-"));

  try {
    const fixtureDirectory = path.join(temporaryRoot, "src", "Fixture");
    mkdirSync(fixtureDirectory, { recursive: true });

    writeFileSync(
      path.join(fixtureDirectory, "Violation.cs"),
      [
        "internal static class Violation",
        "{",
        "    internal static void Write() => RegDeleteKeyEx();",
        "}",
        "",
      ].join("\n"),
      "utf8");

    writeFileSync(
      path.join(fixtureDirectory, "Comments.cs"),
      [
        "// RegSetValueEx must be ignored in a line comment.",
        "/* RegCreateKeyEx must be ignored in a block comment.",
        "   RegCopyTree must also be ignored. */",
        "internal static class Comments",
        "{",
        "}",
        "",
      ].join("\n"),
      "utf8");

    const result = spawnSync(
      process.execPath,
      [scriptPath, "--root", temporaryRoot],
      { encoding: "utf8" });

    const output = `${result.stdout ?? ""}${result.stderr ?? ""}`;

    if (result.status !== 1) {
      throw new Error(`Self-test expected exit code 1 but got ${result.status}.\n${output}`);
    }

    if (!output.includes("Violation.cs") || !output.includes("RegDeleteKeyEx")) {
      throw new Error(`Self-test did not report the fixture violation.\n${output}`);
    }

    if (output.includes("Comments.cs")) {
      throw new Error(`Self-test reported a comment-only match.\n${output}`);
    }

    console.log("check-registry-writer self-test: fixture violation detected, comments ignored.");
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
    return runSelfTest();
  }

  const violations = scan(options.root);
  printViolations(violations);

  if (violations.length > 0) {
    console.error(`check-registry-writer: ${violations.length} forbidden reference(s) outside RegistryWriter.`);
    return 1;
  }

  console.log("check-registry-writer: no mutating registry APIs outside RegistryWriter.");
  return 0;
}

process.exitCode = main();
