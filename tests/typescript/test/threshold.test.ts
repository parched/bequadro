// TS-011: a table's write keeps its out-of-line fields' offsets in locals when it has up to 64 of them, and in an
// array when it has more. Threshold.fbs has a table on each side of that limit, with the same fields but for Broad's
// 65th, so both ways of writing must give the same bytes.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import * as T from "../generated/Threshold.ts";

/** Every field of Narrow set, nesting another through next and items, with distinct strings. */
function full(depth = 1): T.NarrowInit {
  const strings = Object.fromEntries(Array.from({ length: 54 }, (_, i) => [`s${i}`, `string ${i}`]));
  const leaf = { id: 7 };
  return {
    ...strings,
    ...(depth < 3 ? { next: full(depth + 1) } : {}),
    item: { kind: "Point", value: { x: 1.5, y: -2 } },
    items: [{ kind: "Leaf", value: leaf }, { kind: "Point", value: { x: 3, y: 4 } }],
    name: `level ${depth}`,
    leaf,
    leaves: [leaf, { id: 8 }],
    names: ["a", "bc"],
    points: [{ x: 5, y: 6 }],
    values: [1, -2, 3],
    bytes: new Uint8Array([9, 10]),
    count: depth,
    position: { x: 0.5, y: 0.25 },
  };
}

test("tables either side of the limit for locals write the same bytes, and round trip", () => {
  const narrow = T.Narrow.write(full());
  // Broad's 65th field, s54, is absent, so its vtable is the same as Narrow's
  const broad = T.Broad.write(full() as unknown as T.BroadInit);

  assert.deepEqual(broad, narrow);
  assert.deepEqual(T.Narrow.write(T.Narrow.parse(narrow)), narrow);
  assert.deepEqual(T.Broad.write(T.Broad.parse(broad)), broad);
  const parsed = T.Narrow.parse(narrow);
  assert.equal(parsed.s53, "string 53");
  assert.deepEqual(parsed.item, { kind: "Point", value: { x: 1.5, y: -2 } });
  assert.equal(parsed.items?.[0]?.kind, "Leaf");
  assert.equal(parsed.next?.next?.name, "level 3");
  assert.equal(parsed.leaves?.[0], parsed.leaf, "a table referenced twice parses as one object");
});

test("a table with 64 out-of-line fields keeps them in locals, and one with 65 in an array", () => {
  const source = readFileSync(new URL("../generated/Threshold.ts", import.meta.url), "utf8");
  const write = (table: string): string => {
    const start = source.indexOf(`write($writer: $bq.Writer, $value: ${table}Init): number {`);
    assert.notEqual(start, -1, `${table} has a write`);
    return source.slice(start, source.indexOf("endTable", start));
  };

  assert.doesNotMatch(write("Narrow"), /\$o\b/);
  assert.match(write("Narrow"), /const \$u2 = /);
  assert.match(write("Broad"), /const \$o: number\[\] = \[\];/);
  assert.match(write("Broad"), /\$bq\.pair\(\$o, 2, /);
});
