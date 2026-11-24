// CLI-009, CLI-010: the packages on the oldest Node they support, 20.19, which CI runs this on after build.py has
// installed the packed tarballs here. Plain JavaScript, because Node 20 can't run the TypeScript tests.
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { generate } from "bequadro";
import bequadro from "bequadro/vite";
import { build } from "vite";

const root = realpathSync(mkdtempSync(join(tmpdir(), "bequadro-smoke-")));
try {
  mkdirSync(join(root, "schemas"));
  writeFileSync(join(root, "schemas", "shapes.fbs"), "namespace Shapes;\n\ntable Circle { radius: float = 1; }\n");

  const result = await generate({ target: "typescript", inputs: ["schemas"], out: "generated", cwd: root });
  assert.equal(result.exitCode, 0, result.stderr);
  assert.match(readFileSync(join(root, "generated", "Shapes.ts"), "utf8"), /export const Circle/);

  writeFileSync(join(root, "main.ts"), 'export { Circle } from "./vite-generated/Shapes.ts";\n');
  await build({
    root,
    configFile: false,
    logLevel: "silent",
    plugins: [bequadro({ inputs: "schemas", out: "vite-generated" })],
    build: { lib: { entry: "main.ts", formats: ["es"], fileName: () => "main.js" }, outDir: "dist" },
  });
  assert.match(readFileSync(join(root, "dist", "main.js"), "utf8"), /Circle/);
  console.log(`The packages work on Node ${process.version}.`);
} finally {
  rmSync(root, { recursive: true, force: true });
}
