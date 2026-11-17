import fs from "node:fs";
import path from "node:path";
import { UPDATE_EXE, UPDATE_STALE_FILES } from "../flavor-metadata.js";

// BwcUpdate.exe is NativeAOT. Publishing it with -o set to the shared publish folder is unsafe: MSBuild's
// IncrementalClean deletes every file that the project's previous publish wrote and the new one does not. The old
// CoreCLR helper had copied the whole self-contained runtime into the shared folder, so the first NativeAOT publish
// deleted the runtime of the Hub (update-elevation.md HN25). So the helper is published into its own folder and only
// its exe is copied (HN17).

/** The staging folder for the helper of a shared publish folder: publish/<rid>-<flavor>-bwcupdate-aot. */
export function updateStagingDir(outDir) {
  return `${outDir}-bwcupdate-aot`;
}

/**
 * Deletes this project's FileListAbsolute.txt (the list IncrementalClean reads) for one platform and rid, from the
 * project's own obj folder, and nothing else. Returns the removed paths. A list left by an earlier build can name files
 * of a folder that this publish does not write to.
 */
export function removeStaleFileLists({ projectDir, projectFile, platform, rid }) {
  const listName = `${projectFile}.FileListAbsolute.txt`;
  const roots = [path.join(projectDir, "obj", platform, "Release"), path.join(projectDir, "obj", "Release")];
  const removed = [];
  for (const root of roots) {
    if (!fs.existsSync(root)) continue;
    for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const list = path.join(root, entry.name, rid, listName);
      if (!fs.existsSync(list)) continue;
      fs.rmSync(list, { force: true });
      removed.push(list);
    }
  }
  return removed;
}

/**
 * Copies BwcUpdate.exe (and BwcUpdate.pdb when the staging folder has one) into the shared folder, then removes the
 * stale managed helper files from it by exact name. Nothing else is copied, replaced, or deleted. Throws, having
 * changed nothing, when the staging folder has no BwcUpdate.exe.
 */
export function installUpdateOutputs(stagingDir, outDir) {
  const exe = path.join(stagingDir, UPDATE_EXE);
  if (!fs.existsSync(exe)) {
    throw new Error(`the NativeAOT publish left no ${UPDATE_EXE} in ${stagingDir}`);
  }
  const copied = [];
  for (const name of [UPDATE_EXE, "BwcUpdate.pdb"]) {
    const source = path.join(stagingDir, name);
    if (!fs.existsSync(source)) continue;
    fs.copyFileSync(source, path.join(outDir, name));
    copied.push(name);
  }
  const removed = [];
  for (const name of UPDATE_STALE_FILES) {
    const stale = path.join(outDir, name);
    if (!fs.existsSync(stale)) continue;
    fs.rmSync(stale, { force: true });
    removed.push(name);
  }
  return { copied, removed };
}
