#!/usr/bin/env node
/**
 * Validates a BWC update manifest (schema 1), its minisign signature, and optional installer files.
 *
 * This is the release-side validator. It mirrors the contract in
 * docs/contracts/update-manifest.md and never contacts the network.
 */
import fs from "node:fs";
import path from "node:path";

import { isDirectExecutionOf } from "./direct-execution.mjs";
import {
  EXIT,
  describeError,
  exitCodeForErrors,
  parseArguments,
  printRefusals,
  writeJson,
} from "./lib/cli.js";
import { isPlaceholderKeyText, loadPublicKeys, parsePublicKeyText } from "./lib/minisign.js";
import { channelForVersion, tryParseSemVer } from "./lib/semver.js";
import { validateManifestBuffer } from "./lib/update-manifest.js";

const SCRIPT = "scripts/validate-updater-manifest.js";

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} --manifest <path> [options]

Validates the manifest JSON, its minisign signature, and optionally each installer's
size, SHA-256, minisign signature, signed version, and signed architecture. No network.

Options:
  --manifest <path>          Required. Manifest JSON file.
  --signature <path|text>    Raw .minisig text or its base64 envelope. Defaults to <manifest>.minisig.
  --trusted-key <path|text>  Trusted minisign public key. Repeatable.
  --channel <stable|beta>    Expected channel.
  --version <semver>         Expected manifest version.
  --require-arch <rid>       Required architecture: win-x64 or win-arm64. Repeatable.
  --current-version <semver> Refuse same-version and downgrade against this installed version.
  --max-age-hours <hours>    Refuse an expired pubDate older than this many hours.
  --installer <rid>=<path>   Installer file to check. Repeatable.
  --structure-only           Skip signature and trusted key requirements.
  --json                     Print one JSON line instead of prose.
  --report <path>            Write the full validation report as JSON.
  --help                     Show this help.

Exit codes: 0 valid, 1 refused, 2 usage, 3 manifest or installer file missing.`);
}

function readSignatureValue(value) {
  if (value === undefined) return { ok: true, text: null, path: null };
  const resolved = path.resolve(value);
  if (fs.existsSync(resolved) && fs.statSync(resolved).isFile()) {
    return { ok: true, text: fs.readFileSync(resolved, "utf8"), path: resolved };
  }
  return { ok: true, text: value, path: null };
}

function parseInstallerValues(values) {
  const result = {};
  for (const value of values) {
    const equals = value.indexOf("=");
    if (equals <= 0 || equals === value.length - 1) {
      throw new Error(`--installer must be <rid>=<path>; received ${value}`);
    }
    const rid = value.slice(0, equals);
    const file = value.slice(equals + 1);
    if (Object.prototype.hasOwnProperty.call(result, rid)) {
      throw new Error(`--installer was given more than once for ${rid}`);
    }
    result[rid] = path.resolve(file);
  }
  return result;
}

function numericOption(value, name) {
  if (value === undefined) return undefined;
  const number = Number(value);
  if (!Number.isFinite(number) || number < 0) {
    throw new Error(`--${name} must be a non-negative number`);
  }
  return number;
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    const parsed = parseArguments(argv, {
      values: [
        "manifest",
        "signature",
        { name: "trusted-key", multi: true },
        "channel",
        "version",
        { name: "require-arch", multi: true },
        "current-version",
        "max-age-hours",
        { name: "installer", multi: true },
        "report",
      ],
      flags: ["structure-only", "json", "help"],
    });
    if (parsed.has("help")) {
      printHelp();
      return EXIT.OK;
    }
    if (!parsed.get("manifest")) {
      throw new Error("--manifest is required");
    }
    if (parsed.positionals.length > 0) {
      throw new Error(`unexpected argument: ${parsed.positionals[0]}`);
    }
    const channel = parsed.get("channel");
    if (channel !== undefined && channel !== "stable" && channel !== "beta") {
      throw new Error("--channel must be stable or beta");
    }
    const version = parsed.get("version");
    if (version !== undefined && !tryParseSemVer(version)) {
      throw new Error("--version must be SemVer");
    }
    const currentVersion = parsed.get("current-version");
    if (currentVersion !== undefined && !tryParseSemVer(currentVersion)) {
      throw new Error("--current-version must be SemVer");
    }
    for (const architecture of parsed.all("require-arch")) {
      if (architecture !== "win-x64" && architecture !== "win-arm64") {
        throw new Error(`--require-arch must be win-x64 or win-arm64; received ${architecture}`);
      }
    }
    options = {
      manifest: path.resolve(parsed.get("manifest")),
      signatureValue: parsed.get("signature"),
      trustedKeyValues: parsed.all("trusted-key"),
      channel,
      version,
      requireArches: parsed.all("require-arch"),
      currentVersion,
      maxAgeHours: numericOption(parsed.get("max-age-hours"), "max-age-hours"),
      installers: parseInstallerValues(parsed.all("installer")),
      structureOnly: parsed.has("structure-only"),
      json: parsed.has("json"),
      report: parsed.get("report") ? path.resolve(parsed.get("report")) : null,
    };
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }

  return run(options);
}

async function run(options) {
  const errors = [];
  let manifestBytes = null;
  let signatureText = null;
  let trustedKeys = [];
  let keyPaths = [];

  if (!fs.existsSync(options.manifest) || !fs.statSync(options.manifest).isFile()) {
    errors.push({
      reason: "manifest-missing",
      detail: `manifest file is missing: ${options.manifest}`,
      exitCode: EXIT.MISSING,
    });
  } else {
    manifestBytes = fs.readFileSync(options.manifest);

    if (!options.structureOnly) {
      const signature = readSignatureValue(options.signatureValue);
      if (!signature.ok) {
        errors.push({ reason: "signature-format", detail: signature.detail });
      } else if (signature.text === null) {
        const sibling = `${options.manifest}.minisig`;
        if (fs.existsSync(sibling) && fs.statSync(sibling).isFile()) {
          signatureText = fs.readFileSync(sibling, "utf8");
        } else {
          errors.push({
            reason: "signature-missing",
            detail: `no --signature and no sibling signature at ${sibling}`,
          });
        }
      } else {
        signatureText = signature.text;
      }

      if (options.trustedKeyValues.length === 0) {
        errors.push({
          reason: "trusted-key-missing",
          detail: "at least one --trusted-key is required to validate a signature",
        });
      } else {
        const loaded = loadPublicKeys(options.trustedKeyValues);
        if (!loaded.ok) {
          errors.push({ reason: loaded.reason, detail: loaded.detail });
        } else {
          for (let index = 0; index < loaded.texts.length; index += 1) {
            if (isPlaceholderKeyText(loaded.texts[index])) {
              errors.push({
                reason: "placeholder-key",
                detail: `trusted key ${index} is a placeholder`,
              });
              continue;
            }
            const parsed = parsePublicKeyText(loaded.texts[index]);
            if (!parsed.ok) {
              errors.push({
                reason: parsed.reason,
                detail: `trusted key ${index}: ${parsed.detail}`,
              });
            }
          }
          trustedKeys = loaded.texts;
          keyPaths = loaded.paths;
        }
      }
    }
  }

  let validation = {
    ok: false,
    errors: [],
    checks: [],
    manifest: null,
    manifestBytes,
  };
  if (manifestBytes) {
    validation = await validateManifestBuffer(manifestBytes, {
      signature: options.structureOnly ? null : signatureText,
      signatureRequired: !options.structureOnly,
      trustedKeys,
      channel: options.channel,
      version: options.version,
      requireArches: options.requireArches,
      currentVersion: options.currentVersion,
      maxAgeHours: options.maxAgeHours,
      installers: options.installers,
      verifyInstallerSignatures: !options.structureOnly,
    });
  }

  const allErrors = [...errors, ...validation.errors];
  const signaturePath = options.signatureValue
    ? fs.existsSync(path.resolve(options.signatureValue))
      ? path.resolve(options.signatureValue)
      : "(inline signature text)"
    : `${options.manifest}.minisig`;
  const report = {
    schema: 1,
    tool: SCRIPT,
    ok: allErrors.length === 0,
    manifest: options.manifest,
    signature: signaturePath,
    trustedKeys: keyPaths.filter(Boolean),
    channel: options.channel ?? validation.channel ?? channelForVersion(options.version ?? ""),
    version: options.version ?? validation.version ?? null,
    errors: allErrors,
    checks: validation.checks,
  };

  if (options.report) {
    writeJson(options.report, report);
  }

  if (options.json) {
    console.log(JSON.stringify(report));
  } else if (allErrors.length === 0) {
    console.log(
      `validate-updater-manifest: ok (${report.version ?? "unknown version"}, ${report.channel ?? "unknown channel"})`,
    );
    for (const check of validation.checks) {
      console.log(`  ${check.pass ? "PASS" : "FAIL"} ${check.name}${check.detail ? `: ${check.detail}` : ""}`);
    }
  } else {
    printRefusals("validate-updater-manifest", allErrors);
    for (const error of allErrors) {
      console.error(`  detail: [${error.reason}] ${error.detail}`);
    }
  }

  return exitCodeForErrors(allErrors);
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = await main();
}
