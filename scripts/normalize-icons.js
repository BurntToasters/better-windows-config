#!/usr/bin/env node
/**
 * Regenerates the Windows app icons from their PNG sources.
 *
 * Each row of APP_ICONS pairs a source PNG with the .ico that the build embeds
 * and that the window loads. Every .ico holds ICO_SIZES. ImageMagick does the
 * resize and padding. Nothing is downloaded.
 */
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT = "scripts/normalize-icons.js";
const REPO_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const SOURCE_SIZE = 1024;
// The longer side must be SOURCE_SIZE. The shorter side may be up to this many
// pixels short, because -extent centers the image on a square transparent canvas.
const MAX_LETTERBOX_PX = 4;
const ICO_SIZES = [256, 128, 96, 72, 64, 48, 40, 32, 24, 20, 16];
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const IMAGEMAGICK_ROOT = process.env.ProgramFiles || "C:\\Program Files";

// One row per app icon. Add a row when another tool gets its own icon.
const APP_ICONS = [
  { source: "resources/bwc.png", icon: "resources/bwc.ico" },
  { source: "resources/bwc-regedit.png", icon: "resources/bwc-regedit.ico" },
];

function printHelp() {
  console.log(`Usage:
  node ${SCRIPT} [--check] [--source <png> --out <ico>]

Regenerates each app icon from its PNG source with ImageMagick. Every .ico
holds these sizes: ${ICO_SIZES.join(", ")}.

Source rule: a PNG whose longer side is exactly ${SOURCE_SIZE} px and whose
shorter side is at most ${MAX_LETTERBOX_PX} px short of it. The image is padded to a
${SOURCE_SIZE}x${SOURCE_SIZE} transparent square before the resize.

Options:
  --check          Write nothing. Verify each .ico exists, holds every size,
                   and is not older than its source. Exit 1 when out of date.
  --source <png>   Process one source instead of the table. Needs --out.
  --out <ico>      Output for --source. Needs --source.
  --help           Show this help.

ImageMagick is found as "magick" on PATH, then as magick.exe in
"<Program Files>\\ImageMagick-*". Install it yourself; nothing is downloaded.`);
}

function parseArgs(argv) {
  const options = { check: false, help: false, source: null, out: null };
  const args = argv.slice(2);
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
    } else if (arg === "--check") {
      options.check = true;
    } else if (arg === "--source" || arg === "--out") {
      const value = args[index + 1];
      if (value === undefined || value.startsWith("--")) {
        throw new Error(`${arg} requires a path. Run node ${SCRIPT} --help.`);
      }
      options[arg.slice(2)] = value;
      index += 1;
    } else {
      throw new Error(`Unknown option ${arg}. Run node ${SCRIPT} --help.`);
    }
  }
  if ((options.source === null) !== (options.out === null)) {
    throw new Error("--source and --out must be given together.");
  }
  return options;
}

function selectPairs(options) {
  if (options.source !== null) {
    return [{ source: path.resolve(options.source), icon: path.resolve(options.out) }];
  }
  return APP_ICONS.map(({ source, icon }) => ({
    source: path.join(REPO_ROOT, source),
    icon: path.join(REPO_ROOT, icon),
  }));
}

function displayPath(file) {
  const relative = path.relative(REPO_ROOT, file);
  return relative.startsWith("..") || path.isAbsolute(relative) ? file : relative.split(path.sep).join("/");
}

function pngSizeFromBytes(bytes, label) {
  const isPng =
    bytes.length >= 24 &&
    bytes.subarray(0, 8).equals(PNG_SIGNATURE) &&
    bytes.toString("latin1", 12, 16) === "IHDR";
  if (!isPng) {
    throw new Error(`${label} is not a PNG file.`);
  }
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

function validateSource(file) {
  const label = displayPath(file);
  if (!fs.existsSync(file)) {
    throw new Error(`Source not found: ${label}.`);
  }
  const { width, height } = pngSizeFromBytes(fs.readFileSync(file), label);
  const longer = Math.max(width, height);
  const shorter = Math.min(width, height);
  const ok = longer === SOURCE_SIZE && shorter >= SOURCE_SIZE - MAX_LETTERBOX_PX;
  if (!ok) {
    throw new Error(
      `Source ${label} is ${width}x${height}. It must be a PNG whose longer side is ${SOURCE_SIZE} px ` +
        `and whose shorter side is at least ${SOURCE_SIZE - MAX_LETTERBOX_PX} px. Export it at ${SOURCE_SIZE}x${SOURCE_SIZE}.`,
    );
  }
  return { width, height };
}

function readIcoEntries(bytes, label) {
  const isIco = bytes.length >= 6 && bytes.readUInt16LE(0) === 0 && bytes.readUInt16LE(2) === 1;
  if (!isIco) {
    throw new Error(`${label} is not an ICO file.`);
  }
  const count = bytes.readUInt16LE(4);
  if (bytes.length < 6 + count * 16) {
    throw new Error(`${label} is truncated: its directory lists ${count} images.`);
  }
  const entries = [];
  for (let index = 0; index < count; index += 1) {
    const offset = 6 + index * 16;
    // A width or height byte of 0 means 256.
    const width = bytes[offset] || 256;
    const height = bytes[offset + 1] || 256;
    const size = bytes.readUInt32LE(offset + 8);
    const dataOffset = bytes.readUInt32LE(offset + 12);
    if (width !== height) {
      throw new Error(`${label} image ${index} is ${width}x${height}. Icon images must be square.`);
    }
    if (dataOffset + size > bytes.length) {
      throw new Error(`${label} image ${index} points past the end of the file.`);
    }
    entries.push({ width, height, size, dataOffset });
  }
  return entries;
}

/** Parses an .ico and returns its image sizes, largest first. Throws when a size is missing. */
function verifyIco(file, requiredSizes = ICO_SIZES) {
  const label = displayPath(file);
  const bytes = fs.readFileSync(file);
  const entries = readIcoEntries(bytes, label);
  for (const entry of entries) {
    // PNG-compressed images carry their own dimensions. Check they match the directory.
    const data = bytes.subarray(entry.dataOffset, entry.dataOffset + entry.size);
    if (data.length >= 8 && data.subarray(0, 8).equals(PNG_SIGNATURE)) {
      const inner = pngSizeFromBytes(data, label);
      if (inner.width !== entry.width || inner.height !== entry.height) {
        throw new Error(`${label} has a ${entry.width}px image whose PNG data is ${inner.width}x${inner.height}.`);
      }
    }
  }
  const found = new Set(entries.map((entry) => entry.width));
  const missing = requiredSizes.filter((size) => !found.has(size));
  if (missing.length > 0) {
    throw new Error(`${label} is missing sizes: ${missing.join(", ")}.`);
  }
  return { sizes: [...found].sort((a, b) => b - a), bytes: bytes.length };
}

function probeMagick(command) {
  const result = spawnSync(command, ["-version"], { encoding: "utf8", windowsHide: true, shell: false });
  return !result.error && result.status === 0 && /ImageMagick/.test(result.stdout ?? "");
}

function listImageMagickInstalls() {
  if (process.platform !== "win32") {
    return [];
  }
  let entries;
  try {
    entries = fs.readdirSync(IMAGEMAGICK_ROOT, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((entry) => entry.isDirectory() && /^ImageMagick-/i.test(entry.name))
    .map((entry) => entry.name)
    .sort()
    .reverse()
    .map((name) => path.join(IMAGEMAGICK_ROOT, name, "magick.exe"))
    .filter((exe) => fs.existsSync(exe));
}

function findMagick() {
  if (probeMagick("magick")) {
    return "magick";
  }
  for (const exe of listImageMagickInstalls()) {
    if (probeMagick(exe)) {
      return exe;
    }
  }
  throw new Error(
    "ImageMagick was not found. Install ImageMagick so that magick is on PATH, or so that " +
      `magick.exe is under ${path.join(IMAGEMAGICK_ROOT, "ImageMagick-*")}. This script does not download it.`,
  );
}

function generateIcon(magick, { source, icon }) {
  const temp = path.join(path.dirname(icon), `.${path.basename(icon)}.tmp.ico`);
  const args = [
    source,
    "-background", "none",
    "-gravity", "center",
    "-extent", `${SOURCE_SIZE}x${SOURCE_SIZE}`,
    "-filter", "Lanczos",
    "-define", `icon:auto-resize=${ICO_SIZES.join(",")}`,
    temp,
  ];
  const result = spawnSync(magick, args, { encoding: "utf8", windowsHide: true, shell: false });
  if (result.error || result.status !== 0) {
    fs.rmSync(temp, { force: true });
    const detail = result.error ? result.error.message : (result.stderr ?? "").trim();
    throw new Error(`ImageMagick failed for ${displayPath(source)}: ${detail}`);
  }
  let summary;
  try {
    summary = verifyIco(temp);
  } catch (error) {
    fs.rmSync(temp, { force: true });
    throw error;
  }
  fs.renameSync(temp, icon);
  return summary;
}

function checkPair({ source, icon }) {
  const problems = [];
  try {
    validateSource(source);
  } catch (error) {
    problems.push(error.message);
  }
  if (!fs.existsSync(icon)) {
    problems.push(`${displayPath(icon)} does not exist.`);
    return problems;
  }
  try {
    verifyIco(icon);
  } catch (error) {
    problems.push(error.message);
  }
  if (fs.existsSync(source) && fs.statSync(icon).mtimeMs < fs.statSync(source).mtimeMs) {
    problems.push(`${displayPath(icon)} is older than ${displayPath(source)}.`);
  }
  return problems;
}

function main(argv) {
  const options = parseArgs(argv);
  if (options.help) {
    printHelp();
    return 0;
  }
  const pairs = selectPairs(options);

  if (options.check) {
    const problems = pairs.flatMap((pair) => checkPair(pair));
    if (problems.length > 0) {
      for (const problem of problems) {
        console.error(`icons:normalize --check: ${problem}`);
      }
      console.error(
        "The app icons are out of date or invalid. Run npm run icons:normalize to regenerate them, then commit the .ico files.",
      );
      return 1;
    }
    for (const { icon } of pairs) {
      const { sizes } = verifyIco(icon);
      console.log(`${displayPath(icon)}: up to date, ${sizes.length} sizes (${sizes.join(", ")})`);
    }
    return 0;
  }

  for (const { source } of pairs) {
    validateSource(source);
  }
  const magick = findMagick();
  for (const pair of pairs) {
    const { sizes, bytes } = generateIcon(magick, pair);
    console.log(
      `${displayPath(pair.icon)}: from ${displayPath(pair.source)}, ${sizes.length} sizes ` +
        `(${sizes.join(", ")}), ${bytes} bytes`,
    );
  }
  return 0;
}

try {
  process.exitCode = main(process.argv);
} catch (error) {
  console.error(`${SCRIPT}: ${error.message}`);
  process.exitCode = 1;
}
