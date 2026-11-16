import { decodeCanonicalBase64, parseStrictJson } from "./strict-json.js";
import { EXIT } from "./cli.js";
import { parseTrustedComment, verifyBuffer } from "./minisign.js";
import { compareSemVer, tryParseSemVer } from "./semver.js";
import { KNOWN_ARCHES, manifestTrustedComment, MAX_INSTALLER_BYTES, MAX_MANIFEST_BYTES } from "./update-manifest.js";
import { validateUrl } from "./url-policy.js";

/**
 * The feed that 0.1.1 clients read (docs/failure-modes/release.md, section LF). 0.1.1 installs per user and reads
 * releases/latest/download/bwc-stable.json. 0.1.2 installs per machine and reads bwc-stable-v2.json, so a 0.1.1 client
 * must never be shown an installer for a newer version: its in-app update would run a per-machine setup unelevated.
 * Each stable release says what 0.1.1 sees, with --legacy-feed:
 *
 *   frozen        the signed 0.1.1 manifest, re-signed with a fresh expiry. 0.1.1 reads "up to date".
 *   no-installer  schema 1, the new version, an empty installers map. 0.1.1 reads "No update is available for this
 *                 type of computer yet."
 *   none          no legacy file. GitHub answers 404 and 0.1.1 shows a download failure.
 *
 * The legacy manifest keeps schema 1, which the 0.1.1 parser reads. Its validator differs from the current one in two
 * ways: an empty installers map is valid for no-installer, and an installer signature comment may lack product and
 * channel (0.1.1 did not write them).
 */
export const LEGACY_MANIFEST_NAME = "bwc-stable.json";
export const LAST_PER_USER_VERSION = "0.1.1";
export const LEGACY_MODES = Object.freeze(["frozen", "no-installer", "none"]);

const SHA256_PATTERN = /^[0-9a-f]{64}$/;
const EXPIRES_PATTERN = /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$/;
const DOWNLOAD_VERSION = /\/releases\/download\/v([^/]+)\//;

export function isLegacyMode(value) {
  return LEGACY_MODES.includes(value);
}

function newerThanLastPerUser(version) {
  return typeof version === "string" && tryParseSemVer(version) !== null && compareSemVer(version, LAST_PER_USER_VERSION) > 0;
}

/**
 * True when an installer entry points at a release newer than 0.1.1, by its URL (the release tag directory or a
 * version in the file name). A legacy manifest never lists one.
 */
function entryIsNewer(entry) {
  const url = typeof entry?.url === "string" ? entry.url : "";
  const tag = DOWNLOAD_VERSION.exec(url);
  if (tag && newerThanLastPerUser(tag[1])) return true;
  let name = url.split("?")[0];
  try {
    name = decodeURIComponent(name);
  } catch {
    // An undecodable URL is matched as written.
  }
  const named = /BetterWindowsConfig-([0-9A-Za-z.+-]+?)-win-(?:x64|arm64)-setup\.exe/.exec(name);
  return Boolean(named && newerThanLastPerUser(named[1]));
}

function entryProblems(architecture, entry) {
  const problems = [];
  if (!entry || typeof entry !== "object" || Array.isArray(entry)) {
    return [`${architecture}: entry must be an object`];
  }
  if (typeof entry.url !== "string" || !validateUrl(entry.url, { requireHttps: true }).ok) {
    problems.push(`${architecture}: url must be an https URL`);
  }
  if (!Number.isInteger(entry.size) || entry.size < 1 || entry.size > MAX_INSTALLER_BYTES) {
    problems.push(`${architecture}: size must be an integer from 1 to 1 GiB`);
  }
  if (typeof entry.sha256 !== "string" || !SHA256_PATTERN.test(entry.sha256)) {
    problems.push(`${architecture}: sha256 must be 64 lowercase hex characters`);
  }
  const decoded = typeof entry.signature === "string" ? decodeCanonicalBase64(entry.signature) : null;
  if (!decoded || !decoded.toString("utf8").startsWith("untrusted comment:")) {
    problems.push(`${architecture}: signature must be a canonical base64 minisign envelope`);
  }
  return problems;
}

/**
 * The legacy manifest object for a mode. no-installer: the release version and no installers. frozen: the 0.1.1 source
 * manifest's own fields. Throws with "legacy-newer-installer" when a frozen source lists an installer for a newer
 * version, and never builds anything for none.
 */
export function buildLegacyManifest({ mode, version, pubDate, source }) {
  if (mode === "no-installer") {
    return { schema: 1, version, channel: "stable", pubDate, installers: {} };
  }
  if (mode === "frozen") {
    if (!source || typeof source !== "object") {
      throw new Error("legacy-source-missing: frozen needs the signed 0.1.1 manifest");
    }
    if (newerThanLastPerUser(source.version) || Object.values(source.installers ?? {}).some(entryIsNewer)) {
      throw new Error("legacy-newer-installer: a legacy manifest never lists an installer for a version above 0.1.1");
    }
    const manifest = {
      schema: 1,
      version: source.version,
      channel: "stable",
      pubDate: source.pubDate,
      installers: source.installers,
    };
    if (source.notesUrl) manifest.notesUrl = source.notesUrl;
    return manifest;
  }
  throw new Error(`no legacy manifest for mode ${String(mode)}`);
}

function sameJson(left, right) {
  return JSON.stringify(left) === JSON.stringify(right);
}

function checkSignature(bytes, signature, trustedKeys, add, { now, enforceExpiry, expectedExpires, version }) {
  if (!signature) {
    add("legacy-signature-missing", "the legacy manifest has no signature");
    return;
  }
  const verified = verifyBuffer(bytes, signature, trustedKeys);
  if (!verified.ok) {
    add(verified.reason, verified.detail);
    return;
  }
  const comment = parseTrustedComment(verified.trustedComment);
  if (!comment.ok) {
    add("trusted-comment-invalid", comment.detail);
    return;
  }
  const signedVersion = comment.fields.get("version");
  const signedChannel = comment.fields.get("channel");
  const signedExpires = comment.fields.get("expires");
  if (signedVersion !== version) {
    add("version-mismatch", `the signed version ${String(signedVersion)} is not ${version}`);
  }
  if (signedChannel !== "stable") {
    add("channel-mismatch", `the signed channel ${String(signedChannel)} is not stable`);
  }
  if (!signedExpires || !EXPIRES_PATTERN.test(signedExpires) || Number.isNaN(Date.parse(signedExpires))) {
    add("trusted-comment-invalid", "the signed comment has no valid expires field");
    return;
  }
  if (enforceExpiry && now.getTime() > Date.parse(signedExpires)) {
    add("expired", `the signed expiry ${signedExpires} has passed`);
  }
  if (expectedExpires && signedExpires !== expectedExpires) {
    add("legacy-expiry-mismatch", `the legacy expiry ${signedExpires} is not the release expiry ${expectedExpires}`);
  }
  if (verified.trustedComment !== manifestTrustedComment({ version, channel: "stable", expires: signedExpires })) {
    add("trusted-comment-invalid", "the signed comment must be exactly version, channel, and expires in that order");
  }
}

/**
 * Validates a legacy manifest. mode is frozen or no-installer; version is the release version (no-installer reads it).
 * source, when given, is the parsed 0.1.1 manifest a frozen file must carry unchanged. Returns { ok, errors, manifest }.
 */
export async function validateLegacyManifest(bytes, options = {}) {
  const errors = [];
  const add = (reason, detail, exitCode = EXIT.REFUSED) => errors.push({ reason, detail, exitCode });
  const now = options.now instanceof Date ? options.now : new Date();
  const mode = options.mode;
  if (mode !== "frozen" && mode !== "no-installer") {
    add("legacy-feed-invalid", `no legacy manifest is validated for mode ${String(mode)}`, EXIT.USAGE);
    return { ok: false, errors, manifest: null };
  }

  const parsed = parseStrictJson(bytes, { maxBytes: MAX_MANIFEST_BYTES });
  if (!parsed.ok) {
    add(parsed.reason, parsed.detail);
    return { ok: false, errors, manifest: null };
  }
  const manifest = parsed.value;
  if (!manifest || typeof manifest !== "object" || Array.isArray(manifest)) {
    add("manifest-invalid", "manifest root must be an object");
    return { ok: false, errors, manifest: null };
  }

  if (manifest.schema !== 1) {
    add("unsupported-schema", "a legacy manifest keeps schema 1, the only one 0.1.1 reads");
  }
  if (manifest.channel !== "stable") {
    add("channel-mismatch", "a legacy manifest is the stable channel");
  }
  if (typeof manifest.pubDate !== "string" || Number.isNaN(Date.parse(manifest.pubDate))) {
    add("pubdate-invalid", "pubDate must be a UTC timestamp");
  }

  const expectedVersion = mode === "frozen" ? LAST_PER_USER_VERSION : options.version;
  if (manifest.version !== expectedVersion) {
    add("version-mismatch", `the ${mode} manifest version is ${JSON.stringify(manifest.version)}, expected ${expectedVersion}`);
  }

  const installers = manifest.installers;
  if (!installers || typeof installers !== "object" || Array.isArray(installers)) {
    add("manifest-invalid", "installers must be an object");
  } else {
    const names = Object.keys(installers);
    for (const name of names) {
      if (!KNOWN_ARCHES.includes(name)) add("manifest-invalid", `unknown installer architecture ${name}`);
    }
    if (mode === "no-installer") {
      if (names.length > 0) {
        add(
          newerThanLastPerUser(manifest.version) ? "legacy-newer-installer" : "manifest-invalid",
          "a no-installer legacy manifest has an empty installers map",
        );
      }
    } else {
      if (names.length === 0) add("manifest-invalid", "a frozen legacy manifest has the 0.1.1 installer entries");
      for (const architecture of KNOWN_ARCHES) {
        if (Object.prototype.hasOwnProperty.call(installers, architecture)) {
          for (const problem of entryProblems(architecture, installers[architecture])) add("manifest-invalid", problem);
        }
      }
      if (options.source && !sameJson(installers, options.source.installers)) {
        add("legacy-frozen-mismatch", "the installer entries differ from the signed 0.1.1 manifest");
      }
    }
    // The invariant of the whole feed: nothing for a version above 0.1.1 is ever offered to a 0.1.1 client.
    if (names.length > 0 && (newerThanLastPerUser(manifest.version) || Object.values(installers).some(entryIsNewer))) {
      if (!errors.some((error) => error.reason === "legacy-newer-installer")) {
        add("legacy-newer-installer", "a legacy manifest never lists an installer for a version above 0.1.1");
      }
    }
  }

  checkSignature(bytes, options.signature, options.trustedKeys ?? [], add, {
    now,
    enforceExpiry: options.enforceExpiry !== false,
    expectedExpires: options.expires,
    version: manifest.version,
  });

  return { ok: errors.length === 0, errors, manifest };
}

/**
 * Validates the signed 0.1.1 manifest that frozen mode copies. It must be the stable 0.1.1 manifest with at least one
 * well-formed installer entry, signed by an embedded key. Its own expiry may have passed (the frozen file is re-signed).
 * Every problem has the reason legacy-source-invalid, so the release names one thing to fix.
 */
export async function validateLegacySource(bytes, { signature, trustedKeys }) {
  const inner = await validateLegacyManifest(bytes, {
    mode: "frozen",
    signature,
    trustedKeys,
    enforceExpiry: false,
  });
  const errors = inner.errors.map((error) => ({
    reason: "legacy-source-invalid",
    detail: `${error.reason}: ${error.detail}`,
    exitCode: EXIT.REFUSED,
  }));
  return { ok: errors.length === 0, errors, manifest: inner.ok ? inner.manifest : null };
}
