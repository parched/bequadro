# Incrementality and Performance Requirements

## INC-001 Incremental Generator

Acceptance criteria:

- The generator implements `IIncrementalGenerator`. It does not implement the legacy `ISourceGenerator`.
- Its pipeline uses only `AdditionalTextsProvider` (filtered to `.fbs`) and the global options of `AnalyzerConfigOptionsProvider` (the MSBuild properties of CON-006) as inputs. It never reads the `Compilation`, so editing C# source never re-runs schema parsing, binding or code emission.
- Every value that flows between pipeline stages has value equality: records, plus an equatable immutable array wrapper. No stage value holds a Roslyn `Compilation`, `ISymbol`, `SyntaxNode` or `AdditionalText`.

Acceptance tests (with `GeneratorDriver` step tracking enabled):

- Running the driver again after an unrelated C# change reports every generator output step as `Cached` or `Unchanged`.
- Changing one `.fbs` file re-parses only that file. All other files' parse steps report `Cached`.
- Changing a doc comment on one table regenerates only that table's source. The outputs for other declarations report `Cached` or `Unchanged`.
- Changing only comments or white space in a file leaves the fingerprint's step `Cached` or `Unchanged`, and its source the same. Changing a token regenerates it (SER-015). The other tests count only the declarations' sources, beside which a change to tokens always regenerates the fingerprint.
- Introducing an error in one table's field regenerates only that table's source, without the field (DIA-001). The other declarations are still generated, and report `Unchanged`.
- Changing an MSBuild property (CON-006) re-binds without re-parsing, and regenerates only the declarations whose code it changes. The others report `Unchanged`.
- Adding or removing `bequadro_value_equality` on a table regenerates only that table, not the tables and unions that refer to it.
- Adding a declaration after all the others, or removing the last one, leaves the other outputs `Unchanged`. Adding or removing one before others regenerates only the declarations after it. Either way, the generated sources are the same as a fresh run's.

Design decisions:

- Roslyn compares the declarations of one run with the previous run's by position, and has no way to compare them by key, so a declaration added or removed before others makes each later one compare with its former neighbor. Declarations are ordered by file path and then position (NAM-002), so a new declaration costs nothing extra when it comes last: at the end of the file whose path sorts last, or in a new file that sorts last.

## INC-002 No File System or Environment Access

Acceptance criteria:

- The generator doesn't use `System.IO.File`, `Directory`, environment variables or the network. Analyzer rule RS1035 is enforced as an error.
- Include resolution works purely on the paths and texts of the `AdditionalFiles`.

## INC-003 Generator Performance

Acceptance criteria:

- Generating code for a schema set of about 50 files and 400 declarations, the size of a large real-world schema set, takes under 1 second of generator time on a typical developer machine. This is measured with the driver in a test that fails above a deliberately generous 5 second threshold, to catch pathological regressions.

## INC-004 Runtime Performance

Acceptance criteria:

- Generated serialization and deserialization code uses no reflection, no LINQ, and no boxing of scalars or of structs that aren't union members.
- Scalars, and vectors of scalars, enums and structs, are read and written with span and `BinaryPrimitives`/`MemoryMarshal` operations, never byte by byte through general-purpose streams.
- Repeatedly writing graphs of similar size reuses the working buffer and lookup tables. After warm-up, a write allocates little beyond the returned array: within 64 KB for a 20,000-table graph.
- The runtime refuses to run on big-endian platforms: the first use throws `PlatformNotSupportedException`.

Design decisions:

- Supporting only little-endian lets vectors of scalars and structs be bulk-copied. Every platform .NET 11 supports in practice is little-endian.
