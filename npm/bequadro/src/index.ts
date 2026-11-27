// The Node API of the bequadro npm package (CLI-009): finds the native executable for this platform and runs it.
import { spawn } from "node:child_process";
import { statSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";

/** The platform packages, which each hold the executable for one platform, by `${process.platform}-${process.arch}`. */
export const platformPackages: Readonly<Record<string, string>> = {
  "darwin-arm64": "@bequadro/darwin-arm64",
  "darwin-x64": "@bequadro/darwin-x64",
  "linux-arm64": "@bequadro/linux-arm64",
  "linux-x64": "@bequadro/linux-x64",
  "win32-arm64": "@bequadro/win32-arm64",
  "win32-x64": "@bequadro/win32-x64",
};

/**
 * The path of the native `bequadro` executable: the `BEQUADRO_BINARY` environment variable if it is set, resolved
 * against the current directory, otherwise the executable in the platform package installed for this platform.
 * Throws if there is none.
 */
export function binaryPath(): string {
  const override = process.env["BEQUADRO_BINARY"];
  if (override !== undefined && override !== "") {
    return resolve(override);
  }

  const platform = `${process.platform}-${process.arch}`;
  const name = platformPackages[platform];
  if (name === undefined) {
    const supported = Object.keys(platformPackages).join(", ");
    throw new Error(
      `bequadro has no executable for ${platform} (supported: ${supported}). Set BEQUADRO_BINARY to the path of one built from source.`,
    );
  }

  let manifest: string;
  try {
    manifest = createRequire(import.meta.url).resolve(`${name}/package.json`);
  } catch {
    // npm doesn't install the Linux packages where the C library isn't glibc
    const libc =
      process.platform === "linux" ? " On Linux it needs glibc 2.34 or later, so musl (as on Alpine) isn't supported." : "";
    throw new Error(
      `bequadro's executable for ${platform} is in the package ${name}, which isn't installed. It is an optional ` +
        `dependency of bequadro: reinstall without --omit=optional or --no-optional, or install it directly.${libc}`,
    );
  }

  return join(dirname(manifest), "bin", process.platform === "win32" ? "bequadro.exe" : "bequadro");
}

/** Options for {@link generate}, one per command-line option (CLI-002). */
export interface GenerateOptions {
  /** The language to generate. */
  readonly target: "typescript" | "python";
  /** Schema files and directories of them. */
  readonly inputs: readonly string[];
  /** The output directory. */
  readonly out: string;
  /** TypeScript only: the extension of relative imports. Defaults to `ts`. */
  readonly importExtension?: "ts" | "js" | undefined;
  /** Scalar and enum table fields without a default must be given when constructing. */
  readonly requireScalarsWithoutDefault?: boolean | undefined;
  /** Python only: don't write type stubs. */
  readonly noStubs?: boolean | undefined;
  /** Compare with the output directory instead of writing. */
  readonly check?: boolean | undefined;
  /** The directory relative paths are resolved against. Defaults to the current directory. */
  readonly cwd?: string | undefined;
}

/** A diagnostic in the schema set, parsed from the CLI's `{path}:{line}:{column}: error {id}: {message}` (CLI-004). */
export interface Diagnostic {
  /** The path as the CLI printed it, relative to `cwd` when the input was. */
  readonly path: string;
  /** 1-based. */
  readonly line: number;
  /** 1-based. */
  readonly column: number;
  readonly id: string;
  readonly message: string;
}

export interface GenerateResult {
  /** 0 for success, 1 for schema errors or, with `check`, out-of-date output, 2 for usage and file errors (CLI-004). */
  readonly exitCode: number;
  readonly diagnostics: readonly Diagnostic[];
  readonly stdout: string;
  readonly stderr: string;
}

/** The command-line arguments for options, without the executable. */
export function generateArguments(options: GenerateOptions): string[] {
  const args = ["generate", "--target", options.target, "--out", path(options.out)];
  if (options.importExtension !== undefined) {
    args.push("--import-extension", options.importExtension);
  }
  if (options.requireScalarsWithoutDefault === true) {
    args.push("--require-scalars-without-default");
  }
  if (options.noStubs === true) {
    args.push("--no-stubs");
  }
  if (options.check === true) {
    args.push("--check");
  }

  args.push(...options.inputs.map(path));
  return args;
}

/** A path the CLI can't mistake for an option. */
function path(value: string): string {
  return value.startsWith("-") ? `./${value}` : value;
}

// The path is the shortest prefix that fits, because messages quote schema text, which can look like a diagnostic
const diagnosticPattern = /^(.*?):(\d+):(\d+): error (BQ\d+): (.*)$/;

/** Parses the diagnostics in the CLI's standard error, ignoring other lines. */
export function parseDiagnostics(stderr: string): Diagnostic[] {
  const diagnostics: Diagnostic[] = [];
  for (const line of stderr.split(/\r?\n/)) {
    const match = diagnosticPattern.exec(line);
    if (match !== null) {
      const [, path, row, column, id, message] = match as unknown as [string, string, string, string, string, string];
      diagnostics.push({ path, line: Number(row), column: Number(column), id, message });
    }
  }

  return diagnostics;
}

/**
 * Runs `bequadro generate`. Resolves with the exit code and diagnostics whatever the outcome, and rejects only if the
 * executable can't be found or started, or is killed by a signal, so that it has no exit code.
 */
export async function generate(options: GenerateOptions): Promise<GenerateResult> {
  const binary = binaryPath();
  return new Promise((resolvePromise, reject) => {
    const child = spawn(binary, generateArguments(options), {
      cwd: options.cwd,
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
    });
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8").on("data", (chunk: string) => (stdout += chunk));
    child.stderr.setEncoding("utf8").on("data", (chunk: string) => (stderr += chunk));
    child.on("error", (error) => {
      // Node reports a missing working directory as the executable not being found
      reject(
        options.cwd !== undefined && !isDirectory(options.cwd)
          ? new Error(`bequadro couldn't start: the working directory ${resolve(options.cwd)} doesn't exist.`)
          : new Error(`bequadro couldn't run ${binary}: ${error.message}`, { cause: error }),
      );
    });
    child.on("close", (code, signal) => {
      if (code === null) {
        reject(new Error(`bequadro was terminated by ${String(signal)}.`));
      } else {
        resolvePromise({ exitCode: code, diagnostics: parseDiagnostics(stderr), stdout, stderr });
      }
    });
  });
}

function isDirectory(path: string): boolean {
  try {
    return statSync(path).isDirectory();
  } catch {
    return false;
  }
}
