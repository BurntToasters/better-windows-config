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

/**
 * Ensure a `## [version]` section exists. An existing section is left where it
 * is. Otherwise the new section is inserted directly after `# Changelog`.
 *
 * @param {string} changelog
 * @param {string} version
 * @returns {string}
 */
export function syncChangelogForVersion(changelog, version) {
  const parsed = parsePackageVersion(version);
  const newline = detectNewline(changelog);
  const section = `## [${parsed.version}] - Unreleased`;
  const sectionPattern = new RegExp(
    `^## \\[${escapeRegExp(parsed.version)}\\](?:[ \\t]|$)`,
    "m",
  );

  let updated = changelog;
  if (!sectionPattern.test(updated)) {
    const heading = updated.match(/^# Changelog[ \t]*(?=\r?\n|$)/m);
    if (!heading) {
      throw new Error("CHANGELOG.md is missing the # Changelog heading");
    }
    const headingEnd = heading.index + heading[0].length;
    const blankLines = updated.slice(headingEnd).match(/^(?:[ \t]*\r?\n)*/);
    const contentStart = headingEnd + (blankLines ? blankLines[0].length : 0);
    updated =
      updated.slice(0, headingEnd) +
      `${newline}${newline}${section}${newline}${newline}` +
      updated.slice(contentStart);
  }

  if (!updated.endsWith("\n")) {
    updated += newline;
  }
  return updated;
}
