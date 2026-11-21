#!/usr/bin/env node
// Ranks where an E2E run spends its time, and compares two runs.
// The harness writes a top-level "timings" object into artifacts/e2e/result-<rid>-<flavor>.json (tests/Bwc.E2E/Harness/E2ETimings.cs).
// docs/failure-modes/e2e-harness.md (section 46) and docs/failure-modes/quality-gate.md (TIME-1, TIME-2) list the cases.
//
// One file: ranks the tests, the tests with the most untimed time, the sessions by executable, the wait call sites,
// the screenshot totals and the element lookup totals.
// Two files: compares the total time, the time of each test and the check id sets. A check id of the first file that is
// missing in the second is a removed or renamed check and fails the comparison. New ids are listed and allowed.
// A file without "timings" (an older run) is not an error: it prints a note and still takes part in the check id comparison.
// A file with timings from before the residual, capture and lookup fields skips those sections.
import { readFileSync } from "node:fs";
import path from "node:path";
import { isDirectExecutionOf } from "./direct-execution.mjs";

const TOP_TESTS = 15;
const TOP_WAITS = 10;
const TOP_UNTIMED = 15;
const TOP_CAPTURES = 10;
const TOP_DELTAS = 15;

const usage = `Usage:
  node scripts/e2e-timing-report.js <result.json>
  node scripts/e2e-timing-report.js <before.json> <after.json>
  node scripts/e2e-timing-report.js --self-test

One file: ranks the slowest tests, the tests with the largest untimed time, the sessions by executable (count, start and
close time), the wait call sites, the screenshot totals and the element lookup totals.
Two files: compares the total time, the per test time and the check ids. A check id of <before> that is missing in
<after> is listed and exits 1. New ids are listed and allowed.
A file without a timings object prints a note and still takes part in the check id comparison.

Options:
  --self-test   Run built-in cases on inline data (no file is read) and exit 0 when they pass
  --help        Show this help

Exit codes: 0 ok, 1 a check id was removed or the self-test failed, 2 usage error or unreadable file.
`;

function numberOrZero(value) {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function hasTimings(result) {
  return result !== null && typeof result === "object" && result.timings !== null && typeof result.timings === "object";
}

function listOf(value) {
  return Array.isArray(value) ? value : [];
}

// Tests with a duration, slowest first. A test with no duration (no clock was started for it) is left out.
export function rankTests(result, limit = TOP_TESTS) {
  if (!hasTimings(result)) {
    return [];
  }
  return listOf(result.timings.tests)
    .filter((test) => typeof test.durationMs === "number")
    .map((test) => ({ name: String(test.test ?? `${test.class}.${test.method}`), ms: test.durationMs }))
    .sort((a, b) => b.ms - a.ms || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
    .slice(0, limit);
}

// Tests with the largest untimedMs (duration minus waits, sessions and captures). A test without the field is left out,
// so an older file gives an empty list.
export function rankUntimed(result, limit = TOP_UNTIMED) {
  if (!hasTimings(result)) {
    return [];
  }
  return listOf(result.timings.tests)
    .filter((test) => typeof test.untimedMs === "number")
    .map((test) => ({
      name: String(test.test ?? `${test.class}.${test.method}`),
      untimedMs: test.untimedMs,
      durationMs: numberOrZero(test.durationMs),
      waitMs: numberOrZero(test.waitMs),
      sessionMs: numberOrZero(test.sessionMs),
      captureMs: numberOrZero(test.captureMs),
      clamped: test.untimedClamped === true,
    }))
    .sort((a, b) => b.untimedMs - a.untimedMs || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
    .slice(0, limit);
}

// The screenshot totals and the slowest capture sites. Null when the file has no capture section.
export function captureReport(result, limit = TOP_CAPTURES) {
  if (!hasTimings(result) || !Array.isArray(result.timings.capture)) {
    return null;
  }
  const sites = result.timings.capture
    .map((row) => ({
      site: `${row.file}:${row.line}`,
      calls: numberOrZero(row.calls),
      totalMs: numberOrZero(row.totalMs),
      maxMs: numberOrZero(row.maxMs),
      unsettled: numberOrZero(row.unsettled),
    }))
    .sort((a, b) => b.totalMs - a.totalMs || (a.site < b.site ? -1 : a.site > b.site ? 1 : 0));
  return {
    calls: sites.reduce((sum, row) => sum + row.calls, 0),
    totalMs: sites.reduce((sum, row) => sum + row.totalMs, 0),
    unsettled: sites.reduce((sum, row) => sum + row.unsettled, 0),
    sites: sites.slice(0, limit),
  };
}

// The element lookup counters, the largest total first. Null when the file has no lookups section.
export function lookupReport(result) {
  if (!hasTimings(result) || !Array.isArray(result.timings.lookups)) {
    return null;
  }
  return result.timings.lookups
    .map((row) => ({ name: String(row.name), calls: numberOrZero(row.calls), totalMs: numberOrZero(row.totalMs) }))
    .sort((a, b) => b.totalMs - a.totalMs || (a.name < b.name ? -1 : 1));
}

// One row per executable: session count, time to the main window, time spent in Close, kills, sessions only disposed.
export function rankSessionsByExe(result) {
  if (!hasTimings(result)) {
    return [];
  }
  const byExe = new Map();
  for (const session of listOf(result.timings.sessions)) {
    const exe = String(session.exe ?? "(unknown)");
    const row = byExe.get(exe) ?? { exe, count: 0, startMs: 0, maxStartMs: 0, closeMs: 0, killed: 0, notClosed: 0 };
    row.count += 1;
    row.startMs += numberOrZero(session.startMs);
    row.maxStartMs = Math.max(row.maxStartMs, numberOrZero(session.startMs));
    row.closeMs += numberOrZero(session.closeMs);
    row.killed += session.killed === true ? 1 : 0;
    row.notClosed += session.closedViaClose === false ? 1 : 0;
    byExe.set(exe, row);
  }
  return [...byExe.values()].sort((a, b) => b.startMs + b.closeMs - (a.startMs + a.closeMs) || (a.exe < b.exe ? -1 : 1));
}

// Wait call sites, the largest total first.
export function rankWaits(result, limit = TOP_WAITS) {
  if (!hasTimings(result)) {
    return [];
  }
  return listOf(result.timings.waits)
    .map((wait) => ({
      site: `${wait.file}:${wait.line}`,
      calls: numberOrZero(wait.calls),
      totalMs: numberOrZero(wait.totalMs),
      maxMs: numberOrZero(wait.maxMs),
      timeouts: numberOrZero(wait.timeouts),
      nestedCalls: numberOrZero(wait.nestedCalls),
    }))
    .sort((a, b) => b.totalMs - a.totalMs || (a.site < b.site ? -1 : a.site > b.site ? 1 : 0))
    .slice(0, limit);
}

function nestedText(wait) {
  return wait.nestedCalls > 0 ? `, nested ${wait.nestedCalls}` : "";
}

// The untimed, capture and lookup sections shared by the report and the run summary. Empty for an older file.
function residualLines(result, captureSites) {
  const lines = [];
  const untimed = rankUntimed(result);
  if (untimed.length > 0) {
    const totals = result.timings.totals ?? {};
    lines.push(
      "",
      `Largest untimed time (top ${TOP_UNTIMED}; duration minus waits, sessions and captures, total ${ms(totals.untimedMs)} ms):`,
    );
    for (const test of untimed) {
      lines.push(
        `  ${pad(ms(test.untimedMs), 11)} ms  ${test.name}  (duration ${ms(test.durationMs)}, waits ${ms(test.waitMs)}, ` +
          `sessions ${ms(test.sessionMs)}, captures ${ms(test.captureMs)}${test.clamped ? ", clamped at 0" : ""})`,
      );
    }
  }
  const capture = captureReport(result, captureSites);
  if (capture !== null) {
    lines.push("", `Screenshots: ${capture.calls} calls, ${ms(capture.totalMs)} ms, ${capture.unsettled} hit the settle timeout`);
    for (const site of capture.sites) {
      lines.push(
        `  ${pad(ms(site.totalMs), 11)} ms  ${site.site}  calls ${site.calls}, max ${ms(site.maxMs)} ms, unsettled ${site.unsettled}`,
      );
    }
  }
  const lookups = lookupReport(result);
  if (lookups !== null) {
    lines.push("", "Element lookups:");
    for (const row of lookups) {
      lines.push(`  ${pad(ms(row.totalMs), 11)} ms  ${row.name}  calls ${row.calls}`);
    }
  }
  return lines;
}

export function checkIds(result) {
  const ids = new Set();
  for (const check of listOf(result?.checks)) {
    if (check && typeof check.id === "string") {
      ids.add(check.id);
    }
  }
  return ids;
}

function ms(value) {
  return numberOrZero(value).toFixed(1);
}

function pad(text, width) {
  return String(text).padStart(width);
}

// The report of one result as lines. A result without timings gives one line that says so.
export function reportLines(result, label = "result") {
  if (!hasTimings(result)) {
    return [`${label}: no timings object (a run from before the timing record). Nothing to rank.`];
  }
  const lines = [];
  const totals = result.timings.totals ?? {};
  lines.push(
    `${label}: tests ${ms(totals.testMs)} ms, session start ${ms(totals.sessionStartMs)} ms, ` +
      `session close ${ms(totals.sessionCloseMs)} ms, waits ${ms(totals.waitMs)} ms`,
  );
  if (result.timings.truncated === true) {
    lines.push("  timings was reduced to its totals (the record was too large).");
  }
  for (const problem of listOf(result.timings.problems)) {
    lines.push(`  recorder problem: ${problem}`);
  }

  lines.push("", `Slowest tests (top ${TOP_TESTS}):`);
  for (const test of rankTests(result)) {
    lines.push(`  ${pad(ms(test.ms), 11)} ms  ${test.name}`);
  }

  lines.push("", "Sessions by executable:");
  for (const row of rankSessionsByExe(result)) {
    lines.push(
      `  ${row.exe}: ${row.count} sessions, start ${ms(row.startMs)} ms (max ${ms(row.maxStartMs)}), ` +
        `close ${ms(row.closeMs)} ms, killed ${row.killed}, only disposed ${row.notClosed}`,
    );
  }

  lines.push("", `Slowest wait sites (top ${TOP_WAITS}, by total):`);
  for (const wait of rankWaits(result)) {
    lines.push(
      `  ${pad(ms(wait.totalMs), 11)} ms  ${wait.site}  calls ${wait.calls}, max ${ms(wait.maxMs)} ms, timeouts ${wait.timeouts}${nestedText(wait)}`,
    );
  }
  lines.push(...residualLines(result, TOP_CAPTURES));
  return lines;
}

// The lines scripts/test-e2e.js prints after a run. Empty when the file has no timings.
export function summaryLines(result) {
  if (!hasTimings(result)) {
    return [];
  }
  const lines = ["", "E2E timing (artifact timings object; node scripts/e2e-timing-report.js <file> for the full ranking)"];
  lines.push(`slowest tests (top ${TOP_TESTS}):`);
  for (const test of rankTests(result)) {
    lines.push(`  ${pad(ms(test.ms), 11)} ms  ${test.name}`);
  }
  lines.push("sessions by executable:");
  for (const row of rankSessionsByExe(result)) {
    lines.push(`  ${row.exe}: ${row.count} sessions, start ${ms(row.startMs)} ms, close ${ms(row.closeMs)} ms`);
  }
  lines.push(`slowest wait sites (top ${TOP_WAITS}, by total):`);
  for (const wait of rankWaits(result)) {
    lines.push(`  ${pad(ms(wait.totalMs), 11)} ms  ${wait.site}  calls ${wait.calls}, timeouts ${wait.timeouts}${nestedText(wait)}`);
  }
  lines.push(...residualLines(result, 5));
  return lines;
}

function testDurations(result) {
  const durations = new Map();
  for (const test of listOf(result?.timings?.tests)) {
    if (typeof test.durationMs === "number") {
      const name = String(test.test ?? `${test.class}.${test.method}`);
      durations.set(name, (durations.get(name) ?? 0) + test.durationMs);
    }
  }
  return durations;
}

// Compares two results. removed is the sorted list of check ids of before that are missing in after.
export function compareResults(before, after, limit = TOP_DELTAS) {
  const beforeIds = checkIds(before);
  const afterIds = checkIds(after);
  const removed = [...beforeIds].filter((id) => !afterIds.has(id)).sort();
  const added = [...afterIds].filter((id) => !beforeIds.has(id)).sort();

  const deltas = [];
  if (hasTimings(before) && hasTimings(after)) {
    const beforeTests = testDurations(before);
    const afterTests = testDurations(after);
    for (const [name, afterMs] of afterTests) {
      if (beforeTests.has(name)) {
        const beforeMs = beforeTests.get(name);
        deltas.push({ name, beforeMs, afterMs, deltaMs: afterMs - beforeMs });
      }
    }
    deltas.sort((a, b) => Math.abs(b.deltaMs) - Math.abs(a.deltaMs) || (a.name < b.name ? -1 : 1));
  }

  return {
    removed,
    added,
    deltas: deltas.slice(0, limit),
    comparedTests: deltas.length,
    totals: {
      beforeRunMs: typeof before?.durationMs === "number" ? before.durationMs : null,
      afterRunMs: typeof after?.durationMs === "number" ? after.durationMs : null,
      beforeTestMs: hasTimings(before) ? numberOrZero(before.timings.totals?.testMs) : null,
      afterTestMs: hasTimings(after) ? numberOrZero(after.timings.totals?.testMs) : null,
      beforeSessionStartMs: hasTimings(before) ? numberOrZero(before.timings.totals?.sessionStartMs) : null,
      afterSessionStartMs: hasTimings(after) ? numberOrZero(after.timings.totals?.sessionStartMs) : null,
      beforeSessionCloseMs: hasTimings(before) ? numberOrZero(before.timings.totals?.sessionCloseMs) : null,
      afterSessionCloseMs: hasTimings(after) ? numberOrZero(after.timings.totals?.sessionCloseMs) : null,
      beforeWaitMs: hasTimings(before) ? numberOrZero(before.timings.totals?.waitMs) : null,
      afterWaitMs: hasTimings(after) ? numberOrZero(after.timings.totals?.waitMs) : null,
    },
  };
}

function signed(value) {
  return `${value >= 0 ? "+" : "-"}${Math.abs(value).toFixed(1)}`;
}

function totalLine(label, beforeValue, afterValue) {
  if (beforeValue === null || afterValue === null) {
    return `  ${label}: not available in both files`;
  }
  return `  ${label}: ${ms(beforeValue)} -> ${ms(afterValue)} ms (${signed(afterValue - beforeValue)} ms)`;
}

// The comparison as lines and the exit code: 1 when a check id was removed.
export function compareLines(before, after, beforeLabel = "before", afterLabel = "after") {
  const comparison = compareResults(before, after);
  const lines = [`Comparing ${beforeLabel} -> ${afterLabel}`, "", "Total time:"];
  lines.push(totalLine("whole run", comparison.totals.beforeRunMs, comparison.totals.afterRunMs));
  lines.push(totalLine("tests", comparison.totals.beforeTestMs, comparison.totals.afterTestMs));
  lines.push(totalLine("session start", comparison.totals.beforeSessionStartMs, comparison.totals.afterSessionStartMs));
  lines.push(totalLine("session close", comparison.totals.beforeSessionCloseMs, comparison.totals.afterSessionCloseMs));
  lines.push(totalLine("waits", comparison.totals.beforeWaitMs, comparison.totals.afterWaitMs));

  lines.push("", `Per test change (top ${TOP_DELTAS} of ${comparison.comparedTests} tests in both files):`);
  if (!hasTimings(before) || !hasTimings(after)) {
    lines.push("  skipped: a file has no timings object (a run from before the timing record).");
  }
  for (const delta of comparison.deltas) {
    lines.push(`  ${pad(signed(delta.deltaMs), 11)} ms  ${delta.name}  (${ms(delta.beforeMs)} -> ${ms(delta.afterMs)})`);
  }

  lines.push("", `Check ids: ${checkIds(before).size} before, ${checkIds(after).size} after`);
  lines.push(`  new in ${afterLabel}: ${comparison.added.length} (allowed)`);
  for (const id of comparison.added) {
    lines.push(`    + ${id}`);
  }
  lines.push(`  missing in ${afterLabel}: ${comparison.removed.length}`);
  for (const id of comparison.removed) {
    lines.push(`    - ${id}`);
  }
  if (comparison.removed.length > 0) {
    lines.push("", `FAIL: ${comparison.removed.length} check id(s) of ${beforeLabel} are missing in ${afterLabel} (a removed or renamed check).`);
  }
  return { lines, exitCode: comparison.removed.length > 0 ? 1 : 0 };
}

function readResult(file, io) {
  let result;
  try {
    result = JSON.parse(io.read(file));
  } catch (error) {
    throw new Error(`Could not read ${file}: ${error.message}`);
  }
  if (result === null || typeof result !== "object" || Array.isArray(result)) {
    throw new Error(`${file} is not a result object.`);
  }
  return result;
}

// Runs the command line with the given file reader and output functions. Returns the exit code.
export function runCli(args, io) {
  const files = args.filter((arg) => !arg.startsWith("--"));
  const unknown = args.filter((arg) => arg.startsWith("--") && arg !== "--help");
  if (args.includes("--help")) {
    io.out(usage);
    return 0;
  }
  if (unknown.length > 0 || files.length < 1 || files.length > 2) {
    io.err(unknown.length > 0 ? `Unknown argument '${unknown[0]}'.` : "Give one result file, or two to compare.");
    io.err(usage);
    return 2;
  }

  try {
    if (files.length === 1) {
      for (const line of reportLines(readResult(files[0], io), path.basename(files[0]))) {
        io.out(line);
      }
      return 0;
    }
    const { lines, exitCode } = compareLines(
      readResult(files[0], io),
      readResult(files[1], io),
      path.basename(files[0]),
      path.basename(files[1]),
    );
    for (const line of lines) {
      io.out(line);
    }
    return exitCode;
  } catch (error) {
    io.err(error.message);
    return 2;
  }
}

function sampleResult({ ids, tests, withTimings = true, withResidual = false, durationMs = 1000 }) {
  const result = { schema: 2, pass: true, durationMs, checks: ids.map((id) => ({ id, pass: true, notes: "" })) };
  if (withTimings) {
    result.timings = {
      tests: tests.map(([name, ms]) => ({ test: name, class: name.split(".")[0], method: name.split(".")[1], durationMs: ms, outcome: "Passed", sessions: [] })),
      sessions: [
        { id: 1, exe: "BwcRegedit.exe", test: "A.One", startMs: 800, closeMs: 100, killed: false, closedViaClose: true },
        { id: 2, exe: "BwcHub.exe", test: "A.Two", startMs: 300, closeMs: 50, killed: false, closedViaClose: false },
        { id: 3, exe: "BwcRegedit.exe", test: "A.Two", startMs: 700, closeMs: 4000, killed: true, closedViaClose: true },
      ],
      waits: [
        { file: "Small.cs", line: 3, calls: 10, totalMs: 20, maxMs: 5, timeouts: 0 },
        { file: "Big.cs", line: 9, calls: 2, totalMs: 9000, maxMs: 5000, timeouts: 1 },
        { file: "Mid.cs", line: 4, calls: 5, totalMs: 400, maxMs: 100, timeouts: 0 },
      ],
      totals: { testMs: tests.reduce((sum, [, value]) => sum + value, 0), sessionStartMs: 1800, sessionCloseMs: 4150, waitMs: 9420 },
      problems: [],
    };
    if (withResidual) {
      // untimedMs of a test: the duration minus 100 ms of waits, so the order differs from the duration order.
      tests.forEach(([, value], index) => {
        const row = result.timings.tests[index];
        row.waitMs = 100;
        row.sessionMs = 10;
        row.captureMs = 5;
        row.untimedMs = Math.max(0, value - 115);
        row.untimedClamped = value < 100;
      });
      result.timings.capture = [
        { file: "Shots.cs", line: 7, calls: 3, totalMs: 6000, maxMs: 3000, unsettled: 1 },
        { file: "Other.cs", line: 2, calls: 9, totalMs: 900, maxMs: 200, unsettled: 0 },
      ];
      result.timings.lookups = [
        { name: "ContextMenuDriver.DesktopFindAllDescendants", calls: 5, totalMs: 50 },
        { name: "EditingDriver.FindElement", calls: 1717, totalMs: 90000 },
      ];
      result.timings.waits[1].nestedCalls = 4;
      result.timings.totals.untimedMs = result.timings.tests.reduce((sum, row) => sum + row.untimedMs, 0);
    }
  }
  return result;
}

function runSelfTest() {
  const failures = [];
  const expect = (name, condition, detail = "") => {
    if (!condition) {
      failures.push(`${name}${detail ? `: ${detail}` : ""}`);
    }
  };
  const capture = (files, args) => {
    const out = [];
    const err = [];
    const code = runCli(args, {
      read: (file) => {
        if (!(file in files)) {
          throw new Error("missing inline file");
        }
        return JSON.stringify(files[file]);
      },
      out: (line) => out.push(line),
      err: (line) => err.push(line),
    });
    return { code, out: out.join("\n"), err: err.join("\n") };
  };

  const base = sampleResult({ ids: ["a", "b", "c"], tests: [["A.One", 50], ["A.Two", 5000], ["A.Three", 700]] });

  // Ranking: tests slowest first, sessions by time spent, wait sites by total.
  expect("ranking-tests", rankTests(base).map((test) => test.name).join(",") === "A.Two,A.Three,A.One");
  expect("ranking-tests-limit", rankTests(base, 2).length === 2);
  const byExe = rankSessionsByExe(base);
  expect("ranking-sessions-order", byExe.map((row) => row.exe).join(",") === "BwcRegedit.exe,BwcHub.exe");
  expect("ranking-sessions-sums", byExe[0].count === 2 && byExe[0].startMs === 1500 && byExe[0].closeMs === 4100 && byExe[0].killed === 1);
  expect("ranking-sessions-disposed", byExe[1].notClosed === 1);
  expect("ranking-waits", rankWaits(base).map((wait) => wait.site).join(",") === "Big.cs:9,Mid.cs:4,Small.cs:3");
  const noDuration = sampleResult({ ids: ["a"], tests: [["A.One", 10]] });
  noDuration.timings.tests.push({ test: "A.NoClock", class: "A", method: "NoClock", durationMs: null, outcome: "Passed", sessions: [] });
  expect("ranking-null-duration-left-out", rankTests(noDuration).length === 1);

  // The residual, capture and lookup sections: ranked by untimed time, not by duration.
  const withParts = sampleResult({
    ids: ["a"],
    tests: [["A.Slow", 9000], ["A.Mid", 500], ["A.Tiny", 50]].concat(Array.from({ length: 20 }, (_, i) => [`B.T${i}`, 1000 + i])),
    withResidual: true,
  });
  const untimed = rankUntimed(withParts);
  expect("untimed-limit-15", untimed.length === 15);
  expect("untimed-order", untimed[0].name === "A.Slow" && untimed[0].untimedMs === 8885 && untimed[1].name === "B.T19");
  expect("untimed-clamp-flag", rankUntimed(withParts, 30).find((row) => row.name === "A.Tiny").clamped === true);
  const captureInfo = captureReport(withParts);
  expect("capture-totals", captureInfo.calls === 12 && captureInfo.totalMs === 6900 && captureInfo.unsettled === 1);
  expect("capture-order", captureInfo.sites[0].site === "Shots.cs:7");
  const lookupInfo = lookupReport(withParts);
  expect("lookup-order", lookupInfo[0].name === "EditingDriver.FindElement" && lookupInfo[0].calls === 1717);
  expect("nested-calls-ranked", rankWaits(withParts).find((wait) => wait.site === "Big.cs:9").nestedCalls === 4);
  const fullReport = capture({ "p.json": withParts }, ["p.json"]);
  expect("report-untimed-section", fullReport.code === 0 && fullReport.out.includes("Largest untimed time"));
  expect("report-capture-section", fullReport.out.includes("Screenshots: 12 calls, 6900.0 ms, 1 hit the settle timeout") && fullReport.out.includes("Shots.cs:7"));
  expect("report-lookup-section", fullReport.out.includes("EditingDriver.FindElement  calls 1717"));
  expect("report-nested-wait", fullReport.out.includes("nested 4"));
  const summary = summaryLines(withParts).join("\n");
  expect("summary-has-new-sections", summary.includes("Largest untimed time") && summary.includes("Screenshots: 12 calls") && summary.includes("Element lookups"));
  // A file with timings but none of the new fields still prints, without those sections.
  expect("no-residual-fields", rankUntimed(base).length === 0 && captureReport(base) === null && lookupReport(base) === null);
  const plain = reportLines(base).join("\n");
  expect("no-residual-sections-printed", !plain.includes("untimed") && !plain.includes("Screenshots") && !plain.includes("Element lookups"));
  expect("no-residual-summary", !summaryLines(base).join("\n").includes("Screenshots"));

  // One file prints a ranking and exits 0.
  const single = capture({ "one.json": base }, ["one.json"]);
  expect("single-exit", single.code === 0, single.err);
  expect("single-output", single.out.indexOf("A.Two") < single.out.indexOf("A.Three") && single.out.includes("Big.cs:9"));

  // A removed check id fails and is listed.
  const withoutB = sampleResult({ ids: ["a", "c"], tests: [["A.One", 40]] });
  const removed = capture({ "before.json": base, "after.json": withoutB }, ["before.json", "after.json"]);
  expect("removed-exit", removed.code === 1);
  expect("removed-listed", removed.out.includes("- b") && removed.out.includes("FAIL"));

  // A renamed id is a removed id plus a new id, so it fails too.
  const renamed = sampleResult({ ids: ["a", "b2", "c"], tests: [["A.One", 40]] });
  expect("renamed-exit", capture({ "x.json": base, "y.json": renamed }, ["x.json", "y.json"]).code === 1);

  // An added id passes and is listed.
  const withD = sampleResult({ ids: ["a", "b", "c", "d"], tests: [["A.One", 40], ["A.Two", 4000]] });
  const added = capture({ "before.json": base, "after.json": withD }, ["before.json", "after.json"]);
  expect("added-exit", added.code === 0, added.err);
  expect("added-listed", added.out.includes("+ d") && !added.out.includes("FAIL"));
  expect("delta-top", added.out.indexOf("A.Two") > 0 && added.out.includes("-1000.0 ms"));

  // An old file without timings still compares check ids, and is not an error.
  const old = sampleResult({ ids: ["a", "b", "c"], tests: [], withTimings: false });
  const oldOk = capture({ "old.json": old, "new.json": withD }, ["old.json", "new.json"]);
  expect("old-file-passes", oldOk.code === 0, oldOk.err);
  expect("old-file-says-skipped", oldOk.out.includes("skipped"));
  const oldRemoved = capture({ "old.json": old, "new.json": withoutB }, ["old.json", "new.json"]);
  expect("old-file-removed-id", oldRemoved.code === 1 && oldRemoved.out.includes("- b"));
  const oldSingle = capture({ "old.json": old }, ["old.json"]);
  expect("old-file-single", oldSingle.code === 0 && oldSingle.out.includes("no timings"));
  expect("old-file-summary-empty", summaryLines(old).length === 0);
  expect("new-file-summary", summaryLines(base).some((line) => line.includes("A.Two")));

  // Usage errors.
  expect("no-args", capture({}, []).code === 2);
  expect("three-files", capture({}, ["a", "b", "c"]).code === 2);
  expect("unreadable", capture({}, ["missing.json"]).code === 2);
  expect("unknown-option", capture({}, ["--nope"]).code === 2);

  if (failures.length > 0) {
    for (const failure of failures) {
      console.error(`SELF-TEST FAIL: ${failure}`);
    }
    return 1;
  }
  console.log("e2e-timing-report self-test passed.");
  return 0;
}

function main() {
  const args = process.argv.slice(2);
  if (args.includes("--self-test")) {
    return runSelfTest();
  }
  return runCli(args, {
    read: (file) => readFileSync(path.resolve(file), "utf8"),
    out: (text) => console.log(text),
    err: (text) => console.error(text),
  });
}

if (isDirectExecutionOf(import.meta.url)) {
  process.exitCode = main();
}
