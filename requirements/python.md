# Python Requirements

How the CLI ([cli.md](cli.md)) maps schema declarations to Python, and how the Python runtime behaves. The mapping is fixed by convention.

## PY-001 Supported Environments

Acceptance criteria:

- Generated code and the runtime run on CPython 3.12 or later, and use only the standard library.
- They pass `pyright` in `strict` mode and `mypy --strict` without errors. Generated modules start with `# pyright: reportPrivateUsage=false, reportUnusedClass=false`, because schema names may start with an underscore, which makes them private to pyright, while generated code has to use them across classes and modules.
- Every generated module starts with `from __future__ import annotations`.

Design decisions:

- 3.12 is the oldest version with the `type` statement (PEP 695), which unions use (PY-007), and with `collections.abc.Buffer`. Every version before it is out of support or close to it.
- Postponed annotations allow forward references between classes, and between namespace modules in an import cycle, without quoting them.

## PY-002 Files and Modules

Acceptance criteria:

- The output directory is a package. Namespace `a.b.c` is the package `a/b/c/__init__.py` under it. The root namespace's declarations are in the module `bequadro_root.py`, and the output directory's own `__init__.py` re-exports them, so they are imported from the package itself. Every package on the way gets an `__init__.py`, so intermediate namespaces are importable even when they have no declarations. Namespace segments are used as written.
- The runtime is the module `bequadro.py` in the output directory (CLI-006). It exports the public runtime API: `write`, `parse` (PY-011), the error classes, `Table`, `Struct`, `IntEnum` and `UnionUnknown`.
- The module `bequadro_schema.py` in the output directory holds the schema set's fingerprint as `FINGERPRINT` (SER-015).
- Generated modules import the runtime and each other only with relative imports, so the output directory can be placed in any package. With `--out src/app/schema`, namespace `MyGame` is imported as `app.schema.MyGame`.
- Namespace packages may import each other in cycles. Importing any of them first works whatever the cycles. Each module defines its enums before it imports other namespaces, so that the module imported first already has the enums that class bodies use as defaults.

Design decisions:

- Each namespace is a package rather than a module, because a namespace can contain both declarations and nested namespaces.
- The root namespace's declarations have a module of their own, because a package can't import its own `__init__.py` relatively, and other namespaces need to import them.
- Relative imports, so generated code doesn't need to know the name of the package it is placed in.

## PY-003 Naming

Acceptance criteria:

- Table, struct, enum and union names, field names, enum value names and namespace segments are used as written. Union member labels don't appear in the Python API (PY-007).
- Names that Python can't use as written are renamed (NAM-002). They are:
  - any name that is a Python keyword (`False`, `None`, `True`, `and`, `as`, `assert`, `async`, `await`, `break`, `class`, `continue`, `def`, `del`, `elif`, `else`, `except`, `finally`, `for`, `from`, `global`, `if`, `import`, `in`, `is`, `lambda`, `nonlocal`, `not`, `or`, `pass`, `raise`, `return`, `try`, `while`, `with` or `yield`). Soft keywords such as `match` and `type` are used as written;
  - a field, enum value or declaration whose name Python would mangle in a class body (it starts with `__` and doesn't end with `__`), or that is a special name (it starts with `__` and ends with exactly two underscores), and a namespace segment that is a special name;
  - an enum value that `enum` or type checkers reserve: `name`, `value`, names that start and end with a single `_`, and the public attributes of an `IntEnum` class or member, which type checkers report a member as overriding: `as_integer_ratio`, `bit_count`, `bit_length`, `conjugate`, `denominator`, `from_bytes`, `imag`, `is_integer`, `mro`, `numerator`, `real` and `to_bytes`. Those are the names without a leading underscore in `dir(int)`, `dir(enum.IntEnum)` and `dir(type)`, the same in every version from 3.12 to 3.15;
  - a field named `bool`, `int`, `float`, `str`, `bytearray`, `list` or `object`, or like its own type or a declaration in its own namespace that its type's fields refer to, because the class's annotations would then mean the field instead of the type;
  - a declaration named like any public name of Python's `builtins` module, in any version from 3.12 to 3.15, such as `eval`, `id`, `list` or `type`. Type checkers can resolve a reference to a declaration defined later in the module to the built-in instead;
  - a field, declaration or namespace segment that starts with `_bq` and doesn't end with `_`. Generated modules name their internals that way, including every parameter and local variable of generated functions;
  - a declaration or namespace segment named `annotations`, the name that `from __future__ import annotations` (PY-001) binds in every generated module. A subpackage named like it would be unreachable, because importing it from its parent package finds that binding first;
  - a top-level namespace named `bequadro`, `bequadro_root` or `bequadro_schema` ignoring case, and a declaration in the root namespace with one of those names. These are the names of the runtime module, the root namespace's module and the fingerprint's module, which the output package's `__init__.py` would otherwise replace when it re-exports the root namespace's declarations.
- A namespace's declarations and the namespaces directly inside it share one scope (NAM-002), because a subpackage is an attribute of its parent package once imported. The later of the two by NAM-002's order is renamed: with `namespace a; table b {}` first, namespace `a.b` becomes the package `a/b_`.
- The fixed name of a class is `__bequadro__`, the class attribute that holds its codec. It claims its name first (NAM-002).
- For example, the field `from` becomes `from_`, and with a field `from_` in the same table already, the later of the two by declaration order becomes `from__`. The field `__x` becomes `__x___`, and the table `eval` becomes `eval_`.
- Two namespaces whose package paths, or any of their directories, are equal ignoring case are renamed at the first segment that differs (NAM-002).

Design decisions:

- Names are used as written, as in C#. Schemas are usually written in snake_case for fields and PascalCase for types already, which is the Python convention.
- A trailing underscore is the PEP 8 convention for names that clash with keywords and built-ins.
- Every rule is satisfied by appending underscores, so renaming always ends: a name starting with `__` once it ends with three underscores, and one starting with `_bq` once it ends with one.
- Generated functions name their parameters and locals with `_bq` too (`_bq_w`, `_bq_p`, `_bq_f3`, and `_bq_self` and `_bq_other` in `__eq__`), rather than reserving the short names they would otherwise use. A declaration named like a local would be shadowed inside the function that refers to it, and the list of locals would grow with the generator, so reserving names would rename schema names for reasons a schema author can't see. No declaration can start with `_bq`, so none can collide with any generated name, now or later.
- The enum names are the attributes type checkers report, not every name that works at runtime: a member named `real` works at runtime but fails pyright and mypy (PY-001).

## PY-004 Tables

Given:

```fbs
table Monster { name: string; hp: short = 100; weapons: [Weapon]; equipped: Equipment; }
```

the package contains:

```python
@final
@dataclass(slots=True, kw_only=True, eq=False)
class Monster(_bq.Table):
    name: str | None = None
    hp: int = 100
    weapons: list[Weapon] | None = None
    equipped: Equipment | None = None
```

Acceptance criteria:

- A table is a `@final` dataclass with `slots=True` and `kw_only=True`, deriving from `bequadro.Table`. Each field is a mutable attribute, in declaration order.
- Defaults:
  - Scalar and enum fields default to their schema default, or to zero (`0`, `0.0`, `False`, the enum's 0 value) when there is none. An optional scalar (`= null`) is `int | None` (or `float | None`, `bool | None`, the enum or `None`) and defaults to `None`.
  - A field that isn't `required` defaults to `None`, and its annotation includes `| None`.
  - A `required` field has no default, so construction must give it.
  - With `--require-scalars-without-default`, scalar and enum fields without a default in the schema have no default either.
- Tables without `bequadro_value_equality` are dataclasses with `eq=False`, so `==` and `hash` are by identity. A table with the attribute gets value equality (PY-010).
- `repr` is the dataclass `repr`.
- A run of `///` comments becomes the class docstring, or a string literal right after the field (an attribute docstring, which editors show).

Design decisions:

- Dataclasses are the standard library's plain data type, so construction, `repr`, `dataclasses.replace` and `dataclasses.fields` work as Python developers expect, and type checkers understand them.
- Keyword-only, so adding a field to a schema never shifts positional arguments.
- `@final`, because, as in C# (API-005), a subclass would be silently dropped by writing and never produced by parsing.
- Python needs no separate input type (TS-004): construction already lets scalars with defaults be left out, and the instance is always complete.
- Serialization is in runtime functions, not methods, so no field name has to be reserved for it.

## PY-005 Structs

Acceptance criteria:

- A struct is a `@final` dataclass with `slots=True` and `kw_only=True`, deriving from `bequadro.Struct`. Each field is a mutable attribute, in declaration order, defaulting to zero. A nested struct field defaults to a new zero struct.
- Structs have value equality (PY-010).
- Assigning a struct doesn't copy it, so the same object in two places is one object in memory. Writing writes each occurrence (SER-003), and parsing returns a fresh object for each occurrence (SER-011).

Design decisions:

- Mutable, like C# structs (API-004) and TypeScript structs (TS-005), so that code can change struct fields in place.

## PY-006 Enums

Given `enum Color : ubyte { Red, Green = 3 }`, the package contains:

```python
class Color(_bq.IntEnum):
    Red = 0
    Green = 3
```

Acceptance criteria:

- An enum derives from the runtime's `IntEnum`, a subclass of `enum.IntEnum`, with every value written explicitly.
- Generated code refers to a member whose name starts with `_` by its value, `Color(0)`, because type checkers don't treat such names as members.
- Parsing a value the schema doesn't define is not an error (SER-009). It returns an instance of the enum class with that value, which equals the raw `int` but isn't one of the class's members. Code that matches on an enum should have a `case _` branch for values added by newer schemas.
- Such an instance's `name` is its value in decimal, a `str` as `enum` declares it: `Color(9).name` is `"9"`, its `repr` is `<Color.9: 9>`, and `str` and `format` give `9`, as for members.
- Writing accepts any `int` within the underlying type's range for an enum field.

Design decisions:

- `IntEnum` compares and converts like an `int`, as an enum on the wire does.
- Type checkers treat a `match` over every member as exhaustive, so the value from a newer schema isn't in the static type, as in C# and TypeScript (TS-006).
- The value as its name, because `name` is typed `str`, and it is what C#'s `ToString` gives for a value the enum doesn't define.

## PY-007 Unions

Given `union Equipment { Weapon, Armor: Shield }`, the package contains:

```python
type Equipment = Weapon | Shield | _bq.UnionUnknown
```

Acceptance criteria:

- A union is a type alias of its member types, plus `bequadro.UnionUnknown` unless the union is declared with `bequadro_exhaustive`.
- `UnionUnknown` is a frozen dataclass with one field, `discriminator: int`. It holds a discriminator the schema doesn't define. Writing it raises (SER-004).
- `NONE` is `None`:
  - A union field that isn't `required` is `Equipment | None = None`. That includes unions declared with `bequadro_exclude_none`, where an absent field is still valid.
  - A `required` union field is `Equipment`, with no default.
  - A vector of unions is `list[Equipment | None]`, or `list[Equipment]` for a union declared with `bequadro_exclude_none`.
- `match` with class patterns (`case Weapon(damage=d):`) selects members, and `assert_never` in a final `case _` makes type checkers check exhaustiveness, `case None` included.
- The writer takes the discriminator from the value's exact class. A value whose class isn't a member of the field's union raises `FlatBufferWriteError`. Member labels don't appear in the Python API.

Design decisions:

- Python classes carry their type at runtime, and class patterns are the idiomatic way to match, so union values are the members themselves, with no wrapper. That is the C# design too (API-006). Member types are unique within a union (BQ4002), so the class identifies the member.
- `NONE` is `None`, because type checkers already check `None` arms for exhaustiveness.

## PY-008 Vectors

Acceptance criteria:

- A vector of `T` is `list[T]`. A vector of `ubyte` is `bytearray`.
- Writing accepts a list or tuple for any vector except `[ubyte]`, and any bytes-like object, including a non-contiguous `memoryview`, for `[ubyte]`. A list or tuple of `int`s for `[ubyte]` raises `FlatBufferWriteError` (PY-011).
- Parsed vectors are fresh lists or `bytearray`s that don't refer to the input buffer (SER-010).

Design decisions:

- `bytearray`, not `bytes`, so parsed byte vectors are mutable like every other parsed value.
- `[ubyte]` takes bytes-like objects only, as its annotation `bytearray` says, so a type checker accepts exactly what writing accepts. A list of `int`s would be checked and packed one by one.

## PY-009 Type Mapping

| Schema type | Python type |
| --- | --- |
| `bool` | `bool` |
| integer types (and sized aliases) | `int` |
| `float`, `double` (`float32`, `float64`) | `float` |
| `string` | `str` |
| enum `E` | `E` (an `IntEnum`) |
| struct `S` | `S` |
| table `T` | `T` |
| union `U` | `U` (a type alias) |
| `[X]` | `list[X]` |
| `[ubyte]` | `bytearray` |

Optionality and defaults follow PY-004.

## PY-010 Value Equality

Acceptance criteria:

- Structs, and tables declared with `bequadro_value_equality`, get a generated `__eq__`, which follows API-004 and API-005:
  - floats compare as `a == b or (a != a and b != b)`, so `NaN` equals `NaN`, as in C#. A `float` field (32-bit) compares its values rounded to 32 bits, as they are written, so `T(f=0.1)` equals the `T` that writing and parsing it gives, as in C#, where the field holds the rounded value;
  - vectors are equal when both are `None`, or when they have the same length and equal elements, compared as fields of their type are. A list equals a tuple with the same elements, since writing accepts both (PY-008). `[ubyte]` compares bytes, so a `bytearray` equals `bytes` or a `memoryview` with the same bytes. `None` isn't equal to an empty vector;
  - a table field compares with that table's `__eq__`, which is by identity unless the table has value equality;
  - a union field compares its values: equal when both are `None`, when both are the same member with equal values, or when both are `UnionUnknown` with equal discriminators.
- `__eq__` returns `NotImplemented` for an object of another type.
- These classes set `__hash__ = None`, so they are unhashable.
- Tables without the attribute keep identity `==` and `hash`, so parsed instances can key dictionaries and sets (SER-011).

Design decisions:

- A generated `__eq__` instead of the dataclass one, which compares fields as tuples. Two separately parsed `NaN`s are different objects that compare unequal there, which would contradict C#.
- Unhashable, which is the Python convention for mutable value types, unlike C#, where value-equal tables can be dictionary keys (API-005). Changing an object after using it as a key would make it unfindable, and list fields aren't hashable anyway.

## PY-011 Serialization

Acceptance criteria:

- The runtime module exports:
  - `write(value: Table, /, *, share_strings: bool = False) -> bytes`, which serializes `value` as the root table and returns exactly the buffer, and raises `TypeError` if `value` isn't an instance of a generated table;
  - `parse(table_type: type[T], data: Buffer, /, *, share_strings: bool = False) -> T`, which parses a buffer whose root is a `table_type`, from any bytes-like object, `memoryview` slices included.

  `share_strings` turns on string sharing (SER-003, SER-011).
- The runtime meets SER-002 to SER-013, with these substitutions:
  - `InvalidDataException` is `FlatBufferDataError`, and `InvalidOperationException` is `FlatBufferWriteError`. Both are exported by the runtime and derive from `ValueError`.
  - `null` is `None`.
  - Identity is object identity: one table object is written once, and one table position parses as one object. This holds for tables with value equality too.
  - The size limit of SER-012 is the same number, 2,147,483,591 bytes.
  - Nesting up to the limit of SER-003 and SER-009 is written and parsed without `RecursionError`, whatever `sys.getrecursionlimit()` is and however deep the caller's stack already is. To do so, `write` and `parse` raise the recursion limit, for the rest of the process, to the current stack depth plus 4 frames for each level of nesting plus a margin of 1000, if it is lower. They never lower it.
  - Once `parse` returns or raises, it holds no export of `data`'s buffer, so a `bytearray` can be resized and an `mmap` closed even while the exception is alive. The same holds for a `[ubyte]` value once `write` returns or raises. A buffer that isn't C-contiguous, such as a `memoryview` slice with a step, is copied.
- The writer also checks values that type annotations can't enforce at runtime, and raises `FlatBufferWriteError` naming the table and field (with the index of a vector element) for:
  - a value that isn't an instance of the field's type. An `int` is accepted for a float field, and a `bool` for an integer field, as Python's numeric types allow;
  - an integer or enum value out of the field's range, however large, and an `int` too large to convert to `float` for a float field;
  - an object that isn't an instance of the table class a field or vector expects, including a subclass of it.

  No other exception type comes out of `write` for a bad value.
- A `float` field is written as the nearest 32-bit float, and values beyond its range become infinite, as in C#.
- A string is written as .NET writes the UTF-16 string with the same code units, instead of raising `UnicodeEncodeError` for surrogates: a high surrogate followed by a low one is written as the character they encode together (`chr(0xD83D) + chr(0xDE00)` as `F0 9F 98 80`, as for `"\U0001F600"`), and any other surrogate as U+FFFD. Strings without surrogates pay nothing for this.
- NaN bits:
  - A `double` NaN is written with exactly the bits of the Python `float`, so parsing then writing keeps any NaN's sign and payload. `math.nan` and `float("nan")` are the quiet NaN with the sign bit clear (`000000000000F87F` little-endian), while .NET's `double.NaN` has it set (`...F8FF`), so a NaN Python creates is written with different bits from one C# creates.
  - A `float` (32-bit) NaN is converted to and from a Python `float` by CPython, which keeps its sign and payload, so parsing then writing keeps a quiet NaN's bits. A signalling NaN may become quiet (its quiet bit set): CPython 3.12, 3.13 and 3.15 convert in hardware, which quiets it, while 3.14 keeps it signalling.
- `parse` raises `TypeError` if `table_type` isn't a generated table class.

Design decisions:

- Functions in the runtime, not methods on each class, so no field name is reserved for them (PY-004), and one import serves every table.
- Errors derive from `ValueError`, as the standard library's errors for malformed data, such as `json.JSONDecodeError`, do.
- The recursion limit is raised rather than restored afterwards, because another thread may be inside `write` or `parse` at the same time, and restoring could lower the limit under it. Python 3.12 and later don't use the C stack for calls between Python functions, so a higher limit doesn't risk a crash. Counting the caller's frames costs one step per frame, which is small next to a write.
- A vector element's path is only formatted for an error, and a scalar vector whose elements are all of types that `struct` converts exactly as the checks above do is packed in one call, which checks the ranges in C. The checks and messages are the same either way.
- Surrogates are handled by an encoding error handler, so the common case is a single `str.encode`. A string with surrogates can only come from Python code, never from parsing, so the handler follows .NET instead of rejecting it.
- NaN bits are documented rather than normalized: normalizing would cost a check on every float, and change bits that parse-then-write otherwise keeps, while a signalling NaN in a 32-bit field is rare and its quiet bit is the only difference.
