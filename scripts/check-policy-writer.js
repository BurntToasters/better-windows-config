#!/usr/bin/env node
// Static guard for the local group policy writer (release 0.2.0, piece G0).
//
// The machine's real policy lives in %SystemRoot%\System32\GroupPolicy. The template folders are
// PolicyDefinitions (system) and %ProgramData%\BetterWindowsConfig\PolicyDefinitions (the BWC import store).
// Nothing may write them except these named files, which later pieces create under these exact names:
//
//   src/Bwc.Gpo/Store/GpoFileWriter.cs          the only GPO folder writer (atomic temp-then-replace, backup)
//   src/Bwc.Policy/Store/TemplateStoreWriter.cs the only import store writer
//   src/Bwc.Gpo/Refresh/PolicyRefresh.cs        the only file that names RefreshPolicy* and the policy COM objects
//
// Rules (docs/failure-modes/policy-writer.md has every case and how each is caught):
//   protected-write         a file-write API in src/Bwc.Gpo, Bwc.Policy, Bwc.Gpedit, Bwc.Gpedit.Editor, or
//                           Bwc.Gpedit.App outside the two writer files
//   protected-native-name   a native file-write name there (code or NativeMethods.txt) outside the writers
//   policy-path-write       a file-write API in a file that names GroupPolicy, GroupPolicyUsers, PolicyDefinitions,
//                           Registry.pol, gpt.ini, or comment.cmtx, unless allowlisted with a reason
//   refresh-name            RefreshPolicy, RefreshPolicyEx, ForceSyncFgPolicy, the critical section calls, or
//                           IGroupPolicyObject anywhere but PolicyRefresh.cs
//   policy-process-launch   a launch of gpupdate, secedit, gpedit.msc, or mmc outside tools/Bwc.Oracle.Capture/Gpedit
//   writer-scope            a writer that names the other writer's folder
//
// Modeled on scripts/check-registry-writer.js: comments are stripped before matching, every allowlist entry
// records a reason, and --self-test proves each rule against a temporary tree.
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const scriptDir = path.dirname(scriptPath);
const repoRoot = path.resolve(scriptDir, "..");

const scannedRoots = ["src", "tests", "tools", "scripts"];
const skippedDirectories = new Set(["bin", "obj", "node_modules"]);
const scriptExtensions = new Map([
  [".ps1", "ps1"],
  [".js", "js"],
  [".mjs", "js"],
  [".cjs", "js"],
]);

// Projects where no file may be written, whatever the path. The two writers are the only exceptions.
const protectedPrefixes = [
  "src/Bwc.Gpo/",
  "src/Bwc.Policy/",
  "src/Bwc.Gpedit/",
  "src/Bwc.Gpedit.Editor/",
  "src/Bwc.Gpedit.App/",
];
const gpoWriterFile = "src/Bwc.Gpo/Store/GpoFileWriter.cs";
const templateWriterFile = "src/Bwc.Policy/Store/TemplateStoreWriter.cs";
const refreshFile = "src/Bwc.Gpo/Refresh/PolicyRefresh.cs";
// A NativeMethods.txt beside a writer may list the native calls that writer needs.
const writerNativeMethods = new Set(["src/Bwc.Gpo/Store/NativeMethods.txt", "src/Bwc.Policy/Store/NativeMethods.txt"]);
const refreshNativeMethods = "src/Bwc.Gpo/Refresh/NativeMethods.txt";
const launchAllowedPrefix = "tools/Bwc.Oracle.Capture/Gpedit/";

// Names a policy folder or file. GroupPolicy counts only as a whole word with no dot beside it, so GroupPolicyObject
// and the assembly name Microsoft.GroupPolicy.AdmTmplEditor do not match. PolicyDefinitions is case sensitive,
// because the ADMX root element is policyDefinitions.
const gpoFolderLiteral = /(?<![A-Za-z0-9.])GroupPolicy(?:Users)?(?![A-Za-z0-9.])|Registry\.pol|gpt\.ini|comment\.cmtx/i;
const policyDefinitionsLiteral = /PolicyDefinitions/;
const policyPathPatterns = [gpoFolderLiteral, policyDefinitionsLiteral];

// Returns the earliest policy path name in text as a match, or null.
function findPolicyPath(text) {
  let best = null;
  for (const pattern of policyPathPatterns) {
    const match = text.match(pattern);
    if (match !== null && (best === null || match.index < best.index)) {
      best = match;
    }
  }

  return best;
}

// Managed write members of File and Directory. PowerShell reaches them as [System.IO.File]::Member.
const staticWriteMembers = [
  "WriteAllText",
  "WriteAllBytes",
  "WriteAllLines",
  "WriteAllTextAsync",
  "WriteAllBytesAsync",
  "WriteAllLinesAsync",
  "AppendAllText",
  "AppendAllLines",
  "AppendAllTextAsync",
  "AppendAllLinesAsync",
  "AppendText",
  "Create",
  "CreateText",
  "CreateDirectory",
  "CreateSymbolicLink",
  "CreateTempSubdirectory",
  "Delete",
  "Move",
  "Copy",
  "Replace",
  "Encrypt",
  "Decrypt",
  "OpenWrite",
  "SetAttributes",
  "SetUnixFileMode",
  "SetCreationTime",
  "SetCreationTimeUtc",
  "SetLastWriteTime",
  "SetLastWriteTimeUtc",
  "SetLastAccessTime",
  "SetLastAccessTimeUtc",
  "SetAccessControl",
];
const staticFileWrite = new RegExp(`\\b(File|Directory)\\]?\\s*(?:\\.|::)\\s*(${staticWriteMembers.join("|")})\\s*\\(`, "g");
// FileInfo and DirectoryInfo members. Delete, Create, Replace, MoveTo, CopyTo, and Open are common names on other
// types (XmlReader.Create, string.Replace), so they count only on a variable the file declares as FileInfo,
// DirectoryInfo, or FileSystemInfo, or on a new FileInfo(...) chain.
const fileInfoDeclaration = /\b(?:FileInfo|DirectoryInfo|FileSystemInfo)\??\s+(\w+)\b|\b(\w+)\s*=\s*new\s+(?:FileInfo|DirectoryInfo)\b/g;
const fileInfoChain = /\bnew\s+(?:FileInfo|DirectoryInfo)\s*\(/g;
const instanceTypedMembers = "Delete|Create|Replace|MoveTo|CopyTo|Open";
const instanceAlwaysWrite = /\.\s*(CreateText|AppendText|OpenWrite|CreateSubdirectory|SetAccessControl)\s*\(/g;
const fileModeWrite = /\bFileMode\s*\.\s*(Create|CreateNew|OpenOrCreate|Append|Truncate)\b/g;
const fileAccessWrite = /\bFileAccess\s*\.\s*(Write|ReadWrite)\b/g;
const deleteOnClose = /\bFileOptions\s*\.\s*DeleteOnClose\b/g;
// Opens that default to read and write access unless the call says FileAccess.
const newFileStream = /\bnew\s+FileStream\s*\(/g;
const fileOpen = /\bFile\s*\.\s*Open\s*\(/g;
// Writers that take a path or a stream. They count unless the argument is clearly a stream, so a writer over a
// MemoryStream, StringBuilder, or TextWriter passes and a writer over any other name is reviewed.
const pathWriterNew = /\bnew\s+(StreamWriter|XmlTextWriter)\s*\(/g;
const xmlWriterCreate = /\bXmlWriter\s*\.\s*Create\s*\(/g;
const saveCall = /\.\s*Save\s*\(/g;
const streamLikeArgument = /stream|memory|buffer|writer|builder|\bms\b|\bsb\b/i;

// Native file-write entry points. A name matches with or without the A, W, Ex, 2, and Transacted forms.
const nativeFileWritePatterns = [
  "CreateFile(?:Transacted)?(?:2|A|W)?",
  "(?:Nt|Zw)(?:CreateFile|OpenFile|WriteFile|SetInformationFile|DeleteFile)",
  "MoveFile(?:Ex|WithProgress|Transacted)?(?:A|W)?",
  "ReplaceFile(?:A|W)?",
  "DeleteFile(?:Transacted)?(?:A|W)?",
  "RemoveDirectory(?:Transacted)?(?:A|W)?",
  "CreateDirectory(?:Ex|Transacted)?(?:A|W)?",
  "CopyFile(?:Ex|2|Transacted)?(?:A|W)?",
  "CreateHardLink(?:Transacted)?(?:A|W)?",
  "CreateSymbolicLink(?:Transacted)?(?:A|W)?",
  "SetFileAttributes(?:Transacted)?(?:A|W)?",
  "SetEndOfFile",
  "SetFileTime",
  "SetFileValidData",
  "SetFileInformationByHandle",
  "WriteFile(?:Ex|Gather)?",
  "SHFileOperation(?:A|W)?",
  "SHCreateDirectory(?:Ex)?(?:A|W)?",
  "IFileOperation",
];
const nativeNameGroup = nativeFileWritePatterns.join("|");
// A name used as a member of an unknown receiver (Directory.CreateDirectory) is not a P/Invoke. The generated
// PInvoke class and the common wrapper class names are.
const nativeFileWriteIdentifier = new RegExp(
  `(?:(?<![.\\w])|(?<=\\b(?:PInvoke|NativeMethods|Native|Kernel32|Ntdll|Shell32)\\s*\\.\\s*))(${nativeNameGroup})\\b`,
  "g",
);
const nativeFileWriteExactName = new RegExp(`^(?:${nativeNameGroup})$`);

// Policy refresh and the policy COM objects. Only PolicyRefresh.cs may name them.
const refreshNamePattern =
  "RefreshPolicy(?:Ex)?|ForceSyncFgPolicy|EnterCriticalPolicySection|LeaveCriticalPolicySection|IGroupPolicyObject|CLSID_GroupPolicyObject";
const refreshNameIdentifier = new RegExp(`\\b(${refreshNamePattern})\\b`, "g");
const refreshNameInLine = new RegExp(`\\b(?:${refreshNamePattern})\\b`);
// CLSID_GroupPolicyObject and IID_IGroupPolicyObject, for a COM import that never spells the name.
const groupPolicyGuid = /\bEA50272[23]-A23D-11D1-A7D3-0000F87571E3\b/i;

// Process launches of the tools that apply or edit policy.
const processLaunchReference = /\bProcess(?:StartInfo)?\b|\bShellExecute\b|\bCreateProcess\w*\b/;
const scriptProcessLaunch =
  /\bchild_process\b|\bspawn(?:Sync)?\b|\bexec(?:File|Sync|FileSync)?\s*\(|\bStart-Process\b|\bProcess(?:StartInfo)?\b|\bShellExecute\b/i;
const policyToolExecutable = /^(?:.*[\\/])?(?:gpupdate|secedit|mmc)(?:\.exe)?$|^(?:.*[\\/])?gpedit\.msc$/i;
const policyToolCommandLine =
  /^\s*(?:(?:cmd|powershell|pwsh)(?:\.exe)?\s+)?(?:(?:\/[ck]|-c(?:ommand)?)\s+)?(?:\S*[\\/])?(?:(?:gpupdate|secedit|mmc)(?:\.exe)?|gpedit\.msc)(?:\s|$)/i;
const policyToolCommandWord = /\b(?:gpupdate|secedit|mmc)(?:\.exe)?\b|\bgpedit\.msc\b/i;

// File commands handed to a shell. They count in a file that starts a process.
const shellFileCommandLiteral =
  /^\s*(?:(?:cmd|powershell|pwsh)(?:\.exe)?\s+)?(?:(?:\/[ck]|-c(?:ommand)?)\s+)?(copy|xcopy|robocopy|move|del|erase|rmdir|rd|mkdir|md|ren|rename|icacls|takeown|attrib|mklink)\s/i;

// Script writes.
const powerShellFileCmdlet =
  /\b(?:Set-Content|Add-Content|Out-File|Copy-Item|Move-Item|Remove-Item|New-Item|Rename-Item|Clear-Content|Set-Acl|Export-Clixml|Export-Csv|Tee-Object|Compress-Archive|Expand-Archive)\b/i;
const powerShellFileAlias =
  /^\s*(?:&\s*)?(?:copy|cp|cpi|move|mv|mi|del|erase|rm|rmdir|rd|ri|ren|rni|rename|md|mkdir|ni|xcopy|robocopy|icacls|takeown|attrib|mklink)\s+\S/i;
const nodeFsImport = /\bnode:fs\b|\brequire\(\s*["']fs["']|\bfrom\s+["']fs(?:\/promises)?["']/;
const nodeFileWriteCall =
  /\b(?:writeFile|appendFile|copyFile|cp|rm|rmdir|rename|unlink|mkdir|mkdtemp|truncate|symlink|link|chmod|utimes)(?:Sync)?\s*\(|\b(?:createWriteStream|openSync|writeSync)\s*\(/g;

// Files that may use these APIs or name these paths. Each entry records why it is exempt. An entry ending in /**
// covers the folder.
const selfAllowlist = new Map([
  [
    "scripts/check-policy-writer.js",
    "Defines the patterns this checker looks for. Its own literals are the patterns, not policy writes.",
  ],
]);

// Files that write while naming a policy path. Today the tests and the app have none, so only the oracle capture is
// listed. A test that writes a sandbox folder named GroupPolicy under a temp or run root is listed here with that reason.
const oracleCaptureReason = "oracle capture: runs only elevated by hand inside GpeditSession, which backs up and restores";
const policyPathAllowlist = new Map([
  [
    "scripts/check-registry-writer.js",
    "Writes only a temporary fixture tree under os.tmpdir() for its --self-test. Registry.pol appears only in an allowlist reason string.",
  ],
  ["tools/Bwc.Oracle.Capture/Gpedit/Session/**", oracleCaptureReason],
  ["tools/Bwc.Oracle.Capture/Gpedit/Driver/**", oracleCaptureReason],
  ["tools/Bwc.Oracle.Capture/Gpedit/Matrix/**", oracleCaptureReason],
  ["tools/Bwc.Oracle.Capture/Gpedit/Probe/**", oracleCaptureReason],
]);

// Files that name a refresh API, a policy tool, or a policy COM name without calling it. Each entry says what the
// literal is.
const refreshNameAllowlist = new Map([
  [
    "tools/Bwc.Oracle.Capture/Gpedit/Probe/ProbeCommand.cs",
    "Prints a help line that says gpupdate and RefreshPolicyEx are never called. It calls neither.",
  ],
]);
const launchAllowlist = new Map([]);

const usage = `Usage: node scripts/check-policy-writer.js [options]

Scans every .cs, NativeMethods.txt, .ps1, .js, .mjs, and .cjs file under src/, tests/, tools/, and scripts/
(skipping bin/, obj/, and node_modules/). Fails when:
  - a file-write API appears in src/Bwc.Gpo, Bwc.Policy, Bwc.Gpedit, Bwc.Gpedit.Editor, or Bwc.Gpedit.App outside
    ${gpoWriterFile}
    and ${templateWriterFile},
  - a file-write API shares a file with a GroupPolicy, PolicyDefinitions, Registry.pol, gpt.ini, or comment.cmtx
    literal and the file is not allowlisted,
  - RefreshPolicy, RefreshPolicyEx, ForceSyncFgPolicy, the critical section calls, or IGroupPolicyObject
    appear outside ${refreshFile},
  - a file launches gpupdate, secedit, gpedit.msc, or mmc outside ${launchAllowedPrefix}**,
  - a writer names the other writer's folder.
Comments are stripped before matching.

Options:
  --root <repo>   Repository root to scan (default: the script's parent folder)
  --self-test     Create a temporary tree with one violation of each rule and the files that must pass,
                  require the scan to report exactly the violations, then clean up.
  --help          Show this help
`;

function parseArgs(argv) {
  const options = { root: repoRoot, selfTest: false, help: false };

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

function allowlistReason(list, relative) {
  for (const [pattern, reason] of list) {
    if (pattern.endsWith("/**") ? relative.startsWith(pattern.slice(0, -2)) : relative === pattern) {
      return reason;
    }
  }

  return null;
}

function isProtected(relative) {
  return protectedPrefixes.some((prefix) => relative.startsWith(prefix));
}

// Walks C# source once. Returns the source with comments and literal contents blanked, plus each
// string literal with the offset where it starts. Offsets match the source, so a match in code maps to source text.
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
  const duplicate = violations.some(
    (item) => item.file === relative && item.line === line && item.rule === rule && item.detail === detail,
  );
  if (!duplicate) {
    violations.push({ file: relative, line, rule, detail });
  }
}

// Returns the argument text of the call whose "(" is at openIndex. Balanced on code, so a parenthesis in a
// literal does not count. The text comes from source, so a literal argument stays visible.
function argumentsAt(code, source, openIndex) {
  return source.slice(openIndex + 1, closeParenIndex(code, openIndex));
}

// Returns the index of the ")" that closes the "(" at openIndex, or the end of the text.
function closeParenIndex(code, openIndex) {
  let depth = 0;
  for (let position = openIndex; position < code.length; position += 1) {
    if (code[position] === "(") {
      depth += 1;
    } else if (code[position] === ")") {
      depth -= 1;
      if (depth === 0) {
        return position;
      }
    }
  }

  return code.length;
}

function firstArgumentIsLiteral(args) {
  return /^\s*(?:\$@|@\$|\$|@)?"/.test(args);
}

// Managed file-write calls in C# or PowerShell code. Returns { offset, end, detail } for each.
function collectManagedWrites(code, source) {
  const hits = [];
  const add = (match, detail) => hits.push({ offset: match.index, end: match.index + match[0].length, detail });
  const openParen = (match) => match.index + match[0].length - 1;

  for (const match of code.matchAll(staticFileWrite)) {
    add(match, `${match[1]}.${match[2]}`);
  }

  const addInstance = (offset, end, member, open) => {
    if (member !== "Open") {
      hits.push({ offset, end, detail: `.${member}` });
    } else if (!/\bFileAccess\b/.test(argumentsAt(code, source, open))) {
      hits.push({ offset, end, detail: ".Open without FileAccess.Read" });
    }
  };

  const receivers = new Set();
  for (const match of code.matchAll(fileInfoDeclaration)) {
    receivers.add(match[1] ?? match[2]);
  }

  if (receivers.size > 0) {
    const onReceiver = new RegExp(`\\b(?:${[...receivers].join("|")})\\s*\\.\\s*(${instanceTypedMembers})\\s*\\(`, "g");
    for (const match of code.matchAll(onReceiver)) {
      addInstance(match.index, match.index + match[0].length, match[1], openParen(match));
    }
  }

  for (const match of code.matchAll(fileInfoChain)) {
    const open = openParen(match);
    const close = closeParenIndex(code, open);
    const member = /^\s*\.\s*(Delete|Create|Replace|MoveTo|CopyTo|Open)\s*\(/.exec(code.slice(close + 1));
    if (member !== null) {
      addInstance(match.index, close + member[0].length + 1, member[1], close + member[0].length);
    }
  }

  for (const match of code.matchAll(instanceAlwaysWrite)) {
    add(match, `.${match[1]}`);
  }

  for (const match of code.matchAll(fileModeWrite)) {
    add(match, `FileMode.${match[1]}`);
  }

  for (const match of code.matchAll(fileAccessWrite)) {
    add(match, `FileAccess.${match[1]}`);
  }

  for (const match of code.matchAll(deleteOnClose)) {
    add(match, "FileOptions.DeleteOnClose");
  }

  for (const [pattern, label] of [
    [newFileStream, "new FileStream"],
    [fileOpen, "File.Open"],
  ]) {
    for (const match of code.matchAll(pattern)) {
      if (!/\bFileAccess\b/.test(argumentsAt(code, source, openParen(match)))) {
        add(match, `${label} without FileAccess.Read`);
      }
    }
  }

  for (const [pattern, label] of [
    [pathWriterNew, "new "],
    [xmlWriterCreate, "XmlWriter.Create"],
    [saveCall, ".Save"],
  ]) {
    for (const match of code.matchAll(pattern)) {
      const args = argumentsAt(code, source, openParen(match));
      if (args.trim() !== "" && (firstArgumentIsLiteral(args) || !streamLikeArgument.test(args))) {
        add(match, label === "new " ? `new ${match[1]} over a path` : `${label} over a path`);
      }
    }
  }

  // A call matched twice (File.AppendText also matches .AppendText) is one hit.
  hits.sort((left, right) => left.offset - right.offset);
  const kept = [];
  let lastEnd = -1;
  for (const hit of hits) {
    if (hit.offset >= lastEnd) {
      kept.push(hit);
      lastEnd = hit.end;
    }
  }

  return kept;
}

function collectNativeWrites(code, literals) {
  const hits = [];
  for (const match of code.matchAll(nativeFileWriteIdentifier)) {
    hits.push({ offset: match.index, detail: match[1] });
  }

  for (const literal of literals) {
    if (nativeFileWriteExactName.test(literal.text.trim())) {
      hits.push({ offset: literal.offset, detail: literal.text.trim() });
    }
  }

  return hits;
}

function scanRefreshNames(relative, source, code, literals, violations) {
  if (relative === refreshFile || allowlistReason(refreshNameAllowlist, relative) !== null) {
    return;
  }

  for (const match of code.matchAll(refreshNameIdentifier)) {
    addViolation(violations, relative, lineOf(source, match.index), "refresh-name", match[1]);
  }

  for (const literal of literals) {
    for (const match of literal.text.matchAll(refreshNameIdentifier)) {
      addViolation(violations, relative, lineOf(source, literal.offset), "refresh-name", match[1]);
    }

    const guid = literal.text.match(groupPolicyGuid);
    if (guid !== null) {
      addViolation(violations, relative, lineOf(source, literal.offset), "refresh-name", `group policy COM GUID ${guid[0]}`);
    }
  }
}

function scanNativeMethods(relative, source, violations) {
  const lines = source.split(/\r?\n/);
  lines.forEach((rawLine, index) => {
    const line = rawLine.trim();
    if (line === "" || line.startsWith("//")) {
      return;
    }

    if (relative !== refreshNativeMethods && refreshNameInLine.test(line)) {
      addViolation(violations, relative, index + 1, "refresh-name", line);
    }

    if (isProtected(relative) && !writerNativeMethods.has(relative) && nativeFileWriteExactName.test(line)) {
      addViolation(violations, relative, index + 1, "protected-native-name", line);
    }
  });
}

function scanSource(relative, source, violations) {
  const { code, literals } = tokenize(source);
  const isWriter = relative === gpoWriterFile || relative === templateWriterFile;
  const inProtected = isProtected(relative);
  const hasProcess = processLaunchReference.test(code);

  const writes = collectManagedWrites(code, source).map((hit) => ({ ...hit, native: false }));
  for (const hit of collectNativeWrites(code, literals)) {
    writes.push({ ...hit, native: true });
  }

  if (hasProcess) {
    for (const literal of literals) {
      if (shellFileCommandLiteral.test(literal.text)) {
        writes.push({ offset: literal.offset, detail: `shell command ${shellCommandName(literal.text)}`, native: false });
      }
    }
  }

  if (inProtected && !isWriter) {
    for (const hit of writes) {
      addViolation(
        violations,
        relative,
        lineOf(source, hit.offset),
        hit.native ? "protected-native-name" : "protected-write",
        hit.detail,
      );
    }
  } else if (!isWriter && allowlistReason(policyPathAllowlist, relative) === null) {
    const named = literals.map((literal) => findPolicyPath(literal.text)).find((match) => match !== null);
    if (named) {
      for (const hit of writes) {
        addViolation(
          violations,
          relative,
          lineOf(source, hit.offset),
          "policy-path-write",
          `${hit.detail} in a file that names ${named[0]}`,
        );
      }
    }
  }

  scanRefreshNames(relative, source, code, literals, violations);

  if (hasProcess && !relative.startsWith(launchAllowedPrefix) && allowlistReason(launchAllowlist, relative) === null) {
    for (const literal of literals) {
      const text = literal.text.trim();
      if (policyToolExecutable.test(text) || policyToolCommandLine.test(literal.text)) {
        addViolation(violations, relative, lineOf(source, literal.offset), "policy-process-launch", text);
      }
    }
  }

  if (relative === gpoWriterFile) {
    const named = literals.find((literal) => policyDefinitionsLiteral.test(literal.text));
    if (named) {
      addViolation(violations, relative, lineOf(source, named.offset), "writer-scope", "GpoFileWriter names PolicyDefinitions");
    }
  }

  if (relative === templateWriterFile) {
    const named = literals.find((literal) => gpoFolderLiteral.test(literal.text));
    if (named) {
      addViolation(
        violations,
        relative,
        lineOf(source, named.offset),
        "writer-scope",
        `TemplateStoreWriter names ${named.text.match(gpoFolderLiteral)[0]}`,
      );
    }
  }
}

function scanScript(relative, source, kind, violations) {
  const { code, withStrings, literals } = tokenizeScript(source, kind);
  const codeLines = code.split("\n");
  const writes = [];

  if (kind === "ps1") {
    for (const hit of collectManagedWrites(code, source)) {
      writes.push(hit);
    }

    codeLines.forEach((line, lineIndex) => {
      const match = line.match(powerShellFileCmdlet) ?? line.match(powerShellFileAlias);
      if (match) {
        writes.push({ offset: offsetOfLine(source, lineIndex), detail: match[0].trim().split(/\s+/)[0] });
      }
    });
  } else if (nodeFsImport.test(withStrings)) {
    for (const match of code.matchAll(nodeFileWriteCall)) {
      writes.push({ offset: match.index, detail: match[0].replace(/\s*\($/, "") });
    }
  }

  // A P/Invoke name counts wherever it is written, including inside an Add-Type string.
  for (const match of withStrings.matchAll(nativeFileWriteIdentifier)) {
    writes.push({ offset: match.index, detail: match[1] });
  }

  if (scriptProcessLaunch.test(code)) {
    for (const literal of literals) {
      if (shellFileCommandLiteral.test(literal.text)) {
        writes.push({ offset: literal.offset, detail: `shell command ${shellCommandName(literal.text)}` });
      }
    }
  }

  if (allowlistReason(policyPathAllowlist, relative) === null) {
    const named = findPolicyPath(withStrings);
    if (named) {
      for (const hit of writes) {
        addViolation(
          violations,
          relative,
          lineOf(source, hit.offset),
          "policy-path-write",
          `${hit.detail} in a file that names ${named[0]}`,
        );
      }
    }
  }

  // Refresh names count wherever they are written, including a P/Invoke name in a string.
  if (relative !== refreshFile && allowlistReason(refreshNameAllowlist, relative) === null) {
    for (const match of withStrings.matchAll(refreshNameIdentifier)) {
      addViolation(violations, relative, lineOf(source, match.index), "refresh-name", match[1]);
    }

    const guid = withStrings.match(groupPolicyGuid);
    if (guid !== null) {
      addViolation(violations, relative, lineOf(source, guid.index), "refresh-name", `group policy COM GUID ${guid[0]}`);
    }
  }

  if (!relative.startsWith(launchAllowedPrefix) && allowlistReason(launchAllowlist, relative) === null) {
    if (kind === "ps1") {
      codeLines.forEach((line, lineIndex) => {
        const match = line.match(policyToolCommandWord);
        if (match) {
          addViolation(violations, relative, lineIndex + 1, "policy-process-launch", match[0]);
        }
      });

      for (const literal of literals) {
        const text = literal.text.trim();
        if (policyToolExecutable.test(text) || policyToolCommandLine.test(literal.text)) {
          addViolation(violations, relative, lineOf(source, literal.offset), "policy-process-launch", text);
        }
      }
    } else if (scriptProcessLaunch.test(code)) {
      for (const literal of literals) {
        const text = literal.text.trim();
        if (policyToolExecutable.test(text) || policyToolCommandLine.test(literal.text)) {
          addViolation(violations, relative, lineOf(source, literal.offset), "policy-process-launch", text);
        }
      }
    }
  }
}

function shellCommandName(text) {
  return text.match(shellFileCommandLiteral)[1].toLowerCase();
}

function offsetOfLine(source, lineIndex) {
  let offset = 0;
  for (let line = 0; line < lineIndex; line += 1) {
    const next = source.indexOf("\n", offset);
    if (next === -1) {
      return source.length;
    }

    offset = next + 1;
  }

  return offset;
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
    if (allowlistReason(selfAllowlist, relative) !== null) {
      continue;
    }

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

    scanSource(relative, source, violations);
  }

  return violations;
}

function printViolations(violations) {
  for (const violation of violations) {
    console.error(`${violation.file}:${violation.line}: ${violation.rule}: ${violation.detail}`);
  }
}

function runSelfTest() {
  const temporaryRoot = mkdtempSync(path.join(tmpdir(), "bwc-policy-writer-"));

  try {
    const fixtures = {
      // protected-write: every protected project, each kind of write API.
      "src/Bwc.Gpo/Pol/DirectWrite.cs": [
        "using System.IO;",
        "internal static class DirectWrite",
        "{",
        '    internal static void A(string p) => File.WriteAllText(p, "x");',
        "    internal static void B(string p) => File.Replace(p, p, null);",
        "    internal static void C(string p) { using var s = new FileStream(p, FileMode.Create, FileAccess.Read); }",
        "    internal static void D(string p) { using var s = new FileStream(p, FileMode.Open, FileAccess.Write); }",
        "    internal static void E(string p) { using var s = new FileStream(p, FileMode.Open); }",
        "    internal static void F(string p) => Directory.CreateDirectory(p);",
        "    internal static void G(FileInfo info) => info.Delete();",
        "    internal static void H(string p) { using var w = new StreamWriter(p); }",
        "    internal static void I(string p) => File.Move(p, p + \".bak\");",
        "    internal static void J(string p) => File.Delete(p);",
        "    internal static void K(string p) { using var s = File.Open(p, FileMode.OpenOrCreate, FileAccess.Read); }",
        "    internal static void L(XDocument document, string path) => document.Save(path);",
        "    internal static void M(FileInfo info) => info.OpenWrite();",
        "    internal static void N(string p) { using var s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose); }",
        "    internal static void O(string p) => new FileInfo(p).Delete();",
        "}",
      ],
      "src/Bwc.Gpo/Pol/ReadOnly.cs": [
        "using System.IO;",
        "// File.WriteAllText(path) and new FileStream(path, FileMode.Create) in a comment.",
        "/* File.Delete(path); Directory.CreateDirectory(path); */",
        "internal static class ReadOnly",
        "{",
        '    internal const string Message = "File.WriteAllText is not used here.";',
        "    internal static byte[] Read(string p) => File.ReadAllBytes(p);",
        "    internal static bool Has(string p) => File.Exists(p) || Directory.Exists(p);",
        "    internal static void Open(string p) { using var s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read); }",
        "    internal static void OpenAgain(string p) { using var s = File.Open(p, FileMode.Open, FileAccess.Read); }",
        "    internal static void Memory(MemoryStream memory) { using var w = new StreamWriter(memory); w.Flush(); }",
        "    internal static string Fix(string text) => text.Replace(\"a\", \"b\").Replace(\"c\", \"d\");",
        "    internal static void Copy(Stream a, Stream b) => a.CopyTo(b);",
        "    internal static void Settings(Options options) => options.Save();",
        "    internal static void Xml(XDocument document, Stream stream) => document.Save(stream);",
        "    internal static void Typed(FileInfo info, Stream s) { var r = XmlReader.Create(s); var t = \"x\".Replace(\"a\", \"b\"); long n = info.Length; }",
        "}",
      ],
      "src/Bwc.Gpo/Pol/Natives.cs": [
        "using System.Runtime.InteropServices;",
        "internal static partial class Natives",
        "{",
        '    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW")]',
        "    internal static partial bool Move(string a, string b, int flags);",
        "    internal static extern SafeFileHandle CreateFileW(string name);",
        "    internal static bool Use(string p) => PInvoke.DeleteFile(p);",
        "    internal static bool Free(string p) => CloseHandle(p) && Directory.CreateDirectory(p) != null;",
        "}",
      ],
      "src/Bwc.Gpo/NativeMethods.txt": ["// a comment", "GetLastError", "CreateFileW", "ReplaceFile"],
      "src/Bwc.Gpo/Store/NativeMethods.txt": ["CreateFileW", "ReplaceFile", "MoveFileExW"],
      "src/Bwc.Policy/Store/NativeMethods.txt": ["CreateFileW", "ReplaceFile"],
      "src/Bwc.Gpo/Store/GpoFileWriter.cs": [
        "namespace Bwc.Gpo.Store;",
        "internal sealed class GpoFileWriter",
        "{",
        '    internal const string Folder = "GroupPolicy";',
        '    internal const string Pol = "Registry.pol";',
        "    internal void Write(string temp, string target) { File.WriteAllBytes(temp, new byte[0]); File.Replace(temp, target, target + \".bak\"); }",
        "    internal void Open(string temp) { using var s = new FileStream(temp, FileMode.Create); }",
        "}",
      ],
      "src/Bwc.Policy/Store/TemplateStoreWriter.cs": [
        "namespace Bwc.Policy.Store;",
        "internal sealed class TemplateStoreWriter",
        "{",
        '    internal const string Folder = "PolicyDefinitions";',
        "    internal void Write(string temp, string target) { File.Move(temp, target); Directory.CreateDirectory(target); }",
        "}",
      ],
      "src/Bwc.Policy/Admx/Copies.cs": ["internal static class Copies", "{", "    internal static void A(string p) => File.Copy(p, p);", "}"],
      "src/Bwc.Gpedit/Editor.cs": ["internal static class Editor", "{", "    internal static void A(string p) => File.Delete(p);", "}"],
      "src/Bwc.Gpedit.Editor/Saves.cs": ["internal static class Saves", "{", "    internal static void A(string p) => File.AppendAllText(p, \"x\");", "}"],
      "src/Bwc.Gpedit.App/Shell.cs": ["internal static class Shell", "{", "    internal static void A(string p) => File.Move(p, p);", "}"],
      // A folder that only starts like a protected one is not protected.
      "src/Bwc.Gpedit.Extra/Other.cs": ["internal static class Other", "{", "    internal static void A(string p) => File.WriteAllText(p, \"x\");", "}"],
      "src/Bwc.Update/Installer.cs": [
        "internal static class Installer",
        "{",
        "    internal static void A(string p) => File.Move(p, p + \".old\");",
        "    internal static extern bool MoveFileExW(string a, string b, int flags);",
        '    internal const string Hint = "gpupdate is not run by the installer.";',
        "}",
      ],
      // policy-path-write
      "src/Bwc.Other/PolicyLiteral.cs": [
        "using System.IO;",
        "internal static class PolicyLiteral",
        "{",
        '    internal static void A(byte[] b) => File.WriteAllBytes(Path.Combine(root, "GroupPolicy", "Machine", "Registry.pol"), b);',
        "    internal static void F(string p) { using var s = new FileStream(p, FileMode.Open); }",
        "}",
      ],
      // Each policy name alone is enough to make a write in the same file a violation.
      "src/Bwc.Other/Names/Users.cs": ["internal static class Users", "{", '    internal static void A() => File.Delete(@"x\\GroupPolicyUsers\\a");', "}"],
      "src/Bwc.Other/Names/Definitions.cs": [
        "internal static class Definitions",
        "{",
        '    internal static void A() => Directory.CreateDirectory(Path.Combine(data, "BetterWindowsConfig", "PolicyDefinitions"));',
        "}",
      ],
      "src/Bwc.Other/Names/Gpt.cs": ["internal static class Gpt", "{", '    internal static void A(string p) => File.Copy(p, "gpt.ini");', "}"],
      "src/Bwc.Other/Names/Comment.cs": ["internal static class Comment", "{", '    internal static void A(string p) => File.Move(p, "comment.cmtx");', "}"],
      "src/Bwc.Other/Names/Pol.cs": ["internal static class Pol", "{", '    internal static void A(byte[] b) => File.WriteAllBytes("Registry.pol", b);', "}"],
      "src/Bwc.Other/Names/Interpolated.cs": [
        "internal static class Interpolated",
        "{",
        '    internal static void A(string root) => File.WriteAllText($"{root}\\\\GroupPolicy\\\\x", "x");',
        "}",
      ],
      "tests/Fixture/Shell.cs": [
        "using System.Diagnostics;",
        "internal static class Shell",
        "{",
        '    internal static void A() => Process.Start("cmd.exe", "/c copy a C:\\\\Windows\\\\System32\\\\GroupPolicy\\\\gpt.ini");',
        "}",
      ],
      "tests/Fixture/ReadPolicy.cs": [
        "using System.IO;",
        "internal static class ReadPolicy",
        "{",
        '    internal static byte[] Read(string root) => File.ReadAllBytes(Path.Combine(root, "GroupPolicy", "Machine", "Registry.pol"));',
        "    internal static bool Has(string root) => File.Exists(root) && Directory.Exists(root);",
        "}",
      ],
      "tests/Fixture/Sandbox.cs": [
        "using System.IO;",
        "// File.Delete(@\"C:\\Windows\\System32\\GroupPolicy\\gpt.ini\") is only a comment.",
        "internal static class Sandbox",
        "{",
        '    internal static void Scratch(string temp) => File.WriteAllText(Path.Combine(temp, "scratch.txt"), "x");',
        "}",
      ],
      "tests/Fixture/XmlName.cs": [
        "using System.IO;",
        "internal static class XmlName",
        "{",
        '    internal const string Xml = "<policyDefinitions><policyNamespaces /></policyDefinitions>";',
        '    internal const string Assembly = "Microsoft.GroupPolicy.AdmTmplEditor";',
        '    internal static void Other(string temp) => File.WriteAllText(Path.Combine(temp, "x.admx"), Xml);',
        "}",
      ],
      "tests/Fixture/ObjectName.cs": [
        "using System.IO;",
        "internal static class ObjectName",
        "{",
        '    internal const string Name = "GroupPolicyObject";',
        '    internal static void Other(string temp) => File.WriteAllText(Path.Combine(temp, "x"), Name);',
        "}",
      ],
      "tools/Bwc.Oracle.Capture/Gpedit/Session/Backup.cs": [
        "internal static class Backup",
        "{",
        '    internal static void A(string p) => File.Copy(p, @"C:\\Windows\\System32\\GroupPolicy\\Machine\\Registry.pol", true);',
        "}",
      ],
      "tools/Bwc.Oracle.Capture/Gpedit/Driver/Seed.cs": [
        "internal static class Seed",
        "{",
        '    internal static void A() => Directory.CreateDirectory(@"C:\\Windows\\PolicyDefinitions\\en-US");',
        "}",
      ],
      "tools/Bwc.Oracle.Capture/Gpedit/Matrix/Apply.cs": [
        "internal static class Apply",
        "{",
        '    internal static void A(byte[] b) => File.WriteAllBytes("Registry.pol", b);',
        "}",
      ],
      "tools/Bwc.Oracle.Capture/Gpedit/Probe/PolFiles.cs": [
        "internal static class PolFiles",
        "{",
        '    internal static void A() => File.Delete("gpt.ini");',
        "}",
      ],
      "tools/Bwc.Oracle.Capture/Gpedit/GpeditCommands.cs": [
        "internal static class GpeditCommands",
        "{",
        '    internal static void A() => File.Delete("gpt.ini");',
        "}",
      ],
      "scripts/Fixture/Copy.ps1": [
        "# Copy-Item x C:\\Windows\\System32\\GroupPolicy is only a comment",
        "Copy-Item $a C:\\Windows\\System32\\GroupPolicy\\Machine\\Registry.pol",
        "[System.IO.File]::WriteAllText('C:\\Windows\\System32\\GroupPolicy\\gpt.ini', 'x')",
        "Set-Content -Path (Join-Path $env:ProgramData 'BetterWindowsConfig\\PolicyDefinitions\\a.admx') -Value 'x'",
        "xcopy a b",
      ],
      "scripts/Fixture/CleanWrite.ps1": [
        "# GroupPolicy and Registry.pol appear only in this comment, with Set-Content.",
        "Set-Content -Path (Join-Path $env:TEMP 'x.txt') -Value 'a'",
      ],
      "scripts/Fixture/ReadOnly.ps1": [
        "Get-Content 'C:\\Windows\\System32\\GroupPolicy\\gpt.ini'",
        "Get-ChildItem 'C:\\Windows\\System32\\GroupPolicy\\Machine'",
        "Test-Path 'C:\\Windows\\PolicyDefinitions'",
      ],
      "scripts/Fixture/Write.js": [
        "import { writeFileSync, copyFileSync } from 'node:fs';",
        "writeFileSync('C:/Windows/System32/GroupPolicy/gpt.ini', 'x');",
        "copyFileSync('a', `C:/Windows/System32/PolicyDefinitions/a.admx`);",
      ],
      "scripts/Fixture/CleanWrite.js": [
        "import { writeFileSync } from 'node:fs';",
        "// writeFileSync('C:/Windows/System32/GroupPolicy/gpt.ini') in a comment",
        "writeFileSync('x.txt', 'a');",
      ],
      "scripts/Fixture/ReadOnly.js": [
        "import { readFileSync } from 'node:fs';",
        "readFileSync('C:/Windows/System32/GroupPolicy/gpt.ini');",
        "const note = 'Registry.pol is read only';",
      ],
      "scripts/Fixture/NoFs.js": [
        "const registry = 'Registry.pol';",
        "const rename = (value) => value.rename('x');",
      ],
      // refresh-name
      "src/Bwc.Gpo/Refresh/PolicyRefresh.cs": [
        "using System.Runtime.InteropServices;",
        "internal static partial class PolicyRefresh",
        "{",
        '    [LibraryImport("userenv.dll", EntryPoint = "RefreshPolicyEx")]',
        "    internal static partial bool RefreshPolicyEx(bool machine, uint options);",
        "    internal static partial IntPtr EnterCriticalPolicySection(bool machine);",
        '    private const string Clsid = "EA502722-A23D-11d1-A7D3-0000F87571E3";',
        "    internal interface IGroupPolicyObject { }",
        "}",
      ],
      "src/Bwc.Gpo/Refresh/NativeMethods.txt": ["RefreshPolicyEx", "EnterCriticalPolicySection", "LeaveCriticalPolicySection"],
      "src/Bwc.Gpo/Refresh/OtherRefresh.cs": ["internal static class OtherRefresh", "{", "    internal static void A() => RefreshPolicyEx(true, 0);", "}"],
      "src/Fixture/Refresh.cs": [
        "using System.Runtime.InteropServices;",
        "internal static class Refresh",
        "{",
        '    [DllImport("userenv.dll")] static extern bool RefreshPolicy(bool machine);',
        "    static void A() => ForceSyncFgPolicy();",
        '    static void B(nint module) => GetProcAddress(module, "RefreshPolicyEx");',
        '    private const string Id = "EA502723-A23D-11d1-A7D3-0000F87571E3";',
        "    static void C() => LeaveCriticalPolicySection(0);",
        '    private const string Class = "CLSID_GroupPolicyObject";',
        "}",
      ],
      "src/Fixture/NativeMethods.txt": ["// RefreshPolicyEx in a comment", "EnterCriticalPolicySection", "LeaveCriticalPolicySection"],
      "src/Fixture/RefreshClean.cs": [
        "// RefreshPolicyEx and IGroupPolicyObject in a comment.",
        "/* ForceSyncFgPolicy in a block comment. */",
        "internal static class RefreshClean",
        "{",
        "    internal static void RefreshPolicyExtra() { }",
        '    internal const string Message = "GroupPolicyObject is not used here";',
        "}",
      ],
      "scripts/Fixture/Refresh.ps1": [
        "# RefreshPolicyEx in a comment",
        "Add-Type -MemberDefinition '[DllImport(\"userenv.dll\")] public static extern bool RefreshPolicy(bool m);' -Name U -Namespace N",
      ],
      "scripts/Fixture/Refresh.js": ["// RefreshPolicyEx in a comment is fine.", "const api = 'RefreshPolicyEx';"],
      // policy-process-launch
      "src/Fixture/Launch.cs": [
        "using System.Diagnostics;",
        "internal static class Launch",
        "{",
        '    internal static void A() => Process.Start("gpupdate", "/force");',
        '    internal static void B() => Process.Start(new ProcessStartInfo("secedit.exe"));',
        '    internal static void C() => Process.Start("cmd.exe", "/c gpupdate /target:computer");',
        '    internal static void D() => Process.Start("mmc.exe", "gpedit.msc");',
        '    internal static void E() => Process.Start(@"C:\\Windows\\System32\\gpupdate.exe");',
        '    internal static void F() => Process.Start("notepad.exe");',
        "}",
      ],
      "src/Fixture/NoLaunch.cs": [
        "internal static class NoLaunch",
        "{",
        '    internal const string Help = "gpupdate";',
        '    internal const string Tool = "mmc.exe";',
        "}",
      ],
      "tools/Bwc.Oracle.Capture/Gpedit/Driver/Launch.cs": [
        "using System.Diagnostics;",
        "internal static class Launch",
        "{",
        '    internal static void A() => Process.Start("mmc.exe", "gpedit.msc");',
        '    internal static void B() => Process.Start("gpupdate", "/force");',
        "}",
      ],
      "tools/Bwc.Oracle.Capture/Other/Launch.cs": [
        "using System.Diagnostics;",
        "internal static class Launch",
        "{",
        '    internal static void A() => Process.Start("mmc.exe", "gpedit.msc");',
        "}",
      ],
      "scripts/Fixture/Launch.ps1": [
        "# gpupdate in a comment",
        "& gpupdate /force",
        "Start-Process 'secedit.exe' -ArgumentList '/configure'",
        "Write-Output 'Run gpupdate later'",
      ],
      "scripts/Fixture/Launch.js": [
        "import { execFileSync, spawnSync } from 'node:child_process';",
        "execFileSync('gpupdate', ['/force']);",
        "spawnSync('mmc.exe', ['gpedit.msc']);",
        "spawnSync('node', ['--version']);",
        "const note = 'Run gpupdate later';",
      ],
      "scripts/Fixture/NoLaunch.js": ["const help = 'gpupdate';", "const tool = 'mmc.exe';"],
    };

    for (const [relative, lines] of Object.entries(fixtures)) {
      writeFixture(temporaryRoot, relative, lines);
    }

    const scopeRoot = mkdtempSync(path.join(tmpdir(), "bwc-policy-writer-scope-"));
    try {
      writeFixture(scopeRoot, gpoWriterFile, [
        "internal sealed class GpoFileWriter",
        "{",
        '    internal const string Wrong = "PolicyDefinitions";',
        "}",
      ]);
      writeFixture(scopeRoot, templateWriterFile, [
        "internal sealed class TemplateStoreWriter",
        "{",
        '    internal const string Wrong = "Registry.pol";',
        '    internal const string Folder = @"C:\\Windows\\System32\\GroupPolicy";',
        "}",
      ]);

      const scopeResult = spawnSync(process.execPath, [scriptPath, "--root", scopeRoot], { encoding: "utf8" });
      const scopeOutput = `${scopeResult.stdout ?? ""}${scopeResult.stderr ?? ""}`;
      if (scopeResult.status !== 1) {
        throw new Error(`Self-test scope tree expected exit code 1 but got ${scopeResult.status}.\n${scopeOutput}`);
      }

      for (const fragment of [
        "src/Bwc.Gpo/Store/GpoFileWriter.cs:3: writer-scope: GpoFileWriter names PolicyDefinitions",
        "src/Bwc.Policy/Store/TemplateStoreWriter.cs:3: writer-scope: TemplateStoreWriter names Registry.pol",
      ]) {
        if (!scopeOutput.includes(fragment)) {
          throw new Error(`Self-test did not report the scope violation "${fragment}".\n${scopeOutput}`);
        }
      }
    } finally {
      rmSync(scopeRoot, { recursive: true, force: true });
    }

    const result = spawnSync(process.execPath, [scriptPath, "--root", temporaryRoot], { encoding: "utf8" });
    const output = `${result.stdout ?? ""}${result.stderr ?? ""}`;

    if (result.status !== 1) {
      throw new Error(`Self-test expected exit code 1 but got ${result.status}.\n${output}`);
    }

    const expectedFragments = [
      "src/Bwc.Gpo/Pol/DirectWrite.cs:4: protected-write: File.WriteAllText",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:5: protected-write: File.Replace",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:6: protected-write: FileMode.Create",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:7: protected-write: FileAccess.Write",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:8: protected-write: new FileStream without FileAccess.Read",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:9: protected-write: Directory.CreateDirectory",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:10: protected-write: .Delete",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:11: protected-write: new StreamWriter over a path",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:12: protected-write: File.Move",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:13: protected-write: File.Delete",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:14: protected-write: FileMode.OpenOrCreate",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:15: protected-write: .Save over a path",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:16: protected-write: .OpenWrite",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:17: protected-write: FileOptions.DeleteOnClose",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:18: protected-write: .Delete",
      "src/Bwc.Gpo/Pol/Natives.cs:4: protected-native-name: MoveFileExW",
      "src/Bwc.Gpo/Pol/Natives.cs:6: protected-native-name: CreateFileW",
      "src/Bwc.Gpo/Pol/Natives.cs:7: protected-native-name: DeleteFile",
      "src/Bwc.Gpo/Pol/Natives.cs:8: protected-write: Directory.CreateDirectory",
      "src/Bwc.Gpo/NativeMethods.txt:3: protected-native-name: CreateFileW",
      "src/Bwc.Gpo/NativeMethods.txt:4: protected-native-name: ReplaceFile",
      "src/Bwc.Policy/Admx/Copies.cs:3: protected-write: File.Copy",
      "src/Bwc.Gpedit/Editor.cs:3: protected-write: File.Delete",
      "src/Bwc.Gpedit.Editor/Saves.cs:3: protected-write: File.AppendAllText",
      "src/Bwc.Gpedit.App/Shell.cs:3: protected-write: File.Move",
      "src/Bwc.Other/PolicyLiteral.cs:4: policy-path-write: File.WriteAllBytes in a file that names GroupPolicy",
      "src/Bwc.Other/PolicyLiteral.cs:5: policy-path-write: new FileStream without FileAccess.Read in a file that names GroupPolicy",
      "src/Bwc.Other/Names/Users.cs:3: policy-path-write: File.Delete in a file that names GroupPolicyUsers",
      "src/Bwc.Other/Names/Definitions.cs:3: policy-path-write: Directory.CreateDirectory in a file that names PolicyDefinitions",
      "src/Bwc.Other/Names/Gpt.cs:3: policy-path-write: File.Copy in a file that names gpt.ini",
      "src/Bwc.Other/Names/Comment.cs:3: policy-path-write: File.Move in a file that names comment.cmtx",
      "src/Bwc.Other/Names/Pol.cs:3: policy-path-write: File.WriteAllBytes in a file that names Registry.pol",
      "src/Bwc.Other/Names/Interpolated.cs:3: policy-path-write: File.WriteAllText in a file that names GroupPolicy",
      "tests/Fixture/Shell.cs:4: policy-path-write: shell command copy in a file that names GroupPolicy",
      "tools/Bwc.Oracle.Capture/Gpedit/GpeditCommands.cs:3: policy-path-write: File.Delete in a file that names gpt.ini",
      "scripts/Fixture/Copy.ps1:2: policy-path-write: Copy-Item in a file that names GroupPolicy",
      "scripts/Fixture/Copy.ps1:3: policy-path-write: File.WriteAllText in a file that names GroupPolicy",
      "scripts/Fixture/Copy.ps1:4: policy-path-write: Set-Content in a file that names GroupPolicy",
      "scripts/Fixture/Copy.ps1:5: policy-path-write: xcopy in a file that names GroupPolicy",
      "scripts/Fixture/Write.js:2: policy-path-write: writeFileSync in a file that names GroupPolicy",
      "scripts/Fixture/Write.js:3: policy-path-write: copyFileSync in a file that names GroupPolicy",
      "src/Bwc.Gpo/Refresh/OtherRefresh.cs:3: refresh-name: RefreshPolicyEx",
      "src/Fixture/Refresh.cs:4: refresh-name: RefreshPolicy",
      "src/Fixture/Refresh.cs:5: refresh-name: ForceSyncFgPolicy",
      "src/Fixture/Refresh.cs:6: refresh-name: RefreshPolicyEx",
      "src/Fixture/Refresh.cs:7: refresh-name: group policy COM GUID EA502723-A23D-11d1-A7D3-0000F87571E3",
      "src/Fixture/Refresh.cs:8: refresh-name: LeaveCriticalPolicySection",
      "src/Fixture/Refresh.cs:9: refresh-name: CLSID_GroupPolicyObject",
      "src/Fixture/NativeMethods.txt:2: refresh-name: EnterCriticalPolicySection",
      "src/Fixture/NativeMethods.txt:3: refresh-name: LeaveCriticalPolicySection",
      "scripts/Fixture/Refresh.ps1:2: refresh-name: RefreshPolicy",
      "scripts/Fixture/Refresh.js:2: refresh-name: RefreshPolicyEx",
      "src/Fixture/Launch.cs:4: policy-process-launch: gpupdate",
      "src/Fixture/Launch.cs:5: policy-process-launch: secedit.exe",
      "src/Fixture/Launch.cs:6: policy-process-launch: /c gpupdate /target:computer",
      "src/Fixture/Launch.cs:7: policy-process-launch: mmc.exe",
      "src/Fixture/Launch.cs:7: policy-process-launch: gpedit.msc",
      "src/Fixture/Launch.cs:8: policy-process-launch: C:\\Windows\\System32\\gpupdate.exe",
      "tools/Bwc.Oracle.Capture/Other/Launch.cs:4: policy-process-launch: mmc.exe",
      "scripts/Fixture/Launch.ps1:2: policy-process-launch: gpupdate",
      "scripts/Fixture/Launch.ps1:3: policy-process-launch: secedit.exe",
      "scripts/Fixture/Launch.js:2: policy-process-launch: gpupdate",
      "scripts/Fixture/Launch.js:3: policy-process-launch: mmc.exe",
    ];

    for (const fragment of expectedFragments) {
      if (!output.includes(fragment)) {
        throw new Error(`Self-test did not report the fixture violation "${fragment}".\n${output}`);
      }
    }

    const forbiddenFragments = [
      "src/Bwc.Gpo/Pol/ReadOnly.cs",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:19",
      "tests/Fixture/XmlName.cs",
      "src/Bwc.Gpo/Pol/DirectWrite.cs:3:",
      "src/Bwc.Gpo/Pol/Natives.cs:5:",
      "src/Bwc.Gpo/Store/NativeMethods.txt",
      "src/Bwc.Policy/Store/NativeMethods.txt",
      "src/Bwc.Gpo/NativeMethods.txt:1",
      "src/Bwc.Gpo/NativeMethods.txt:2",
      "src/Bwc.Gpo/Store/GpoFileWriter.cs",
      "src/Bwc.Policy/Store/TemplateStoreWriter.cs",
      "src/Bwc.Gpedit.Extra/",
      "src/Bwc.Update/Installer.cs",
      "src/Bwc.Other/PolicyLiteral.cs:3",
      "tests/Fixture/ReadPolicy.cs",
      "tests/Fixture/Sandbox.cs",
      "tests/Fixture/ObjectName.cs",
      "scripts/Fixture/ReadOnly.ps1",
      "scripts/Fixture/ReadOnly.js",
      "tools/Bwc.Oracle.Capture/Gpedit/Session/",
      "tools/Bwc.Oracle.Capture/Gpedit/Driver/Seed.cs",
      "tools/Bwc.Oracle.Capture/Gpedit/Matrix/",
      "tools/Bwc.Oracle.Capture/Gpedit/Probe/",
      "tools/Bwc.Oracle.Capture/Gpedit/Driver/Launch.cs",
      "scripts/Fixture/Copy.ps1:1:",
      "scripts/Fixture/CleanWrite.ps1",
      "scripts/Fixture/CleanWrite.js",
      "scripts/Fixture/NoFs.js",
      "scripts/Fixture/NoLaunch.js",
      "scripts/Fixture/Launch.ps1:1:",
      "scripts/Fixture/Launch.ps1:4:",
      "scripts/Fixture/Launch.js:4:",
      "scripts/Fixture/Launch.js:5:",
      "scripts/Fixture/Refresh.ps1:1:",
      "scripts/Fixture/Refresh.js:1:",
      "src/Bwc.Gpo/Refresh/PolicyRefresh.cs",
      "src/Bwc.Gpo/Refresh/NativeMethods.txt",
      "src/Fixture/RefreshClean.cs",
      "src/Fixture/NoLaunch.cs",
      "src/Fixture/Launch.cs:9:",
      "src/Fixture/NativeMethods.txt:1:",
    ];

    for (const fragment of forbiddenFragments) {
      if (output.includes(fragment)) {
        throw new Error(`Self-test reported a file that must pass: ${fragment}.\n${output}`);
      }
    }

    console.log(
      `check-policy-writer self-test: ${expectedFragments.length + 2} fixture violations detected across C#, NativeMethods.txt, script, and tool files; comments, prose, read-only opens, the named writers, the oracle capture, and the adjacent NativeMethods.txt passed.`,
    );
    return 0;
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true });
  }
}

function writeFixture(root, relative, lines) {
  const filePath = path.join(root, ...relative.split("/"));
  mkdirSync(path.dirname(filePath), { recursive: true });
  writeFileSync(filePath, lines.join("\n") + "\n", "utf8");
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
      console.error(`check-policy-writer self-test failed: ${error.message}`);
      return 1;
    }
  }

  const violations = scan(options.root);
  printViolations(violations);

  if (violations.length > 0) {
    console.error(`check-policy-writer: ${violations.length} violation(s) outside the named policy writers and their allowlists.`);
    return 1;
  }

  console.log("check-policy-writer: no policy file writes, refresh calls, or policy tool launches outside the named writers and their allowlists.");
  return 0;
}

process.exitCode = main();
