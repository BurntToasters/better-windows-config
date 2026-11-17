#!/usr/bin/env node
/**
 * Compares every RT_STRING of classic regedit.exe with BWC's own strings and writes string-parity.json.
 *
 * Input: docs/parity/oracle/regedit-resources.json (untracked, written by `npm run oracle -- --only resources`).
 * When that file is absent (CI, a fresh clone) the script prints a skip note and exits 0.
 *
 * Status per id:
 *   template  the text has %%u or %1!...! tokens, which are not compared.
 *   equal     the text (with %1 and %2 turned into {0} and {1}) is the value of a resw entry that the oracle cites, or
 *             of any resw entry, or appears as a C# string literal under src/.
 *   differs   a resw entry whose comment cites "Oracle <id>" has other text.
 *   absent    none of the above.
 *
 * Tracked output: docs/parity/oracle/string-parity.json. It holds ids and statuses only, sorted by id, with no text and
 * no
 * timestamp. Written to a temporary file and renamed into place.
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const DUMP_PATH = path.join(ROOT, "docs", "parity", "oracle", "regedit-resources.json");
const OUT_PATH = path.join(ROOT, "docs", "parity", "oracle", "string-parity.json");
const RESW_FILES = ["src/Bwc.Regedit/Strings/en-US/Resources.resw", "src/Bwc.UI/Strings/en-US/Resources.resw"];
const SOURCE_ROOT = path.join(ROOT, "src");
const SKIPPED_DIRECTORIES = new Set(["bin", "obj", "node_modules"]);

const XML_ENTITIES = { lt: "<", gt: ">", amp: "&", quot: '"', apos: "'" };

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

// Reads the RT_STRING table of regedit.exe (module 0). Throws when the dump does not have the expected shape.
function readOracleStrings(dump) {
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

// Every <data> entry of the resw files, with the comment before it or inside it (if any). A comment before the element
// is the one right above <data>. A comment inside it sits between the opening tag and <value>.
function readResw() {
  const entries = [];
  const dataPattern = /(?:<!--([\s\S]*?)-->\s*)?<data\s+name="([^"]+)"[^>]*>(?:\s*<!--([\s\S]*?)-->)?\s*<value>([\s\S]*?)<\/value>/g;
  for (const relative of RESW_FILES) {
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

// Ids cited by a comment that starts with "Oracle <ids>", such as "Oracle 25 (MSG-010)", "Oracle 302 and 305" or
// "Oracle 49 to 54". A comment that mentions an id later in its text (a file header) cites nothing.
function citedIds(comment) {
  const match = /^Oracle\s+(\d+(?:\s*(?:,|and|to)\s*\d+)*)/.exec(comment.trim());
  if (!match) {
    return [];
  }

  const tokens = match[1].split(/\s*(,|and|to)\s*/);
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

// The text of every C# file under src/, without bin and obj. A string literal is searched in its escaped form.
function readCSharpSources(directory, out = []) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!SKIPPED_DIRECTORIES.has(entry.name)) {
        readCSharpSources(path.join(directory, entry.name), out);
      }
    } else if (entry.isFile() && entry.name.endsWith(".cs")) {
      out.push(fs.readFileSync(path.join(directory, entry.name), "utf8"));
    }
  }
  return out;
}

function csharpLiteral(text) {
  return `"${text.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}

function statusOf(id, text, resw, citedBy, sources) {
  if (text.includes("%%") || /%\d+!/.test(text)) {
    return "template";
  }

  const expected = normalizeLines(text.replace(/%(\d)/g, (_, n) => `{${Number(n) - 1}}`));
  const cited = citedBy.get(id) ?? [];
  if (cited.length > 0) {
    return cited.some((entry) => entry.value === expected) ? "equal" : "differs";
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

  return "absent";
}

function writeAtomically(file, text) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temp = `${file}.tmp`;
  fs.writeFileSync(temp, text, "utf8");
  fs.renameSync(temp, file);
}

function main() {
  if (!fs.existsSync(DUMP_PATH)) {
    console.log(`string-parity: skipped, ${path.relative(ROOT, DUMP_PATH)} is absent (untracked; run npm run oracle -- --only resources)`);
    return 0;
  }

  const dump = JSON.parse(fs.readFileSync(DUMP_PATH, "utf8"));
  const oracle = readOracleStrings(dump);
  const resw = readResw();
  const citedBy = new Map();
  for (const entry of resw) {
    for (const id of citedIds(entry.comment)) {
      if (!citedBy.has(id)) {
        citedBy.set(id, []);
      }
      citedBy.get(id).push(entry);
    }
  }
  const sources = readCSharpSources(SOURCE_ROOT);

  const ids = [...oracle.keys()].sort((a, b) => a - b);
  const strings = ids.map((id) => ({ id, status: statusOf(id, oracle.get(id), resw, citedBy, sources) }));
  const counts = { equal: 0, differs: 0, template: 0, absent: 0 };
  for (const s of strings) {
    counts[s.status] += 1;
  }

  const output = {
    schema: 1,
    generator: "scripts/check-regedit-strings.js",
    source: "regedit.exe RT_STRING ids (untracked dump, regedit-resources.json)",
    compared: RESW_FILES.concat(["src/**/*.cs (excluding bin and obj)"]),
    counts,
    strings,
  };
  writeAtomically(OUT_PATH, `${JSON.stringify(output, null, 2)}\n`);
  console.log(
    `string-parity: ${strings.length} ids: equal ${counts.equal}, differs ${counts.differs}, template ${counts.template}, absent ${counts.absent}`,
  );
  console.log(`string-parity: wrote ${path.relative(ROOT, OUT_PATH)}`);
  return 0;
}

try {
  process.exitCode = main();
} catch (error) {
  console.error(`string-parity: ${error.message}`);
  process.exitCode = 1;
}
