"""NAM-001, PY-003: declarations with names Python reserves, or that generated code could use itself, generate code
that writes and parses (schemas/Locals.fbs and schemas/Names.fbs). pyright and mypy check the same code."""

import enum
import importlib
from typing import Any

import pytest

import generated
from generated import PyNames, bequadro
from generated.PyNames import Enums, Tables

from .values import Builder, check_round_trip, tables

MODULES = [
    "generated.bequadro_root",
    "generated.PyNames",
    "generated.PyNames.Tables",
    "generated.PyNames.Enums",
    "generated.PyNames.Sub",
    "generated.PyNames.Sub.annotations_",
    "generated.PyNames.Shared_",
]


@pytest.mark.parametrize("module", MODULES)
@pytest.mark.parametrize("populate", [False, True], ids=["defaults", "populated"])
def test_tables_named_like_generated_locals_write_and_parse(module: str, populate: bool) -> None:
    classes = tables(importlib.import_module(module), bequadro)
    assert classes
    for cls in classes:
        check_round_trip(bequadro, cls, populate)


def test_value_equality_works_for_classes_named_self_and_other() -> None:
    builder = Builder(bequadro, populate=True)
    a: Any = builder.table(Tables.UsesTables)
    b: Any = builder.table(Tables.UsesTables)
    assert a == b
    assert Tables.self(a=1) == Tables.self(a=1)
    assert Tables.other(a=1) != Tables.other(a=2)
    parsed = bequadro.parse(Tables.UsesTables, bequadro.write(a))
    assert parsed == a


def test_enum_values_named_like_int_attributes_are_renamed() -> None:
    """The schema's IntAttributes lists every public attribute of an IntEnum class and its members in the Python
    running the test, so a version that adds one fails here until PY-003's list has it too."""
    reserved = {n for n in dir(int) + dir(enum.IntEnum) + dir(type) if not n.startswith("_")} | {"name", "value"}
    assert {n + "_" for n in reserved} <= set(PyNames.IntAttributes.__members__)
    assert PyNames.IntAttributes.real_.real == 9
    assert PyNames.UsesNames().attribute is PyNames.IntAttributes.imag_


def test_declarations_named_like_module_bindings_are_renamed() -> None:
    assert PyNames.annotations_(a=1).a == 1
    assert importlib.import_module("generated.PyNames.Sub.annotations_").Inner(a=1).a == 1
    assert isinstance(generated.bequadro_(), bequadro.Table)
    assert isinstance(generated.bequadro_root_(), bequadro.Table)
    assert importlib.import_module("generated.bequadro") is bequadro, "the runtime module keeps its name"
    assert "bequadro" not in vars(generated) or vars(generated)["bequadro"] is bequadro
    assert PyNames.Shared(a=1).a == 1
    assert importlib.import_module("generated.PyNames.Shared_").InShared(a=1).a == 1


def test_enums_named_like_generated_locals_read_their_values() -> None:
    value = Enums.UsesEnumsTable(v_x=[Enums.x.B], e_p=Enums.p.A)
    parsed = bequadro.parse(Enums.UsesEnumsTable, bequadro.write(value))
    assert parsed.v_x == [Enums.x.B]
    assert parsed.e_p is Enums.p.A
    assert parsed.e_d is Enums.d.B
