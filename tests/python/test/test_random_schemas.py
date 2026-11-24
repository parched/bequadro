"""QUA-003: the code generated for the generator tests' random schemas imports, whatever its names and import cycles,
and writes and parses every table. build.py generates it into generated_random first."""

import importlib
from pathlib import Path

import pytest

from .values import Unbuildable, check_round_trip, import_runtime, tables

RANDOM = Path(__file__).parents[1] / "generated_random"


def _modules() -> list[str]:
    if not RANDOM.exists():
        return []
    return sorted(
        ".".join(("generated_random", *path.relative_to(RANDOM).with_suffix("").parts)).removesuffix(".__init__")
        for path in RANDOM.glob("s*/**/*.py")
        if path.name != "bequadro.py"
    )


MODULES = _modules()


def test_random_schemas_were_generated() -> None:
    """Without this, the tests below would pass with no cases."""
    assert MODULES, f"{RANDOM} has no generated code: run build.py --check, or the C# tests and then build.py's steps"


@pytest.mark.parametrize("module", MODULES)
def test_generated_module_imports(module: str) -> None:
    importlib.import_module(module)


@pytest.mark.parametrize("module", MODULES)
@pytest.mark.parametrize("populate", [False, True], ids=["defaults", "populated"])
def test_every_table_writes_and_parses(module: str, populate: bool) -> None:
    """Each table is written with only the fields it requires, then with every field set, down to a few levels."""
    runtime = import_runtime(".".join(module.split(".")[:2]))
    for cls in tables(importlib.import_module(module), runtime):
        try:
            check_round_trip(runtime, cls, populate)
        except Unbuildable:
            # Required fields that need each other: no finite graph has one, so there is nothing to write
            pass
