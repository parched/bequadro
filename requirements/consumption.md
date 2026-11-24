# Consumption Requirements

How Bequadro is packaged and how a consuming C# project supplies schemas. TypeScript and Python projects use the CLI instead ([cli.md](cli.md)).

## CON-001 Single NuGet Package

Bequadro ships as one NuGet package named `Bequadro` that contains both the runtime library and the source generator.

Acceptance criteria:

- The package contains `lib/net11.0/Bequadro.dll` (the runtime), `analyzers/dotnet/cs/Bequadro.Generator.dll` (the generator), and `build/Bequadro.props` and `buildTransitive/Bequadro.props`, which pass the MSBuild properties of CON-006 to the generator.
- Referencing the package with a plain `<PackageReference Include="Bequadro" />` enables the generator and references the runtime. No other package references or MSBuild imports are needed.
- The generator has no dependencies beyond the Roslyn assemblies the compiler provides. Nothing else ships in `analyzers/`.
- The package's license is MIT, as a license expression.

Design decisions:

- One package, not separate runtime and generator packages, so the two can never be version-skewed. The generated code targets exactly the runtime it shipped with.
- The CLI (CLI-001) isn't in the package. C# consumers don't need it, and TypeScript and Python consumers shouldn't need NuGet to get it.
- Shipping a runtime library, instead of emitting all helpers as source into every project, keeps the generated code small. It also gives the helpers one public home, and it avoids duplicate internal helper types when several projects that use Bequadro can see each other's internals.

## CON-002 Supported Consumers

Acceptance criteria:

- The runtime targets `net11.0` only.
- The generated code compiles with C# 11 or later, because it uses `required` members and static abstract interface members.
- Pattern matching on a union's cases (`switch`, `is`), with its exhaustiveness check, needs C# 15, which is the default for `net11.0`. Older language versions treat `[Union]` as an ordinary attribute, so unions still work through `Kind`, the member properties and `TryGetValue`.
- The generator targets `netstandard2.0` (a Roslyn requirement) and runs in the .NET 11 SDK compiler, which every consumer needs to build for `net11.0`.
- Only C# projects are supported.

Design decisions:

- Targeting `net11.0` only keeps the runtime simple and fast: it can use static abstract interface members, `ReferenceEqualityComparer`, modern span APIs, `CollectionsMarshal` and `MemoryMarshal`. All consumers target .NET 11.
- The generator is built against Roslyn 4.12, older than the .NET 11 SDK's compiler. It needs no newer API, and an older reference only widens the compilers that can load it. Compilers older than the .NET 11 SDK's aren't tested, so they aren't promised.

## CON-003 Schema Inputs Are AdditionalFiles

Schemas are supplied as MSBuild `AdditionalFiles` items whose path ends in `.fbs` (case-insensitive).

Acceptance criteria:

- `<AdditionalFiles Include="schema/**/*.fbs" />` in a project that references Bequadro generates C# for every declaration in those files.
- Files that live outside the project directory work too (for example `<AdditionalFiles Include="../Shared.Schema/**/*.fbs" />`).
- `AdditionalFiles` without the `.fbs` extension are ignored.
- A project with no `.fbs` files gets no generated source and no diagnostics.
- Leaving a file out of `AdditionalFiles` means no code is generated for its declarations. It also cannot be included by another file in the set (SCH-003).

Design decisions:

- `AdditionalFiles` is the standard, incremental-friendly way to feed non-C# inputs to a generator. Source generators must not read the file system directly (RS1035), so every file that is needed, includes included, must be an additional file.
- There is no custom MSBuild item type or per-file metadata. Every schema in the set is compiled, so there is nothing to configure per file. Settings for the whole project are MSBuild properties (CON-006).

## CON-004 All Files in the Schema Set Are Compiled Together

Acceptance criteria:

- C# is generated exactly once for each declaration in the schema set, whether a file is included by several other files or by none.
- Two files that declare the same fully qualified name produce BQ2002.

## CON-005 No External Tools

Acceptance criteria:

- Building a consuming project never starts a process. It needs no `flatc` and no network access.
- The generator runs entirely inside the compiler.

## CON-006 MSBuild Properties

A consuming project configures the generator with MSBuild properties. Each one applies to every schema in the project.

| Property | Effect |
| --- | --- |
| `BequadroRequireScalarsWithoutDefault` | When `true`, scalar and enum table fields without a default in the schema become `required` C# properties (API-005). |

Acceptance criteria:

- Setting a property in the project file, for example `<BequadroRequireScalarsWithoutDefault>true</BequadroRequireScalarsWithoutDefault>`, is enough. The package's `build/Bequadro.props`, also shipped as `buildTransitive/Bequadro.props`, declares each property as a `CompilerVisibleProperty`, and the generator reads it as the `build_property.<Name>` global option.
- A property is on only when its value is `true`, in any case and ignoring surrounding white space. Any other value, an empty one included, or leaving it unset, means off, the default.
- Changing a property regenerates only the declarations whose generated code it changes (INC-001).
- The CLI takes the same settings as command-line options (CLI-002).

Design decisions:

- MSBuild properties are the standard way to configure a source generator, and they need nothing beyond the package reference (CON-001).
- Reading only `true` as on matches how MSBuild conditions such as `'$(Property)' == 'true'` treat boolean properties. A diagnostic for other values would have no location in a `.fbs` file (DIA-001), and a misspelled property name couldn't be caught anyway.
- The props file also ships in `buildTransitive/`, because the generator runs in projects that get the package transitively too, and the properties should work the same there.

## CON-007 Publishing

Acceptance criteria:

- The package is published to nuget.org, with a symbol package (`.snupkg`), by CI when a GitHub release is published.
- The release's tag is the only source of a release's version. It must be `v<major>.<minor>.<patch>`, optionally followed by `-<prerelease>`, and every package of the release (this one, and the npm packages of CLI-009) gets the version it names. The version must be valid for both npm and NuGet: a semantic version without build metadata, whose numbers and numeric prerelease identifiers have no leading zeros, with numbers of at most 2147483647, at most 64 characters long, and not `0.0.0-unreleased`. A tag that doesn't name such a version fails CI before anything is built or published.
- Every other build, local or in CI, has the version `0.0.0-unreleased`, which sorts below every release. `build.py --release-tag v<version>` builds a release's version locally.
- The package names its repository and the commit it was built from, and its symbols use Source Link, so debuggers fetch the exact sources. CI builds are deterministic (`ContinuousIntegrationBuild`).
- `build.py --pack` produces the package and its symbol package in `artifacts/package/`, and checks both: the package's contents (CON-001), the repository and commit in its manifest, and that the symbol package's PDB uses Source Link for that commit. `build.py --check` does the same and leaves the packages it tested there, so CI publishes exactly what it tested.
- Publishing can be re-run after a partial failure: the NuGet push skips a package or symbol package that is already on nuget.org, and the npm job skips a package whose version is already on npm.
- Every action the workflow uses is pinned to a commit, with the release it is in a comment.

Design decisions:

- CI publishes with trusted publishing, to nuget.org and to npm: each job exchanges GitHub's OIDC token for a credential that lasts an hour or less, so no long-lived key is stored. npm signs the packages' provenance with the same token. npm can only set up trusted publishing for a package that exists, so the first release publishes with a token, which is then deleted (AGENTS.md).
- NuGet and npm publishing aren't gated on each other. Gating can't make a release atomic, because the second registry can still fail after the first has published, and a version can never be published twice. Instead, both jobs need every package built and tested first, and each is safe to re-run, so a partial release is finished by re-running the failed job.
- Actions are pinned to commits because a tag can be moved to other code, which would then run in a job that can publish.
- The version lives only in the tag, so the repository never has to be changed to release, and a release can't be tagged with one version and built with another.
- Publishing on a release, not on any pushed tag, makes publishing a deliberate step with its own notes, and a tag can be pushed without publishing.
- The package is built once, on Linux. It contains no native code, so one build serves every platform.
