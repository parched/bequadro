# Diagnostics Requirements

## DIA-001 Reporting

Acceptance criteria:

- Every problem with a schema is reported as a Roslyn diagnostic with severity **Error**. The CLI reports the same diagnostics in its own format (CLI-004).
- Each diagnostic's location is in the `.fbs` file: the file path plus the line and column span of the offending token or element. IDEs and the command line can then link straight to it.
- Messages state the problem and name the elements involved (file, type, field), so the fix is clear without reading generator source.
- The source generator still generates code when the schema set has errors, for everything that isn't in error. (The CLI writes nothing when there are errors, CLI-004.) Left out are:
  - a field, enum value or union member that is in error;
  - a declaration that is left with nothing to generate: an enum without a valid underlying type, a struct without valid fields or a union without valid members;
  - everything that refers to what was left out, such as a field of a struct that isn't generated.
- The generated code compiles without errors or warnings whatever errors the schema set has. The only C# errors are in consumer code that uses what was left out.
- The generator never throws for any input text. Any malformed input produces diagnostics.
- Deep input doesn't overflow the stack or take time or memory that grows with the square of its depth, such as a chain of 30,000 structs each containing the next, or a namespace of 100,000 segments. Nothing recurses once per level of the input, except vectors, which can only nest two deep (SCH-009).

Design decisions:

- While a schema is being edited, it is often briefly invalid. Generating nothing would remove every generated type, and every C# file that uses one would show errors until the schema is fixed. Leaving out only what is in error keeps the C# errors to code that uses it. The build still fails on the schema errors, so partly generated code is never shipped.
- Leaving out what refers to an omitted part, instead of generating it with a placeholder, is what keeps the generated code compiling.
- The parser assembles qualified names incrementally to avoid repeatedly copying the whole name as its depth grows.

## DIA-002 Diagnostic Catalog

IDs are stable. Never renumber an ID or reuse a removed one.

| ID | Title | Raised when |
| --- | --- | --- |
| BQ1001 | Syntax error | An unexpected token or character appears. The message says what was expected. |
| BQ1002 | Unterminated literal or comment | A string literal or block comment is not closed. |
| BQ1003 | Invalid numeric literal | A number is malformed or out of range for 64-bit representation. |
| BQ1004 | Include after declarations | An `include` or `native_include` appears after another statement. |
| BQ2001 | Included file not found | An include does not resolve to a file in the schema set, or resolves to more than one ignoring case and to none exactly (SCH-003). |
| BQ2002 | Duplicate declaration | Two declarations have the same fully qualified name. |
| BQ2003 | Unknown type | A type reference does not resolve. |
| BQ2004 | Type not included | A type reference resolves to a declaration in a file that is not the current file or one it includes, directly or transitively. |
| BQ2005 | Undeclared attribute | A non-built-in attribute is used without an `attribute` declaration before it (SCH-011). |
| BQ2006 | Duplicate member name | A field, enum value or union member name repeats within a declaration. |
| BQ2007 | Invalid default value | A default doesn't match the field type, is out of range, or isn't a plain literal (SCH-010). |
| BQ2008 | Invalid required field | `required` is used on a scalar or enum field, or on a struct field. |
| BQ2009 | Invalid struct field type | A struct field is not a scalar, enum or struct. |
| BQ2010 | Empty struct | A struct has no fields. |
| BQ2011 | Recursive struct | A struct contains itself, directly or indirectly. |
| BQ2012 | Invalid enum | The underlying type is missing or not an integer, or values are not ascending, unique and in range. |
| BQ2013 | Invalid union | A member is not a table or struct, there are no members or more than 255, or discriminator values are invalid: not integer literals, not ascending, or outside 1 to 255. |
| BQ2014 | Enum field has no zero value | An enum field has no default and the enum doesn't define 0. |
| BQ2015 | Nested vector | A vector's element type is a vector. |
| BQ2016 | Default not allowed | A default is given for a non-scalar field or a struct field. |
| BQ2018 | Invalid root type | `root_type` doesn't name a table declared before it, by its full name or its name in the current namespace (SCH-002). |
| BQ2020 | Invalid Bequadro attribute | An attribute starting with `bequadro_` is unknown, is used on an element it isn't valid on (SCH-011), or is given a value. |
| BQ2021 | Struct too large | A struct is larger than 65,520 bytes (SCH-006). |
| BQ2022 | Declaration order | A type is used where `flatc`, which reads a file from top to bottom, hasn't read it yet or would resolve the name to another declaration (SCH-012). |
| BQ2023 | Field name clash | A field has a name `flatc` rejects although no other field has it: that of a union field's hidden `_type` field, one `flatc` generates for another field, or that of a table or struct (SCH-005). |
| BQ2024 | Duplicate schema file | Two paths in the schema set normalize to the same path. The second is ignored (SCH-003). |
| BQ2025 | Deprecated struct field | A struct field has the `deprecated` attribute, which only table fields can have (SCH-006). |
| BQ3001 | Unsupported feature | A valid FlatBuffers feature that Bequadro doesn't implement is used. The message names the feature. |
| BQ4001 | Generated name conflict | Removed: names are renamed instead (NAM-001). |
| BQ4002 | Ambiguous union members | Two union members have the same type. `flatc` accepts this, but C# and Python select union members by type (NAM-001). |

BQ2017 and BQ2019 are unassigned.
