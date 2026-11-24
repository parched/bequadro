# Serialization Requirements

Runtime behavior of writing and reading buffers.

These requirements are written for the C# runtime. The TypeScript and Python runtimes meet SER-002 to SER-013 too, with the substitutions listed in TS-011 and PY-011, such as their own error types in place of `InvalidDataException` and `InvalidOperationException`. SER-001 is C# only: TS-011 and PY-011 define the entry points of the other languages. SER-015 is about the code every target generates.

Where a requirement says an error message names a table, field or union, it names it as the schema does: a declaration by its fully qualified schema name, such as `Shapes.Drawing`, and a field or union member by its schema name, such as `required_children`, never by a name a target renamed it to (NAM-002). So a message reads the same in every language.

## SER-001 Serializer Entry Points

`FlatBufferSerializer<T>` (in the runtime, namespace `Bequadro`) is the public entry point for a table type `T`. A singleton instance is exposed on each table as `T.Serializer`, and as `FlatBufferSerializer<T>.Instance` for generic code.

Acceptance criteria:

- `ReadOnlyMemory<byte> WriteToMemory(T value)` serializes `value` as the root table and returns exactly the bytes of the finished buffer.
- `byte[] WriteToArray(T value)` does the same and returns an array of exactly the buffer's length.
- `void Write(IBufferWriter<byte> destination, T value)` appends the finished buffer to `destination`.
- `T Parse(ReadOnlySpan<byte> buffer)` parses a buffer whose root is a `T`. Overloads taking `byte[]` and `ReadOnlyMemory<byte>` exist, so callers can pass either without an explicit conversion or an ambiguous call.
- All of them are thread-safe: concurrent calls on the same serializer instance don't interfere with each other.
- A null `value`, `destination` or `byte[]` buffer throws `ArgumentNullException`.
- Every write and parse method takes an optional `bool shareStrings = false`, which turns on string sharing (SER-003, SER-011).

Design decisions:

- FlatSharp's `GetMaxSize` + `Write(Span<byte>)` pair is gone. The writer grows its own buffer, so callers never pre-size one. Callers that need a header in front of the data can use `Write(IBufferWriter<byte>, ...)`.
- There is no deserialization-mode parameter; parsing is always greedy and mutable (SER-010).

## SER-002 Wire Format Compatibility

Acceptance criteria:

- Buffers that Bequadro writes conform to the [FlatBuffers binary format](https://flatbuffers.dev/internals/). Any conforming reader, such as the official C# or TypeScript runtimes, reads them correctly with code generated from the same schema.
- Bequadro reads any conforming buffer written by any conforming writer. That includes vtables placed before or after their table, vtables shared between tables, and arbitrary padding.
- Little-endian byte order is used for all values.
- The buffer begins with the root uoffset. There is no size prefix and no file identifier.
- Union fields and union vectors use the standard two-slot (`_type` + value) representation. A union member that is a struct is stored out of line, and the union's uoffset points directly at the struct's bytes. A `NONE` element of a union vector has discriminator 0 and a uoffset of 0.

## SER-003 Identity Preservation on Write (Table Deduplication)

Acceptance criteria:

- Within one write, a table object that is reachable more than once (by reference identity, not value equality) is written exactly once, and every reference to it uses the same offset.
- This applies to tables wherever they appear: fields, vectors, unions and the root.
- Vectors and structs are *not* deduplicated. Each occurrence is written separately.
- By default, strings aren't deduplicated either. With `shareStrings: true`, strings that are equal (by ordinal comparison) are written once, and every reference to them uses the same offset.
- A reference cycle among table objects makes the write throw `InvalidOperationException` with a message naming the table type. It must not loop forever or overflow the stack.
- Table nesting deeper than 64 levels, the limit for parsing (SER-009), makes the write throw `InvalidOperationException`, so every buffer that is written can be parsed. The root is level 1, and a table already written doesn't count again. However deep the graph, the write doesn't overflow the stack.

Design decisions:

- Object graphs often share instances heavily: many edges reference the same node, and many items reference the same shared settings. Identity deduplication keeps buffers small, and with SER-011 it makes the sharing survive a round trip.
- Cycles cannot be written in FlatBuffers without an infinite buffer. Failing loudly is better than a stack overflow.
- The nesting limit is the same as the reader's, and both count levels the same way: the writer and the reader visit fields in the same order, so each table is first reached by the same path. Without the limit, a write could succeed and produce a buffer that can't be parsed back.
- String sharing is optional because it costs a lookup per string written, which made writing buffers full of unique strings about twice as slow. Switched off, its cost isn't measurable. It compares values rather than references, so equal strings built separately are shared too.

## SER-004 Writing Defaults and Absent Fields

Acceptance criteria:

- A non-optional scalar or enum field equal to its schema default is not written. Readers see the default.
- Values are compared with the default numerically, so `0.0` and `-0.0` are equal to each other. A field whose default is `-0.0` (SCH-010) isn't written when it is `0.0` or `-0.0`, and reads back as `-0.0`; a field whose default is 0 isn't written when it is `-0.0`, and reads back as `0.0`. A NaN default is the exception, because NaN equals nothing: such a field isn't written when it is any NaN, whatever its bits, and is written when it is anything else. A NaN value of a field whose default isn't NaN is written.
- An optional scalar (`= null`) is written whenever it is non-null, even if it equals 0.
- A null non-required reference field, or a null `Nullable` struct or union field, is not written.
- An empty vector is written as a vector of length 0, so it reads back as empty, not null.
- A null required field makes the write throw `InvalidOperationException`, with a message naming the table and field.
- A null element in a vector of strings or tables makes the write throw `InvalidOperationException`.
- A union field that is `NONE` is not written (neither slot). In a vector, a `NONE` element is written as discriminator 0 with a uoffset of 0.
- A required union field that is `NONE` makes the write throw `InvalidOperationException`, with a message naming the table and field.
- For a union declared with `bequadro_exclude_none`, a value with no member (`default`) makes the write throw `InvalidOperationException`, whether it is in a field or in a vector. An absent optional field of such a union is expressed as `null`.
- A union holding `UnionUnknown` makes the write throw `InvalidOperationException`. Only its discriminator was kept when parsing, so its value can't be written.

Design decisions:

- The comparison with a default is what the C++ `FlatBufferBuilder` does (`IsTheSameAs` in `flatbuffers/base.h`): `==`, and with `FLATBUFFERS_NAN_DEFAULTS`, any NaN equals a NaN default. A writer that kept the sign of zero would write fields `flatc`'s writers leave out. If the sign of a zero matters, give the field no default, or make it optional (`= null`).

## SER-005 Determinism

Acceptance criteria:

- Writing equal object graphs (same structure, values and sharing) always produces identical bytes, whatever the thread, process, platform or runtime instance.
- The C#, TypeScript and Python writers produce identical bytes for equal graphs written with the same `shareStrings` setting. In TypeScript, a scalar left out of an input object (TS-004) counts as equal to its default. The one exception is the bits of NaN values. JavaScript can't control them: it always writes the canonical quiet NaN, whose sign bit differs from .NET's `double.NaN`. Python writes a `double` NaN's bits as they are, but the NaNs Python creates (`math.nan`, `float("nan")`) have the sign bit clear too, and parsing then writing a `float` (32-bit) signalling NaN may make it quiet, depending on the CPython version (PY-011).
- Output never depends on hash codes, the iteration order of hash-based collections, or memory addresses.

Design decisions:

- All three writers implement one layout algorithm (SER-014), so comparing their bytes is a strong cross-language test: interoperability that C# proves against the official runtime carries over to the others.

## SER-006 Optional Scalars

Acceptance criteria:

- An optional scalar field that is absent reads as `null`. One that is present reads as its value.

## SER-007 Alignment

Acceptance criteria:

- Every scalar is aligned to its size relative to the start of the buffer.
- Structs are aligned to their alignment (SCH-006).
- A vector's length prefix is 4-byte aligned, and its elements are aligned to the element alignment.
- The total buffer size is a multiple of the largest alignment used in it.
- In C#, `WriteToArray` and `WriteToMemory` return a whole array, so on 64-bit platforms, where .NET aligns the data of every array to 8 bytes, the buffer starts at an address aligned to its largest alignment. 8 bytes is the largest alignment a schema can have, because `force_align` isn't supported (BQ3001).

Design decisions:

- The address of the returned buffer is aligned by .NET's layout of arrays, not by copying into specially aligned memory. That costs nothing and covers every 64-bit platform. On a 32-bit platform, array data is only 4-byte aligned, and reading an 8-byte value from it is still correct, if possibly slower; a buffer passed to `Write(IBufferWriter<byte>)` is wherever the caller's writer puts it.

## SER-008 Strings

Acceptance criteria:

- Strings are written as UTF-8 with a 32-bit length prefix and a trailing zero byte, which is not counted in the length.
- When reading, invalid UTF-8 is decoded with replacement characters and doesn't throw.

## SER-009 Malformed Input

Acceptance criteria:

- Parsing a buffer that is truncated, has out-of-range offsets, or is otherwise not a valid buffer for the schema throws `System.IO.InvalidDataException`. It never throws `IndexOutOfRangeException`, never reads out of bounds, never hangs, and never overflows the stack.
- A missing required field throws `InvalidDataException` naming the table and field.
- A union discriminator that the schema doesn't define parses as `UnionUnknown` with that discriminator, without following its uoffset. For a union declared with `bequadro_exhaustive`, it throws `InvalidDataException`.
- A union field whose discriminator is 0, or whose discriminator or value slot is absent, parses as `NONE` (or `null` for a union declared with `bequadro_exclude_none`). If the field is required, it is missing.
- A `NONE` element in a union vector parses as `NONE`. For a union declared with `bequadro_exclude_none`, it throws `InvalidDataException`.
- An enum value that the schema doesn't define is not an error. It parses as that raw value cast to the enum type.
- Table nesting deeper than 64 levels throws `InvalidDataException`. Writing enforces the same limit (SER-003).
- Fields that are present in the buffer but not in the schema (newer schema versions) are ignored.
- These are malformed, and throw `InvalidDataException`: a table position that the buffer's uoffsets reach as two different tables, and a union vector whose discriminator and value vectors differ in length.
- These are accepted, as the official readers accept them: any non-zero byte in a `bool` field or vector element reads as `true`; offsets and scalars that aren't aligned (SER-007 constrains writers, not readers); and a string without its trailing zero byte.

Design decisions:

- Parsing is greedy (SER-010), so a buffer whose uoffsets point many times at one large vector parses into a copy of that vector per reference. A hostile buffer can use this to make a parse allocate far more memory than the buffer's size: in a buffer of about 1 MB, 2,000 tables that all refer to one 1 MB vector parse into 2 GB. Strings can be shared (`shareStrings`, SER-011), but vectors can't, because they are mutable. The runtimes don't limit what a parse allocates, because any limit would either reject valid buffers or still allow a large multiple of the buffer's size. The README tells callers who parse untrusted buffers to limit the buffer's size, use `shareStrings: true`, and avoid large vectors of scalars in schemas used for untrusted input.
- The nesting limit is 64, the default `max_depth` of flatc's verifier, so a buffer that the verifier accepts by default isn't rejected for its depth. Recursive data such as trees and scene graphs can go deeper than a small limit like 20, while 64 is far above the deepest real data measured (9 levels). The limit was 1000. Lowering it keeps the recursion of every runtime shallow, so stack use and Python's recursion limit matter far less.
- Booleans, alignment and string terminators are checked by no official reader either, and checking them wouldn't make any other value safer to use.

## SER-010 Greedy, Mutable Parsing

Acceptance criteria:

- `Parse` returns a fully materialized object graph that holds no reference to the input buffer. Changing or reusing the buffer afterwards doesn't affect the result.
- Every parsed object is fully mutable. Properties can be set and vectors replaced.

## SER-011 Identity Preservation on Read

Acceptance criteria:

- Within one `Parse` call, every uoffset that points at the same table position yields the same C# instance.
- Writing a parsed graph again preserves the sharing (SER-003).
- Separate `Parse` calls never share instances.
- With `shareStrings: true`, every uoffset that points at the same string position yields the same `string` instance. By default, each one yields a fresh string.
- Vectors and structs are always fresh values.
- uoffsets are unsigned and point forward, so a valid buffer cannot contain a reference cycle. A malformed buffer whose offsets refer back to a table being read gets the cached instance, so parsing always terminates.

Design decisions:

- Code often keys dictionaries on table identity, for example to map each node of a graph to something computed for it. That only works if shared references in the buffer become shared instances.
- Sharing strings keeps the memory a parse allocates for strings proportional to the buffer, not to how often the buffer refers to each string. Without it, a small buffer whose many uoffsets point at one long string becomes one copy per reference. Such buffers come from writing with `shareStrings: true`, from other libraries' `CreateSharedString`, or from a hostile source. Strings are immutable, so sharing them is only visible through `ReferenceEquals`.
- Parsing doesn't share strings by default, because it costs 5–10% on parses that are mostly strings, and buffers that Bequadro writes only refer to a string twice when written with `shareStrings: true`. Switched off, its cost isn't measurable.
- Vectors are not shared, because they are mutable: a change through one reference would show through the others.

## SER-012 Buffer Size Limit

Acceptance criteria:

- A write whose buffer would exceed the largest .NET array, `Array.MaxLength` (2,147,483,591 bytes), throws `InvalidOperationException`.
- That includes a vector whose elements alone would exceed it.
- A table whose present inline fields take more than 65,535 bytes, or whose vtable would, throws `InvalidOperationException` naming the table, because a vtable entry can't describe it. That needs struct fields, since a struct can be nearly that size itself (SCH-006), or more than 32,765 fields. Which fields are present decides it, so it is a write error rather than a schema diagnostic.

## SER-013 Memory Held Between Calls

Acceptance criteria:

- Once a write or parse returns or throws, the runtime keeps no reference to the graph that was written or parsed, or to any of its tables or strings. They can be collected as soon as the caller lets go of them.
- The identity maps of SER-003 and SER-011 last for one call. Writing or parsing many buffers one after another, as when streaming messages, doesn't accumulate memory from earlier calls.

Design decisions:

- The identity maps are ordinary dictionaries that hold strong references, not weak ones. During a call, everything in them is reachable from the graph being written or built anyway, so weak references couldn't free anything and would only slow every lookup down. What keeps memory from being held is emptying the maps when the call ends.
- Each thread keeps the maps' storage between calls, so repeated writes and parses don't allocate it again. Storage that grew unusually large is trimmed, so one large buffer doesn't hold a large allocation for the rest of the thread's life.

## SER-014 Buffer Layout

Where each part of a buffer goes. SER-005 requires every writer to produce the same bytes, so every writer follows these rules exactly. Readers don't depend on them (SER-002).

Acceptance criteria:

- The buffer is built from the back to the front, as the official builders build it: each part is placed in front of everything written before it. "Before" below means earlier in writing order, so later in the buffer.
- Before each part, the writer pads with zero bytes so that the part's first byte is aligned as the part needs, counting from the end of the buffer. The largest alignment any part needed is remembered for the end.
- A table is written in this order:
  1. Its out-of-line fields that are present, in the order of their slots, which is declaration order: strings, tables (written once each, SER-003), vectors and union values. A union's value is its table, its string, or its struct, which is placed on its own, aligned to the struct's alignment.
  2. Its inline part: its present scalar, enum and struct fields, and the uoffsets of its present out-of-line fields, sorted by alignment from largest to smallest, and within each alignment by slot. A scalar's alignment is its size, a struct's is its own (SCH-006), and a uoffset's is 4. A union's discriminator is a 1-byte scalar in the slot before its uoffset's. Each is aligned to its alignment. A scalar equal to its default isn't written (SER-004).
  3. The table's soffset to its vtable, aligned to 4.
  4. Its vtable: the vtable's size, the table's inline size (from the soffset to the end of its inline part, padding included), then one entry per slot up to the last present field, trailing absent slots left out. If a byte-identical vtable was already written in this buffer, the soffset refers to the first one written and no vtable is written. Otherwise the vtable is placed directly in front of the table, with no padding.
- A string is its UTF-8 bytes followed by a zero byte, preceded by its 32-bit length, all placed as one part aligned to 4.
- A vector of scalars, enums or structs is its elements in order, preceded by its 32-bit length, placed as one part aligned to the larger of 4 and the element's alignment.
- A vector of strings or tables is written as each element in order, then the uoffsets to them preceded by the length, aligned to 4. A vector of unions is written as each element's value in order, then the vector of their uoffsets (0 for `NONE`), then the vector of their discriminators, aligned to 4.
- The buffer ends with the root table's uoffset, placed last (so at the start of the buffer) and aligned to the larger of 4 and the largest alignment of any part, so the buffer's length is a multiple of it.

Design decisions:

- This is the order the official builders produce when generated code adds fields largest first, which keeps padding small, so Bequadro's buffers look like theirs. The order is an acceptance criterion rather than an implementation detail because SER-005 compares the bytes of three independent writers: without a written order, a change to one writer would be a bug only by comparison with the others.


## SER-015 Schema Fingerprint

Generated code in every target holds a fingerprint of the schema set it was generated from, so that programs can check that they were generated from the same schemas, for example when they connect to each other.

Acceptance criteria:

- In C#, the generator emits `BequadroSchema.g.cs`, which declares `public static class BequadroSchema` in the global namespace with the fingerprint as `public const string Fingerprint` (API-001). In TypeScript, the module `bequadro_schema.ts` in the output directory exports it as `fingerprint`, typed `string` (TS-002). In Python, the module `bequadro_schema.py` in the output package defines it as `FINGERPRINT`, typed `Final[str]` (PY-002).
- The fingerprint is 32 lowercase hexadecimal digits.
- It depends only on the tokens of each file in the schema set (SCH-001). Schemas that differ only in white space, line endings, a byte order mark, comments (documentation comments included), or the escapes that spell a string literal's characters have the same fingerprint. So do schema sets whose files have other paths, or come in another order, so it is the same on every platform and wherever the schemas are checked out.
- Any change to a file's tokens changes it, and so does adding or removing a file. That includes changes that don't affect the wire format, such as renaming a table or reordering declarations.
- It is the same in every target for the same schema set: in the source generator and in the CLI for either target. Options (CON-006, CLI-002) don't change it.
- The source generator emits it even when the schema set has errors, as it does the declarations (DIA-001). The CLI writes nothing then (CLI-004).
- A schema set with no files has no fingerprint, so a C# project with no `.fbs` files gets no generated source (CON-003), and the CLI writes only the runtime (CLI-002).
- It may differ between Bequadro versions for the same schemas.

Design decisions:

- The fingerprint is a hash of the tokens, not of the bound models. That is simple, covers every target equally, and needs no canonical form of each model, which would have to change with every model. The cost is that it is stricter than wire compatibility: it answers "were these generated from the same schemas?", not "can these read each other's buffers?".
- Each file's tokens are hashed separately, each token as its kind, its length and its text (a string literal's value), and the files' digests are hashed in sorted order. Leaving paths out keeps it independent of where the schemas are and of the path separator. The CLI parses files under their full paths, and the generator under whatever paths MSBuild gives.
- The hash is SHA-256, cut to 128 bits: short enough to send in every handshake, and far too long for two different schema sets to collide by accident. It isn't a security measure. The front end implements SHA-256 itself, because .NET's uses OpenSSL on Linux, which the Native AOT CLI would then need installed, and the front end takes no NuGet dependencies (CONVENTIONS.md).
- Bequadro's version isn't part of it, so programs built with different versions agree unless the way it is computed changed.
- In C#, one class per compilation in the global namespace gives the shortest name, `BequadroSchema.Fingerprint`, and a generated class needs no runtime support. Because the name is fixed, code that can see the classes of two assemblies has to tell them apart: a project that references two assemblies with schemas gets error CS0433 where it uses the name, and a project with schemas that references one gets warning CS0436 there. `extern alias` tells them apart.
- In TypeScript and Python it is a module of its own, not part of the runtime, whose content depends only on the CLI version and options (CLI-006), and not part of the root namespace's module, which exists only when the root namespace has declarations, and whose names would compete with theirs.
