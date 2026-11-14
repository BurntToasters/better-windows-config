#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const scriptDir = path.dirname(scriptPath);
const repoRoot = path.resolve(scriptDir, "..");

// tools/ and scripts/ hold PowerShell and Node helpers that can write the registry without a C# file,
// so they are scanned with the C# sources.
const scannedRoots = ["src", "tests", "tools", "scripts"];
const writerDirectory = path.join("src", "Bwc.Registry");
const skippedDirectories = new Set(["bin", "obj", "node_modules"]);
// Script files, mapped to the tokenizer that reads them.
const scriptExtensions = new Map([
  [".ps1", "ps1"],
  [".js", "js"],
  [".mjs", "js"],
  [".cjs", "js"],
]);

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
// A SetValue or CreateSubKey call on a receiver the checker cannot type. Files that name no registry type are
// still checked for it, because the call can write a key. Files that name one use managedWriteCall instead.
const unnamedRegistryCall = /\.(?:SetValue|CreateSubKey)\s*\(/;

// Process launches of reg.exe or regedit.exe.
const processLaunchReference = /\bProcess(?:StartInfo)?\b|\bShellExecute\b|\bCreateProcess\w*\b/;
const regExecutableLiteral = /^(?:.*[\\/])?(?:reg|regedit)(?:\.exe)?$/i;
const regWriteCommandLiteral = /\b(?:reg|regedit)(?:\.exe)?\s+(?:add|delete|copy|save|restore|load|unload|import|\/s)\b/i;

// Script rules. The reg command is read from code, where a PowerShell call is code and its arguments are not.
const regWriteCommandCode = /\breg(?:\.exe)?\s+(?:add|delete|copy|save|restore|load|unload|import)\b/i;
const registryPathText = /\bHK(?:CU|LM|CR|U|CC)\b|\bHKEY_[A-Z_]+\b|\bRegistry::/i;
const powerShellRegistryWrite = /\b(?:Set|New|Remove|Clear|Rename|Copy|Move)-(?:ItemProperty|Item)\b|\bSet-Acl\b/i;
const scriptProcessLaunch =
  /\bchild_process\b|\bspawn(?:Sync)?\b|\bexec(?:File|Sync|FileSync)?\s*\(|\bStart-Process\b|\bProcess(?:StartInfo)?\b|\bShellExecute\b/i;

// Files that may call native write APIs. Each entry records why it is exempt.
const nativeWriteAllowlist = new Map([
  [
    "tests/Bwc.Registry.Tests/RegistryLinkFixture.cs",
    "Creates and removes symbolic link keys for the guard tests. The writer never creates links, so the test fixture is the only source of link inputs.",
  ],
  [
    "tools/Bwc.Oracle.Capture/RegistryGuard.cs",
    "Writes only HKCU/Software/BWC-Oracle and the Applets/Regedit key, through the guarded native call.",
  ],
  [
    "tools/Bwc.Oracle.Capture/RegistryNative.cs",
    "Reads raw values with RegQueryValueEx and restores them only under BWC-Oracle and the Applets/Regedit key.",
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
  [
    "tests/Bwc.E2E/Harness/AppletState.cs",
    "Restores the classic regedit applet key from its own snapshot. Writes go only under Applets/Regedit.",
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
  [
    "tests/Bwc.E2E/Installer/InstallerRun.cs",
    "Deletes only its own run key, Software/BWC-E2E/<run-id>, after the installer test finishes.",
  ],
  [
    "tools/Bwc.Oracle.Capture/RegistryGuard.cs",
    "Writes only HKCU/Software/BWC-Oracle and the Applets/Regedit key, which OracleSession snapshots and restores.",
  ],
  ["tests/Bwc.E2E/Print/PrintTests.cs", "Seeds E2E run keys through the harness for print tests."],
]);

// Seeds made through the E2E harness's SandboxRegistry, which accepts only paths under the run root.
const sandboxedSeedReason = "Seeds through SandboxRegistry, which confines every write to Software/BWC-E2E/<run-id>.";

// C# files that name no registry type but call SetValue or CreateSubKey. Each entry says what the receiver is.
const unnamedCallAllowlist = new Map([
  [
    "src/Bwc.Regedit/Controllers/ValueCommands.cs",
    "Sets a hex dialog model in memory (HexViewerDialog and HexEditorDialog), and writes through RegistryStore, the guarded writer.",
  ],
  [
    "src/Bwc.Registry/RegFile/RegFileImporter.cs",
    "Writes imported values through RegistryStore, the product writer that RegistryWriter guards.",
  ],
  [
    "src/Bwc.Registry/RegistryStore.cs",
    "The product store. It calls RegistryWriter, the only native writer, and the writer checks every path.",
  ],
  ["tests/Bwc.E2E/ContextMenus/ContextMenuTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/Favorites/FavoritesFixesTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/Hex/HexEditingFixesTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/List/ListRenameFocusTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/Dialogs/BinaryViewerTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/Dialogs/ClassicDialogTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/HarnessTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/Hex/HexViewerTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/Icons/IconTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/List/LargeListSelectionTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/Navigation/AddressBarTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/Navigation/NavigationTests.cs", sandboxedSeedReason],
  ["tests/Bwc.E2E/Tree/TreeBehaviorTests.cs", sandboxedSeedReason],
  [
    "tests/Bwc.Registry.Tests/RegistryWriterGuardTests.cs",
    "Calls RegistryWriter under test with a policy rooted at the run's own Software/BWC-E2E/<run-id> key.",
  ],
  [
    "tests/Bwc.Registry.Tests/RegistryWriterHardeningTests.cs",
    "Calls RegistryWriter under test with a policy rooted at the run's own Software/BWC-E2E/<run-id> key.",
  ],
]);

// C# files that start classic or Hub processes whose literal names reg or regedit. Each entry says why it is safe.
const launchAllowlist = new Map([
  [
    "tests/Bwc.E2E/Installer/InstallerUpdateTests.cs",
    "Starts classic regedit to look at the installed state. Its writes go through the harness, not a reg command.",
  ],
  [
    "tools/Bwc.Oracle.Capture/MetricsCommand.cs",
    "Starts classic regedit for the oracle capture, under the applet snapshot and restore of OracleSession.",
  ],
  [
    "tools/Bwc.Oracle.Capture/RegeditProcess.cs",
    "Starts classic regedit for the oracle capture, under the applet snapshot and restore of OracleSession.",
  ],
]);

// C# files whose reg command literal is text, not a command.
const commandLiteralAllowlist = new Map([
  [
    "tools/Bwc.Oracle.Capture/Program.cs",
    "Prints a help line that names a reg import case. It runs no command.",
  ],
]);

// Script files that write the registry or call a registry-writing command. Each entry records why.
const scriptWriteAllowlist = new Map([
  [
    "scripts/check-registry-writer.js",
    "Defines the patterns this checker looks for. Its own literals are the patterns, not registry writes.",
  ],
  [
    "scripts/run-app.js",
    "Starts the built Hub or Registry Editor for a developer run. It passes the user's arguments and no reg command.",
  ],
  [
    "tests/Bwc.E2E/Theme/classic-selection-oracle.ps1",
    "Starts its own regedit.exe and restores the Applets/Regedit key from its own reg export. Writes only that key.",
  ],
]);

const usage = `Usage: node scripts/check-registry-writer.js [options]

Scans every .cs, NativeMethods.txt, .ps1, .js, .mjs, and .cjs file under src/, tests/, tools/, and scripts/
(skipping bin/, obj/, and node_modules/). Fails when a file outside the registry writer (src/Bwc.Registry/
RegistryWriter*.cs) names a native write API, when a managed registry write appears outside the allowlist,
when a SetValue or CreateSubKey call sits in a file that names no registry type, when a script runs a reg
write command or a PowerShell registry cmdlet on an HKCU/HKLM path, or when a file launches reg.exe or
regedit.exe to write. Comments are stripped before matching. String literals are matched only when their
whole content is an API name or a reg command line, except in scripts, where the code text is matched.

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
      if (skippedDirectories.has(entry.name)) {
        continue;
      }

      listFiles(fullPath, results);
      continue;
    }

    if (!entry.isFile()) {
      continue;
    }

    const extension = path.extname(entry.name).toLowerCase();
    if (entry.name.endsWith(".cs") || entry.name === "NativeMethods.txt" || scriptExtensions.has(extension)) {
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

// Walks a script. kind is "js" or "ps1". Returns the code with comments and string contents blanked (code),
// the text with comments blanked and strings kept (withStrings), and each string literal with its offset.
function tokenizeScript(source, kind) {
  const code = source.split("");
  const withStrings = source.split("");
  const literals = [];
  const blank = (chars, start, end) => {
    for (let position = start; position < end; position += 1) {
      if (chars[position] !== "\n" && chars[position] !== "\r") {
        chars[position] = " ";
      }
    }
  };

  let index = 0;
  while (index < source.length) {
    const character = source[index];
    const next = source[index + 1];
    const isJs = kind === "js";

    if (isJs ? character === "/" && next === "/" : character === "#") {
      const start = index;
      while (index < source.length && source[index] !== "\n") {
        index += 1;
      }

      blank(code, start, index);
      blank(withStrings, start, index);
      continue;
    }

    if (isJs ? character === "/" && next === "*" : character === "<" && next === "#") {
      const start = index;
      const close = isJs ? "*/" : "#>";
      index += 2;
      while (index < source.length && !source.startsWith(close, index)) {
        index += 1;
      }

      index = Math.min(index + close.length, source.length);
      blank(code, start, index);
      blank(withStrings, start, index);
      continue;
    }

    const quoted = character === "'" || character === '"' || (isJs && character === "`");
    if (quoted) {
      // JavaScript escapes with a backslash. PowerShell escapes with a backtick in double quotes, and doubles a single quote.
      const escape = isJs ? "\\" : character === '"' ? "`" : null;
      const end = skipScriptString(source, index, character, escape);
      literals.push({ text: source.slice(index + 1, Math.max(index + 1, end - 1)), offset: index });
      blank(code, index, end);
      index = end;
      continue;
    }

    index += 1;
  }

  return { code: code.join(""), withStrings: withStrings.join(""), literals };
}

// Returns the index just past the string that starts at start. escape is the escape character, or null.
function skipScriptString(source, start, quote, escape) {
  let index = start + 1;

  while (index < source.length) {
    const character = source[index];

    if (escape !== null && character === escape) {
      index += 2;
      continue;
    }

    if (character === quote) {
      if (escape === null && source[index + 1] === quote) {
        index += 2;
        continue;
      }

      return index + 1;
    }

    index += 1;
  }

  return source.length;
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
  } else if (!managedTypeReference.test(code) && !isWriterFile(relative) && !unnamedCallAllowlist.has(relative)) {
    codeLines.forEach((line, lineIndex) => {
      if (unnamedRegistryCall.test(line)) {
        addViolation(violations, relative, lineIndex + 1, "unnamed-registry-call", line.trim());
      }
    });
  }

  if (processLaunchReference.test(code)) {
    for (const literal of literals) {
      const text = literal.text.trim();
      if (regExecutableLiteral.test(text) && !launchAllowlist.has(relative)) {
        addViolation(violations, relative, lineOf(source, literal.offset), "reg-process-launch", text);
      }
    }
  }

  for (const literal of literals) {
    if (regWriteCommandLiteral.test(literal.text) && !commandLiteralAllowlist.has(relative)) {
      addViolation(violations, relative, lineOf(source, literal.offset), "reg-write-command", literal.text.trim());
    }
  }
}

function scanScript(relative, source, kind, violations) {
  if (scriptWriteAllowlist.has(relative)) {
    return;
  }

  const { code, withStrings, literals } = tokenizeScript(source, kind);
  const codeLines = code.split("\n");
  const textLines = withStrings.split("\n");

  // Native API names count wherever they are written, including a P/Invoke name in a string.
  textLines.forEach((line, lineIndex) => {
    const seen = new Set();
    for (const match of line.matchAll(nativeWriteIdentifier)) {
      if (seen.has(match[1])) {
        continue;
      }

      seen.add(match[1]);
      addViolation(violations, relative, lineIndex + 1, "native-write-name", match[1]);
    }
  });

  codeLines.forEach((line, lineIndex) => {
    if (regWriteCommandCode.test(line)) {
      addViolation(violations, relative, lineIndex + 1, "reg-write-command", line.trim());
    }

    if (unnamedRegistryCall.test(line)) {
      addViolation(violations, relative, lineIndex + 1, "unnamed-registry-call", line.trim());
    }

    if (powerShellRegistryWrite.test(line) && registryPathText.test(textLines[lineIndex] ?? "")) {
      addViolation(violations, relative, lineIndex + 1, "ps-registry-write", line.trim());
    }
  });

  if (scriptProcessLaunch.test(code)) {
    for (const literal of literals) {
      const text = literal.text.trim();
      if (regExecutableLiteral.test(text)) {
        addViolation(violations, relative, lineOf(source, literal.offset), "reg-process-launch", text);
      }
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

    const kind = scriptExtensions.get(path.extname(filePath).toLowerCase());
    if (kind !== undefined) {
      scanScript(relative, source, kind, violations);
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
      "tools/Fixture/Unnamed.cs": [
        "internal static class Unnamed",
        "{",
        "    internal static void Set(object target) => Set(target);",
        "    internal static void Call(dynamic target) => target.SetValue(1);",
        "}",
      ],
      "scripts/Fixture/Registry.ps1": [
        "# reg add HKCU is only a comment",
        "& reg.exe add 'HKCU:/Software/Fixture' /v a /d 1",
        "Set-ItemProperty -Path 'HKCU:/Software/Fixture' -Name a -Value 1",
        "Set-ItemProperty -Path (Join-Path $env:TEMP 'fixture') -Name a -Value 1",
        "$key.SetValue('a', 1)",
      ],
      "scripts/Fixture/Clean.ps1": [
        "# Set-ItemProperty HKCU:/x is a comment",
        "Write-Output 'Set-ItemProperty HKCU:/x is text'",
        "Set-Content -Path (Join-Path $env:TEMP 'x.txt') -Value 'a'",
      ],
      "scripts/Fixture/Launch.js": [
        "import { execFileSync } from 'node:child_process';",
        "execFileSync('reg', ['add', 'HKCU/Software/Fixture']);",
      ],
      "scripts/Fixture/Names.js": [
        "// RegSetValueEx in a comment is fine.",
        "const api = 'RegSetValueExW';",
      ],
      "scripts/Fixture/Clean.js": [
        "// reg add HKCU is only a comment",
        "const text = 'reg add HKCU is text';",
        "module.exports = { text };",
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
      "tools/Fixture/Unnamed.cs:4: unnamed-registry-call",
      "scripts/Fixture/Registry.ps1:2: reg-write-command",
      "scripts/Fixture/Registry.ps1:3: ps-registry-write",
      "scripts/Fixture/Registry.ps1:5: unnamed-registry-call",
      "scripts/Fixture/Launch.js:2: reg-process-launch: reg",
      "scripts/Fixture/Names.js:2: native-write-name: RegSetValueExW",
    ];

    for (const fragment of expectedFragments) {
      if (!output.includes(fragment)) {
        throw new Error(`Self-test did not report the fixture violation "${fragment}".\n${output}`);
      }
    }

    const forbiddenFragments = [
      "src/Fixture/Clean.cs",
      "src/Bwc.Registry/RegistryWriter.Fixture.cs",
      "scripts/Fixture/Clean.ps1",
      "scripts/Fixture/Clean.js",
      "scripts/Fixture/Registry.ps1:4",
    ];

    for (const fragment of forbiddenFragments) {
      if (output.includes(fragment)) {
        throw new Error(`Self-test reported a file that must pass: ${fragment}.\n${output}`);
      }
    }

    console.log(
      `check-registry-writer self-test: ${expectedFragments.length} fixture violations detected across C#, script, and tool files; comments, prose, strings, and the writer file passed.`,
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

  console.log("check-registry-writer: no native write APIs, managed writes, or reg write commands outside the registry writer and its allowlists.");
  return 0;
}

process.exitCode = main();
