// The Vite plugin of the bequadro npm package (CLI-010): generates TypeScript when Vite starts and whenever a schema
// changes.
import { readdirSync, statSync } from "node:fs";
import { isAbsolute, join, relative, resolve } from "node:path";
import type { ErrorPayload, Logger, Plugin, ViteDevServer } from "vite";
import { generate, type GenerateResult } from "./index.js";

export interface BequadroPluginOptions {
  /** Schema files and directories of them, relative to Vite's root. */
  readonly inputs: string | readonly string[];
  /** The output directory, relative to Vite's root. */
  readonly out: string;
  /** The extension of relative imports in generated code. Defaults to `ts`. */
  readonly importExtension?: "ts" | "js" | undefined;
  /** Scalar and enum table fields without a default must be given when constructing. */
  readonly requireScalarsWithoutDefault?: boolean | undefined;
}

type ViteError = ErrorPayload["err"];

/**
 * Generates TypeScript from schemas with the bequadro CLI: once when the dev server or a build starts, and again,
 * one run at a time, whenever a schema file under `inputs` is added, changed or deleted. Schema errors fail a build,
 * and in the dev server they are logged and shown in the error overlay until they are fixed.
 */
export default function bequadro(options: BequadroPluginOptions): Plugin {
  let root = process.cwd();
  let givenInputs: readonly string[] = [];
  let inputs: string[] = [];
  let logger: Logger | undefined;
  let server: ViteDevServer | undefined;
  let lastError: ViteError | undefined;

  // Runs never overlap. A change while a run is in progress queues one more run, which covers any number of changes.
  let tail: Promise<unknown> = Promise.resolve();
  let queued: Promise<GenerateResult> | undefined;

  function regenerate(): Promise<GenerateResult> {
    if (queued === undefined) {
      const run = tail.then(() => {
        queued = undefined;
        return runOnce();
      });
      queued = run;
      tail = run.catch(() => undefined);
    }

    return queued;
  }

  // The file watcher reports at most one change to a file in 50 ms and drops the rest, so in the dev server each run
  // checks the schema files again after that and runs once more if they changed since it started.
  let recheck: ReturnType<typeof setTimeout> | undefined;
  let closed = false;

  function scheduleRecheck(snapshot: string): void {
    clearTimeout(recheck);
    if (closed) {
      return;
    }

    recheck = setTimeout(() => {
      if (queued === undefined && schemaSnapshot() !== snapshot) {
        regenerate().catch(logFailure);
      }
    }, 100);
  }

  function logFailure(error: unknown): void {
    logger?.error(`[bequadro] ${error instanceof Error ? error.message : String(error)}`, { timestamp: true });
  }

  async function runOnce(): Promise<GenerateResult> {
    const snapshot = server === undefined ? "" : schemaSnapshot();
    // The paths as given, relative to the root, so that the CLI rejects an empty one rather than taking the root
    const result = await generate({
      target: "typescript",
      inputs: givenInputs,
      out: options.out,
      importExtension: options.importExtension,
      requireScalarsWithoutDefault: options.requireScalarsWithoutDefault,
      cwd: root,
    });

    if (result.exitCode === 0) {
      if (lastError !== undefined) {
        lastError = undefined;
        logger?.info("[bequadro] schema errors fixed", { timestamp: true });
        // Clears the overlay, even if the fixed schema generates the same code as before the error
        server?.ws.send({ type: "full-reload" });
      }
    } else {
      lastError = toViteError(result);
      logger?.error(`[bequadro] ${lastError.message}`, { timestamp: true });
      server?.ws.send({ type: "error", err: lastError });
    }

    if (server !== undefined) {
      scheduleRecheck(snapshot);
    }

    return result;
  }

  function toViteError(result: GenerateResult): ViteError {
    const message = result.stderr.trim() || `bequadro exited with code ${String(result.exitCode)}.`;
    const first = result.diagnostics[0];
    const error: ViteError = { message, stack: "", plugin: "bequadro" };
    if (first !== undefined) {
      const file = resolve(root, first.path);
      error.id = file;
      // 1-based, as the overlay's open-in-editor link expects
      error.loc = { file, line: first.line, column: first.column };
    }

    return error;
  }

  function isSchema(file: string): boolean {
    return inputs.some((input) => {
      if (file === input) {
        return true;
      }

      const path = relative(input, file);
      return path !== "" && !path.startsWith("..") && !isAbsolute(path) && /\.fbs$/i.test(file);
    });
  }

  /** The schema files with their sizes and modification times. */
  function schemaSnapshot(): string {
    return schemaFiles()
      .map((file) => {
        try {
          const stats = statSync(file);
          return `${file}:${String(stats.size)}:${String(stats.mtimeMs)}`;
        } catch {
          return file;
        }
      })
      .join("\n");
  }

  function schemaFiles(): string[] {
    return inputs.flatMap((input) => {
      try {
        if (!statSync(input).isDirectory()) {
          return [input];
        }
      } catch {
        return [];
      }

      return readdirSync(input, { recursive: true, encoding: "utf8" })
        .filter((name) => /\.fbs$/i.test(name))
        .map((name) => join(input, name));
    });
  }

  return {
    name: "bequadro",

    configResolved(config) {
      root = config.root;
      logger = config.logger;
      givenInputs = typeof options.inputs === "string" ? [options.inputs] : options.inputs;
      inputs = givenInputs.map((input) => resolve(root, input));
    },

    async buildStart() {
      const result = await regenerate();
      // So that vite build --watch rebuilds when a schema changes
      for (const file of schemaFiles()) {
        this.addWatchFile(file);
      }

      if (result.exitCode !== 0 && server === undefined) {
        this.error(lastError?.message ?? `bequadro exited with code ${String(result.exitCode)}.`);
      }
    },

    // Called for each environment when the dev server closes, and at the end of a build. When a server restarts with
    // an inline configuration, the new server uses this same instance, and is configured before the old one closes,
    // so only the current server's environments stop the rechecks.
    closeBundle() {
      if (server !== undefined && (Object.values(server.environments) as unknown[]).includes(this.environment)) {
        closed = true;
        clearTimeout(recheck);
      }
    },

    configureServer(devServer) {
      server = devServer;
      closed = false;
      devServer.watcher.add(inputs);
      const onFile = (file: string): void => {
        if (isSchema(resolve(file))) {
          regenerate().catch(logFailure);
        }
      };
      devServer.watcher.on("add", onFile);
      devServer.watcher.on("change", onFile);
      devServer.watcher.on("unlink", onFile);
      // A page that connects while the schemas have errors shows them too
      devServer.ws.on("vite:client:connect", () => {
        if (lastError !== undefined) {
          devServer.ws.send({ type: "error", err: lastError });
        }
      });
    },
  };
}
