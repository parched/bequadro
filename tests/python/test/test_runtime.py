"""QUA-003: the Python runtime and generated code, mirroring the C# runtime tests (PY-004 to PY-011)."""

import array
import dataclasses
import math
import random
import re
import struct
import sys
from collections.abc import Callable
from pathlib import Path
from typing import assert_never

import pytest

from generated import bequadro
from generated.bequadro import FlatBufferDataError, FlatBufferWriteError, UnionUnknown
from generated.Bequadro_.Tests import Common, Equality, Kitchen, Limits, Shapes
from generated.Deep import N, V
from generated.PyVectors import Small, Vectors

FIXTURES = Path(__file__).parents[3] / "artifacts" / "cross-language"


def minimal_sink() -> Kitchen.Sink:
    return Kitchen.Sink(
        title="minimal",
        required_position=Common.Vec3(x=1, y=2, z=3),
        required_shape=Shapes.Circle(center=Common.Vec3()),
        required_children=[],
    )


def round_trip[T: bequadro.Table](value: T) -> T:
    return bequadro.parse(type(value), bequadro.write(value))


def test_construction_fills_in_defaults_and_round_trips() -> None:
    parsed = round_trip(minimal_sink())

    assert parsed.title == "minimal"
    assert parsed.name is None
    assert parsed.count == 42
    assert parsed.flag_default_true is True
    assert parsed.wide == -(2**63)
    assert parsed.unsigned_wide == 2**64 - 1
    assert math.isnan(parsed.not_a_number)
    assert parsed.infinite == -math.inf
    assert parsed.color is Common.Color.Blue
    assert parsed.optional_int is None
    assert parsed.required_position == Common.Vec3(x=1, y=2, z=3)
    assert isinstance(parsed.required_shape, Shapes.Circle)
    assert parsed.required_shape.radius == 1
    assert parsed.required_children == []


def test_classes_are_final_keyword_only_slotted_dataclasses() -> None:
    assert dataclasses.is_dataclass(Kitchen.Sink)
    assert not hasattr(minimal_sink(), "__dict__")
    with pytest.raises(TypeError):
        Common.Vec3(1, 2, 3)  # type: ignore[call-arg]
    with pytest.raises(TypeError):
        Kitchen.Sink()  # type: ignore[call-arg]


def test_scalars_equal_to_their_defaults_are_not_written() -> None:
    sink = minimal_sink()
    sink.count = 42
    sink.small = -3
    sink.ratio = 0.5
    sink.not_a_number = math.nan
    assert bequadro.write(sink) == bequadro.write(minimal_sink())


def test_negative_zero_equals_a_zero_default_and_nan_is_written() -> None:
    sink = minimal_sink()
    sink.medium = 0
    sink.precise = math.nan
    assert bequadro.write(sink) != bequadro.write(minimal_sink())
    sink.precise = 0.925
    assert bequadro.write(sink) == bequadro.write(minimal_sink())


def test_a_negative_zero_default_reads_as_negative_zero_and_a_zero_of_either_sign_equals_it() -> None:
    assert math.copysign(1, Kitchen.SignedZero().negative) == -1
    parsed = bequadro.parse(Kitchen.SignedZero, bequadro.write(Kitchen.Empty()))
    assert math.copysign(1, parsed.negative) == -1
    assert math.copysign(1, parsed.negative_float) == -1
    defaults = bequadro.write(Kitchen.SignedZero())
    assert bequadro.write(Kitchen.SignedZero(negative=0.0, negative_float=0.0, positive=-0.0)) == defaults
    assert math.copysign(1, bequadro.parse(Kitchen.SignedZero, defaults).positive) == 1
    assert round_trip(Kitchen.SignedZero(negative=1.5)).negative == 1.5


def test_float_fields_are_written_at_32_bit_precision() -> None:
    sink = minimal_sink()
    sink.ratio = 0.1
    assert round_trip(sink).ratio == bequadro.round_float32(0.1)
    sink.ratio = 1e40
    assert round_trip(sink).ratio == math.inf


def test_an_object_referenced_twice_is_written_once_and_parses_as_one_object() -> None:
    node = Kitchen.Node(name="shared")
    graph = Kitchen.Graph(nodes=[node, node], lines=[Kitchen.Line(start=node, end=node)], root=node)
    copies = Kitchen.Graph(
        nodes=[Kitchen.Node(name="shared"), Kitchen.Node(name="shared")],
        lines=[Kitchen.Line(start=Kitchen.Node(name="shared"), end=Kitchen.Node(name="shared"))],
        root=Kitchen.Node(name="shared"),
    )

    data = bequadro.write(graph)
    parsed = bequadro.parse(Kitchen.Graph, data)

    assert len(data) < len(bequadro.write(copies))
    assert parsed.nodes[0] is parsed.nodes[1]
    assert parsed.lines[0].start is parsed.root
    assert bequadro.parse(Kitchen.Graph, data).root is not parsed.root, "separate parses never share objects"


def test_a_reference_cycle_raises_instead_of_looping() -> None:
    node = Kitchen.Node(name="loop")
    node.self = node
    with pytest.raises(FlatBufferWriteError, match="reference cycle"):
        bequadro.write(node)


def chain(depth: int) -> Kitchen.Node:
    node = Kitchen.Node(name="leaf")
    for _ in range(1, depth):
        node = Kitchen.Node(self=node)
    return node


def test_nesting_up_to_64_levels_works_whatever_the_recursion_limit() -> None:
    limit = sys.getrecursionlimit()
    sys.setrecursionlimit(100)
    try:
        assert bequadro.parse(Kitchen.Node, bequadro.write(chain(64))).self is not None
    finally:
        sys.setrecursionlimit(limit)
    with pytest.raises(
        FlatBufferWriteError,
        match=r"more than 64 levels deep, because the buffer couldn't be parsed: an instance of "
        r"'Bequadro\.Tests\.Kitchen\.Node' is at level 65\.$",
    ):
        bequadro.write(chain(65))


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        ("count", "42", "Expected an int for field 'count' of 'Bequadro.Tests.Kitchen.Sink'"),
        ("count", 1.5, "Expected an int for field 'count'"),
        ("tiny", 256, "out of range for ubyte"),
        ("small", -129, "out of range for byte"),
        ("unsigned_wide", -1, "out of range for ulong"),
        ("flag", 1, "Expected a bool for field 'flag'"),
        ("color", 300, "field 'color'"),
        ("ratio", "x", "Expected a float for field 'ratio'"),
        ("name", 5, "Expected a str for field 'name'"),
        ("strings", ["a", None], "null element"),
        ("doubles", "no", "Expected a list for field 'doubles'"),
        ("bytes", "text", "Expected a bytes-like object for field 'bytes'"),
        ("bytes", [1, 2, 3], "Expected a bytes-like object for field 'bytes' of 'Bequadro.Tests.Kitchen.Sink', got list"),
        ("ratio", 10**400, "int of 1329 bits is out of range for float field 'ratio' of 'Bequadro.Tests.Kitchen.Sink'"),
        ("precise", -(10**400), "out of range for double field 'precise'"),
        ("count", 10**5000, "int of 16610 bits is out of range for int field 'count'"),
        ("children", [Kitchen.Node()], "Expected a Sink for field 'children[0]' of 'Bequadro.Tests.Kitchen.Sink'"),
        ("position", Common.Vec3(x="1"), "field 'position.x'"),  # type: ignore[arg-type]
        ("position", (1, 2, 3), "Expected a Vec3 for field 'position'"),
        ("child", Kitchen.Node(), "Expected a Sink for field 'child' of 'Bequadro.Tests.Kitchen.Sink', got Node"),
        ("title", None, "Required field 'title' of table 'Bequadro.Tests.Kitchen.Sink' is null"),
        ("required_position", None, "Required field 'required_position'"),
    ],
    # The default id of a huge int would be its digits, which str() refuses
    ids=lambda value: "huge int" if isinstance(value, int) and value.bit_length() > 1000 else None,
)
def test_the_writer_rejects_values_of_the_wrong_type_or_out_of_range(field: str, value: object, message: str) -> None:
    sink = minimal_sink()
    setattr(sink, field, value)
    with pytest.raises(FlatBufferWriteError, match=re.escape(message)):
        bequadro.write(sink)


def test_ints_are_accepted_for_floats_and_bools_for_ints() -> None:
    sink = minimal_sink()
    sink.precise = 2
    sink.count = True
    parsed = round_trip(sink)
    assert parsed.precise == 2.0
    assert parsed.count == 1


def test_a_subclass_of_a_generated_table_is_rejected() -> None:
    class Derived(Kitchen.Empty):  # type: ignore[misc]
        pass

    with pytest.raises(TypeError, match="isn't an instance of a generated table"):
        bequadro.write(Derived())


def test_unions_round_trip_with_none_as_none() -> None:
    holder = Shapes.Holder(
        required_shape=Shapes.Marker(id=1),
        required_strict=Shapes.Tag(code=9, weight=1.5),
        shapes=[None, Shapes.Polygon(points=[Common.Vec3(x=1)])],
        stricts=[Shapes.Marker(id=3)],
    )

    parsed = round_trip(holder)

    assert parsed.shape is None
    assert parsed.shapes is not None
    assert parsed.shapes[0] is None
    assert isinstance(parsed.shapes[1], Shapes.Polygon)
    assert parsed.shapes[1].points == [Common.Vec3(x=1)]
    assert parsed.required_strict == Shapes.Tag(code=9, weight=1.5)
    assert parsed.stricts == [Shapes.Marker(id=3)]


def describe(shape: Shapes.Shape | None) -> str:
    match shape:
        case Shapes.Circle(radius=radius):
            return f"circle {radius}"
        case Shapes.Polygon(points=points):
            return f"polygon {len(points)}"
        case Shapes.Marker(id=marker):
            return f"marker {marker}"
        case UnionUnknown(discriminator=discriminator):
            return f"unknown {discriminator}"
        case None:
            return "none"
        case _:
            assert_never(shape)


def test_match_selects_union_members_and_type_checkers_check_exhaustiveness() -> None:
    assert describe(Shapes.Marker(id=7)) == "marker 7"
    assert describe(None) == "none"
    assert describe(UnionUnknown(discriminator=9)) == "unknown 9"


def test_unwritable_unions_raise() -> None:
    holder = Shapes.Holder(required_shape=UnionUnknown(discriminator=9), required_strict=Shapes.Marker(id=2))
    with pytest.raises(FlatBufferWriteError, match=r"unknown member \(discriminator 9\)"):
        bequadro.write(holder)

    holder.required_shape = Kitchen.Empty()  # type: ignore[assignment]
    with pytest.raises(FlatBufferWriteError, match="has no member of type Empty"):
        bequadro.write(holder)

    holder.required_shape = Shapes.Marker(id=1)
    holder.stricts = [None]  # type: ignore[list-item]
    with pytest.raises(FlatBufferWriteError, match="'Bequadro.Tests.Shapes.StrictShape' because it has no value"):
        bequadro.write(holder)


def parse_fixture(name: str) -> Shapes.Holder:
    return bequadro.parse(Shapes.Holder, (FIXTURES / f"{name}.parse.bin").read_bytes())


def test_unknown_discriminators_parse_as_unknown_unless_the_union_is_exhaustive() -> None:
    holder = parse_fixture("holder-unknown")
    assert holder.required_shape == UnionUnknown(discriminator=201)
    assert holder.shapes == [UnionUnknown(discriminator=200), Shapes.Marker(id=1)]
    assert parse_fixture("holder-none-in-vector").shapes == [None, Shapes.Marker(id=1)]
    with pytest.raises(FlatBufferDataError, match="discriminator 200 .*StrictShape"):
        parse_fixture("holder-exhaustive-unknown")
    with pytest.raises(FlatBufferDataError, match="NONE .*StrictShape"):
        parse_fixture("holder-none-in-strict-vector")


def test_enum_values_the_schema_does_not_define_are_kept() -> None:
    value = Common.Color(9)
    assert value == 9
    assert isinstance(value, Common.Color)
    assert value not in list(Common.Color)
    assert value.name == "9"
    assert repr(value) == "<Color.9: 9>"
    assert str(value) == "9"
    assert Common.Signed(-7).name == "-7"
    sink = minimal_sink()
    sink.color = value
    assert round_trip(sink).color == 9


def test_value_equality_compares_values_nan_equals_nan_and_plain_tables_by_identity() -> None:
    source = Equality.Source(name="s")

    def measure() -> Equality.Measure:
        return Equality.Measure(
            name="m",
            amount=math.nan,
            position=Common.Vec3(x=1, y=2, z=3),
            unit=Equality.Unit(symbol="kg"),
            source=source,
            item=Common.Vec3(x=1, y=2, z=3),
            values=[0.0, -0.0, math.nan],
            units=[Equality.Unit(symbol="a")],
        )

    a = round_trip(measure())
    b = round_trip(measure())
    assert a != b, "each parse has its own Source, which compares by identity"
    b.source = a.source
    assert a == b
    b.values = []
    assert a != b
    b.values = None
    assert a != b, "None isn't equal to a vector"
    assert Common.Vec3(x=math.nan) == Common.Vec3(x=math.nan, y=-0.0)
    with pytest.raises(TypeError):
        hash(Common.Vec3())
    with pytest.raises(TypeError):
        hash(measure())
    assert hash(source) == hash(source), "tables without value equality keep identity hashing"


def test_malformed_buffers_only_ever_raise_flat_buffer_data_error() -> None:
    original = (FIXTURES / "sink-full.input.bin").read_bytes()
    generator = random.Random(12345)
    for case in range(2000):
        if case % 2 == 0:
            data = original[: generator.randrange(len(original))]
        else:
            mutable = bytearray(original)
            for _ in range(generator.randrange(1, 5)):
                mutable[generator.randrange(len(mutable))] = generator.randrange(256)
            data = bytes(mutable)
        try:
            bequadro.parse(Kitchen.Sink, data)
        except FlatBufferDataError:
            pass


def test_parse_accepts_any_bytes_like_object() -> None:
    data = bequadro.write(minimal_sink())
    padded = bytearray(8) + data + bytearray(8)
    assert bequadro.parse(Kitchen.Sink, memoryview(padded)[8 : 8 + len(data)]).title == "minimal"
    assert bequadro.parse(Kitchen.Sink, bytearray(data)).title == "minimal"


def test_parse_rejects_a_class_that_is_not_a_generated_table() -> None:
    with pytest.raises(TypeError, match="isn't a generated table class"):
        bequadro.parse(Common.Vec3, b"")  # type: ignore[type-var]


def test_strings_with_lone_surrogates_are_written_with_replacement_characters() -> None:
    sink = minimal_sink()
    sink.name = "a\ud800b"
    assert round_trip(sink).name == "a�b"


def test_strings_with_surrogate_pairs_are_written_as_the_characters_they_encode() -> None:
    """A pair held as two code points is what a UTF-16 string holds, so it is written as .NET writes that string."""

    def written(name: str) -> bytes:
        sink = minimal_sink()
        sink.name = name
        return bequadro.write(sink)

    # Built with chr, because the compiler combines a pair of escapes in a literal into one character
    high, low, other_high, face, replacement = chr(0xD83D), chr(0xDE00), chr(0xD800), chr(0x1F600), chr(0xFFFD)
    assert len(high + low) == 2
    assert written(high + low) == written(face)
    assert written("a" + high + low + other_high + "b") == written("a" + face + replacement + "b")
    assert written(low + high) == written(replacement * 2), "a low surrogate before a high one isn't a pair"
    assert written(high + high + low) == written(replacement + face)
    assert written(high) == written(replacement)


# Writing and parsing from a deep stack (PY-011)


def at_depth[T](frames: int, action: Callable[[], T]) -> T:
    """Calls action with about frames more frames on the stack."""
    return action() if frames <= 0 else at_depth(frames - 1, action)


def chain_through_next(depth: int) -> N:
    node = N()
    for _ in range(1, depth):
        node = N(next=node)
    return node


def chain_through_union_vectors(depth: int) -> N:
    node = N()
    for _ in range(1, depth):
        node = N(kids=[node, V(x=2)])
    return node


@pytest.mark.parametrize(
    ("limit", "caller_depth", "chain"),
    [
        (5000, 1500, chain_through_union_vectors),
        (5000, 3200, chain_through_next),
        (3000, 2500, chain_through_union_vectors),
        (20000, 17000, chain_through_union_vectors),
    ],
)
def test_nesting_up_to_64_levels_works_from_a_deep_stack(
    limit: int, caller_depth: int, chain: Callable[[int], N]
) -> None:
    value = chain(64)
    previous = sys.getrecursionlimit()
    sys.setrecursionlimit(limit)
    try:
        data = at_depth(caller_depth, lambda: bequadro.write(value))
        parsed = at_depth(caller_depth, lambda: bequadro.parse(N, data))
    finally:
        sys.setrecursionlimit(previous)
    assert bequadro.write(parsed) == data


def test_the_recursion_limit_stays_raised_for_the_process() -> None:
    previous = sys.getrecursionlimit()
    sys.setrecursionlimit(100)
    try:
        bequadro.write(Kitchen.Empty())
        # 4 frames for each of 65 levels, the limit and one more, and a margin of 1000, above the caller's frames
        assert sys.getrecursionlimit() > 4 * 65 + 1000
    finally:
        sys.setrecursionlimit(previous)


# Buffers (PY-011, SER-010, SER-012)


def test_a_failed_parse_keeps_no_export_of_the_buffer() -> None:
    """Appending to a buffer that was too short to parse, then retrying, is how input arriving in pieces is read."""
    whole = bequadro.write(Kitchen.Node(self=Kitchen.Node(name="n")))
    data = bytearray(whole[:10])
    with pytest.raises(FlatBufferDataError) as error:
        bequadro.parse(Kitchen.Node, data)
    data.extend(whole[10:])
    assert error.value is not None, "the exception and its traceback are still alive"
    assert bequadro.parse(Kitchen.Node, data).self is not None
    data.extend(b"\0")


def test_a_failed_write_keeps_no_export_of_a_bytes_field() -> None:
    data = bytearray(b"\x01\x02")
    sink = minimal_sink()
    sink.bytes = data
    sink.strings = ["a", None]  # type: ignore[list-item]
    with pytest.raises(FlatBufferWriteError):
        bequadro.write(sink)
    data.extend(b"\x03")


def test_non_contiguous_buffers_are_copied() -> None:
    data = bequadro.write(minimal_sink())
    spread = bytearray(2 * len(data))
    spread[::2] = data
    assert bequadro.parse(Kitchen.Sink, memoryview(spread)[::2]).title == "minimal"

    sink = minimal_sink()
    sink.bytes = memoryview(b"abcdef")[::2]  # type: ignore[assignment]
    assert round_trip(sink).bytes == bytearray(b"ace")


def test_ubyte_vectors_accept_any_bytes_like_object() -> None:
    for value in (b"\x01\x02", bytearray(b"\x01\x02"), memoryview(b"\x00\x01\x02")[1:], array.array("B", [1, 2])):
        sink = minimal_sink()
        sink.bytes = value  # type: ignore[assignment]
        assert round_trip(sink).bytes == bytearray(b"\x01\x02")


def test_other_vectors_accept_tuples() -> None:
    sink = minimal_sink()
    sink.doubles = (1.5, 2.5)  # type: ignore[assignment]
    sink.strings = ("a", "b")  # type: ignore[assignment]
    sink.vectors = (Common.Vec3(x=1),)  # type: ignore[assignment]
    child = minimal_sink()
    child.title = "c"
    sink.children = (child,)  # type: ignore[assignment]
    sink.shapes = (None, Shapes.Marker(id=2))  # type: ignore[assignment]
    sink.colors = (Common.Color.Red,)  # type: ignore[assignment]
    parsed = round_trip(sink)
    assert parsed.doubles == [1.5, 2.5]
    assert parsed.strings == ["a", "b"]
    assert parsed.vectors == [Common.Vec3(x=1)]
    assert parsed.children is not None and parsed.children[0].title == "c"
    assert parsed.shapes == [None, Shapes.Marker(id=2)]
    assert parsed.colors == [Common.Color.Red]
    assert bequadro.write(parsed) == bequadro.write(sink)


def test_a_buffer_larger_than_the_limit_raises_before_it_is_built() -> None:
    sink = minimal_sink()
    # bytes(n) is zero pages the system hasn't allocated yet, and the writer raises before reading them
    sink.bytes = bytes(2147483591)  # type: ignore[assignment]
    with pytest.raises(FlatBufferWriteError, match="maximum size of 2147483591 bytes"):
        bequadro.write(sink)


def test_a_table_whose_inline_fields_exceed_a_vtable_raises_naming_the_table() -> None:
    assert bequadro.parse(Limits.TooLarge, bequadro.write(Limits.TooLarge(largest=Limits.Largest()))).largest is not None
    with pytest.raises(
        FlatBufferWriteError,
        match=r"^Cannot serialize an instance of 'Bequadro\.Tests\.Limits\.TooLarge': a table's inline fields can take at most 65535 bytes",
    ):
        bequadro.write(Limits.TooLarge(largest=Limits.Largest(), extra=Limits.S0()))


def test_a_subclass_where_a_nested_table_is_expected_is_rejected() -> None:
    class Derived(Kitchen.Node):  # type: ignore[misc]
        pass

    with pytest.raises(FlatBufferWriteError, match=r"Expected a Node for field 'self' of 'Bequadro.Tests.Kitchen.Node', got Derived"):
        bequadro.write(Kitchen.Node(self=Derived()))
    with pytest.raises(FlatBufferWriteError, match=r"field 'neighbors\[1\]' of 'Bequadro.Tests.Kitchen.Node'"):
        bequadro.write(Kitchen.Node(neighbors=[Kitchen.Node(), Derived()]))


# Parse errors (SER-009), built with the writer's primitives as the C# tests use the official builder


@pytest.mark.parametrize("only_type", [True, False])
def test_a_union_field_with_only_its_discriminator_or_its_value_parses_as_none(only_type: bool) -> None:
    data = holder_buffer(shape_type_without_value=MARKER) if only_type else holder_buffer(shape_value_without_type=True)
    holder = bequadro.parse(Shapes.Holder, data)
    assert holder.shape is None
    assert holder.required_shape == Shapes.Marker(id=1)


def test_invalid_utf8_parses_with_replacement_characters() -> None:
    sink = minimal_sink()
    sink.name = "zzzz marker"
    data = bytearray(bequadro.write(sink))
    position = data.index(b"zzzz")
    data[position] = 0xFF
    data[position + 2] = 0xC3
    assert bequadro.parse(Kitchen.Sink, data).name == "\ufffdz\ufffdz marker"


def test_fields_the_schema_does_not_have_are_ignored() -> None:
    sink = minimal_sink()
    sink.name = "n"
    sink.children = [minimal_sink()]
    sink.ints = [1, 2]
    assert bequadro.parse(Kitchen.Empty, bequadro.write(sink)) is not None


def test_a_table_position_read_as_two_tables_raises() -> None:
    # Graph's nodes and lines both refer to one table, which can't be both a Node and a Line
    writer = bequadro.Writer(False)
    writer.start_table(0)
    table = writer.end_table("test")
    nodes = writer.write_offset_vector([table])
    lines = writer.write_offset_vector([table])
    writer.start_table(3)
    writer.add_offset(0, nodes)
    writer.add_offset(1, lines)
    with pytest.raises(
        FlatBufferDataError, match="read as both 'Bequadro.Tests.Kitchen.Node' and 'Bequadro.Tests.Kitchen.Line'"
    ):
        bequadro.parse(Kitchen.Graph, writer.finish(writer.end_table("test")))


def test_a_null_element_in_a_vector_of_tables_raises() -> None:
    sink = minimal_sink()
    sink.children = [minimal_sink(), None]  # type: ignore[list-item]
    with pytest.raises(FlatBufferWriteError, match="null element in a vector of 'Bequadro.Tests.Kitchen.Sink'"):
        bequadro.write(sink)


def test_a_missing_required_field_raises_naming_it() -> None:
    writer = bequadro.Writer(False)
    writer.start_table(2)
    data = writer.finish(writer.end_table("test"))
    with pytest.raises(FlatBufferDataError, match="required field 'center' of table 'Bequadro.Tests.Shapes.Circle'"):
        bequadro.parse(Shapes.Circle, data)


def nested_tables(levels: int) -> bytes:
    """A buffer of Node tables nested levels deep through self, built without the writer's depth limit."""
    writer = bequadro.Writer(False)
    writer.start_table(0)
    current = writer.end_table("test")
    for _ in range(1, levels):
        writer.start_table(3)
        writer.add_offset(2, current)
        current = writer.end_table("test")
    return writer.finish(current)


def test_tables_nested_64_levels_deep_the_limit_parse() -> None:
    node: Kitchen.Node | None = bequadro.parse(Kitchen.Node, nested_tables(64))
    depth = 0
    while node is not None:
        depth += 1
        node = node.self
    assert depth == 64


@pytest.mark.parametrize("levels", [65, 1100])
def test_tables_nested_deeper_than_64_levels_raise(levels: int) -> None:
    with pytest.raises(FlatBufferDataError, match=r"^The buffer exceeds the maximum table nesting depth of 64\.$"):
        bequadro.parse(Kitchen.Node, nested_tables(levels))


# Discriminators of Marker in each union of Holder
MARKER, STRICT_MARKER, NO_NONE_MARKER, EXHAUSTIVE_MARKER = 3, 2, 2, 2


def holder_buffer(
    *,
    required_shape_type: int = MARKER,
    required_strict_type: int = STRICT_MARKER,
    shape_types: list[int] | None = None,
    strict_types: list[int] | None = None,
    no_none_type: int = 0,
    no_none_types: list[int] | None = None,
    exhaustive_type: int = 0,
    exhaustive_types: list[int] | None = None,
    shape_type_without_value: int = 0,
    shape_value_without_type: bool = False,
) -> bytes:
    """Builds a Holder as the C# tests' HolderBuffer does: every union value is a Marker with id 1, or no value
    (offset 0) for discriminator 0, so discriminators the schema doesn't define can be written. A field with
    discriminator 0 is left out."""
    writer = bequadro.Writer(False)

    def marker(discriminator: int) -> int:
        if discriminator == 0:
            return 0
        return writer.write_struct(Shapes.Marker.__bequadro__, Shapes.Marker(id=1), "", "")

    fields = [
        (2, required_shape_type, marker(required_shape_type)),
        (8, required_strict_type, marker(required_strict_type)),
        (12, no_none_type, marker(no_none_type)),
        (16, exhaustive_type, marker(exhaustive_type)),
    ]
    vectors: list[tuple[int, int, int]] = []
    for slot, types in ((4, shape_types), (10, strict_types), (14, no_none_types), (18, exhaustive_types)):
        if types is not None:
            values = writer.write_offset_vector([marker(t) for t in types])
            vectors.append((slot, writer.write_bytes(bytes(types), "", ""), values))

    shape_value = marker(MARKER) if shape_value_without_type else 0
    writer.start_table(20)
    if shape_value != 0:
        writer.add_offset(1, shape_value)
    if shape_type_without_value != 0:
        writer.add_scalar(0, bequadro.UINT8, shape_type_without_value)
    for slot, discriminator, offset in fields:
        if discriminator != 0:
            writer.add_scalar(slot, bequadro.UINT8, discriminator)
            writer.add_offset(slot + 1, offset)
    for slot, types_offset, values_offset in vectors:
        writer.add_offset(slot, types_offset)
        writer.add_offset(slot + 1, values_offset)
    return writer.finish(writer.end_table("test"))


def minimal_holder() -> Shapes.Holder:
    return Shapes.Holder(required_shape=Shapes.Marker(id=1), required_strict=Shapes.Marker(id=2))


def test_holder_buffer_matches_the_writer() -> None:
    parsed = bequadro.parse(Shapes.Holder, holder_buffer())
    assert parsed.required_shape == Shapes.Marker(id=1)
    assert parsed.required_strict == Shapes.Marker(id=1)


def test_a_required_union_that_is_none_raises_on_write_and_on_parse() -> None:
    holder = minimal_holder()
    holder.required_shape = None  # type: ignore[assignment]
    with pytest.raises(FlatBufferWriteError, match="Required field 'required_shape' of table 'Bequadro.Tests.Shapes.Holder' is null"):
        bequadro.write(holder)
    with pytest.raises(FlatBufferDataError, match="required field 'required_shape'"):
        bequadro.parse(Shapes.Holder, holder_buffer(required_shape_type=0))


def test_single_attribute_unions_round_trip_values_and_none() -> None:
    holder = minimal_holder()
    holder.no_none = Shapes.Marker(id=5)
    holder.no_nones = [Shapes.Circle(center=Common.Vec3(x=1, y=2, z=3)), Shapes.Marker(id=6)]
    holder.exhaustives = [None, Shapes.Marker(id=7)]

    data = bequadro.write(holder)
    parsed = bequadro.parse(Shapes.Holder, data)

    assert parsed.no_none == Shapes.Marker(id=5)
    assert parsed.no_nones is not None
    assert isinstance(parsed.no_nones[0], Shapes.Circle)
    assert parsed.no_nones[0].center == Common.Vec3(x=1, y=2, z=3)
    assert parsed.no_nones[1] == Shapes.Marker(id=6)
    assert parsed.exhaustive is None
    assert parsed.exhaustives == [None, Shapes.Marker(id=7)]
    assert bequadro.write(parsed) == data


def test_an_exclude_none_only_union_with_no_value_in_a_vector_raises() -> None:
    holder = minimal_holder()
    holder.no_nones = [Shapes.Marker(), None]  # type: ignore[list-item]
    with pytest.raises(FlatBufferWriteError, match="NoNoneShape.*no value"):
        bequadro.write(holder)


def test_unknown_discriminators_in_an_exclude_none_only_union_read_as_unknown() -> None:
    holder = bequadro.parse(Shapes.Holder, holder_buffer(no_none_type=200, no_none_types=[201, NO_NONE_MARKER]))
    assert holder.no_none == UnionUnknown(discriminator=200)
    assert holder.no_nones == [UnionUnknown(discriminator=201), Shapes.Marker(id=1)]


def test_a_none_element_in_an_exclude_none_only_union_vector_raises() -> None:
    with pytest.raises(FlatBufferDataError, match="NONE.*NoNoneShape"):
        bequadro.parse(Shapes.Holder, holder_buffer(no_none_types=[NO_NONE_MARKER, 0]))


@pytest.mark.parametrize("in_vector", [True, False])
def test_an_unknown_discriminator_in_an_exhaustive_only_union_raises(in_vector: bool) -> None:
    data = (
        holder_buffer(exhaustive_types=[EXHAUSTIVE_MARKER, 200]) if in_vector else holder_buffer(exhaustive_type=200)
    )
    with pytest.raises(FlatBufferDataError, match="discriminator 200.*ExhaustiveShape"):
        bequadro.parse(Shapes.Holder, data)


def test_a_none_element_in_an_exhaustive_only_union_vector_reads_as_none() -> None:
    holder = bequadro.parse(Shapes.Holder, holder_buffer(exhaustive_types=[0, EXHAUSTIVE_MARKER]))
    assert holder.exhaustives == [None, Shapes.Marker(id=1)]


def test_parsing_with_shared_strings_returns_one_object_per_string_position() -> None:
    nodes = [Kitchen.Node(name="shared name"), Kitchen.Node(name="shared name")]
    data = bequadro.write(Kitchen.Graph(nodes=nodes, lines=[]), share_strings=True)

    shared = bequadro.parse(Kitchen.Graph, data, share_strings=True).nodes
    separate = bequadro.parse(Kitchen.Graph, data).nodes
    again = bequadro.parse(Kitchen.Graph, data, share_strings=True).nodes

    assert shared[0].name is shared[1].name
    assert separate[0].name == separate[1].name
    assert separate[0].name is not separate[1].name
    assert again[0].name is not shared[0].name, "separate parses never share strings"


# Every scalar vector type, and value equality (PY-008, PY-010)


def test_vectors_of_every_scalar_type_round_trip_at_their_limits() -> None:
    vectors = Vectors(
        bools=[True, False],
        bytes=[-128, 127],
        ubytes=bytearray([0, 255]),
        shorts=[-(2**15), 2**15 - 1],
        ushorts=[0, 2**16 - 1],
        ints=[-(2**31), 2**31 - 1],
        uints=[0, 2**32 - 1],
        longs=[-(2**63), 2**63 - 1],
        ulongs=[0, 2**64 - 1],
        floats=[0.5, -math.inf, 3.4028234663852886e38],
        doubles=[math.pi, -0.0],
        smalls=[Small.Low, Small.High, Small(7)],
        strings=["", "é"],
    )
    parsed = bequadro.parse(Vectors, bequadro.write(vectors))
    assert parsed == vectors
    assert parsed.smalls == [Small.Low, Small.High, 7]
    assert all(isinstance(s, Small) for s in parsed.smalls or [])
    with pytest.raises(FlatBufferWriteError, match=r"out of range for ushort field 'ushorts\[0\]'"):
        bequadro.write(Vectors(ushorts=[-1]))


class _Index:
    """Converts to an int with __index__, as numpy integers do, without being an int."""

    def __index__(self) -> int:
        return 1


class _Int(int):
    pass


class _Float(float):
    pass


class _OwnInt(int):
    """Converts to a different int than the one struct would pack."""

    def __int__(self) -> int:
        return 7


class _OwnFloat(float):
    def __float__(self) -> float:
        return 7.0


_SINK = "'Bequadro.Tests.Kitchen.Sink'"


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        ("ints", [1, 2, 2**31], f"int 2147483648 is out of range for int field 'ints[2]' of {_SINK}: it must be from "
         "-2147483648 to 2147483647."),
        ("ints", [1, "2"], f"Expected an int for field 'ints[1]' of {_SINK}, got the string '2'."),
        ("ints", [1, 2.0], f"Expected an int for field 'ints[1]' of {_SINK}, got float 2.0."),
        ("ints", [_Index()], f"Expected an int for field 'ints[0]' of {_SINK}, got _Index."),
        ("ints", [1, None], f"Expected an int for field 'ints[1]' of {_SINK}, got NoneType."),
        ("ulongs", [0, -1], f"int -1 is out of range for ulong field 'ulongs[1]' of {_SINK}: it must be from 0 to "
         "18446744073709551615."),
        ("longs", [2**63], f"int 9223372036854775808 is out of range for long field 'longs[0]' of {_SINK}: it must be "
         "from -9223372036854775808 to 9223372036854775807."),
        ("bools", [True, 1], f"Expected a bool for field 'bools[1]' of {_SINK}, got int 1."),
        ("colors", [Common.Color.Red, 256], f"int 256 is out of range for ubyte field 'colors[1]' of {_SINK}: it must "
         "be from 0 to 255."),
        ("doubles", [0.5, "x"], f"Expected a float for field 'doubles[1]' of {_SINK}, got the string 'x'."),
        ("doubles", [0.5, _Index()], f"Expected a float for field 'doubles[1]' of {_SINK}, got _Index."),
        ("doubles", [10**400], f"int of 1329 bits is out of range for double field 'doubles[0]' of {_SINK}: it is too "
         "large to convert to a float."),
        ("strings", ["a", 5], f"Expected a str for field 'strings[1]' of {_SINK}, got int 5."),
        ("vectors", [Common.Vec3(), Common.Vec3(y="1")],  # type: ignore[arg-type]
         f"Expected a float for field 'vectors[1].y' of {_SINK}, got the string '1'."),
        ("vectors", [Common.Vec3(), (1, 2, 3)], f"Expected a Vec3 for field 'vectors[1]' of {_SINK}, got tuple."),
        ("vectors", [Common.Vec3(), None], f"Cannot serialize a null element in a vector of 'Bequadro.Tests.Common.Vec3' "
         f"in field 'vectors' of {_SINK}."),
        ("position", Common.Vec3(z=10**400), f"int of 1329 bits is out of range for double field 'position.z' of "
         f"{_SINK}: it is too large to convert to a float."),
        ("mixed", Common.Mixed(position=Common.Vec3(z="1")),  # type: ignore[arg-type]
         f"Expected a float for field 'mixed.position.z' of {_SINK}, got the string '1'."),
        ("mixed", Common.Mixed(position=(1, 2, 3)),  # type: ignore[arg-type]
         f"Expected a Vec3 for field 'mixed.position' of {_SINK}, got tuple."),
        ("mixed", Common.Mixed(tiny=-1), f"int -1 is out of range for ubyte field 'mixed.tiny' of {_SINK}: it must be "
         "from 0 to 255."),
    ],
    ids=lambda value: "huge int" if isinstance(value, list) and 10**400 in value else None,
)
def test_bad_vector_elements_and_struct_members_are_named_by_their_path(field: str, value: object, message: str) -> None:
    sink = minimal_sink()
    setattr(sink, field, value)
    with pytest.raises(FlatBufferWriteError) as caught:
        bequadro.write(sink)
    assert str(caught.value) == message


@pytest.mark.parametrize(
    ("mixed", "message"),
    [
        (Common.Mixed(small=1000, position=Common.Vec3(x="a"), ratio="b"), "'mixed.small'"),  # type: ignore[arg-type]
        (Common.Mixed(position=Common.Vec3(y="a"), count=-1), "'mixed.position.y'"),  # type: ignore[arg-type]
        (Common.Mixed(count=-1, ratio="b"), "'mixed.count'"),  # type: ignore[arg-type]
        (Common.Mixed(flag=1, medium=2**15), "'mixed.flag'"),  # type: ignore[arg-type]
    ],
)
def test_a_struct_reports_its_first_bad_member_in_field_order(mixed: Common.Mixed, message: str) -> None:
    sink = minimal_sink()
    sink.mixed = mixed
    with pytest.raises(FlatBufferWriteError, match=re.escape(message)):
        bequadro.write(sink)


def test_vectors_accept_the_same_values_as_fields() -> None:
    sink = minimal_sink()
    sink.ints = [True, False, _Int(3), 2**31 - 1]
    sink.doubles = [1, True, 2.5, _Float(0.25), 2**1000]
    sink.colors = [Common.Color.Red, Common.Color(9), 3]  # type: ignore[list-item]
    sink.ulongs = [2**64 - 1, Common.Big.Huge]
    parsed = round_trip(sink)
    assert parsed.ints == [1, 0, 3, 2**31 - 1]
    assert parsed.doubles == [1.0, 1.0, 2.5, 0.25, float(2**1000)]
    assert parsed.colors == [Common.Color.Red, 9, Common.Color.Green]
    assert parsed.ulongs == [2**64 - 1, 2**64 - 1]
    sink.ints = [1, _OwnInt(2)]
    sink.doubles = [1.0, _OwnFloat(2)]
    sink.vectors = [Common.Vec3(x=_OwnFloat(2))]
    parsed = round_trip(sink)
    assert (parsed.ints, parsed.doubles, parsed.vectors) == ([1, 7], [1.0, 7.0], [Common.Vec3(x=7)]), "as check converts"
    smalls: list[Small] = [-300, 300]  # type: ignore[list-item]
    vectors = bequadro.parse(Vectors, bequadro.write(Vectors(floats=[1e39, -(2**200), 0.1, 2], smalls=smalls)))
    assert vectors.floats == [math.inf, -math.inf, bequadro.round_float32(0.1), 2.0]
    assert vectors.smalls == [Small.Low, Small.High]
    with pytest.raises(FlatBufferWriteError) as caught:
        bequadro.write(Vectors(floats=[0.5, 10**400]))
    assert str(caught.value) == (
        "int of 1329 bits is out of range for float field 'floats[1]' of 'PyVectors.Vectors': it is too large to "
        "convert to a float."
    )
    with pytest.raises(FlatBufferWriteError) as caught:
        bequadro.write(Vectors(smalls=[2**15]))  # type: ignore[list-item]
    assert str(caught.value) == (
        "int 32768 is out of range for short field 'smalls[0]' of 'PyVectors.Vectors': it must be from -32768 to 32767."
    )


def test_float_fields_compare_at_32_bit_precision() -> None:
    assert Vectors(single=0.1) == bequadro.parse(Vectors, bequadro.write(Vectors(single=0.1)))
    assert Vectors(single=0.1) == Vectors(single=bequadro.round_float32(0.1))
    assert Vectors(single=0.1) != Vectors(single=0.2)
    assert Vectors(single=math.nan) == Vectors(single=math.nan)
    assert Vectors(floats=[0.1]) == Vectors(floats=[bequadro.round_float32(0.1)])
    assert Common.Mixed(ratio=0.1) == Common.Mixed(ratio=bequadro.round_float32(0.1))
    sink = minimal_sink()
    sink.mixed = Common.Mixed(ratio=0.1)
    assert round_trip(sink).mixed == Common.Mixed(ratio=0.1)
    assert Vectors(doubles=[0.1]) != Vectors(doubles=[bequadro.round_float32(0.1)]), "doubles keep their precision"


def test_vectors_compare_by_elements_whatever_holds_them() -> None:
    assert Vectors(ints=[1, 2]) == Vectors(ints=(1, 2))  # type: ignore[arg-type]
    assert Vectors(strings=["a"]) == Vectors(strings=("a",))  # type: ignore[arg-type]
    assert Vectors(doubles=[1.0]) == Vectors(doubles=(1.0,))  # type: ignore[arg-type]
    assert Vectors(ints=[1, 2]) != Vectors(ints=(2, 1))  # type: ignore[arg-type]
    assert Vectors(ubytes=bytearray(b"ab")) == Vectors(ubytes=b"ab")  # type: ignore[arg-type]
    assert Vectors(ubytes=bytearray(b"ab")) == Vectors(ubytes=memoryview(b"xab")[1:])  # type: ignore[arg-type]
    assert Vectors(ubytes=bytearray(b"ab")) != Vectors(ubytes=bytearray(b"ba"))
    assert Vectors(ubytes=bytearray()) != Vectors(), "None isn't equal to an empty vector"
    assert Vectors(ints=[]) != Vectors()


def test_equality_returns_not_implemented_for_other_types_and_compares_unknown_union_members() -> None:
    assert Common.Vec3().__eq__(1) is NotImplemented
    assert Vectors().__eq__("x") is NotImplemented
    assert Common.Vec3() != 1
    assert Equality.Measure(item=UnionUnknown(discriminator=9)) == Equality.Measure(item=UnionUnknown(discriminator=9))
    assert Equality.Measure(item=UnionUnknown(discriminator=9)) != Equality.Measure(item=UnionUnknown(discriminator=8))
    assert Equality.Measure(item=UnionUnknown(discriminator=9)) != Equality.Measure(item=None)


# NaN bits (PY-011)


def test_double_nans_are_written_with_their_bits() -> None:
    sink = minimal_sink()
    sink.precise = math.nan
    assert struct.pack("<d", math.nan) in bequadro.write(sink)
    assert struct.pack("<d", math.nan) == bytes.fromhex("000000000000f87f"), "unlike .NET's double.NaN, f8ff"
    for bits in ("010000000000f07f", "0100000000a0f8ff", "000000000000f8ff"):
        sink.precise = struct.unpack("<d", bytes.fromhex(bits))[0]
        data = bequadro.write(sink)
        assert bytes.fromhex(bits) in data
        assert bequadro.write(round_trip(sink)) == data


def test_float_nans_keep_their_bits_except_that_a_signalling_nan_may_become_quiet() -> None:
    sink = minimal_sink()
    for bits, written in (
        ("0100c07f", {"0100c07f"}),
        ("ffffffff", {"ffffffff"}),
        ("0100a07f", {"0100a07f", "0100e07f"}),
        ("ffffbfff", {"ffffbfff", "ffffffff"}),
    ):
        sink.ratio = struct.unpack("<f", bytes.fromhex(bits))[0]
        data = bequadro.write(sink)
        assert any(bytes.fromhex(w) in data for w in written), bits
        assert bequadro.write(round_trip(sink)) == data
