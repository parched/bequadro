"""QUA-003, SER-005: parsing a buffer the C# tests wrote and writing it back gives exactly the bytes C# writes. The
fixtures come from CrossLanguageFixtureTests, which build.py runs first."""

import json
from collections.abc import Callable
from pathlib import Path

import pytest

from generated import bequadro
from generated.Bequadro_.Tests import Equality, Kitchen, Limits

FIXTURES = Path(__file__).parents[3] / "artifacts" / "cross-language"

type Rewrite = Callable[[bytes, bool], bytes]


def _rewrite(table_type: type[bequadro.Table]) -> Rewrite:
    def rewrite(data: bytes, share_strings: bool) -> bytes:
        parsed = bequadro.parse(table_type, data, share_strings=share_strings)
        return bequadro.write(parsed, share_strings=share_strings)

    return rewrite


REWRITES: dict[str, Rewrite] = {
    "Bequadro.Tests.Kitchen.Sink": _rewrite(Kitchen.Sink),
    "Bequadro.Tests.Kitchen.Graph": _rewrite(Kitchen.Graph),
    "Bequadro.Tests.Kitchen.Node": _rewrite(Kitchen.Node),
    "Bequadro.Tests.Equality.Measure": _rewrite(Equality.Measure),
    "Bequadro.Tests.Limits.LargestHolder": _rewrite(Limits.LargestHolder),
}


def _fixtures() -> list[dict[str, object]]:
    manifest: list[dict[str, object]] = json.loads((FIXTURES / "manifest.json").read_text())
    return manifest


@pytest.mark.parametrize("fixture", _fixtures(), ids=lambda f: str(f["name"]))
def test_rewriting_a_fixture_gives_the_bytes_csharp_writes(fixture: dict[str, object]) -> None:
    rewrite = REWRITES[str(fixture["root"])]
    data = (FIXTURES / f"{fixture['name']}.input.bin").read_bytes()
    expected = (FIXTURES / f"{fixture['name']}.expected.bin").read_bytes()
    assert rewrite(data, bool(fixture["shareStrings"])) == expected


TABLES: dict[str, type[bequadro.Table]] = {
    "Bequadro.Tests.Kitchen.Sink": Kitchen.Sink,
    "Bequadro.Tests.Kitchen.Graph": Kitchen.Graph,
    "Bequadro.Tests.Kitchen.Node": Kitchen.Node,
    "Bequadro.Tests.Equality.Measure": Equality.Measure,
    "Bequadro.Tests.Limits.LargestHolder": Limits.LargestHolder,
}


@pytest.mark.parametrize(
    "fixture",
    [f for f in _fixtures() if (FIXTURES / f"{f['name']}.expected.bin").stat().st_size <= 2048],
    ids=lambda f: str(f["name"]),
)
def test_rewriting_a_fixture_gives_the_bytes_csharp_writes_whenever_the_buffer_grows(fixture: dict[str, object]) -> None:
    """The writer starts with 1024 bytes, so most fixtures never make it grow. Starting from every smaller capacity
    makes each allocation in turn be the one that grows the buffer, which must not change the bytes."""
    table_type = TABLES[str(fixture["root"])]
    share_strings = bool(fixture["shareStrings"])
    data = (FIXTURES / f"{fixture['name']}.input.bin").read_bytes()
    expected = (FIXTURES / f"{fixture['name']}.expected.bin").read_bytes()
    value = bequadro.parse(table_type, data, share_strings=share_strings)
    for capacity in range(len(expected) + 1):
        writer = bequadro.Writer(share_strings, capacity)
        actual = writer.finish(writer.write_table(table_type.__bequadro__, value))
        assert actual == expected, f"starting with {capacity} bytes"


def test_parsing_a_csharp_buffer_keeps_shared_tables_shared() -> None:
    graph = bequadro.parse(Kitchen.Graph, (FIXTURES / "graph-shared-nodes.input.bin").read_bytes())
    a, b, c, again = graph.nodes
    assert a.name == "a"
    assert again is a
    assert b.neighbors is not None and b.neighbors[0] is a
    assert c.neighbors is not None and c.neighbors[1] is b
    assert graph.lines[0].end is b
    assert graph.root is c


def test_parsing_a_buffer_built_with_the_official_builder_reads_every_field_kind() -> None:
    sink = bequadro.parse(Kitchen.Sink, (FIXTURES / "google-sink.input.bin").read_bytes())
    assert sink.name == "google"
    assert sink.flag is True
    assert sink.small == -9
    assert sink.count == 5
    assert sink.wide == 123
    assert sink.precise == 6.5
    assert sink.optional_int == 0
    assert sink.ratio == 0.5, "absent fields read as the schema default"
    assert sink.doubles == [1.5, 2.5]
    assert sink.strings == ["x", "yz"]
    assert sink.children is not None
    assert sink.children[0] is sink.children[1] is sink.child
