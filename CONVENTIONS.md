# Conventions

## General

- Use US English spelling in code, comments and documentation.
- Use Title Case for headings.
- Format all C# with CSharpier (`dotnet csharpier format .`), using a print width of 120.
- Order members top-down: higher-level members come before the lower-level ones they call, and public before private. Ideally a file reads topologically, with each method appearing before the methods it calls.

## Naming

- Types, methods, properties and constants: `PascalCase`.
- Interfaces: `I` prefix plus `PascalCase`.
- Locals and parameters: `camelCase`.
- Private fields: `_camelCase`. Static fields don't use an `s_` prefix.
- Source files: `PascalCase.cs`, named after the primary type they contain, one primary type per file. Small records that only make sense together, such as the semantic models in `Models.cs`, the syntax nodes in `SyntaxNodes.cs` or the per-thread caches in `ReaderCache.cs`, may share a file named for what they have in common.
- Test classes are named after the subject plus a `Tests` suffix (`LexerTests`).
- Test methods follow `Subject_Conditions_Expectation`, for example `Parse_TableWithUnionField_AssignsTwoSlots`.

## Schema Front End (`src/Bequadro.Schema`)

- Target `netstandard2.0`, and don't reference Roslyn or add NuGet dependencies: the generator compiles these sources into the analyzer, and the CLI references the project. Use PolySharp for modern language features such as records, `init` and `required`.
- Don't rely on behavior that differs between .NET and .NET Framework, because the generator also runs in Visual Studio's .NET Framework compiler host. Floating-point parsing and formatting differ, for example, so `Semantics/Constants.cs` does without `double.Parse`.
- Results must be equatable, because they are pipeline values: use `record` types and `EquatableArray<T>` for collections, and a `SourceSpan` (path plus line and column positions) for a location. Inside the binder, key dictionaries by `Declaration`, which has reference equality, not by syntax records, whose hashes cover their whole trees.
- Report problems in schemas as `SchemaDiagnostic`s, never as exceptions. Exceptions are only for violated internal invariants (bugs). No input may make the front end recurse once per level of the input, so use explicit stacks for anything nested (DIA-001).
- Every diagnostic ID is declared once, as a constant in `Diagnostics/DiagnosticIds.cs`, with its title and message format in `Diagnostics/DiagnosticCatalog.cs`, and is documented in [requirements/diagnostics.md](requirements/diagnostics.md) under the same title. A removed ID stays in the table, marked removed, and is never reused.
- Where the front end follows `flatc`, the comment names the function in `idl_parser.cpp` it follows, and so does the requirement.

## Generator (`src/Bequadro.Generator`)

- Target `netstandard2.0`. Use PolySharp for modern language features such as records, `init` and `required`. Don't add NuGet dependencies, because anything referenced would have to ship inside the analyzer.
- Pipeline values must be equatable: use `record` types and `EquatableArray<T>` for collections. Never store `ISymbol`, `SyntaxNode`, `Compilation`, `AdditionalText` or `Location` in a pipeline value. Store a `SourceSpan` instead, and create Roslyn `Location`s only when reporting.
- Report problems in schemas as diagnostics, never as exceptions. Exceptions are only for violated internal invariants (bugs).
- Emit C# with the `CodeWriter` helper, not Roslyn `SyntaxFactory`. Emitted code should look hand-written: 4-space indentation, one member per line, and blank lines between members.
- Always emit type names fully qualified with `global::`.
- `Diagnostics/DiagnosticDescriptors.cs` makes a Roslyn descriptor for each entry of the schema front end's catalog. Don't declare diagnostics anywhere else.

## Runtime (`src/Bequadro`)

- Target `net11.0` only.
- Keep the public API minimal. Types that only generated code should use go in the `Bequadro.Internal` namespace and are marked `[EditorBrowsable(EditorBrowsableState.Never)]`. They have to be public so generated code in other assemblies can call them, but they are not a supported API.
- Hot paths (anything called per field or per element) must not allocate except for the values being produced, and must not use LINQ, reflection or boxing.
- Throw `InvalidDataException` for malformed input buffers and `InvalidOperationException` for object graphs that can't be serialized.

## CLI (`src/Bequadro.Cli`)

- Target `net11.0` with Native AOT. Don't add dependencies that aren't AOT-compatible, and keep the publish free of trimming and AOT warnings.
- Report usage errors and unreadable or unwritable files by throwing `UsageException`, which becomes exit code 2. Schema problems are diagnostics, never exceptions.
- Emit each target's code with `SourceWriter`. Generated code should look hand-written in its language: TypeScript with 2-space indentation.
- Don't cite requirement IDs in generated code or in the runtimes in `Runtime/`, which users read without the requirements (CLI-005).

## TypeScript (`src/Bequadro.Cli/Runtime/bequadro.ts`, `tests/typescript`)

- Use only erasable syntax and ECMAScript built-ins, as generated code does (TS-001). The runtime must compile under the options in `tests/typescript/tsconfig.json`.
- Use 2-space indentation, double quotes and semicolons.
- Write tests with `node:test` and `node:assert/strict`, in `tests/typescript/test/*.test.ts`. Tests that only need to compile, such as exhaustiveness checks, go in the same files: `tsc` checks them.
- Pin tool versions in `tests/typescript/package.json`, and commit `package-lock.json`.

## Python (`src/Bequadro.Cli/Runtime/bequadro.py`, `tests/python`)

- Target Python 3.12 and use only the standard library in the runtime. It must pass pyright (strict) and mypy (strict) with the settings in `tests/python/pyproject.toml`.
- Use 4-space indentation, double quotes, and lines of at most 120 characters. Generated code should look hand-written: PEP 8 blank lines and keyword arguments.
- Write tests with pytest, in `tests/python/test/test_*.py`, with type annotations, because the tests are type-checked too.
- Pin tool versions in `tests/python/pyproject.toml`, and commit `uv.lock`.

## Tests

- Use xUnit v3 and AwesomeAssertions.
- Use the `Snapshot` helper for snapshots of generated source. Snapshot files (`*.verified.cs`, `*.verified.ts`, `*.verified.py`) are committed, and every change to them is reviewed.
- Use FsCheck for property-based tests.
- `tests/Bequadro.Generator.Tests` tests the generator in memory through `CSharpGeneratorDriver`: parsing, binding, diagnostics, snapshots and incrementality.
- `tests/Bequadro.Tests` tests the runtime and the generated code end to end. Its schemas live in `tests/Bequadro.Tests/Schemas` and are compiled by the real generator. The TypeScript tests use the same schemas.
- `tests/Bequadro.Cli.Tests` tests the CLI in process through `CliHarness`, which gives each test a temporary directory.
