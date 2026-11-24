// QUA-003: the TypeScript runtime and generated code, mirroring the C# runtime tests (TS-004 to TS-011).
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { runInNewContext } from "node:vm";
import { FlatBufferDataError, FlatBufferWriteError, write, Writer } from "../generated/bequadro.ts";
import * as Common from "../generated/Bequadro_/Tests/Common.ts";
import * as Equality from "../generated/Bequadro_/Tests/Equality.ts";
import * as Kitchen from "../generated/Bequadro_/Tests/Kitchen.ts";
import * as Shapes from "../generated/Bequadro_/Tests/Shapes.ts";

const fixtures = fileURLToPath(new URL("../../../artifacts/cross-language/", import.meta.url));

function minimalSink(): Kitchen.SinkInit {
  return {
    title: "minimal",
    requiredPosition: { x: 1, y: 2, z: 3 },
    requiredShape: { kind: "Circle", value: { center: { x: 0, y: 0, z: 0 } } },
    requiredChildren: [],
  };
}

test("a literal round trips, with left-out scalars reading as their defaults", () => {
  const parsed = Kitchen.Sink.parse(Kitchen.Sink.write(minimalSink()));

  assert.equal(parsed.title, "minimal");
  assert.equal(parsed.name, undefined);
  assert.equal(parsed.count, 42);
  assert.equal(parsed.flagDefaultTrue, true);
  assert.equal(parsed.wide, -9223372036854775808n);
  assert.equal(parsed.unsignedWide, 18446744073709551615n);
  assert.ok(Number.isNaN(parsed.notANumber));
  assert.equal(parsed.infinite, -Infinity);
  assert.equal(parsed.color, Common.Color.Blue);
  assert.equal(parsed.optionalInt, undefined);
  assert.deepEqual(parsed.requiredPosition, { x: 1, y: 2, z: 3 });
  assert.deepEqual(parsed.requiredShape, { kind: "Circle", value: { center: { x: 0, y: 0, z: 0 }, radius: 1 } });
  assert.deepEqual(parsed.requiredChildren, []);
});

test("parsed objects have every field as an own property, in declaration order", () => {
  const parsed = Kitchen.Sink.parse(Kitchen.Sink.write(minimalSink()));
  const created = Kitchen.Sink.create({
    title: "t",
    requiredPosition: Common.Vec3.create(),
    requiredShape: { kind: "Marker", value: { id: 1 } },
    requiredChildren: [],
  });

  assert.equal(Object.getPrototypeOf(parsed), Object.prototype);
  assert.deepEqual(Object.keys(parsed), Object.keys(created));
  assert.equal(Object.keys(parsed)[0], "name");
  assert.equal(created.count, 42);
  assert.equal(created.name, undefined);
  assert.ok("name" in created);
});

test("scalars equal to their defaults are not written", () => {
  const withDefaults = { ...minimalSink(), count: 42, flagDefaultTrue: true, small: -3, ratio: 0.5, wide: -9223372036854775808n };
  assert.deepEqual(Kitchen.Sink.write(withDefaults), Kitchen.Sink.write(minimalSink()));
});

test("-0 equals a zero default and isn't written, and NaN is written unless the default is NaN", () => {
  const zero = Kitchen.Sink.write({ ...minimalSink(), precise: 0.925, ratio: 0.5, medium: 0 });
  assert.deepEqual(Kitchen.Sink.write({ ...minimalSink(), notANumber: NaN }), Kitchen.Sink.write(minimalSink()));
  assert.notDeepEqual(Kitchen.Sink.write({ ...minimalSink(), precise: NaN }), zero);
});

test("a -0 default reads as -0, and a zero of either sign is equal to a zero default and isn't written", () => {
  assert.ok(Object.is(Kitchen.SignedZero.create().negative, -0));
  const parsed = Kitchen.SignedZero.parse(Kitchen.Empty.write({}));
  assert.ok(Object.is(parsed.negative, -0));
  assert.ok(Object.is(parsed.negativeFloat, -0));
  const defaults = Kitchen.SignedZero.write({});
  assert.deepEqual(Kitchen.SignedZero.write({ negative: 0, negativeFloat: 0, positive: -0 }), defaults);
  assert.ok(Object.is(Kitchen.SignedZero.parse(defaults).positive, 0));
  assert.equal(Kitchen.SignedZero.parse(Kitchen.SignedZero.write({ negative: 1.5 })).negative, 1.5);
});

test("float fields are written at 32-bit precision", () => {
  const parsed = Kitchen.Sink.parse(Kitchen.Sink.write({ ...minimalSink(), ratio: 0.1 }));
  assert.equal(parsed.ratio, Math.fround(0.1));
});

test("an object referenced twice is written once and parses as one object", () => {
  const node: Kitchen.NodeInit = { name: "shared" };
  const graph: Kitchen.GraphInit = { nodes: [node, node], lines: [{ start: node, end: node }], root: node };
  const copies: Kitchen.GraphInit = {
    nodes: [{ name: "shared" }, { name: "shared" }],
    lines: [{ start: { name: "shared" }, end: { name: "shared" } }],
    root: { name: "shared" },
  };

  const bytes = Kitchen.Graph.write(graph);
  const parsed = Kitchen.Graph.parse(bytes);

  assert.ok(bytes.length < Kitchen.Graph.write(copies).length);
  assert.equal(parsed.nodes[0], parsed.nodes[1]);
  assert.equal(parsed.lines[0]?.start, parsed.root);
  assert.notEqual(Kitchen.Graph.parse(bytes).root, parsed.root, "separate parses never share objects");
});

test("a reference cycle throws instead of looping", () => {
  const node: Kitchen.NodeInit & { self?: Kitchen.NodeInit } = { name: "loop" };
  node.self = node;
  assert.throws(() => Kitchen.Node.write(node), FlatBufferWriteError);
  assert.throws(
    () => Kitchen.Node.write(node),
    /^FlatBufferWriteError: Cannot serialize a reference cycle: an object written as 'Bequadro\.Tests\.Kitchen\.Node' contains itself\.$/,
  );
});

test("nesting deeper than 64 levels throws when writing", () => {
  const chain = (depth: number): Kitchen.NodeInit => {
    let node: Kitchen.NodeInit = { name: "leaf" };
    for (let i = 1; i < depth; i++) {
      node = { self: node };
    }

    return node;
  };

  Kitchen.Node.write(chain(64));
  assert.throws(
    () => Kitchen.Node.write(chain(65)),
    /more than 64 levels deep, because the buffer couldn't be parsed: an object written as 'Bequadro\.Tests\.Kitchen\.Node' is at level 65\.$/,
  );
});

test("one object written as two different tables throws", () => {
  const both = { name: "n", symbol: "s" };
  assert.throws(
    () => Equality.Measure.write({ unit: both, source: both }),
    /^FlatBufferWriteError: Cannot serialize one object as two tables: it is written as both 'Bequadro\.Tests\.Equality\.Unit' and 'Bequadro\.Tests\.Equality\.Source'\.$/,
  );
});

test("the writer rejects values of the wrong type or out of range, naming the field", () => {
  const cases: [Partial<Record<keyof Kitchen.SinkInit, unknown>>, RegExp][] = [
    [{ count: "42" }, /Expected a number for field 'count' of 'Bequadro.Tests.Kitchen.Sink'/],
    [{ count: 1.5 }, /not an integer .* 'count'/],
    [{ tiny: 256 }, /from 0 to 255/],
    [{ small: -129 }, /from -128 to 127/],
    [{ wide: 1 }, /Expected a bigint for field 'wide'/],
    [{ unsignedWide: -1n }, /out of range for ulong/],
    [{ flag: 1 }, /Expected a boolean for field 'flag'/],
    [{ color: 300 }, /field 'color'/],
    [{ name: 5 }, /Expected a string for field 'name'/],
    [{ strings: ["a", null] }, /null element/],
    [{ doubles: "no" }, /Expected an array for field 'doubles'/],
    [{ bytes: [1, 2] }, /Expected a Uint8Array for field 'bytes'/],
    [{ position: { x: "1" } }, /field 'position.x'/],
    [{ vectors: [{}, 5] }, /Expected an object for field 'vectors\[1\]' of 'Bequadro.Tests.Kitchen.Sink', got 5/],
    [{ vectors: [{}, undefined] }, /null element in a vector of 'Bequadro.Tests.Common.Vec3' in field 'vectors'/],
    [{ title: undefined }, /Required field 'title' of table 'Bequadro.Tests.Kitchen.Sink' is null/],
    [{ requiredPosition: null }, /Required field 'required_position'/],
  ];

  for (const [change, message] of cases) {
    const value = { ...minimalSink(), ...change } as Kitchen.SinkInit;
    assert.throws(() => Kitchen.Sink.write(value), (error: unknown) => {
      assert.ok(error instanceof FlatBufferWriteError, `${Object.keys(change).join()} throws a FlatBufferWriteError`);
      assert.match(error.message, message);
      return true;
    });
  }
});

test("messages name the path to a bad vector element or struct member exactly", () => {
  const sink = "of 'Bequadro.Tests.Kitchen.Sink'";
  const cases: [Partial<Record<keyof Kitchen.SinkInit, unknown>>, string][] = [
    [{ doubles: [1, "2"] }, `Expected a number for field 'doubles[1]' ${sink}, got the string "2".`],
    [{ ints: [1, 2.5] }, `2.5 is not an integer from -2147483648 to 2147483647, as int field 'ints[1]' ${sink} must be.`],
    [{ ulongs: [1n, -1n] }, `-1n is out of range for ulong field 'ulongs[1]' ${sink}.`],
    [{ strings: ["a", 5] }, `Expected a string for field 'strings[1]' ${sink}, got 5.`],
    [{ vectors: [{}, { x: "1" }] }, `Expected a number for field 'vectors[1].x' ${sink}, got the string "1".`],
    [{ position: { y: 1n } }, `Expected a number for field 'position.y' ${sink}, got 1n.`],
    [{ mixed: { wide: 1 } }, `Expected a bigint for field 'mixed.wide' ${sink}, got 1.`],
    [{ mixed: { position: { z: true } } }, `Expected a number for field 'mixed.position.z' ${sink}, got true.`],
    [{ mixed: { position: 5 } }, `Expected an object for field 'mixed.position' ${sink}, got 5.`],
    [
      { shape: { kind: "Marker", value: { id: "7" } } },
      `Expected a number for field 'Marker.id' of 'Bequadro.Tests.Shapes.Shape', got the string "7".`,
    ],
  ];

  for (const [change, message] of cases) {
    const value = { ...minimalSink(), ...change } as Kitchen.SinkInit;
    assert.throws(() => Kitchen.Sink.write(value), (error: unknown) => {
      assert.ok(error instanceof FlatBufferWriteError, `${Object.keys(change).join()} throws a FlatBufferWriteError`);
      assert.equal(error.message, message);
      return true;
    });
  }
});

test("an error from a getter in a struct passes through the writer unchanged", () => {
  const thrown = new RangeError("from a getter");
  const position = {
    get x(): number {
      throw thrown;
    },
  };

  assert.throws(() => Kitchen.Sink.write({ ...minimalSink(), position }), (error: unknown) => error === thrown);
});

test("a struct member whose getter is invalid only the first time still fails the write", () => {
  // The writer writes a struct again after an error, to build the message with its path; the second read is valid
  let reads = 0;
  const position = {
    get x(): unknown {
      return reads++ === 0 ? "bad" : 1;
    },
  } as unknown as Common.Vec3Init;

  assert.throws(() => Kitchen.Sink.write({ ...minimalSink(), position }), FlatBufferWriteError);
});

test("write returns the whole of an ArrayBuffer of its own, typed as one", () => {
  // The annotations are checked by tsc
  const bytes: Uint8Array<ArrayBuffer> = Kitchen.Sink.write(minimalSink());
  const generic: Uint8Array<ArrayBuffer> = write(Kitchen.$c_Sink, minimalSink());

  for (const result of [bytes, generic]) {
    assert.ok(result.buffer instanceof ArrayBuffer);
    assert.equal(result.byteOffset, 0);
    assert.equal(result.byteLength, result.buffer.byteLength);
  }
});

test("null is treated as undefined for untyped callers", () => {
  const value = { ...minimalSink(), name: null, optionalInt: null, shape: null } as unknown as Kitchen.SinkInit;
  assert.deepEqual(Kitchen.Sink.write(value), Kitchen.Sink.write(minimalSink()));
});

test("unions round trip, with NONE as undefined", () => {
  const holder: Shapes.HolderInit = {
    requiredShape: { kind: "Marker", value: { id: 1 } },
    requiredStrict: { kind: "Tag", value: { code: 9, weight: 1.5 } },
    shapes: [undefined, { kind: "Poly", value: { points: [{ x: 1 }] } }],
    stricts: [{ kind: "Marker", value: { id: 3 } }],
  };

  const parsed = Shapes.Holder.parse(Shapes.Holder.write(holder));

  assert.equal(parsed.shape, undefined);
  assert.deepEqual(parsed.shapes, [undefined, { kind: "Poly", value: { points: [{ x: 1, y: 0, z: 0 }] } }]);
  assert.deepEqual(parsed.requiredStrict, { kind: "Tag", value: { code: 9, weight: 1.5 } });
  assert.deepEqual(parsed.stricts, [{ kind: "Marker", value: { id: 3 } }]);
});

test("a switch over a union's kind narrows and is checked for exhaustiveness", () => {
  const describe = (shape: Shapes.Shape | undefined): string => {
    switch (shape?.kind) {
      case "Circle":
        return `circle ${shape.value.radius}`;
      case "Poly":
        return `polygon ${shape.value.points.length}`;
      case "Marker":
        return `marker ${shape.value.id}`;
      case "unknown":
        return `unknown ${shape.discriminator}`;
      case undefined:
        return "none";
      default:
        return shape satisfies never;
    }
  };

  const strict = (shape: Shapes.StrictShape): number => {
    switch (shape.kind) {
      case "Circle":
        return shape.value.radius;
      case "Marker":
        return shape.value.id;
      case "Tag":
        return shape.value.code;
      default:
        return shape satisfies never;
    }
  };

  assert.equal(describe({ kind: "Marker", value: { id: 7 } }), "marker 7");
  assert.equal(describe(undefined), "none");
  assert.equal(strict({ kind: "Tag", value: { code: 2, weight: 0 } }), 2);
});

test("parsed values are assignable to the input types", () => {
  const parsed = Kitchen.Sink.parse(Kitchen.Sink.write(minimalSink()));
  const input: Kitchen.SinkInit = parsed;
  const holder: Shapes.HolderInit = Shapes.Holder.parse(
    Shapes.Holder.write({
      requiredShape: { kind: "Marker", value: { id: 1 } },
      requiredStrict: { kind: "Marker", value: { id: 2 } },
    }),
  );
  assert.deepEqual(Kitchen.Sink.write(input), Kitchen.Sink.write(minimalSink()));
  assert.ok(Shapes.Holder.write(holder).length > 0);
});

test("unwritable unions throw", () => {
  const base = { requiredStrict: { kind: "Marker", value: { id: 2 } } } as const;
  assert.throws(
    () => Shapes.Holder.write({ ...base, requiredShape: { kind: "unknown", discriminator: 9 } }),
    /unknown member \(discriminator 9\)/,
  );
  assert.throws(
    () => Shapes.Holder.write({ ...base, requiredShape: { kind: "Square", value: {} } } as unknown as Shapes.HolderInit),
    /no member with kind the string "Square"/,
  );
  assert.throws(
    () =>
      Shapes.Holder.write({
        ...base,
        requiredShape: { kind: "Marker", value: { id: 1 } },
        stricts: [undefined] as unknown as Shapes.StrictShapeInit[],
      }),
    /'Bequadro.Tests.Shapes.StrictShape' because it has no value/,
  );
});

test("unknown discriminators parse as unknown, unless the union is exhaustive", () => {
  const read = (name: string): Uint8Array => readFileSync(fixtures + name + ".parse.bin");

  const holder = Shapes.Holder.parse(read("holder-unknown"));
  assert.deepEqual(holder.requiredShape, { kind: "unknown", discriminator: 201 });
  assert.deepEqual(holder.shapes, [{ kind: "unknown", discriminator: 200 }, { kind: "Marker", value: { id: 1 } }]);
  assert.deepEqual(Shapes.Holder.parse(read("holder-none-in-vector")).shapes, [undefined, { kind: "Marker", value: { id: 1 } }]);
  assert.throws(() => Shapes.Holder.parse(read("holder-exhaustive-unknown")), /discriminator 200 .*StrictShape/);
  assert.throws(() => Shapes.Holder.parse(read("holder-none-in-strict-vector")), /NONE .*StrictShape/);
});

test("equals compares values: NaN equals NaN, vectors by element, and plain tables by identity", () => {
  const source: Equality.SourceInit = { name: "s" };
  const measure = (): Equality.MeasureInit => ({
    name: "m",
    amount: NaN,
    position: { x: 1, y: 2, z: 3 },
    unit: { symbol: "kg" },
    source,
    item: { kind: "Vec3", value: { x: 1, y: 2, z: 3 } },
    values: [0, -0, NaN],
    units: [{ symbol: "a" }],
  });

  const a = Equality.Measure.parse(Equality.Measure.write(measure()));
  const b = Equality.Measure.parse(Equality.Measure.write(measure()));
  assert.ok(!Equality.Measure.equals(a, b), "each parse has its own Source, which compares by identity");
  b.source = a.source;
  assert.ok(Equality.Measure.equals(a, b));
  b.values = [0, 0, NaN];
  assert.ok(Equality.Measure.equals(a, b), "0 equals -0");
  b.values = [];
  assert.ok(!Equality.Measure.equals(a, b));
  b.values = undefined;
  assert.ok(!Equality.Measure.equals(a, b), "undefined isn't equal to a vector");
  assert.ok(Common.Vec3.equals({ x: NaN, y: 0, z: 0 }, { x: NaN, y: -0, z: 0 }));
  assert.ok(Equality.Item.equals(undefined, undefined));
  assert.ok(!Equality.Item.equals({ kind: "Unit", value: { symbol: "a" } }, undefined));
  assert.ok(Equality.Item.equals({ kind: "Unit", value: { symbol: "a" } }, { kind: "Unit", value: { symbol: "a" } }));
});

test("malformed buffers only ever throw FlatBufferDataError", () => {
  const original = new Uint8Array(readFileSync(fixtures + "sink-full.input.bin"));
  let seed = 12345;
  const random = (limit: number): number => {
    seed = (seed * 1103515245 + 12345) % 2147483648;
    return seed % limit;
  };

  for (let i = 0; i < 2000; i++) {
    const bytes =
      i % 2 === 0
        ? original.slice(0, random(original.length))
        : original.map((byte, index) => (random(original.length) < 4 || index === random(original.length) ? random(256) : byte));
    try {
      Kitchen.Sink.parse(bytes);
    } catch (error) {
      assert.ok(error instanceof FlatBufferDataError, `case ${i} threw ${String(error)}`);
    }
  }
});

test("parse honors the view's offset and length", () => {
  const bytes = Kitchen.Sink.write(minimalSink());
  const padded = new Uint8Array(bytes.length + 16);
  padded.set(bytes, 8);
  assert.equal(Kitchen.Sink.parse(padded.subarray(8, 8 + bytes.length)).title, "minimal");
});

test("write and parse reject arguments that aren't a value or a byte array", () => {
  assert.throws(() => Kitchen.Sink.write(undefined as unknown as Kitchen.SinkInit), TypeError);
  assert.throws(() => Kitchen.Sink.parse([1, 2, 3] as unknown as Uint8Array), TypeError);
});

test("a [ubyte] parsed from a Node Buffer is a fresh plain Uint8Array, not a view of the buffer", () => {
  const buffer = Buffer.from(Kitchen.Sink.write({ ...minimalSink(), bytes: new Uint8Array([1, 2, 3]) }));

  const parsed = Kitchen.Sink.parse(buffer);
  buffer.fill(0xff);

  assert.equal(parsed.bytes?.constructor, Uint8Array);
  assert.notEqual(parsed.bytes?.buffer, buffer.buffer);
  assert.deepEqual(parsed.bytes, new Uint8Array([1, 2, 3]));
});

test("a [ubyte] parsed from a Uint8Array is a copy, which changing the input doesn't affect", () => {
  const bytes = Kitchen.Sink.write({ ...minimalSink(), bytes: new Uint8Array([4, 5]) });

  const parsed = Kitchen.Sink.parse(bytes);
  bytes.fill(0);

  assert.deepEqual(parsed.bytes, new Uint8Array([4, 5]));
});

test("a string starting with a byte order mark keeps it, and parsing then writing keeps the bytes", () => {
  const bytes = Kitchen.Sink.write({ ...minimalSink(), name: "\uFEFFhello", strings: ["\uFEFF", "\uFEFF\uFEFFx"] });

  const parsed = Kitchen.Sink.parse(bytes);

  assert.equal(parsed.name, "\uFEFFhello");
  assert.deepEqual(parsed.strings, ["\uFEFF", "\uFEFF\uFEFFx"]);
  assert.deepEqual(Kitchen.Sink.write(parsed), bytes);
});

test("strings are written as UTF-8, with lone surrogates as U+FFFD, on every path of the writer", () => {
  // ASCII of up to 12 characters is copied a character at a time, other strings of up to 65,536 characters are
  // encoded into a reused scratch array, and longer ones on their own. Written into one writer in this order, the
  // scratch array grows and is then reused for shorter strings.
  const strings = [
    "",
    "a",
    "\u00e9",
    "\u65e5\u672c\u8a9e",
    "\u{1F600}",
    "\uD800",
    "a\uDC00b",
    "\uDE00\uD83D",
    "\uFEFFbom",
    "\u007f\u0080",
    "a\0b",
    "x".repeat(11),
    "x".repeat(12),
    "x".repeat(13),
    "x".repeat(12) + "\u00e9",
    "\u00e9" + "x".repeat(11),
    "\u00e9".repeat(100),
    "after a long one",
    "x".repeat(65536),
    "x".repeat(65537),
    "x".repeat(65535) + "\u00e9",
    "\uD800".repeat(65537),
    "a",
    "x".repeat(12),
  ];
  const reference = new TextEncoder();

  for (const shareStrings of [false, true]) {
    const writer = new Writer(shareStrings);
    const buffer = writer.finish(writer.writeOffsetVector(strings.map((value) => writer.writeString(value, "test", "s"))));
    const view = new DataView(buffer.buffer, buffer.byteOffset, buffer.byteLength);
    const vector = view.getUint32(0, true);
    strings.forEach((value, i) => {
      const uoffset = vector + 4 + 4 * i;
      const position = uoffset + view.getUint32(uoffset, true);
      const length = view.getInt32(position, true);
      const name = `string ${i}, shareStrings ${shareStrings}`;
      assert.equal(position % 4, 0, `${name} is aligned to 4`);
      assert.deepEqual(buffer.subarray(position + 4, position + 4 + length), reference.encode(value), name);
      assert.equal(buffer[position + 4 + length], 0, `${name} ends with a zero byte`);
    });
  }
});

test("Uint8Arrays from another realm are parsed and written, other typed arrays are not", () => {
  const bytes = Kitchen.Sink.write({ ...minimalSink(), bytes: new Uint8Array([7]) });
  const foreign = runInNewContext("(bytes) => new Uint8Array(bytes)", {}) as (bytes: number[]) => Uint8Array;
  assert.ok(!(foreign([]) instanceof Uint8Array), "the vm context is another realm");

  const parsed = Kitchen.Sink.parse(foreign(Array.from(bytes)));

  assert.deepEqual(parsed.bytes, new Uint8Array([7]));
  assert.equal(parsed.bytes?.constructor, Uint8Array, "parsed values are of this realm");
  assert.deepEqual(Kitchen.Sink.write({ ...minimalSink(), bytes: foreign([7]) }), bytes);
  assert.throws(() => Kitchen.Sink.parse(new Int8Array(bytes.buffer) as unknown as Uint8Array), TypeError);
  assert.throws(() => Kitchen.Sink.parse(new DataView(bytes.buffer) as unknown as Uint8Array), TypeError);
  assert.throws(() => Kitchen.Sink.parse({ length: 0, byteLength: 0 } as unknown as Uint8Array), TypeError);
  assert.throws(
    () => Kitchen.Sink.write({ ...minimalSink(), bytes: new Int8Array([1]) as unknown as Uint8Array }),
    /Expected a Uint8Array for field 'bytes'/,
  );
});
