// QUA-003: TypeScript counterparts of C# runtime tests (ParseErrorTests, UnionSerializationTests, SerializerTests),
// and the TS-006 to TS-011 rules that only TypeScript has. Buffers that the writer refuses to write are built with the
// runtime's Writer directly, as the C# tests use the official builder.
import assert from "node:assert/strict";
import { test } from "node:test";
import { FlatBufferDataError, FlatBufferWriteError, type StructCodec, Uint8, Writer } from "../generated/bequadro.ts";
import * as Common from "../generated/Bequadro_/Tests/Common.ts";
import * as Kitchen from "../generated/Bequadro_/Tests/Kitchen.ts";
import * as Limits from "../generated/Bequadro_/Tests/Limits.ts";
import * as Shapes from "../generated/Bequadro_/Tests/Shapes.ts";
import * as Values from "../generated/Values.ts";

function minimalSink(): Kitchen.SinkInit {
  return {
    title: "minimal",
    requiredPosition: { x: 1, y: 2, z: 3 },
    requiredShape: { kind: "Circle", value: { center: { x: 0, y: 0, z: 0 } } },
    requiredChildren: [],
  };
}

function minimalHolder(): Shapes.HolderInit {
  return {
    requiredShape: { kind: "Marker", value: { id: 1 } },
    requiredStrict: { kind: "Marker", value: { id: 2 } },
  };
}

/** Asserts that run throws a FlatBufferDataError whose message matches. */
function throwsData(run: () => unknown, message: RegExp): void {
  assert.throws(run, (error: unknown) => error instanceof FlatBufferDataError && message.test(error.message));
}

/** Asserts that run throws a FlatBufferWriteError whose message matches. */
function throwsWrite(run: () => unknown, message: RegExp): void {
  assert.throws(run, (error: unknown) => error instanceof FlatBufferWriteError && message.test(error.message));
}

// Holder's slots: each union takes its discriminator's slot and the next
const slots = { requiredShape: 3, requiredStrict: 9, noNone: 13, noNones: 15, exhaustive: 17, exhaustives: 19 };
const markerType = { shape: 3, strict: 2, noNone: 2, exhaustive: 2 };

interface HolderParts {
  readonly requiredShapeType?: number;
  readonly noNoneType?: number;
  readonly noNoneTypes?: readonly number[];
  readonly exhaustiveType?: number;
  readonly exhaustiveTypes?: readonly number[];
  /** Writes field shape's discriminator without its value. */
  readonly shapeTypeWithoutValue?: number;
  /** Writes field shape's value without its discriminator. */
  readonly shapeValueWithoutType?: boolean;
}

/**
 * A Holder whose unions all hold a Marker with id 1, under the given discriminators, which the writer wouldn't write
 * (UnionSerializationTests.HolderBuffer). A discriminator of 0 in a vector has a 0 offset.
 */
function holderBuffer(parts: HolderParts): Uint8Array {
  const writer = new Writer(false);
  const marker = (): number => writer.writeStruct(Shapes.$c_Marker, { id: 1 }, "test", "marker");
  const vector = (types: readonly number[]): [number, number] => {
    const values = writer.writeOffsetVector(types.map((type) => (type === 0 ? 0 : marker())));
    return [writer.writeBytes(new Uint8Array(types), "test", "types"), values];
  };

  const unions: [number, number, number][] = [
    [slots.requiredShape, parts.requiredShapeType ?? markerType.shape, marker()],
    [slots.requiredStrict, markerType.strict, marker()],
  ];
  if (parts.noNoneType !== undefined) unions.push([slots.noNone, parts.noNoneType, marker()]);
  if (parts.exhaustiveType !== undefined) unions.push([slots.exhaustive, parts.exhaustiveType, marker()]);
  const vectors: [number, [number, number]][] = [];
  if (parts.noNoneTypes !== undefined) vectors.push([slots.noNones, vector(parts.noNoneTypes)]);
  if (parts.exhaustiveTypes !== undefined) vectors.push([slots.exhaustives, vector(parts.exhaustiveTypes)]);

  const shapeValue = parts.shapeValueWithoutType === true ? marker() : 0;
  writer.startTable(20);
  if (shapeValue !== 0) writer.addOffset(1, shapeValue);
  if (parts.shapeTypeWithoutValue !== undefined) writer.addScalar(0, Uint8, parts.shapeTypeWithoutValue);
  for (const [slot, , offset] of unions) writer.addOffset(slot, offset);
  for (const [slot, [types, values]] of vectors) {
    writer.addOffset(slot - 1, types);
    writer.addOffset(slot, values);
  }

  for (const [slot, type] of unions) writer.addUnionType(slot - 1, type);
  return writer.finish(writer.endTable("test"));
}

// SER-009 (ParseErrorTests)

test("a missing required field throws when parsing, naming the table and field", () => {
  const writer = new Writer(false);
  writer.startTable(2);
  const empty = writer.finish(writer.endTable("test"));

  throwsData(() => Shapes.Circle.parse(empty), /required field 'center' of table 'Bequadro.Tests.Shapes.Circle' is missing/);
});

/** A buffer of Node tables nested levels deep through self, built without the writer's depth limit. */
function nestedTables(levels: number): Uint8Array {
  const writer = new Writer(false);
  writer.startTable(0);
  let current = writer.endTable("test");
  for (let i = 1; i < levels; i++) {
    writer.startTable(3);
    writer.addOffset(2, current);
    current = writer.endTable("test");
  }

  return writer.finish(current);
}

test("tables nested 64 levels deep, the limit, parse", () => {
  let node: Kitchen.Node | undefined = Kitchen.Node.parse(nestedTables(64));
  let depth = 0;
  for (; node !== undefined; node = node.self) {
    depth++;
  }

  assert.equal(depth, 64);
});

test("tables nested more than 64 levels deep throw when parsing", () => {
  for (const levels of [65, 1100]) {
    throwsData(() => Kitchen.Node.parse(nestedTables(levels)), /^The buffer exceeds the maximum table nesting depth of 64\.$/);
  }
});

test("a union field with only its discriminator or only its value parses as absent", () => {
  for (const parts of [{ shapeTypeWithoutValue: markerType.shape }, { shapeValueWithoutType: true }]) {
    const holder = Shapes.Holder.parse(holderBuffer(parts));
    assert.equal(holder.shape, undefined);
    assert.deepEqual(holder.requiredShape, { kind: "Marker", value: { id: 1 } });
  }
});

test("invalid UTF-8 parses with replacement characters", () => {
  const bytes = Kitchen.Sink.write({ ...minimalSink(), name: "zzzz marker" });
  const position = Buffer.from(bytes).indexOf("zzzz");
  bytes[position] = 0xff;
  bytes[position + 2] = 0xc3;
  assert.equal(Kitchen.Sink.parse(bytes).name, "\ufffdz\ufffdz marker");
});

test("fields the schema doesn't have are ignored", () => {
  const bytes = Kitchen.Sink.write({ ...minimalSink(), name: "n", children: [minimalSink()], ints: [1, 2] });
  assert.deepEqual(Kitchen.Empty.parse(bytes), {});
});

test("a table position read as two tables throws", () => {
  // Graph's nodes and lines both refer to one table, which can't be both a Node and a Line
  const writer = new Writer(false);
  writer.startTable(0);
  const table = writer.endTable("test");
  const nodes = writer.writeOffsetVector([table]);
  const lines = writer.writeOffsetVector([table]);
  writer.startTable(3);
  writer.addOffset(0, nodes);
  writer.addOffset(1, lines);
  throwsData(() => Kitchen.Graph.parse(writer.finish(writer.endTable("test"))), /read as both 'Bequadro.Tests.Kitchen.Node' and 'Bequadro.Tests.Kitchen.Line'/);
});

test("vtables whose hashes collide are compared by their bytes, and the first identical vtable is shared", () => {
  // A struct of any size, which writes nothing, to fill a table to exact field offsets
  const filler = (size: number): StructCodec<unknown, object> => ({
    name: "Filler",
    size,
    alignment: 1,
    write() {},
    read: () => ({}),
  });

  const writer = new Writer(false);
  // Each table starts 4-aligned, so endTable adds no padding: an empty [ubyte] aligns the offset to 4 and adds 4
  const aligned = (): void => void writer.writeBytes(new Uint8Array(0), "test", "align");
  // vtable [10, 32, 30, 31, 4]: slot 1 is written first, the 26-byte filler in slot 2 last, next to the soffset
  const first = (): number => {
    aligned();
    writer.startTable(3);
    writer.addScalar(1, Uint8, 1);
    writer.addScalar(0, Uint8, 2);
    writer.addStruct(2, filler(26), {}, "test", "filler");
    return writer.endTable("test");
  };
  // vtable [10, 32, 31, 0, 4]: its entries differ by (+1, -31) in consecutive slots, so with h = 31 h + entry its
  // hash is the same as first's
  const second = (): number => {
    aligned();
    writer.startTable(3);
    writer.addScalar(0, Uint8, 3);
    writer.addStruct(2, filler(27), {}, "test", "filler");
    return writer.endTable("test");
  };

  const offsets = [first(), second(), first(), second()];
  const buffer = writer.finish(offsets[0]!);
  const view = new DataView(buffer.buffer, buffer.byteOffset, buffer.byteLength);
  const vtablePositions = offsets.map((offset) => {
    const table = buffer.length - offset;
    return table - view.getInt32(table, true);
  });
  const vtable = (position: number): number[] =>
    Array.from({ length: view.getUint16(position, true) / 2 }, (_, i) => view.getUint16(position + 2 * i, true));

  assert.deepEqual(vtable(vtablePositions[0]!), [10, 32, 30, 31, 4]);
  assert.deepEqual(vtable(vtablePositions[1]!), [10, 32, 31, 0, 4]);
  assert.deepEqual(vtablePositions, [vtablePositions[0], vtablePositions[1], vtablePositions[0], vtablePositions[1]]);
  assert.notEqual(vtablePositions[0], vtablePositions[1]);
});

/** A Graph whose parse reads 1 + nodes + lines tables: each line refers to two of the nodes. */
function graph(nodes: number, lines: number): Kitchen.GraphInit {
  const all = Array.from({ length: nodes }, (_, i) => ({ name: `n${i}` }));
  return { nodes: all, lines: Array.from({ length: lines }, (_, i) => ({ start: all[i % nodes]!, end: all[(7 * i) % nodes]! })) };
}

/**
 * The same buffer with every table, and everything else, 2 bytes further on: 2 bytes inserted after the root uoffset,
 * which is the only absolute offset. No FlatBuffers writer puts tables there, but readers must accept it.
 */
function shifted(bytes: Uint8Array): Uint8Array {
  const result = new Uint8Array(bytes.length + 2);
  result.set(bytes.subarray(4), 6);
  new DataView(result.buffer).setUint32(0, new DataView(bytes.buffer, bytes.byteOffset).getUint32(0, true) + 2, true);
  return result;
}

test("tables parse as one object per position on each side of the switch to the table index, aligned or not", () => {
  // The reader finds tables with a Map until it has read 4096, and with an index of positions that are multiples of 4
  // after that, while tables at other positions stay in the Map. With 6000 nodes, some are first read after the switch.
  for (const [nodes, lines] of [[2047, 2047], [2047, 2048], [2048, 2048], [6000, 12000]] as const) {
    const bytes = Kitchen.Graph.write(graph(nodes, lines));
    for (const buffer of [bytes, shifted(bytes)]) {
      const parsed = Kitchen.Graph.parse(buffer);
      const name = `${1 + nodes + lines} tables${buffer === bytes ? "" : ", shifted"}`;
      parsed.lines.forEach((line, i) => {
        assert.equal(line.start, parsed.nodes[i % nodes], name);
        assert.equal(line.end, parsed.nodes[(7 * i) % nodes], name);
      });
      // The writer shares tables by identity, so the same bytes show that every shared table parsed as one object
      assert.deepEqual(Kitchen.Graph.write(parsed), bytes, name);
    }
  }
});

test("a table position read as two tables throws once the reader uses the table index, aligned or not", () => {
  // 5000 nodes are read before the line, which refers to the first node's table
  const writer = new Writer(false);
  const tables = Array.from({ length: 5000 }, () => {
    writer.startTable(0);
    return writer.endTable("test");
  });
  const nodes = writer.writeOffsetVector(tables);
  const lines = writer.writeOffsetVector([tables[0]!]);
  writer.startTable(3);
  writer.addOffset(0, nodes);
  writer.addOffset(1, lines);
  const bytes = writer.finish(writer.endTable("test"));

  for (const buffer of [bytes, shifted(bytes)]) {
    throwsData(() => Kitchen.Graph.parse(buffer), /read as both 'Bequadro.Tests.Kitchen.Node' and 'Bequadro.Tests.Kitchen.Line'/);
  }
});

test("a null element in a vector of tables throws when writing", () => {
  throwsWrite(
    () => Kitchen.Sink.write({ ...minimalSink(), children: [minimalSink(), null as unknown as Kitchen.SinkInit] }),
    /null element in a vector of 'Bequadro.Tests.Kitchen.Sink'/,
  );
});

// SER-004, SER-009: required unions (UnionSerializationTests)

test("a required union that is NONE throws when writing", () => {
  for (const change of [{ requiredShape: undefined }, { requiredStrict: null }]) {
    const holder = { ...minimalHolder(), ...change } as unknown as Shapes.HolderInit;
    throwsWrite(() => Shapes.Holder.write(holder), /Required field 'required_(shape|strict)' of table 'Bequadro.Tests.Shapes.Holder' is null/);
  }
});

test("a required union that is absent or NONE throws when parsing", () => {
  throwsData(() => Shapes.Holder.parse(holderBuffer({ requiredShapeType: 0 })), /required field 'required_shape'/);

  const writer = new Writer(false);
  const marker = writer.writeStruct(Shapes.$c_Marker, { id: 1 }, "test", "marker");
  writer.startTable(20);
  writer.addOffset(slots.requiredStrict, marker);
  writer.addScalar(slots.requiredStrict - 1, Uint8, markerType.strict);
  throwsData(() => Shapes.Holder.parse(writer.finish(writer.endTable("test"))), /required field 'required_shape'/);
});

// The single-attribute unions NoNoneShape (bequadro_exclude_none) and ExhaustiveShape (bequadro_exhaustive)

test("single-attribute unions keep their values and NONE", () => {
  const holder: Shapes.HolderInit = {
    ...minimalHolder(),
    noNone: { kind: "Marker", value: { id: 5 } },
    noNones: [
      { kind: "Circle", value: { center: { x: 1, y: 2, z: 3 } } },
      { kind: "Marker", value: { id: 6 } },
    ],
    exhaustives: [undefined, { kind: "Marker", value: { id: 7 } }],
  };

  const parsed = Shapes.Holder.parse(Shapes.Holder.write(holder));

  assert.deepEqual(parsed.noNone, { kind: "Marker", value: { id: 5 } });
  assert.deepEqual(parsed.noNones, [
    { kind: "Circle", value: { center: { x: 1, y: 2, z: 3 }, radius: 1 } },
    { kind: "Marker", value: { id: 6 } },
  ]);
  assert.equal(parsed.exhaustive, undefined);
  assert.deepEqual(parsed.exhaustives, [undefined, { kind: "Marker", value: { id: 7 } }]);
});

test("an exclude-none-only union vector element with no value throws when writing, and an absent field is absent", () => {
  throwsWrite(
    () => Shapes.Holder.write({ ...minimalHolder(), noNones: [{ kind: "Marker", value: { id: 1 } }, undefined] as unknown as Shapes.NoNoneShapeInit[] }),
    /union 'Bequadro.Tests.Shapes.NoNoneShape' because it has no value/,
  );
  assert.deepEqual(
    Shapes.Holder.write({ ...minimalHolder(), noNone: undefined }),
    Shapes.Holder.write(minimalHolder()),
    "TS-007: a field that isn't required may be absent",
  );
});

test("unknown discriminators in an exclude-none-only union read as unknown", () => {
  const holder = Shapes.Holder.parse(holderBuffer({ noNoneType: 200, noNoneTypes: [201, markerType.noNone] }));

  assert.deepEqual(holder.noNone, { kind: "unknown", discriminator: 200 });
  assert.deepEqual(holder.noNones, [{ kind: "unknown", discriminator: 201 }, { kind: "Marker", value: { id: 1 } }]);
});

test("a NONE element in an exclude-none-only union vector throws when parsing", () => {
  throwsData(() => Shapes.Holder.parse(holderBuffer({ noNoneTypes: [markerType.noNone, 0] })), /NONE .*NoNoneShape/);
});

test("an unknown discriminator in an exhaustive-only union throws when parsing, in a field or a vector", () => {
  for (const parts of [{ exhaustiveTypes: [markerType.exhaustive, 200] }, { exhaustiveType: 200 }]) {
    throwsData(() => Shapes.Holder.parse(holderBuffer(parts)), /discriminator 200 .*ExhaustiveShape/);
  }
});

test("a NONE element in an exhaustive-only union vector reads as undefined", () => {
  const holder = Shapes.Holder.parse(holderBuffer({ exhaustiveTypes: [0, markerType.exhaustive] }));

  assert.deepEqual(holder.exhaustives, [undefined, { kind: "Marker", value: { id: 1 } }]);
});

// TS-007

test("a union variant whose value is missing throws when writing", () => {
  for (const shape of [{ kind: "Circle" }, { kind: "Marker" }, { kind: "Circle", value: null }]) {
    throwsWrite(
      () => Shapes.Holder.write({ ...minimalHolder(), shape } as unknown as Shapes.HolderInit),
      /Expected an object for field '(Circle|Marker)' of 'Bequadro.Tests.Shapes.Shape', got (undefined|null)/,
    );
  }
});

// SER-012

test("a write whose buffer would exceed 2,147,483,591 bytes throws", () => {
  // A sparse array of 2^28 doubles needs 2^31 bytes, and is refused before its elements are read
  throwsWrite(
    () => Kitchen.Sink.write({ ...minimalSink(), doubles: new Array<number>(2 ** 28) }),
    /exceed the maximum size of 2147483591 bytes/,
  );
});

test("a table whose inline fields exceed 65,535 bytes throws, naming the table", () => {
  const largest = Limits.Largest.create();
  assert.ok(Limits.TooLarge.parse(Limits.TooLarge.write({ largest })).largest);
  throwsWrite(
    () => Limits.TooLarge.write({ largest, extra: Limits.S0.create() }),
    /^Cannot serialize an instance of 'Bequadro\.Tests\.Limits\.TooLarge': a table's inline fields can take at most 65535 bytes/,
  );
});

// TS-006

test("an enum value the schema doesn't define is written, and parses as its number", () => {
  const parsed = Kitchen.Sink.parse(Kitchen.Sink.write({ ...minimalSink(), color: 77 as Common.Color, colors: [9 as Common.Color] }));

  assert.equal(parsed.color, 77);
  assert.deepEqual(parsed.colors, [9]);
});

// TS-011

test("properties the schema doesn't have are ignored when writing", () => {
  const extra = { ...minimalSink(), unknown: 1, Title: "x", requiredPosition: { x: 1, y: 2, z: 3, w: 4 } };

  assert.deepEqual(Kitchen.Sink.write(extra), Kitchen.Sink.write(minimalSink()));
});

test("a float beyond its range is written as infinity", () => {
  const parsed = Kitchen.Sink.parse(Kitchen.Sink.write({ ...minimalSink(), ratio: 1e300, position: { x: -1e300, y: 0, z: 0 } }));

  assert.equal(parsed.ratio, Infinity);
  assert.equal(parsed.position?.x, -1e300, "a double field keeps it");
  const tiny = Kitchen.Sink.parse(Kitchen.Sink.write({ ...minimalSink(), ratio: -1e300 }));
  assert.equal(tiny.ratio, -Infinity);
});

test("write given null or undefined throws TypeError", () => {
  for (const value of [null, undefined]) {
    assert.throws(() => Kitchen.Sink.write(value as unknown as Kitchen.SinkInit), TypeError);
  }
});

// TS-008, TS-009, TS-010

function allVectors(): Values.Vectors {
  return Values.Vectors.create({
    bools: [true, false],
    signedBytes: [-128, 127],
    bytes: new Uint8Array([0, 255]),
    shorts: [-32768, 32767],
    unsignedShorts: [0, 65535],
    ints: [-2147483648, 2147483647],
    unsignedInts: [0, 4294967295],
    longs: [-9223372036854775808n, 9223372036854775807n],
    unsignedLongs: [0n, 18446744073709551615n],
    floats: [1.5, -0.25, Infinity],
    doubles: [Math.PI, -0, NaN],
    smalls: [Values.Small.Low, Values.Small.High],
    huges: [Values.Huge.Zero, Values.Huge.Top],
  });
}

test("vectors of every scalar type and of wide enums round trip", () => {
  const vectors = allVectors();

  const parsed = Values.Vectors.parse(Values.Vectors.write(vectors));

  assert.deepEqual(parsed, vectors);
  assert.ok(Object.is(parsed.doubles?.[1], -0));
  assert.equal(typeof parsed.longs?.[0], "bigint");
  assert.throws(() => Values.Vectors.write({ longs: [1 as unknown as bigint] }), /Expected a bigint for field 'longs\[0\]'/);
  assert.throws(() => Values.Vectors.write({ shorts: [40000] }), /field 'shorts\[0\]'/);
});

test("equals compares Uint8Arrays and every kind of vector element by element", () => {
  const a = allVectors();
  const b = Values.Vectors.parse(Values.Vectors.write(allVectors()));

  assert.ok(Values.Vectors.equals(a, b), "NaN equals NaN, and -0 equals 0");
  assert.ok(!Values.Vectors.equals(a, { ...b, bytes: new Uint8Array([0, 254]) }));
  assert.ok(!Values.Vectors.equals(a, { ...b, bytes: new Uint8Array([0]) }));
  assert.ok(!Values.Vectors.equals(a, { ...b, bytes: undefined }));
  assert.ok(!Values.Vectors.equals(a, { ...b, unsignedLongs: [0n, 1n] }));
});

test("unknown variants are equal when their discriminators are", () => {
  assert.ok(Shapes.Shape.equals({ kind: "unknown", discriminator: 9 }, { kind: "unknown", discriminator: 9 }));
  assert.ok(!Shapes.Shape.equals({ kind: "unknown", discriminator: 9 }, { kind: "unknown", discriminator: 10 }));
  assert.ok(!Shapes.Shape.equals({ kind: "unknown", discriminator: 3 }, { kind: "Marker", value: { id: 0 } }));
  assert.ok(!Shapes.Shape.equals({ kind: "Marker", value: { id: 0 } }, { kind: "unknown", discriminator: 3 }));
});
