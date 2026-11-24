# CLI Requirements

The `bequadro` command-line tool, which generates TypeScript ([typescript.md](typescript.md)) and Python ([python.md](python.md)) from the same schemas that the source generator compiles to C#.

## CLI-001 Native Executable

Acceptance criteria:

- The CLI is published with Native AOT as a single executable, `bequadro` (`bequadro.exe` on Windows), that runs without a .NET runtime installed.
- It builds without trimming or AOT warnings: `build.py` publishes it with warnings as errors, which the AOT compiler applies to its own warnings too.
- `build.py --pack` publishes it for the host platform to `artifacts/cli/<runtime identifier>/`, and packs it into npm packages (CLI-009).
- After CI has tested every platform package, it attaches each executable to the published GitHub release as `bequadro-<rid>` (`.exe` on Windows). The runtime identifiers are `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` and `win-arm64`. Rerunning the job skips existing assets by name.
- It is not part of the NuGet package. CON-001 is unchanged.

Design decisions:

- Native AOT, so TypeScript and Python developers don't need .NET installed, and starting it costs milliseconds, not a JIT warm-up.
- npm installs the platform executable for projects using npm (CLI-009). GitHub releases also offer direct downloads of the same tested bytes, extracted from the platform tarballs after checking their package names and versions.

## CLI-002 Command Line

Syntax: `bequadro generate --target <typescript|python> --out <directory> [options] <input>...`

Acceptance criteria:

- Each `<input>` is a file or a directory. A directory contributes every file under it, recursively, whose name ends in `.fbs` (case-insensitive). Symbolic links in it are followed, except one to a directory the search is already inside, which would loop. A file is used whatever its extension. At least one input is required.
- A directory input with no `.fbs` files under it contributes nothing, and isn't an error: if no input contributes a file, the run succeeds and the output directory holds only the runtime (CLI-006).
- Options:

  | Option | Effect |
  | --- | --- |
  | `--target typescript` / `--target python` | The language to generate. Required. |
  | `--out <directory>` | The output directory (CLI-005). Required. |
  | `--require-scalars-without-default` | The CLI equivalent of `BequadroRequireScalarsWithoutDefault` (CON-006): scalar and enum table fields without a default in the schema must be given when constructing (TS-004, PY-004). |
  | `--import-extension ts` / `--import-extension js` | TypeScript only: the extension of relative imports in generated code (TS-001). Defaults to `ts`. |
  | `--check` | Compares what would be generated with the output directory without writing anything (CLI-005). |

- `bequadro --help` prints usage, and so does `bequadro generate` with `--help` or `-h` anywhere among its options. `bequadro --version` prints the version, which is the version of the NuGet package built from the same source, and takes no other arguments.
- An unknown option, a missing, empty or invalid option value, an option given more than once, an option that doesn't apply to the target, an empty input, or an input that doesn't exist is a usage error (CLI-004).

Design decisions:

- One target per run. Each language needs its own output directory anyway, and a build step for one language shouldn't depend on the other.
- There is no configuration file. The command is short enough to live in a `package.json` script, a `Makefile` or a `pyproject.toml` task.
- An empty `--out` or input is an error, not the working directory, because that is what `--out "$UNSET"` gives, and the working directory would then be swept for stale files (CLI-005).
- An option given twice is an error rather than the last one winning: with no configuration file to override, a repeated option is a mistake in the command.
- A directory input without schemas isn't an error, so a project can point the CLI at its schema directory before it has any schemas, or remove its last one, and get an output directory with no stale modules in it.

## CLI-003 Schema Set

Acceptance criteria:

- The schema set (see the glossary in [REQUIREMENTS.md](../REQUIREMENTS.md)) is every file the inputs contribute. A file reached through more than one input, or through symbolic links, is counted once: under its own path if an input reaches it without a link, otherwise under the first of its paths in ordinal order, which is the path includes resolve against (SCH-003). The order of the inputs doesn't matter.
- Files are read as UTF-8, and a leading UTF-8 byte order mark is ignored (SCH-001). Other byte order marks get no special treatment, so a UTF-16 file has syntax errors.
- The CLI can read a schema that an editor has open for writing if the editor allows shared reads. The CLI's read handle allows writes and deletes while it is open.
- Every schema-language requirement (SCH-001 to SCH-012) applies unchanged. In particular, includes resolve only to files in the set (SCH-003). A file that is included but not given as an input produces BQ2001, as it does for a file left out of `AdditionalFiles`.
- Code is generated exactly once for each declaration in the set (CON-004).

Design decisions:

- The set works exactly as it does for the source generator, so a schema set means the same in every language. The CLI could read included files from disk on its own, but then a set that builds in TypeScript could fail in C#. Passing a directory is as short as passing a root file.
- Shared reads support editors that save in place or replace the file while generation is running, including on Windows.

## CLI-004 Diagnostics and Exit Codes

Acceptance criteria:

- The CLI reports the same diagnostics as the source generator (DIA-002). Names are never an error in any target: they are renamed (NAM-001).
- Each diagnostic is written to standard error as `{path}:{line}:{column}: error {id}: {message}`. Lines and columns are 1-based, and the path is as given on the command line, joined with the path below a directory input. Diagnostics are sorted by path, then by position.
- Paths in messages are shown the same way: a file in the set as its input gave it (BQ2002, BQ2004), and the resolved path of an include that isn't in the set (BQ2001) relative to the working directory, or absolute if the including file's input was. BQ2001 says to add the file to the inputs, where the source generator says to add it to `AdditionalFiles`.
- When standard output or error is redirected, it is UTF-8 on every platform. A console keeps its own encoding.
- Exit codes:

  | Code | Meaning |
  | --- | --- |
  | 0 | Success. With `--check`, the output is up to date. |
  | 1 | The schema set has errors, or, with `--check`, the output is out of date. |
  | 2 | A usage error, or an input or output file can't be read or written. A message on standard error says which, naming the file and the reason, such as `permission denied`. |

- When the schema set has errors, nothing is written: the output directory is left exactly as it was.
- The CLI never crashes or prints a stack trace for any schema text (DIA-001).

Design decisions:

- `path:line:column` is the format that terminals, VS Code and most editors turn into links.
- The executable is built with `UseSystemResourceKeys`, which removes .NET's exception messages to keep it small, so the CLI writes its own messages for file errors, from the kind of error and the path it was working on.
- Paths in messages follow the command line, so that output doesn't depend on where the project is checked out, and a path can be copied back into the command.
- The source generator generates whatever isn't in error (DIA-001), because it runs as the user types, and removing every type would flood the editor with errors. A CLI run is an explicit step, so it keeps the last good output instead. That also keeps the editor working, and partial output can never be committed by accident.

## CLI-005 Output Directory

Acceptance criteria:

- The output directory is created if it doesn't exist.
- Every generated file starts with the comment `Code generated by Bequadro. DO NOT EDIT.`, written in the target language's comment syntax.
- A file is a generated file if its first line, after an optional byte order mark, is exactly that comment (followed by LF, CRLF or the end of the file).
- A generated file is written only if its content changed, so unchanged files keep their modification times and don't trigger watchers. Each file is replaced atomically: readers see the old content or the new, never a partial file.
- Generated files under the output directory that this run didn't generate are deleted. Directories left empty by that are removed. Other files are never changed or deleted.
- Symbolic links below the output directory are never followed, changed or deleted, and files and directories whose names start with `.` are never deleted or searched. The output directory itself may be a link.
- Nothing is written, and the CLI exits with code 2 naming the path, if a path the run would generate exists and isn't a generated file, is a directory or a symbolic link, or if a directory it would generate a file in is a file or a symbolic link. The same applies if `--out` is a file.
- Runs on the same output directory at the same time, such as a dev server and a test runner, never report each other's files as not generated. The output is that of whichever run wrote last.
- Output is deterministic: the same schema set and options give the same bytes whatever the input order, platform or working directory. Files are UTF-8 without a byte order mark, with LF line endings and a final newline.
- With `--check`, nothing is written. The CLI lists each file it would create, change or delete, and exits with 1 if there are any.

Design decisions:

- Deleting stale files is necessary, because removing a namespace or declaration must not leave an old module behind that still imports. Limiting it to files with the generated comment means pointing `--out` at the wrong directory can't destroy hand-written code.
- A link inside the output directory usually points at something another tool owns, such as another package's generated code, or a pnpm-style `node_modules`, so following one could delete or overwrite files outside the output directory (CLI-007).
- Generated names never start with `.`, and each file is written to a temporary `.{name}.{random}.tmp` next to it, then renamed, so another run's sweep can't delete a file being written. Whether a planned path is a generated file is decided by reading that file, not from a listing of the directory, which can miss a file that another run is renaming at that moment.
- `--check` lets CI verify that committed generated code matches its schemas.

## CLI-006 Runtime Emitted With the Output

Acceptance criteria:

- Every run writes the runtime library into the output directory, next to the generated code: `bequadro.ts` for TypeScript, `bequadro.py` for Python. Its content depends only on the CLI version and the options.
- Generated code and the runtime depend only on the language's standard library and built-ins. Consumers install no npm or PyPI packages.

Design decisions:

- The runtime is emitted with the code, not published as a package, for the same reason that C# ships the runtime and the generator in one package (CON-001): the generated code always runs against exactly the runtime it was generated for. Nothing has to be published to npm or PyPI, and nothing can drift.
- The cost is a copy of the runtime per output directory. Values are plain data (TypeScript objects, Python dataclasses from their own modules), so two output directories in one program don't interfere.

## CLI-007 No Network or Processes

Acceptance criteria:

- The CLI never accesses the network and never starts a process. It reads only its inputs and reads and writes only the output directory.

## CLI-008 Performance

Acceptance criteria:

- Generating either target for a schema set of about 50 files and 400 declarations (the INC-003 set) takes under 1 second, process start included, on a typical developer machine. A test fails above a generous 5 second threshold, to catch pathological regressions.

## CLI-009 npm Packages

Acceptance criteria:

- The CLI is published to npm with the version of the NuGet package, as:
  - a platform package, `@bequadro/<platform>-<arch>`, for each of `linux-x64`, `linux-arm64`, `darwin-x64`, `darwin-arm64`, `win32-x64` and `win32-arm64` (Node's `process.platform` and `process.arch`), each holding only the executable for that platform in `bin/`, with `os` and `cpu` fields so that npm installs each only on its platform. The Linux packages also have `"libc": ["glibc"]`: their executables need glibc 2.34 or later (RHEL 9, Debian 12, Ubuntu 22.04 and later), which `build.py` checks;
  - `bequadro`, which has every platform package as an exact-version optional dependency, and holds the Node API, the `bequadro` command and the Vite plugin (CLI-010).
- The `bequadro` command runs the executable with the same arguments, standard streams and exit code. If the executable can't be found or started, it exits with code 2 and says why.
- The Node API, the package's main export, has:
  - `binaryPath()`: the `BEQUADRO_BINARY` environment variable if it is set, resolved against the process's current directory, otherwise the executable of the platform package for `process.platform` and `process.arch`. On an unsupported platform, or if the platform package isn't installed, it throws an error that says what to do.
  - `generate(options)`: runs `bequadro generate` with an option for each command-line option (CLI-002) and a working directory, and resolves with the exit code, standard output and error, and the diagnostics parsed from standard error (CLI-004). It never throws. It rejects if the executable can't be found or started, saying why (a missing working directory is named as such), or if it is killed by a signal, so that there is no exit code. Paths that start with `-` are passed so the CLI doesn't read them as options; empty ones are passed as they are, so the CLI reports them (CLI-002).
  - `generateArguments(options)`: the arguments `generate` passes to the executable for `options`, for callers that run it themselves.
  - `platformPackages`: the platform package for each supported `${process.platform}-${process.arch}`.
  - `parseDiagnostics(stderr)`: the diagnostics in the CLI's standard error. A diagnostic's path is the shortest prefix of the line that fits the format, because messages can quote schema text that looks like a diagnostic.
- Every package is MIT licensed and includes the `LICENSE` file.
- The package is ESM with type declarations, supports Node 20.19 and later, and has no dependencies other than the platform packages. Its declarations type-check in a consumer with `strict` and `exactOptionalPropertyTypes`. CI runs a smoke test of the API and the Vite plugin on Node 20.19.
- `build.py --pack` packs the package for the host platform and the main package into `artifacts/npm/`. CI builds and tests each platform package on a runner of that platform, and publishes them all when a GitHub release is published (CON-007), the platform packages first. A prerelease version gets the dist-tag `next`, so `npm install bequadro` doesn't install it.

Design decisions:

- One package per platform, selected by npm with `os` and `cpu`, is the pattern of esbuild and TypeScript 7: each install downloads one 3 MB executable, with no install script and no download from elsewhere at install time.
- The platform packages share the `@bequadro` scope, so owning the npm organization reserves every platform's name, now and later. Unscoped names would each have to be claimed, and npm silently installs whatever package has an optional dependency's name.
- The platforms are those GitHub has free hosted runners for, so each is built and tested natively. Another needs only a runner and a line in `platformPackages` and `NPM_PLATFORMS`. Linux with musl, as on Alpine, isn't supported: it would need a separate package, built on a musl runner. The `libc` field makes npm skip the glibc package there, so `binaryPath` explains the problem instead of the executable failing to load.
- The glibc minimum is what .NET's Native AOT output on the CI runner needs. `build.py` reads the symbol versions the executable needs, so a newer runner or .NET can't raise it unnoticed.
- `generate` rejects for a signal rather than resolving, because `exitCode` would have to be made up, and a killed run has written some of its files.
- A relative `BEQUADRO_BINARY` is resolved against the process's directory, where it was set, not against `cwd`, which differs per call (the Vite plugin passes Vite's root).
- `BEQUADRO_BINARY` lets a project use an executable built from source, and lets the tests point at a missing one.
- Before the packages are published, a project can install the tarballs from `artifacts/npm/`, with the platform packages as optional `file:` dependencies, so that npm skips the one for the other platform.

## CLI-010 Vite Plugin

Acceptance criteria:

- `bequadro/vite` exports a Vite plugin, `bequadro({ inputs, out, importExtension?, requireScalarsWithoutDefault? })`, that generates TypeScript with the CLI. `inputs` is a path or an array of paths and `out` a path, relative to Vite's root, passed to the CLI as given with Vite's root as its working directory, so an empty one is an error (CLI-002). It supports Vite 7.2 and later, which the tests check with both Vite 7.2 and the newest Vite.
- Generation runs when a build or the dev server starts, before any module is loaded.
- In the dev server, it runs again whenever a `.fbs` file under a directory input, or a file input, is added, changed or deleted. Runs never overlap: changes during a run cause one more run after it. Because the file watcher drops changes to a file within 50 ms of the last one it reported, each run checks the schema files' sizes and modification times again shortly after it ends, and runs again if they changed since it started. The code always ends up generated from the schemas as last saved, also after the server restarts with the same plugin instance, as `server.restart()` does with an inline configuration.
- With `vite build --watch`, every schema file is a watched file, so changing one rebuilds, which generates first.
- Schema errors fail a build with the CLI's diagnostics. In the dev server they are logged and sent to the error overlay, located at the first diagnostic, and sent again to pages that connect while they last. The last good code is kept (CLI-004). When a run succeeds again, the page reloads, which clears the overlay.

Design decisions:

- The plugin calls the CLI rather than generating in-process, so it generates exactly what the command line does, and it costs about 20 ms per run.
- Only TypeScript: a Vite project has no use for Python.
- Unchanged files aren't rewritten (CLI-005), so a schema change only reaches hot module replacement through the modules whose code changed.
