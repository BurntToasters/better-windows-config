#!/usr/bin/env node
/**
 * Offline release dry run. Publishes both RIDs only when asked, computes sizes and SHA-256,
 * assembles one channel bundle with scripts/assemble-bundle.js, validates it, and writes a
 * repeatable artifact under artifacts/release/<version>/<channel>/.
 *
 * With no --secret-key, an ephemeral test key signs the bundle. The secret exists only in this
 * process and is never written. With --secret-key, a real or test key signs it, and the key must
 * be one of the keys embedded in src/Bwc.Update/Signing/TrustedKeys.cs.
 *
 * No network, no gh, no gpg, no NSIS, no UAC. The dry-run installer stand-in is never a release
 * asset: release-draft.js --apply refuses a .dry-run.json installer.
 */
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";

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
import { generateKeyPair } from "./lib/minisign.js";
import { publishInventory } from "./lib/publish-inventory.js";
import { channelForVersion, tryParseSemVer } from "./lib/semver.js";
import { validateUrl } from "./lib/url-policy.js";
import { passwordArgumentGiven } from "./lib/secret-input.js";
import {
  assembleBundle,
  defaultDownloadBaseUrl,
  defaultNotesUrl,
  normalizePubDate,
} from "./assemble-bundle.js";
import { parsePackageVersion } from "./sync-version-helpers.js";
import { resolveKeyPassword, secretKeyFromPath } from "./sign-update-manifest.js";

const SCRIPT = "scripts/release-dry-run.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const RIDS = ["win-x64", "win-arm64"];
const DEFAULT_OUT = path.join("artifacts", "release");
const INSTALLER_DIR = path.join(REPO_ROOT, "artifacts", "installer");
const DEFAULT_EXPIRES_HOURS = 168;

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--rid win-x64|win-arm64|all] [--version <semver>] [--channel <stable|beta>]
                 [--dry-run] [--no-build] [--pub-date <utc>] [--expires-hours <n>] [--download-base-url <url>]
                 [--notes-url <url>] [--secret-key <dir|file>] [--password-env <NAME>]
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
  --expires-hours <n>          Signed expiry after the publish time. Default ${DEFAULT_EXPIRES_HOURS}.
  --download-base-url <url>    Must be https. Default https://github.com/<owner>/<repo>/releases/download/v<version>.
  --notes-url <url>            Must be https. Default https://github.com/<owner>/<repo>/releases/tag/v<version>.
  --secret-key <dir|file>      Sign with this key file or folder instead of the ephemeral test key. The key
                               must be embedded in TrustedKeys.cs. The environment key is not read here;
                               release:assemble reads TAURI_SIGNING_PRIVATE_KEY.
  --password-env <NAME>        Environment variable with the password of an encrypted --secret-key.
                               A password is never a command-line argument.
  --installer-<rid> <path>     Use an explicit installer file for one RID.
  --current-version <semver>   Also check that the generated version is newer.
  --max-age-hours <n>          Manifest freshness limit for the validation report. Default 24.
  --json                       Print one JSON line summary.
  --help                       Show this help.

Output: artifacts/release/<version>/<channel>/ with bwc-<channel>.json, bwc-<channel>.json.minisig,
bwc-<channel>.json.sig, installer copies with signatures, publish hashes, assembly.json,
release-plan.json, and validation-report.json. Also artifacts/release/<version>/publish-<rid>.json.

Exit codes: 0 pass, 1 validation refused, 2 usage, 3 input missing, 4 build failed.`);
}

function parseRids(value) {
  if (!value || value === "all") return RIDS;
  if (!RIDS.includes(value)) {
    throw new Error(`--rid must be win-x64, win-arm64, or all; received ${value}`);
  }
  return [value];
}

function normalizeBaseUrl(value, optionName = "--download-base-url") {
  const checked = validateUrl(value, { requireHttps: true });
  if (!checked.ok) {
    throw new Error(`${optionName} is refused: ${checked.detail}`);
  }
  const url = checked.url.toString();
  if (url.includes("?") || url.includes("#")) {
    throw new Error(`${optionName} may not carry a query or fragment`);
  }
  return url.replace(/\/+$/, "");
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

/**
 * Resolves the signing key: an ephemeral test key, or the key file or folder the caller named. The
 * environment key is deliberately not read here, so a dry run never touches the release secret.
 */
async function signingKey(options) {
  if (!options.secretKey) {
    const pair = generateKeyPair({ comment: "untrusted comment: BWC RELEASE DRY-RUN TEST KEY" });
    return { text: pair.secretKeyText, password: undefined, ephemeral: true, pair };
  }
  const loaded = secretKeyFromPath(options.secretKey);
  if (!loaded.ok) {
    return { error: { reason: loaded.reason, detail: loaded.detail, exitCode: loaded.exitCode } };
  }
  const resolved = await resolveKeyPassword({
    text: loaded.text,
    source: loaded.source,
    passwordEnv: options.passwordEnv,
  });
  if (!resolved.ok) {
    return { error: { reason: resolved.reason, detail: resolved.detail, exitCode: EXIT.USAGE } };
  }
  return { text: loaded.text, password: resolved.password, ephemeral: false };
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

  const key = await signingKey(options);
  if (key.error) {
    console.error(`${SCRIPT}: REFUSED [${key.error.reason}]: ${key.error.detail}`);
    return key.error.exitCode;
  }

  const artifactRoot = path.join(options.out, options.version);
  const artifactDir = path.join(artifactRoot, channel);
  fs.rmSync(artifactDir, { recursive: true, force: true });
  for (const rid of RIDS) {
    fs.rmSync(path.join(artifactRoot, `publish-${rid}.json`), { force: true });
  }
  fs.mkdirSync(artifactDir, { recursive: true });
  // Only the public half of an ephemeral key is written, so the bundle can be validated afterwards.
  let ephemeralPublicPath = null;
  if (key.ephemeral) {
    ephemeralPublicPath = path.join(artifactDir, "ephemeral", "minisign.pub");
    fs.mkdirSync(path.dirname(ephemeralPublicPath), { recursive: true });
    fs.writeFileSync(ephemeralPublicPath, key.pair.publicKeyText, "utf8");
  }

  const buildCommands = options.rids.map((rid) => `node scripts/dotnet-build.js --rid ${rid}`);
  const publishReports = {};
  const installerFiles = {};
  const installerSources = {};

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
    const inventoryFile = path.join(artifactRoot, `publish-${rid}.json`);
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
    installerFiles[rid] = chosen.path;
    installerSources[rid] = chosen;
  }

  const assembled = await assembleBundle({
    root: REPO_ROOT,
    version: options.version,
    channel,
    keyText: key.text,
    password: key.password,
    dryRun: key.ephemeral,
    pubDate: options.pubDate,
    expiresHours: options.expiresHours,
    baseUrl: options.baseUrl,
    notesUrl: options.notesUrl,
    out: artifactRoot,
    installers: installerFiles,
  });

  const installers = {};
  for (const rid of options.rids) {
    const entry = assembled.installers?.[rid];
    installers[rid] = {
      file: entry?.file ?? installerFiles[rid],
      size: entry?.size ?? fs.statSync(installerFiles[rid]).size,
      sha256: entry?.sha256 ?? (await sha256FileStream(installerFiles[rid])),
      source: installerSources[rid].source,
      dryRun: installerSources[rid].dryRun,
      url: entry?.url ?? null,
    };
  }

  const validationErrors = assembled.errors ?? [];
  const validationChecks = assembled.checks ?? [];
  const report = {
    schema: 1,
    tool: SCRIPT,
    ok: assembled.ok,
    version: options.version,
    channel,
    pubDate: options.pubDate,
    expires: assembled.expires ?? null,
    artifactDir: relativeTo(REPO_ROOT, artifactDir),
    dryRun: options.dryRun,
    network: "none",
    key: {
      keyId: assembled.keyIdHex ?? null,
      fingerprint: assembled.fingerprint ?? null,
      testKey: Boolean(assembled.testKey),
      source: key.ephemeral ? "ephemeral" : "file",
      publicKey: ephemeralPublicPath ? relativeTo(REPO_ROOT, ephemeralPublicPath) : null,
    },
    manifest: assembled.manifestPath ? relativeTo(REPO_ROOT, assembled.manifestPath) : null,
    manifestSignature: assembled.signaturePath ? relativeTo(REPO_ROOT, assembled.signaturePath) : null,
    installers: Object.fromEntries(
      Object.entries(installers).map(([rid, installer]) => [
        rid,
        {
          file: relativeTo(REPO_ROOT, installer.file),
          size: installer.size,
          sha256: installer.sha256,
          source: installer.source,
          dryRun: installer.dryRun,
        },
      ]),
    ),
    publish: publishReports,
    errors: validationErrors,
    checks: validationChecks,
  };
  writeJson(path.join(artifactDir, "validation-report.json"), report);

  writeJson(path.join(artifactDir, "release-plan.json"), {
    schema: 1,
    tool: SCRIPT,
    version: options.version,
    channel,
    pubDate: options.pubDate,
    rids: options.rids,
    dryRun: options.dryRun,
    network: "none",
    published: false,
    artifactDir: relativeTo(REPO_ROOT, artifactDir),
    buildCommands,
    installers: Object.fromEntries(
      Object.entries(installers).map(([rid, installer]) => [
        rid,
        { file: relativeTo(REPO_ROOT, installer.file), source: installer.source, dryRun: installer.dryRun },
      ]),
    ),
    signer: key.ephemeral ? "ephemeral test key, never written to disk" : "key named by --secret-key",
    next:
      "Validate the artifact with scripts/validate-updater-manifest.js, then run the draft and " +
      "publish scripts in their default dry-run mode.",
  });

  if (!assembled.ok) {
    printRefusals("release-dry-run", validationErrors);
    console.error(`release-dry-run: FAIL: validation report ${relativeTo(REPO_ROOT, path.join(artifactDir, "validation-report.json"))}`);
    return assembled.errors?.some((error) => error.exitCode === EXIT.MISSING) ? EXIT.MISSING : EXIT.REFUSED;
  }

  if (options.json) {
    console.log(
      JSON.stringify({
        schema: 1,
        tool: SCRIPT,
        ok: true,
        artifactDir: relativeTo(REPO_ROOT, artifactDir),
        manifest: relativeTo(REPO_ROOT, assembled.manifestPath),
        signature: relativeTo(REPO_ROOT, assembled.signaturePath),
        keyId: assembled.keyIdHex,
        rids: options.rids,
      }),
    );
  } else {
    console.log("release-dry-run: PASS");
    console.log(`  artifact: ${relativeTo(REPO_ROOT, artifactDir)}`);
    console.log(`  manifest: ${relativeTo(REPO_ROOT, assembled.manifestPath)}`);
    console.log(`  signature: ${relativeTo(REPO_ROOT, assembled.signaturePath)}`);
    console.log(`  validation: ${relativeTo(REPO_ROOT, path.join(artifactDir, "validation-report.json"))}`);
    console.log(`  channel: ${channel}, version: ${options.version}, pubDate: ${options.pubDate}, expires: ${assembled.expires}`);
    for (const rid of options.rids) {
      const installer = installers[rid];
      console.log(`  ${rid}: ${path.basename(installer.file)} (${installer.size} bytes, ${installer.source})`);
    }
  }
  return EXIT.OK;
}

function main(argv = process.argv.slice(2)) {
  if (passwordArgumentGiven(argv)) {
    console.error(`${SCRIPT}: REFUSED [password-argument]: passwords are never command-line arguments; use --password-env <NAME>`);
    return EXIT.USAGE;
  }
  let options;
  try {
    const parsed = parseArguments(argv, {
      values: [
        "rid",
        "version",
        "channel",
        "pub-date",
        "expires-hours",
        "download-base-url",
        "notes-url",
        "installer-win-x64",
        "installer-win-arm64",
        "current-version",
        "max-age-hours",
        "out",
        "secret-key",
        "password-env",
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
    const expiresText = parsed.get("expires-hours");
    const expiresHours = expiresText === undefined ? DEFAULT_EXPIRES_HOURS : Number(expiresText);
    if (!Number.isInteger(expiresHours) || expiresHours < 1) {
      throw new Error("--expires-hours must be a positive whole number");
    }
    options = {
      rids: parseRids(parsed.get("rid")),
      version,
      channel,
      pubDate: normalizePubDate(parsed.get("pub-date")),
      expiresHours,
      baseUrl: normalizeBaseUrl(parsed.get("download-base-url") ?? defaultDownloadBaseUrl(version)),
      notesUrl: normalizeBaseUrl(parsed.get("notes-url") ?? defaultNotesUrl(version), "--notes-url"),
      explicitInstallers: {
        "win-x64": parsed.get("installer-win-x64"),
        "win-arm64": parsed.get("installer-win-arm64"),
      },
      secretKey: parsed.get("secret-key"),
      passwordEnv: parsed.get("password-env"),
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
