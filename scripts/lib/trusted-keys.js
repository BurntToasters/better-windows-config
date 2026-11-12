// Reads the keys the updater embeds, from src/Bwc.Update/Signing/TrustedKeys.cs. The release
// tooling uses this list as the only source of truth for "a key this build trusts". Comments are
// never consulted, so an editor cannot make a test key look like a release key.
import fs from "node:fs";
import path from "node:path";

import { isPlaceholderKeyText, parsePublicKeyText, samePublicKey } from "./minisign.js";

export const TRUSTED_KEYS_RELATIVE_PATH = path.join("src", "Bwc.Update", "Signing", "TrustedKeys.cs");

/** Unescapes the body of a C# regular string literal (without the quotes). */
function unescapeCSharp(body) {
  let out = "";
  for (let index = 0; index < body.length; index += 1) {
    const character = body[index];
    if (character !== "\\") {
      out += character;
      continue;
    }
    index += 1;
    const next = body[index];
    switch (next) {
      case "n": out += "\n"; break;
      case "r": out += "\r"; break;
      case "t": out += "\t"; break;
      case "0": out += "\0"; break;
      case "\\": out += "\\"; break;
      case '"': out += '"'; break;
      case "'": out += "'"; break;
      default:
        throw new Error(`unsupported escape \\${next ?? ""} in a trusted key string`);
    }
  }
  return out;
}

/** Concatenates the string literals of an expression such as "a" + "b". Returns null if none. */
function literalsIn(expression) {
  const literals = [...expression.matchAll(/"((?:[^"\\]|\\.)*)"/g)];
  if (literals.length === 0) return null;
  return literals.map((match) => unescapeCSharp(match[1])).join("");
}

/**
 * Parses TrustedKeys.cs text. Returns { ok, isPlaceholder, embedded: string[] } or
 * { ok: false, reason, detail }. Unknown constructs refuse the whole list (fail closed).
 */
export function parseTrustedKeysSource(text) {
  const flag = text.match(/IsPlaceholder\s*=\s*(true|false)\s*;/);
  if (!flag) {
    return { ok: false, reason: "trusted-keys-unreadable", detail: "IsPlaceholder is not a bool constant" };
  }
  const constants = new Map();
  for (const match of text.matchAll(/const\s+string\s+(\w+)\s*=\s*((?:"(?:[^"\\]|\\.)*"\s*\+?\s*)+);/g)) {
    try {
      constants.set(match[1], literalsIn(match[2]));
    } catch (error) {
      return { ok: false, reason: "trusted-keys-unreadable", detail: error.message };
    }
  }
  const list = text.match(/Embedded\s*=\s*(?:new\s*\w*\s*\[\s*\]\s*)?[[{]([^\]}]*)[\]}]\s*;/);
  if (!list) {
    return { ok: false, reason: "trusted-keys-unreadable", detail: "the Embedded key list was not found" };
  }
  const embedded = [];
  const elements = list[1].split(",").map((item) => item.trim()).filter((item) => item.length > 0);
  for (const element of elements) {
    if (/^\w+$/.test(element)) {
      if (!constants.has(element) || constants.get(element) === null) {
        return { ok: false, reason: "trusted-keys-unreadable", detail: `Embedded names ${element}, which is not a string constant` };
      }
      embedded.push(constants.get(element));
    } else if (element.startsWith('"')) {
      const value = literalsIn(element);
      if (value === null) {
        return { ok: false, reason: "trusted-keys-unreadable", detail: "an Embedded entry is not a string" };
      }
      embedded.push(value);
    } else {
      return { ok: false, reason: "trusted-keys-unreadable", detail: `unsupported Embedded entry: ${element}` };
    }
  }
  return { ok: true, isPlaceholder: flag[1] === "true", embedded };
}

/** Reads the source file under a repository root. */
export function readTrustedKeysFile(root) {
  const file = path.join(root, TRUSTED_KEYS_RELATIVE_PATH);
  if (!fs.existsSync(file)) {
    return { ok: false, reason: "missing-trusted-key-source", detail: file, file };
  }
  const parsed = parseTrustedKeysSource(fs.readFileSync(file, "utf8"));
  return { ...parsed, file };
}

/**
 * Finds the key that matches candidateText among the embedded keys. Compares the key id and the
 * key bytes. A malformed embedded key fails the whole check, so one bad entry cannot hide others.
 */
export function matchEmbeddedKey(candidateText, embeddedTexts) {
  const candidate = parsePublicKeyText(candidateText);
  if (!candidate.ok) {
    return { ok: false, reason: candidate.reason, detail: candidate.detail };
  }
  if (embeddedTexts.length === 0) {
    return { ok: false, reason: "embedded-key-mismatch", detail: "TrustedKeys.cs embeds no key" };
  }
  let matched = null;
  for (let index = 0; index < embeddedTexts.length; index += 1) {
    if (isPlaceholderKeyText(embeddedTexts[index])) {
      return { ok: false, reason: "placeholder-key", detail: `embedded key ${index} is a placeholder` };
    }
    const parsed = parsePublicKeyText(embeddedTexts[index]);
    if (!parsed.ok) {
      return { ok: false, reason: "malformed-key", detail: `embedded key ${index}: ${parsed.detail}` };
    }
    if (matched === null && samePublicKey(candidate, parsed)) {
      matched = index;
    }
  }
  if (matched === null) {
    return { ok: false, reason: "embedded-key-mismatch", detail: `key ${candidate.keyIdHex} is not one of the embedded keys` };
  }
  return { ok: true, index: matched, keyIdHex: candidate.keyIdHex, fingerprint: candidate.fingerprint };
}
