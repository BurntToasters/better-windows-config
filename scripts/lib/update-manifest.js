import fs from "node:fs";

import { EXIT, sha256FileStream } from "./cli.js";
import { verifyBuffer, verifyFile, parseTrustedComment, isPlainFileName } from "./minisign.js";
import { compareSemVer, isPrerelease, tryParseSemVer } from "./semver.js";
import { parseStrictJson, decodeCanonicalBase64 } from "./strict-json.js";
import { validateUrl } from "./url-policy.js";

export const KNOWN_ARCHES = Object.freeze(["win-x64", "win-arm64"]);
export const MAX_MANIFEST_BYTES = 256 * 1024;
export const MAX_INSTALLER_BYTES = 1024 * 1024 * 1024;
export const MAX_FUTURE_SKEW_HOURS = 24;
const SHA256_PATTERN = /^[0-9a-f]{64}$/;
const PUBDATE_PATTERN = /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{3})?Z$/;

export function buildManifest({ version, channel, pubDate, notesUrl, installers }) {
  const manifest = {
    schema: 1,
    version,
    channel,
    pubDate,
    installers,
  };
  if (notesUrl) {
    manifest.notesUrl = notesUrl;
  }
  return manifest;
}

export function serializeManifest(manifest) {
  return Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`, "utf8");
}

function makeResult() {
  const errors = [];
  const checks = [];
  return {
    errors,
    checks,
    add(reason, detail, exitCode = EXIT.REFUSED) {
      errors.push({ reason, detail, exitCode });
    },
    check(name, pass, detail = "") {
      checks.push({ name, pass, detail });
    },
  };
}

function stringField(object, name) {
  const value = object[name];
  return typeof value === "string" ? value : null;
}

export async function validateManifestBuffer(bytes, options = {}) {
  const result = makeResult();
  const maxBytes = options.maxManifestBytes ?? MAX_MANIFEST_BYTES;
  const now = options.now instanceof Date ? options.now : new Date();

  const parsed = parseStrictJson(bytes, { maxBytes });
  if (!parsed.ok) {
    result.add(parsed.reason, parsed.detail);
    result.check("manifest-json", false, parsed.detail);
    return { ok: false, errors: result.errors, checks: result.checks, manifest: null, manifestBytes: bytes };
  }
  result.check("manifest-json", true);

  const manifest = parsed.value;
  if (!manifest || typeof manifest !== "object" || Array.isArray(manifest)) {
    result.add("manifest-invalid", "manifest root must be an object");
    return { ok: false, errors: result.errors, checks: result.checks, manifest: null, manifestBytes: bytes };
  }

  if (!Number.isInteger(manifest.schema)) {
    result.add("manifest-invalid", "schema must be an integer");
  } else if (manifest.schema !== 1) {
    result.add("unsupported-schema", `schema ${manifest.schema} is not supported`);
  }
  result.check("schema", manifest.schema === 1);

  const versionText = stringField(manifest, "version");
  const version = versionText ? tryParseSemVer(versionText) : null;
  if (!version) {
    result.add("version-invalid", "version must be strict SemVer without a leading v");
  }
  result.check("version", Boolean(version));

  const channelText = stringField(manifest, "channel");
  const channelValid = channelText === "stable" || channelText === "beta";
  if (!channelValid) {
    result.add("channel-invalid", "channel must be stable or beta");
  }
  result.check("channel", channelValid);

  let pubDate = null;
  const pubDateText = stringField(manifest, "pubDate");
  if (!pubDateText || !PUBDATE_PATTERN.test(pubDateText)) {
    result.add("pubdate-invalid", "pubDate must be a UTC ISO-8601 timestamp ending in Z");
  } else {
    pubDate = new Date(pubDateText);
    const expected = pubDateText.includes(".") ? pubDateText : pubDateText.replace(/Z$/, ".000Z");
    if (Number.isNaN(pubDate.getTime()) || pubDate.toISOString() !== expected) {
      result.add("pubdate-invalid", "pubDate is not a real normalized UTC timestamp");
      pubDate = null;
    }
  }
  if (pubDate) {
    const futureLimit = new Date(now.getTime() + MAX_FUTURE_SKEW_HOURS * 60 * 60 * 1000);
    if (pubDate > futureLimit) {
      result.add("pubdate-future", "pubDate is further in the future than 24 hours");
    } else if (options.maxAgeHours !== undefined && options.maxAgeHours !== null) {
      const ageHours = (now.getTime() - pubDate.getTime()) / (60 * 60 * 1000);
      if (ageHours > options.maxAgeHours) {
        result.add(
          "expired",
          `pubDate is ${ageHours.toFixed(1)} hours old, over the ${options.maxAgeHours} hour limit`,
        );
      }
    }
  }
  result.check("pubdate", Boolean(pubDate));

  if (manifest.notesUrl !== undefined) {
    if (typeof manifest.notesUrl !== "string") {
      result.add("manifest-invalid", "notesUrl must be a string");
    } else {
      const notes = validateUrl(manifest.notesUrl, { requireHttps: true });
      if (!notes.ok) {
        result.add("url-rejected", `notesUrl: ${notes.detail}`);
      }
    }
  }

  if (!manifest.installers || typeof manifest.installers !== "object" || Array.isArray(manifest.installers)) {
    result.add("manifest-invalid", "installers must be an object");
  }

  const entries = new Map();
  if (manifest.installers && typeof manifest.installers === "object" && !Array.isArray(manifest.installers)) {
    for (const architecture of KNOWN_ARCHES) {
      if (!Object.prototype.hasOwnProperty.call(manifest.installers, architecture)) continue;
      const entry = manifest.installers[architecture];
      if (!entry || typeof entry !== "object" || Array.isArray(entry)) {
        result.add("manifest-invalid", `${architecture}: entry must be an object`);
        continue;
      }

      const url = stringField(entry, "url");
      if (!url) {
        result.add("manifest-invalid", `${architecture}: url must be a string`);
      } else {
        const checkedUrl = validateUrl(url, { requireHttps: true });
        if (!checkedUrl.ok) {
          result.add("url-rejected", `${architecture}: ${checkedUrl.detail}`);
        }
      }

      if (!Number.isInteger(entry.size) || entry.size < 1 || entry.size > MAX_INSTALLER_BYTES) {
        result.add("manifest-invalid", `${architecture}: size must be an integer from 1 to 1 GiB`);
      }

      if (typeof entry.sha256 !== "string" || !SHA256_PATTERN.test(entry.sha256)) {
        result.add("manifest-invalid", `${architecture}: sha256 must be 64 lowercase hex characters`);
      }

      if (typeof entry.signature !== "string") {
        result.add("signature-format", `${architecture}: signature must be a base64 minisign envelope`);
      } else {
        const decoded = decodeCanonicalBase64(entry.signature);
        if (!decoded || !decoded.toString("utf8").startsWith("untrusted comment:")) {
          result.add("signature-format", `${architecture}: signature must be a canonical base64 minisign envelope`);
        }
      }

      entries.set(architecture, entry);
    }
  }

  if (entries.size === 0) {
    result.add("manifest-invalid", "manifest has no known installer entries");
  }
  result.check("installers", entries.size > 0);

  if (options.version && versionText && versionText !== options.version) {
    result.add("version-mismatch", `manifest version ${JSON.stringify(versionText)} is not ${options.version}`);
  }
  result.check("expected-version", !options.version || versionText === options.version);

  if (options.channel && channelText && channelText !== options.channel) {
    result.add("channel-mismatch", `manifest channel ${JSON.stringify(channelText)} is not ${options.channel}`);
  }
  if (channelText === "stable" && versionText && isPrerelease(versionText)) {
    result.add("prerelease-on-stable", "a stable channel never offers a prerelease");
  }
  result.check("expected-channel", !options.channel || channelText === options.channel);

  if (options.currentVersion) {
    if (!tryParseSemVer(options.currentVersion)) {
      result.add("version-invalid", `--current-version is not SemVer: ${options.currentVersion}`, EXIT.USAGE);
    } else if (version) {
      const order = compareSemVer(versionText, options.currentVersion);
      if (order === 0) {
        result.add("same-version", "the offered version is the installed version");
      } else if (order < 0) {
        result.add("downgrade", "the offered version is older than the installed version");
      }
    }
  }

  for (const architecture of options.requireArches ?? []) {
    if (!entries.has(architecture)) {
      result.add("missing-architecture", `manifest has no installer for ${architecture}`);
    }
  }

  const trustedKeys = options.trustedKeys ?? [];
  if (options.signatureRequired !== false && !options.signature) {
    result.add("signature-missing", "manifest signature is required");
  }

  if (options.signature) {
    if (trustedKeys.length === 0) {
      result.add("trusted-key-missing", "a trusted public key is required with a signature");
    } else {
      const verified = verifyBuffer(bytes, options.signature, trustedKeys);
      if (!verified.ok) {
        result.add(verified.reason, verified.detail);
        result.check("manifest-signature", false, verified.detail);
      } else {
        result.check("manifest-signature", true);
        const comment = parseTrustedComment(verified.trustedComment);
        if (!comment.ok) {
          result.add("trusted-comment-invalid", comment.detail);
        } else {
          const signedVersion = comment.fields.get("version");
          if (!signedVersion) {
            result.add("trusted-comment-invalid", "manifest trusted comment has no version field");
          } else if (versionText && signedVersion !== versionText) {
            result.add("version-mismatch", "signed trusted comment version does not match the manifest version");
          }
        }
      }
    }
  }

  const installerChecks = options.installers ?? {};
  for (const [architecture, filePath] of Object.entries(installerChecks)) {
    if (!KNOWN_ARCHES.includes(architecture)) {
      result.add("wrong-arch", `unknown installer architecture ${architecture}`);
      continue;
    }
    const entry = entries.get(architecture);
    if (!entry) {
      result.add("missing-architecture", `manifest has no installer entry for ${architecture}`);
      continue;
    }
    if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
      result.add("installer-missing", `${architecture}: installer file is missing: ${filePath}`, EXIT.MISSING);
      result.check(`installer-${architecture}`, false, "missing");
      continue;
    }

    const size = fs.statSync(filePath).size;
    if (size !== entry.size) {
      result.add("installer-size-mismatch", `${architecture}: installer is ${size} bytes, manifest says ${entry.size}`);
    }

    const digest = await sha256FileStream(filePath);
    if (digest !== entry.sha256) {
      result.add("installer-hash-mismatch", `${architecture}: installer SHA-256 does not match the manifest`);
    }

    if (options.verifyInstallerSignatures === false) {
      result.check(`installer-${architecture}`, true, "signature skipped by --structure-only");
      continue;
    }

    if (trustedKeys.length === 0) {
      result.add("trusted-key-missing", `${architecture}: a trusted public key is required to verify the installer`);
      continue;
    }

    const signed = await verifyFile(filePath, entry.signature, trustedKeys);
    if (!signed.ok) {
      const reason = signed.reason === "signature-invalid" ? "installer-signature-invalid" : signed.reason;
      result.add(reason, `${architecture}: ${signed.detail}`);
      result.check(`installer-${architecture}`, false, signed.detail);
      continue;
    }

    const comment = parseTrustedComment(signed.trustedComment);
    if (!comment.ok) {
      result.add("trusted-comment-invalid", `${architecture}: ${comment.detail}`);
      continue;
    }
    const signedVersion = comment.fields.get("version");
    const signedArch = comment.fields.get("arch");
    const signedFile = comment.fields.get("file");
    if (!signedVersion) {
      result.add("trusted-comment-invalid", `${architecture}: installer trusted comment has no version field`);
    } else if (versionText && signedVersion !== versionText) {
      result.add("installer-version-mismatch", `${architecture}: signed version does not match the manifest`);
    }
    if (!signedArch) {
      result.add("trusted-comment-invalid", `${architecture}: installer trusted comment has no arch field`);
    } else if (signedArch !== architecture) {
      result.add("wrong-arch", `${architecture}: installer signed for ${signedArch}`);
    }
    if (signedFile && !isPlainFileName(signedFile)) {
      result.add("trusted-comment-invalid", `${architecture}: signed file name is not a plain file name`);
    }
    result.check(`installer-${architecture}`, true);
  }

  return {
    ok: result.errors.length === 0,
    errors: result.errors,
    checks: result.checks,
    manifest,
    manifestBytes: bytes,
    channel: channelText,
    version: versionText,
  };
}

export function readManifestFile(filePath, options = {}) {
  if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
    return {
      ok: false,
      reason: "manifest-missing",
      detail: `manifest file is missing: ${filePath}`,
    };
  }
  return { ok: true, bytes: fs.readFileSync(filePath) };
}
