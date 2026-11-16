import fs from "node:fs";
import path from "node:path";

import { EXIT, sha256FileStream } from "./cli.js";
import {
  isPlaceholderKeyText,
  loadPublicKeys,
  parsePublicKeyText,
} from "./minisign.js";
import { isLegacyMode, LEGACY_MANIFEST_NAME, validateLegacyManifest } from "./legacy-feed.js";
import { matchEmbeddedKey } from "./trusted-keys.js";
import { validateUrl } from "./url-policy.js";
import { KNOWN_ARCHES, manifestFileName, validateManifestBuffer } from "./update-manifest.js";

export function installerFilesFromManifest(directory, manifest) {
  const files = {};
  const errors = [];
  for (const architecture of KNOWN_ARCHES) {
    const entry = manifest?.installers?.[architecture];
    if (!entry || typeof entry.url !== "string") continue;
    const validated = validateUrl(entry.url, { requireHttps: true });
    if (!validated.ok) {
      errors.push({ reason: "url-rejected", detail: `${architecture}: ${validated.detail}`, exitCode: EXIT.REFUSED });
      continue;
    }
    let name = "";
    try {
      name = decodeURIComponent(new URL(entry.url).pathname.split("/").filter(Boolean).at(-1) ?? "");
    } catch {
      errors.push({ reason: "url-rejected", detail: `${architecture}: URL has no file name`, exitCode: EXIT.REFUSED });
      continue;
    }
    if (!name || name === "." || name === ".." || /[\\/]/.test(name)) {
      errors.push({ reason: "url-rejected", detail: `${architecture}: URL has an unsafe file name`, exitCode: EXIT.REFUSED });
      continue;
    }
    files[architecture] = path.join(directory, name);
  }
  return { files, errors };
}

/**
 * Describes each trusted key. A key is a test key when it is not one of the embedded keys. Without
 * the embedded list every key counts as a test key, so the caller refuses it for a release.
 */
function keySummary(loadedPaths, texts, embeddedTexts) {
  return texts.map((text, index) => {
    const parsed = parsePublicKeyText(text);
    return {
      path: loadedPaths[index] ?? null,
      keyId: parsed.ok ? parsed.keyIdHex : null,
      fingerprint: parsed.ok ? parsed.fingerprint : null,
      testKey: !Array.isArray(embeddedTexts) || !matchEmbeddedKey(text, embeddedTexts).ok,
      placeholder: isPlaceholderKeyText(text),
      ok: parsed.ok,
      detail: parsed.ok ? "" : parsed.detail,
    };
  });
}

/**
 * The 0.1.1 feed of a stable bundle (release.md, section LF). assembly.json records which mode the release chose. The
 * files must agree with it: none has no bwc-stable.json, the other two have it and its signature, and the manifest
 * passes the legacy validator (which refuses any installer for a version above 0.1.1). A beta has no legacy feed.
 */
async function checkLegacyFeed({ artifactDir, channel, version, trustedTexts, errors }) {
  const manifestPath = path.join(artifactDir, LEGACY_MANIFEST_NAME);
  const signaturePath = `${manifestPath}.minisig`;
  const none = { mode: "none", manifestPath, signaturePath, assets: [] };
  if (channel !== "stable") return none;

  let mode;
  try {
    mode = JSON.parse(fs.readFileSync(path.join(artifactDir, "assembly.json"), "utf8"))?.legacyFeed?.mode;
  } catch {
    mode = undefined;
  }
  if (!isLegacyMode(mode)) {
    errors.push({
      reason: "legacy-feed-missing",
      detail: "assembly.json does not record a legacy feed mode; assemble the stable bundle with --legacy-feed <frozen|no-installer|none>",
      exitCode: EXIT.REFUSED,
    });
    return { ...none, mode: null };
  }

  const manifestThere = fs.existsSync(manifestPath) && fs.statSync(manifestPath).isFile();
  if (mode === "none") {
    if (manifestThere) {
      errors.push({
        reason: "legacy-feed-invalid",
        detail: `assembly.json says the legacy feed is none, but ${LEGACY_MANIFEST_NAME} is in the folder`,
        exitCode: EXIT.REFUSED,
      });
    }
    return none;
  }

  const signatureThere = fs.existsSync(signaturePath) && fs.statSync(signaturePath).isFile();
  for (const [there, file] of [[manifestThere, manifestPath], [signatureThere, signaturePath]]) {
    if (!there) {
      errors.push({ reason: "asset-missing", detail: `legacy feed file is missing: ${path.basename(file)}`, exitCode: EXIT.MISSING });
    }
  }
  if (!manifestThere || !signatureThere) return { ...none, mode };

  if (trustedTexts.length > 0) {
    const validation = await validateLegacyManifest(fs.readFileSync(manifestPath), {
      mode,
      version,
      signature: fs.readFileSync(signaturePath, "utf8"),
      trustedKeys: trustedTexts,
    });
    errors.push(...validation.errors);
  }
  return { mode, manifestPath, signaturePath, assets: [path.basename(manifestPath), path.basename(signaturePath)] };
}

/**
 * The asset names a release of this bundle may hold: the manifest and its signature, the legacy pair only when the legacy
 * mode has one, and the setups (release.md LF17). Nothing else is expected, so nothing else is uploaded.
 */
export function expectedAssetNames(bundle) {
  return [
    path.basename(bundle.manifestPath),
    path.basename(bundle.signaturePath),
    ...(bundle.legacy?.assets ?? []),
    ...bundle.installers.map((installer) => installer.fileName),
  ];
}

/**
 * The assets of a release that are outside the expected set (LF17). A GPG signature `<expected name>.asc` is a sidecar of
 * an expected asset and is allowed. Names compare exactly: a stale `bwc-stable.json` offers a 0.1.2 installer to 0.1.1 when
 * releases/latest/download serves it, so it is never ignored.
 */
export function unexpectedAssets(release, expectedNames) {
  const allowed = new Set(expectedNames);
  return (release?.assets ?? []).filter((asset) => {
    const name = asset?.name;
    if (typeof name !== "string") return true;
    if (allowed.has(name)) return false;
    return !(name.endsWith(".asc") && allowed.has(name.slice(0, -4)));
  });
}

/**
 * The assets of a reused draft that are the legacy manifest or its signature while this bundle has no legacy feed (LF18).
 * Only these are deleted by the draft step. Any other unexpected asset is refused and left for the user.
 */
export function staleLegacyAssets(release, bundle) {
  if ((bundle.legacy?.assets ?? []).length > 0) return [];
  const stale = new Set([LEGACY_MANIFEST_NAME, `${LEGACY_MANIFEST_NAME}.minisig`]);
  return (release?.assets ?? []).filter((asset) => stale.has(asset?.name));
}

/**
 * Loads a D5 artifact folder and validates it with the D4 validator. It never contacts
 * the network. The caller decides whether a dry-run test key or stand-in installer is
 * acceptable for the operation.
 */
export async function loadReleaseBundle(options) {
  const artifactDir = path.resolve(options.dir);
  const errors = [];
  const checks = [];
  const manifestPath = path.join(artifactDir, manifestFileName(options.channel));
  const signaturePath = `${manifestPath}.minisig`;

  if (!fs.existsSync(artifactDir) || !fs.statSync(artifactDir).isDirectory()) {
    return {
      ok: false,
      errors: [{ reason: "missing-artifact", detail: `artifact directory is missing: ${artifactDir}`, exitCode: EXIT.MISSING }],
      checks,
      artifactDir,
      manifestPath,
      signaturePath,
      manifest: null,
      installers: [],
      trustedKeys: [],
      dryRun: false,
      testKey: false,
    };
  }
  if (!fs.existsSync(manifestPath) || !fs.statSync(manifestPath).isFile()) {
    return {
      ok: false,
      errors: [{ reason: "missing-artifact", detail: `manifest is missing: ${manifestPath}`, exitCode: EXIT.MISSING }],
      checks,
      artifactDir,
      manifestPath,
      signaturePath,
      manifest: null,
      installers: [],
      trustedKeys: [],
      dryRun: false,
      testKey: false,
    };
  }
  if (!fs.existsSync(signaturePath) || !fs.statSync(signaturePath).isFile()) {
    return {
      ok: false,
      errors: [{ reason: "signature-missing", detail: `manifest signature is missing: ${signaturePath}`, exitCode: EXIT.MISSING }],
      checks,
      artifactDir,
      manifestPath,
      signaturePath,
      manifest: null,
      installers: [],
      trustedKeys: [],
      dryRun: false,
      testKey: false,
    };
  }

  const trustedKeyValues = options.trustedKeys ?? [];
  let trustedTexts = [];
  let trustedPaths = [];
  if (trustedKeyValues.length === 0) {
    errors.push({
      reason: "trusted-key-missing",
      detail: "no trusted key: pass --trusted-key, or embed the release key in TrustedKeys.cs (docs/RELEASE.md, User-owned steps)",
      exitCode: EXIT.REFUSED,
    });
  } else {
    const loaded = loadPublicKeys(trustedKeyValues);
    if (!loaded.ok) {
      errors.push({ reason: loaded.reason, detail: loaded.detail, exitCode: EXIT.MISSING });
    } else {
      trustedTexts = loaded.texts;
      trustedPaths = loaded.paths;
    }
  }

  const bytes = fs.readFileSync(manifestPath);
  let manifest = null;
  try {
    manifest = JSON.parse(bytes.toString("utf8"));
  } catch {
    // The D4 validator reports the exact JSON reason below.
  }

  const mapped = installerFilesFromManifest(artifactDir, manifest ?? {});
  errors.push(...mapped.errors);
  const existingInstallers = {};
  for (const [architecture, filePath] of Object.entries(mapped.files)) {
    if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
      existingInstallers[architecture] = filePath;
    } else {
      errors.push({
        reason: "asset-missing",
        detail: `${architecture}: installer asset is missing: ${path.basename(filePath)}`,
        exitCode: EXIT.MISSING,
      });
    }
  }

  const validation = await validateManifestBuffer(bytes, {
    signature: fs.readFileSync(signaturePath, "utf8"),
    trustedKeys: trustedTexts,
    channel: options.channel,
    version: options.version,
    requireArches: options.requireArches ?? KNOWN_ARCHES,
    currentVersion: options.currentVersion,
    maxAgeHours: options.maxAgeHours,
    installers: existingInstallers,
  });
  errors.push(...validation.errors);
  checks.push(...validation.checks);

  const installers = [];
  for (const [architecture, filePath] of Object.entries(existingInstallers)) {
    installers.push({
      rid: architecture,
      file: filePath,
      fileName: path.basename(filePath),
      size: fs.statSync(filePath).size,
      sha256: await sha256FileStream(filePath),
      dryRun: path.basename(filePath).endsWith(".dry-run.json"),
    });
  }
  installers.sort((left, right) => (left.rid < right.rid ? -1 : left.rid > right.rid ? 1 : 0));

  const legacy = await checkLegacyFeed({
    artifactDir,
    channel: options.channel,
    version: options.version ?? manifest?.version,
    trustedTexts,
    errors,
  });

  const trustedKeys = keySummary(trustedPaths, trustedTexts, options.embeddedKeys);
  const testKey = trustedKeys.some((key) => key.testKey);
  const placeholder = trustedKeys.some((key) => key.placeholder);
  if (placeholder) {
    errors.push({ reason: "placeholder-key", detail: "a trusted key is a placeholder", exitCode: EXIT.REFUSED });
  }
  if (testKey) {
    checks.push({ name: "test-key", pass: true, detail: "dry-run test key" });
  }

  const dryRun = installers.some((installer) => installer.dryRun);
  const requiredAssets = [
    path.basename(manifestPath),
    path.basename(signaturePath),
    ...legacy.assets,
    ...installers.map((installer) => installer.fileName),
  ];

  return {
    ok: errors.length === 0,
    errors,
    checks,
    artifactDir,
    manifestPath,
    signaturePath,
    manifest,
    manifestBytes: bytes,
    signatureText: fs.readFileSync(signaturePath, "utf8"),
    installers,
    trustedKeys,
    testKey,
    placeholder,
    dryRun,
    legacy,
    requiredAssets,
  };
}
