const MAX_DEPTH = 16;
const NUMBER_PATTERN = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/;

class StrictJsonError extends Error {
  constructor(reason, detail) {
    super(detail);
    this.reason = reason;
  }
}

class Parser {
  constructor(text) {
    this.text = text;
    this.index = 0;
    this.depth = 0;
  }

  parse() {
    this.skipWhitespace();
    if (this.index >= this.text.length) {
      throw new StrictJsonError("invalid-json", "document is empty");
    }
    const value = this.parseValue();
    this.skipWhitespace();
    if (this.index < this.text.length) {
      throw new StrictJsonError("trailing-data", "document has bytes after the root value");
    }
    return value;
  }

  skipWhitespace() {
    while (this.index < this.text.length) {
      const code = this.text.charCodeAt(this.index);
      if (code === 0x20 || code === 0x09 || code === 0x0a || code === 0x0d) {
        this.index += 1;
      } else {
        break;
      }
    }
  }

  parseValue() {
    this.skipWhitespace();
    if (this.index >= this.text.length) {
      throw new StrictJsonError("invalid-json", "unexpected end of document");
    }
    const character = this.text[this.index];
    if (character === "{") return this.parseObject();
    if (character === "[") return this.parseArray();
    if (character === '"') return this.parseString();
    if (character === "t") return this.parseLiteral("true", true);
    if (character === "f") return this.parseLiteral("false", false);
    if (character === "n") return this.parseLiteral("null", null);
    if (character === "-" || (character >= "0" && character <= "9")) {
      return this.parseNumber();
    }
    throw new StrictJsonError("invalid-json", `unexpected character ${JSON.stringify(character)}`);
  }

  parseObject() {
    this.enter();
    this.index += 1;
    const result = Object.create(null);
    const names = new Set();
    this.skipWhitespace();
    if (this.text[this.index] === "}") {
      this.index += 1;
      this.leave();
      return result;
    }

    for (;;) {
      this.skipWhitespace();
      if (this.text[this.index] !== '"') {
        throw new StrictJsonError("invalid-json", "object property name must be a string");
      }
      const name = this.parseString();
      if (names.has(name)) {
        throw new StrictJsonError("duplicate-key", `object repeats the property name ${JSON.stringify(name)}`);
      }
      names.add(name);
      this.skipWhitespace();
      if (this.text[this.index] !== ":") {
        throw new StrictJsonError("invalid-json", "object property name must be followed by a colon");
      }
      this.index += 1;
      result[name] = this.parseValue();
      this.skipWhitespace();
      const character = this.text[this.index];
      if (character === ",") {
        this.index += 1;
        continue;
      }
      if (character === "}") {
        this.index += 1;
        this.leave();
        return result;
      }
      throw new StrictJsonError("invalid-json", "object must continue with a comma or close brace");
    }
  }

  parseArray() {
    this.enter();
    this.index += 1;
    const result = [];
    this.skipWhitespace();
    if (this.text[this.index] === "]") {
      this.index += 1;
      this.leave();
      return result;
    }

    for (;;) {
      result.push(this.parseValue());
      this.skipWhitespace();
      const character = this.text[this.index];
      if (character === ",") {
        this.index += 1;
        continue;
      }
      if (character === "]") {
        this.index += 1;
        this.leave();
        return result;
      }
      throw new StrictJsonError("invalid-json", "array must continue with a comma or close bracket");
    }
  }

  parseString() {
    const start = this.index;
    this.index += 1;
    while (this.index < this.text.length) {
      const character = this.text[this.index];
      if (character === '"') {
        this.index += 1;
        const raw = this.text.slice(start, this.index);
        try {
          return JSON.parse(raw);
        } catch {
          throw new StrictJsonError("invalid-json", "string has an invalid escape or code point");
        }
      }
      if (character === "\\") {
        this.index += 1;
        if (this.index >= this.text.length) {
          throw new StrictJsonError("invalid-json", "string ends inside an escape");
        }
        const escape = this.text[this.index];
        if (escape === "u") {
          const hex = this.text.slice(this.index + 1, this.index + 5);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) {
            throw new StrictJsonError("invalid-json", "string has an invalid unicode escape");
          }
          this.index += 5;
          continue;
        }
        if (!'"\\/bfnrt'.includes(escape)) {
          throw new StrictJsonError("invalid-json", "string has an invalid escape");
        }
        this.index += 1;
        continue;
      }
      if (character.charCodeAt(0) < 0x20) {
        throw new StrictJsonError("invalid-json", "string contains an unescaped control character");
      }
      this.index += 1;
    }
    throw new StrictJsonError("invalid-json", "string is not terminated");
  }

  parseNumber() {
    const match = NUMBER_PATTERN.exec(this.text.slice(this.index));
    if (!match) {
      throw new StrictJsonError("invalid-json", "number is malformed");
    }
    this.index += match[0].length;
    return Number(match[0]);
  }

  parseLiteral(text, value) {
    if (!this.text.startsWith(text, this.index)) {
      throw new StrictJsonError("invalid-json", `expected ${text}`);
    }
    this.index += text.length;
    return value;
  }

  enter() {
    this.depth += 1;
    if (this.depth > MAX_DEPTH) {
      throw new StrictJsonError("too-deep", `document nests deeper than ${MAX_DEPTH} levels`);
    }
  }

  leave() {
    this.depth -= 1;
  }
}

export function parseStrictJson(bytes, { maxBytes = 256 * 1024 } = {}) {
  if (bytes.length > maxBytes) {
    return { ok: false, reason: "manifest-too-large", detail: `document is larger than ${maxBytes} bytes` };
  }
  if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    return { ok: false, reason: "bom", detail: "document starts with a UTF-8 byte-order mark" };
  }

  let text;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    return { ok: false, reason: "invalid-utf8", detail: "document is not valid UTF-8" };
  }

  try {
    return { ok: true, value: new Parser(text).parse() };
  } catch (error) {
    if (error instanceof StrictJsonError) {
      return { ok: false, reason: error.reason, detail: error.message };
    }
    return { ok: false, reason: "invalid-json", detail: error instanceof Error ? error.message : String(error) };
  }
}

export function decodeCanonicalBase64(text) {
  if (typeof text !== "string" || text.length === 0 || text.length % 4 !== 0) {
    return null;
  }
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(text)) {
    return null;
  }
  const bytes = Buffer.from(text, "base64");
  if (bytes.toString("base64") !== text) {
    return null;
  }
  return bytes;
}
