// SER-003, SER-009, TS-011: writing and parsing tables nested 64 levels deep, the limit, doesn't overflow the default
// stack, however wide the tables are. Each case runs in a fresh process, with the default stack and no JIT warm-up,
// where stack frames are largest.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";

const generated = new URL("../generated/", import.meta.url).href;

/** Runs an ES module in a fresh Node process, and returns what it printed, failing on any error. */
function runFresh(script: string): string {
  const result = spawnSync(process.execPath, ["--input-type=module", "--eval", script], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout.trim();
}

const wide = `
  import * as W from "${generated}Wide.ts";
  import { FlatBufferWriteError } from "${generated}bequadro.ts";

  // Every field set, through next (a table field) or kids (a vector of unions), at every level
  function chain(depth, throughUnions) {
    let node;
    for (let level = depth; level >= 1; level--) {
      const value = {};
      for (let i = 0; i < 300; i++) {
        value["i" + i] = level + i;
        value["s" + i] = "s" + i;
      }

      if (node !== undefined) {
        if (throughUnions) value.kids = [{ kind: "Wide", value: node }];
        else value.next = node;
      }

      node = value;
    }

    return node;
  }

  for (const throughUnions of [false, true]) {
    let parsed = W.Wide.parse(W.Wide.write(chain(64, throughUnions)));
    let depth = 1;
    while ((parsed = throughUnions ? parsed.kids?.[0]?.value : parsed.next) !== undefined) {
      if (parsed.i299 !== depth + 1 + 299 || parsed.s299 !== "s299") throw new Error("wrong value at " + depth);
      depth++;
    }

    if (depth !== 64) throw new Error("parsed " + depth + " levels");
    try {
      W.Wide.write(chain(65, throughUnions));
      throw new Error("65 levels were written");
    } catch (error) {
      if (!(error instanceof FlatBufferWriteError)) throw error;
    }
  }

  console.log("ok");
`;

test("a table with 600 fields nests 64 levels deep through a table field or a union vector, in a fresh process", () => {
  assert.equal(runFresh(wide), "ok");
});

test("tables with 64 and 65 out-of-line fields, kept in locals and an array, nest 64 levels deep, in a fresh process", () => {
  const script = `
    import * as T from "${generated}Threshold.ts";

    for (const [name, table] of [["Narrow", T.Narrow], ["Broad", T.Broad]]) {
      for (const throughUnions of [false, true]) {
        let node;
        for (let level = 64; level >= 1; level--) {
          const value = { count: level, name: "n", names: ["a"], values: [1] };
          for (let i = 0; i < 54; i++) value["s" + i] = "s" + i;
          if (node !== undefined) {
            if (throughUnions) value.items = [{ kind: name, value: node }];
            else value.next = node;
          }

          node = value;
        }

        let parsed = table.parse(table.write(node));
        let depth = 1;
        while ((parsed = throughUnions ? parsed.items?.[0]?.value : parsed.next) !== undefined) depth++;
        if (depth !== 64) throw new Error(name + " parsed " + depth + " levels");
      }
    }

    console.log("ok");
  `;
  assert.equal(runFresh(script), "ok");
});

test("Kitchen.Sink, with 37 fields, nests 64 levels deep through required_children, in a fresh process", () => {
  const script = `
    import * as Kitchen from "${generated}Bequadro_/Tests/Kitchen.ts";

    let sink = { title: "leaf", requiredPosition: {}, requiredShape: { kind: "Marker", value: { id: 1 } }, requiredChildren: [] };
    for (let i = 1; i < 64; i++) {
      sink = { ...sink, title: "level", requiredChildren: [sink] };
    }

    let parsed = Kitchen.Sink.parse(Kitchen.Sink.write(sink));
    let depth = 1;
    while (parsed.requiredChildren.length > 0) {
      parsed = parsed.requiredChildren[0];
      depth++;
    }

    console.log(depth);
  `;
  assert.equal(runFresh(script), "64");
});
