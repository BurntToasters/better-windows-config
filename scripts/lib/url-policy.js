const MAX_LENGTH = 2048;
const ENCODED_DANGERS = ["%2e", "%2f", "%5c", "%00"];

function pathOf(text) {
  let rest = text;
  const scheme = text.indexOf("://");
  if (scheme >= 0) {
    rest = text.slice(scheme + 3);
    const slash = rest.indexOf("/");
    if (slash < 0) return "";
    rest = rest.slice(slash);
  }
  const cut = rest.search(/[?#]/);
  return cut >= 0 ? rest.slice(0, cut) : rest;
}

function hasDotSegment(pathText) {
  return pathText.split("/").some((segment) => segment === "." || segment === "..");
}

export function validateUrl(text, { requireHttps = true } = {}) {
  if (typeof text !== "string" || text.length === 0) {
    return { ok: false, detail: "URL is empty" };
  }
  if (text.length > MAX_LENGTH) {
    return { ok: false, detail: "URL is longer than 2048 characters" };
  }

  for (const character of text) {
    const code = character.codePointAt(0);
    if (code <= 0x20 || code >= 0x7f || character === "\\" || character === '"' || character === "#") {
      return {
        ok: false,
        detail: "URL has a space, control character, non-ASCII character, backslash, quote, or fragment",
      };
    }
  }

  const lower = text.toLowerCase();
  if (ENCODED_DANGERS.some((danger) => lower.includes(danger))) {
    return { ok: false, detail: "URL has an encoded path separator, dot, or NUL" };
  }

  let url;
  try {
    url = new URL(text);
  } catch {
    return { ok: false, detail: "URL is not an absolute URI" };
  }

  if (url.protocol !== "https:" && url.protocol !== "http:") {
    return { ok: false, detail: "URL scheme must be https or http" };
  }
  if (requireHttps && url.protocol !== "https:") {
    return { ok: false, detail: "https is required" };
  }
  if (url.username !== "" || url.password !== "") {
    return { ok: false, detail: "URL carries user information" };
  }
  if (!url.hostname) {
    return { ok: false, detail: "URL has no host" };
  }
  if (hasDotSegment(pathOf(text))) {
    return { ok: false, detail: "URL path has a dot segment" };
  }

  return { ok: true, url, text: url.toString() };
}

/** True for an http or https URL whose host is the loopback address or localhost. Tests only. */
export function isLoopbackUrl(text) {
  if (typeof text !== "string") return false;
  let url;
  try {
    url = new URL(text);
  } catch {
    return false;
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") return false;
  return url.hostname === "127.0.0.1" || url.hostname === "localhost" || url.hostname === "[::1]";
}
