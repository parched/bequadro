# bequadro

Generates TypeScript and Python from [FlatBuffers](https://flatbuffers.dev) schemas, as plain, mutable objects that are parsed all at once, with shared tables kept shared. Bequadro is also a C# source generator, and all three languages write identical bytes. See the [Bequadro documentation](https://github.com/parched/bequadro#readme) for the generated code.

The generator is a native executable, installed for your platform as an optional dependency, such as `@bequadro/linux-x64`, so it needs no .NET and starts in milliseconds. Generated code needs no packages at run time: the runtime, `bequadro.ts`, is written next to it.

```sh
npm install --save-dev bequadro
```

## Command Line

```sh
npx bequadro generate --target typescript --out src/schema schemas/
```

`--check` writes nothing and fails if the output is out of date, which is useful in CI. `npx bequadro --help` lists the options.

## Vite

```ts
// vite.config.ts
import { defineConfig } from "vite";
import bequadro from "bequadro/vite";

export default defineConfig({
  plugins: [bequadro({ inputs: "schemas", out: "src/schema" })],
});
```

The plugin generates the code when the dev server or a build starts, and again whenever a `.fbs` file under `inputs` is added, changed or deleted, one run at a time. Paths are relative to Vite's root. Unchanged files aren't rewritten, so only real changes reach hot module replacement. Schema errors fail a build. In the dev server they are logged and shown in the error overlay, and the last good code is kept until they are fixed.

| Option | Description |
| --- | --- |
| `inputs` | A schema file or directory, or an array of them. Directories are searched recursively for `.fbs` files. |
| `out` | The output directory. Only files Bequadro generated are ever changed or deleted in it. |
| `importExtension` | `"ts"` (the default) or `"js"`, the extension of relative imports in generated code. |
| `requireScalarsWithoutDefault` | Scalar and enum fields without a default in the schema must be given when constructing. |

It needs Vite 7.2 or later.

## Node API

```ts
import { generate } from "bequadro";

const result = await generate({ target: "typescript", inputs: ["schemas"], out: "src/schema" });
for (const d of result.diagnostics) {
  console.log(`${d.path}:${d.line}:${d.column} ${d.id} ${d.message}`);
}
```

`generate` takes an option for each command-line option: `target`, `inputs`, `out`, `importExtension`, `requireScalarsWithoutDefault`, `noStubs` (Python only: no `.pyi` stubs) and `check`, and `cwd` for the working directory. It resolves with the exit code (0 for success, 1 for schema errors or out-of-date output with `check`, 2 for usage and file errors), the parsed diagnostics, and the output. It rejects only if the executable can't be run, or is killed by a signal. `binaryPath()` returns the executable's path, `generateArguments(options)` the command-line arguments `generate` passes it, `parseDiagnostics(stderr)` parses diagnostics from the CLI's standard error, and `platformPackages` maps each supported `${process.platform}-${process.arch}` to its platform package.

## Platforms

Linux, macOS and Windows, each on x64 and arm64. Linux needs glibc 2.34 or later (RHEL 9, Debian 12, Ubuntu 22.04 and later); musl, as on Alpine, isn't supported. Set `BEQUADRO_BINARY` to the path of an executable to use that instead, for example one built from source with `uv run build.py --pack`. A relative path is relative to the current directory.

## License

MIT
