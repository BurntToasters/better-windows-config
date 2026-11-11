/**
 * Elevation check for the current process, shared by the E2E runner and the launch helper.
 *
 * WindowsPrincipal.IsInRole(Administrator) is true only when the Administrators group is enabled
 * in the process token. A UAC-filtered token of an administrator has that group as deny-only, so
 * the check returns false until the shell is elevated. Windows PowerShell 5.1 ships with every
 * supported Windows, so no module or dependency is needed.
 */
import { spawnSync } from "node:child_process";
import path from "node:path";

const IS_ADMIN_SCRIPT =
  "([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent())" +
  ".IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)";

function windowsPowerShell() {
  const systemRoot = process.env.SystemRoot ?? "C:\\Windows";
  return path.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
}

// Returns { known, elevated, detail }. known is false when the check itself could not run, so
// callers must treat that as "not run", not as "not elevated" or "elevated".
export function elevationState() {
  if (process.platform !== "win32") {
    return { known: true, elevated: false, detail: "not Windows" };
  }

  const encoded = Buffer.from(IS_ADMIN_SCRIPT, "utf16le").toString("base64");
  const result = spawnSync(
    windowsPowerShell(),
    ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
    { encoding: "utf8", windowsHide: true, timeout: 60_000 },
  );
  if (result.error) {
    return { known: false, elevated: false, detail: result.error.message };
  }

  const output = String(result.stdout ?? "").trim();
  if (result.status !== 0 || (output !== "True" && output !== "False")) {
    const stderr = String(result.stderr ?? "").trim();
    return {
      known: false,
      elevated: false,
      detail: `PowerShell exited with ${result.status} (${stderr || output || "no output"})`,
    };
  }
  return { known: true, elevated: output === "True", detail: `IsInRole(Administrator)=${output}` };
}
