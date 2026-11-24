# Schema Language Requirements

The subset of the [FlatBuffers schema language](https://flatbuffers.dev/schema/) that Bequadro accepts.

The guiding rule: **a schema that Bequadro accepts must also be accepted by `flatc`, and must mean the same thing on the wire.** Names are never a reason to reject a schema: names a target can't use are renamed in its generated code (NAM-001). Schemas are often also compiled by `flatc` for other languages. Bequadro may reject some valid `flatc` schemas, but it must never accept a schema that `flatc` rejects. The one exception is where checking would require semantics Bequadro doesn't implement; those cases are listed explicitly below.

What `flatc` accepts is taken from its parser, `src/idl_parser.cpp` in FlatBuffers v25.12.19. Where a requirement says what `flatc` does, it names the function that does it.

## SCH-001 Lexical Structure

Acceptance criteria:

- Source text is UTF-8. A leading byte order mark is ignored.
- Whitespace is space, tab, carriage return and line feed.
- Comments:
  - `// ...` runs to the end of the line: the next line feed or carriage return.
  - `/* ... */` may span lines and does not nest.
  - `/// ...` is a documentation comment. It must not follow a token on the same line, otherwise BQ1001 is reported. A line break inside a block comment doesn't count as the start of a line, and neither does the start of a file that begins with a byte order mark.
  - The `///` comments after the previous token become the documentation of the declaration, field, enum value or union member that follows, one line each, with surrounding whitespace removed. Blank lines between them don't separate them. Any other comment discards the documentation lines before it, and so does a line starting with `////`, so that lines of slashes aren't documentation.
- Identifiers match `[A-Za-z_][A-Za-z0-9_]*`. Qualified names are identifiers separated by `.`.
- String literals are delimited by `"` and support the escapes `\" \\ \/ \b \f \n \r \t \uXXXX`. Any other escape, a control character (U+0000 to U+001F, such as a tab), and a `\u` escape of a surrogate that isn't part of a pair produce BQ1001. A string may not span lines: a line feed before the closing `"` produces BQ1002.
- Integer literals are decimal (`123`, `-5`, `+5`) or hexadecimal (`0x1F`, `-0x1F`).
- Floating-point literals are decimal with an optional fraction and exponent (`1.0`, `-2.5e-3`, `.5`, `5.`), plus `nan`, `inf` and `infinity` in any case (`NaN`, `Infinity`).
- A sign must come directly before its number or floating-point name, with nothing between them, otherwise BQ1001 is reported: `- 5` and `-/* */5` are errors.
- `true` and `false` are boolean literals.
- A file must contain at least one statement. An empty file, or one with only comments, produces BQ1001.
- An unterminated string or block comment produces BQ1002. A malformed number produces BQ1003. Any other unexpected character produces BQ1001.

Design decisions:

- Each rule is `flatc`'s (`Parser::Next`). It ends a `//` comment at a carriage return, rejects a documentation comment after a token ("a documentation comment should be on a line on its own"), control characters and unpaired surrogates in strings, and an empty file ("input file is empty"). It reads a sign as part of the number that follows, and converts `nan`, `inf` and `infinity` with `strtod`, which ignores case.
- `flatc` also accepts strings in single quotes, `\x` escapes and hexadecimal floating-point literals. Bequadro doesn't: schemas rarely use them.
- What becomes documentation differs from `flatc` in two ways that can't change whether a schema is accepted: `flatc` keeps `////` lines, and keeps documentation lines that come before an ordinary comment.

## SCH-002 File Structure

A file is a sequence of the following statements, in any order except that `include` and `native_include` statements come first.

| Statement | Support |
| --- | --- |
| `include "path";` | Supported (SCH-003). Must appear before every statement other than `include` and `native_include`, otherwise BQ1004. |
| `native_include "path";` | Accepted and ignored. Must appear before every statement other than `include` and `native_include`, otherwise BQ1004. |
| `namespace a.b.c;` | Supported (SCH-004). |
| `attribute "name";` / `attribute name;` | Supported (SCH-011). |
| `table`, `struct`, `enum`, `union` | Supported (SCH-005 to SCH-009). |
| `root_type Name;` | Accepted and validated: it must name a table declared before it, by its full name or by its name in the current namespace (BQ2018). It has no other effect. |
| `file_extension "ext";` | Accepted and ignored. |
| `file_identifier "ABCD";` | BQ3001 (unsupported; it would change the buffer layout). |
| `rpc_service` | BQ3001. |

Acceptance criteria:

- A syntax error produces BQ1001, located at the offending token, and saying what was expected.
- After a syntax error the parser recovers at the next `;` or `}` at declaration level and keeps going, so one file can report several errors. Declarations that parsed successfully are still checked semantically, and code is generated for what isn't in error (DIA-001).

Design decisions:

- `flatc` reads `include` and `native_include` statements only at the start of a file (`Parser::DoParse`), and looks up `root_type` among what it has read so far, first by the name as written and then qualified by the current namespace (`Parser::SetRootType`). It would also accept a `root_type` naming a table that so far is only a forward reference (SCH-012); Bequadro requires the table itself to come first.

## SCH-003 Includes

Acceptance criteria:

- `include "relative/path.fbs";` resolves relative to the directory of the including file. `..` and `.` segments are allowed, and `/` and `\` are both accepted as separators. A `\` is written `\\`, because a backslash in a string starts an escape (SCH-001): `include "..\\shared\\a.fbs";`.
- Absolute include paths are resolved as given.
- Include paths are resolved lexically, then compared with the normalized full paths of the files in the schema set. An exact match is used if there is one. Otherwise a unique case-insensitive match is used, so includes written with the wrong case still work on case-insensitive file systems.
- If the resolved file is not in the schema set, or there is no exact match and more than one case-insensitive one, BQ2001 is reported at the include statement.
- There are no include search paths (`flatc -I`). Only relative and absolute paths work.
- Include cycles are allowed and harmless, as in `flatc`.
- Two paths in the schema set that normalize to the same path, such as `d/../a.fbs` and `a.fbs`, are one file. The second, in ordinal order of the paths as given, is ignored, and BQ2024 is reported at its start.

Design decisions:

- Relative includes are the common case. Search paths would need configuration, so they are left out.

## SCH-004 Namespaces

Acceptance criteria:

- `namespace a.b.c;` sets the namespace for the declarations that follow, up to the next `namespace` statement or the end of the file. A file may contain several namespace statements.
- `namespace;`, with no name, returns to the root namespace, as in `flatc` (`Parser::ParseNamespace`).
- Declarations before any namespace statement are in the root (global) namespace.
- The fully qualified name of a declaration is `namespace.Name`, or just `Name` in the root namespace.

## SCH-005 Tables

Syntax: `table Name (attributes)? { field* }`, where a field is `name : type (= default)? (attributes)? ;`.

Acceptance criteria:

- Field types may be any scalar, `string`, any enum, struct, table or union, or a vector `[T]` of any of these except another vector.
- Field names must be unique within the table (BQ2006).
- A field may not have a name that `flatc` rejects although no other field has it (BQ2023):
  - `u_type`, beside a union or union vector field `u`, because `flatc` adds a field of that name for the discriminator;
  - a name `flatc`'s code generators use for another field: `uType` beside a union field `u`, `v_length` or `vLength` beside a vector field `v` (including the discriminator vector of a union vector), and `s_byte_vector` or `sByteVector` beside a string field `s`;
  - the name of a table or struct that `flatc` finds when it looks the field's name up as a type (SCH-012), or of one it may find because its file may be read first.
- Field IDs (vtable slots) are assigned in declaration order starting at 0. A union field takes two consecutive slots: the `_type` discriminator first, then the value.
- `required` is valid only on non-scalar fields: strings, structs, tables, unions and vectors. Using it on a scalar or enum field produces BQ2008.
- Defaults (SCH-010) are valid only on scalar and enum fields. A default on any other field type produces BQ2016.
- A table may have no fields.
- `bequadro_value_equality` on the table declaration gives the generated table value equality instead of reference equality (API-005, TS-010, PY-010). It doesn't change the wire format.

Design decisions:

- The field names `flatc` rejects come from `Parser::ParseField`, which adds the `_type` field and rejects "field name can not be the same as table/struct name", and from `Parser::CheckClash`, called by `Parser::ParseDecl`. A table or struct that a field's name could refer to in another file counts whichever order `flatc` reads the files in, because that order depends on the file `flatc` is given.

## SCH-006 Structs

Syntax: `struct Name (attributes)? { field* }`.

Acceptance criteria:

- Field types are limited to scalars, enums and other structs (BQ2009). Strings, vectors, tables and unions are not allowed, and fixed-size arrays (`[T:N]`) produce BQ3001.
- A struct must have at least one field (BQ2010).
- A struct may not contain itself, directly or indirectly (BQ2011).
- A struct field may not have the name of a table or struct, as in a table (BQ2023, SCH-005).
- Struct fields may not have defaults (BQ2016) and may not be `required` (BQ2008).
- Layout follows the FlatBuffers rules: each field is aligned to its own alignment (scalars to their size, structs to their largest member alignment). Padding is inserted between fields as needed and at the end, so the size is a multiple of the struct's alignment.
- A struct may be at most 65,520 bytes (BQ2021).

Design decisions:

- 65,520 bytes is the largest struct that fits in a table as its only field. A table's inline data is at most 65,535 bytes, because vtable entries are 16-bit, and it also holds a 4-byte soffset and up to 4 bytes of padding before an 8-byte-aligned struct. A larger struct could only be used in vectors and unions, and the limit also keeps size arithmetic far from overflowing.

## SCH-007 Enums

Syntax: `enum Name : underlying_type (attributes)? { Value (= integer)?, ... }`. A trailing comma is allowed.

Acceptance criteria:

- The underlying type must be an integer scalar: `byte`, `ubyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` or their sized aliases (BQ2012). If no underlying type is given, BQ2012 is reported; `flatc` requires one too.
- The first value is 0 unless one is given. Each later value is the previous value plus 1 unless one is given.
- Values must be strictly ascending in declaration order, must fit the underlying type, and must be unique (BQ2012).
- Value names must be unique within the enum (BQ2006).
- An enum declared without values, `enum E : byte {}`, has the one value `NONE` = 0, as in `flatc` (`Parser::ParseEnum`).
- `bit_flags` produces BQ3001.

## SCH-008 Unions

Syntax: `union Name (attributes)? { Member, Label: Type, ... }`. A trailing comma is allowed.

Acceptance criteria:

- Each member is either a type name, whose label is the type's unqualified name, or `Label: Type`.
- A member type must be a table or a struct (BQ2013). Strings in unions produce BQ3001.
- Discriminator values start at 1 and increase by 1 in declaration order. An explicit `= n` must be an integer literal, and is accepted if it keeps the values strictly ascending, within 1 to 255. Anything else, such as `= "2"`, `= 0` or `= 256`, produces BQ2013. Implicit values must be at most 255 too, so a member without a value after one with `= 255` produces BQ2013.
- There must be at least one member, and there may be at most 255 (BQ2013).
- Member labels must be unique (BQ2006). A label of `NONE` produces BQ2006 too, because every union implicitly has the member `NONE` with discriminator 0, as in `flatc`.
- Labels must also be unique as `flatc` makes them: it labels a member written as a qualified type name, such as `a.T`, with that name with `_` for `.`, `a_T`. So `union U { a.T, a_T: b.T }` produces BQ2006, and so does `union U { a.T, b.T }`, whose labels are both `T`.
- Two members with the same type produce BQ4002. The generated C# API (constructors, implicit conversions, `TryGetValue`) and Python unions can't tell them apart. This is the one schema `flatc` accepts that Bequadro rejects because of its generated API (NAM-001).
- These attributes on the union declaration change its behavior (API-006, TS-007, PY-007, SER-004, SER-009):
  - `bequadro_exclude_none`: `NONE` is not a valid value.
  - `bequadro_exhaustive`: discriminators the schema doesn't define are rejected instead of read as unknown members.

Design decisions:

- Without attributes, a union behaves as FlatBuffers specifies: `NONE` is a valid value, and readers tolerate members added by newer schemas. The attributes opt in to strictness, so the default never surprises someone who knows FlatBuffers.
- A member written as a qualified name is labelled with its unqualified name, which reads better in generated code than `flatc`'s `a_T`, and which Bequadro has always used. Requiring both labels to be unique keeps every accepted schema valid for `flatc` (`Parser::ParseEnum`, with `union_value_namespacing` on, its default) without renaming existing members. The cost is rejecting `union U { a.T, b.T }`, which `flatc` accepts; label the members to use both.
- `flatc` requires an integer token after `=` (`Parser::ParseEnum`), and rejects a value of 0 as a duplicate of `NONE`. It doesn't require ascending values, but Bequadro does, as for enums.

## SCH-009 Vectors

Acceptance criteria:

- `[T]` is allowed as a table field type for any T except another vector (BQ2015).
- A vector type nested more than two deep, such as `[[[int]]]`, is a syntax error (BQ1001), so no schema can make the parser recurse deeply.
- A vector of unions is supported. On the wire it is a `[ubyte]` discriminator vector in the `_type` slot plus a vector of offsets in the value slot.

## SCH-010 Default Values

Acceptance criteria:

- Scalar fields may have a numeric default. Integer fields accept only integer literals within the type's range. Bool fields accept `true`, `false`, `0` or `1`. A default written as a string, such as `"true"` or `"5"`, produces BQ2007 for every scalar type.
- Floating-point fields accept integer or floating-point literals, including `nan`, `inf` and `infinity` in any case. A hexadecimal integer produces BQ2007. A literal is rounded once to the nearest value of the field's type, ties to even: one too large for the type is infinity with the literal's sign, and one too small for it is zero with its sign.
- A default of `-0.0` or `-0` is kept as negative zero: an absent field reads as `-0.0`, and the generated code of every target starts the field at `-0.0`. How a written value is compared with it is in SER-004.
- Anything else produces BQ2007.
- Enum fields may name an enum value by its identifier, or use an integer that is one of the enum's values. Any other value produces BQ2007.
- `= null` on a scalar or enum field makes it an *optional scalar*: absence is distinguishable from every value (SER-006). It produces BQ2007 for a field of an enum with a value named `null`.
- Without a default, a scalar field defaults to 0 or `false`. An enum field defaults to the value 0, and if the enum has no value equal to 0 and no explicit default is given, BQ2014 is reported. With `BequadroRequireScalarsWithoutDefault`, such a field is also a `required` C# property (API-005), and with the CLI's `--require-scalars-without-default` it must be given when constructing (TS-004, PY-004), but its default on the wire is unchanged.

Design decisions:

- `flatc` converts a default with `strtod` or `strtof` (`Parser::ParseSingleValue`, `atot`), so a `float` default is rounded straight from the literal, not through a `double`, and negative zero keeps its sign. Bequadro converts literals exactly with integer arithmetic instead of `double.Parse`, because the generator also runs on .NET Framework, whose `double.Parse` rejects a number too large for a `double` and doesn't always round correctly.
- `flatc` also accepts a scalar default written as a string, such as `"true"` or `"5"`, and an integer in any form that fits, such as `0x1` for a bool. Bequadro accepts only the plain literals: they are what schemas use, and SCH-010 can list them all.
- `flatc` rejects a hexadecimal integer as a floating-point default unless it has a binary exponent (`Parser::TryTypedValue`), and rejects `null` on a field of an enum with a value named `null` (`Parser::ParseField`).

## SCH-011 Attributes

Attributes appear in parentheses after a declaration name, field type or enum value: `(name, name: value, ...)`. A value is a string literal or an integer literal that fits in a 32-bit signed integer. Any other value, such as an identifier, `true`, `-inf` or `1.5`, produces BQ1001.

Acceptance criteria:

- `required` is built in and supported (SCH-005). On a declaration, an enum value or a union member it is accepted and has no effect.
- Attributes starting with `bequadro_` are Bequadro's own, and take no value. `bequadro_exclude_none` and `bequadro_exhaustive` are valid on union declarations (SCH-008), and `bequadro_value_equality` on table declarations (SCH-005). They need no `attribute` declaration, but may have one, which `flatc` requires. Any other `bequadro_` attribute, one used on any other element, or one given a value, produces BQ2020.
- These built-in `flatc` attributes would change the wire format or semantics, and produce BQ3001: `id`, `deprecated`, `key`, `bit_flags`, `force_align`, `nested_flatbuffer`, `flexbuffer`, `hash`, `original_order`, `offset64` and `vector64`.
- These built-in `flatc` attributes have no effect on generated code, and are accepted and ignored without being declared: `native_inline`, `native_default`, `native_custom_alloc`, `native_type`, `native_type_pack_name`, `cpp_type`, `cpp_ptr_type`, `cpp_ptr_type_get`, `cpp_str_type`, `cpp_str_flex_ctor`, `shared`, `csharp_partial`, `streaming`, `idempotent`, `private`.
- Any other attribute must be declared with an `attribute` statement before it is used: earlier in the same file, or in a file it includes, directly or transitively, that isn't in an include cycle with it. Otherwise BQ2005 is reported.
- Declared (user) attributes are accepted on any element and have no effect on generated code.

Design decisions:

- `flatc` reads a file's includes before the file, and reports an attribute it hasn't read a declaration of yet ("user define attributes must be declared before use", `Parser::ParseMetaData`). Of two files in an include cycle, it reads the one it isn't given first, so which comes first depends on the file it is given.
- `flatc` reads an attribute value with no type to convert it to (`Parser::ParseSingleValue`), so it accepts only a string, or an integer token, which it checks as a 32-bit integer.
- `flatc` checks `required` only on fields, so it accepts it anywhere else. Bequadro accepts it too, as it does the other built-in attributes that have no effect: rejecting it would gain nothing.
- FlatSharp configured C# mapping through `fs_*` attributes. Bequadro uses fixed conventions instead (see [generated-api.md](generated-api.md)), so `fs_*` attributes are only user attributes. Schemas should drop them.
- User attributes are not emitted as metadata in any language: nothing reads them, and emitting them bloats the generated code.
- All Bequadro attributes share the `bequadro_` prefix, so they can't clash with other tools' attributes, and a misspelled one is an error instead of being ignored as a user attribute.

## SCH-012 Type References and Visibility

Acceptance criteria:

- A type reference `A.B` written inside namespace `x.y.z` is resolved like `flatc` does. The candidates are `x.y.z.A.B`, `x.y.A.B`, `x.A.B` and `A.B`, in that order. The first that is an enum or union wins, and only if none is, the first that is a table or struct. So an enum in an enclosing namespace wins over a table with the same name in the current one. A union member written without a label is looked up the other way round: tables and structs first.
- If no candidate exists, BQ2003 is reported.
- The resolved declaration must be declared in the referencing file or in a file it includes, directly or transitively. Otherwise BQ2004 is reported, even though the type exists somewhere in the schema set. This keeps schemas valid for `flatc`, which only sees included files.
- `flatc` reads a file from top to bottom, after the files it includes, and resolves each name among the declarations it has read so far. A reference that it would resolve to another declaration, or not at all, produces BQ2022. So:
  - an enum or union must be declared before it is used;
  - a struct must be declared before a struct that contains it;
  - a table or struct used before it is declared is accepted when `flatc` resolves it to that declaration, as it does when the reference is written as the declaration's name or full name and no other table or struct the name could refer to comes first;
  - a declaration in a file in an include cycle with the referencing file counts as declared after the reference, because `flatc` may read that file second.
- Scalar type names are reserved and always mean the scalar type: `bool`, `byte`, `ubyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `int8`, `uint8`, `int16`, `uint16`, `int32`, `uint32`, `int64`, `uint64`, `float32`, `float64` and `string`.

Design decisions:

- `flatc`'s `Parser::ParseTypeIdent` looks for an enum or union through every enclosing namespace (`Parser::LookupEnum`) before it looks for a table or struct (`Parser::LookupCreateStruct`). An enum it hasn't read yet is invisible to it: the name becomes a forward reference to a table that is never declared ("type referenced but not defined").
- A forward reference is how `flatc` lets tables refer to each other. `Parser::LookupCreateStruct` records it under the name as written, and the next table or struct declared with that name, or with that full name, takes it over, in whatever namespace it is (`Parser::StartStruct`). Bequadro replays these lookups for each file, so it reports a forward reference that `flatc` would resolve to a different declaration, or leave unresolved.
- Which file `flatc` reads first in an include cycle depends on the file it is given. Bequadro reports anything whose meaning depends on that order.
- Resolution stops at the first declared candidate, even if it isn't visible and an outer candidate is. `flatc` would resolve to the outer one, because it never sees the file the inner one is in. But then adding an include could silently change which type a reference means. Reporting BQ2004 makes the author qualify the name instead.
