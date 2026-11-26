#!/usr/bin/env node
/**
 * Compares every RT_STRING of a classic tool with BWC's own strings and writes string-parity.json, per tool.
 *
 * Usage:
 *   node scripts/check-regedit-strings.js              every tool whose dump exists
 *   node scripts/check-regedit-strings.js --tool gpedit  one tool (regedit or gpedit)
 *
 * Tools:
 *   regedit  Input docs/parity/oracle/regedit-resources.json (modules[0], regedit.exe). Output
 *            docs/parity/oracle/string-parity.json. Ids are integers. Citation in a resw comment: "Oracle 123".
 *   gpedit   Input docs/parity/oracle/gpedit/gpedit-resources.json (every module with an RT_STRING table). Output
 *            docs/parity/oracle/gpedit/string-parity.json. Ids are qualified by module, "admtmpl.dll.mui:123".
 *            Citation in a resw comment: "Oracle admtmpl.dll.mui:123" (a list may follow the colon).
 *            Compared: every Strings/en-US/*.resw under src/Bwc.Gpedit, src/Bwc.Gpedit.Editor and src/Bwc.UI/Console
 *            (none need exist), src/Bwc.UI/Strings/en-US/Console.resw (the console frame), and the C# literals under
 *            those folders.
 *
 * The dumps are untracked (written by `npm run oracle -- [--tool gpedit] --only resources`). When a tool's dump is
 * absent (CI, a fresh clone) the script prints a skip note for it and exits 0.
 *
 * The folded comparisons apply to gpedit only; the regedit comparison is exact, so its tracked file stays as it is.
 *
 * Status per id:
 *   template  the text has %%u or %1!...! tokens, which are not compared.
 *   equal     the text (with %1 and %2 turned into {0} and {1}) is the value of a resw entry that the oracle cites, or
 *             of any resw entry, or appears as a C# string literal in the compared sources. Failing that, the folded
 *             forms are compared (see fold below): access keys (&), a trailing ellipsis, and for a classic string that
 *             holds a help line or tooltip copy after one newline, the label before it. A citing comment that ends
 *             "with <module>:<id>" also lets a classic "%s" stand for the text of that other id (the classic string is
 *             a format; BWC stores the composed text).
 *   differs   a resw entry whose comment cites the id has other text.
 *   absent    none of the above.
 *
 * The gpedit tool also reads docs/parity/oracle/gpedit/gpedit-forms.json (untracked, the managed resources of the
 * setting dialog): every string entry of the main assembly's resource files that is a text (not designer metadata) gets
 * the id "forms:<resource file>:<entry name>" and is compared with the same rules, without citations.
 *
 * Tracked output holds ids and statuses only, sorted by id, with no text and no timestamp. Written to a temporary file
 * and renamed into place.
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const SOURCE_ROOT = path.join(ROOT, "src");
const SKIPPED_DIRECTORIES = new Set(["bin", "obj", "node_modules"]);

// The gpedit projects whose strings and C# literals are compared, relative to the repository root.
const GPEDIT_ROOTS = ["src/Bwc.Gpedit", "src/Bwc.Gpedit.Editor", "src/Bwc.UI/Console"];

// The console frame's strings live in Bwc.UI beside the Registry Editor's Resources.resw, outside GPEDIT_ROOTS, so the
// file is named here. The Registry Editor's own resw files are not compared for gpedit.
const GPEDIT_EXTRA_RESW = ["src/Bwc.UI/Strings/en-US/Console.resw"];

const XML_ENTITIES = { lt: "<", gt: ">", amp: "&", quot: '"', apos: "'" };

const TOOLS = {
  regedit: {
    dump: path.join("docs", "parity", "oracle", "regedit-resources.json"),
    out: path.join("docs", "parity", "oracle", "string-parity.json"),
    skipHint: "npm run oracle -- --only resources",
    // Explicit resw files, and the whole of src/ for C# literals.
    reswFiles: () => ["src/Bwc.Regedit/Strings/en-US/Resources.resw", "src/Bwc.UI/Strings/en-US/Resources.resw"],
    sourceRoots: [SOURCE_ROOT],
    compared: () => ["src/Bwc.Regedit/Strings/en-US/Resources.resw", "src/Bwc.UI/Strings/en-US/Resources.resw", "src/**/*.cs (excluding bin and obj)"],
    source: "regedit.exe RT_STRING ids (untracked dump, regedit-resources.json)",
    qualified: false,
    folded: false,
    readOracleStrings: readRegeditStrings,
  },
  gpedit: {
    dump: path.join("docs", "parity", "oracle", "gpedit", "gpedit-resources.json"),
    out: path.join("docs", "parity", "oracle", "gpedit", "string-parity.json"),
    skipHint: "npm run oracle -- --tool gpedit --only resources",
    reswFiles: () => [...findReswFiles(GPEDIT_ROOTS), ...GPEDIT_EXTRA_RESW].sort(),
    sourceRoots: GPEDIT_ROOTS.map((root) => path.join(ROOT, root)),
    compared: () => [
      ...GPEDIT_ROOTS.map((root) => `${root}/**/Strings/en-US/*.resw`),
      ...GPEDIT_EXTRA_RESW,
      ...GPEDIT_ROOTS.map((root) => `${root}/**/*.cs (excluding bin and obj)`),
    ],
    source: "gpedit RT_STRING ids by module (untracked dump, gpedit-resources.json)",
    qualified: true,
    folded: true,
    formsDump: path.join("docs", "parity", "oracle", "gpedit", "gpedit-forms.json"),
    readOracleStrings: readGpeditStrings,
  },
};

function decodeXml(text) {
  return text.replace(/&(#x[0-9a-fA-F]+|#\d+|[a-zA-Z]+);/g, (match, body) => {
    if (body.startsWith("#x")) {
      return String.fromCodePoint(Number.parseInt(body.slice(2), 16));
    }
    if (body.startsWith("#")) {
      return String.fromCodePoint(Number.parseInt(body.slice(1), 10));
    }
    return XML_ENTITIES[body] ?? match;
  });
}

// Line endings are compared as LF on both sides, so an encoded CRLF and a raw LF do not read as a difference.
function normalizeLines(text) {
  return text.replace(/\r\n?/g, "\n");
}

// The regedit key is the integer string id. Reads the RT_STRING table of regedit.exe (module 0). Throws when the dump
// does not have the expected shape.
function readRegeditStrings(dump) {
  const module = Array.isArray(dump.modules) ? dump.modules[0] : undefined;
  if (!module || !Array.isArray(module.stringTables)) {
    throw new Error("regedit-resources.json has no modules[0].stringTables.");
  }

  const byId = new Map();
  for (const table of module.stringTables) {
    for (const entry of table.strings ?? []) {
      if (!Number.isInteger(entry.id) || typeof entry.text !== "string") {
        throw new Error("a string table entry has no integer id or no text.");
      }
      if (!byId.has(entry.id)) {
        byId.set(entry.id, entry.text);
      }
    }
  }

  if (byId.size === 0) {
    throw new Error("regedit-resources.json has no RT_STRING entries.");
  }
  return byId;
}

// The gpedit key is "<module>:<id>" for every module of the dump, so id 123 of two modules never collides.
function readGpeditStrings(dump) {
  if (!Array.isArray(dump.modules) || dump.modules.length === 0) {
    throw new Error("gpedit-resources.json has no modules.");
  }

  const byKey = new Map();
  for (const module of dump.modules) {
    if (typeof module.module !== "string" || !Array.isArray(module.stringTables)) {
      throw new Error("a gpedit-resources.json module has no name or no stringTables.");
    }
    for (const table of module.stringTables) {
      for (const entry of table.strings ?? []) {
        if (!Number.isInteger(entry.id) || typeof entry.text !== "string") {
          throw new Error(`a string table entry of ${module.module} has no integer id or no text.`);
        }
        const key = `${module.module}:${entry.id}`;
        if (!byKey.has(key)) {
          byKey.set(key, entry.text);
        }
      }
    }
  }

  if (byKey.size === 0) {
    throw new Error("gpedit-resources.json has no RT_STRING entries.");
  }
  return byKey;
}

// The texts of the managed resource files of the main assembly in gpedit-forms.json, keyed "forms:<file>:<entry>" with the
// file name short ("Editor.resources"). Designer metadata (names starting ">>", booleans) is not a text. A dump without
// resourceFiles contributes nothing; one with a malformed entry throws.
function readFormsStrings(forms) {
  const byKey = new Map();
  if (!Array.isArray(forms.resourceFiles)) {
    return byKey;
  }
  const assemblies = new Map((forms.assemblies ?? []).map((assembly) => [assembly.assembly, assembly.role]));
  for (const file of forms.resourceFiles) {
    if (assemblies.get(file.assembly) !== "main") {
      continue;
    }
    const shortName = String(file.name).slice(String(file.assembly).length + 1);
    for (const entry of file.entries ?? []) {
      if (typeof entry.name !== "string") {
        throw new Error(`a resource entry of ${file.name} has no name.`);
      }
      if (entry.kind !== "string" || entry.name.startsWith(">>") || typeof entry.value !== "string") {
        continue;
      }
      if (entry.value.trim().length === 0 || entry.value === "true" || entry.value === "false") {
        continue;
      }
      const key = `forms:${shortName}:${entry.name}`;
      if (!byKey.has(key)) {
        byKey.set(key, entry.value);
      }
    }
  }
  return byKey;
}

// Every Strings/en-US/*.resw under the given source folders, relative to the repository root. A folder that does not
// exist yet contributes nothing.
function findReswFiles(roots) {
  const found = [];
  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        if (!SKIPPED_DIRECTORIES.has(entry.name)) {
          walk(full);
        }
      } else if (entry.isFile() && entry.name.endsWith(".resw")) {
        const parent = path.dirname(full).split(path.sep).slice(-2).join("/");
        if (parent === "Strings/en-US") {
          found.push(path.relative(ROOT, full).split(path.sep).join("/"));
        }
      }
    }
  };

  for (const root of roots) {
    const absolute = path.join(ROOT, root);
    if (fs.existsSync(absolute)) {
      walk(absolute);
    }
  }
  return found.sort();
}

// Every <data> entry of the resw files, with the comment before it or inside it (if any). A comment before the element
// is the one right above <data>. A comment inside it sits between the opening tag and <value>.
function readResw(files) {
  const entries = [];
  const dataPattern = /(?:<!--([\s\S]*?)-->\s*)?<data\s+name="([^"]+)"[^>]*>(?:\s*<!--([\s\S]*?)-->)?\s*<value>([\s\S]*?)<\/value>/g;
  for (const relative of files) {
    const file = path.join(ROOT, relative);
    const xml = fs.readFileSync(file, "utf8");
    for (const match of xml.matchAll(dataPattern)) {
      entries.push({
        name: match[2],
        comment: match[1] ?? match[3] ?? "",
        value: normalizeLines(decodeXml(match[4])),
      });
    }
  }
  return entries;
}

// The ids of a list such as "25", "302 and 305" or "49 to 54".
function parseIdList(list) {
  const tokens = list.split(/\s*(,|and|to)\s*/);
  const ids = [Number.parseInt(tokens[0], 10)];
  for (let i = 1; i < tokens.length; i += 2) {
    const value = Number.parseInt(tokens[i + 1], 10);
    if (tokens[i] === "to") {
      const from = ids[ids.length - 1];
      for (let n = from + 1; n <= value && n - from <= 200; n++) {
        ids.push(n);
      }
    } else {
      ids.push(value);
    }
  }
  return ids;
}

const ID_LIST = String.raw`\d+(?:\s*(?:,|and|to)\s*\d+)*`;

// Keys cited by a comment that starts with "Oracle ...". The regedit form is "Oracle 25 (MSG-010)", "Oracle 302 and 305"
// or "Oracle 49 to 54". The gpedit form needs the module: "Oracle admtmpl.dll.mui:123" or "Oracle gpedit.dll.mui:5, 6".
// A comment that mentions an id later in its text (a file header) cites nothing.
function citedKeys(comment, qualified) {
  const text = comment.trim();
  if (!qualified) {
    const match = new RegExp(`^Oracle\\s+(${ID_LIST})`).exec(text);
    return match ? parseIdList(match[1]) : [];
  }

  const match = new RegExp(`^Oracle\\s+([A-Za-z0-9_.\\-]+):(${ID_LIST})`).exec(text);
  return match ? parseIdList(match[2]).map((id) => `${match[1]}:${id}`) : [];
}

// The text of every C# file under the given folders, without bin and obj. A string literal is searched in its escaped form.
function readCSharpSources(directories) {
  const out = [];
  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (!SKIPPED_DIRECTORIES.has(entry.name)) {
          walk(path.join(directory, entry.name));
        }
      } else if (entry.isFile() && entry.name.endsWith(".cs")) {
        out.push(fs.readFileSync(path.join(directory, entry.name), "utf8"));
      }
    }
  };
  for (const directory of directories) {
    if (fs.existsSync(directory)) {
      walk(directory);
    }
  }
  return out;
}

function csharpLiteral(text) {
  return `"${text.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}

// The comparable form of a text: access keys removed ("&&" is a literal ampersand and stays one) and a trailing
// ellipsis (three dots or U+2026) removed.
function fold(text) {
  return text
    .replace(/&&/g, "\u0000")
    .replace(/&/g, "")
    .replace(/\u0000/g, "&")
    .replace(/(?:\.{3}|…)\s*$/, "");
}

// The label of a classic string that holds one help line or tooltip copy after a single newline ("Name\nHelp text").
// A message of several paragraphs, or a first line that ends a sentence, has no label part and keeps its whole text.
function labelPart(text) {
  const lines = text.split("\n");
  if (lines.length === 2 && lines[0].length > 0 && lines[1].length > 0 && !/[.:!?]\s*$/.test(lines[0])) {
    return lines[0];
  }
  return text;
}

// The classic text with a "%s" completed by the text of the id a citing comment names after "with" (a format string
// whose argument BWC stores inside its own text).
function composedTexts(text, entry, oracle, qualified) {
  if (!qualified || !text.includes("%s")) {
    return [];
  }
  const match = /\bwith\s+([A-Za-z0-9_.\-]+:\d+)/.exec(entry.comment);
  const other = match ? oracle.get(match[1]) : undefined;
  return other === undefined ? [] : [normalizeLines(text.replace("%s", () => other))];
}

function statusOf(id, text, context) {
  const { resw, citedBy, sources, oracle, qualified, folded } = context;
  if (text.includes("%%") || /%\d+!/.test(text)) {
    return "template";
  }

  const expected = normalizeLines(text.replace(/%(\d)/g, (_, n) => `{${Number(n) - 1}}`));
  const foldedExpected = folded ? fold(labelPart(expected)) : "";
  const cited = citedBy.get(id) ?? [];
  if (cited.length > 0) {
    const matches = (entry) =>
      entry.value === expected ||
      (folded && composedTexts(text, entry, oracle, qualified).some((composed) => entry.value === composed)) ||
      (foldedExpected.length > 0 && fold(entry.value) === foldedExpected);
    return cited.some(matches) ? "equal" : "differs";
  }

  if (expected.length === 0) {
    return "absent";
  }

  if (resw.some((entry) => entry.value === expected)) {
    return "equal";
  }

  if (!expected.includes("\n")) {
    const literal = csharpLiteral(expected);
    if (sources.some((source) => source.includes(literal))) {
      return "equal";
    }
  }

  if (foldedExpected.length > 0 && resw.some((entry) => fold(entry.value) === foldedExpected)) {
    return "equal";
  }

  return "absent";
}

// Integer ids ascending, or "<module>:<id>" by module (ordinal) then id.
function compareKeys(a, b) {
  if (typeof a === "number" && typeof b === "number") {
    return a - b;
  }
  const formA = String(a).startsWith("forms:");
  const formB = String(b).startsWith("forms:");
  if (formA || formB) {
    if (formA && formB) {
      return a < b ? -1 : a > b ? 1 : 0;
    }
    return formA ? 1 : -1;
  }
  const [moduleA, idA] = String(a).split(/:(?=\d+$)/);
  const [moduleB, idB] = String(b).split(/:(?=\d+$)/);
  if (moduleA !== moduleB) {
    return moduleA < moduleB ? -1 : 1;
  }
  return Number(idA) - Number(idB);
}

function writeAtomically(file, text) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  let existing;
  try {
    existing = fs.readFileSync(file, "utf8");
  } catch (error) {
    if (error.code !== "ENOENT") {
      throw error;
    }
  }
  if (existing !== undefined && normalizeLines(existing) === normalizeLines(text)) {
    return;
  }

  const temp = `${file}.tmp`;
  fs.writeFileSync(temp, text, "utf8");
  fs.renameSync(temp, file);
}

function checkTool(name) {
  const tool = TOOLS[name];
  const dumpPath = path.join(ROOT, tool.dump);
  const outPath = path.join(ROOT, tool.out);
  if (!fs.existsSync(dumpPath)) {
    console.log(`string-parity: ${name}: skipped, ${tool.dump.split(path.sep).join("/")} is absent (untracked; run ${tool.skipHint})`);
    return;
  }

  const dump = JSON.parse(fs.readFileSync(dumpPath, "utf8"));
  const oracle = tool.readOracleStrings(dump);
  if (tool.formsDump) {
    const formsPath = path.join(ROOT, tool.formsDump);
    if (fs.existsSync(formsPath)) {
      for (const [key, text] of readFormsStrings(JSON.parse(fs.readFileSync(formsPath, "utf8")))) {
        oracle.set(key, text);
      }
    }
  }
  const reswFiles = tool.reswFiles();
  const resw = readResw(reswFiles);
  const citedBy = new Map();
  for (const entry of resw) {
    for (const key of citedKeys(entry.comment, tool.qualified)) {
      if (!citedBy.has(key)) {
        citedBy.set(key, []);
      }
      citedBy.get(key).push(entry);
    }
  }
  const sources = readCSharpSources(tool.sourceRoots);

  const keys = [...oracle.keys()].sort(compareKeys);
  const strings = keys.map((id) => ({ id, status: statusOf(id, oracle.get(id), { resw, citedBy, sources, oracle, qualified: tool.qualified, folded: tool.folded }) }));
  const counts = { equal: 0, differs: 0, template: 0, absent: 0 };
  for (const s of strings) {
    counts[s.status] += 1;
  }

  const output = {
    schema: 1,
    generator: "scripts/check-regedit-strings.js",
    source: tool.source,
    compared: tool.compared(),
    counts,
    strings,
  };
  writeAtomically(outPath, `${JSON.stringify(output, null, 2)}\n`);
  console.log(
    `string-parity: ${name}: ${strings.length} ids: equal ${counts.equal}, differs ${counts.differs}, template ${counts.template}, absent ${counts.absent}`,
  );
  console.log(`string-parity: ${name}: wrote ${path.relative(ROOT, outPath).split(path.sep).join("/")}`);
}

function parseArgs(argv) {
  let only = null;
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === "--tool") {
      const value = argv[i + 1];
      if (!value || !Object.hasOwn(TOOLS, value)) {
        throw new Error(`--tool needs one of: ${Object.keys(TOOLS).join(", ")}`);
      }
      only = value;
      i += 1;
    } else {
      throw new Error(`unknown argument '${argv[i]}'`);
    }
  }
  return only;
}

function main() {
  const only = parseArgs(process.argv.slice(2));
  for (const name of only ? [only] : Object.keys(TOOLS)) {
    checkTool(name);
  }
  return 0;
}

try {
  process.exitCode = main();
} catch (error) {
  console.error(`string-parity: ${error.message}`);
  process.exitCode = 1;
}
