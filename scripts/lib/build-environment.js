import fs from "node:fs";
import path from "node:path";

/**
 * The folder of vswhere.exe that the Visual Studio installer keeps, or null when it is not there. The NativeAOT linker
 * step of BwcUpdate (ILCompiler findvcvarsall.bat, then vcvarsall.bat of Visual Studio 18) runs "vswhere" by name, so
 * it fails with "'vswhere.exe' is not recognized" when that folder is not on PATH (update-elevation.md HN23).
 */
export function vswhereDirectory(source, exists = fs.existsSync) {
  const programFiles = source["ProgramFiles(x86)"] ?? source["PROGRAMFILES(X86)"] ?? "C:\\Program Files (x86)";
  const directory = path.win32.join(programFiles, "Microsoft Visual Studio", "Installer");
  return exists(path.win32.join(directory, "vswhere.exe")) ? directory : null;
}

/**
 * The environment MSBuild runs in. DefineConstants, in any case, is dropped: MSBuild turns an environment variable into
 * a property, so one set in the caller's shell could add BWC_E2E to a ship publish. The flavor comes only from
 * -p:BwcFlavor (update-elevation.md, HB3). The Visual Studio installer folder (vswhere.exe) is added to this one
 * process's PATH when it is missing, for the NativeAOT link of BwcUpdate. Nothing else changes, and no setting outlives
 * the build. Kept apart from scripts/dotnet-build.js so the selftest can import it.
 */
export function buildEnvironment(source, exists = fs.existsSync) {
  const env = { ...source };
  for (const name of Object.keys(env)) {
    if (name.toLowerCase() === "defineconstants") {
      delete env[name];
    }
  }

  const directory = vswhereDirectory(source, exists);
  const pathName = Object.keys(env).find((name) => name.toLowerCase() === "path");
  if (directory !== null && pathName !== undefined) {
    const entries = env[pathName].split(";").map((entry) => entry.replace(/[\\/]+$/, "").toLowerCase());
    if (!entries.includes(directory.toLowerCase())) {
      env[pathName] = `${env[pathName]};${directory}`;
    }
  }
  return env;
}
