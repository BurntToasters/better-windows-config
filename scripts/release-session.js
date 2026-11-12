#!/usr/bin/env node
/**
 * Records and verifies the local release session. The session is a proof of the preflight
 * inputs, not a signature. It lives under artifacts/release/session.json and is git-ignored.
 */
import fs from "node:fs";
import path from "node:path";

import { EXIT, describeError, exitCodeForErrors, parseArguments, printRefusals, repoRootFrom, writeJson } from "./lib/cli.js";
import { currentCommit, runPreflight, sessionPath } from "./lib/release-policy.js";

const SCRIPT = "scripts/release-session.js";
const REPO_ROOT = repoRootFrom(import.meta.url);
const DEFAULT_MAX_AGE_HOURS = 24;

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} create [options]
  node ${SCRIPT} verify [options]
  node ${SCRIPT} show [--session <path>] [--json]

Creates or verifies artifacts/release/session.json for one version and channel.

Options:
  --root <dir>            Repository root. Defaults to the parent of scripts/.
  --version <semver>      Release version. Defaults to package.json.
  --channel <stable|beta> Expected channel. Defaults to the version.
  --allow-detached        Allow a detached HEAD contained in the expected branch.
  --trusted-key <path>    Public release key to record and verify.
  --require-trusted-key   Refuse when --trusted-key is absent.
  --artifact-dir <dir>    Also validate a D5 artifact directory.
  --max-age-hours <n>     Session lifetime. Default ${DEFAULT_MAX_AGE_HOURS}.
  --session <path>        Session file. Default artifacts/release/session.json.
  --force                 create: replace an existing session.
  --json                  Print one JSON line.
  --help                  Show this help.

Exit codes: 0 pass, 1 refused or expired, 2 usage, 3 missing session or input.`);
}

function parseOptions(argv) {
  const parsed = parseArguments(argv, {
    values: ["root", "version", "channel", "trusted-key", "artifact-dir", "max-age-hours", "session"],
    flags: ["allow-detached", "require-trusted-key", "force", "json", "help"],
  });
  if (parsed.has("help")) return { help: true };
  if (parsed.positionals.length > 1) {
    throw new Error(`unexpected argument: ${parsed.positionals[1]}`);
  }
  const command = parsed.positionals[0] ?? "verify";
  if (!["create", "verify", "show"].includes(command)) {
    throw new Error(`expected create, verify, or show; received ${command}`);
  }
  const channel = parsed.get("channel");
  if (channel !== undefined && channel !== "stable" && channel !== "beta") {
    throw new Error("--channel must be stable or beta");
  }
  const maxAgeHours = parsed.get("max-age-hours");
  if (maxAgeHours !== undefined) {
    const number = Number(maxAgeHours);
    if (!Number.isFinite(number) || number <= 0) {
      throw new Error("--max-age-hours must be a positive number");
    }
  }
  return {
    command,
    root: path.resolve(parsed.get("root") ?? REPO_ROOT),
    version: parsed.get("version"),
    channel,
    allowDetached: parsed.has("allow-detached"),
    trustedKey: parsed.get("trusted-key"),
    requireTrustedKey: parsed.has("require-trusted-key"),
    artifactDir: parsed.get("artifact-dir") ? path.resolve(parsed.get("artifact-dir")) : null,
    maxAgeHours: maxAgeHours === undefined ? DEFAULT_MAX_AGE_HOURS : Number(maxAgeHours),
    session: parsed.get("session") ? path.resolve(parsed.get("session")) : null,
    force: parsed.has("force"),
    json: parsed.has("json"),
  };
}

function resolveSessionPath(options) {
  return options.session ?? sessionPath(options.root);
}

function readSession(filePath) {
  if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
    return {
      ok: false,
      errors: [
        {
          reason: "missing-session",
          detail: `no release session at ${filePath}; run release-session.js create first`,
          exitCode: EXIT.MISSING,
        },
      ],
    };
  }
  try {
    return { ok: true, session: JSON.parse(fs.readFileSync(filePath, "utf8")) };
  } catch (error) {
    return {
      ok: false,
      errors: [
        {
          reason: "missing-session",
          detail: `release session is not valid JSON: ${describeError(error)}`,
          exitCode: EXIT.MISSING,
        },
      ],
    };
  }
}

function sessionKeySummary(key) {
  if (!key) return null;
  return {
    path: key.path,
    keyId: key.keyId,
    fingerprint: key.fingerprint,
    testKey: Boolean(key.testKey),
  };
}

async function create(options) {
  const sessionFile = resolveSessionPath(options);
  if (!options.force && fs.existsSync(sessionFile)) {
    console.error(`${SCRIPT}: REFUSED [session-exists]: ${sessionFile}; pass --force to replace it`);
    return EXIT.REFUSED;
  }

  const result = await runPreflight(options.root, options);
  if (!result.ok) {
    if (options.json) {
      console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: false, errors: result.errors, checks: result.checks }));
    } else {
      printRefusals("release-session", result.errors);
      for (const error of result.errors) {
        console.error(`  detail: [${error.reason}] ${error.detail}`);
      }
      console.error("release-session: no session was written.");
    }
    return exitCodeForErrors(result.errors);
  }

  const session = {
    schema: 1,
    product: "Better Windows Config",
    version: result.release.version,
    channel: result.release.channel,
    commit: result.branch.commit,
    branch: result.branch.branch,
    detached: result.branch.detached,
    packageVersion: result.release.packageVersion,
    changelogHeading: result.changelog?.heading ?? null,
    trustedKey: sessionKeySummary(result.trusted.key),
    startedUtc: new Date().toISOString(),
    maxAgeHours: options.maxAgeHours,
  };
  writeJson(sessionFile, session);

  if (options.json) {
    console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: true, session, path: sessionFile }));
  } else {
    console.log(`release-session: created ${sessionFile}`);
    console.log(`  version: ${session.version}`);
    console.log(`  channel: ${session.channel}`);
    console.log(`  commit: ${session.commit}`);
    console.log(`  branch: ${session.branch ?? "detached"}`);
  }
  return EXIT.OK;
}

async function verify(options) {
  const sessionFile = resolveSessionPath(options);
  const loaded = readSession(sessionFile);
  if (!loaded.ok) {
    if (options.json) {
      console.log(JSON.stringify({ schema: 1, tool: SCRIPT, ok: false, errors: loaded.errors }));
    } else {
      printRefusals("release-session", loaded.errors);
    }
    return exitCodeForErrors(loaded.errors);
  }

  const session = loaded.session;
  const errors = [];
  const checks = [];

  if (session.schema !== 1 || typeof session.version !== "string" || typeof session.channel !== "string") {
    errors.push({ reason: "session-mismatch", detail: "session schema, version, or channel is invalid", exitCode: EXIT.REFUSED });
  }

  const requestedVersion = options.version ?? session.version;
  const requestedChannel = options.channel ?? session.channel;
  if (session.version !== requestedVersion) {
    errors.push({
      reason: "session-mismatch",
      detail: `session is for ${session.version}, not ${requestedVersion}`,
      exitCode: EXIT.REFUSED,
    });
  }
  if (session.channel !== requestedChannel) {
    errors.push({
      reason: "session-mismatch",
      detail: `session is for ${session.channel}, not ${requestedChannel}`,
      exitCode: EXIT.REFUSED,
    });
  }

  const head = currentCommit(options.root);
  if (session.commit !== head) {
    errors.push({
      reason: "session-commit-mismatch",
      detail: `session commit ${String(session.commit).slice(0, 12)} is not HEAD ${head.slice(0, 12)}`,
      exitCode: EXIT.REFUSED,
    });
  } else {
    checks.push({ name: "session-commit", pass: true, detail: head.slice(0, 12) });
  }

  const started = Date.parse(session.startedUtc);
  const maxAgeHours = options.maxAgeHours ?? session.maxAgeHours ?? DEFAULT_MAX_AGE_HOURS;
  if (!Number.isFinite(started)) {
    errors.push({ reason: "session-mismatch", detail: "session has no valid startedUtc", exitCode: EXIT.REFUSED });
  } else {
    const ageHours = (Date.now() - started) / (60 * 60 * 1000);
    if (ageHours < 0) {
      errors.push({ reason: "session-mismatch", detail: "session start time is in the future", exitCode: EXIT.REFUSED });
    } else if (ageHours > maxAgeHours) {
      errors.push({
        reason: "session-expired",
        detail: `session is ${ageHours.toFixed(1)} hours old, over the ${maxAgeHours} hour limit`,
        exitCode: EXIT.REFUSED,
      });
    } else {
      checks.push({ name: "session-age", pass: true, detail: `${ageHours.toFixed(1)}h` });
    }
  }

  const result = await runPreflight(options.root, {
    ...options,
    version: requestedVersion,
    channel: requestedChannel,
  });
  errors.push(...result.errors);
  checks.push(...result.checks);

  if (session.trustedKey?.fingerprint && result.trusted.key?.fingerprint) {
    if (session.trustedKey.fingerprint !== result.trusted.key.fingerprint) {
      errors.push({
        reason: "session-mismatch",
        detail: "the trusted key fingerprint does not match the session",
        exitCode: EXIT.REFUSED,
      });
    } else {
      checks.push({ name: "session-trusted-key", pass: true, detail: session.trustedKey.keyId });
    }
  }

  const report = { schema: 1, tool: SCRIPT, ok: errors.length === 0, path: sessionFile, session, errors, checks };
  if (options.json) {
    console.log(JSON.stringify(report));
  } else if (report.ok) {
    console.log(`release-session: ok (${session.version}, ${session.channel}, ${session.commit.slice(0, 12)})`);
    for (const check of checks) {
      console.log(`  PASS ${check.name}${check.detail ? `: ${check.detail}` : ""}`);
    }
  } else {
    printRefusals("release-session", errors);
    for (const error of errors) {
      console.error(`  detail: [${error.reason}] ${error.detail}`);
    }
  }
  return exitCodeForErrors(errors);
}

function show(options) {
  const sessionFile = resolveSessionPath(options);
  const loaded = readSession(sessionFile);
  if (!loaded.ok) {
    printRefusals("release-session", loaded.errors);
    return exitCodeForErrors(loaded.errors);
  }
  console.log(options.json ? JSON.stringify(loaded.session) : `${JSON.stringify(loaded.session, null, 2)}`);
  return EXIT.OK;
}

async function main(argv = process.argv.slice(2)) {
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
  if (options.command === "create") return create(options);
  if (options.command === "show") return show(options);
  return verify(options);
}

process.exitCode = await main();
