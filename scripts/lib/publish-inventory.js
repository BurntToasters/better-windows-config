import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

export function listFiles(directory) {
  if (!fs.existsSync(directory) || !fs.statSync(directory).isDirectory()) {
    return [];
  }
  const files = [];
  function walk(current, relative) {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const absolute = path.join(current, entry.name);
      const rel = relative ? `${relative}/${entry.name}` : entry.name;
      if (entry.isSymbolicLink()) {
        throw new Error(`${rel} is a symbolic link; publish inventory accepts regular files only`);
      }
      if (entry.isDirectory()) {
        walk(absolute, rel);
      } else if (entry.isFile()) {
        files.push({ rel, absolute, size: fs.statSync(absolute).size });
      }
    }
  }
  walk(directory, "");
  return files.sort((left, right) => (left.rel < right.rel ? -1 : left.rel > right.rel ? 1 : 0));
}

async function sha256File(filePath) {
  const hash = createHash("sha256");
  for await (const chunk of fs.createReadStream(filePath, { highWaterMark: 1 << 20 })) {
    hash.update(chunk);
  }
  return hash.digest("hex");
}

export async function publishInventory(directory) {
  if (!fs.existsSync(directory) || !fs.statSync(directory).isDirectory()) {
    return {
      present: false,
      directory,
      files: [],
      bytes: 0,
      treeSha256: null,
      detail: "publish folder is missing",
    };
  }

  const entries = listFiles(directory);
  const files = [];
  let bytes = 0;
  for (const entry of entries) {
    const sha256 = await sha256File(entry.absolute);
    files.push({ path: entry.rel, size: entry.size, sha256 });
    bytes += entry.size;
  }
  const tree = createHash("sha256");
  for (const file of files) {
    tree.update(`${file.sha256}  ${file.path}\n`, "utf8");
  }
  return {
    present: true,
    directory,
    files,
    bytes,
    treeSha256: tree.digest("hex"),
  };
}
