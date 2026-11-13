#!/usr/bin/env node
/**
 * Read-only check of the published stable update feed. It fetches bwc-stable.json and its .minisig
 * from releases/latest/download, verifies the signature against the keys embedded in
 * src/Bwc.Update/Signing/TrustedKeys.cs, checks version, channel, expiry, and each architecture, then
 * downloads every installer and checks its size, SHA-256, and installer signature. No write, no gh.
 *
 * UPDATER_LIVE_BASE_URL replaces the base URL for tests. It must be a loopback URL (127.0.0.1 or
 * localhost). Only the stable channel is checked: /latest never points at a prerelease.
 */
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { Readable, Transform } from "node:stream";
import { pipeline } from "node:stream/promises";

import { EXIT, describeError, parseArguments, printRefusals, repoRootFrom } from "./lib/cli.js";
import { resolveRepo } from "./lib/github-release.js";
import { isPlaceholderKeyText, parsePublicKeyText } from "./lib/minisign.js";
import { readPackageVersion } from "./lib/release-policy.js";
import { channelForVersion, tryParseSemVer } from "./lib/semver.js";
import { readTrustedKeysFile } from "./lib/trusted-keys.js";
import { KNOWN_ARCHES, manifestFileName, validateManifestBuffer } from "./lib/update-manifest.js";
import { isLoopbackUrl } from "./lib/url-policy.js";
import { isDirectExecutionOf } from "./direct-execution.mjs";

const SCRIPT = "scripts/validate-updater-live.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const CHANNEL = "stable";
const MANIFEST_TIMEOUT_MS = 60_000;
const INSTALLER_TIMEOUT_MS = 15 * 60_000;
const USER_AGENT = "bwc-release-check";

function refusal(reason, detail, exitCode = EXIT.REFUSED) {
  return { reason, detail, exitCode };
}

/** A failure raised by a fetch or a download. Carries the reason and exit code the caller reports. */
class LiveError extends Error {
  constructor(reason, detail, exitCode = EXIT.REFUSED) {
    super(detail);
    this.reason = reason;
    this.exitCode = exitCode;
  }
}

function toRefusal(error) {
  if (error instanceof LiveError) return refusal(error.reason, error.message, error.exitCode);
  return refusal("fetch-failed", describeError(error), EXIT.EXTERNAL);
}

/** Exit 4 when any transport failed, otherwise 1 for a refusal. Usage errors exit before this. */
function exitFor(errors) {
  if (errors.length === 0) return EXIT.OK;
  return errors.some((error) => error.exitCode === EXIT.EXTERNAL) ? EXIT.EXTERNAL : EXIT.REFUSED;
}

async function fetchBytes(url, timeoutMs) {
  let response;
  try {
    response = await fetch(url, {
      redirect: "follow",
      signal: AbortSignal.timeout(timeoutMs),
      headers: { "User-Agent": USER_AGENT },
    });
    if (!response.ok) throw new LiveError("http-status", `${url}: HTTP ${response.status}`);
    return Buffer.from(await response.arrayBuffer());
  } catch (error) {
    if (error instanceof LiveError) throw error;
    throw new LiveError("fetch-failed", `${url}: ${describeError(error)}`, EXIT.EXTERNAL);
  }
}

/**
 * Streams an installer to disk and stops at the size the manifest declares. A longer body is refused
 * at once, so a hostile server cannot fill the disk.
 */
async function downloadInstaller(url, destination, declaredSize) {
  let response;
  try {
    response = await fetch(url, {
      redirect: "follow",
      signal: AbortSignal.timeout(INSTALLER_TIMEOUT_MS),
      headers: { "User-Agent": USER_AGENT },
    });
  } catch (error) {
    throw new LiveError("fetch-failed", `${url}: ${describeError(error)}`, EXIT.EXTERNAL);
  }
  if (!response.ok) throw new LiveError("http-status", `${url}: HTTP ${response.status}`);
  if (!response.body) throw new LiveError("fetch-failed", `${url}: the response has no body`, EXIT.EXTERNAL);

  let received = 0;
  const cap = new Transform({
    transform(chunk, _encoding, callback) {
      received += chunk.length;
      if (received > declaredSize) {
        callback(new LiveError("installer-size-mismatch", `${url}: more than the ${declaredSize} bytes the manifest declares`));
        return;
      }
      callback(null, chunk);
    },
  });
  try {
    await pipeline(Readable.fromWeb(response.body), cap, fs.createWriteStream(destination, { flags: "wx" }));
  } catch (error) {
    if (error instanceof LiveError) throw error;
    throw new LiveError("fetch-failed", `${url}: ${describeError(error)}`, EXIT.EXTERNAL);
  }
}

/** The embedded keys. A placeholder, an unreadable source, or a malformed key stops the check. */
export function loadEmbeddedKeys(root) {
  const source = readTrustedKeysFile(root);
  if (!source.ok) {
    return { ok: false, errors: [refusal(source.reason, `${source.detail ?? source.file}`)] };
  }
  if (source.isPlaceholder || source.embedded.length === 0 || source.embedded.some(isPlaceholderKeyText)) {
    return {
      ok: false,
      errors: [
        refusal(
          "placeholder-key",
          `${source.file} still embeds the placeholder; the live check cannot pass until the release key is embedded`,
        ),
      ],
    };
  }
  for (const [index, text] of source.embedded.entries()) {
    const parsed = parsePublicKeyText(text);
    if (!parsed.ok) {
      return { ok: false, errors: [refusal("malformed-key", `embedded key ${index}: ${parsed.detail}`)] };
    }
  }
  return { ok: true, keys: source.embedded };
}

/**
 * Checks the live feed at `base`. Returns { ok, errors, checks, manifestUrl }. The manifest is
 * verified before any installer is downloaded, so a refused manifest causes no download.
 */
export async function checkStableFeed({ base, keys, expectedVersion, requireArches, loopback }) {
  const manifestUrl = `${base}/${manifestFileName(CHANNEL)}`;
  const signatureUrl = `${manifestUrl}.minisig`;
  const manifestBytes = await fetchBytes(manifestUrl, MANIFEST_TIMEOUT_MS);
  const signature = (await fetchBytes(signatureUrl, MANIFEST_TIMEOUT_MS)).toString("utf8");
  const options = {
    signature,
    trustedKeys: keys,
    channel: CHANNEL,
    version: expectedVersion,
    requireArches,
    allowLoopbackHttp: loopback,
  };

  const first = await validateManifestBuffer(manifestBytes, options);
  const errors = [...first.errors];
  const checks = [...first.checks];
  if (!first.ok) return { ok: false, errors, checks, manifestUrl };

  const manifest = JSON.parse(manifestBytes.toString("utf8"));
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), "bwc-updater-live-"));
  try {
    const installers = {};
    for (const architecture of KNOWN_ARCHES) {
      const entry = manifest.installers?.[architecture];
      if (!entry) continue;
      const destination = path.join(workDir, `${architecture}.bin`);
      try {
        await downloadInstaller(entry.url, destination, entry.size);
        installers[architecture] = destination;
      } catch (error) {
        errors.push(toRefusal(error));
      }
    }
    const second = await validateManifestBuffer(manifestBytes, { ...options, installers });
    errors.push(...second.errors);
    checks.push(...second.checks);
    return { ok: errors.length === 0, errors, checks, manifestUrl, manifest: first.manifest };
  } finally {
    fs.rmSync(workDir, { recursive: true, force: true });
  }
}

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--expected-version <semver|current>] [--repo <owner/name>] [--root <repo>]
                 [--require-arch <win-x64|win-arm64>]... [--json] [--help]

Checks the published stable update feed (releases/latest/download/bwc-stable.json). It verifies the
manifest signature against the keys embedded in src/Bwc.Update/Signing/TrustedKeys.cs, the version,
the channel, the signed expiry, and each required architecture. It then downloads each installer and
checks its size, SHA-256, and installer signature. Read-only; no gh; no write.

Options:
  --expected-version <v>   Required version. "current" (the default) reads package.json. Stable only.
  --repo <owner/name>      Default GH_REPO, then GH_REPO_OWNER and GH_REPO_NAME, then BurntToasters/better-windows-config.
  --root <repo>            Repository whose TrustedKeys.cs lists the keys. Default: this checkout.
                           Tests only; the release run never passes it.
  --require-arch <rid>     Required architecture. Repeatable. Default: win-x64 and win-arm64.
  --json                   Print one JSON line.
  --help                   Show this help.

Environment:
  UPDATER_LIVE_BASE_URL    Tests only. A loopback http(s) base (127.0.0.1 or localhost) replaces GitHub.

Exit codes: 0 verified, 1 refused, 2 usage, 3 missing input, 4 network or transport failure.`);
}

function resolveExpected(value) {
  const text = value ?? "current";
  const version = text === "current" ? readPackageVersion(REPO_ROOT) : text;
  if (!tryParseSemVer(version)) {
    return { ok: false, error: refusal("version-invalid", `expected version is not SemVer: ${version}`, EXIT.USAGE) };
  }
  if (channelForVersion(version) !== CHANNEL) {
    return {
      ok: false,
      error: refusal("channel-invalid", `${version} is a prerelease; this check covers the stable channel only`, EXIT.USAGE),
    };
  }
  return { ok: true, version };
}

export async function main(argv = process.argv.slice(2), env = process.env) {
  let parsed;
  try {
    parsed = parseArguments(argv, {
      values: ["expected-version", "repo", "root", { name: "require-arch", multi: true }],
      flags: ["json", "help"],
    });
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }
  if (parsed.has("help")) {
    printHelp();
    return EXIT.OK;
  }
  if (parsed.positionals.length > 0) {
    console.error(`${SCRIPT}: unexpected argument: ${parsed.positionals[0]}`);
    return EXIT.USAGE;
  }
  const json = parsed.has("json");
  const usage = (error) => {
    printRefusals(SCRIPT, [error]);
    return error.exitCode;
  };

  const expected = resolveExpected(parsed.get("expected-version"));
  if (!expected.ok) return usage(expected.error);

  const requireArches = parsed.all("require-arch");
  for (const architecture of requireArches) {
    if (!KNOWN_ARCHES.includes(architecture)) {
      return usage(refusal("usage", `--require-arch must be win-x64 or win-arm64; received ${architecture}`, EXIT.USAGE));
    }
  }

  const override = String(env.UPDATER_LIVE_BASE_URL ?? "").trim();
  let base;
  if (override) {
    if (!isLoopbackUrl(override)) {
      return usage(refusal("base-url-refused", "UPDATER_LIVE_BASE_URL must be a loopback http(s) URL (tests only)", EXIT.USAGE));
    }
    base = override.replace(/\/+$/, "");
  } else {
    let repo;
    try {
      repo = resolveRepo(parsed.get("repo"), env);
    } catch (error) {
      return usage(refusal(error.reason ?? "repo-invalid", describeError(error), error.exitCode ?? EXIT.USAGE));
    }
    base = `https://github.com/${repo.slug}/releases/latest/download`;
  }

  const source = loadEmbeddedKeys(path.resolve(parsed.get("root") ?? REPO_ROOT));
  let result;
  if (!source.ok) {
    result = { ok: false, errors: source.errors, checks: [], manifestUrl: null };
  } else {
    try {
      result = await checkStableFeed({
        base,
        keys: source.keys,
        expectedVersion: expected.version,
        requireArches: requireArches.length > 0 ? requireArches : [...KNOWN_ARCHES],
        loopback: Boolean(override),
      });
    } catch (error) {
      result = { ok: false, errors: [toRefusal(error)], checks: [], manifestUrl: `${base}/${manifestFileName(CHANNEL)}` };
    }
  }

  if (!result.ok) {
    printRefusals(SCRIPT, result.errors);
    if (json) {
      console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: false, version: expected.version, errors: result.errors.map((e) => e.reason) }));
    }
    return exitFor(result.errors);
  }
  if (json) {
    console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: true, version: expected.version, channel: CHANNEL, manifestUrl: result.manifestUrl, checks: result.checks }));
  } else {
    console.log(`${SCRIPT}: ok (${expected.version}, ${CHANNEL}, ${Object.keys(result.manifest.installers).join(", ")})`);
    console.log(`  manifest: ${result.manifestUrl}`);
    for (const check of result.checks) {
      console.log(`  ${check.pass ? "PASS" : "FAIL"} ${check.name}${check.detail ? `: ${check.detail}` : ""}`);
    }
  }
  return EXIT.OK;
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = await main();
}
