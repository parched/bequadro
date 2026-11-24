// TS-002: importing any module first works whatever the cycles between namespaces, because no module uses an import
// while it is being evaluated. Each case runs in a fresh process, since a module is only evaluated once per process.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";

const generated = new URL("../generated/", import.meta.url).href;

for (const [first, second] of [
  ["A", "B"],
  ["B", "A"],
]) {
  test(`importing Cycles.${first} before Cycles.${second} works`, () => {
    const script = `
      const ${first} = await import("${generated}Cycles/${first}.ts");
      const ${second} = await import("${generated}Cycles/${second}.ts");

      const pair = B.Pair.create();
      if (pair.a.tint !== B.Tint.Dim || pair.color !== A.Color.Red) throw new Error("struct zeros");
      const node = A.Node.create({ point: A.Point.create({ x: 1 }) });
      if (node.tint !== B.Tint.Bright) throw new Error("enum default");
      const leaf = B.Leaf.create({ pair, node, either: { kind: "Pair", value: pair } });
      if (leaf.color !== A.Color.Green) throw new Error("enum default");
      node.either = { kind: "Leaf", value: B.Leaf.create({}) };
      const parsed = B.Leaf.parse(B.Leaf.write(leaf));
      if (!B.Leaf.equals(parsed, leaf)) throw new Error("round trip");
      console.log("ok");
    `;
    const result = spawnSync(process.execPath, ["--input-type=module", "--eval", script], { encoding: "utf8" });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), "ok");
  });
}
