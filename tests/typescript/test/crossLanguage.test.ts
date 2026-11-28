// QUA-003, SER-005: parsing a buffer the C# tests wrote and writing it back gives exactly the bytes C# writes. The
// fixtures come from CrossLanguageFixtureTests, which build.py runs first.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { parse, type TableCodec, Writer } from "../generated/bequadro.ts";
import * as Equality from "../generated/Bequadro_/Tests/Equality.ts";
import * as Evolution from "../generated/Bequadro_/Tests/Evolution.ts";
import * as Kitchen from "../generated/Bequadro_/Tests/Kitchen.ts";
import * as Limits from "../generated/Bequadro_/Tests/Limits.ts";

const directory = fileURLToPath(new URL("../../../artifacts/cross-language/", import.meta.url));

interface Fixture {
  readonly name: string;
  readonly root: string;
  readonly shareStrings: boolean;
}

type Rewrite = (input: Uint8Array, shareStrings: boolean) => Uint8Array;

const rewrites: Readonly<Record<string, Rewrite>> = {
  "Bequadro.Tests.Kitchen.Sink": (input, shareStrings) =>
    Kitchen.Sink.write(Kitchen.Sink.parse(input, { shareStrings }), { shareStrings }),
  "Bequadro.Tests.Kitchen.Graph": (input, shareStrings) =>
    Kitchen.Graph.write(Kitchen.Graph.parse(input, { shareStrings }), { shareStrings }),
  "Bequadro.Tests.Kitchen.Node": (input, shareStrings) =>
    Kitchen.Node.write(Kitchen.Node.parse(input, { shareStrings }), { shareStrings }),
  "Bequadro.Tests.Equality.Measure": (input, shareStrings) =>
    Equality.Measure.write(Equality.Measure.parse(input, { shareStrings }), { shareStrings }),
  "Bequadro.Tests.Limits.LargestHolder": (input, shareStrings) =>
    Limits.LargestHolder.write(Limits.LargestHolder.parse(input, { shareStrings }), { shareStrings }),
  "Bequadro.Tests.Evolution.After": (input, shareStrings) =>
    Evolution.After.write(Evolution.After.parse(input, { shareStrings }), { shareStrings }),
};

const codecs: Readonly<Record<string, TableCodec<unknown, unknown>>> = {
  "Bequadro.Tests.Kitchen.Sink": Kitchen.$c_Sink,
  "Bequadro.Tests.Kitchen.Graph": Kitchen.$c_Graph,
  "Bequadro.Tests.Kitchen.Node": Kitchen.$c_Node,
  "Bequadro.Tests.Equality.Measure": Equality.$c_Measure,
  "Bequadro.Tests.Limits.LargestHolder": Limits.$c_LargestHolder,
  "Bequadro.Tests.Evolution.After": Evolution.$c_After,
};

/** A fixture as Node reads it: a Buffer, whose slice is a view, unlike a Uint8Array's (TS-008). */
function read(name: string): Uint8Array {
  return readFileSync(directory + name);
}

const fixtures = JSON.parse(readFileSync(directory + "manifest.json", "utf8")) as Fixture[];

for (const fixture of fixtures) {
  test(`rewriting ${fixture.name} gives the bytes C# writes`, () => {
    const rewrite = rewrites[fixture.root];
    assert.ok(rewrite, `no rewrite for root ${fixture.root}`);
    const actual = rewrite(read(fixture.name + ".input.bin"), fixture.shareStrings);
    assert.deepEqual(actual, new Uint8Array(read(fixture.name + ".expected.bin")));
  });
}

// The writer starts with 1024 bytes, so most fixtures never make it grow. Starting from every smaller capacity makes
// each allocation in turn be the one that grows the buffer, which must not change the bytes.
for (const fixture of fixtures) {
  const expected = new Uint8Array(read(fixture.name + ".expected.bin"));
  if (expected.length > 2048) {
    continue;
  }

  test(`rewriting ${fixture.name} gives the bytes C# writes whenever the buffer grows`, () => {
    const codec = codecs[fixture.root];
    assert.ok(codec, `no codec for root ${fixture.root}`);
    const options = { shareStrings: fixture.shareStrings };
    const value = parse(codec, read(fixture.name + ".input.bin"), options);
    for (let capacity = 0; capacity <= expected.length; capacity++) {
      const writer = new Writer(fixture.shareStrings, capacity);
      const actual = writer.finish(writer.writeTable(codec, value));
      assert.deepEqual(actual, expected, `starting with ${capacity} bytes`);
    }
  });
}

test("parsing a C# buffer keeps shared tables shared", () => {
  const graph = Kitchen.Graph.parse(read("graph-shared-nodes.input.bin"));
  const [a, b, c, again] = graph.nodes;
  assert.equal(a?.name, "a");
  assert.equal(again, a);
  assert.equal(b?.neighbors?.[0], a);
  assert.equal(c?.neighbors?.[1], b);
  assert.equal(graph.lines[0]?.end, b);
  assert.equal(graph.root, c);
});

test("parsing a buffer built with the official builder reads every field kind", () => {
  const sink = Kitchen.Sink.parse(read("google-sink.input.bin"));
  assert.equal(sink.name, "google");
  assert.equal(sink.flag, true);
  assert.equal(sink.small, -9);
  assert.equal(sink.count, 5);
  assert.equal(sink.wide, 123n);
  assert.equal(sink.precise, 6.5);
  assert.equal(sink.optionalInt, 0);
  assert.equal(sink.ratio, 0.5, "absent fields read as the schema default");
  assert.deepEqual(sink.doubles, [1.5, 2.5]);
  assert.deepEqual(sink.strings, ["x", "yz"]);
  assert.equal(sink.children?.[0], sink.children?.[1]);
  assert.equal(sink.children?.[0], sink.child);
  assert.deepEqual(
    sink.shapes?.map((shape) => shape?.kind),
    ["Poly", "Marker"],
  );
});

test("parsing a buffer with deprecated fields reads the other fields", () => {
  const after = Evolution.After.parse(read("deprecated-fields.input.bin"));
  assert.deepEqual(after, { kept: 7, later: "after" });
  // @ts-expect-error: SCH-005, a deprecated field isn't generated
  assert.equal(after.removed, undefined);
});
