// CLI-010: the Vite plugin with the oldest Vite it supports, 7.2.
import assert from "node:assert/strict";
import { join } from "node:path";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import bequadro from "bequadro/vite";
import { build, createServer, type InlineConfig, type PluginOption } from "vite-7.2";
import { Project, shapes, until } from "./fixture.ts";

function config(project: Project): InlineConfig {
  return {
    root: project.root,
    configFile: false,
    logLevel: "silent",
    // The plugin's declarations take Vite's types from "vite", which is Vite 7.2 in a Vite 7.2 project, but Vite 8 here
    plugins: [bequadro({ inputs: "schemas", out: "generated" }) as unknown as PluginOption],
  };
}

test("vite 7.2 builds the generated code", async () => {
  using project = new Project();
  project.write(
    "main.ts",
    'import * as Shapes from "./generated/Shapes.ts";\n' +
      "export const radius = Shapes.Circle.parse(Shapes.Circle.write({ radius: 2 })).radius;\n",
  );
  await build({
    ...config(project),
    build: { lib: { entry: "main.ts", formats: ["es"], fileName: () => "main.js" }, outDir: "dist", minify: false },
  });
  const bundle = (await import(pathToFileURL(join(project.root, "dist", "main.js")).href)) as { radius: number };
  assert.equal(bundle.radius, 2);
});

test("vite 7.2's dev server regenerates when a schema changes", async () => {
  using project = new Project();
  const server = await createServer({ ...config(project), server: { middlewareMode: true } });
  try {
    await until(() => project.read("generated/Shapes.ts") !== undefined, "the code is generated on start");
    project.writeSchema(shapes + "\ntable Square { side: float; }\n");
    await until(() => project.read("generated/Shapes.ts")?.includes("export const Square") === true, "Square is generated");
  } finally {
    await server.close();
  }
});
