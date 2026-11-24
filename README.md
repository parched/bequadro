# Bequadro

Bequadro (♮) generates C#, TypeScript and Python from [FlatBuffers](https://flatbuffers.dev) schemas (`.fbs`). It is designed for applications that read or edit whole messages as mutable objects and exchange them in the standard FlatBuffers binary format.

## Why Bequadro?

- **C# types update in your IDE.** Add your `.fbs` files to the project and the [incremental source generator](#c) updates the C# types and reports schema errors as you edit. Generation runs as part of normal compilation.
- **Vite keeps TypeScript in sync.** The [Vite plugin](#vite-plugin) generates code before Vite loads your modules and regenerates it when schemas change. While you fix a schema error in the dev server, the overlay shows the error and the last good code stays usable.
- **Keep shared tables shared.** If two routes reference the same waypoint, Bequadro writes the waypoint once. After a round trip, both routes still reference one waypoint, so an edit through either route is visible through both.
- **Use types that fit your language.** The same schema gives you partial classes and pattern matching in [C#](#c), plain objects and discriminated unions in [TypeScript](#typescript), and typed dataclasses in [Python](#python).

### How It Compares

**`flatc --gen-object-api`** also gives you mutable objects. It generates separate object types alongside its buffer-backed accessors, so you can read selected fields directly or unpack a whole message. Its generated C#, TypeScript, and Python object APIs pack and unpack table references independently, so shared tables become separate objects. Bequadro preserves those references and exposes direct serialization APIs for every table. [FlatBuffers compiler docs](https://flatbuffers.dev/flatc/) · [C# generator](https://github.com/google/flatbuffers/blob/master/src/idl_gen_csharp.cpp#L1949) · [TypeScript generator](https://github.com/google/flatbuffers/blob/master/src/idl_gen_ts.cpp#L1265) · [Python generator](https://github.com/google/flatbuffers/blob/master/src/idl_gen_python.cpp#L2295)

`flatc` supports more languages and schema features, including explicit field IDs and file identifiers. You can use both tools with schemas that use features supported by both targets. [Bequadro schema support](#schema-support)

**[FlatSharp](https://github.com/jamescourtney/FlatSharp)** inspired Bequadro's C# API. FlatSharp generates C# at build time and offers several parsing modes, from lazy access to greedy parsing. Bequadro's Roslyn source generator runs inside the C# compiler, and its parser always loads the full graph into mutable objects that you can use after releasing the input buffer.

## Example schema

Save the following as `schemas/shapes.fbs` to follow the examples below.

```fbs
namespace Shapes;

struct Point { x: double; y: double; }

table Circle
{
    center: Point (required);
    radius: double = 1;
}

table Polygon { points: [Point] (required); }

union Shape { Circle, Polygon }

table Drawing
{
    title: string (required);
    shapes: [Shape];
}
```

## C#

The NuGet package includes the source generator and a dependency-free runtime. The generator reads `.fbs` files directly, so a C# project needs only the package and its schemas to generate code.

### Getting Started

Install the [Bequadro NuGet package](https://www.nuget.org/packages/Bequadro).

```sh
dotnet add package Bequadro
```

Then add your schemas as `AdditionalFiles`:

```xml
<ItemGroup>
  <AdditionalFiles Include="schemas/**/*.fbs" />
</ItemGroup>
```

Use the .NET 11 runtime and the .NET 11 SDK or later. Union pattern matching requires C# 15 or later, the default for `net11.0`.

The [example schema](#example-schema) generates the `Drawing` type and its serializer.

```csharp
using Bequadro;
using Shapes;

var drawing = new Drawing
{
    Title = "Example",
    Shapes = [new Circle { Center = new Point { X = 1, Y = 2 } }, new Polygon { Points = [] }],
};

byte[] bytes = Drawing.Serializer.WriteToArray(drawing);
Drawing parsed = Drawing.Serializer.Parse(bytes);

foreach (var shape in parsed.Shapes ?? [])
{
    var description = shape switch
    {
        Circle c => $"circle of radius {c.Radius}",
        Polygon p => $"polygon with {p.Points.Count} points",
        UnionNone => "nothing",
        UnionUnknown u => $"a shape added in a newer schema ({u.Discriminator})",
    };
}
```

### Serializing

Every table `T` has a static `T.Serializer` with these members. Generic code, which can't name `T.Serializer`, can use the same instance as `FlatBufferSerializer<T>.Instance`.

| Member | Description |
| --- | --- |
| `byte[] WriteToArray(T value, bool shareStrings = false)` | Serializes to a new array. |
| `ReadOnlyMemory<byte> WriteToMemory(T value, bool shareStrings = false)` | Serializes to new memory. |
| `void Write(IBufferWriter<byte> destination, T value, bool shareStrings = false)` | Appends the buffer to `destination`, for example after a header you have already written. |
| `T Parse(ReadOnlySpan<byte> buffer, bool shareStrings = false)` | Parses a buffer. Overloads also accept `byte[]` and `ReadOnlyMemory<byte>`. |

Parsing is greedy. You get a complete object graph that doesn't depend on the buffer, and every object in it is mutable.

The serializer keeps nothing from a graph once a write or parse returns, so streaming many messages through it doesn't build up memory.

With `shareStrings: true`, writing stores equal strings once, which makes buffers with repeated strings smaller but writing strings slower. Parse such buffers with `shareStrings: true` too, so each stored string becomes one `string` instance instead of one copy per reference. Leaving it off costs nothing.

#### Shared References

Within one write, a table *instance* that is referenced from several places is written once. Within one parse, each table in the buffer becomes a single instance. So this holds:

```csharp
var node = new Node();
var parsed = Model.Serializer.Parse(Model.Serializer.WriteToArray(new Model { Nodes = [node], Start = node }));
ReferenceEquals(parsed.Nodes[0], parsed.Start); // true
```

Strings, vectors and structs are written once per reference and parsed as copies, except for strings with `shareStrings: true`. A reference cycle between tables can't be represented in FlatBuffers, so writing one throws `InvalidOperationException`.

#### Errors

- Writing throws `InvalidOperationException` when:
  - a required field is null, or a required union is `NONE`;
  - a union declared with `bequadro_exclude_none` has no value;
  - a union holds an unknown member (`UnionUnknown`), whose data isn't kept when parsing;
  - a vector contains null;
  - the graph contains a cycle;
  - tables are nested more than 64 levels deep, which is the most that parsing accepts;
  - the buffer would be larger than 2,147,483,591 bytes, the largest .NET array;
  - a table's present inline fields would take more than 65,535 bytes, which only very large structs can do.
- Passing `null` as the value, the destination or a `byte[]` buffer throws `ArgumentNullException`.
- Messages name tables, fields and unions as the schema does, so they read the same in C#, TypeScript and Python.
- Parsing throws `InvalidDataException` when the buffer is malformed, tables are nested more than 64 levels deep, or a required field is missing. With the strict union attributes, it also throws for an unknown discriminator (`bequadro_exhaustive`) or a `NONE` vector element (`bequadro_exclude_none`).

### Generated C#

| Schema | C# |
| --- | --- |
| `table T` | `public sealed partial class T`, with `{ get; set; }` properties and reference equality (or value equality, see [Value Equality](#value-equality)) |
| `struct S` | `public partial struct S`, with public mutable fields and value equality |
| `enum E : ubyte` | `public enum E : byte` |
| `union U` | `public readonly partial struct U` that C# pattern matching understands (see [Unions](#unions)) |
| `[T]` | `IReadOnlyList<T>`. Assign any list or array; parsing produces arrays. |
| `string` | `string` |

Mapping rules:

- **Field names** become PascalCase: `parent_index` becomes `ParentIndex`.
- **`required` fields** become `required` properties with non-nullable types.
- **Other non-scalar fields** are nullable, except fields of unions that allow `NONE`, where `NONE` means absent.
- **Scalar defaults** become property initializers: `radius: double = 1` gives `Radius { get; set; } = 1d`.
- **`= null`** makes an optional scalar such as `int?`.
- **Scalars without a default** can be made `required` with a build property (see [Required Scalars](#required-scalars)).
- **Partial types**: everything is `partial`, so you can add members, interfaces and constructors in your own files. If you add a constructor to a table, the parameterless constructor is still generated.
- **Sealed tables**: you can't derive from a table, because writing a subclass would drop what it adds, and parsing would give back the table type. Add members in a partial declaration instead.
- **Doc comments**: `///` comments in the schema become XML documentation.
- **Names are never rejected**: a name C# can't use is renamed with a trailing `_`, and its doc comment says so (`Schema name: serializer`). See [Renamed Names](#renamed-names).

#### Renamed Names

Any name `flatc` accepts works. When C# can't use a name as written, the generated code renames it by appending `_` until it can, and the element's doc comment gives the schema name. The wire format doesn't store names, so renaming never changes a buffer. Renamed are:

- a field or union label named like a generated member, or like what every type inherits from `object`: `Serializer`, `ToString`, `Equals`, `GetHashCode`, `GetType`, `MemberwiseClone`, `Finalize` and `ReferenceEquals`, and for unions `Kind`, `ItemKind`, `Value`, `HasValue`, `TryGetValue`, `_kind`, `_reference` and `_value1`, `_value2`, ...;
- names that collide with the methods C# generates behind a property or operator: a property `Kind` is also the method `get_Kind`, and `==` is `op_Equality`. So a union label `get_Kind` or `op_Implicit`, a label `get_A` beside a label `A`, a table named `get_Serializer`, and a struct named `op_Equality` are renamed, and so is field `x` of a table named `get_X`;
- a member named like its type, which C# forbids;
- a type named like a member generated in it, such as a table named `Serializer`;
- an enum value or union label named `value__`, which .NET reserves;
- fields that collide after PascalCase, such as `foo_bar` and `fooBar`, which become `FooBar` and `FooBar_`. The field declared first keeps the plain name, so adding a field never renames an existing one;
- a field that would start with a digit, such as `_1`, which gets a leading `_`;
- names that would hide the namespaces generated code refers to: a root-namespace type named `System` or `Bequadro`, a top-level namespace `System`, a root-namespace type or top-level namespace named `BequadroSchema`, which the [schema fingerprint](#schema-fingerprint) uses, and types and namespaces in `Bequadro` named like the runtime's (`FlatBufferSerializer`, `UnionNone`, `UnionUnknown`, `Internal`). `namespace System.Collections;` becomes `System_.Collections`, and the doc comment of each type in it gives the type's full schema name;
- a type with the same full name as a namespace, such as table `N.M` and namespace `N.M`. Whichever appears first in the schema set keeps the name;
- generated file names that differ only in case, as for tables `Foo` and `foo` (only the file is renamed).

C# keywords, contextual ones such as `record` included, are escaped with `@`, which isn't renaming, and so are type names made only of lowercase letters, such as `monster`, which C# would otherwise warn about (CS8981). As in hand-written C#, a type named `var`, `dynamic` or `nint` changes what that word means in your code in the same namespace. A root-namespace type named like a namespace from another assembly you reference, such as `Microsoft`, compiles with warning CS0437, as it would in hand-written C#. Put it in a namespace.

#### Unions

A union is a struct marked as a C# union, so `switch` and `is` work on it directly, and the compiler warns when a `switch` misses a case. Its cases are:

- each member type;
- `Bequadro.UnionNone`, the FlatBuffers `NONE` value. `default(U)` is `NONE`, and an absent union field reads as `NONE`;
- `Bequadro.UnionUnknown`, a member added by a newer schema, read from a buffer written with that schema. Only the discriminator is kept, so writing it throws.

These two extra cases are the FlatBuffers defaults. Attributes on the union declaration remove them:

```fbs
union Shape (bequadro_exclude_none, bequadro_exhaustive) { Circle, Polygon }
```

- `bequadro_exclude_none`: no `UnionNone` case. Optional fields of the union are `Nullable<U>`. Writing a union with no value throws, and so does parsing a vector with a `NONE` element.
- `bequadro_exhaustive`: no `UnionUnknown` case. Parsing an unknown discriminator throws.

Bequadro attributes don't need an `attribute` declaration. `flatc` does need one, so declare them if you also compile the schema with `flatc`.

Each union also has:

- a constructor and an implicit conversion from each member type, and a constructor from `UnionNone` and `UnionUnknown` when they are cases;
- `Kind`, of a nested `ItemKind` enum with `NONE` and one value per member label. For an unknown member it is the raw discriminator;
- a property per member label, such as `shape.Circle`, which throws if the union holds something else;
- `TryGetValue(out T)` for each case;
- value equality: table members compare by their own equality (by reference, unless the table has [value equality](#value-equality)), struct members by value.

Struct members are stored unboxed, so matching on them doesn't allocate.

#### Value Equality

Tables compare by reference, so you can key dictionaries on instances. For a small table that you use like a struct, but made a table so that you can add fields later, add `bequadro_value_equality`:

```fbs
table Unit (bequadro_value_equality) { symbol: string (required); scale: double = 1; }
```

The class then implements `IEquatable<Unit>`, and overrides `Equals`, `GetHashCode`, `==` and `!=`. Two instances are equal when all their fields are equal:

- scalars as `ValueTuple` compares them, so `NaN` equals `NaN`, and strings by ordinal comparison;
- structs and unions by their own value equality;
- vectors element by element, whatever the collection type, so an array equals a `List<T>` with the same elements. A `null` vector doesn't equal an empty one;
- table fields by the referenced table's equality, which is by reference unless that table has the attribute too.

Things to be aware of:

- **Mutability**: tables stay mutable. Don't change one while it is a key in a dictionary or a set.
- **Sharing**: it still follows instances. Two equal but separate instances are written twice and parse as two instances.
- **Cycles**: comparing or hashing a reference cycle of such tables overflows the stack, as it does for C# records.
- **Schema evolution**: parsing drops fields that a newer schema added, so buffers that differ only in those fields parse as equal tables.

### Build Properties

Set these in your project file. They apply to every schema in the project.

| Property | Default | Effect |
| --- | --- | --- |
| `BequadroRequireScalarsWithoutDefault` | `false` | Makes scalar and enum fields without a default `required` (see [Required Scalars](#required-scalars)). |

Only `true` turns a property on. Any other value is the same as leaving it unset.

#### Required Scalars

A scalar field without a default is 0 when you forget to set it, and nothing tells you. To make the compiler catch that, set:

```xml
<PropertyGroup>
  <BequadroRequireScalarsWithoutDefault>true</BequadroRequireScalarsWithoutDefault>
</PropertyGroup>
```

Scalar and enum table fields without a default in the schema then become `required` properties, so an object initializer that leaves one out doesn't compile (CS9035). Write a default for fields that may be left out, even if it is 0:

```fbs
table Section
{
    depth: double;          // public required double Depth { get; set; }
    angle: double = 0;      // public double Angle { get; set; }
    offset: double = null;  // public double? Offset { get; set; }
}
```

This only changes the C# properties. A required scalar is still optional in the buffer: writing leaves it out when it equals its default, and parsing a buffer without it gives the default, so buffers stay compatible both ways. If you add a constructor to a table in a partial declaration, mark it `[SetsRequiredMembers]` when it sets every required property.

### Migrating From FlatSharp

- Remove the FlatSharp packages and MSBuild targets, and list the schemas as `AdditionalFiles`.
- `fs_*` attributes have no effect. Delete them, and their `attribute` declarations, from your schemas. These FlatSharp settings are now fixed behavior:
  - vectors are always `IReadOnlyList<T>` (`fs_vector`);
  - structs are always value types (`fs_valueStruct`);
  - every table has a serializer (`fs_serializer`).
- `Parse(buffer, FlatBufferDeserializationOption.X)` becomes `Parse(buffer)`. Parsing is always greedy and mutable.
- `GetMaxSize` plus `Write(Span<byte>, ...)` becomes `WriteToArray` or `Write(IBufferWriter<byte>, ...)`.
- Tables are sealed and their properties aren't virtual. Move members of classes derived from tables into partial declarations of the tables.
- Unions keep `Kind`, `ItemKind`, named accessors and implicit conversions. Replace `Match` and visitors with a C# `switch` on the member types, and `TryGet` with `TryGetValue`. `Switch`, `Discriminator` and `ItemN` are gone.
- FlatSharp unions had no `NONE` or unknown value. To keep that behavior, add `bequadro_exclude_none` and `bequadro_exhaustive` to the union. Otherwise handle `UnionNone` and `UnionUnknown`.

### Troubleshooting

- **Code isn't regenerated after updating the package or generator**: run `dotnet build-server shutdown`. The compiler server caches analyzers.
- **To see the generated code**: set `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` and look under `obj/**/generated`, or use your IDE's "Go to definition".

## Command-line tool

The `bequadro` CLI generates TypeScript and Python and writes the runtime alongside the generated code. It is a standalone executable for Linux (glibc 2.34 or later), macOS and Windows on x64 and arm64, and runs without .NET installed.

```sh
npm install --save-dev bequadro
```

npm installs the executable for your platform as an optional dependency.

You can also download the standalone CLI from [GitHub Releases](https://github.com/parched/bequadro/releases). Pick `bequadro-linux-*` for Linux, `bequadro-osx-*` for macOS, or `bequadro-win-*.exe` for Windows, with `x64` or `arm64` for your machine. On Linux and macOS, use `chmod +x` on the downloaded file before running it.

To build from source, `uv run build.py --pack` puts the standalone executable at `artifacts/cli/<rid>/bequadro` (`bequadro.exe` on Windows).

Pass schema files or directories as inputs. The CLI searches directories recursively for `.fbs` files. Every included file must be among the inputs.

The CLI opens schemas for shared reading, so editors can save them while generation is running, including on Windows.

The CLI uses a generated-file header to identify the files it can update or remove, and skips symbolic links inside the output directory. Use `--check` in CI to list files it would create, update or remove and fail if any changes are needed.

## TypeScript

Use the [Vite plugin](#vite-plugin) to generate TypeScript as part of your Vite build, or run the [CLI](#command-line-tool) directly with the [example schema](#example-schema).

```sh
npx bequadro generate --target typescript --out src/schema schemas/
```

The output directory gets one module per namespace (`Shapes.ts` for `namespace Shapes;`, `index.ts` for the root namespace), the runtime, `bequadro.ts`, and the [schema fingerprint](#schema-fingerprint), `bequadro_schema.ts`. The generated code has no npm runtime dependencies.

### Vite Plugin

Add `bequadro/vite` to your Vite config. The plugin generates TypeScript before modules load in both the dev server and production builds. In the dev server, adding, editing, or deleting a schema triggers generation again.

```ts
// vite.config.ts
import { defineConfig } from "vite";
import bequadro from "bequadro/vite";

export default defineConfig({
  plugins: [bequadro({ inputs: "schemas", out: "src/schema" })],
});
```

Schema errors fail the build. The dev server shows them in the error overlay and keeps the last good generated files. For plugin options and the `generate` Node API, see the [npm package README](npm/bequadro/README.md).

### Generated TypeScript

Imports use `.ts` extensions, which run directly under Node's type stripping, Deno and Bun, and work with bundlers and `tsc`'s `rewriteRelativeImportExtensions`. Pass `--import-extension js` for a `tsc` project with `module: nodenext` that doesn't rewrite extensions. The code needs TypeScript 5.8 or later, and compiles under `strict`, `exactOptionalPropertyTypes`, `isolatedDeclarations` and `erasableSyntaxOnly`.

Import the generated module to write and parse a drawing:

```ts
import { Drawing, type Shape } from "./schema/Shapes.ts";

const bytes = Drawing.write({
  title: "Example",
  shapes: [
    { kind: "Circle", value: { center: { x: 1, y: 2 } } },
    { kind: "Polygon", value: { points: [] } },
  ],
});

const drawing = Drawing.parse(bytes);

function describe(shape: Shape | undefined): string {
  switch (shape?.kind) {
    case "Circle":
      return `circle of radius ${shape.value.radius}`;
    case "Polygon":
      return `polygon with ${shape.value.points.length} points`;
    case "unknown":
      return `a shape added in a newer schema (${shape.discriminator})`;
    case undefined:
      return "nothing";
  }
}
```

| Schema | TypeScript |
| --- | --- |
| `table T` | `interface T`, plus `interface TInit` for writing, and `const T` with `create`, `write` and `parse` |
| `struct S` | `interface S` and `SInit`, and `const S` with `create` and `equals` |
| `enum E : ubyte` | `const E = { A: 0, ... } as const` and `type E` |
| `union U` | `{ kind: "Member"; value: Member } \| ...`, plus `{ kind: "unknown"; discriminator }`, and `UInit` |
| `[T]` | `T[]`; `[ubyte]` is `Uint8Array` |
| `long`, `ulong` | `bigint` |
| other scalars | `number` or `boolean` |

- **Two types per table.** `T` is what `parse` and `create` return, so every scalar is present: `circle.radius` is a `number`. `TInit` is what `write` accepts, where scalars may be left out and are written as their defaults, so a message can be a plain literal. A parsed `T` is assignable to `TInit`, so you can parse, change and write back. `T.create({...})` gives a complete object with defaults filled in, for state you keep and read.
- **Absent is `undefined`.** Fields that can be absent are optional properties, `x?: X | undefined`, and a union's `NONE` is `undefined` too. `bequadro_exclude_none` only changes union vectors, whose elements then can't be `undefined`; `bequadro_exhaustive` removes the `unknown` variant.
- **Names.** Field names become camelCase. Names TypeScript can't use get a trailing `_`, like C#'s: a field named `constructor`, a table named `class` or `keyof`, a union member labelled `unknown`, or a top-level namespace named `bequadro`, `index` or `bequadro_schema`.
- **Bytes.** `parse` takes any `Uint8Array`, such as a Node `Buffer` from `fs.readFileSync`, and `write` returns a plain `Uint8Array` holding exactly the buffer, over an `ArrayBuffer` of its own. Its type is `Uint8Array<ArrayBuffer>`, so it can be passed to APIs that don't accept a `SharedArrayBuffer`, such as `Blob` and `fetch`, without a cast. A parsed `[ubyte]` is a copy, so the input can be reused.
- **Errors.** `parse` throws `FlatBufferDataError` and `write` throws `FlatBufferWriteError`, from `bequadro.ts`. Because plain objects aren't type-checked at runtime, `write` also checks each value's type and range, and names the field when one is wrong.
- **Equality.** Structs and unions, and tables with `bequadro_value_equality`, have an `equals(a, b)` function, with the same rules as C#'s `Equals`. `===` always compares objects by identity.
- **Enums from newer schemas.** A value the schema doesn't define parses as its number, which the enum's type doesn't include, so give a `switch` on an enum a `default` branch.

## Python

Generate Python from the [example schema](#example-schema) using the [CLI](#command-line-tool). The generated code works with Python 3.12 and later and uses only the standard library.

```sh
npx bequadro generate --target python --out src/app/schema schemas/
```

If you downloaded or built the standalone executable, use its path in place of `npx bequadro`.

The output directory is a package: each namespace is a subpackage (`app.schema.Shapes` for `namespace Shapes;`), the root namespace's declarations are importable from the package itself, the runtime is `bequadro.py`, and the [schema fingerprint](#schema-fingerprint) is in `bequadro_schema.py`. Imports between generated modules are relative, so put the output anywhere in your package.

```python
from app.schema import bequadro
from app.schema.Shapes import Circle, Drawing, Point, Polygon, Shape
from app.schema.bequadro import UnionUnknown

data = bequadro.write(
    Drawing(title="Example", shapes=[Circle(center=Point(x=1, y=2)), Polygon(points=[])])
)
drawing = bequadro.parse(Drawing, data)


def describe(shape: Shape | None) -> str:
    match shape:
        case Circle(radius=radius):
            return f"circle of radius {radius}"
        case Polygon(points=points):
            return f"polygon with {len(points)} points"
        case UnionUnknown(discriminator=discriminator):
            return f"a shape added in a newer schema ({discriminator})"
        case None:
            return "nothing"
```

- **Classes.** Tables and structs are `@final` dataclasses with `slots=True` and `kw_only=True`, so construction takes keyword arguments and fills in defaults. `required` fields have no default. With `--require-scalars-without-default`, neither do scalars without a default in the schema.
- **Unions** are plain unions of their member classes, `type Shape = Circle | Polygon | UnionUnknown`, so `match` with class patterns selects members, and `assert_never` makes type checkers check exhaustiveness. `NONE` is `None`.
- **Types.** Vectors are `list`s (writing takes tuples too), except `[ubyte]`, which is `bytearray` (writing takes any bytes-like object). Enums are `IntEnum`s; a value from a newer schema parses as an instance that isn't a member, whose `name` is the value, such as `"9"`, so give a `match` a `case _`.
- **Names** are used as written. Names Python can't use get a trailing `_`: keywords (`from_`), declarations named like built-ins (`type_`) or `annotations`, and enum values named like `IntEnum` attributes (`name_`, `value_`, `real_`).
- **Equality.** Tables compare and hash by identity, so parsed instances can key dictionaries. Structs, and tables with `bequadro_value_equality`, compare by value, with `NaN` equal to `NaN` and `float` fields at 32-bit precision as in C#, and are unhashable, as mutable value types are in Python.
- **Errors.** `parse` raises `FlatBufferDataError` and `write` raises `FlatBufferWriteError`, both `ValueError`s. `write` checks each value's type and range, and names the field when one is wrong.
- **Recursion limit.** `write` and `parse` recurse once per level of nesting, so they raise the process's recursion limit (`sys.setrecursionlimit`) enough for 64 levels above the caller's stack, and leave it raised. They never lower it.
- **Type checking.** Generated code passes `pyright --strict` and `mypy --strict`.

## Schema Fingerprint

Generated code in every language includes a fingerprint of the schemas it was generated from, as 32 hex digits. Compare fingerprints to check that two programs, such as a C# server and a TypeScript client, were generated from the same schemas:

| Language | Fingerprint |
| --- | --- |
| C# | `BequadroSchema.Fingerprint`, a `const string` in the global namespace |
| TypeScript | `fingerprint`, exported by `bequadro_schema.ts` in the output directory |
| Python | `FINGERPRINT`, in the module `bequadro_schema` of the output package |

The fingerprint depends only on the schemas' tokens, so white space, line endings, comments and where the files are don't change it, and it's the same on every platform. Any other change does, even one that doesn't change the wire format, such as renaming a table. A different version of Bequadro may compute a different fingerprint for the same schemas.

In C#, a project that references two assemblies with schemas gets error CS0433 where it uses `BequadroSchema`, and a project with schemas that references one gets warning CS0436. Use `extern alias` to tell them apart.

## Schema Support

Bequadro supports the parts of the schema language that most schemas use:

- tables, structs, enums and unions, including vectors of unions and structs as union members;
- namespaces and relative or absolute `include`s (there are no include search paths);
- scalar and enum defaults, and optional scalars;
- `required`;
- user attributes declared with `attribute`;
- the union attributes `bequadro_exclude_none` and `bequadro_exhaustive` ([Unions](#unions));
- the table attribute `bequadro_value_equality` ([Value Equality](#value-equality)).

Anything that would change the wire format but isn't implemented is reported as an error, never silently ignored. That covers `id`, `deprecated`, `key`, `bit_flags`, `force_align`, `nested_flatbuffer`, `flexbuffer`, `hash`, `original_order`, `offset64`, `vector64`, fixed-size arrays, strings in unions, `file_identifier` and `rpc_service`.

For interoperability with an official FlatBuffers implementation, check that its target supports the schema features you use, such as vectors of unions. Declare any `bequadro_*` attributes with `attribute` before use when sharing schemas with `flatc`. [FlatBuffers schema docs](https://flatbuffers.dev/schema/)

Like `flatc`, which reads a file from top to bottom, Bequadro requires an enum or union to be declared before it is used, a struct before a struct that contains it, and an `attribute` declaration before the attribute is used. A table or struct may be used before its declaration. A `///` comment must be on a line of its own.

Include every schema file in the C# project's `AdditionalFiles` or in the CLI inputs, including files referenced by `include`.

## Diagnostics

All three targets report schema errors with an ID and a location in the `.fbs` file. IDs are `BQ1xxx` for syntax, `BQ2xxx` for semantics, `BQ3xxx` for unsupported features and `BQ4xxx` for limits of the generated API. If a schema name can't be used in a target language, Bequadro renames it in the generated code. See the [diagnostic catalog](requirements/diagnostics.md#dia-002-diagnostic-catalog) for the full list.

The C# generator reports errors in the IDE and build, and still generates code for valid declarations that don't depend on broken ones. The CLI prints errors as `path:line:column: error ID: message`, exits with status 1 and keeps the previous output.

## Untrusted Input

Parsing never reads out of bounds, hangs or overflows the stack, whatever the buffer holds. But it is greedy, so what it allocates depends on what the buffer refers to, not only on its size: a buffer of 1 MB in which 2,000 tables all refer to one 1 MB vector parses into 2,000 copies of that vector, 2 GB. When you parse buffers from a source you don't trust, in any of the three languages:

- limit the size of the buffers you accept;
- enable string sharing when parsing (`shareStrings: true` in C#, `{ shareStrings: true }` in TypeScript or `share_strings=True` in Python) to reuse the string read from a shared buffer offset;
- avoid large vectors in schemas that untrusted buffers use, or check the buffer's size against what you expect it to hold. Vectors can't be shared, because they are mutable.

## License

[MIT](LICENSE).
