// CLI-009: the bequadro npm package and its platform package, installed from the packed tarballs.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  chmodSync,
  closeSync,
  existsSync,
  mkdirSync,
  openSync,
  readdirSync,
  readFileSync,
  renameSync,
  writeFileSync,
} from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, relative, resolve } from "node:path";
import { test } from "node:test";
import { binaryPath, generate, generateArguments, parseDiagnostics, platformPackages } from "bequadro";
import { EOL } from "node:os";
import { Project } from "./fixture.ts";

const require = createRequire(import.meta.url);
const packageDirectory = dirname(require.resolve("bequadro/package.json"));
const version = (JSON.parse(readFileSync(join(packageDirectory, "package.json"), "utf8")) as { version: string })
  .version;

/** Runs action with process.platform and process.arch replaced, as on another machine. */
function onPlatform<T>(platform: string, arch: string, action: () => T): T {
  const original = {
    platform: Object.getOwnPropertyDescriptor(process, "platform")!,
    arch: Object.getOwnPropertyDescriptor(process, "arch")!,
  };
  Object.defineProperty(process, "platform", { ...original.platform, value: platform });
  Object.defineProperty(process, "arch", { ...original.arch, value: arch });
  try {
    return action();
  } finally {
    Object.defineProperty(process, "platform", original.platform);
    Object.defineProperty(process, "arch", original.arch);
  }
}

/** Runs action with BEQUADRO_BINARY set. */
async function withBinary<T>(binary: string, action: () => Promise<T>): Promise<T> {
  process.env["BEQUADRO_BINARY"] = binary;
  try {
    return await action();
  } finally {
    delete process.env["BEQUADRO_BINARY"];
  }
}

// Where file permissions can deny access
const permissionsApply = process.platform !== "win32" && process.getuid?.() !== 0;

function command(...args: string[]): ReturnType<typeof spawnSync> {
  return spawnSync(process.execPath, [join(packageDirectory, "dist", "cli.js"), ...args], { encoding: "utf8" });
}

test("the executable comes from the platform package for this platform", () => {
  const name = platformPackages[`${process.platform}-${process.arch}`];
  assert.ok(name, "this platform is supported");
  const path = binaryPath();
  assert.ok(existsSync(path), path);
  assert.equal(dirname(dirname(path)), dirname(require.resolve(`${name}/package.json`)));
});

test("the platform package has the same version as the main package", () => {
  const name = platformPackages[`${process.platform}-${process.arch}`]!;
  const manifest = JSON.parse(readFileSync(require.resolve(`${name}/package.json`), "utf8")) as Record<string, unknown>;
  assert.equal(manifest["version"], version);
  assert.deepEqual(manifest["os"], [process.platform]);
  assert.deepEqual(manifest["cpu"], [process.arch]);
  // So that npm skips it where the C library is musl
  assert.deepEqual(manifest["libc"], process.platform === "linux" ? ["glibc"] : undefined);
  const main = JSON.parse(readFileSync(join(packageDirectory, "package.json"), "utf8")) as Record<string, unknown>;
  assert.deepEqual(
    main["optionalDependencies"],
    Object.fromEntries(Object.values(platformPackages).map((p) => [p, version])),
  );
});

test("every package is MIT licensed and includes the license", () => {
  const name = platformPackages[`${process.platform}-${process.arch}`]!;
  for (const directory of [packageDirectory, dirname(require.resolve(`${name}/package.json`))]) {
    const manifest = JSON.parse(readFileSync(join(directory, "package.json"), "utf8")) as Record<string, unknown>;
    assert.equal(manifest["license"], "MIT", directory);
    assert.match(readFileSync(join(directory, "LICENSE"), "utf8"), /^MIT License/);
  }
});

test("BEQUADRO_BINARY overrides the executable, relative to the current directory", async () => {
  await withBinary("/custom/bequadro", async () => assert.equal(binaryPath(), resolve("/custom/bequadro")));
  await withBinary(join("custom", "bequadro"), async () =>
    assert.equal(binaryPath(), join(process.cwd(), "custom", "bequadro")),
  );

  // Not relative to the working directory generate runs in
  using project = new Project();
  const binary = relative(process.cwd(), binaryPath());
  const result = await withBinary(binary, () =>
    generate({ target: "typescript", inputs: ["schemas"], out: "generated", cwd: project.root }),
  );
  assert.equal(result.exitCode, 0, result.stderr);
});

test("binaryPath says what to do on an unsupported platform", () => {
  assert.throws(
    () => onPlatform("aix", "ppc64", binaryPath),
    /bequadro has no executable for aix-ppc64 \(supported: darwin-arm64, .*\)\. Set BEQUADRO_BINARY/,
  );
});

test("binaryPath says what to do when the platform package isn't installed", () => {
  // The package for the other architecture of this platform, which npm didn't install
  const arch = process.arch === "x64" ? "arm64" : "x64";
  assert.throws(
    () => onPlatform(process.platform, arch, binaryPath),
    (error: Error) => {
      assert.match(error.message, new RegExp(`is in the package @bequadro/${process.platform}-${arch}, which isn't installed`));
      assert.match(error.message, /reinstall without --omit=optional/);
      return true;
    },
  );
});

test("generate rejects, rather than throwing, when there is no executable", async () => {
  using project = new Project();
  const options = { target: "typescript", inputs: ["schemas"], out: "generated", cwd: project.root } as const;

  const unsupported = onPlatform("aix", "ppc64", () => generate(options));
  await assert.rejects(unsupported, /bequadro has no executable for aix-ppc64/);
  await withBinary(join(project.root, "no-such-executable"), async () => {
    await assert.rejects(generate(options), /bequadro couldn't run .*no-such-executable/);
  });
  await assert.rejects(
    generate({ ...options, cwd: join(project.root, "no-such-directory") }),
    /the working directory .*no-such-directory doesn't exist/,
  );
});

test("generate rejects when the executable is killed by a signal", { skip: process.platform === "win32" }, async () => {
  using project = new Project();
  const script = join(project.root, "killed.sh");
  writeFileSync(script, "#!/bin/sh\nkill -TERM $$\n");
  chmodSync(script, 0o755);
  await withBinary(script, async () => {
    await assert.rejects(
      generate({ target: "typescript", inputs: ["schemas"], out: "generated", cwd: project.root }),
      /bequadro was terminated by SIGTERM/,
    );
  });
});

test("generate reports an empty out or input as a usage error, and writes nothing", async () => {
  using project = new Project();
  const before = readdirSync(project.root).sort();

  const out = await generate({ target: "typescript", inputs: ["schemas"], out: "", cwd: project.root });
  const input = await generate({ target: "typescript", inputs: ["schemas", ""], out: "generated", cwd: project.root });

  assert.equal(out.exitCode, 2);
  assert.equal(out.stderr, `bequadro: Empty value for --out.${EOL}`);
  assert.equal(input.exitCode, 2);
  assert.equal(input.stderr, `bequadro: An input is empty.${EOL}`);
  assert.deepEqual(readdirSync(project.root).sort(), before);
});

test("the executable's file errors name the file and the reason", async () => {
  // The executable is built without .NET's exception messages (UseSystemResourceKeys), so this checks the CLI's own
  using project = new Project();
  project.write("file", "");
  const result = await generate({ target: "typescript", inputs: ["schemas"], out: "file", cwd: project.root });
  assert.equal(result.exitCode, 2);
  assert.equal(result.stderr, `bequadro: The output directory 'file' is a file.${EOL}`);

  mkdirSync(project.generated);
  project.write("generated/Shapes", "mine");
  const blocked = await generate({ target: "python", inputs: ["schemas"], out: "generated", cwd: project.root });
  assert.equal(blocked.exitCode, 2);
  assert.equal(
    blocked.stderr,
    `bequadro: 'Shapes' in the output directory is a file, where a directory would be generated; nothing was written.${EOL}`,
  );
  assert.deepEqual(readdirSync(project.generated), ["Shapes"]);

  if (permissionsApply) {
    chmodSync(project.schema, 0);
    try {
      const unreadable = await generate({ target: "typescript", inputs: ["schemas"], out: "out", cwd: project.root });
      assert.equal(unreadable.exitCode, 2);
      assert.equal(unreadable.stderr, `bequadro: Can't read '${join("schemas", "shapes.fbs")}': permission denied.${EOL}`);
    } finally {
      chmodSync(project.schema, 0o644);
    }
  }
});

test("diagnostics keep non-ASCII paths", async () => {
  using project = new Project("namespace Shapes;\n\ntable Circle { radius: nosuchtype; }\n");
  renameSync(join(project.root, "schemas"), join(project.root, "schémas"));
  const result = await generate({ target: "typescript", inputs: ["schémas"], out: "generated", cwd: project.root });
  assert.equal(result.exitCode, 1, result.stderr);
  assert.equal(result.diagnostics[0]?.path, join("schémas", "shapes.fbs"));
});

test("the bequadro command passes its arguments and exit code through", () => {
  const ok = command("--version");
  assert.equal(ok.status, 0, String(ok.stderr));
  assert.equal(String(ok.stdout).trim(), version);

  const usage = command("generate", "--no-such-option");
  assert.equal(usage.status, 2);
  assert.match(String(usage.stderr), /Unknown option '--no-such-option'/);
});

test("the bequadro command explains a missing executable", () => {
  const result = spawnSync(process.execPath, [join(packageDirectory, "dist", "cli.js"), "--version"], {
    encoding: "utf8",
    env: { ...process.env, BEQUADRO_BINARY: join(packageDirectory, "no-such-executable") },
  });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /couldn't run .*no-such-executable/);
});

test("generate writes the code and reports success", async () => {
  using project = new Project();
  const result = await generate({ target: "typescript", inputs: ["schemas"], out: "generated", cwd: project.root });
  assert.equal(result.exitCode, 0, result.stderr);
  assert.deepEqual(result.diagnostics, []);
  assert.match(project.read("generated/Shapes.ts") ?? "", /export const Circle/);
  assert.ok(project.read("generated/bequadro.ts"));

  const check = await generate({ target: "typescript", inputs: ["schemas"], out: "generated", check: true, cwd: project.root });
  assert.equal(check.exitCode, 0, check.stdout + check.stderr);
});

test("generate reads a schema an editor has open for writing", async () => {
  using project = new Project();
  const editor = openSync(project.schema, "r+");
  try {
    const result = await generate({ target: "typescript", inputs: ["schemas"], out: "generated", cwd: project.root });
    assert.equal(result.exitCode, 0, result.stderr);
    assert.match(project.read("generated/Shapes.ts") ?? "", /export const Circle/);
  } finally {
    closeSync(editor);
  }
});

test("generate parses diagnostics and writes nothing for a schema with errors", async () => {
  using project = new Project("namespace Shapes;\n\ntable Circle { radius: nosuchtype; }\n");
  const result = await generate({ target: "python", inputs: ["schemas"], out: "generated", cwd: project.root });
  assert.equal(result.exitCode, 1);
  assert.equal(result.diagnostics.length, 1);
  const [diagnostic] = result.diagnostics;
  assert.equal(diagnostic?.path, join("schemas", "shapes.fbs"));
  assert.equal(diagnostic?.line, 3);
  assert.equal(diagnostic?.column, 24);
  assert.equal(diagnostic?.id, "BQ2003");
  assert.match(diagnostic?.message ?? "", /nosuchtype/);
  assert.equal(existsSync(project.generated), false);
});

test("generate passes every option", () => {
  assert.deepEqual(
    generateArguments({
      target: "typescript",
      inputs: ["a.fbs", "-b.fbs"],
      out: "-out",
      importExtension: "js",
      requireScalarsWithoutDefault: true,
      check: true,
    }),
    [
      "generate",
      "--target",
      "typescript",
      "--out",
      "./-out",
      "--import-extension",
      "js",
      "--require-scalars-without-default",
      "--check",
      "a.fbs",
      "./-b.fbs",
    ],
  );
});

test("parseDiagnostics reads Windows paths and ignores other lines", () => {
  assert.deepEqual(parseDiagnostics("C:\\s\\a.fbs:2:5: error BQ2003: Unknown type 'x'\r\nsomething else\r\n"), [
    { path: "C:\\s\\a.fbs", line: 2, column: 5, id: "BQ2003", message: "Unknown type 'x'" },
  ]);
});

test("parseDiagnostics reads a message that quotes something like a diagnostic", () => {
  const message = "Included file 'x:1:2: error BQ9999: y.fbs' (resolved to 'x:1:2: error BQ9999: y.fbs') is not in the schema set";
  assert.deepEqual(parseDiagnostics(`a.fbs:1:1: error BQ2001: ${message}\n`), [
    { path: "a.fbs", line: 1, column: 1, id: "BQ2001", message },
  ]);
});
