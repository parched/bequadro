"""Builds instances of generated tables from their annotations, for tests that write and parse every table of a
generated package without knowing its schema."""

import dataclasses
import enum
import importlib
import types
import typing
from typing import Any, TypeAliasType


class Unbuildable(Exception):
    """The table can't be built: its required fields need each other, so no finite graph has one."""


class Builder:
    """Builds values for the classes of one generated package, whose runtime module is runtime."""

    def __init__(self, runtime: types.ModuleType, populate: bool) -> None:
        self._runtime = runtime
        self._populate = populate

    def table(self, cls: type[Any], depth: int = 0) -> Any:
        """An instance of a table or struct class. With populate, every field has a value other than its default, down
        to a few levels of tables; otherwise only fields without defaults have one."""
        if depth > 8:
            raise Unbuildable(cls.__qualname__)

        hints = typing.get_type_hints(cls)
        values: dict[str, Any] = {}
        for field in dataclasses.fields(cls):
            has_default = field.default is not dataclasses.MISSING or field.default_factory is not dataclasses.MISSING
            if has_default and not (self._populate and depth < 3):
                continue
            values[field.name] = self.value(hints[field.name], depth + 1)
        return cls(**values)

    def value(self, hint: Any, depth: int) -> Any:
        origin = typing.get_origin(hint)
        if origin in (types.UnionType, typing.Union):
            choices = [a for a in typing.get_args(hint) if a is not type(None)]
            return self.value(choices[0], depth)
        if isinstance(hint, TypeAliasType):
            union = hint.__value__
            alternatives = typing.get_args(union) if typing.get_origin(union) in (types.UnionType, typing.Union) else (union,)
            members = [m for m in alternatives if m is not self._runtime.UnionUnknown]
            return self.value(members[-1] if self._populate else members[0], depth)
        if origin is list:
            (element,) = typing.get_args(hint)
            values = [self.value(element, depth)]
            if typing.get_origin(element) in (types.UnionType, typing.Union):
                values.append(None)
            return values
        if hint is bool:
            return True
        if hint is int:
            return 1
        if hint is float:
            return 0.5
        if hint is str:
            return "s"
        if hint is bytearray:
            return bytearray(b"\x01\x02")
        if isinstance(hint, type) and issubclass(hint, enum.IntEnum):
            members = list(hint)
            return members[-1] if members else hint(0)
        if isinstance(hint, type) and issubclass(hint, (self._runtime.Table, self._runtime.Struct)):
            return self.table(hint, depth)
        raise AssertionError(f"Unexpected annotation {hint!r}")


def tables(module: types.ModuleType, runtime: types.ModuleType) -> list[type[Any]]:
    """The table classes a generated module declares."""
    return [
        cls
        for cls in vars(module).values()
        if isinstance(cls, type) and cls.__module__ == module.__name__ and issubclass(cls, runtime.Table)
    ]


def check_round_trip(runtime: types.ModuleType, cls: type[Any], populate: bool) -> None:
    """Writes an instance of a table, parses it, and checks that writing the result gives the same bytes, so that
    reading and writing every field agree (SER-005)."""
    value = Builder(runtime, populate).table(cls)
    data: bytes = runtime.write(value)
    parsed = runtime.parse(cls, data)
    assert type(parsed) is cls
    assert runtime.write(parsed) == data, cls.__qualname__


def import_runtime(package: str) -> types.ModuleType:
    return importlib.import_module(package + ".bequadro")
