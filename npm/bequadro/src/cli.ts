#!/usr/bin/env node
// The package's `bequadro` command (CLI-009): runs the native executable with the same arguments and exit code.
import { spawnSync } from "node:child_process";
import { binaryPath } from "./index.js";

let binary: string;
try {
  binary = binaryPath();
} catch (error) {
  console.error(`bequadro: ${(error as Error).message}`);
  process.exit(2);
}

const result = spawnSync(binary, process.argv.slice(2), { stdio: "inherit", windowsHide: true });
if (result.error !== undefined) {
  console.error(`bequadro: couldn't run ${binary}: ${result.error.message}`);
  process.exit(2);
}

if (result.signal !== null) {
  process.kill(process.pid, result.signal);
}

process.exit(result.status ?? 2);
