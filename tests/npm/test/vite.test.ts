// CLI-010: the Vite plugin, in real builds and dev servers.
import assert from "node:assert/strict";
import { rmSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import bequadro from "bequadro/vite";
import { build, createLogger, createServer, type HotPayload, type InlineConfig, type ViteDevServer } from "vite";
import { Project, shapes, until } from "./fixture.ts";

function config(project: Project): InlineConfig {
  return {
    root: project.root,
    configFile: false,
    logLevel: "silent",
    plugins: [bequadro({ inputs: "schemas", out: "generated" })],
  };
}

function buildLibrary(project: Project, watch = false): ReturnType<typeof build> {
  project.write(
    "main.ts",
    'import * as Shapes from "./generated/Shapes.ts";\n' +
      "export const radius = Shapes.Circle.parse(Shapes.Circle.write({ radius: 2 })).radius;\n" +
      "export const names = Object.keys(Shapes);\n",
  );
  return build({
    ...config(project),
    build: {
      lib: { entry: "main.ts", formats: ["es"], fileName: () => "main.js" },
      outDir: "dist",
      minify: false,
      watch: watch ? {} : null,
    },
  });
}

test("vite build generates the code before bundling it", async () => {
  using project = new Project();
  await buildLibrary(project);
  const bundle = (await import(pathToFileURL(join(project.root, "dist", "main.js")).href)) as { radius: number };
  assert.equal(bundle.radius, 2);
});

test("vite build fails with the diagnostics when the schemas have errors", async () => {
  using project = new Project("namespace Shapes;\n\ntable Circle { radius: nosuchtype; }\n");
  await assert.rejects(buildLibrary(project), (error: Error) => {
    assert.match(error.message, /shapes\.fbs:3:24: error BQ2003: Unknown type 'nosuchtype'/);
    return true;
  });
});

test("vite build --watch rebuilds when a schema changes", async () => {
  using project = new Project();
  const watcher = await buildLibrary(project, true);
  assert.ok("close" in watcher);
  try {
    await until(() => project.read("dist/main.js") !== undefined, "the first build finishes");
    project.writeSchema(shapes + "\ntable Square { side: float; }\n");
    await until(() => project.read("dist/main.js")?.includes("Square") === true, "the bundle includes Square");
  } finally {
    await watcher.close();
  }
});

interface DevServer extends AsyncDisposable {
  readonly server: ViteDevServer;
  /** Every payload the plugin sent to the browser. */
  readonly sent: HotPayload[];
}

async function devServer(project: Project): Promise<DevServer> {
  const server = await createServer({ ...config(project), server: { middlewareMode: true } });
  const sent: HotPayload[] = [];
  const send = server.ws.send.bind(server.ws) as (payload: HotPayload) => void;
  server.ws.send = ((payload: HotPayload) => {
    sent.push(payload);
    send(payload);
  }) as typeof server.ws.send;
  await until(() => project.read("generated/Shapes.ts") !== undefined, "the code is generated on start");
  return { server, sent, [Symbol.asyncDispose]: () => server.close() };
}

test("the dev server regenerates when a schema changes", async () => {
  using project = new Project();
  await using dev = await devServer(project);

  project.writeSchema(shapes + "\ntable Square { side: float; }\n");
  await until(() => project.read("generated/Shapes.ts")?.includes("export const Square") === true, "Square is generated");

  project.write("schemas/more.fbs", "namespace More;\n\ntable Thing { name: string; }\n");
  await until(() => project.read("generated/More.ts") !== undefined, "a new schema file is picked up");
  assert.deepEqual(dev.sent.filter((p) => p.type === "error"), []);
});

test("the dev server shows schema errors in the overlay until they are fixed", async () => {
  using project = new Project();
  await using dev = await devServer(project);
  const before = project.read("generated/Shapes.ts");

  project.writeSchema("namespace Shapes;\n\ntable Circle { radius: nosuchtype; }\n");
  await until(() => dev.sent.some((p) => p.type === "error"), "an error is sent");
  const error = dev.sent.find((p) => p.type === "error");
  assert.ok(error?.type === "error");
  assert.equal(error.err.plugin, "bequadro");
  assert.match(error.err.message, /error BQ2003: Unknown type 'nosuchtype'/);
  assert.deepEqual(error.err.loc, { file: project.schema, line: 3, column: 24 });
  assert.equal(project.read("generated/Shapes.ts"), before, "the last good code is kept");

  project.writeSchema(shapes);
  await until(() => dev.sent.some((p) => p.type === "full-reload"), "the overlay is cleared");
});

test("the dev server ends up with the code for the last of many quick changes", async () => {
  using project = new Project();
  await using dev = await devServer(project);

  // Saved atomically, so no run can read a half-written schema, and any error sent would be the plugin's own
  for (let i = 0; i < 20; i++) {
    project.replaceSchema(shapes + `\ntable Last${String(i)} { value: int; }\n`);
    await new Promise((resolve) => setTimeout(resolve, 5));
  }

  await until(() => project.read("generated/Shapes.ts")?.includes("export const Last19") === true, "Last19 is generated");
  assert.deepEqual(dev.sent.filter((p) => p.type === "error"), []);
});

test("the dev server keeps rechecking the schemas after it restarts", async () => {
  // With an inline configuration, the restarted server uses the same plugin instance, which the old server closed
  using project = new Project();
  await using dev = await devServer(project);
  await dev.server.restart();

  for (let round = 0; round < 3; round++) {
    for (let i = 0; i < 20; i++) {
      project.writeSchema(shapes + `\ntable Last${String(round)}x${String(i)} { value: int; }\n`);
      await new Promise((resolve) => setTimeout(resolve, 5));
    }

    const last = `export const Last${String(round)}x19`;
    await until(() => project.read("generated/Shapes.ts")?.includes(last) === true, `${last} is generated`);
  }
});

test("the dev server sends schema errors to pages that connect while they last", async () => {
  using project = new Project("namespace Shapes;\n\ntable Circle { radius: nosuchtype; }\n");
  const logged: string[] = [];
  const customLogger = createLogger("silent");
  customLogger.error = (message) => logged.push(message);
  const server = await createServer({ ...config(project), customLogger, server: { port: 0, host: "127.0.0.1" } });
  await server.listen();
  try {
    const address = server.httpServer?.address();
    assert.ok(address !== null && typeof address === "object");
    // So the error was sent before the page connects
    await until(() => logged.some((m) => m.includes("BQ2003")), "the error is logged");
    const socket = new WebSocket(
      `ws://127.0.0.1:${String(address.port)}/?token=${server.config.webSocketToken}`,
      "vite-hmr",
    );
    const messages: { type: string; err?: { message: string } }[] = [];
    socket.addEventListener("message", (event) => {
      messages.push(JSON.parse(String(event.data)) as (typeof messages)[number]);
    });
    try {
      await until(() => messages.some((m) => m.type === "error"), "the page is sent the error");
      assert.match(messages.find((m) => m.type === "error")?.err?.message ?? "", /error BQ2003: Unknown type 'nosuchtype'/);
    } finally {
      socket.close();
    }
  } finally {
    await server.close();
  }
});

test("the plugin takes an array of file and directory inputs, and every option", async () => {
  using project = new Project();
  project.write("extra.fbs", "namespace Extra;\n\ntable Thing { count: int; }\n");
  project.write("schemas/more.fbs", "namespace More;\n\ntable Other { name: string; }\n");
  const server = await createServer({
    ...config(project),
    plugins: [
      bequadro({
        inputs: ["extra.fbs", "schemas"],
        out: "generated",
        importExtension: "js",
        requireScalarsWithoutDefault: true,
      }),
    ],
    server: { middlewareMode: true },
  });
  try {
    await until(() => project.read("generated/More.ts") !== undefined, "the code is generated on start");
    const extra = project.read("generated/Extra.ts") ?? "";
    assert.match(extra, /from "\.\/bequadro\.js"/, "imports use the js extension");
    assert.doesNotMatch(extra, /count\?:/, "a scalar without a default must be given when constructing");

    project.write("extra.fbs", "namespace Extra;\n\ntable Thing { count: int; size: int; }\n");
    await until(() => project.read("generated/Extra.ts")?.includes("size") === true, "a file input is watched");

    rmSync(join(project.root, "schemas", "more.fbs"));
    await until(() => project.read("generated/More.ts") === undefined, "a deleted schema's module is deleted");
  } finally {
    await server.close();
  }
});

test("vite build fails with the CLI's message for an empty out", async () => {
  using project = new Project();
  await assert.rejects(
    build({ ...config(project), plugins: [bequadro({ inputs: "schemas", out: "" })], build: { write: false } }),
    /Empty value for --out/,
  );
  assert.equal(project.read("bequadro.ts"), undefined, "nothing is generated in the root");
});
