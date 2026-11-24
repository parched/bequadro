# Naming Requirements

How schema names become names in generated code, in every target. The target-specific parts are in API-002 (C#), TS-003 (TypeScript) and PY-003 (Python).

## NAM-001 Names Are Never Rejected

Acceptance criteria:

- No schema is rejected because of what it names its declarations, fields, enum values, union members or namespaces. Every name `flatc` accepts generates working code in every target. Names that a target can't use as written are renamed in the generated code (NAM-002).
- Renaming never produces a diagnostic.
- The only name-related errors are those `flatc` reports too, such as duplicate names (BQ2002, BQ2006).

Design decisions:

- `flatc` renames per language rather than rejecting names, and a schema is shared by every language that uses it. If a target could reject names, adding a target could break a schema that already worked, and the author would have to rename a field on the wire-visible schema to suit one language.
- BQ4002 (two union members of one type) remains. It isn't about names: C# selects a union's members by type (API-006), and a Python union is a union of types (PY-007), so the two members couldn't be told apart.

## NAM-002 Renaming

Acceptance criteria:

- Each target first maps a schema name to a candidate: C# and TypeScript change the case of field names, and C# escapes keywords with `@`, which isn't a rename because `@class` is the identifier `class`.
- A candidate that starts with a digit, such as `1` from the field `_1` in C#, gets a leading `_`.
- A candidate that the target can't use as written (API-002, TS-003, PY-003 list these) gets a trailing `_`, repeatedly, until it can be used. For example, the Python field `class` becomes `class_`.
- Within each scope, candidates claim their names in a fixed order, and one whose name is already taken gets a trailing `_`, repeatedly, until it is free. A name that is free but unusable, or usable but taken, keeps getting underscores until it is both.
  - Scopes are: the members of one type; the enum values of one enum; the declarations and derived type names of one namespace; and the files and directories of the output, compared ignoring case.
  - The order is: first the names of the target's fixed API (such as C#'s `Serializer`, or TypeScript's runtime module `bequadro` and root namespace module `index`), which are never renamed; then names written in the schema; then names Bequadro derives from them (such as TypeScript's `MonsterInit` for table `Monster`); within each group, order of first appearance. Declarations appear in the order of their files' normalized paths, ordinally, then of their positions. Members appear in declaration order. A namespace appears where its first declaration does, counting the declarations of the namespaces inside it.
- When a generated name differs from the schema name by more than the target's case conversion, the generated documentation says so, for example "Schema name: `class`". A name that comes out as written, such as the C# field `_1`, whose candidate `1` gets its `_` back, has no such note. A declaration in a namespace that was renamed has one too, giving its full schema name, such as "Schema name: `System.Foo.T`" for the C# type `System_.Foo.T`.
- The schema name is unchanged on the wire. Renaming can't change any buffer, because FlatBuffers stores no names.

Design decisions:

- A trailing underscore is the convention for this in Python (PEP 8), and what `flatc` does.
- Schema names win over derived ones, because the author can see what they wrote but not what Bequadro derives. A table named `MonsterInit` keeps its name, and table `Monster`'s input type becomes `MonsterInit_`.
- Declaration order makes renaming stable as schemas evolve. New fields are added at the end of a table, because a field's position is its id on the wire, so an existing field never changes name when a new one collides with it. Only the newcomer is renamed.
- Declarations and namespaces in different files are ordered by path, so adding a file can rename a name in a file after it. That needs a collision across files, such as two namespaces that differ only in case, or a type and a namespace with the same full name, which no real schema set should have.
