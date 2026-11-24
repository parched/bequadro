using Bequadro.Tests.Common;
using Bequadro.Tests.Kitchen;
using Bequadro.Tests.Shapes;
using Google.FlatBuffers;

namespace Bequadro.Tests.Interop;

/// <summary>
/// Checks wire compatibility with the official Google.FlatBuffers runtime in both directions (SER-002).
/// Slot numbers follow the field order of Kitchen.fbs and Shapes.fbs.
/// </summary>
public class GoogleInteropTests
{
    private static class SinkSlot
    {
        public const int Name = 0;
        public const int Title = 1;
        public const int Flag = 2;
        public const int FlagDefaultTrue = 3;
        public const int Small = 4;
        public const int Tiny = 5;
        public const int Medium = 6;
        public const int Count = 8;
        public const int UnsignedCount = 9;
        public const int Wide = 10;
        public const int Ratio = 12;
        public const int Precise = 13;
        public const int OptionalInt = 16;
        public const int OptionalColor = 17;
        public const int Color = 18;
        public const int Position = 21;
        public const int RequiredPosition = 22;
        public const int Mixed = 23;
        public const int Child = 24;
        public const int ShapeType = 25;
        public const int Shape = 26;
        public const int RequiredShapeType = 27;
        public const int RequiredShape = 28;
        public const int Bools = 29;
        public const int Bytes = 30;
        public const int Doubles = 31;
        public const int Colors = 32;
        public const int Strings = 33;
        public const int Vectors = 34;
        public const int Children = 35;
        public const int ShapesType = 36;
        public const int Shapes = 37;
        public const int RequiredChildren = 38;
        public const int Sbytes = 39;
        public const int Ints = 42;
        public const int Longs = 44;
        public const int Floats = 46;
        public const int Signeds = 47;
        public const int Bigs = 48;
        public const int Count_ = 49;
    }

    [Fact]
    public void GoogleReader_BequadroBuffer_ReadsScalarsStringsAndStructs()
    {
        var sink = TestData.FullSink();

        var table = GoogleTable.Root(Sink.Serializer.WriteToArray(sink));

        table.GetString(SinkSlot.Name).Should().Be("full");
        table.GetString(SinkSlot.Title).Should().Be("title");
        table.GetByte(SinkSlot.Flag).Should().Be(1);
        table.GetByte(SinkSlot.FlagDefaultTrue, defaultValue: 1).Should().Be(0);
        table.GetSbyte(SinkSlot.Small, defaultValue: -3).Should().Be(-100);
        table.GetByte(SinkSlot.Tiny, defaultValue: 200).Should().Be(7);
        table.GetShort(SinkSlot.Medium).Should().Be(-1234);
        table.GetInt(SinkSlot.Count, defaultValue: 42).Should().Be(0);
        table.GetUint(SinkSlot.UnsignedCount).Should().Be(4_000_000_000);
        table.GetLong(SinkSlot.Wide, defaultValue: long.MinValue).Should().Be(long.MaxValue);
        table.GetFloat(SinkSlot.Ratio, defaultValue: 0.5f).Should().Be(1.25f);
        table.GetDouble(SinkSlot.Precise, defaultValue: 0.925).Should().Be(-2.5);
        table.Has(SinkSlot.OptionalInt).Should().BeTrue();
        table.GetInt(SinkSlot.OptionalInt).Should().Be(0);
        table.GetByte(SinkSlot.OptionalColor).Should().Be((byte)Color.Red);
        table.GetByte(SinkSlot.Color, defaultValue: (byte)Color.Blue).Should().Be((byte)Color.Green);

        ReadVec3(table.Buffer, table.FieldPosition(SinkSlot.Position)).Should().Be(TestData.Vec(4, 5, 6));
        ReadVec3(table.Buffer, table.FieldPosition(SinkSlot.RequiredPosition)).Should().Be(TestData.Vec(7, 8, 9));

        // Mixed layout: flag@0 small@1 tiny@2 medium@4 position@8 count@32 wide@40 color@48 ratio@52, size 56
        var mixed = table.FieldPosition(SinkSlot.Mixed);
        table.Buffer.Get(mixed).Should().Be(1);
        table.Buffer.GetSbyte(mixed + 1).Should().Be(-5);
        table.Buffer.Get(mixed + 2).Should().Be(250);
        table.Buffer.GetShort(mixed + 4).Should().Be(-300);
        ReadVec3(table.Buffer, mixed + 8).Should().Be(TestData.Vec(1.5, 2.5, 3.5));
        table.Buffer.GetUint(mixed + 32).Should().Be(77);
        table.Buffer.GetLong(mixed + 40).Should().Be(-88);
        table.Buffer.Get(mixed + 48).Should().Be((byte)Color.Blue);
        table.Buffer.GetFloat(mixed + 52).Should().Be(0.125f);
    }

    [Fact]
    public void GoogleReader_BequadroBuffer_OmitsDefaultsAndAbsentFields()
    {
        var table = GoogleTable.Root(Sink.Serializer.WriteToArray(TestData.MinimalSink()));

        foreach (var slot in Enumerable.Range(0, SinkSlot.Count_))
        {
            var expectedPresent =
                slot
                is SinkSlot.Title
                    or SinkSlot.RequiredPosition
                    or SinkSlot.RequiredShapeType
                    or SinkSlot.RequiredShape
                    or SinkSlot.RequiredChildren;
            table
                .Has(slot)
                .Should()
                .Be(expectedPresent, $"slot {slot} should be {(expectedPresent ? "present" : "absent")}");
        }

        table.VectorLength(SinkSlot.RequiredChildren).Should().Be(0);
    }

    [Fact]
    public void GoogleReader_BequadroBuffer_ReadsTablesVectorsAndUnions()
    {
        var table = GoogleTable.Root(Sink.Serializer.WriteToArray(TestData.FullSink()));

        table.GetTable(SinkSlot.Child)!.Value.GetString(SinkSlot.Title).Should().Be("child");

        table.GetByte(SinkSlot.ShapeType).Should().Be((byte)Shape.ItemKind.Poly);
        var polygon = table.GetTable(SinkSlot.Shape)!.Value;
        polygon.VectorLength(0).Should().Be(2);
        ReadVec3(polygon.Buffer, polygon.VectorStart(0) + 24).Should().Be(TestData.Vec(2, 2, 2));

        table.GetByte(SinkSlot.RequiredShapeType).Should().Be((byte)Shape.ItemKind.Marker);
        table.Buffer.GetInt(table.GetIndirect(SinkSlot.RequiredShape)).Should().Be(99);

        table.VectorLength(SinkSlot.Bools).Should().Be(3);
        table.Buffer.Get(table.VectorStart(SinkSlot.Bools) + 1).Should().Be(0);
        table.VectorLength(SinkSlot.Doubles).Should().Be(3);
        table.Buffer.GetDouble(table.VectorStart(SinkSlot.Doubles) + 8).Should().Be(-2.5);
        table.VectorString(SinkSlot.Strings, 2).Should().Be("héllo ✓");
        ReadVec3(table.Buffer, table.VectorStart(SinkSlot.Vectors)).Should().Be(TestData.Vec(1, 2, 3));
        table.VectorTable(SinkSlot.Children, 1).GetString(SinkSlot.Title).Should().Be("child");

        table.VectorLength(SinkSlot.ShapesType).Should().Be(3);
        table.VectorLength(SinkSlot.Shapes).Should().Be(3);
        var typesStart = table.VectorStart(SinkSlot.ShapesType);
        table.Buffer.Get(typesStart).Should().Be((byte)Shape.ItemKind.Circle);
        table.Buffer.Get(typesStart + 1).Should().Be((byte)Shape.ItemKind.Marker);
        table.VectorTable(SinkSlot.Shapes, 0).GetDouble(1, defaultValue: 1).Should().Be(2);
        table.Buffer.GetInt(table.VectorIndirect(SinkSlot.Shapes, 1)).Should().Be(5);
    }

    [Fact]
    public void Parse_GoogleBuiltBuffer_ReadsEveryFieldKind()
    {
        var sink = Sink.Serializer.Parse(BuildGoogleSink());

        sink.Name.Should().Be("google");
        sink.Flag.Should().BeTrue();
        sink.Small.Should().Be(-9);
        sink.Count.Should().Be(5);
        sink.Wide.Should().Be(123);
        sink.Precise.Should().Be(6.5);
        sink.OptionalInt.Should().Be(0);
        sink.Ratio.Should().Be(0.5f, "absent fields read as the schema default");
        sink.Color.Should().Be(Color.Red);
        sink.RequiredPosition.Should().Be(TestData.Vec(1, 1, 1));
        sink.Child!.Title.Should().Be("child");
        sink.Shape.Poly.Points.Should().Equal(TestData.Vec(9, 9, 9));
        sink.RequiredShape.Marker.Id.Should().Be(3);
        sink.Doubles.Should().Equal(1.5, 2.5);
        sink.Strings.Should().Equal("x", "yz");
        sink.Vectors.Should().Equal(TestData.Vec(1, 2, 3), TestData.Vec(4, 5, 6));
        sink.Children.Should().HaveCount(2);
        sink.Children![0].Should().BeSameAs(sink.Children[1]).And.BeSameAs(sink.Child);
        sink.Shapes!.Select(s => s.Kind).Should().Equal(Shape.ItemKind.Poly, Shape.ItemKind.Marker);
        sink.Shapes![1].Marker.Id.Should().Be(7);
        sink.RequiredChildren.Should().ContainSingle().Which.Should().BeSameAs(sink.Child);
        sink.Bools.Should().Equal(true, false, true);
        sink.Bytes.Should().Equal((byte)0, (byte)255);
        sink.Colors.Should().Equal(Color.Green, Color.Red);
        sink.Sbytes.Should().Equal((sbyte)-1, sbyte.MaxValue);
        sink.Ints.Should().Equal(-7, int.MaxValue);
        sink.Longs.Should().Equal(long.MinValue);
        sink.Floats.Should().Equal(0.25f, -1f);
        sink.Signeds.Should().Equal(Signed.Negative);
        sink.Bigs.Should().Equal(Big.Huge);
    }

    /// <summary>
    /// A Sink built with the official builder, with every kind of field and a shared child. Also a cross-language
    /// fixture (<see cref="CrossLanguageFixtureTests"/>).
    /// </summary>
    internal static byte[] BuildGoogleSink()
    {
        var builder = new FlatBufferBuilder(64);

        var childTitle = builder.CreateString("child");
        var childMarker = CreateMarker(builder, 1);
        var noChildren = CreateOffsetVector(builder);
        builder.StartTable(SinkSlot.Count_);
        builder.AddOffset(SinkSlot.Title, childTitle.Value, 0);
        builder.AddStruct(SinkSlot.RequiredPosition, CreateVec3(builder, TestData.Vec(0, 0, 0)), 0);
        builder.AddByte(SinkSlot.RequiredShapeType, (byte)Shape.ItemKind.Marker, 0);
        builder.AddOffset(SinkSlot.RequiredShape, childMarker, 0);
        builder.AddOffset(SinkSlot.RequiredChildren, noChildren, 0);
        var child = builder.EndTable();

        var name = builder.CreateString("google");
        var title = builder.CreateString("title");
        var strings = CreateStringVector(builder, "x", "yz");
        var doubles = CreateDoubleVector(builder, 1.5, 2.5);
        var children = CreateOffsetVector(builder, child, child);
        var vectors = CreateVec3Vector(builder, TestData.Vec(1, 2, 3), TestData.Vec(4, 5, 6));
        var polygonPoints = CreateVec3Vector(builder, TestData.Vec(9, 9, 9));
        builder.StartTable(1);
        builder.AddOffset(0, polygonPoints, 0);
        var polygon = builder.EndTable();
        var marker = CreateMarker(builder, 7);
        var shapeTypes = CreateByteVector(builder, (byte)Shape.ItemKind.Poly, (byte)Shape.ItemKind.Marker);
        var shapes = CreateOffsetVector(builder, polygon, marker);
        var circleMarker = CreateMarker(builder, 3);
        var requiredChildren = CreateOffsetVector(builder, child);
        var bools = CreateVector(builder, 1, 3, i => builder.AddBool(i != 1));
        var bytes = CreateByteVector(builder, 0, 255);
        var colors = CreateByteVector(builder, (byte)Color.Green, (byte)Color.Red);
        var sbytes = CreateVector(builder, 1, 2, i => builder.AddSbyte(i == 0 ? (sbyte)-1 : sbyte.MaxValue));
        var ints = CreateVector(builder, 4, 2, i => builder.AddInt(i == 0 ? -7 : int.MaxValue));
        var longs = CreateVector(builder, 8, 1, _ => builder.AddLong(long.MinValue));
        var floats = CreateVector(builder, 4, 2, i => builder.AddFloat(i == 0 ? 0.25f : -1));
        var signeds = CreateVector(builder, 1, 1, _ => builder.AddSbyte((sbyte)Signed.Negative));
        var bigs = CreateVector(builder, 8, 1, _ => builder.AddUlong((ulong)Big.Huge));

        builder.StartTable(SinkSlot.Count_);
        builder.AddOffset(SinkSlot.Name, name.Value, 0);
        builder.AddOffset(SinkSlot.Title, title.Value, 0);
        builder.AddBool(SinkSlot.Flag, true, false);
        builder.AddSbyte(SinkSlot.Small, -9, -3);
        builder.AddInt(SinkSlot.Count, 5, 42);
        builder.AddLong(SinkSlot.Wide, 123, long.MinValue);
        builder.AddDouble(SinkSlot.Precise, 6.5, 0.925);
        builder.AddInt(SinkSlot.OptionalInt, 0);
        builder.AddByte(SinkSlot.Color, (byte)Color.Red, (byte)Color.Blue);
        builder.AddStruct(SinkSlot.RequiredPosition, CreateVec3(builder, TestData.Vec(1, 1, 1)), 0);
        builder.AddOffset(SinkSlot.Child, child, 0);
        builder.AddByte(SinkSlot.ShapeType, (byte)Shape.ItemKind.Poly, 0);
        builder.AddOffset(SinkSlot.Shape, polygon, 0);
        builder.AddByte(SinkSlot.RequiredShapeType, (byte)Shape.ItemKind.Marker, 0);
        builder.AddOffset(SinkSlot.RequiredShape, circleMarker, 0);
        builder.AddOffset(SinkSlot.Doubles, doubles, 0);
        builder.AddOffset(SinkSlot.Strings, strings, 0);
        builder.AddOffset(SinkSlot.Vectors, vectors, 0);
        builder.AddOffset(SinkSlot.Children, children, 0);
        builder.AddOffset(SinkSlot.ShapesType, shapeTypes, 0);
        builder.AddOffset(SinkSlot.Shapes, shapes, 0);
        builder.AddOffset(SinkSlot.RequiredChildren, requiredChildren, 0);
        builder.AddOffset(SinkSlot.Bools, bools, 0);
        builder.AddOffset(SinkSlot.Bytes, bytes, 0);
        builder.AddOffset(SinkSlot.Colors, colors, 0);
        builder.AddOffset(SinkSlot.Sbytes, sbytes, 0);
        builder.AddOffset(SinkSlot.Ints, ints, 0);
        builder.AddOffset(SinkSlot.Longs, longs, 0);
        builder.AddOffset(SinkSlot.Floats, floats, 0);
        builder.AddOffset(SinkSlot.Signeds, signeds, 0);
        builder.AddOffset(SinkSlot.Bigs, bigs, 0);
        builder.Finish(builder.EndTable());
        return builder.SizedByteArray();
    }

    private static Vec3 ReadVec3(ByteBuffer buffer, int position) =>
        TestData.Vec(buffer.GetDouble(position), buffer.GetDouble(position + 8), buffer.GetDouble(position + 16));

    private static int CreateVec3(FlatBufferBuilder builder, Vec3 value)
    {
        builder.Prep(8, 24);
        builder.PutDouble(value.Z);
        builder.PutDouble(value.Y);
        builder.PutDouble(value.X);
        return builder.Offset;
    }

    private static int CreateMarker(FlatBufferBuilder builder, int id)
    {
        builder.Prep(4, 4);
        builder.PutInt(id);
        return builder.Offset;
    }

    private static int CreateVec3Vector(FlatBufferBuilder builder, params Vec3[] values)
    {
        builder.StartVector(24, values.Length, 8);
        for (var i = values.Length - 1; i >= 0; i--)
        {
            CreateVec3(builder, values[i]);
        }

        return builder.EndVector().Value;
    }

    private static int CreateStringVector(FlatBufferBuilder builder, params string[] values)
    {
        var offsets = values.Select(v => builder.CreateString(v).Value).ToArray();
        return CreateOffsetVector(builder, offsets);
    }

    private static int CreateOffsetVector(FlatBufferBuilder builder, params int[] offsets)
    {
        builder.StartVector(4, offsets.Length, 4);
        for (var i = offsets.Length - 1; i >= 0; i--)
        {
            builder.AddOffset(offsets[i]);
        }

        return builder.EndVector().Value;
    }

    private static int CreateDoubleVector(FlatBufferBuilder builder, params double[] values)
    {
        builder.StartVector(8, values.Length, 8);
        for (var i = values.Length - 1; i >= 0; i--)
        {
            builder.AddDouble(values[i]);
        }

        return builder.EndVector().Value;
    }

    /// <summary>
    /// A vector of <paramref name="count"/> elements of <paramref name="size"/> bytes; <paramref name="add"/> adds the
    /// element at an index, and is called from the last element to the first, as the builder builds backwards.
    /// </summary>
    private static int CreateVector(FlatBufferBuilder builder, int size, int count, Action<int> add)
    {
        builder.StartVector(size, count, size);
        for (var i = count - 1; i >= 0; i--)
        {
            add(i);
        }

        return builder.EndVector().Value;
    }

    private static int CreateByteVector(FlatBufferBuilder builder, params byte[] values)
    {
        builder.StartVector(1, values.Length, 1);
        for (var i = values.Length - 1; i >= 0; i--)
        {
            builder.AddByte(values[i]);
        }

        return builder.EndVector().Value;
    }
}
