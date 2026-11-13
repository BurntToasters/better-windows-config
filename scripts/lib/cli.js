import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const EXIT = Object.freeze({
  OK: 0,
  REFUSED: 1,
  USAGE: 2,
  MISSING: 3,
  EXTERNAL: 4,
});

export const RELEASE_SCRIPTS = Object.freeze([
  "scripts/sign-update-manifest.js",
  "scripts/validate-updater-manifest.js",
  "scripts/release-dry-run.js",
  "scripts/release-preflight.js",
  "scripts/release-session.js",
  "scripts/release-draft.js",
  "scripts/release-verify.js",
  "scripts/release-publish.js",
  "scripts/release-selftest.js",
]);

export function repoRootFrom(moduleUrl) {
  return path.dirname(path.dirname(fileURLToPath(moduleUrl)));
}

/**
 * Parses --name value, --name=value, and boolean flags. Repeated values are
 * collected only for options declared with multi: true.
 */
export function parseArguments(argv, spec = {}) {
  const values = new Map();
  const flags = new Set();
  const positionals = [];
  const valueOptions = new Map();
  const flagOptions = new Set(spec.flags ?? []);

  for (const entry of spec.values ?? []) {
    const definition =
      typeof entry === "string" ? { name: entry, multi: false } : entry;
    valueOptions.set(definition.name, Boolean(definition.multi));
  }

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--") {
      positionals.push(...argv.slice(index + 1));
      break;
    }
    if (!arg.startsWith("-")) {
      positionals.push(arg);
      continue;
    }
    if (!arg.startsWith("--") || arg === "-") {
      throw new Error(`unrecognized argument: ${arg}`);
    }

    const equals = arg.indexOf("=");
    const name = equals === -1 ? arg.slice(2) : arg.slice(2, equals);
    const inline = equals === -1 ? undefined : arg.slice(equals + 1);

    if (flagOptions.has(name)) {
      if (inline !== undefined) {
        throw new Error(`--${name} does not take a value`);
      }
      flags.add(name);
      continue;
    }

    if (!valueOptions.has(name)) {
      throw new Error(`unrecognized option: --${name}`);
    }

    let value = inline;
    if (value === undefined) {
      if (index + 1 >= argv.length || argv[index + 1].startsWith("--")) {
        throw new Error(`--${name} requires a value`);
      }
      value = argv[index + 1];
      index += 1;
    }
    if (value === "") {
      throw new Error(`--${name} requires a non-empty value`);
    }

    if (valueOptions.get(name)) {
      if (!values.has(name)) values.set(name, []);
      values.get(name).push(value);
    } else {
      if (values.has(name)) {
        throw new Error(`--${name} was given more than once`);
      }
      values.set(name, value);
    }
  }

  return {
    values,
    flags,
    positionals,
    get(name, fallback = undefined) {
      return values.has(name) ? values.get(name) : fallback;
    },
    all(name) {
      const value = values.get(name);
      return value === undefined ? [] : Array.isArray(value) ? value : [value];
    },
    has(name) {
      return flags.has(name) || values.has(name);
    },
  };
}

export function writeJson(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

export function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

export function sha256File(filePath) {
  return createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

export async function sha256FileStream(filePath) {
  const hash = createHash("sha256");
  for await (const chunk of fs.createReadStream(filePath, { highWaterMark: 1 << 20 })) {
    hash.update(chunk);
  }
  return hash.digest("hex");
}

export function sha256Text(text) {
  return createHash("sha256").update(text, "utf8").digest("hex");
}

export function relativeTo(root, filePath) {
  const relative = path.relative(root, filePath).split(path.sep).join("/");
  return relative && !relative.startsWith("..") ? relative : filePath;
}

export function isPathInside(root, filePath) {
  const relative = path.relative(root, filePath);
  return relative !== "" && !relative.startsWith("..") && !path.isAbsolute(relative);
}

export function formatRidList(rids) {
  return rids.length === 0 ? "(none)" : rids.join(", ");
}

export function ensureDirectory(directory) {
  fs.mkdirSync(directory, { recursive: true });
  return directory;
}

export function describeError(error) {
  return error instanceof Error ? error.message : String(error);
}

export function printRefusals(prefix, errors) {
  for (const error of errors) {
    const detail = error.detail ? `: ${error.detail}` : "";
    console.error(`${prefix}: REFUSED [${error.reason}]${detail}`);
  }
}

export function quoteForDisplay(value) {
  return /\s/.test(value) ? `"${value}"` : value;
}

export function formatCommand(program, args) {
  return [program, ...args].map(quoteForDisplay).join(" ");
}

export function exitCodeForErrors(errors) {
  if (errors.length === 0) return EXIT.OK;
  return errors.some((error) => error.exitCode === EXIT.MISSING)
    ? EXIT.MISSING
    : EXIT.REFUSED;
}
