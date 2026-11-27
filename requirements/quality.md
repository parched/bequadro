# Quality and Testing Requirements

## QUA-001 Test Coverage by Area

Acceptance criteria:

- **Lexer and parser**: unit tests for every construct in [schema-language.md](schema-language.md), both accepted and rejected. Each diagnostic in the [catalog](diagnostics.md) has at least one test that asserts its ID and location. Random input never makes the parser or binder throw.
- **Semantic analysis**: tests for name resolution (namespace walk-up, visibility through transitive includes), struct layout (size, alignment, padding), enum value assignment, union discriminators, and default value checking.
- **Code generation**: snapshot tests of the generated source for one representative schema per declaration kind, compared against committed `*.verified.cs` files. Every snapshot change is reviewed when it is accepted.
- **Generated code compiles**: schemas covering every supported feature compile without warnings, with nullable enabled and all warnings treated as errors. They compile with and without each build property (CON-006). This includes consumer partial declarations that add constructors, interfaces and members, and C# keywords used as schema identifiers.
- **Round trip**: for every supported field type, writing then parsing gives an equivalent graph. Property-based tests ([FsCheck](https://fscheck.github.io/FsCheck/)) check this over generated scalars, strings, vectors and graphs.
- **Interoperability**: buffers written by Bequadro are read correctly by the official `Google.FlatBuffers` C# runtime, and buffers built with `Google.FlatBuffers.FlatBufferBuilder` are read correctly by Bequadro. This covers scalars, defaults, optional scalars, strings, structs, nested tables, all vector kinds, unions and union vectors.
- **Identity**: tests for SER-003 and SER-011, including sharing within vectors, within unions and as the root, and cycles on write.
- **Malformed input**: random truncation and corruption of valid buffers only ever produce `InvalidDataException` or a successful parse (SER-009).
- **Incrementality**: the INC-001 acceptance tests.
- **CLI, TypeScript and Python**: QUA-003.

Design decisions:

- Snapshots use a small in-repo helper, not the Verify library. Recent Verify versions refuse to build unless a sponsorship license property is set, and comparing text files needs very little code.

## QUA-002 Build Hygiene

Acceptance criteria:

- The solution builds with zero warnings with `-warnaserror`.
- Code is formatted with CSharpier, and `dotnet csharpier check .` passes.
- `build.py --check` in this directory runs the formatting check, the build and every test, the TypeScript and Python tests of QUA-003 included.
- In CI, the C# tests compare snapshots and check the diagnostic catalog in the checkout, and write the cross-language fixtures and random schemas to its `artifacts/` directory.

Design decisions:

- Test projects keep physical caller paths to locate files in the checkout. CI still maps source paths for published packages (CON-007).

## QUA-003 CLI, TypeScript and Python Tests

Acceptance criteria:

- **CLI**: tests for every option and usage error, the exit codes, the diagnostic format, writing nothing when there are errors, unchanged files keeping their modification times, deleting only stale generated files, refusing to overwrite files that aren't generated, `--check`, and deterministic output (CLI-002 to CLI-005).
- **Renaming**: tests for each rule of NAM-002, API-002, TS-003 and PY-003, including chains of collisions, the precedence of schema names over derived ones, stability when a field is added, and the documentation note on renamed elements.
- **Snapshots**: snapshot tests of the generated TypeScript and Python for one representative schema per declaration kind (`*.verified.ts`, `*.verified.py`).
- **Type checking**: the generated code for schemas covering every supported feature passes `tsc` with the options of TS-001 (with both `--import-extension` values), and `pyright` (strict) and `mypy --strict`, and the Python stubs match their modules by `stubtest` (PY-012). So does the code generated for the random schemas of QUA-001, which includes names from pools of TypeScript and Python keywords, built-ins and reserved names. Test code that relies on narrowing and exhaustiveness (`switch` over a union's `kind`, `match` with `assert_never`) is type-checked too.
- **Runtime behavior**: round trip, identity, value equality, writer checks and malformed-input tests in each language, mirroring the C# tests of QUA-001, run with Node's test runner and pytest.
- **Cross-language**: one set of test graphs, covering every field type, defaults, sharing and unions, is written by each of C#, TypeScript and Python and parsed by each of the three. The three writers' buffers are byte for byte identical (SER-005), including when the buffer grows at any allocation: the TypeScript and Python writers write each fixture starting from every capacity smaller than its size. Buffers built with the official `Google.FlatBuffers` builder are parsed correctly by TypeScript and Python. The AOT executable generates, in both targets, the fingerprint the source generator gives the runtime tests' schemas (SER-015).
- **Native AOT**: the TypeScript and Python tests run against code generated by the AOT-published executable, not the JIT build, so `build.py --check` proves the published binary works.
- **npm packages**: the packages are packed, installed from the tarballs into a test project, and tested there: finding the executable, the `bequadro` command, `generate` and its diagnostics, and the Vite plugin in a real build, a watch build and a dev server, including schema errors in the overlay and quick successive changes (CLI-009, CLI-010). The package for each platform is tested on that platform.
- **Supported versions**: the Python tests run on 3.12 and the newest Python release, and so do importing the code generated for every random schema, which catches what type checks can't, such as import cycles, and writing and parsing each of its tables. The Python tests fail if that code is missing, rather than passing with no cases. The TypeScript tests type-check with the newest TypeScript release and TypeScript 5.8, and run on Node's current LTS release.

Design decisions:

- Byte-identical writers make the cross-language test strong and cheap: one comparison checks the layout, the defaults and the sharing of all three writers at once.
