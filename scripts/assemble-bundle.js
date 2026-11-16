#!/usr/bin/env node
/**
 * Assembles one channel's update bundle: copies each installer, signs it, writes the manifest,
 * signs the manifest, and validates the result with the same validator the updater uses.
 *
 * Output: <out>/<channel>/ holds bwc-<channel>-v2.json and bwc-<channel>-v2.json.minisig (the GitHub
 * asset names; stable is bwc-stable-v2.json), bwc-<channel>-v2.json.sig (the base64 envelope), each
 * installer with its .minisig and .sig, and assembly.json (a summary with no secret material).
 * One manifest per channel: the channel is part of the folder, the file name, and the signed comment.
 *
 * A stable release also decides what 0.1.1 clients see. They read bwc-stable.json (per-user install), and must never
 * be offered a per-machine installer. --legacy-feed is required for stable: frozen (the re-signed 0.1.1 manifest, from
 * --legacy-source), no-installer (the new version, no installers), or none (no bwc-stable.json). See lib/legacy-feed.js.
 *
 * Signed comments (exact):
 *   manifest:  version:<semver> channel:<stable|beta> expires:<yyyy-MM-ddTHH:mm:ssZ>
 *   installer: version:<semver> arch:<win-x64|win-arm64>
 *
 * The signing key is --secret-key <path>, else TAURI_SIGNING_PRIVATE_KEY (see sign-update-manifest.js).
 * It must be one of the keys embedded in src/Bwc.Update/Signing/TrustedKeys.cs, matched by key id and
 * key bytes. --dry-run lifts that rule for a test key and labels the output.
 */
import fs from "node:fs";
import path from "node:path";

import { isDirectExecutionOf } from "./direct-execution.mjs";
import { EXIT, describeError, exitCodeForErrors, parseArguments, printRefusals, relativeTo, repoRootFrom, sha256FileStream, writeJson } from "./lib/cli.js";
import { envelope, isPlaceholderKeyText, parseSecretKeyText, publicKeyFingerprint, signFile } from "./lib/minisign.js";
import { resolveRepo } from "./lib/github-release.js";
import { passwordArgumentGiven } from "./lib/secret-input.js";
import { channelForVersion } from "./lib/semver.js";
import {
  buildManifest,
  manifestFileName,
  installerTrustedComment,
  manifestTrustedComment,
  serializeManifest,
  validateManifestBuffer,
} from "./lib/update-manifest.js";
import { matchEmbeddedKey, readTrustedKeysFile } from "./lib/trusted-keys.js";
import {
  buildLegacyManifest,
  isLegacyMode,
  LEGACY_MANIFEST_NAME,
  LEGACY_MODES,
  validateLegacyManifest,
  validateLegacySource,
} from "./lib/legacy-feed.js";
import { validateUrl } from "./lib/url-policy.js";
import { parsePackageVersion } from "./sync-version-helpers.js";
import { resolveKeyPassword, resolveSigningKey } from "./sign-update-manifest.js";

const SCRIPT = "scripts/assemble-bundle.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const DEFAULT_EXPIRES_HOURS = 168;
const MAX_EXPIRES_HOURS = 87600;
const PUBDATE_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;
const RID_ARCH = [
  ["win-x64", "--installer-win-x64"],
  ["win-arm64", "--installer-win-arm64"],
];

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} --version <semver> --installer-win-x64 <file> [--installer-win-arm64 <file>]
                 [--secret-key <dir|file>] [--password-env <NAME>] [--root <repo>] [--out <dir>]
                 [--pub-date <utc>] [--expires-hours <n>] [--download-base-url <url>]
                 [--notes-url <url>] [--legacy-feed <frozen|no-installer|none>] [--legacy-source <file>]
                 [--dry-run] [--json]

Options:
  --version <semver>           Required. X.Y.Z or X.Y.Z-beta.N. The channel follows from it.
  --installer-win-x64 <file>   Setup EXE for win-x64. At least one installer option is required.
  --installer-win-arm64 <file> Setup EXE for win-arm64.
  --secret-key <dir|file>      minisign secret key file or folder. Without it, the key comes from
                               TAURI_SIGNING_PRIVATE_KEY (base64 of the key file, the raw text, or a path).
                               Key text is never a command-line argument.
  --password-env <NAME>        Environment variable with the key password. Never an argument. For an
                               environment key, TAURI_SIGNING_PRIVATE_KEY_PASSWORD is used by default.
  --root <repo>                Repository whose src/Bwc.Update/Signing/TrustedKeys.cs lists the keys.
                               Default: this checkout.
  --out <dir>                  Output root. Default: artifacts/release/<version>.
  --pub-date <utc>             Default now, or SOURCE_DATE_EPOCH. Normalized UTC ending in Z.
  --expires-hours <n>          Signed expiry after the publish time. Default ${DEFAULT_EXPIRES_HOURS}, at most ${MAX_EXPIRES_HOURS}.
  --download-base-url <url>    Directory that holds the installer assets. Must be https. Default
                               https://github.com/<owner>/<repo>/releases/download/v<version>, with the
                               repository from GH_REPO, GH_REPO_OWNER, and GH_REPO_NAME (see resolveRepo).
  --notes-url <url>            Release notes page. Must be https. Default
                               https://github.com/<owner>/<repo>/releases/tag/v<version>.
  --legacy-feed <mode>         Required for a stable version. What 0.1.1 clients (per-user install) read at
                               bwc-stable.json: frozen = the signed 0.1.1 manifest re-signed with this release's expiry
                               (0.1.1 reads "up to date"), no-installer = this version with no installers (0.1.1 reads
                               "No update is available for this type of computer yet."), none = no file (0.1.1 shows a
                               download failure). A legacy file never lists an installer newer than 0.1.1. Beta: none.
  --legacy-source <file>       With frozen only. The published 0.1.1 bwc-stable.json; its .minisig must be beside it.
  --dry-run                    Allow a key that is not embedded (a test key). The output is labelled.
  --json                       Print one JSON line.
  --help                       Show this help.

Exit codes: 0 assembled and validated, 1 refused, 2 usage, 3 input missing, 4 external failure.`);
}

/** The default installer directory for a version: the GitHub release download page for v<version>. */
export function defaultDownloadBaseUrl(version) {
  return `https://github.com/${resolveRepo().slug}/releases/download/v${version}`;
}

/** The default notes page for a version: the GitHub release tag page. */
export function defaultNotesUrl(version) {
  return `https://github.com/${resolveRepo().slug}/releases/tag/v${version}`;
}

export function normalizePubDate(value) {
  if (!value) {
    const epoch = process.env.SOURCE_DATE_EPOCH;
    if (epoch !== undefined && /^\d+$/.test(epoch)) {
      return new Date(Number(epoch) * 1000).toISOString().replace(/\.\d{3}Z$/, "Z");
    }
    return new Date(Math.floor(Date.now() / 1000) * 1000).toISOString().replace(/\.\d{3}Z$/, "Z");
  }
  if (!PUBDATE_PATTERN.test(value)) {
    throw new Error("--pub-date must be a UTC ISO-8601 timestamp ending in Z");
  }
  if (Number.isNaN(new Date(value).getTime())) {
    throw new Error("--pub-date is not a real timestamp");
  }
  return value.replace(/\.\d{3}Z$/, "Z");
}

/** The signed expiry: the publish time plus the hours, as whole UTC seconds. */
export function expiresFor(pubDate, hours) {
  return new Date(Date.parse(pubDate) + hours * 3600 * 1000).toISOString().replace(/\.\d{3}Z$/, "Z");
}

/** The public key text for a secret key, built from its key id and public bytes. */
function publicKeyTextFor(keyId, publicBytes, label) {
  return (
    `untrusted comment: ${label} ${Buffer.from(keyId).reverse().toString("hex").toUpperCase()}\n` +
    `${Buffer.concat([Buffer.from("Ed"), keyId, publicBytes]).toString("base64")}\n`
  );
}

function fail(errors, reason, detail, exitCode = EXIT.REFUSED) {
  errors.push({ reason, detail, exitCode });
}

/**
 * The legacy mode for this run, or null after recording why not. Stable needs the option (the choice is the user's, at
 * release time, so there is no default). A beta never has a 0.1.1 feed: only none is accepted for it.
 */
function legacyModeFor(options, channel, errors) {
  const given = options.legacyFeed;
  if (channel === "beta") {
    if (given !== undefined && given !== "none") {
      fail(errors, "legacy-feed-beta", "a beta release never writes the 0.1.1 feed; use --legacy-feed none or leave it out", EXIT.USAGE);
      return null;
    }
    if (options.legacySource !== undefined) {
      fail(errors, "legacy-source-unused", "--legacy-source is only for --legacy-feed frozen", EXIT.USAGE);
      return null;
    }
    return "none";
  }
  if (given === undefined) {
    fail(
      errors,
      "legacy-feed-missing",
      `a stable release needs --legacy-feed <${LEGACY_MODES.join("|")}>: it decides what 0.1.1 clients see (docs/RELEASE.md)`,
      EXIT.USAGE,
    );
    return null;
  }
  if (!isLegacyMode(given)) {
    fail(errors, "legacy-feed-invalid", `--legacy-feed is ${JSON.stringify(given)}, expected ${LEGACY_MODES.join(", ")}`, EXIT.USAGE);
    return null;
  }
  if (given === "frozen") {
    if (!options.legacySource) {
      fail(errors, "legacy-source-missing", "--legacy-feed frozen needs --legacy-source <the published 0.1.1 bwc-stable.json>", EXIT.USAGE);
      return null;
    }
  } else if (options.legacySource !== undefined) {
    fail(errors, "legacy-source-unused", `--legacy-source is only for --legacy-feed frozen, not ${given}`, EXIT.USAGE);
    return null;
  }
  return given;
}

/** Reads the published 0.1.1 manifest and its signature and validates them. Returns the parsed manifest, or null. */
async function readLegacySource(file, trustedKeys, errors) {
  const manifestPath = path.resolve(file);
  const signaturePath = `${manifestPath}.minisig`;
  for (const [label, target] of [["manifest", manifestPath], ["signature", signaturePath]]) {
    if (!fs.existsSync(target) || !fs.statSync(target).isFile()) {
      fail(errors, "legacy-source-missing", `the 0.1.1 ${label} is missing: ${target}`, EXIT.MISSING);
      return null;
    }
  }
  const bytes = fs.readFileSync(manifestPath);
  const checked = await validateLegacySource(bytes, { signature: fs.readFileSync(signaturePath, "utf8"), trustedKeys });
  if (!checked.ok) {
    errors.push(...checked.errors);
    return null;
  }
  return { ...checked.manifest, sha256: await sha256FileStream(manifestPath) };
}

/**
 * Writes bwc-stable.json (the 0.1.1 feed) beside the v2 manifest, signed with this release's expiry, or removes a
 * leftover one for none. Returns the summary that assembly.json records.
 */
async function writeLegacyFeed({ mode, channelDir, keyText, password, publicText, version, pubDate, expires, source, errors, checks }) {
  const legacyPath = path.join(channelDir, LEGACY_MANIFEST_NAME);
  const sidecars = [legacyPath, `${legacyPath}.minisig`, `${legacyPath}.sig`];
  if (mode === "none") {
    for (const file of sidecars) fs.rmSync(file, { force: true });
    return { mode };
  }

  let manifest;
  try {
    manifest = buildLegacyManifest({ mode, version, pubDate, source });
  } catch (error) {
    fail(errors, String(error.message).split(":")[0], describeError(error));
    return { mode };
  }
  const bytes = serializeManifest(manifest);
  fs.writeFileSync(legacyPath, bytes);
  const signature = await signFile(
    legacyPath,
    keyText,
    manifestTrustedComment({ version: manifest.version, channel: "stable", expires }),
    { password },
  );
  fs.writeFileSync(`${legacyPath}.minisig`, signature, "utf8");
  fs.writeFileSync(`${legacyPath}.sig`, envelope(signature), "utf8");

  const validation = await validateLegacyManifest(bytes, {
    mode,
    version,
    signature,
    trustedKeys: [publicText],
    expires,
    source: source ?? undefined,
  });
  checks.push({ name: "legacy-feed", pass: validation.ok, detail: `${mode} ${manifest.version}` });
  errors.push(...validation.errors);
  return {
    mode,
    file: LEGACY_MANIFEST_NAME,
    version: manifest.version,
    installerArches: Object.keys(manifest.installers),
    ...(source ? { sourceSha256: source.sha256 } : {}),
  };
}

/**
 * Assembles and validates one channel's bundle. Returns { ok, errors, checks, ... }. Nothing is
 * published here; the caller decides what to do with the folder.
 */
export async function assembleBundle(options) {
  const errors = [];
  const checks = [];
  const result = { ok: false, errors, checks };

  let version;
  try {
    version = options.version;
    parsePackageVersion(version);
  } catch (error) {
    fail(errors, "version-invalid", describeError(error), EXIT.USAGE);
    return result;
  }
  const channel = channelForVersion(version);
  if (channel !== "stable" && channel !== "beta") {
    fail(errors, "channel-invalid", `${version} has no supported channel`, EXIT.USAGE);
    return result;
  }
  if (options.channel && options.channel !== channel) {
    fail(errors, "channel-mismatch", `version ${version} is ${channel}, not ${options.channel}`, EXIT.USAGE);
    return result;
  }

  const arches = Object.keys(options.installers ?? {});
  if (arches.length === 0) {
    fail(errors, "usage", "at least one installer is required", EXIT.USAGE);
    return result;
  }
  const hours = options.expiresHours ?? DEFAULT_EXPIRES_HOURS;
  if (!Number.isInteger(hours) || hours < 1 || hours > MAX_EXPIRES_HOURS) {
    fail(errors, "usage", `--expires-hours must be 1 to ${MAX_EXPIRES_HOURS}`, EXIT.USAGE);
    return result;
  }

  const legacyMode = legacyModeFor(options, channel, errors);
  if (legacyMode === null) {
    return result;
  }

  const pubDate = options.pubDate;
  const expires = expiresFor(pubDate, hours);
  const outRoot = path.resolve(options.out);
  const channelDir = path.join(outRoot, channel);
  const baseUrl = options.baseUrl;
  const keyText = options.keyText;

  if (typeof keyText !== "string" || keyText.length === 0) {
    fail(errors, "key-missing", "no secret key text was given", EXIT.USAGE);
    return result;
  }
  if (isPlaceholderKeyText(keyText)) {
    fail(errors, "placeholder-key", "the secret key is a placeholder");
    return result;
  }

  let parsedKey;
  try {
    parsedKey = parseSecretKeyText(keyText, { password: options.password });
  } catch (error) {
    fail(errors, error.reason ?? "malformed-secret-key", describeError(error), error.exitCode ?? EXIT.REFUSED);
    return result;
  }
  const publicText = publicKeyTextFor(parsedKey.keyId, parsedKey.publicBytes, options.dryRun ? "BWC DRY-RUN TEST KEY" : "BWC RELEASE KEY");
  const keyIdHex = Buffer.from(parsedKey.keyId).reverse().toString("hex").toUpperCase();
  const fingerprint = publicKeyFingerprint(publicText);

  // The embedded list decides whether this key may sign. A dry run may use a key outside it.
  const source = readTrustedKeysFile(options.root);
  let testKey = true;
  if (source.ok) {
    const matched = matchEmbeddedKey(publicText, source.embedded);
    testKey = !matched.ok;
    if (!matched.ok && !options.dryRun) {
      fail(errors, matched.reason, `${matched.detail}; the signing key must be one of the keys in ${source.file}`);
      return result;
    }
    if (matched.ok) checks.push({ name: "embedded-key", pass: true, detail: keyIdHex });
  } else if (!options.dryRun) {
    fail(errors, source.reason, source.detail ?? source.file, EXIT.MISSING);
    return result;
  }
  if (testKey) {
    checks.push({ name: "test-key", pass: true, detail: "not an embedded key; dry run only" });
  }

  // The frozen source is read and checked before any file is written, so a refused run leaves no bundle behind.
  let legacySource = null;
  if (legacyMode === "frozen") {
    const trusted = [...new Set([...(source.ok ? source.embedded : []), publicText])];
    legacySource = await readLegacySource(options.legacySource, trusted, errors);
    if (legacySource === null) {
      return result;
    }
  }

  fs.mkdirSync(channelDir, { recursive: true });
  const installers = {};
  for (const [architecture, sourcePath] of Object.entries(options.installers)) {
    const resolved = path.resolve(sourcePath);
    if (!fs.existsSync(resolved) || !fs.statSync(resolved).isFile()) {
      fail(errors, "installer-missing", `${architecture}: installer is missing: ${resolved}`, EXIT.MISSING);
      return result;
    }
    const fileName = path.basename(resolved);
    const target = path.join(channelDir, fileName);
    if (path.resolve(target) !== resolved) {
      fs.copyFileSync(resolved, target);
    }
    const signatureText = await signFile(target, keyText, installerTrustedComment({ version, architecture, channel }), {
      password: options.password,
    });
    fs.writeFileSync(`${target}.minisig`, signatureText, "utf8");
    fs.writeFileSync(`${target}.sig`, envelope(signatureText), "utf8");
    installers[architecture] = {
      file: target,
      fileName,
      url: `${baseUrl}/${encodeURIComponent(fileName)}`,
      size: fs.statSync(target).size,
      sha256: await sha256FileStream(target),
      signature: envelope(signatureText),
    };
  }

  const manifest = buildManifest({
    version,
    channel,
    pubDate,
    notesUrl: options.notesUrl,
    installers: Object.fromEntries(
      Object.entries(installers).map(([architecture, entry]) => [
        architecture,
        { url: entry.url, size: entry.size, sha256: entry.sha256, signature: entry.signature },
      ]),
    ),
  });
  const manifestBytes = serializeManifest(manifest);
  const manifestPath = path.join(channelDir, manifestFileName(channel));
  fs.writeFileSync(manifestPath, manifestBytes);
  const manifestSignature = await signFile(
    manifestPath,
    keyText,
    manifestTrustedComment({ version, channel, expires }),
    { password: options.password },
  );
  fs.writeFileSync(`${manifestPath}.minisig`, manifestSignature, "utf8");
  fs.writeFileSync(`${manifestPath}.sig`, envelope(manifestSignature), "utf8");

  const legacy = await writeLegacyFeed({
    mode: legacyMode,
    channelDir,
    keyText,
    password: options.password,
    publicText,
    version,
    pubDate,
    expires,
    source: legacySource,
    errors,
    checks,
  });

  // The bundle must pass the validator the updater's contract describes, with the signing key only.
  const validation = await validateManifestBuffer(manifestBytes, {
    signature: manifestSignature,
    trustedKeys: [publicText],
    channel,
    version,
    requireArches: arches,
    installers: Object.fromEntries(Object.entries(installers).map(([architecture, entry]) => [architecture, entry.file])),
  });
  checks.push(...validation.checks);
  if (!validation.ok) {
    errors.push(...validation.errors);
  }

  const summary = {
    schema: 1,
    tool: SCRIPT,
    version,
    channel,
    pubDate,
    expires,
    arches,
    key: { keyId: keyIdHex, fingerprint, testKey },
    dryRun: Boolean(options.dryRun),
    network: "none",
    manifest: relativeTo(REPO_ROOT, manifestPath),
    legacyFeed: legacy,
    installers: Object.fromEntries(
      Object.entries(installers).map(([architecture, entry]) => [
        architecture,
        { file: entry.fileName, size: entry.size, sha256: entry.sha256 },
      ]),
    ),
    ok: validation.ok && errors.length === 0,
  };
  writeJson(path.join(channelDir, "assembly.json"), summary);

  result.ok = errors.length === 0;
  result.channel = channel;
  result.version = version;
  result.expires = expires;
  result.outDir = channelDir;
  result.manifestPath = manifestPath;
  result.signaturePath = `${manifestPath}.minisig`;
  result.installers = installers;
  result.legacy = legacy;
  result.testKey = testKey;
  result.keyIdHex = keyIdHex;
  result.fingerprint = fingerprint;
  return result;
}

function parseOptions(argv) {
  const parsed = parseArguments(argv, {
    values: [
      "version",
      "channel",
      "root",
      "secret-key",
      "password-env",
      "pub-date",
      "expires-hours",
      "download-base-url",
      "notes-url",
      "out",
      "installer-win-x64",
      "installer-win-arm64",
      "legacy-feed",
      "legacy-source",
    ],
    flags: ["dry-run", "json", "help"],
  });
  if (parsed.has("help")) return { help: true };
  if (parsed.positionals.length > 0) throw new Error(`unexpected argument: ${parsed.positionals[0]}`);
  const version = parsed.get("version");
  if (!version) throw new Error("--version is required");
  try {
    parsePackageVersion(version);
  } catch (error) {
    throw new Error(describeError(error));
  }
  const installers = {};
  for (const [architecture, option] of RID_ARCH) {
    const value = parsed.get(option.slice(2));
    if (value !== undefined) installers[architecture] = value;
  }
  if (Object.keys(installers).length === 0) {
    throw new Error("at least one of --installer-win-x64 or --installer-win-arm64 is required");
  }
  const baseUrlOption = parsed.get("download-base-url");
  const baseUrl = validateUrl(baseUrlOption ?? defaultDownloadBaseUrl(version), { requireHttps: true });
  if (!baseUrl.ok) throw new Error(`--download-base-url is refused: ${baseUrl.detail}`);
  const notesOption = parsed.get("notes-url");
  const notesUrl = validateUrl(notesOption ?? defaultNotesUrl(version), { requireHttps: true });
  if (!notesUrl.ok) throw new Error(`--notes-url is refused: ${notesUrl.detail}`);
  const expiresText = parsed.get("expires-hours");
  const expiresHours = expiresText === undefined ? DEFAULT_EXPIRES_HOURS : Number(expiresText);
  if (!Number.isInteger(expiresHours)) throw new Error("--expires-hours must be a whole number of hours");
  return {
    version,
    channel: parsed.get("channel"),
    root: path.resolve(parsed.get("root") ?? REPO_ROOT),
    secretKey: parsed.get("secret-key"),
    passwordEnv: parsed.get("password-env"),
    pubDate: normalizePubDate(parsed.get("pub-date")),
    expiresHours,
    baseUrl: baseUrl.url.toString().replace(/\/+$/, ""),
    notesUrl: notesUrl.url.toString(),
    out: path.resolve(REPO_ROOT, parsed.get("out") ?? path.join("artifacts", "release", version)),
    installers,
    legacyFeed: parsed.get("legacy-feed"),
    legacySource: parsed.get("legacy-source"),
    dryRun: parsed.has("dry-run"),
    json: parsed.has("json"),
  };
}

async function main(argv = process.argv.slice(2)) {
  if (passwordArgumentGiven(argv)) {
    console.error(`${SCRIPT}: REFUSED [password-argument]: passwords are never command-line arguments; use --password-env <NAME>`);
    return EXIT.USAGE;
  }
  let options;
  try {
    options = parseOptions(argv);
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }
  if (options.help) {
    printHelp();
    return EXIT.OK;
  }

  // The key and password are resolved here, never from argv text. Refusals print no key material.
  const key = resolveSigningKey({ keyPath: options.secretKey });
  if (!key.ok) {
    console.error(`${SCRIPT}: REFUSED [${key.reason}]: ${key.detail}`);
    return key.exitCode;
  }
  const passwordResult = await resolveKeyPassword({
    text: key.text,
    source: key.source,
    passwordEnv: options.passwordEnv,
  });
  if (!passwordResult.ok) {
    console.error(`${SCRIPT}: REFUSED [${passwordResult.reason}]: ${passwordResult.detail}`);
    return EXIT.USAGE;
  }

  let result;
  try {
    result = await assembleBundle({ ...options, keyText: key.text, password: passwordResult.password });
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    return EXIT.EXTERNAL;
  }
  if (!result.ok) {
    printRefusals(SCRIPT, result.errors);
    return exitCodeForErrors(result.errors);
  }
  if (options.json) {
    console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: true, channel: result.channel, outDir: relativeTo(REPO_ROOT, result.outDir), testKey: result.testKey }));
  } else {
    console.log(`${SCRIPT}: PASS ${result.version} (${result.channel}) signed by ${result.keyIdHex}${result.testKey ? " (TEST KEY, dry run)" : ""}`);
    console.log(`  manifest: ${relativeTo(REPO_ROOT, result.manifestPath)}`);
    console.log(`  legacy feed (0.1.1 clients): ${result.legacy.mode}${result.legacy.file ? ` (${result.legacy.file}, version ${result.legacy.version})` : ""}`);
    console.log(`  expires: ${result.expires}`);
    for (const architecture of Object.keys(result.installers)) {
      console.log(`  ${architecture}: ${result.installers[architecture].fileName} (${result.installers[architecture].size} bytes)`);
    }
  }
  return EXIT.OK;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = await main();
}
