const NUMERIC = "(?:0|[1-9]\\d*)";
const PRERELEASE_IDENTIFIER = "(?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*)";
const SEMVER_PATTERN = new RegExp(
  `^(${NUMERIC})\\.(${NUMERIC})\\.(${NUMERIC})(?:-(${PRERELEASE_IDENTIFIER}(?:\\.${PRERELEASE_IDENTIFIER})*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$`,
);

export function tryParseSemVer(value) {
  if (typeof value !== "string") return null;
  const match = SEMVER_PATTERN.exec(value);
  if (!match) return null;
  return {
    text: value,
    major: match[1],
    minor: match[2],
    patch: match[3],
    prerelease: match[4] ? match[4].split(".") : [],
    build: match[5] ? match[5].split(".") : [],
  };
}

export function isPrerelease(version) {
  const parsed = typeof version === "string" ? tryParseSemVer(version) : version;
  return Boolean(parsed && parsed.prerelease.length > 0);
}

export function channelForVersion(version) {
  const parsed = typeof version === "string" ? tryParseSemVer(version) : version;
  if (!parsed) return null;
  return parsed.prerelease.length === 0 ? "stable" : "beta";
}

function compareNumericText(left, right) {
  if (left.length !== right.length) return left.length < right.length ? -1 : 1;
  return left < right ? -1 : left > right ? 1 : 0;
}

function compareIdentifier(left, right) {
  const leftNumeric = /^\d+$/.test(left);
  const rightNumeric = /^\d+$/.test(right);
  if (leftNumeric && rightNumeric) {
    return compareNumericText(left, right);
  }
  if (leftNumeric !== rightNumeric) {
    return leftNumeric ? -1 : 1;
  }
  return left < right ? -1 : left > right ? 1 : 0;
}

export function compareSemVer(left, right) {
  const a = typeof left === "string" ? tryParseSemVer(left) : left;
  const b = typeof right === "string" ? tryParseSemVer(right) : right;
  if (!a || !b) {
    throw new Error("compareSemVer needs two valid SemVer values");
  }

  for (const key of ["major", "minor", "patch"]) {
    const order = compareNumericText(a[key], b[key]);
    if (order !== 0) return order;
  }

  if (a.prerelease.length === 0 && b.prerelease.length === 0) return 0;
  if (a.prerelease.length === 0) return 1;
  if (b.prerelease.length === 0) return -1;

  const count = Math.max(a.prerelease.length, b.prerelease.length);
  for (let index = 0; index < count; index += 1) {
    if (index >= a.prerelease.length) return -1;
    if (index >= b.prerelease.length) return 1;
    const order = compareIdentifier(a.prerelease[index], b.prerelease[index]);
    if (order !== 0) return order;
  }
  return 0;
}

export function versionIsNewer(offered, current) {
  return compareSemVer(offered, current) > 0;
}
