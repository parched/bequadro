# Bequadro Requirements

Bequadro is a C# incremental source generator, plus a small runtime library, that turns FlatBuffers schema files (`.fbs`) into C# types that serialize to and from the FlatBuffers binary format. A command-line tool generates TypeScript and Python from the same schemas, with the same semantics.

This document and the files it links to are the specification. Someone given only these documents should be able to build a functionally equivalent replacement. Implementation details live in [ARCHITECTURE.md](ARCHITECTURE.md), and usage documentation lives in [README.md](README.md).

## Background and Goals

Bequadro is an alternative to [FlatSharp](https://github.com/jamescourtney/FlatSharp) for code that works with whole object graphs. It covers the parts of the FlatBuffers schema language that most schemas use, not all of it. It differs from FlatSharp in that it:

- parses schemas itself, so no `flatc` executable or external compiler process is needed;
- runs as a Roslyn incremental source generator, not an MSBuild task;
- offers a single, opinionated way of mapping schemas to C#, controlled by convention instead of attributes;
- preserves object-graph identity: shared table instances are written once and come back shared when read.

The CLI brings the same model to TypeScript and Python, where `flatc` generates lazy, buffer-backed accessors: plain, greedy, mutable objects, identity preservation, and unions that the type checker narrows and checks for exhaustiveness.

### Non-Goals

- Supporting the whole FlatBuffers schema language. Unsupported features produce a diagnostic and are never silently ignored when they affect the wire format ([diagnostics](requirements/diagnostics.md)).
- Lazy or progressive deserialization, or buffer-backed objects. All parsing is greedy and mutable, in every language.
- Generating languages other than C#, TypeScript and Python.
- gRPC services, FlexBuffers, JSON conversion, reflection (`.bfbs`), and object pooling.
- Byte-for-byte identical output to FlatSharp or `flatc`. The output must be valid FlatBuffers that other implementations can read, but its layout is Bequadro's own. Bequadro's own writers do agree byte for byte (SER-005).
- Running the C# runtime on big-endian platforms.

## Requirement Documents

| Area | Document | ID prefix |
| --- | --- | --- |
| Packaging, inputs and consumer setup | [requirements/consumption.md](requirements/consumption.md) | `CON` |
| Schema language subset | [requirements/schema-language.md](requirements/schema-language.md) | `SCH` |
| Generated C# API | [requirements/generated-api.md](requirements/generated-api.md) | `API` |
| Serialization and the wire format | [requirements/serialization.md](requirements/serialization.md) | `SER` |
| Diagnostics | [requirements/diagnostics.md](requirements/diagnostics.md) | `BQ` (diagnostic IDs) |
| Incrementality and performance | [requirements/incrementality.md](requirements/incrementality.md) | `INC` |
| Quality and testing | [requirements/quality.md](requirements/quality.md) | `QUA` |
| Naming in every target | [requirements/naming.md](requirements/naming.md) | `NAM` |
| Command-line tool | [requirements/cli.md](requirements/cli.md) | `CLI` |
| Generated TypeScript and its runtime | [requirements/typescript.md](requirements/typescript.md) | `TS` |
| Generated Python and its runtime | [requirements/python.md](requirements/python.md) | `PY` |

Each requirement has a stable ID, a statement, acceptance criteria, and, where relevant, the design decisions behind it. When a requirement changes, edit it in place and keep its ID. When one is removed, mark it as removed and don't reuse its ID.

## Glossary

- **Schema set**: all `.fbs` files given to one compilation as `AdditionalFiles`, or to one run of the CLI as inputs (CLI-003).
- **Target**: the language code is generated for. The source generator's target is C#. The CLI's is TypeScript or Python.
- **Declaration**: a `table`, `struct`, `enum` or `union` in a schema.
- **Offset types**: tables, strings, vectors and unions. These are stored out of line and referenced by a 32-bit unsigned offset (uoffset).
- **Inline types**: scalars, enums and structs, stored directly inside their container.
- **Scalar**: `bool`, the integer types and the floating-point types.
- **Identity preservation**: within one serialization or one parse, each distinct table *object* maps to exactly one table in the buffer, and each distinct table *in the buffer* maps to exactly one object.
