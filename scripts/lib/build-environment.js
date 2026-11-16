/**
 * The environment MSBuild runs in. DefineConstants, in any case, is dropped: MSBuild turns an environment variable into
 * a property, so one set in the caller's shell could add BWC_E2E to a ship publish. The flavor comes only from
 * -p:BwcFlavor (update-elevation.md, HB3). Kept apart from scripts/dotnet-build.js so the selftest can import it.
 */
export function buildEnvironment(source) {
  const env = { ...source };
  for (const name of Object.keys(env)) {
    if (name.toLowerCase() === "defineconstants") {
      delete env[name];
    }
  }
  return env;
}
