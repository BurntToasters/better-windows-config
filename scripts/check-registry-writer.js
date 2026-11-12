#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const scriptDir = path.dirname(scriptPath);
const repoRoot = path.resolve(scriptDir, "..");

const scannedRoots = ["src", "tests"];
const writerDirectory = path.join("src", "Bwc.Registry");

// Native write entry points. Names match with or without the A, W, Ex, ExA, and ExW suffixes.
const nativeWritePatterns = [
  "Reg(?:SetValue|CreateKey|CreateKeyTransacted|DeleteKey|DeleteKeyTransacted|DeleteKeyValue|DeleteValue|DeleteTree|RenameKey|CopyTree|LoadKey|LoadAppKey|UnLoadKey|SaveKey|ReplaceKey|RestoreKey|SetKeySecurity|SetKeyValue|OverridePredefKey)(?:A|W|Ex|ExA|ExW)?",
  "(?:Nt|Zw)(?:SetValueKey|CreateKey|CreateKeyTransacted|DeleteKey|DeleteKeyTransacted|DeleteValueKey|RenameKey|LoadKey2|LoadKeyEx|LoadKey|UnloadKey2|UnloadKeyEx|UnloadKey|SaveKeyEx|SaveKey|SaveMergedKeys|ReplaceKey|RestoreKey|SetInformationKey)",
  "SH(?:SetValue|DeleteKey|DeleteValue|DeleteEmptyKey|CopyKey|RegSetUSValue|RegWriteUSValue|RegDeleteUSValue|RegCreateUSKey|RegOpenUSKey|RegDeleteEmptyUSKey)(?:A|W)?",
  // Security descriptor writes change who can read or write a key, so they count as writes too.
  "(?:TreeSetNamedSecurityInfo|SetNamedSecurityInfo|SetSecurityInfo)(?:A|W|Ex|ExA|ExW)?",
];

const nativeWriteIdentifier = new RegExp(`\\b(${nativeWritePatterns.join("|")})\\b`, "g");
const nativeWriteExactName = new RegExp(`^(?:${nativeWritePatterns.join("|")})$`);

// Managed registry writes. They are flagged only in files that reference a managed registry type,
// because the checker has no type information for a receiver.
const managedTypeReference =
  /\bRegistryKey\b|\bRegistryHive\b|\bMicrosoft\.Win32\.Registry\b|\bRegistry\.(?:CurrentUser|LocalMachine|Users|ClassesRoot|CurrentConfig|SetValue)\b/;
const managedWriteCall =
  /\.(?:SetValue|CreateSubKey|DeleteSubKey|DeleteSubKeyTree|DeleteValue|SetAccessControl)\s*\(/;
const managedWritableOpen = /\bOpenSubKey\s*\([^;]*?(?:,\s*true\s*\)|\bwritable\s*:\s*true)/;
const managedReadWriteRights = /\bReadWriteSubTree\b/;

// Process launches of reg.exe or regedit.exe.
const processLaunchReference = /\bProcess(?:StartInfo)?\b|\bShellExecute\b|\bCreateProcess\w*\b/;
const regExecutableLiteral = /^(?:.*[\\/])?(?:reg|regedit)(?:\.exe)?$/i;
const regWriteCommandLiteral = /\b(?:reg|regedit)(?:\.exe)?\s+(?:add|delete|copy|save|restore|load|unload|import|\/s)\b/i;

// Files that may call native write APIs. Each entry records why it is exempt.
const nativeWriteAllowlist = new Map([
  [
    "tests/Bwc.Registry.Tests/RegistryLinkFixture.cs",
    "Creates and removes symbolic link keys for the guard tests. The writer never creates links, so the test fixture is the only source of link inputs.",
  ],
]);

// Test and harness files that write managed registry values. Each write is confined to a run root
// under HKCU\Software\BWC-E2E by the file's own helper, and the file is listed so a new managed
// write must be reviewed.
const managedWriteAllowlist = new Map([
  [
    "tests/Bwc.E2E/Harness/SandboxRegistry.cs",
    "The E2E harness owns HKCU\\Software\\BWC-E2E\\<run-id> and validates relative paths before writing.",
  ],
  ["tests/Bwc.Registry.Tests/RegFileExporterTests.cs", "Seeds and removes sandbox run keys for export tests."],
  ["tests/Bwc.Registry.Tests/RegFileImporterTests.cs", "Seeds and removes sandbox run keys for import tests."],
  ["tests/Bwc.Registry.Tests/RegistryFinderTests.cs", "Seeds and removes sandbox run keys for find tests."],
  ["tests/Bwc.Registry.Tests/RegistryReaderTests.cs", "Seeds sandbox run keys with typed values for reader tests."],
  ["tests/Bwc.Registry.Tests/RegistrySecurityTests.cs", "Seeds and removes sandbox run keys for security tests."],
  ["tests/Bwc.Registry.Tests/RegistryStoreTests.cs", "Seeds and removes sandbox run keys for store tests."],
  ["tests/Bwc.Registry.Tests/RegistryWriterSandboxTests.cs", "Seeds and removes sandbox run keys for writer tests."],
  ["tests/Bwc.E2E/CommandLine/CommandLineTests.cs", "Seeds E2E run keys through the harness for command-line tests."],
  ["tests/Bwc.E2E/ContextMenus/ContextMenuTests.cs", "Seeds E2E run keys through the harness for context-menu tests."],
  ["tests/Bwc.E2E/Controls/ControlTestSupport.cs", "Seeds E2E run keys through the harness for control tests."],
  ["tests/Bwc.E2E/Editing/EditingTests.cs", "Seeds E2E run keys through the harness for editing tests."],
  ["tests/Bwc.E2E/Favorites/FavoritesTests.cs", "Seeds E2E run keys through the harness for favorites tests."],
  ["tests/Bwc.E2E/Find/FindTests.cs", "Seeds E2E run keys through the harness for find tests."],
  ["tests/Bwc.E2E/Hex/HexEditingTests.cs", "Seeds E2E run keys through the harness for hex editing tests."],
  ["tests/Bwc.E2E/Hives/HiveTests.cs", "Seeds E2E run keys through the harness for hive tests."],
  ["tests/Bwc.E2E/Remote/RemoteRegistryTests.cs", "Seeds E2E run keys through the harness for remote registry tests."],
  ["tests/Bwc.E2E/Hex/HexViewerTests.cs", "Seeds E2E run keys through the harness for hex viewer tests."],
  ["tests/Bwc.E2E/ImportExport/ImportExportTests.cs", "Seeds E2E run keys through the harness for import and export tests."],
  ["tests/Bwc.E2E/Permissions/PermissionsTests.cs", "Seeds E2E run keys through the harness for permissions tests."],
  ["tests/Bwc.E2E/Print/PrintTests.cs", "Seeds E2E run keys through the harness for print tests."],
]);

const usage = `Usage: node scripts/check-registry-writer.js [options]

Scans every .cs file and NativeMethods.txt under src/ and tests/ (skipping bin/ and obj/).
Fails when a file outside the registry writer (src/Bwc.Registry/RegistryWriter*.cs) names a
native write API, when a managed registry write appears outside the allowlist, or when a file
launches reg.exe or regedit.exe to write. Comments are stripped before matching. String literals
are matched only when their whole content is an API name or a reg command line.

Options:
  --root <repo>   Repository root to scan (default: the script's parent folder)
  --self-test     Create a temporary tree with one violation of each rule family and a clean
                  file, require the scan to report exactly the violations, then clean up.
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

function listFiles(directory, results) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);

    if (entry.isDirectory()) {
      if (entry.name === "bin" || entry.name === "obj") {
        continue;
      }

      listFiles(fullPath, results);
      continue;
    }

    if (entry.isFile() && (entry.name.endsWith(".cs") || entry.name === "NativeMethods.txt")) {
      results.push(fullPath);
    }
  }
}

function toRelative(root, filePath) {
  return path.relative(root, filePath).split(path.sep).join("/");
}

function isWriterFile(relative) {
  const directory = path.posix.dirname(relative);
  return directory === writerDirectory.split(path.sep).join("/") && /^RegistryWriter.*\.cs$/.test(path.posix.basename(relative));
}

function isWriterNativeMethods(relative) {
  return relative === `${writerDirectory.split(path.sep).join("/")}/NativeMethods.txt`;
}

// Walks C# source once. Returns the source with comments and literal contents blanked, plus each
// string literal with the offset where it starts.
function tokenize(source) {
  const code = source.split("");
  const literals = [];
  let index = 0;

  const blank = (start, end) => {
    for (let position = start; position < end; position += 1) {
      if (code[position] !== "\n" && code[position] !== "\r") {
        code[position] = " ";
      }
    }
  };

  while (index < source.length) {
    const character = source[index];

    if (character === "/" && source[index + 1] === "/") {
      const start = index;
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

      index = Math.min(index + 2, source.length);
      blank(start, index);
      continue;
    }

    const literal = readStringLiteral(source, index);
    if (literal !== null) {
      literals.push({ text: literal.content, offset: index });
      blank(index, literal.end);
      index = literal.end;
      continue;
    }

    if (character === "'") {
      const end = skipQuoted(source, index, "'");
      blank(index, end);
      index = end;
      continue;
    }

    index += 1;
  }

  return { code: code.join(""), literals };
}

function skipQuoted(source, start, quote) {
  let index = start + 1;

  while (index < source.length) {
    if (source[index] === "\\") {
      index += 2;
      continue;
    }

    if (source[index] === quote) {
      return index + 1;
    }

    index += 1;
  }

  return source.length;
}

// Returns { end, content } when a string literal starts at index, otherwise null.
function readStringLiteral(source, index) {
  let position = index;
  let verbatim = false;
  let interpolated = false;

  if (source[position] === "$") {
    interpolated = true;
    position += 1;
  }

  if (source[position] === "@") {
    verbatim = true;
    position += 1;
    if (!interpolated && source[position] === "$") {
      interpolated = true;
      position += 1;
    }
  }

  if (source[position] !== '"') {
    return null;
  }

  if (!interpolated && !verbatim) {
    let quoteCount = 0;
    while (source[position + quoteCount] === '"') {
      quoteCount += 1;
    }

    if (quoteCount >= 3) {
      return readRawString(source, position, quoteCount);
    }
  }

  const contentStart = position + 1;
  let cursor = contentStart;

  while (cursor < source.length) {
    const character = source[cursor];

    if (verbatim) {
      if (character === '"') {
        if (source[cursor + 1] === '"') {
          cursor += 2;
          continue;
        }

        return { end: cursor + 1, content: source.slice(contentStart, cursor).replace(/""/g, '"') };
      }

      cursor += 1;
      continue;
    }

    if (character === "\\") {
      cursor += 2;
      continue;
    }

    if (character === '"') {
      return { end: cursor + 1, content: source.slice(contentStart, cursor) };
    }

    cursor += 1;
  }

  return { end: source.length, content: source.slice(contentStart) };
}

function readRawString(source, start, quoteCount) {
  const contentStart = start + quoteCount;
  let cursor = contentStart;

  while (cursor < source.length) {
    if (source[cursor] !== '"') {
      cursor += 1;
      continue;
    }

    let runLength = 0;
    while (source[cursor + runLength] === '"') {
      runLength += 1;
    }

    if (runLength >= quoteCount) {
      return { end: cursor + runLength, content: source.slice(contentStart, cursor) };
    }

    cursor += runLength;
  }

  return { end: source.length, content: source.slice(contentStart) };
}

function lineOf(source, offset) {
  let line = 1;
  for (let position = 0; position < offset && position < source.length; position += 1) {
    if (source[position] === "\n") {
      line += 1;
    }
  }

  return line;
}

function addViolation(violations, relative, line, rule, detail) {
  violations.push({ file: relative, line, rule, detail });
}

function scanNativeMethods(relative, source, violations) {
  if (isWriterNativeMethods(relative)) {
    return;
  }

  const lines = source.split(/\r?\n/);
  lines.forEach((rawLine, index) => {
    const line = rawLine.trim();
    if (line === "" || line.startsWith("//")) {
      return;
    }

    if (nativeWriteExactName.test(line)) {
      addViolation(violations, relative, index + 1, "native-write-name", line);
    }
  });
}

function scanSource(root, relative, source, violations) {
  const { code, literals } = tokenize(source);
  const codeLines = code.split("\n");

  if (!isWriterFile(relative) && !nativeWriteAllowlist.has(relative)) {
    codeLines.forEach((line, lineIndex) => {
      const seen = new Set();
      for (const match of line.matchAll(nativeWriteIdentifier)) {
        if (seen.has(match[1])) {
          continue;
        }

        seen.add(match[1]);
        addViolation(violations, relative, lineIndex + 1, "native-write-name", match[1]);
      }
    });

    for (const literal of literals) {
      if (nativeWriteExactName.test(literal.text.trim())) {
        addViolation(violations, relative, lineOf(source, literal.offset), "native-write-name", literal.text.trim());
      }
    }
  }

  if (managedTypeReference.test(code) && !managedWriteAllowlist.has(relative) && !isWriterFile(relative)) {
    codeLines.forEach((line, lineIndex) => {
      if (managedWriteCall.test(line) || managedWritableOpen.test(line) || managedReadWriteRights.test(line)) {
        addViolation(violations, relative, lineIndex + 1, "managed-write", line.trim());
      }
    });
  }

  if (processLaunchReference.test(code)) {
    for (const literal of literals) {
      const text = literal.text.trim();
      if (regExecutableLiteral.test(text)) {
        addViolation(violations, relative, lineOf(source, literal.offset), "reg-process-launch", text);
      }
    }
  }

  for (const literal of literals) {
    if (regWriteCommandLiteral.test(literal.text)) {
      addViolation(violations, relative, lineOf(source, literal.offset), "reg-write-command", literal.text.trim());
    }
  }
}

function scan(root) {
  const files = [];

  for (const scannedRoot of scannedRoots) {
    const directory = path.join(root, scannedRoot);
    try {
      listFiles(directory, files);
    } catch (error) {
      if (error.code !== "ENOENT") {
        throw error;
      }
    }
  }

  files.sort();
  const violations = [];

  for (const filePath of files) {
    const relative = toRelative(root, filePath);
    const source = readFileSync(filePath, "utf8");

    if (path.basename(filePath) === "NativeMethods.txt") {
      scanNativeMethods(relative, source, violations);
      continue;
    }

    scanSource(root, relative, source, violations);
  }

  return violations;
}

function printViolations(violations) {
  for (const violation of violations) {
    console.error(`${violation.file}:${violation.line}: ${violation.rule}: ${violation.detail}`);
  }
}

function runSelfTest() {
  const temporaryRoot = mkdtempSync(path.join(tmpdir(), "bwc-registry-writer-"));

  try {
    const fixtures = {
      "src/Fixture/NativeWriteSuffixes.cs": [
        "internal static class NativeWriteSuffixes",
        "{",
        "    internal static void Write() { RegSetValueExW(); RegCreateKey(); }",
        "    internal static void Nt() => NtSetValueKey();",
        "    internal static void Shell() => SHSetValueW();",
        "}",
      ],
      "src/Fixture/EntryPointLiteral.cs": [
        "using System.Runtime.InteropServices;",
        "internal static class EntryPointLiteral",
        "{",
        '    [DllImport("advapi32.dll", EntryPoint = "RegDeleteKeyExA")]',
        "    internal static extern int Delete();",
        "}",
      ],
      "src/Fixture/NativeMethods.txt": ["RegDeleteValueW", "RegSetKeySecurity"],
      "src/Fixture/SecurityInfo.cs": [
        "internal static class SecurityInfo",
        "{",
        "    internal static void Set() { SetSecurityInfo(); SetNamedSecurityInfoW(); }",
        "    internal static void Tree() => TreeSetNamedSecurityInfoW();",
        "}",
      ],
      "src/Fixture/ManagedWrite.cs": [
        "using Microsoft.Win32;",
        "internal static class ManagedWrite",
        "{",
        "    internal static void Write(RegistryKey key) => key.SetValue(\"A\", 1);",
        "    internal static void Open() => Registry.CurrentUser.OpenSubKey(\"x\", writable: true);",
        "}",
      ],
      "src/Fixture/ProcessLaunch.cs": [
        "using System.Diagnostics;",
        "internal static class ProcessLaunch",
        "{",
        '    internal static void Query() => Process.Start(new ProcessStartInfo("reg.exe", "query HKCU"));',
        '    internal static void Write() => Process.Start(new ProcessStartInfo("cmd.exe", "/c reg add HKCU\\\\x"));',
        "}",
      ],
      "src/Fixture/CommandLine.cs": [
        "internal static class CommandLine",
        "{",
        '    internal const string Command = "cmd /c reg add HKCU\\\\Software\\\\x /v a";',
        "}",
      ],
      "src/Bwc.Registry/RegistryWriter.Fixture.cs": [
        "namespace Bwc.Registry;",
        "internal static partial class RegistryWriterFixture",
        "{",
        "    internal static void Write() => RegSetValueEx();",
        "}",
      ],
      "src/Fixture/Clean.cs": [
        "// RegSetValueEx and NtSetValueKey may appear in comments.",
        "/* RegCreateKey and RegistryKey.SetValue(...) in a block comment. */",
        "internal static class Clean",
        "{",
        '    internal const string Message = "RegSaveKey did not create the hive file.";',
        '    internal const string Url = "https://example.test/RegDeleteKey";',
        "    internal static string Read() => Registry.Read();",
        "}",
      ],
    };

    for (const [relative, lines] of Object.entries(fixtures)) {
      const filePath = path.join(temporaryRoot, ...relative.split("/"));
      mkdirSync(path.dirname(filePath), { recursive: true });
      writeFileSync(filePath, lines.join("\n") + "\n", "utf8");
    }

    const result = spawnSync(process.execPath, [scriptPath, "--root", temporaryRoot], { encoding: "utf8" });
    const output = `${result.stdout ?? ""}${result.stderr ?? ""}`;

    if (result.status !== 1) {
      throw new Error(`Self-test expected exit code 1 but got ${result.status}.\n${output}`);
    }

    const expectedFragments = [
      "src/Fixture/NativeWriteSuffixes.cs:3: native-write-name: RegSetValueExW",
      "src/Fixture/NativeWriteSuffixes.cs:3: native-write-name: RegCreateKey",
      "src/Fixture/NativeWriteSuffixes.cs:4: native-write-name: NtSetValueKey",
      "src/Fixture/NativeWriteSuffixes.cs:5: native-write-name: SHSetValueW",
      "src/Fixture/EntryPointLiteral.cs:4: native-write-name: RegDeleteKeyExA",
      "src/Fixture/NativeMethods.txt:1: native-write-name: RegDeleteValueW",
      "src/Fixture/NativeMethods.txt:2: native-write-name: RegSetKeySecurity",
      "src/Fixture/SecurityInfo.cs:3: native-write-name: SetSecurityInfo",
      "src/Fixture/SecurityInfo.cs:3: native-write-name: SetNamedSecurityInfoW",
      "src/Fixture/SecurityInfo.cs:4: native-write-name: TreeSetNamedSecurityInfoW",
      "src/Fixture/ManagedWrite.cs:4: managed-write",
      "src/Fixture/ManagedWrite.cs:5: managed-write",
      "src/Fixture/ProcessLaunch.cs:4: reg-process-launch: reg.exe",
      "src/Fixture/ProcessLaunch.cs:5: reg-write-command",
      "src/Fixture/CommandLine.cs:3: reg-write-command",
    ];

    for (const fragment of expectedFragments) {
      if (!output.includes(fragment)) {
        throw new Error(`Self-test did not report the fixture violation "${fragment}".\n${output}`);
      }
    }

    const forbiddenFragments = [
      "src/Fixture/Clean.cs",
      "src/Bwc.Registry/RegistryWriter.Fixture.cs",
    ];

    for (const fragment of forbiddenFragments) {
      if (output.includes(fragment)) {
        throw new Error(`Self-test reported a file that must pass: ${fragment}.\n${output}`);
      }
    }

    console.log(
      `check-registry-writer self-test: ${expectedFragments.length} fixture violations detected across four rule families; comments, prose, and the writer file passed.`,
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
    return runSelfTest();
  }

  const violations = scan(options.root);
  printViolations(violations);

  if (violations.length > 0) {
    console.error(`check-registry-writer: ${violations.length} violation(s) outside the registry writer and its allowlists.`);
    return 1;
  }

  console.log("check-registry-writer: no native write APIs, managed writes, or reg.exe write launches outside the registry writer and its allowlists.");
  return 0;
}

process.exitCode = main();
