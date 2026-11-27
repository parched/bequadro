# TypeScript Requirements

How the CLI ([cli.md](cli.md)) maps schema declarations to TypeScript, and how the TypeScript runtime behaves. The mapping is fixed by convention.

## TS-001 Supported Environments

Acceptance criteria:

- Generated code and the runtime are ECMAScript modules that use only erasable TypeScript syntax: no `enum`, no runtime `namespace`, no parameter properties.
- They compile without errors with TypeScript 5.8 or later, including 7.x, with `strict`, `exactOptionalPropertyTypes`, `noUncheckedIndexedAccess`, `noImplicitOverride`, `noImplicitReturns`, `noPropertyAccessFromIndexSignature`, `noUnusedLocals`, `noUnusedParameters`, `verbatimModuleSyntax`, `isolatedModules`, `isolatedDeclarations` and `erasableSyntaxOnly` all on. They need `target` ES2022 or later.
- Relative imports use the `.ts` extension, which works with Node's type stripping, Deno, Bun, bundlers, and `tsc` with `rewriteRelativeImportExtensions` or `allowImportingTsExtensions`. `--import-extension js` writes `.js` instead, for `tsc` projects that compile with `module` `nodenext` and don't rewrite extensions.
- At runtime they use only ECMAScript built-ins plus `TextEncoder` and `TextDecoder`. They run unchanged in Node 22.18 or later (with type stripping), Deno, Bun and browsers.

Design decisions:

- Erasable syntax only, so the files run directly under Node's type stripping, and every tool that just removes types handles them.
- TypeScript 5.8 is the first version with `erasableSyntaxOnly`, and 5.7 added `rewriteRelativeImportExtensions`.

## TS-002 Files and Modules

Acceptance criteria:

- Each schema namespace with at least one declaration becomes one module. Namespace `a.b.c` is written to `a/b/c.ts` under the output directory, and the root namespace to `index.ts`. Namespace segments are used as written.
- The runtime is `bequadro.ts` in the output directory (CLI-006). It exports the public runtime API: the error classes (TS-011) and the types that generated signatures name.
- The module `bequadro_schema.ts` in the output directory exports the schema set's fingerprint as `fingerprint` (SER-015).
- A module imports the runtime and the other namespace modules it refers to with relative imports, and imports types with `import type`.
- Namespace modules may refer to each other in cycles. Importing any module first works whatever the cycles, because no module uses an import while it is being evaluated.
- A module declares the schema's declarations and their companions first, in declaration order. The codecs that write and parse them follow at the end, after a comment saying that they aren't part of the API.
- Generated code means the same whatever the schema's names: it refers to the runtime and to JavaScript built-ins only through names that a schema identifier can't spell, such as names containing `$`, and names its own parameters and locals that way too (`$init`, `$value`, `$writer`), so a declaration can't shadow them or be shadowed by them.

Design decisions:

- One module per namespace keeps imports short (`import { Monster } from "./schema/MyGame.ts"`), and mirrors the C# namespace. One module per declaration, as `flatc` writes, would make users import from hundreds of paths.
- Codecs last, so that people and coding agents reading a module find its API at the start, without the codecs in between, which make up most of the file.
- The parameters of the companions' public functions start with `$` too (`create($init)`, `write($value, $options)`), although editors show them in signatures, so that the rule has no exceptions to get wrong. With `create(init)`, an enum named `init` whose value was a field's default was read from the parameter instead.

## TS-003 Naming

Acceptance criteria:

- Table, struct, enum and union names, enum value names, union member labels and namespace segments are used as written.
- Table and struct field names become camelCase. The name is split on `_`, empty segments are dropped, the first character of the first segment is lower-cased and the first character of each later segment is upper-cased, the rest is kept as written, and the segments are joined. For example, `parent_index` becomes `parentIndex`, `YAxis` becomes `yAxis`, `force_11` becomes `force11`, and `x` stays `x`. A name made only of underscores, such as `_` or `__`, has no segment to join, so it is kept as written.
- Keywords need no escaping as property names, so a field named `class` stays `class`.
- Names that TypeScript can't use as written are renamed (NAM-002). They are:
  - a field that maps to the name of an `Object.prototype` member: `constructor`, `hasOwnProperty`, `isPrototypeOf`, `propertyIsEnumerable`, `toLocaleString`, `toString` or `valueOf`;
  - an enum value named `__proto__`;
  - a union member labelled `unknown`, which is the `kind` of unknown members (TS-007). Its `kind` becomes `"unknown_"`;
  - a declaration named like a JavaScript reserved word (`break`, `case`, `catch`, `class`, `const`, `continue`, `debugger`, `default`, `delete`, `do`, `else`, `enum`, `export`, `extends`, `false`, `finally`, `for`, `function`, `if`, `import`, `in`, `instanceof`, `new`, `null`, `return`, `super`, `switch`, `this`, `throw`, `true`, `try`, `typeof`, `var`, `void`, `while` or `with`), a name strict-mode modules can't bind (`implements`, `interface`, `let`, `package`, `private`, `protected`, `public`, `static`, `yield`, `await`, `eval` or `arguments`), a predefined type (`any`, `bigint`, `boolean`, `never`, `number`, `object`, `string`, `symbol`, `undefined` or `unknown`), or a contextual keyword that TypeScript parses specially where generated code writes a type or a type declaration (`as`, `infer`, `keyof`, `readonly` or `unique`);
  - a top-level namespace named `bequadro`, `index` or `bequadro_schema`, ignoring case, whose module would be the runtime, the root namespace module or the fingerprint's module.
- The derived names are each table's, struct's and union's input type, `{Name}Init`, and they yield to declared names (NAM-002). A table `Monster` next to a declared `MonsterInit` gets the input type `MonsterInit_`.
- Two namespaces whose module paths, or any of their directories, are equal ignoring case are renamed at the first segment that differs (NAM-002).

Design decisions:

- camelCase is the TypeScript convention, and what `flatc` generates.
- `Object.prototype` names are renamed because an input object (TS-004) that omits such a field would inherit the prototype's member instead of being absent.
- Fields named like the companion's functions, `create`, `write`, `parse` or `equals`, aren't renamed. Fields are properties of the table and input types, and the functions are properties of the companion `const`, so they never share a scope.
- Every other contextual keyword, such as `type`, `async`, `get`, `module`, `namespace`, `satisfies` or `using`, works as a declaration name in every position generated code writes one, so it is kept: a table named `type` is plausible. The TypeScript tests compile a schema that names a table, struct, enum and union after each TypeScript keyword, contextual or not, with every supported TypeScript version, so a version that gives another word a special meaning fails them.

## TS-004 Tables

Given:

```fbs
table Monster { name: string; hp: short = 100; weapons: [Weapon]; equipped: Equipment; }
```

the module contains:

```ts
export interface Monster {
  name?: string | undefined;
  hp: number;
  weapons?: Weapon[] | undefined;
  equipped?: Equipment | undefined;
}

export interface MonsterInit {
  name?: string | undefined;
  hp?: number | undefined;
  weapons?: readonly WeaponInit[] | undefined;
  equipped?: EquipmentInit | undefined;
}

export const Monster: {
  create($init?: ...): Monster;
  write($value: MonsterInit, $options?: WriteOptions): Uint8Array<ArrayBuffer>;
  parse($buffer: Uint8Array, $options?: ParseOptions): Monster;
};
```

Acceptance criteria:

- **The table type** `Name` is an interface with one mutable property per field, in declaration order. It describes the objects that `parse` and `create` return, so every field is present:
  - A scalar or enum field without `= null` is a required property: `hp: number`.
  - A `required` field is a required property of a non-optional type: `name: string`.
  - Every other field (strings, tables, vectors, structs, unions and optional scalars) is optional with `| undefined`: `name?: string | undefined`.
- **The input type** `NameInit` is what `write` accepts. It has the same properties, except that:
  - scalar and enum fields are optional, and a missing one is written as its default (SER-004);
  - table, struct and union fields refer to the input types of those declarations (`WeaponInit`), and vectors are `readonly` arrays of input types.

  `required` fields stay required. With `--require-scalars-without-default`, scalar and enum fields that have no default in the schema are required too.
- A value of the table type is assignable to its input type, so a parsed object can be written back without conversion.
- **The companion** `const Name` has:
  - `create(init)`, which returns a new object with every field property present, in declaration order: the given value, otherwise the field's default (for scalars and enums) or `undefined`. Its parameter is the table type with scalar and enum fields optional (unless required with `--require-scalars-without-default`), and every other non-required field optional. It is shallow: nested values must already be of their table, struct or union types, and are used as given, not copied. The parameter itself is optional when no property is required.
  - `write(value, options?)` and `parse(buffer, options?)` (TS-011).
  - `equals(a, b)`, only for a table declared with `bequadro_value_equality` (TS-010).
- Objects returned by `parse` and `create` are plain objects: their prototype is `Object.prototype`, and every field is an own property, in declaration order, so all objects of one table share a shape. An absent field is `undefined`, or its default for scalars and enums.
- A run of `///` comments becomes a `/** ... */` comment on the interfaces and the companion, or on the property.

Design decisions:

- Plain objects, not classes, so values survive `structuredClone`, `postMessage`, spreading (`{ ...m, hp: 1 }`) and `JSON.stringify` with their types intact, and test frameworks can compare them with literals. Without classes, `instanceof` can't tell union members apart, so unions carry a tag instead (TS-007).
- Fields that may be absent on the wire are optional properties, not `| null`. FlatBuffers has a single notion of absence, so TypeScript gets a single sentinel, and object literals can leave them out.
- Separate input types, so a message can be written as a literal that leaves out scalars with defaults, while parsed objects keep precise types: `hp` reads as `number`, never `number | undefined`, without repeating the schema default at every use. An object built as an input type and then read in application code sees `number | undefined`. Long-lived state should be built with `create`.
- `create` is shallow. Accepting nested input types would mean either copying the whole graph (with an identity map to keep sharing) or filling in defaults on the caller's objects in place, and neither is obvious behavior. Building bottom-up with `create` works.
- Companion objects instead of a TypeScript `namespace`, which isn't erasable syntax, and a separate `NameInit` name, because a `const` can't merge with a namespace to hold `Name.Init`.
- Every own property is always assigned, so parsed objects get one hidden class per table in JavaScript engines, and property access stays monomorphic.

## TS-005 Structs

Acceptance criteria:

- A struct `Name` generates an interface `Name` with one mutable, non-optional property per field, in declaration order, and an input interface `NameInit` in which every property is optional, and nested structs are input types. A missing struct field is written as zero. The zero of an enum field whose enum has no value 0, which a struct field allows, is `0` cast to the enum.
- The companion `const Name` has `create(init?)`, which takes the struct type with every property optional and returns a new object with every field present, missing ones zero (a missing nested struct is a new zero struct), and `equals(a, b)` (TS-010).
- Struct values are plain objects. Assigning one doesn't copy it, so the same object in two places is one object in memory. Writing writes each occurrence (SER-003), and parsing returns a fresh object for each occurrence (SER-011).

Design decisions:

- TypeScript has no value types. Making structs immutable would force a copy for every change to a field, which is a common operation.

## TS-006 Enums

Given `enum Color : ubyte { Red, Green = 3 }`, the module contains:

```ts
export const Color = { Red: 0, Green: 3 } as const;
export type Color = (typeof Color)[keyof typeof Color];
```

Acceptance criteria:

- Every value is written explicitly. Enums with a 64-bit underlying type (`long`, `ulong`) use `bigint` literals, such as `{ Big: 0n }`.
- Parsing a value the schema doesn't define is not an error (SER-009). The raw number, or `bigint`, is returned, even though the static type `Color` doesn't include it. Code that switches on an enum should have a `default` branch for values added by newer schemas.

Design decisions:

- An `as const` object instead of `enum`, which isn't erasable syntax (TS-001).
- The type includes only declared values, so `Record<Color, T>` lookup tables are checked for completeness. The price is that a value from a newer schema isn't in the type, which is also true of C# enums.

## TS-007 Unions

Given `union Equipment { Weapon, Armor: Shield }`, the module contains:

```ts
export type Equipment =
  | { readonly kind: "Weapon"; value: Weapon }
  | { readonly kind: "Armor"; value: Shield }
  | { readonly kind: "unknown"; readonly discriminator: number };

export type EquipmentInit =
  | { readonly kind: "Weapon"; value: WeaponInit }
  | { readonly kind: "Armor"; value: ShieldInit }
  | { readonly kind: "unknown"; readonly discriminator: number };

export const Equipment: { equals($a: Equipment | undefined, $b: Equipment | undefined): boolean };
```

Acceptance criteria:

- The union type has one variant per member: its `kind` is the member's label and its `value` is the member's table or struct type. The input type uses the members' input types.
- The `unknown` variant holds a discriminator that the schema doesn't define. It is left out for a union declared with `bequadro_exhaustive`. Writing it throws (SER-004).
- `NONE` is `undefined`:
  - A union field that isn't `required` is optional: `equipped?: Equipment | undefined`. That includes unions declared with `bequadro_exclude_none`, where an absent field is still valid.
  - A `required` union field is `equipped: Equipment`.
  - A vector of unions is `(Equipment | undefined)[]`, or `Equipment[]` for a union declared with `bequadro_exclude_none`.
- `switch (m.equipped?.kind)` narrows `value` in each `case`, and checks exhaustiveness, `case undefined` included.
- The companion `const Name` has `equals(a, b)` (TS-010).
- Writing a variant whose `kind` isn't a member label, or whose `value` is missing, throws `FlatBufferWriteError`.

Design decisions:

- A tagged object, because plain objects carry no runtime type for the writer to read the discriminator from, and a `switch` on a literal `kind` is the most reliable narrowing and exhaustiveness check in TypeScript.
- The tag is called `kind`, like C#'s `Kind` (API-006).
- `NONE` is `undefined` rather than a `{ kind: "NONE" }` variant, consistently with absent fields (TS-004), and `?.` keeps switches short.

## TS-008 Vectors

Acceptance criteria:

- A vector of `T` is `T[]` in the table type and `readonly TInit[]` in the input type. Consumers may assign any array.
- A vector of `ubyte` is `Uint8Array` in both types. Writing accepts any `Uint8Array` (TS-011), including a Node `Buffer`.
- Parsed vectors are fresh arrays, and a parsed `Uint8Array` is a plain `Uint8Array` holding a copy, never a view of the input buffer, even when the input is a Node `Buffer`, whose `slice` returns a view (SER-010).

Design decisions:

- Plain arrays, not typed arrays, so vectors can grow: `push` and `splice` work on parsed values. `[ubyte]` is the exception, because byte payloads are almost always passed on whole, and `Uint8Array` is what other APIs take.

## TS-009 Type Mapping

| Schema type | TypeScript type |
| --- | --- |
| `bool` | `boolean` |
| `byte`, `ubyte`, `short`, `ushort`, `int`, `uint` (and sized aliases) | `number` |
| `long`, `ulong` (`int64`, `uint64`) | `bigint` |
| `float`, `double` (`float32`, `float64`) | `number` |
| `string` | `string` |
| enum `E` | `E` |
| struct `S` | `S` (input: `SInit`) |
| table `T` | `T` (input: `TInit`) |
| union `U` | `U` (input: `UInit`) |
| `[X]` | `X[]` (input: `readonly XInit[]`) |
| `[ubyte]` | `Uint8Array` |

Optionality follows TS-004.

Design decisions:

- `bigint` for 64-bit integers, because `number` silently loses precision above 2^53.

## TS-010 Value Equality

Acceptance criteria:

- `equals(a, b)` is generated on the companion of every struct and union, and of every table declared with `bequadro_value_equality`. It follows API-004, API-005 and API-006:
  - numbers compare as `a === b || (a !== a && b !== b)`, so `NaN` equals `NaN` and `0` equals `-0`, as `double.Equals` does in C#;
  - `bigint`s, strings and booleans compare with `===`;
  - vectors are equal when both are `undefined`, or when they have the same length and equal elements, `Uint8Array`s included. `undefined` isn't equal to an empty vector;
  - structs compare field by field;
  - unions are equal when both are `undefined`, or when their `kind`s are equal and their values are equal by the member's own equality. Two `unknown` variants are equal when their discriminators are;
  - a table field compares with that table's `equals` if it has value equality, and with `===` otherwise.
- `equals` takes the table, struct or union types, not the input types.
- No hash function is generated.

Design decisions:

- JavaScript has no equality protocol: `===`, `Map` and `Set` always compare by reference. These functions exist for consistency with C#, and only help code that calls them.
- Input types aren't accepted, because `undefined` there means "the default", and comparing it would need its own rules.

## TS-011 Serialization

Acceptance criteria:

- `Name.write(value, options?)` serializes `value` as the root table and returns a new `Uint8Array` holding exactly the buffer, typed `Uint8Array<ArrayBuffer>`: the whole of an `ArrayBuffer` of its own. `Name.parse(buffer, options?)` parses a buffer whose root is a `Name`, and honors the view's `byteOffset` and `byteLength`. The options object is `{ shareStrings?: boolean }`, default `false` (SER-003, SER-011).
- The runtime meets SER-002 to SER-013, with these substitutions:
  - `InvalidDataException` is `FlatBufferDataError`, and `InvalidOperationException` is `FlatBufferWriteError`. Both are exported by `bequadro.ts` and extend `Error`.
  - `null` is `undefined`. A `null` where the type says `undefined` is accepted and treated the same, for untyped callers.
  - Identity is object identity: one table object is written once, and one table position parses as one object.
  - The size limit of SER-012 is the same number, 2,147,483,591 bytes, so every buffer any Bequadro writer produces can be parsed by all of them.
- The writer also checks values that the type system can't, and throws `FlatBufferWriteError` naming the table and field for:
  - a value of the wrong JavaScript type, such as a string in a number field or a `number` in a `long` field;
  - an integer field given a value that isn't an integer, or is out of the field's range;
  - an enum field given a value out of the underlying type's range.
- A `float` field is written as the nearest 32-bit float, as `Math.fround` rounds, so values beyond its range become infinite.
- Properties the schema doesn't have are ignored when writing.
- One object written as two different tables, which plain objects make possible, throws `FlatBufferWriteError`. Otherwise the second table would silently get the first one's offset.
- `write` given `null` or `undefined` instead of a value, and `parse` given anything but a `Uint8Array`, throw `TypeError`, as C# throws `ArgumentNullException` (SER-001). A `Uint8Array` is accepted from any realm, such as another frame or a `vm` context, and of any subclass, such as Node's `Buffer`. Other typed arrays and `DataView`s are not.
- Strings are written as UTF-8, as .NET writes the same UTF-16 code units: a lone surrogate is written as U+FFFD (SER-005).
- Strings are decoded as UTF-8 exactly as written: a leading U+FEFF is part of the string, not a byte order mark to strip, as in the other runtimes (SER-005, SER-008).
- Values are little-endian on every platform.
- Writing and parsing tables nested 64 levels deep, the limit (SER-003, SER-009), fits in the default stack of Node, from a caller that isn't itself deeply recursive, however many fields the tables have: a table's `write` keeps at most 64 offsets in locals, so its stack frame doesn't grow with the table beyond that. A caller that is already deep in its own recursion can still run out of stack, and gets the engine's `RangeError`, as with any JavaScript code.

Design decisions:

- `Uint8Array`s are recognized by the internal slot that `TypedArray.prototype[Symbol.toStringTag]` reads, not with `instanceof`, which is false for one made in another realm. Test runners such as Jest run tests in a `vm` context, where a `Buffer` from `fs.readFileSync` comes from Node's realm, and would otherwise be rejected.
- Only `write`'s result is typed `Uint8Array<ArrayBuffer>`. A parsed `[ubyte]` is a fresh copy too, but parsed objects are mutable, and typing their byte fields that narrowly would reject assigning a Node `Buffer` to them, whose type allows a `SharedArrayBuffer`.
- Writer checks cost a `typeof` and a range comparison per scalar, and turn a JavaScript mistake into an error naming the field, instead of a buffer with wrapped or truncated values.
- The writer copies an ASCII string of up to 12 characters a character at a time, and encodes other strings with `TextEncoder.encodeInto` into a scratch array that it reuses, then copies the bytes. `TextEncoder.encode` allocates an array per string, which cost about 350 ns whatever the length, against about 50 ns for `encodeInto`, and the character loop is faster still below about 14 characters. Strings longer than 65,536 characters use `encode`, so the scratch array stays under 200 KB. On a graph of short names this halved the time to write. The reader keeps `TextDecoder` for every string: decoding a character at a time was only faster below about 10 characters, and built strings that were slower to use as `Map` keys.
- The reader finds the tables it has already read, to return one object per position, with a `Map` from position for the first tables, then with an `Int32Array` indexed by position / 4 once it has read at least 4096 tables and 1 per KB of buffer. The `Map` was most of the time to parse buffers of many small tables, and the index made that parse about 30% faster. The index takes as much memory as the buffer, so a buffer with few tables, such as one mostly holding a large vector, never makes it, and it never takes more than 1 KB per table read. Every FlatBuffers writer puts tables at multiples of 4, but a valid buffer could put one elsewhere, and such tables stay in the `Map`. If the index can't be allocated, the `Map` keeps every table.
- The path in a message, such as `vectors[1].x`, is only built when a write fails. Building it for every vector element and struct member allocated a string for each, which halved the speed of writing large vectors of structs. A scalar or string vector passes the element's index, which costs nothing. A struct is written first with `""` as its path, which members join with their names without allocating, and after an error it is written again with its real path, which throws the same error with that path. Only that struct is written again. An error that isn't a `FlatBufferWriteError`, such as one thrown by a getter, passes through unchanged. If a getter returns an invalid value the first time and a valid one the second, the write still fails with the first error, whose message then lacks the path.
- A table's `write` passes each field straight to the runtime call that writes it, and keeps only the offsets of its out-of-line children until it starts the table. It keeps them in a local each when it has up to 64, and in one array, indexed by slot, when it has more. Locals made writing a graph of small tables about 12% faster than the array, which costs an allocation per table. With a local per field, a table of 150 fields overflowed the stack at depth 1000, the limit before it was lowered to 64, so wider tables keep the array, which keeps the frame bounded. The tests write and parse tables with 64 and 65 out-of-line fields, and a table of 600 fields, 64 levels deep in a fresh process, where unoptimized frames are largest.
- There is no backstop that turns a stack overflow into `FlatBufferWriteError`. JavaScript reports it as a `RangeError`, like an allocation failure, so catching it would also relabel those, and within the depth limit it doesn't happen.
