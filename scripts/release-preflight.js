#!/usr/bin/env node
/**
 * Release preflight: clean tree, version agreement, changelog section, branch, trusted key,
 * and optionally a complete D5 artifact directory. No network.
 */
import path from "node:path";

import { EXIT, describeError, exitCodeForErrors, parseArguments, printRefusals, repoRootFrom } from "./lib/cli.js";
import { runPreflight } from "./lib/release-policy.js";

const SCRIPT = "scripts/release-preflight.js";
const REPO_ROOT = repoRootFrom(import.meta.url);

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [options]

Checks the release rules for the current checkout. It never writes a release session
and never contacts the network.

Options:
  --root <dir>            Repository root. Defaults to the parent of scripts/.
  --version <semver>      Release version. Defaults to package.json.
  --channel <stable|beta> Expected channel. Defaults to the version.
  --allow-detached        Allow a detached HEAD contained in the expected branch.
  --trusted-key <path>    Public release key to parse and report.
  --require-trusted-key   Refuse when there is no trusted key: no --trusted-key and no embedded
                          non-placeholder key in TrustedKeys.cs.
  --artifact-dir <dir>    Validate a D5 artifact directory as part of preflight.
  --json                  Print one JSON line.
  --help                  Show this help.

Exit codes: 0 pass, 1 refused, 2 usage, 3 missing input.`);
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    const parsed = parseArguments(argv, {
      values: ["root", "version", "channel", "trusted-key", "artifact-dir"],
      flags: ["allow-detached", "require-trusted-key", "json", "help"],
    });
    if (parsed.has("help")) {
      printHelp();
      return EXIT.OK;
    }
    if (parsed.positionals.length > 0) {
      throw new Error(`unexpected argument: ${parsed.positionals[0]}`);
    }
    const channel = parsed.get("channel");
    if (channel !== undefined && channel !== "stable" && channel !== "beta") {
      throw new Error("--channel must be stable or beta");
    }
    options = {
      root: path.resolve(parsed.get("root") ?? REPO_ROOT),
      version: parsed.get("version"),
      channel,
      allowDetached: parsed.has("allow-detached"),
      trustedKey: parsed.get("trusted-key"),
      requireTrustedKey: parsed.has("require-trusted-key"),
      artifactDir: parsed.get("artifact-dir") ? path.resolve(parsed.get("artifact-dir")) : null,
      json: parsed.has("json"),
    };
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    console.error(`Try: node ${SCRIPT} --help`);
    return EXIT.USAGE;
  }

  return run(options);
}

async function run(options) {
  let result;
  try {
    result = await runPreflight(options.root, options);
  } catch (error) {
    console.error(`${SCRIPT}: ${describeError(error)}`);
    return EXIT.REFUSED;
  }

  const report = {
    schema: 1,
    tool: SCRIPT,
    ok: result.ok,
    root: options.root,
    version: result.release.ok ? result.release.version : options.version ?? null,
    channel: result.release.ok ? result.release.channel : options.channel ?? null,
    commit: result.branch.commit,
    branch: result.branch.branch,
    detached: result.branch.detached,
    dirtyPaths: result.clean.dirtyPaths,
    trustedKey: result.trusted.key
      ? { path: result.trusted.key.path, keyId: result.trusted.key.keyId, fingerprint: result.trusted.key.fingerprint }
      : null,
    errors: result.errors,
    checks: result.checks,
  };

  if (options.json) {
    console.log(JSON.stringify(report));
  } else if (result.ok) {
    console.log(
      `release-preflight: ok (${report.version}, ${report.channel}, ${report.branch ?? "detached"}, ` +
        `${String(report.commit).slice(0, 12)})`,
    );
    for (const check of result.checks) {
      console.log(`  PASS ${check.name}${check.detail ? `: ${check.detail}` : ""}`);
    }
  } else {
    printRefusals("release-preflight", result.errors);
    for (const error of result.errors) {
      console.error(`  detail: [${error.reason}] ${error.detail}`);
    }
    console.error("release-preflight: no release session was written.");
  }

  return exitCodeForErrors(result.errors);
}

process.exitCode = await main();
