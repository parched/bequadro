// TS-002, TS-003, NAM-001: code generated from schemas/Names.fbs, which names declarations, members and fields after
// every TypeScript keyword and contextual keyword, the names generated code uses for its own parameters and locals, and
// the built-ins the runtime uses. tsc compiling it is most of the test; these check that the code also works: a name
// that shadows a parameter, such as an enum `init` read inside `create(init)`, compiles but reads the wrong value.
import assert from "node:assert/strict";
import { test } from "node:test";
import * as Enums from "../generated/Names/Enums.ts";
import * as Members from "../generated/Names/Members.ts";
import * as Structs from "../generated/Names/Structs.ts";
import * as Tables from "../generated/Names/Tables.ts";
import * as Unions from "../generated/Names/Unions.ts";

type Loose = Record<string, unknown>;

/** Every field of a table, from an object that create filled in, set by its name: f_ fields, v_ vectors, o_ optionals. */
function fill(created: object, single: (key: string) => unknown, vector: (key: string) => unknown[]): Loose {
  const value: Loose = { ...created };
  for (const key of Object.keys(value)) {
    if (key.startsWith("v")) {
      value[key] = vector(key);
    } else if (key.startsWith("f")) {
      value[key] = single(key);
    }
  }

  return value;
}

test("declarations named like generated parameters keep their meaning: an enum default named init, a struct named b", () => {
  const holder = Enums.Holder.create();
  assert.equal(holder.fInit, Enums.init.B);
  assert.equal(holder.fA, Enums.a.B);
  assert.equal(holder.fValue, Enums.value.B);
  assert.equal(holder.fKeyof, Enums.keyof_.B);
  assert.deepEqual(Structs.Outer.create().fInit, { x: 0 });
  assert.ok(Structs.Outer.equals(Structs.Outer.create(), Structs.Outer.create()));
  assert.ok(!Structs.Outer.equals(Structs.Outer.create(), { ...Structs.Outer.create(), fB: { x: 1 } }));
  assert.ok(Unions.Holder.equals(Unions.Holder.create(), Unions.Holder.create()));
});

test("tables named after keywords and generated names round trip, and compare with their own equals", () => {
  const created = Tables.Holder.create();
  const value = fill(
    created,
    (key) => ({ x: key.length }),
    (key) => [{ x: key.length }, { x: 2 }],
  );
  value["uAny"] = { kind: "init", value: { x: 3 } };
  value["anys"] = [{ kind: "keyof", value: { x: 4 } }, { kind: "Object", value: { x: 5 } }];

  const parsed = Tables.Holder.parse(Tables.Holder.write(value as Tables.HolderInit));

  assert.deepEqual(parsed, value);
  assert.ok(Tables.Holder.equals(parsed, Tables.Holder.parse(Tables.Holder.write(value as Tables.HolderInit))));
  assert.ok(!Tables.Holder.equals(parsed, { ...parsed, fInit: { x: 99 } }));
});

test("structs named after keywords and generated names round trip, and compare with their own equals", () => {
  const value = fill(
    Structs.Holder.create(),
    (key) => ({ x: key.length }),
    (key) => [{ x: key.length }],
  );
  value["outer"] = Structs.Outer.create({ fInit: { x: 7 }, fA: { x: 8 } });
  value["uAny"] = { kind: "b", value: { x: 9 } };

  const parsed = Structs.Holder.parse(Structs.Holder.write(value as Structs.HolderInit));

  assert.deepEqual(parsed, value);
  assert.ok(Structs.Holder.equals(parsed, value as Structs.Holder));
  assert.ok(!Structs.Holder.equals(parsed, { ...parsed, outer: Structs.Outer.create() }));
});

test("enums named after keywords and generated names round trip", () => {
  const created = Enums.Holder.create();
  const value: Loose = { ...created };
  for (const key of Object.keys(value)) {
    value[key] = key === "outer" ? Enums.Outer.create() : key.startsWith("v") ? [0, 1] : key.startsWith("o") ? 1 : 0;
  }

  assert.deepEqual(Enums.Holder.parse(Enums.Holder.write(value as Enums.HolderInit)), value);
  assert.deepEqual(Enums.Holder.parse(Enums.Holder.write({})), created);
});

test("unions named after keywords and generated names round trip, and compare with their own equals", () => {
  const value = fill(
    Unions.Holder.create(),
    (key) => (key.length % 2 === 0 ? { kind: "Member", value: { x: 3 } } : { kind: "Point", value: { x: 4 } }),
    () => [undefined, { kind: "Point", value: { x: 5 } }],
  );

  const parsed = Unions.Holder.parse(Unions.Holder.write(value as Unions.HolderInit));

  assert.deepEqual(parsed, value);
  assert.ok(Unions.Holder.equals(parsed, value as Unions.Holder));
  assert.ok(!Unions.Holder.equals(parsed, { ...parsed, fInit: undefined }));
});

test("fields, enum values and union labels named after keywords and generated names round trip", () => {
  const value: Loose = { ...Members.T.create() };
  let i = 0;
  for (const key of Object.keys(value)) {
    value[key] = i++;
  }

  const labels = Object.keys(Members.E);
  value["enumValue"] = Members.E.yield;
  value["unionValue"] = { kind: "constructor", value: { x: 1 } };
  value["structValue"] = Members.S.create({ constructor_: 1, init: 2, keyof: 3 });

  const parsed = Members.T.parse(Members.T.write(value as Members.TInit));

  assert.equal(new Set(Object.values(Members.E)).size, labels.length);
  assert.deepEqual(parsed, value);
  assert.ok(Members.T.equals(parsed, { ...parsed }), "union members are plain tables, so only the same object is equal");
  assert.ok(Members.S.equals(parsed.structValue!, Members.S.create({ constructor_: 1, init: 2, keyof: 3 })));
});
