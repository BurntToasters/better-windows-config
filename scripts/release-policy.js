/**
 * Signing policy for the Windows installer build. Pure functions, no I/O.
 *
 * SKIP_WIN_CODESIGN=1 (exactly) is the only value that skips Authenticode signing. Unset, 0, or any
 * other value enforces it, so a typo cannot ship an unsigned release. The variable names match the
 * ones Zinnia uses, so one .env works for both repositories.
 */

export const SKIP_ENV = "SKIP_WIN_CODESIGN";

// SignTool and the Artifact Signing dlib read these. AZURE_SUBSCRIPTION_ID may be in the .env, but
// signing does not use it, so it is not required here.
export const SIGNING_REQUIRED_VARIABLES = Object.freeze([
  "AZURE_CLIENT_ID",
  "AZURE_TENANT_ID",
  "AZURE_CLIENT_SECRET",
  "AZURE_ARTIFACT_SIGNING_ENDPOINT",
  "AZURE_ARTIFACT_SIGNING_ACCOUNT",
  "AZURE_ARTIFACT_SIGNING_PROFILE",
  "AZURE_ARTIFACT_SIGNING_PUBLISHER",
  "AZURE_ARTIFACT_SIGNING_PUBLISHER_DN",
]);

// BWC's own binaries in a publish folder root: the three apps (exe and dll) and every Bwc.*.dll.
// Runtime, Microsoft, and third-party files never match.
const BWC_BINARY = /^(?:Bwc\.[^\\/]+|BwcRegedit|BwcUpdate|BetterWindowsConfig)\.(?:dll|exe)$/i;

/**
 * Returns { skip, warning }. skip is true only for the exact value "1". A warning names any other
 * value that is neither empty nor "0", so a misspelled skip is visible.
 */
export function signingPolicy(env = process.env) {
  const raw = env[SKIP_ENV];
  const skip = raw === "1";
  const warning =
    raw === undefined || raw === "" || raw === "0" || skip
      ? null
      : `${SKIP_ENV}=${JSON.stringify(raw)} is not 1, so signing is enforced. Use exactly 1 to skip.`;
  return { skip, warning };
}

/** Names of the required variables that are unset or blank, in the order of SIGNING_REQUIRED_VARIABLES. */
export function missingSigningVariables(env = process.env) {
  return SIGNING_REQUIRED_VARIABLES.filter((name) => !String(env[name] ?? "").trim());
}

/** True when a file name in a publish folder root is one of BWC's own binaries. */
export function isBwcSigningTarget(fileName) {
  return BWC_BINARY.test(fileName);
}

/** The subset of fileNames that BWC signs, sorted. Only names are checked, not folders. */
export function bwcSigningTargets(fileNames) {
  return fileNames.filter(isBwcSigningTarget).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}
