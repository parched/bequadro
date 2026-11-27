# Architecture

How Bequadro is built. What it does is specified in [REQUIREMENTS.md](REQUIREMENTS.md).

## Layout

```
src/Bequadro/                 Runtime (net11.0), packed as the Bequadro NuGet package
  build/Bequadro.props        Makes the MSBuild properties visible to the generator (packed into build/ and buildTransitive/)
  FlatBufferSerializer.cs     Public entry point: Parse / WriteToArray / WriteToMemory / Write
  UnionNone.cs, UnionUnknown.cs   Public case types for a union's NONE value and unknown members
  Internal/                   Public-but-hidden API that generated code calls
    IFlatBufferTable/Struct/Union.cs   Static abstract interfaces implemented by generated types
    FlatBufferWriter.cs       Back-to-front buffer builder with table and vtable deduplication
    FlatBufferReader.cs       Bounds-checked reader with a per-parse table identity map
    WriterCache.cs            One reusable writer per thread
    ReaderCache.cs            One reusable set of reader lookups per thread
    StringLookup.cs           Finds the string a parse already read from a buffer position
src/Bequadro.Schema/          The front end shared by every target (netstandard2.0, no Roslyn)
  Syntax/                     Lexer, Parser, syntax records
  Semantics/                  Binder (resolution and validation) and declaration models
  Naming/                     ModelRenamer, NameScope and the NamingRules each target implements (NAM-002)
  Diagnostics/                Diagnostic IDs, titles and message formats
  Common/                     EquatableArray, SourceSpan, path helpers and SHA-256
  SchemaOptions.cs            Settings for a whole schema set
  SchemaFingerprint.cs        The schema set's fingerprint, from its files' tokens (SER-015)
src/Bequadro.Generator/       Source generator (netstandard2.0), shipped in the package's analyzers/ folder
  BequadroGenerator.cs        The incremental pipeline
  GeneratorOptions.cs         Reads SchemaOptions from MSBuild properties
  Emit/                       One emitter per declaration kind
  Diagnostics/                Roslyn descriptors and locations for the schema diagnostics
  Common/                     CodeWriter, C# naming rules and helpers
tests/Bequadro.Generator.Tests/  In-memory generator tests (CSharpGeneratorDriver)
tests/Bequadro.Tests/            End-to-end tests over schemas compiled by the real generator
tests/PackageConsumer/          A consumer project that build.py builds against the packed package
src/Bequadro.Cli/, tests/Bequadro.Cli.Tests/, tests/typescript/, tests/python/   The CLI and its tests (see CLI)
npm/bequadro/, tests/npm/        The npm packages and the Vite plugin, and their tests (see npm Packages)
.github/workflows/build.yml      CI: checks on Linux, an npm package per platform on its own runner, publishing to nuget.org and npm on a release
```

## Generator Pipeline

```
AdditionalTexts (*.fbs)
  └─ Select: Parser.Parse(path, text)            → ParsedFile          (per file, cached by text)
       ├─ Collect
       │    └─ Combine with options               ← AnalyzerConfigOptionsProvider
       │         │                                    └─ Select: GeneratorOptions.From(GlobalOptions)
       │         └─ Select: Binder.Bind, then CSharpModels.Prepare → SchemaSetResult (diagnostics + DeclarationModels)
       │              ├─ RegisterSourceOutput: report diagnostics
       │              └─ SelectMany: models       → DeclarationModel    (per declaration)
       │                   └─ RegisterSourceOutput: Emitter.Emit(model) → "{FullName}.g.cs"
       └─ Select: Digest, then Collect, then SchemaFingerprint.Combine → fingerprint
            └─ RegisterSourceOutput: FingerprintEmitter.Emit → "BequadroSchema.g.cs"
```

- **Parsing is per file**, so editing one schema only re-parses that file.
- **Binding needs the whole schema set**: includes, cross-file name resolution and duplicate detection. Binding is cheap (milliseconds for 400 declarations), so it simply re-runs whenever any file changes.
- **Emission is per declaration.** Roslyn compares each `DeclarationModel` with the previous run's value at the same position and only re-emits the ones that changed. To make that effective, declaration models deliberately contain no source locations. Otherwise, adding a line at the top of a file would change every model after it. A referenced type appears in a model only as the minimal `TypeModel` (its kind and name, a struct's size and alignment, and whether a union allows `NONE` and a table has value equality, which the TypeScript and Python emitters use), so editing a referenced table's fields doesn't regenerate the tables that reference it. `CSharpModels.Prepare` also clears what C# doesn't use from it, such as whether a referenced table has value equality.
- **Adding or removing a declaration** re-emits the declarations after it in the schema set's order, because `SelectMany` items are compared by position, so each of them is compared with its neighbor. Roslyn has no keyed alternative. Edits, and declarations added after all the others, re-emit only what changed.
- **MSBuild properties** (CON-006) reach the generator as `build_property.*` global options, because the package's `build/Bequadro.props` lists them as `CompilerVisibleProperty` items. `GeneratorOptions` reads them into a record, so an unrelated change to the options provider is cached at that step. The options go into binding, not emission, so a declaration whose model they don't change keeps an unchanged model and isn't regenerated.
- **The fingerprint** (SER-015) comes from the parse step's digests, not from binding, so an edit that changes no token, such as to a comment, leaves it cached, and the options can't change it. `Parser.Parse` hashes each file's token list (`SchemaFingerprint.OfFile`), and `SchemaFingerprint.Combine` hashes the sorted digests. The CLI calls the same two methods, so every target gets the same value. SHA-256 is implemented in `Common/Sha256.cs`: .NET's uses OpenSSL on Linux, which the Native AOT CLI would then need at run time. `CSharpNamingRules` fixes `BequadroSchema` in the root namespace's scope, and `CSharpModels.Prepare` fixes it among file names, so no declaration takes the class's name or its file's.
- **Errors don't stop generation.** The binder leaves out of the models whatever is in error, and whatever refers to it, so the generated code always compiles (DIA-001). A declaration with an error elsewhere in the set keeps an unchanged model, so an edit that breaks one declaration doesn't regenerate the others.

Every pipeline value is a `record` built from strings, numbers, nested records and `EquatableArray<T>`, so value equality comes for free. Diagnostics carry a `SourceSpan` (path, offset and line/column positions), which becomes a Roslyn `Location` only when reported.

## Parser

- **Lexer** (`Syntax/Lexer.cs`): a hand-written scanner that accepts what `flatc`'s `Parser::Next` does, or less (SCH-001). It attaches each run of `///` lines to the next token, so the parser can pick up documentation from the first token of a declaration, field or member without tracking trivia. Any other comment clears the pending run. It tracks whether a line feed came since the previous token, because `flatc` rejects a `///` comment that follows a token on its line.
- **Parser** (`Syntax/Parser.cs`): recursive descent over the token list.
  - Syntax errors are reported and then thrown as a private `SyntaxException`.
  - The exception is caught at two recovery points. Statement level skips to `;` or the closing `}`. Field level skips to `;`, but not past the enclosing `}`. This keeps one typo from hiding the rest of the file.
  - The parser tracks the current `namespace` statement and stamps it onto each declaration, so later stages never need statement order.
  - Qualified names are built by appending each segment to a `StringBuilder`, so parsing long names takes time proportional to their length.
  - The only recursion is in vector types, which stop at two levels (`[[T]]`, kept so the binder can report BQ2015). So no input can make the parser recurse deeply.
- **Signs**: a leading `+`/`-` is lexed as a separate token and combined with the following number or `inf`/`nan` in `ParseValue`, which requires the two to be adjacent, as `flatc` lexes them as one token. That keeps number lexing simple.
- **Attribute values** are checked in the parser, as `flatc` checks them while parsing: a string, or an integer that fits an `int`.

## Binder

`Semantics/Binder.cs` processes the whole set in these steps:

1. **Index files** by normalized path (`PathUtilities.Normalize`: forward slashes, with `.` and `..` resolved lexically). A second path that normalizes to the same one is reported (BQ2024) and ignored.
2. **Resolve includes** for each file, relative to the including file. An exact path match is tried first, then a unique case-insensitive one. Compute each file's transitive closure of includes, its *visible files*, and its *earlier files*: those `flatc` has read completely before it, whichever file it is given, which are the visible files reached without passing through a file in an include cycle with it. The attributes declared in the earlier files are available to the whole file; those of the file itself only after their declarations.
3. **Collect declarations** by fully qualified name, in the order of their files' normalized paths and then of their positions, which is the order renaming relies on (NAM-002), and index them by unqualified name too. Duplicates are reported. Each is wrapped in a `Declaration`, a class with reference equality, so that dictionaries keyed by declarations don't hash whole syntax trees.
4. **Bind each declaration**:
   - Type references resolve as `flatc`'s `ParseTypeIdent` does: the innermost candidate (SCH-012) that is an enum or union, else the innermost that is a table or struct. `NameCandidates` finds it among the declarations with the name's last segment, comparing their full names with the namespace in place, so a namespace with very many segments doesn't make it build a string per segment. The target must be declared in a visible file.
   - Then the reference is checked against what `flatc` would find at that point in the file (BQ2022). `FlatcLookup` replays, once per file and from top to bottom, the lookups `flatc` makes while reading it: `LookupEnum`, `LookupCreateStruct` with its forward references and how `StartStruct` takes them over, the field-name lookup in `ParseField` (BQ2023) and `SetRootType`. It sees the file's own declarations as it reaches them and those of its earlier files. A forward reference resolved to the right declaration is accepted; otherwise the binder reports what `flatc` would do instead.
   - What is in error is left out rather than failing the declaration: a field whose type doesn't resolve or whose name repeats another's, an enum value C# would reject, a union member in error. A reference to a declaration that won't be generated (an enum without a valid underlying type, a struct without fields, a union without members) resolves to nothing, without another diagnostic, so it is left out too.
   - Struct layouts, enum values and unions are computed lazily and memoized, so each is validated, and each of its diagnostics reported, exactly once, however many fields refer to it. A struct's nested structs are laid out first with an explicit stack, not recursion, so a long chain of nested structs can't overflow the stack (DIA-001). Struct recursion is caught with an in-progress set. A struct's duplicate field names are checked while laying it out, so its layout only contains fields that are generated.
   - Defaults become typed `ConstantModel`s here, while the binder still has the enum and scalar information. Each emitter formats them for its language. `Constants` converts floating-point literals to the nearest value of the field's type with `BigInteger` arithmetic, and checks that the C# literals it formats round-trip, because the generator also runs on .NET Framework, whose `double.Parse` and `"R"` format don't always round correctly (SCH-010).
   - A table field has two kinds of required: `IsRequired`, from the schema, which the writer and reader enforce, and `IsRequiredMember`, which makes the C# property `required`. The second also covers scalars and enums without a schema default when `BequadroRequireScalarsWithoutDefault` is set, which only the property declaration uses, so serialization doesn't change.
5. **Validate `root_type` statements.** They have no other effect.

Integer constants are held as `decimal`, which represents every `long` and `ulong` value exactly and keeps range checks simple.

Models come out of the binder with schema names, and nothing in them is specific to a target.

## Renaming

`Naming/ModelRenamer.cs` renames a schema set's models for one target (NAM-002). Each target supplies a `NamingRules`: which names it can't use, its fixed member names, its case conversion for fields, any names it derives, and whether namespaces are files and share a scope with declarations. The renamer:

1. builds a tree of namespaces from the models, in which each namespace keeps its nested namespaces and declarations in order of first appearance;
2. walks the tree from the root with an explicit stack, claiming names in a `NameScope` per namespace, so each namespace's target name is known before its children are named. A namespace's target name is a `TargetNamespace`, a chain of segments that becomes a string only for namespaces with declarations, so a namespace with very many segments takes neither deep recursion nor time quadratic in its depth (DIA-001);
3. renames enum members, so it can rewrite enum defaults that refer to them;
4. rewrites every model: its namespace and name, the full names in its `TypeModel` references, and its members, in a `NameScope` per type that starts with the target's fixed member names.

`NameScope.Claim` adds a leading `_` to a candidate starting with a digit, then trailing `_`s until the name is usable and free. Names claimed as files are compared ignoring case too. Each renamed element records its schema name in `RenamedFrom`, which emitters put in its documentation: a declaration in a renamed namespace records its full schema name.

The renamer reports nothing: names are never errors (NAM-001). It runs on the whole set, after binding, because a reference's target name depends on the declaration it refers to. A model only changes when a name it contains changes, so incrementality is unaffected. For C#, `CSharpModels.Prepare` runs it with `CSharpNamingRules`, then gives each model a file name that is unique ignoring case. `CSharpNamingRules` derives the reserved names from what each kind of type generates, adding each property's accessor names and each operator's method name, plus what every type inherits from `object`. One C# collision depends on two final names, a union label `get_L` beside a label `L`, so `Prepare` resolves it after renaming (`SeparateLabelGetters`).

## Emitters

Each emitter writes C# text through `CodeWriter`.

- **No `SyntaxFactory`**: plain text is faster, and readable in snapshots.
- **Fully qualified names**: all type references are `global::`-qualified.
- **Serialization is inline**: each generated type implements the runtime's static abstract interfaces with *explicit* implementations. The serialization members stay hidden from IntelliSense, but generic runtime helpers (`WriteTable<T>`, `ReadTableVector<T>`, ...) can still call them.
- **Tables**:
  - `Write` serializes every out-of-line child first, in declaration order, whatever its kind (SER-014).
  - It then starts the table and adds inline fields and child offsets in descending alignment order, which minimizes padding. A union field is added in two parts: its discriminator byte sorts with the 1-byte fields, and its offset with the 4-byte ones.
  - Default-valued scalars are skipped.
  - `Read` looks up each slot and assigns the field.
  - Reading uses a private `[SetsRequiredMembers]` constructor selected by the `DeserializationConstructor` marker. Required properties can then be assigned by `Read` instead of an object initializer, and property initializers still apply the schema defaults for absent fields.
  - With `bequadro_value_equality`, `Equals` compares vectors through `VectorEquality` and every other field with `EqualityComparer<T>.Default`. That covers optional scalars, nullable structs and unions, and makes a table field use the referenced table's own `Equals`, so the emitter needs no per-type cases. `GetHashCode` hashes the same values. The writer's identity map uses `ReferenceEqualityComparer`, so value equality never changes which tables are shared.
- **Structs**:
  - Fields are written at their computed offsets with `BinaryPrimitives` (little-endian), so the C# layout doesn't matter: structs are `[StructLayout(LayoutKind.Auto)]`, which lets consumers add fields in a partial declaration without warning CS0282.
  - The runtime hands `Write` a zeroed span, so padding is always zero.
  - Nested structs go through `StructHelpers`, because explicit interface members can only be reached through a generic constraint.
- **Unions**:
  - Each union is a `[Union]` struct implementing `IUnion`, so the C# compiler treats it as a union: its cases are the parameter types of its single-parameter public constructors, and pattern matching calls the matching `TryGetValue(out T)` overload. That path never touches `Value`, so struct members aren't boxed.
  - Storage is a `byte _kind` (the discriminator), one `object? _reference` shared by all table members, and a typed `_value{discriminator}` field per struct member. With two or more struct members, the struct gets `[StructLayout(Explicit)]`: the reference at 0, the discriminator after it, and the struct fields overlapping last, from the largest schema alignment of the struct members. They come last because a consumer can add fields to a struct, so its size in memory may be larger than its schema size. This is safe because `IFlatBufferStruct<T>` requires `T : unmanaged`. With fewer, the layout stays sequential, which keeps the struct eligible for JIT promotion.
  - Table members are read back with a checked cast, not `Unsafe.As`, so a torn read from a data race can't turn into type confusion.
  - `NONE` and unknown members are the case types `UnionNone` and `UnionUnknown` from the runtime. An unknown member keeps its discriminator in `_kind`.
  - Generated `Write` and `Read` switch on the discriminator byte. `Write` returns `(0, 0)` for an allowed `NONE`, which tables treat as absent and union vectors write as a 0 uoffset.
  - Generated table code tests `Nullable<union>` fields with `HasValue`, not a pattern such as `is { }`, because patterns on a union type follow the union's cases.
  - Equality switches on `_kind`: table members use `object.Equals`, which is by value for tables with `bequadro_value_equality`, and struct members their generated `Equals`.

## Runtime

### Writer

`FlatBufferWriter` builds the buffer from the end towards the front, like the official builders. Every position is held as an offset from the end of the buffer. These offsets stay valid when the buffer is reallocated, because growing copies the data to the end of the larger array.

- **Alignment**:
  - `Prep(alignment, byteCount)` pads so that once `byteCount` more bytes are written, the offset is a multiple of `alignment`.
  - Because the final buffer size is padded to the largest alignment used (`_minAlign`) in `Finish`, an offset that is a multiple of `a` is also an `a`-aligned absolute position.
  - Vectors are prepared with `max(4, elementAlignment)`, so the length prefix and the elements are both aligned.
- **Tables**:
  - `StartTable` records the current offset.
  - `AddScalar`, `AddStruct` and `AddOffset` record each field's offset in a slot array.
  - `EndTable` writes the soffset placeholder, builds the vtable on the stack, and looks for an identical vtable already written: a hash lookup, then a byte comparison. Before hashing, it compares with the vtable the last table with as many slots used in this write, kept in an array indexed by slot count, as tables of one type usually have the same vtable. The slot count stands in for the type: reading a static of a per-type generic class from `EndTable<T>`, which is shared generic code, took most of what the cache saved. A match there is the same vtable the lookup would find, because identical vtables are only ever written once. The array is cleared after every write, as an offset from an earlier write could point at stale bytes in the reused pooled buffer. It then patches the soffset, which is negative when it points at an earlier (higher-address) vtable.
- **Identity**:
  - `WriteTable<T>` keeps a `Dictionary<object, int>` with reference equality, from each table instance to its offset.
  - A sentinel value of `-1` marks a table whose write is in progress. Meeting that sentinel again means the graph has a cycle.
  - With `shareStrings`, `WriteString` also looks each string up in a `Dictionary<string, int>` (ordinal) from string to offset. Otherwise it skips the lookup, so the option costs nothing when off.
- **Depth**: a counter limits table nesting to the reader's 64 levels (`FlatBufferReader.MaxDepth`). Both count only a table's first visit, and visit fields in the same order, so any graph that can be written can be parsed.
- **Reuse**:
  - `WriterCache` keeps one writer per thread, so its lookup dictionaries are reused. They are emptied after every write, even one that throws, so they don't keep the written graph alive, and trimmed when they grew beyond 65,536 entries. The next write sizes the table map for as many tables as the last one wrote, so a large graph allocates its map once instead of rehashing it through several resizes. The identity maps are strong dictionaries, not weak tables: during a write everything in them is reachable from the graph anyway, so emptying them is what releases it (SER-013).
  - The byte buffer is rented from `ArrayPool<byte>.Shared` when a write starts, re-rented at double the size when it grows (capped at `Array.MaxLength`), and returned when the write ends. Large buffers are therefore shared through the pool, which trims under memory pressure, instead of being pinned to a thread.
  - Each `FlatBufferSerializer<T>` remembers the size of its last buffer and rents that much up front. Repeatedly writing similar large graphs doesn't grow from a small buffer every time.
  - The finished bytes are copied out (to an exactly sized array, or into the caller's `IBufferWriter`) before the buffer is returned, so nothing returned to callers aliases pooled memory.

### Reader

`FlatBufferReader` is a `ref struct` over a `ReadOnlySpan<byte>`.

- **Bounds checking**: every access is checked first, and failures throw `InvalidDataException`. Arithmetic that could overflow uses `long`.
- **Identity**:
  - `ReadTable<T>` keeps a `Dictionary<int, object>` from table position to instance.
  - The instance is added *before* its fields are read, which guarantees that malformed self-referencing offsets terminate. Finding and adding are one lookup, through `CollectionsMarshal.GetValueRefOrAddDefault`.
  - If the same position is read as two different types, the buffer is malformed.
  - With `shareStrings`, `ReadString` likewise returns one instance per string position. Otherwise the reader has no `StringLookup` and decodes every reference. Even buffers written with sharing often refer to most strings only once, and a dictionary insert per string made parses of many small strings up to 57% slower. So `StringLookup` at first only marks positions in a bitmap, one bit per 4 bytes, and keeps the strings in a list. When a marked position is read again, it indexes the list in a dictionary, which serves the rest of the parse.
  - `ReaderCache` keeps one set of these lookups per thread. They are emptied after every parse, so they don't keep the parsed graph alive, and trimmed when they grew beyond 65,536 entries. As in the writer, the next parse sizes the table map for as many tables as the last one read.
- **Depth**: a depth counter limits table nesting to 64 levels.
- **Bulk reads**: vectors of scalars use `MemoryMarshal.Cast`, then `ToArray`. This depends on little-endian, which is checked once per call in `FlatBufferSerializer`.

## CLI

`src/Bequadro.Cli` is the `bequadro` executable (CLI-001), published with Native AOT. It references `Bequadro.Schema`, and doesn't use Roslyn, which isn't AOT-friendly and isn't needed: it reports diagnostics as text.

```
src/Bequadro.Cli/
  CommandLine.cs              Argument parsing and exit codes (CLI-002, CLI-004)
  SchemaSet.cs                Collects the input files, parses and binds them, formats diagnostics (CLI-003)
  Generator.cs                Runs `generate`: diagnostics, then the target, then the output directory
  OutputDirectory.cs          Writes, compares and deletes generated files (CLI-005)
  FileErrors.cs               Messages for file errors, made from the exception's type and the path (CLI-004)
  Runtime.cs, Runtime/        The runtimes, embedded as resources and written with the output (CLI-006)
  TypeScript/                 Naming rules, the module emitter, and module paths and imports
  Python/                     Naming rules (with the list of Python's built-ins), the module emitter, package layout
tests/Bequadro.Cli.Tests/     In-process CLI tests, naming tests and snapshots (*.verified.ts, *.verified.py)
tests/typescript/             Type checks and node:test tests of generated TypeScript; package.json pins TypeScript
tests/python/                 pytest tests and pyright/mypy/stubtest checks of generated Python; pyproject.toml pins the tools
```

- **Pipeline.** `SchemaSet` parses every input under its full path, so includes resolve the same however the inputs were written, and maps paths back to how the inputs gave them for diagnostics, including paths in their messages. It groups the paths that reach a file by the file's real path, with every symbolic link resolved, so a file is parsed once, and stops walking a directory link that leads back into the walk. A `StreamReader` reads each schema as UTF-8 with encoding detection disabled. The file handle shares write and delete access so editors can save while it is open. With errors, the run stops before generating anything. Otherwise the target renames the models with its `NamingRules` and emits files, and `OutputDirectory` applies them.
- **Output directory.** A file is generated if its first line is exactly the generated comment. Every planned path, and every directory on the way to it, is checked before anything is written: a planned file is read itself to decide whether it is generated, rather than looked up in a listing of the directory, which a concurrent run renaming the file can make miss it. Planned files are compared byte for byte with what is on disk, and only changed ones are written, through a hidden temporary file and a rename. The sweep for stale files walks the directory by hand, skipping symbolic links and names that start with `.`, so it never leaves the output directory or deletes another run's temporary file. Generated files that weren't planned are deleted, along with directories that become empty.
- **File errors.** The executable is built with `UseSystemResourceKeys`, so exception messages are resource keys. Every file operation runs through `FileErrors.Guard`, which reports a failure as "Can't {read, write, ...} '{path}': {reason}", with the reason taken from the exception's type, or from its HResult (errno on Unix) for a plain `IOException`.
- **Output encoding.** `Program` writes redirected standard output and error as UTF-8 on every platform. It doesn't set `Console.OutputEncoding`, which on Windows would change the code page of the console it shares with its parent.
- **TypeScript.** One `TypeScriptModuleEmitter` per namespace module. It first collects the namespaces the module refers to, and gives them the aliases `$n0`, `$n1`, ... in ordinal order, then emits the body, noting which imports are used as values, and writes `import type` for the others. Generated code names everything that isn't a schema name with a `$`, which schema identifiers can't contain: `$bq` for the runtime, `$c_Name` for codecs, `$bq.Bytes` and `$bq.View` for `Uint8Array` and `DataView`, and its own parameters and locals (`$init`, `$a`, `$value`, `$writer`, `$o`, `$o2`, `$u3`). Modules only use their imports inside functions, so import cycles between namespaces are harmless.
- **Fingerprint modules.** Each target writes its fingerprint module (`bequadro_schema.ts`, `bequadro_schema.py`) from `SchemaSet.Fingerprint`, which is null when the inputs contribute no file, and fixes the module's name among the root names its `NamingRules` claim first, beside the runtime's.
- **Python.** One `PythonModuleEmitter` per namespace package. A module defines its enums first, then imports the other namespaces it refers to as `_bq_n0`, `_bq_n1`, ..., then defines structs and tables, then unions, and then, after a comment, the codecs of all of them. That order makes import cycles between namespaces work: the module imported first already has its enums, which class bodies use as defaults, and everything else is only used inside functions or in annotations, which `from __future__ import annotations` and `type` statements leave unevaluated. Unions come after the classes because mypy resolves a name in a `type` statement that comes before its class to a built-in of the same name. The same emitter writes the stub (PY-012), with `stub: true`: it leaves out `from __future__ import annotations`, writes `__eq__` without its body, and declares the type of the module's `__bequadro__` dictionary in place of the codecs, which it doesn't generate. The root namespace's declarations are in `bequadro_root.py`, because a subpackage can't import its parent package's `__init__.py` relatively. A struct's scalar members are read with one `unpack_from` and written with one `pack_into` of a `struct.Struct` made at import (`_bq_layout_Vec3_0_0`), with `x` for padding. Nested structs split the members into runs, one layout each, and are written between runs by their own codecs, so members are still checked in field order and a value with several bad members reports the same one. Generated code reaches table and struct codecs through each class's `__bequadro__` attribute, and union codecs through each module's `__bequadro__` dictionary, because pyright treats other names that start with an underscore as private to their module. Internal names start with `_bq` and never end with `_`, and renaming keeps declarations from doing the same (PY-003). That includes every parameter and local of generated functions (`_bq_w`, `_bq_p`, `_bq_self`), so no declaration can shadow one or be shadowed by one, however generated code changes; `PythonTests` checks every generated function for it.
- **Python enum annotations.** Generated member names that start with an underscore and end with at least two use `ClassVar[Self]` (PY-006). The emitter imports `typing` under a name available in every enum body.
- **Codecs.** Each table, struct and union gets a codec object, after all the declarations and their companions, so that the start of a module is its API: `write` and `empty`/`fill` for tables, `write`/`read` on a `DataView` for structs, `write`/`read` for unions. The runtime calls these, so it never inspects property names. Parsing creates a table with `empty()`, which assigns every property in declaration order, registers it for identity, then fills it, so every parsed object of a table shares one hidden class.
- **Stack use in TypeScript.** Writing and parsing recurse once per level of table nesting, so a frame that grew with the table's width would make wide tables overflow the stack within the 64-level limit. A table codec's `write` passes each field straight to a runtime call that treats `undefined` as absent (`writeString`, `addScalarField`, `addStruct`, `addOffset`, ...), and keeps only its out-of-line children's offsets until it starts the table: in a local each (`$o2`, or `$u3` for a union's pair of offsets) when it has up to 64 of them (`MaxChildLocals`), which made writing about 12% faster, and in the array `$o`, indexed by slot, with `$bq.pair` for a union, when it has more. So its frame is bounded however wide the table is. `fill` reuses one local, `$p`. The runtime functions on the recursion path (`writeTable`, `writeUnionVector`, `readTableVector`, `readUnionVector`) keep their loops free of destructuring, which needs many registers. In a fresh Node process, where frames are unoptimized, a level costs about 350 bytes through a table field and 700 through a vector of unions, of the default 984 KB stack.

### Runtimes

- **One layout algorithm.** The TypeScript writer (`Runtime/bequadro.ts`) is a port of `FlatBufferWriter`: back-to-front, the same `Prep` alignment rule, children written in declaration order, fields added in descending alignment order and then by slot, and the same vtable deduplication. `endTable` builds the vtable's uint16s in one reused array and hashes them, then compares them with the bytes of each earlier vtable with that hash, in the order they were written, so it finds the same first identical vtable as C#'s hash-and-compare without allocating. So its bytes are identical to C#'s (SER-005).
- **Table identity when parsing TypeScript.** `Reader` keeps the tables it has read in two parallel arrays, of values and codecs, and finds a table's index in them by its position: in the `Map` `#tablePositions` until it has read `max(4096, length / 1024)` tables, then, for positions that are multiples of 4, in `#tableIndex`, an `Int32Array` holding the index plus 1 at position / 4. `#makeTableIndex` moves the aligned entries from the `Map` into the new index. Tables at other positions stay in the `Map`.
- **Strings in TypeScript.** `Writer.#writeNewString` copies ASCII strings of up to 12 characters with a `charCodeAt` loop, and encodes others with `encodeInto` into the writer's scratch array (`#encode`), which grows to three bytes per UTF-16 code unit, then copies them into the buffer. Strings over 65,536 characters use `encode`. The bytes are the same on every path.
- **Paths in Python messages.** As in TypeScript, the writer formats a path only when it raises: `check` and the vector writers take an element's index, and `_write_struct` writes a struct with `""` as its name, which `"" + ".x"` returns unchanged in CPython, then writes it again with the real path on a `FlatBufferWriteError`. A scalar vector is packed by `struct` in one call when every element's type is one that `struct` converts exactly as `check` does (`_packs_as_checked`), so `struct` checks the ranges in C. If it raises, or a type doesn't qualify, `check` runs on each element, giving the same message, or making a float too large for 32 bits infinite.
- **Paths in TypeScript messages.** The writer builds a field's path for a message (`vectors[1].x`) only when it throws. `check` takes a vector element's index as a number. Struct codecs join `$field` with each member's name, and `writeStructAt` calls them with `""` as `$field`, so the join doesn't allocate, then on a `FlatBufferWriteError` writes the same struct again with the real path to throw the full message. `writeNestedStruct` takes the member separately and passes `""` on while the outer path is `""`, so nested members cost nothing either.
- **TypeScript**: the writer grows a `Uint8Array` from the end and writes through a `DataView` with explicit little-endian flags, so it works on any platform. Memory is fresh, so padding is zero without clearing. The writer's identity map is a `Map` from each object to its index in two parallel arrays, of codecs and offsets, which is cheaper than an object per table. The codec catches one object written as two tables, and an offset still `inProgress` catches a cycle. The slots of the table being written are a reused `Int32Array`. Scalars are checked and normalized by `check` (TS-011): integers for range, floats rounded with `Math.fround` for 32-bit fields.
- **Growing in TypeScript and Python.** Growing replaces the array (and in TypeScript the `DataView`), so the writer reads it only after `allocate` returns, never in the same expression: both languages evaluate `this.#buffer` in `this.#buffer.set(x, this.#allocate(n))` before the call, and would write into the old array. C#'s `Allocate` returns a `Span` over the new array, so it can't make this mistake. The writers take an initial capacity, which only the tests set.
- **Python** (`Runtime/bequadro.py`): the writer builds into a `bytearray` from the end, with precompiled `struct.Struct` objects, and is the same port of the C# writer. A Python call costs more than most of what the runtime's helpers do, so padding and reserving bytes are one helper, `_reserve`, which `add_offset` and `end_table` inline, offset vectors are packed in one call, and `Reader.field` and `Reader.read_table` inline their bounds checks. `field` doesn't check the table's position again, because its callers, the generated fill functions, only get positions `read_table` has checked. Identity maps are keyed by `id(obj)` and keep a reference to the object, so ids can't be reused during the call. They can't be keyed by the object itself, because value-equality tables are unhashable (PY-010). Parsing creates a table with `object.__new__`, registers it for identity, then lets the generated fill function assign every field. Each level of nesting takes up to four Python frames (through a vector of unions), so `write` and `parse` count the frames already on the stack and raise the recursion limit to that plus four per level plus a margin, if it is lower; they never lower it, so concurrent calls can't interfere. `parse` and `write_bytes` hold their `memoryview`s in `with` blocks, so no export of the caller's buffer outlives the call, even in a traceback; a buffer that can't be cast to bytes, such as a non-contiguous one, is copied. Strings with surrogates are encoded with an error handler that combines surrogate pairs and writes U+FFFD for the rest, as .NET does for the same UTF-16 string. Generated enums derive from the runtime's `IntEnum`, whose `_missing_` keeps values the schema doesn't define, named by their value. Value equality uses runtime helpers: `equal_float32s` rounds to 32 bits, `equal_vectors` compares any sequences element by element, and `equal_bytes` compares bytes-like objects by their bytes.

### Cross-Language Tests

The C# tests produce what the other languages' tests consume, under `artifacts/`, so `build.py --check` runs them first:

- `CrossLanguageFixtureTests` writes buffers to `artifacts/cross-language/`, with a manifest. For each, the other languages parse the input and write it back, and must produce exactly the expected bytes: C#'s own bytes, or what C# writes for a buffer from the official builder. That one comparison checks their readers, identity handling and layout. Most fixtures are smaller than the 1024 bytes the writers start with, so each is also written starting from every smaller capacity, which makes every allocation in turn the one that grows the buffer. Parse fixtures, such as unions with unknown discriminators, are checked by name.
- `RandomSchemaTests` writes its schemas to `artifacts/random-schemas/`. `build.py` generates code for each with the AOT executable and type-checks the result, so the published binary, not the JIT build, is what's tested.
- `CrossLanguageFixtureTests` also writes the fingerprint the source generator gave the runtime tests' schemas to `artifacts/cross-language/fingerprint.txt`. `build.py` (`check_fingerprint`) generates both targets from the same directory with the AOT executable and compares their fingerprint modules with it, which checks that MSBuild's and the CLI's ways of reading the files give the same tokens.

## npm Packages

`npm/bequadro/` is the source of the `bequadro` npm package (CLI-009), in TypeScript, compiled to `dist/` with `tsc`:

```
npm/bequadro/src/
  index.ts                    binaryPath, generate and the diagnostic parser
  cli.ts                      The bequadro command: runs the executable with the same arguments and exit code
  vite.ts                     The Vite plugin (CLI-010)
tests/npm/                    A consumer project that installs the packed tarballs and tests them with node:test
```

- **Packing.** `build.py` (`pack_npm`) builds the platform package for the host from the AOT executable and a generated `package.json` (with `libc` for Linux, after checking that the executable needs no newer glibc than the documented minimum), then the main package from `dist/`, `README.md` and `package.json`, with the version `build.py` builds (the release tag's, CON-007, or `0.0.0-unreleased`) and exact-version `optionalDependencies` on every platform package. The source `package.json` has neither, so developing the package doesn't look up unpublished packages. `NPM_PLATFORMS` in `build.py` and `platformPackages` in `index.ts` list the platforms.
- **Testing.** `check_npm` installs the tarballs into `tests/npm` without saving them, type-checks the tests against the installed declarations, and runs them. The tests run real Vite builds and dev servers (in middleware mode, recording what the plugin sends to the browser, or listening, with a WebSocket client as a page) on temporary projects, with the newest Vite and with Vite 7.2, installed as the alias `vite-7.2`. A CI job per other platform runs `build.py --check-npm` on that platform, which builds and tests its package the same way. The check job then runs `tests/npm/smoke.mjs`, plain JavaScript, on Node 20.19.
- **Plugin.** Runs are serialized through a promise chain: a change while a run is queued but not started joins it, and a change during a run queues one more. Chokidar, Vite's watcher, reports at most one change to a file per 50 ms and drops the rest, so in the dev server each run records the schema files' sizes and modification times before starting, and compares them 100 ms after it ends. Closing the server stops the rechecks, but only when the closing environment is the current server's: a restart configures the new server, with the same plugin instance, before it closes the old one. For builds, `buildStart` generates and then adds every schema file with `addWatchFile`, so `vite build --watch` rebuilds when one changes.
- **Release.** CI publishes only the packages the check jobs tested (`build.py --check` leaves them in `artifacts/`). `build.py` checks the release tag (`release_version`, tested by `tests/build`, which `--check` runs first with `unittest`). A release-only job runs `build.py --stage-release-cli` on the same npm artifacts to stage all six executables under `artifacts/release-cli/`. The job uses the GitHub Actions token to attach missing assets to the published release.

## Testing Strategy

- Test projects keep physical caller paths through `Directory.Build.targets`, so their tests can find and write files in the checkout during CI. Package projects use mapped source paths for reproducible publishing.
- **Generator tests** run the generator in memory (`GeneratorHarness`) against small schemas. The harness compiles the output as a consumer that treats warnings as errors would, at the highest warning level (a `net11.0` project uses level 11) and with documentation comments checked, so warning waves such as CS8981 and malformed doc comments fail tests. They cover:
  - parser and binder unit tests through `Parser.Parse`/`Binder.Bind`;
  - the diagnostic catalog, where a `«marker»` in the schema text gives the expected location, and a check that the catalog and requirements/diagnostics.md list the same IDs and titles;
  - agreement with `flatc` (`FlatcAgreementTests`): schemas `flatc` rejects or reads differently, each test naming the `idl_parser.cpp` function that decides it, since `flatc` itself isn't run;
  - floating-point constants (`ConstantsTests`), compared bit for bit with .NET's own parsing on random literals, which on .NET (unlike .NET Framework) rounds correctly;
  - deep and large input: long chains of structs, a namespace of 100,000 segments, and a large enum used by many fields bind without deep recursion and quickly;
  - snapshot tests of emitted source (`Snapshot` helper and `Snapshots/*.verified.cs`);
  - compilation of the generated code together with consumer partial types, and the compiler's exhaustiveness checks on union `switch`es. The test project uses a newer Roslyn package than the generator (which stays on the oldest version it supports), so these compilations understand C# unions;
  - generation in the presence of errors (`ErrorRecoveryTests`): exactly which types and members are still generated, and that the output compiles;
  - renaming (`NamingTests`): the C# names each colliding or reserved name gets, that the output compiles, the doc note on renamed elements, and that adding a field doesn't rename an existing one;
  - compilation of the generated code for hundreds of random schemas (`RandomSchemaTests`), which must compile without warnings whether or not the schema has errors. Their names are drawn from pools that collide with C# keywords, generated members and reserved namespaces, every other schema has a few characters deleted to cause syntax errors, and every third is generated with `BequadroRequireScalarsWithoutDefault`;
  - which AdditionalFiles are schemas (`ConsumptionTests`);
  - MSBuild properties, which the harness passes as `build_property.*` global options through an `AnalyzerConfigOptionsProvider`, as the compiler does;
  - pipeline step tracking for incrementality.
  - the fingerprint (`FingerprintTests`): its SHA-256 against .NET's, what changes it and what doesn't, and the generated class.
- **Runtime tests** use schemas in `tests/Bequadro.Tests/Schemas`, compiled by the real generator through an analyzer project reference, exactly as a consumer would. They cover:
  - round trips;
  - identity;
  - errors;
  - fuzzed malformed input;
  - FsCheck properties;
  - interoperability with the official `Google.FlatBuffers` runtime, both reading Bequadro's buffers with its `Table` accessors and parsing buffers built with its `FlatBufferBuilder`.
- **CLI tests** (`tests/Bequadro.Cli.Tests`) run `CommandLine.Run` in process against a temporary directory: arguments and exit codes, the diagnostic format, the output directory's rules, TypeScript and Python naming, and snapshots of generated TypeScript and Python (`*.verified.ts`, `*.verified.py`). The CLI-008 timing test instead runs the built executable as a process, so process start is included.
- **Python tests** (`tests/python`) work like the TypeScript ones: pytest on Python 3.12 and the newest release, then pyright (strict) and mypy (strict) over the generated code, the random schemas' code and the tests. mypy reads only the stub of a module that has one, so `build.py` also runs it over the generated code and its consumer tests in a copy without stubs, and runs `stubtest` over the stubs, comparing its concise diagnostics against the exact expected messages for each union and each dataclass with a field named self, and checks that each stub has the same unions as its module, which stubtest can't compare (PY-012). They also import every module generated for the random schemas, and write and parse every table in them, once with only the fields it requires and once with every field set (`test/values.py` builds the instances from the classes' annotations). `tests/python/schemas` holds schemas only the Python tests use: names that generated code or Python reserves, deep nesting through unions, and a vector of every scalar type.
- **TypeScript tests** (`tests/typescript`) run after the C# tests, with code the AOT executable generated from `tests/Bequadro.Tests/Schemas` and from their own schemas in `tests/typescript/schemas`: every TypeScript keyword and every name generated code uses for itself, as each kind of declaration and member, a 600-field table, and tables either side of the limit for locals, nested 64 levels deep in a fresh process, a vector of every scalar type, and two namespaces that import each other, each imported first in a fresh process. `tsc` checks it with TypeScript 7 and 5.8 and every TS-001 option, with both import extensions and for the random schemas, and `node --test` runs the cross-language fixtures and behavior tests written in TypeScript. Tests that only need to compile, such as exhaustive `switch`es and assigning parsed objects to input types, are checked by `tsc`.
- **The package test** in `build.py --check` packs the package and checks its contents (CON-001). It then copies `tests/PackageConsumer` out of the repository and builds and runs it against that package, with `AnalysisMode` `All`, `GenerateDocumentationFile` and warnings as errors, from a private package folder so it never uses a stale cached copy. The consumer sets `BequadroRequireScalarsWithoutDefault` and checks with reflection that it made the expected properties `required`, which proves that the package's props file passes properties to the generator.

## Design Decisions

- **The runtime is a separate assembly, not emitted source.** It keeps generated files small, and gives one implementation to optimize and test. It also avoids clashes between internal helper types in consumers that share `InternalsVisibleTo`.
- **Static abstract interfaces instead of reflection or registration.** Generic runtime code dispatches to the generated code with no lookups, and it works with trimming and AOT.
- **Back-to-front writing.** Children must be written before their parents, but nobody has to compute sizes in advance (which FlatSharp's `GetMaxSize` did). It is also the layout the official builders produce, so the output is familiar to anyone debugging buffers.
- **Greedy parsing only.** Parsed models are meant to be changed and to keep instance identity (SER-010, SER-011). Lazy objects backed by the buffer would complicate both.
