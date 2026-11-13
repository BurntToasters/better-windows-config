// Password input for encrypted minisign secret keys. A password is never a command-line argument:
// a process list or shell history would show it. The sources are an environment variable whose
// name the caller passes with --password-env, or a hidden interactive prompt on a terminal.
import readline from "node:readline";

const PASSWORD_ARGUMENT = /^--password(?:=|$)/;

/** True when argv carries a --password option, which this tooling refuses on purpose. */
export function passwordArgumentGiven(argv) {
  return argv.some((arg) => PASSWORD_ARGUMENT.test(arg));
}

/** Reads a password from the named environment variable. Returns { ok, password | reason, detail }. */
export function passwordFromEnvironment(name, env = process.env) {
  if (!/^[A-Z][A-Z0-9_]{0,63}$/.test(name)) {
    return { ok: false, reason: "password-missing", detail: "--password-env needs an upper-case variable name" };
  }
  const value = env[name];
  if (typeof value !== "string" || value.length === 0) {
    return { ok: false, reason: "password-missing", detail: `environment variable ${name} is not set` };
  }
  return { ok: true, password: value };
}

/** Asks on the terminal without echoing. Returns null when stdin is not a terminal. */
export function promptHiddenPassword(question, { input = process.stdin, output = process.stderr } = {}) {
  if (!input.isTTY) return Promise.resolve(null);
  return new Promise((resolve) => {
    const rl = readline.createInterface({ input, output, terminal: true });
    rl._writeToOutput = () => {};
    output.write(question);
    rl.question("", (answer) => {
      rl.close();
      output.write("\n");
      resolve(answer);
    });
  });
}

/**
 * Resolves the password for an encrypted key. With envName, only that variable is read. Without
 * it, a terminal prompt is used. Neither source gives password-missing (exit 2).
 */
export async function resolvePassword({ envName, env = process.env, prompt = promptHiddenPassword } = {}) {
  if (envName !== undefined) {
    return passwordFromEnvironment(envName, env);
  }
  const typed = await prompt("Password for the encrypted secret key: ");
  if (typeof typed !== "string" || typed.length === 0) {
    return {
      ok: false,
      reason: "password-missing",
      detail: "the secret key is encrypted: pass --password-env <NAME> or run in a terminal",
    };
  }
  return { ok: true, password: typed };
}
