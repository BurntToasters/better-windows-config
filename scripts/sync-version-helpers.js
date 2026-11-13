/**
 * Pure helpers for scripts/sync-version.js. No file system access lives here.
 */

const VERSION_PATTERN =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-beta\.(0|[1-9]\d*))?$/;

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Parse X.Y.Z or X.Y.Z-beta.N and derive the four-part Windows FileVersion.
 *
 * @param {string} value
 * @returns {{ version: string, fileVersion: string, prerelease: string | null }}
 */
export function parsePackageVersion(value) {
  if (typeof value !== "string") {
    throw new Error("package.json version must be a string");
  }
  const match = value.match(VERSION_PATTERN);
  if (!match) {
    throw new Error(
      `Unsupported package version: ${value}. Expected X.Y.Z or X.Y.Z-beta.N.`,
    );
  }
  const [, major, minor, patch, prerelease] = match;
  return {
    version: value,
    fileVersion: `${major}.${minor}.${patch}.${prerelease ?? "0"}`,
    prerelease: prerelease ?? null,
  };
}

/** @param {string} version */
export function fileVersionFromSemver(version) {
  return parsePackageVersion(version).fileVersion;
}

/** @param {string} text */
export function detectNewline(text) {
  return String(text).includes("\r\n") ? "\r\n" : "\n";
}

function countMatches(text, pattern) {
  return (text.match(pattern) ?? []).length;
}

/**
 * Locate the first unconditional PropertyGroup. Conditional groups belong to
 * flavors and CI and must not receive the release version.
 *
 * @param {string} xml
 */
export function findMainPropertyGroup(xml) {
  const openPattern = /<PropertyGroup\b([^>]*)>/g;
  let match;
  while ((match = openPattern.exec(xml)) !== null) {
    if (/\bCondition\s*=/.test(match[1])) continue;
    const contentStart = openPattern.lastIndex;
    const closeIndex = xml.indexOf("</PropertyGroup>", contentStart);
    if (closeIndex === -1) {
      throw new Error("Directory.Build.props has no closing </PropertyGroup>");
    }
    return { contentStart, closeIndex };
  }
  throw new Error(
    "Directory.Build.props has no unconditional <PropertyGroup>",
  );
}

/**
 * Read Version and FileVersion from the main PropertyGroup without changing
 * anything.
 *
 * @param {string} xml
 * @returns {{ version: string | null, fileVersion: string | null }}
 */
export function readDirectoryBuildPropsVersion(xml) {
  const group = findMainPropertyGroup(xml);
  const block = xml.slice(group.contentStart, group.closeIndex);
  const version = block.match(/<Version>([^<]*)<\/Version>/);
  const fileVersion = block.match(/<FileVersion>([^<]*)<\/FileVersion>/);
  return {
    version: version ? version[1] : null,
    fileVersion: fileVersion ? fileVersion[1] : null,
  };
}

/**
 * Update Version and FileVersion in the main PropertyGroup. Inserts
 * FileVersion directly after Version when it is missing. All other bytes,
 * including line endings, are preserved.
 *
 * @param {string} xml
 * @param {string} version
 * @returns {string}
 */
export function updateDirectoryBuildProps(xml, version) {
  const parsed = parsePackageVersion(version);
  const newline = detectNewline(xml);
  const group = findMainPropertyGroup(xml);
  const block = xml.slice(group.contentStart, group.closeIndex);

  const versionTags = block.match(/<Version>[^<]*<\/Version>/g) ?? [];
  if (versionTags.length !== 1) {
    throw new Error(
      `Directory.Build.props main PropertyGroup must contain exactly one <Version>; found ${versionTags.length}`,
    );
  }
  const fileVersionTags = block.match(/<FileVersion>[^<]*<\/FileVersion>/g) ?? [];
  if (fileVersionTags.length > 1) {
    throw new Error(
      `Directory.Build.props main PropertyGroup must contain at most one <FileVersion>; found ${fileVersionTags.length}`,
    );
  }

  let nextBlock;
  if (fileVersionTags.length === 1) {
    nextBlock = block
      .replace(/<Version>[^<]*<\/Version>/, `<Version>${version}</Version>`)
      .replace(
        /<FileVersion>[^<]*<\/FileVersion>/,
        `<FileVersion>${parsed.fileVersion}</FileVersion>`,
      );
  } else {
    const versionLine = block.match(
      /^([ \t]*)<Version>[^<]*<\/Version>([ \t]*)(\r?\n|$)/m,
    );
    if (!versionLine) {
      throw new Error(
        "Directory.Build.props <Version> must be on its own line so <FileVersion> can be inserted",
      );
    }
    const indent = versionLine[1];
    const trailing = versionLine[2];
    const eol = versionLine[3];
    const versionLineText = `${indent}<Version>${version}</Version>${trailing}`;
    const fileVersionLine = `${indent}<FileVersion>${parsed.fileVersion}</FileVersion>`;
    const replacement =
      eol === ""
        ? `${versionLineText}${newline}${fileVersionLine}`
        : `${versionLineText}${eol}${fileVersionLine}${eol}`;
    nextBlock = block.replace(versionLine[0], replacement);
  }

  const updated =
    xml.slice(0, group.contentStart) + nextBlock + xml.slice(group.closeIndex);

  const projectOpen = countMatches(updated, /<Project\b/g);
  const projectClose = countMatches(updated, /<\/Project>/g);
  const groupOpen = countMatches(updated, /<PropertyGroup\b/g);
  const groupClose = countMatches(updated, /<\/PropertyGroup>/g);
  if (projectOpen !== 1 || projectClose !== 1) {
    throw new Error(
      `Directory.Build.props must contain exactly one Project element; found ${projectOpen} open and ${projectClose} close`,
    );
  }
  if (groupOpen !== groupClose) {
    throw new Error(
      `Directory.Build.props has unbalanced PropertyGroup tags: ${groupOpen} open, ${groupClose} close`,
    );
  }

  const verify = readDirectoryBuildPropsVersion(updated);
  if (
    verify.version !== parsed.version ||
    verify.fileVersion !== parsed.fileVersion
  ) {
    throw new Error(
      "Directory.Build.props update did not produce the expected Version and FileVersion",
    );
  }
  return updated;
}

/*
 * CHANGELOG.md is the full GitHub release body in BCLS `binary` form: the
 * downloads table, the signing callout, one `## Changes in \`vX.Y.Z:\`` section
 * per carried-forward version, and the Release Info footer.
 */

/** Text that sync-version inserts for a new section. The validator refuses it. */
export const CHANGELOG_PLACEHOLDER = "(add release notes)";

/** BCLS §7 beta callout, placed at the very top of a beta body. */
export const CHANGELOG_BETA_CALLOUT = "> [!NOTE]\n> 🅱️ This is a Beta build.";

const BETA_CALLOUT_PATTERN = /^> \[!NOTE\][ \t]*\r?\n> 🅱️ This is a Beta build\.[ \t]*(?:\r?\n)*/;

/**
 * Matches a setup EXE download link. Groups: 1 tag version, 2 file version,
 * 3 RID.
 */
const DOWNLOAD_URL_PATTERN =
  /\/releases\/download\/v([^/\s)"'<>]+)\/BetterWindowsConfig-([^\s)"'<>]+?)-(win-(?:x64|arm64))-setup\.exe/g;

/** @param {string} version */
export function changelogSectionHeading(version) {
  return `## Changes in \`v${version}:\``;
}

/**
 * List every setup EXE download link in the changelog.
 *
 * @param {string} changelog
 * @returns {{ url: string, tagVersion: string, fileVersion: string, rid: string }[]}
 */
export function listChangelogDownloads(changelog) {
  return [...String(changelog).matchAll(DOWNLOAD_URL_PATTERN)].map((match) => ({
    url: match[0],
    tagVersion: match[1],
    fileVersion: match[2],
    rid: match[3],
  }));
}

/**
 * Find the `## Changes in \`v<version>:\`` section. The body runs to the next
 * level-two heading, so `### Known issues` stays inside it.
 *
 * @param {string} changelog
 * @param {string} version
 * @returns {{ count: number, heading: string, body: string, lines: number[] }}
 */
export function findChangelogSection(changelog, version) {
  const text = String(changelog);
  const heading = changelogSectionHeading(version);
  const pattern = new RegExp(`^${escapeRegExp(heading)}[ \\t]*$`, "gm");
  const matches = [...text.matchAll(pattern)];
  const lines = matches.map((match) => text.slice(0, match.index).split("\n").length);
  if (matches.length !== 1) {
    return { count: matches.length, heading, body: "", lines };
  }
  const headingEnd = text.indexOf("\n", matches[0].index);
  const rest = headingEnd === -1 ? "" : text.slice(headingEnd + 1);
  const next = rest.search(/^## /m);
  const body = (next === -1 ? rest : rest.slice(0, next)).trim();
  return { count: 1, heading, body, lines };
}

/**
 * Align CHANGELOG.md with a version, the same way Zinnia's sync-version does:
 * point every download link at the version's tag and file names, add the BCLS
 * beta callout for betas and remove it for stable, and insert an empty
 * `## Changes in` section (with a placeholder) above the newest one when the
 * version has none. Line endings are preserved.
 *
 * @param {string} changelog
 * @param {string} version
 * @returns {string}
 */
export function syncChangelogForVersion(changelog, version) {
  const parsed = parsePackageVersion(version);
  const newline = detectNewline(changelog);
  const nl = (text) => text.replace(/\n/g, newline);
  let updated = String(changelog);

  if (!/^# ⬇️ Downloads[ \t]*$/m.test(updated)) {
    throw new Error("CHANGELOG.md is missing the # ⬇️ Downloads heading");
  }
  if (listChangelogDownloads(updated).length === 0) {
    throw new Error("CHANGELOG.md has no setup EXE download links");
  }
  updated = updated.replace(
    DOWNLOAD_URL_PATTERN,
    (_match, _tag, _file, rid) =>
      `/releases/download/v${parsed.version}/BetterWindowsConfig-${parsed.version}-${rid}-setup.exe`,
  );

  const withoutCallout = updated.replace(BETA_CALLOUT_PATTERN, "");
  updated = parsed.prerelease
    ? nl(`${CHANGELOG_BETA_CALLOUT}\n\n`) + withoutCallout
    : withoutCallout;

  const section = findChangelogSection(updated, parsed.version);
  if (section.count > 1) {
    throw new Error(
      `CHANGELOG.md has ${section.count} "${section.heading}" headings (lines ${section.lines.join(", ")}); keep exactly one`,
    );
  }
  if (section.count === 0) {
    const anchor = updated.search(/^## (?:Changes in `|ℹ️ Release Info)/m);
    if (anchor === -1) {
      throw new Error(
        'CHANGELOG.md has no "## Changes in" section or "## ℹ️ Release Info" footer to insert above',
      );
    }
    updated =
      updated.slice(0, anchor) +
      nl(`${section.heading}\n\n- **Misc:** ${CHANGELOG_PLACEHOLDER}\n\n`) +
      updated.slice(anchor);
  }

  if (!updated.endsWith("\n")) {
    updated += newline;
  }
  return updated;
}

/**
 * Check CHANGELOG.md against a version. Returns human-readable problems; an
 * empty list means the changelog can be the release body for that version.
 *
 * @param {string} changelog
 * @param {string} version
 * @returns {string[]}
 */
export function validateChangelogForVersion(changelog, version) {
  const parsed = parsePackageVersion(version);
  const text = String(changelog);
  const errors = [];

  const section = findChangelogSection(text, parsed.version);
  if (section.count === 0) {
    errors.push(`missing section heading "${section.heading}"`);
  } else if (section.count > 1) {
    errors.push(
      `${section.count} "${section.heading}" headings (lines ${section.lines.join(", ")}); keep exactly one`,
    );
  } else if (section.body.length === 0) {
    errors.push(`section "${section.heading}" is empty`);
  }
  if (text.includes(CHANGELOG_PLACEHOLDER)) {
    errors.push(
      `placeholder "${CHANGELOG_PLACEHOLDER}" is still present (inserted by sync-version; write the real notes)`,
    );
  }

  const downloads = listChangelogDownloads(text);
  if (downloads.length === 0) {
    errors.push("no setup EXE download links in the # ⬇️ Downloads table");
  }
  for (const download of downloads) {
    if (download.tagVersion !== parsed.version || download.fileVersion !== parsed.version) {
      errors.push(`download link is not for v${parsed.version}: ${download.url}`);
    }
  }

  const hasBetaCallout = BETA_CALLOUT_PATTERN.test(text);
  if (parsed.prerelease && !hasBetaCallout) {
    errors.push("beta changelog must start with the Beta build callout");
  }
  if (!parsed.prerelease && /This is a Beta build/.test(text)) {
    errors.push("stable changelog must not include the Beta build callout");
  }
  return errors;
}
