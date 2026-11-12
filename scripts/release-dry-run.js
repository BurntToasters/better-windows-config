#!/usr/bin/env node
/**
 * Offline release dry run. Publishes both RIDs only when asked, computes sizes and SHA-256,
 * makes a manifest with an ephemeral minisign test key, validates it, and writes a repeatable
 * artifact under artifacts/release/<version>/.
 *
 * No network, no gh, no gpg, no NSIS, no UAC. The dry-run installer stand-in is never a release
 * asset: release-draft.js --apply refuses a .dry-run.json installer.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import {
  EXIT,
  describeError,
  parseArguments,
  printRefusals,
  relativeTo,
  repoRootFrom,
  sha256FileStream,
  writeJson,
} from "./lib/cli.js";
import { envelope, generateKeyPair, signFile } from "./lib/minisign.js";
import { publishInventory } from "./lib/publish-inventory.js";
import { channelForVersion, tryParseSemVer } from "./lib/semver.js";
import { buildManifest, serializeManifest, validateManifestBuffer } from "./lib/update-manifest.js";
import { validateUrl } from "./lib/url-policy.js";
import { parsePackageVersion } from "./sync-version-helpers.js";

const SCRIPT = "scripts/release-dry-run.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const RIDS = ["win-x64", "win-arm64"];
const DEFAULT_OUT = path.join("artifacts", "release");
const INSTALLER_DIR = path.join(REPO_ROOT, "artifacts", "installer");
const DEFAULT_BASE_URL = "https://example.invalid/bwc";
const PUBDATE_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--rid win-x64|win-arm64|all] [--version <semver>] [--channel <stable|beta>]
                 [--dry-run] [--no-build] [--pub-date <utc>] [--download-base-url <url>]
                 [--installer-win-x64 <path>] [--installer-win-arm64 <path>]
                 [--current-version <semver>] [--max-age-hours <n>] [--json]

Options:
  --rid <rid|all>              Default all. all builds or describes both RIDs.
  --version <semver>           Default package.json. Accepts X.Y.Z or X.Y.Z-beta.N.
  --channel <stable|beta>      Default from the version. Must agree with it.
  --dry-run                    Do not build. Describe the build commands and use whatever
                               publish or setup EXE already exists.
  --no-build                   Do not build, but do not label the run a dry run.
  --pub-date <utc>             Default now, or SOURCE_DATE_EPOCH. Normalized UTC ending in Z.
  --download-base-url <url>    Default ${DEFAULT_BASE_URL}. Must be https.
  --installer-<rid> <path>     Use an explicit installer file for one RID.
  --current-version <semver>   Also check that the generated version is newer.
  --max-age-hours <n>          Manifest freshness limit for the validation report. Default 24.
  --json                       Print one JSON line summary.
  --help                       Show this help.

Output: artifacts/release/<version>/ with manifest.json, manifest.json.minisig,
manifest.json.sig, installer signatures, publish hashes, release-plan.json,
validation-report.json, and ephemeral/minisign.pub.

Exit codes: 0 pass, 1 validation refused, 2 usage, 3 input missing, 4 build failed.`);
}

function normalizePubDate(value) {
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
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    throw new Error("--pub-date is not a real timestamp");
  }
  return value;
}

function normalizeBaseUrl(value) {
  const checked = validateUrl(value, { requireHttps: true });
  if (!checked.ok) {
    throw new Error(`--download-base-url is refused: ${checked.detail}`);
  }
  const url = checked.url.toString();
  if (url.includes("?") || url.includes("#")) {
    throw new Error("--download-base-url may not carry a query or fragment");
  }
  return url.replace(/\/+$/, "");
}

function parseRids(value) {
  if (!value || value === "all") return RIDS;
  if (!RIDS.includes(value)) {
    throw new Error(`--rid must be win-x64, win-arm64, or all; received ${value}`);
  }
  return [value];
}

function numericOption(value, name, fallback) {
  if (value === undefined) return fallback;
  const number = Number(value);
  if (!Number.isFinite(number) || number < 0) {
    throw new Error(`--${name} must be a non-negative number`);
  }
  return number;
}

function publishRecordGood(rid) {
  const recordPath = path.join(REPO_ROOT, "artifacts", "build", `${rid}.json`);
  if (!fs.existsSync(recordPath)) return false;
  try {
    const record = JSON.parse(fs.readFileSync(recordPath, "utf8"));
    return record.pass === true && record.flavor === "ship" && record.rid === rid;
  } catch {
    return false;
  }
}

function ensurePublish(rid, dryRun, noBuild) {
  const publishDir = path.join(REPO_ROOT, "publish", rid);
  const good = publishRecordGood(rid) && fs.existsSync(path.join(publishDir, "BetterWindowsConfig.exe"));
  if (good) {
    return { ok: true, built: false, publishDir };
  }
  if (dryRun || noBuild) {
    return { ok: true, built: false, publishDir, detail: "publish is missing but the build was skipped" };
  }

  const args = [path.join(REPO_ROOT, "scripts", "dotnet-build.js"), "--rid", rid];
  console.log(`release-dry-run: publishing ${rid}`);
  const result = spawnSync(process.execPath, args, { cwd: REPO_ROOT, stdio: "inherit", windowsHide: true });
  if (result.error || result.status !== 0) {
    return {
      ok: false,
      built: true,
      publishDir,
      detail: result.error ? result.error.message : `dotnet-build exited ${result.status}`,
    };
  }
  return { ok: true, built: true, publishDir };
}

function setupRecordPath(version, rid) {
  return path.join(INSTALLER_DIR, `BetterWindowsConfig-${version}-${rid}-setup.json`);
}

function setupPath(version, rid) {
  return path.join(INSTALLER_DIR, `BetterWindowsConfig-${version}-${rid}-setup.exe`);
}

function readSetupRecord(version, rid) {
  const recordPath = setupRecordPath(version, rid);
  if (!fs.existsSync(recordPath)) return null;
  try {
    const record = JSON.parse(fs.readFileSync(recordPath, "utf8"));
    if (record.pass !== true || record.version !== version || record.rid !== rid || record.flavor !== "ship") {
      return null;
    }
    return record;
  } catch {
    return null;
  }
}

function chooseInstaller({ rid, version, channel, artifactDir, explicitPath, publish }) {
  if (explicitPath) {
    const resolved = path.resolve(explicitPath);
    if (!fs.existsSync(resolved) || !fs.statSync(resolved).isFile()) {
      return {
        ok: false,
        reason: "installer-missing",
        detail: `--installer-${rid} file is missing: ${resolved}`,
      };
    }
    return {
      ok: true,
      path: resolved,
      source: "explicit",
      dryRun: path.basename(resolved).endsWith(".dry-run.json"),
    };
  }

  const real = setupPath(version, rid);
  const record = readSetupRecord(version, rid);
  if (record && fs.existsSync(real) && fs.statSync(real).isFile()) {
    return { ok: true, path: real, source: "setup-exe", dryRun: false, record };
  }

  const name = `BetterWindowsConfig-${version}-${rid}-setup.dry-run.json`;
  const target = path.join(artifactDir, name);
  const body = {
    schema: 1,
    kind: "bwc-release-dry-run-installer",
    version,
    channel,
    rid,
    arch: rid === "win-x64" ? "x64" : "arm64",
    publish: {
      present: publish.present,
      directory: relativeTo(REPO_ROOT, publish.directory),
      files: publish.files.length,
      bytes: publish.bytes,
      treeSha256: publish.treeSha256,
    },
    reason:
      "No matching setup EXE record exists. NSIS is not installed or the installer has not " +
      "been compiled. This file stands in for the setup EXE in the release dry run.",
    note: "Never publish this file. release-draft.js --apply refuses .dry-run.json installers.",
  };
  writeJson(target, body);
  return {
    ok: true,
    path: target,
    source: "dry-run-stand-in",
    dryRun: true,
    reason: body.reason,
  };
}

function signedComment({ version, rid, fileName, pubDate }) {
  const timestamp = Math.floor(pubDate.getTime() / 1000);
  return `timestamp:${timestamp} file:${fileName} version:${version} arch:${rid}`;
}

function artifactUrl(baseUrl, version, fileName) {
  return `${baseUrl}/${encodeURIComponent(version)}/${encodeURIComponent(fileName)}`;
}

async function run(options) {
  let parsedVersion;
  try {
    parsedVersion = parsePackageVersion(options.version);
  } catch (error) {
    console.error(`${SCRIPT}: REFUSED [version-invalid]: ${describeError(error)}`);
    return EXIT.USAGE;
  }
  const channel = options.channel ?? channelForVersion(options.version);
  if (channel !== "stable" && channel !== "beta") {
    console.error(`${SCRIPT}: REFUSED [channel-invalid]: ${options.version} has no supported channel`);
    return EXIT.USAGE;
  }
  if (options.channel && options.channel !== channelForVersion(options.version)) {
    console.error(
      `${SCRIPT}: REFUSED [channel-mismatch]: version ${options.version} is ${channelForVersion(options.version)}, not ${options.channel}`,
    );
    return EXIT.USAGE;
  }
  if (options.currentVersion && !tryParseSemVer(options.currentVersion)) {
    console.error(`${SCRIPT}: REFUSED [version-invalid]: --current-version is not SemVer`);
    return EXIT.USAGE;
  }

  const pubDate = options.pubDate;
  const artifactDir = path.join(options.out, options.version);
  const ephemeralDir = path.join(artifactDir, "ephemeral");
  const generated = [
    path.join(artifactDir, "manifest.json"),
    path.join(artifactDir, "manifest.json.minisig"),
    path.join(artifactDir, "manifest.json.sig"),
    path.join(artifactDir, "release-plan.json"),
    path.join(artifactDir, "validation-report.json"),
  ];
  for (const rid of RIDS) {
    generated.push(
      path.join(artifactDir, `BetterWindowsConfig-${options.version}-${rid}-setup.dry-run.json`),
      path.join(artifactDir, `BetterWindowsConfig-${options.version}-${rid}-setup.dry-run.json.minisig`),
      path.join(artifactDir, `BetterWindowsConfig-${options.version}-${rid}-setup.dry-run.json.sig`),
      path.join(artifactDir, `publish-${rid}.json`),
    );
  }
  fs.mkdirSync(artifactDir, { recursive: true });
  for (const file of generated) {
    fs.rmSync(file, { force: true });
  }
  fs.rmSync(ephemeralDir, { recursive: true, force: true });

  const pair = generateKeyPair({ comment: "untrusted comment: BWC RELEASE DRY-RUN TEST KEY" });
  const publicKeyPath = path.join(ephemeralDir, "minisign.pub");
  fs.mkdirSync(ephemeralDir, { recursive: true });
  fs.writeFileSync(publicKeyPath, pair.publicKeyText, "utf8");

  const buildCommands = options.rids.map((rid) => `node scripts/dotnet-build.js --rid ${rid}`);
  const publishReports = {};
  const installers = {};

  for (const rid of options.rids) {
      const built = ensurePublish(rid, options.dryRun, options.noBuild);
      if (!built.ok) {
        console.error(`${SCRIPT}: FAIL [build-failed]: ${built.detail}`);
        return EXIT.EXTERNAL;
      }
      if (!built.built && built.detail) {
        console.log(`release-dry-run: ${rid}: ${built.detail}`);
      }

      const inventory = await publishInventory(built.publishDir);
      const inventoryFile = path.join(artifactDir, `publish-${rid}.json`);
      writeJson(inventoryFile, {
        schema: 1,
        tool: SCRIPT,
        rid,
        present: inventory.present,
        directory: relativeTo(REPO_ROOT, inventory.directory),
        files: inventory.files,
        bytes: inventory.bytes,
        treeSha256: inventory.treeSha256,
        detail: inventory.detail ?? null,
      });
      publishReports[rid] = {
        present: inventory.present,
        files: inventory.files.length,
        bytes: inventory.bytes,
        treeSha256: inventory.treeSha256,
        report: relativeTo(REPO_ROOT, inventoryFile),
      };

      const chosen = chooseInstaller({
        rid,
        version: options.version,
        channel,
        artifactDir,
        explicitPath: options.explicitInstallers[rid],
        publish: inventory,
      });
      if (!chosen.ok) {
        console.error(`${SCRIPT}: REFUSED [${chosen.reason}]: ${chosen.detail}`);
        return EXIT.MISSING;
      }

      const fileName = path.basename(chosen.path);
      const trustedComment = signedComment({ version: options.version, rid, fileName, pubDate: new Date(pubDate) });
      const signatureText = await signFile(chosen.path, pair.secretKeyText, trustedComment);
      const signaturePath = `${chosen.path}.minisig`;
      const envelopePath = `${chosen.path}.sig`;
      fs.writeFileSync(signaturePath, signatureText, "utf8");
      fs.writeFileSync(envelopePath, envelope(signatureText), "utf8");

      const size = fs.statSync(chosen.path).size;
      const sha256 = await sha256FileStream(chosen.path);
      installers[rid] = {
        file: chosen.path,
        fileName,
        size,
        sha256,
        signature: envelope(signatureText),
        signatureFile: signaturePath,
        envelopeFile: envelopePath,
        source: chosen.source,
        dryRun: chosen.dryRun,
        url: artifactUrl(options.baseUrl, options.version, fileName),
      };
      console.log(
        `release-dry-run: ${rid}: ${chosen.source} ${fileName} (${size} bytes, sha256 ${sha256.slice(0, 12)}...)`,
      );
    }

    const manifest = buildManifest({
      version: options.version,
      channel,
      pubDate,
      notesUrl: `${options.baseUrl}/${encodeURIComponent(options.version)}/notes`,
      installers: Object.fromEntries(
        Object.entries(installers).map(([rid, installer]) => [
          rid,
          {
            url: installer.url,
            size: installer.size,
            sha256: installer.sha256,
            signature: installer.signature,
          },
        ]),
      ),
    });
    const manifestBytes = serializeManifest(manifest);
    const manifestPath = path.join(artifactDir, "manifest.json");
    fs.writeFileSync(manifestPath, manifestBytes);
    const manifestSignatureText = await signFile(manifestPath, pair.secretKeyText, `version:${options.version}`);
    const manifestSignaturePath = `${manifestPath}.minisig`;
    fs.writeFileSync(manifestSignaturePath, manifestSignatureText, "utf8");
    fs.writeFileSync(`${manifestPath}.sig`, envelope(manifestSignatureText), "utf8");

    const validation = await validateManifestBuffer(manifestBytes, {
      signature: manifestSignatureText,
      trustedKeys: [pair.publicKeyText],
      channel,
      version: options.version,
      requireArches: options.rids,
      currentVersion: options.currentVersion,
      maxAgeHours: options.maxAgeHours,
      installers: Object.fromEntries(Object.entries(installers).map(([rid, installer]) => [rid, installer.file])),
    });

    const report = {
      schema: 1,
      tool: SCRIPT,
      ok: validation.ok,
      version: options.version,
      channel,
      pubDate,
      artifactDir: relativeTo(REPO_ROOT, artifactDir),
      dryRun: options.dryRun,
      network: "none",
      key: {
        keyId: pair.keyIdHex,
        fingerprint: pair.fingerprint,
        publicKey: relativeTo(REPO_ROOT, publicKeyPath),
        testKey: true,
      },
      manifest: relativeTo(REPO_ROOT, manifestPath),
      manifestSignature: relativeTo(REPO_ROOT, manifestSignaturePath),
      installers: Object.fromEntries(
        Object.entries(installers).map(([rid, installer]) => [
          rid,
          {
            file: relativeTo(REPO_ROOT, installer.file),
            size: installer.size,
            sha256: installer.sha256,
            source: installer.source,
            dryRun: installer.dryRun,
            signature: relativeTo(REPO_ROOT, installer.signatureFile),
          },
        ]),
      ),
      publish: publishReports,
      errors: validation.errors,
      checks: validation.checks,
    };
    writeJson(path.join(artifactDir, "validation-report.json"), report);

    writeJson(path.join(artifactDir, "release-plan.json"), {
      schema: 1,
      tool: SCRIPT,
      version: options.version,
      channel,
      pubDate,
      rids: options.rids,
      dryRun: options.dryRun,
      network: "none",
      published: false,
      artifactDir: relativeTo(REPO_ROOT, artifactDir),
      buildCommands,
      installers: Object.fromEntries(
        Object.entries(installers).map(([rid, installer]) => [
          rid,
          {
            file: relativeTo(REPO_ROOT, installer.file),
            source: installer.source,
            dryRun: installer.dryRun,
          },
        ]),
      ),
      signer: "ephemeral test key, secret deleted after the run",
      next:
        "Validate the artifact with scripts/validate-updater-manifest.js, then run the draft and " +
        "publish scripts in their default dry-run mode.",
    });

    if (!validation.ok) {
      printRefusals("release-dry-run", validation.errors);
      console.error(`release-dry-run: FAIL: validation report ${relativeTo(REPO_ROOT, path.join(artifactDir, "validation-report.json"))}`);
      return EXIT.REFUSED;
    }

    if (options.json) {
      console.log(
        JSON.stringify({
          schema: 1,
          tool: SCRIPT,
          ok: true,
          artifactDir: relativeTo(REPO_ROOT, artifactDir),
          manifest: relativeTo(REPO_ROOT, manifestPath),
          signature: relativeTo(REPO_ROOT, manifestSignaturePath),
          keyId: pair.keyIdHex,
          rids: options.rids,
        }),
      );
    } else {
      console.log("release-dry-run: PASS");
      console.log(`  artifact: ${relativeTo(REPO_ROOT, artifactDir)}`);
      console.log(`  manifest: ${relativeTo(REPO_ROOT, manifestPath)}`);
      console.log(`  signature: ${relativeTo(REPO_ROOT, manifestSignaturePath)}`);
      console.log(`  public key: ${relativeTo(REPO_ROOT, publicKeyPath)}`);
      console.log(`  validation: ${relativeTo(REPO_ROOT, path.join(artifactDir, "validation-report.json"))}`);
      console.log(`  channel: ${channel}, version: ${options.version}, pubDate: ${pubDate}`);
      for (const rid of options.rids) {
        const installer = installers[rid];
        console.log(
          `  ${rid}: ${path.basename(installer.file)} (${installer.size} bytes, ${installer.source})`,
        );
      }
    }
    return EXIT.OK;
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    const parsed = parseArguments(argv, {
      values: [
        "rid",
        "version",
        "channel",
        "pub-date",
        "download-base-url",
        "installer-win-x64",
        "installer-win-arm64",
        "current-version",
        "max-age-hours",
        "out",
      ],
      flags: ["dry-run", "no-build", "json", "help"],
    });
    if (parsed.has("help")) {
      printHelp();
      return EXIT.OK;
    }
    if (parsed.positionals.length > 0) {
      throw new Error(`unexpected argument: ${parsed.positionals[0]}`);
    }
    if (parsed.has("dry-run") && parsed.has("no-build")) {
      throw new Error("--dry-run and --no-build cannot be combined");
    }
    const packageVersion = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, "package.json"), "utf8")).version;
    const version = parsed.get("version") ?? packageVersion;
    try {
      parsePackageVersion(version);
    } catch (error) {
      throw new Error(describeError(error));
    }
    const channel = parsed.get("channel");
    if (channel !== undefined && channel !== "stable" && channel !== "beta") {
      throw new Error("--channel must be stable or beta");
    }
    options = {
      rids: parseRids(parsed.get("rid")),
      version,
      channel,
      pubDate: normalizePubDate(parsed.get("pub-date")),
      baseUrl: normalizeBaseUrl(parsed.get("download-base-url") ?? DEFAULT_BASE_URL),
      explicitInstallers: {
        "win-x64": parsed.get("installer-win-x64"),
        "win-arm64": parsed.get("installer-win-arm64"),
      },
      currentVersion: parsed.get("current-version"),
      maxAgeHours: numericOption(parsed.get("max-age-hours"), "max-age-hours", 24),
      out: path.resolve(REPO_ROOT, parsed.get("out") ?? DEFAULT_OUT),
      dryRun: parsed.has("dry-run"),
      noBuild: parsed.has("no-build"),
      json: parsed.has("json"),
    };
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }

  return run(options);
}

process.exitCode = await main();
