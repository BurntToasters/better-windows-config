import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pipeline } from "node:stream/promises";

import { formatCommand, writeJson } from "./cli.js";
import { git } from "./release-policy.js";

export function resolveRepo(root, explicit) {
  let raw = explicit || process.env.GH_REPO || "";
  if (!raw) {
    const remote = git(root, ["config", "--get", "remote.origin.url"], { allowFailure: true });
    if (remote) {
      const match = remote.match(/github\.com[:/]([^/]+)\/([^/]+?)(?:\.git)?$/i);
      if (match) raw = `${match[1]}/${match[2]}`;
    }
  }
  if (!raw) {
    // No default repository: a release must name its target, so it cannot reach the wrong one.
    const failure = new Error(
      "no GitHub repository: pass --repo owner/name, set GH_REPO, or add a GitHub origin remote",
    );
    failure.reason = "repo-invalid";
    failure.exitCode = 2;
    throw failure;
  }
  const match = raw.match(/^([A-Za-z0-9_.-]+)\/([A-Za-z0-9_.-]+)$/);
  if (!match) {
    const failure = new Error(`--repo must be owner/name; received ${raw}`);
    failure.reason = "repo-invalid";
    failure.exitCode = 2;
    throw failure;
  }
  return { owner: match[1], name: match[2], slug: `${match[1]}/${match[2]}`, api: `/repos/${match[1]}/${match[2]}` };
}

/**
 * True only when a draft asset's SHA-256 is known and equals the local file's. Size is never used:
 * two files of the same size can differ. A missing remote hash is a mismatch, and the caller
 * downloads the asset and hashes it before deciding.
 */
export function assetBytesMatch({ remoteSha256, localSha256 }) {
  return typeof remoteSha256 === "string" && remoteSha256.length === 64 && remoteSha256 === localSha256;
}

export function tagForVersion(version) {
  return `v${version}`;
}

export function isPrereleaseVersion(version) {
  return /-beta\.\d+$/.test(String(version || ""));
}

export function ghListReleasesArgs(repo) {
  return ["api", "--paginate", "--slurp", `${repo.api}/releases?per_page=100`];
}

export function ghCreateReleaseArgs(repo, bodyFile) {
  return ["api", "--method", "POST", `${repo.api}/releases`, "--input", bodyFile];
}

export function ghEditReleaseArgs(repo, releaseId, bodyFile) {
  return ["api", "--method", "PATCH", `${repo.api}/releases/${releaseId}`, "--input", bodyFile];
}

export function ghGetReleaseArgs(repo, releaseId) {
  return ["api", `${repo.api}/releases/${releaseId}`];
}

export function ghUploadAssetArgs(repo, releaseId, filePath) {
  const name = path.basename(filePath);
  return [
    "api",
    "--method",
    "POST",
    "-H",
    "Content-Type: application/octet-stream",
    "--input",
    filePath,
    `${repo.api}/releases/${releaseId}/assets?name=${encodeURIComponent(name)}`,
  ];
}

export function ghDeleteAssetArgs(repo, assetId) {
  return ["api", "--method", "DELETE", `${repo.api}/releases/assets/${assetId}`];
}

export function ghDownloadAssetArgs(repo, assetId) {
  return ["api", "-H", "Accept: application/octet-stream", `${repo.api}/releases/assets/${assetId}`];
}

export function displayGh(args) {
  return formatCommand("gh", args);
}

export function draftBody({ tag, version, commit, notesBody, prerelease }) {
  return {
    tag_name: tag,
    target_commitish: commit,
    name: version,
    body: notesBody,
    draft: true,
    prerelease: Boolean(prerelease),
  };
}

export function publishBody({ prerelease }) {
  return { draft: false, prerelease: Boolean(prerelease) };
}

export function writeGhBody(filePath, body) {
  writeJson(filePath, body);
  return filePath;
}

export function selectRelease(releases, tag) {
  const matching = (releases ?? []).filter((release) => release && release.tag_name === tag);
  const drafts = matching.filter((release) => release.draft);
  if (drafts.length > 1) {
    return {
      ok: false,
      reason: "duplicate-drafts",
      detail: `${drafts.length} draft releases exist for ${tag}`,
    };
  }
  if (drafts.length === 1) {
    return { ok: true, kind: "draft", release: drafts[0] };
  }
  if (matching.length > 0) {
    return { ok: true, kind: "published", release: matching[0] };
  }
  return { ok: true, kind: "none", release: null };
}

function ghError(args, error) {
  if (error?.code === "ENOENT") {
    const failure = new Error(`gh was not found on PATH (${displayGh(args)})`);
    failure.reason = "gh-missing";
    failure.exitCode = 4;
    return failure;
  }
  const text = String(error?.stderr || error?.stdout || error?.message || error).trim();
  const failure = new Error(`gh failed (${displayGh(args)}): ${text}`);
  failure.reason = "gh-failed";
  failure.exitCode = 4;
  return failure;
}

export function runGhJson(args, { cwd } = {}) {
  const result = spawnSync("gh", args, {
    cwd,
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
    windowsHide: true,
  });
  if (result.error) throw ghError(args, result.error);
  if (result.status !== 0) {
    throw ghError(args, { stderr: result.stderr, stdout: result.stdout });
  }
  const text = String(result.stdout || "").trim();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch (error) {
    const failure = new Error(`gh returned invalid JSON (${displayGh(args)}): ${error.message}`);
    failure.reason = "gh-failed";
    failure.exitCode = 4;
    throw failure;
  }
}

export function listReleases(repo, { cwd } = {}) {
  const pages = runGhJson(ghListReleasesArgs(repo), { cwd });
  if (!Array.isArray(pages)) return [];
  return pages.flatMap((page) => (Array.isArray(page) ? page : [page]));
}

export function getRelease(repo, releaseId, { cwd } = {}) {
  return runGhJson(ghGetReleaseArgs(repo, releaseId), { cwd });
}

export async function downloadAssetToFile(args, destination) {
  await fs.promises.mkdir(path.dirname(destination), { recursive: true });
  const child = spawn("gh", args, { stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  let stderr = "";
  child.stderr.on("data", (chunk) => {
    stderr += chunk.toString();
  });
  const finished = pipeline(child.stdout, fs.createWriteStream(destination));
  const exit = new Promise((resolve, reject) => {
    child.on("error", (error) => reject(ghError(args, error)));
    child.on("close", (code) => {
      if (code === 0) resolve();
      else reject(ghError(args, { stderr }));
    });
  });
  await Promise.all([finished, exit]);
}

export function createTempDirectory(prefix = "bwc-release-") {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

export function removeDirectory(directory) {
  fs.rmSync(directory, { recursive: true, force: true });
}

export function assetNameFromUrl(url) {
  try {
    return decodeURIComponent(new URL(url).pathname.split("/").filter(Boolean).at(-1) ?? "");
  } catch {
    return "";
  }
}

export function releaseAssetNames(release) {
  return (release?.assets ?? []).map((asset) => asset.name).filter(Boolean);
}

export function assetByName(release, name) {
  return (release?.assets ?? []).find((asset) => asset && asset.name === name) ?? null;
}
