using Bequadro.Tests.Common;
using Bequadro.Tests.Kitchen;
using Bequadro.Tests.Limits;
using Bequadro.Tests.Shapes;

namespace Bequadro.Tests;

public class RoundTripTests
{
    [Fact]
    public void Parse_WrittenFullSink_RestoresEveryField()
    {
        var expected = TestData.FullSink();

        var actual = Sink.Serializer.Parse(Sink.Serializer.WriteToArray(expected));

        actual.Name.Should().Be("full");
        actual.Title.Should().Be("title");
        actual.Flag.Should().BeTrue();
        actual.FlagDefaultTrue.Should().BeFalse();
        actual.Small.Should().Be(-100);
        actual.Tiny.Should().Be(7);
        actual.Medium.Should().Be(-1234);
        actual.UnsignedMedium.Should().Be(65000);
        actual.Count.Should().Be(0);
        actual.UnsignedCount.Should().Be(4_000_000_000);
        actual.Wide.Should().Be(long.MaxValue);
        actual.UnsignedWide.Should().Be(12);
        actual.Ratio.Should().Be(1.25f);
        actual.Precise.Should().Be(-2.5);
        actual.NotANumber.Should().Be(3);
        actual.Infinite.Should().Be(4);
        actual.OptionalInt.Should().Be(0);
        actual.OptionalColor.Should().Be(Color.Red);
        actual.Color.Should().Be(Color.Green);
        actual.Signed.Should().Be(Signed.Negative);
        actual.Big.Should().Be(Big.Huge);
        actual.Position.Should().Be(TestData.Vec(4, 5, 6));
        actual.RequiredPosition.Should().Be(TestData.Vec(7, 8, 9));
        actual.Mixed.Should().Be(expected.Mixed);
        actual.Child!.Title.Should().Be("child");
        actual.Shape.Poly.Points.Should().Equal(TestData.Vec(1, 1, 1), TestData.Vec(2, 2, 2));
        actual.RequiredShape.Marker.Id.Should().Be(99);
        actual.Bools.Should().Equal(true, false, true);
        actual.Bytes.Should().Equal((byte)1, (byte)2, (byte)3, (byte)255);
        actual.Doubles.Should().HaveCount(3).And.Subject.Last().Should().Be(double.NaN);
        actual.Colors.Should().Equal(Color.Blue, Color.Red);
        actual.Strings.Should().Equal("a", "", "héllo ✓");
        actual.Vectors.Should().Equal(TestData.Vec(1, 2, 3));
        actual.Children.Should().HaveCount(2);
        actual
            .Shapes!.Select(s => s.Kind)
            .Should()
            .Equal(Shape.ItemKind.Circle, Shape.ItemKind.Marker, Shape.ItemKind.Circle);
        actual.Shapes![0].Circle.Radius.Should().Be(2);
        actual.Shapes![1].Marker.Id.Should().Be(5);
        actual.RequiredChildren.Should().ContainSingle();
        actual.Sbytes.Should().Equal(expected.Sbytes);
        actual.Shorts.Should().Equal(expected.Shorts);
        actual.Ushorts.Should().Equal(expected.Ushorts);
        actual.Ints.Should().Equal(expected.Ints);
        actual.Uints.Should().Equal(expected.Uints);
        actual.Longs.Should().Equal(expected.Longs);
        actual.Ulongs.Should().Equal(expected.Ulongs);
        actual.Floats.Should().Equal(expected.Floats);
        BitConverter.SingleToInt32Bits(actual.Floats![1]).Should().Be(int.MinValue, "-0 keeps its sign");
        actual.Signeds.Should().Equal(expected.Signeds);
        actual.Bigs.Should().Equal(expected.Bigs);
    }

    [Fact]
    public void Parse_WrittenMinimalSink_UsesSchemaDefaults()
    {
        var actual = Sink.Serializer.Parse(Sink.Serializer.WriteToArray(TestData.MinimalSink()));

        actual.Name.Should().BeNull();
        actual.FlagDefaultTrue.Should().BeTrue();
        actual.Small.Should().Be(-3);
        actual.Tiny.Should().Be(200);
        actual.Count.Should().Be(42);
        actual.Wide.Should().Be(long.MinValue);
        actual.UnsignedWide.Should().Be(ulong.MaxValue);
        actual.Ratio.Should().Be(0.5f);
        actual.Precise.Should().Be(0.925);
        actual.NotANumber.Should().Be(double.NaN);
        actual.Infinite.Should().Be(float.NegativeInfinity);
        actual.OptionalInt.Should().BeNull();
        actual.OptionalColor.Should().BeNull();
        actual.Color.Should().Be(Color.Blue);
        actual.Signed.Should().Be(Signed.Positive);
        actual.Big.Should().Be(Big.Small);
        actual.Position.Should().BeNull();
        actual.Mixed.Should().BeNull();
        actual.Child.Should().BeNull();
        actual.Shape.Kind.Should().Be(Shape.ItemKind.NONE);
        actual.Bools.Should().BeNull();
        actual.Strings.Should().BeNull();
        actual.Shapes.Should().BeNull();
        actual.RequiredChildren.Should().BeEmpty();
    }

    /// <summary>
    /// API-007: any implementation of a vector writes the same bytes. Arrays and lists are copied in bulk, and other
    /// implementations element by element.
    /// </summary>
    [Fact]
    public void WriteToArray_AnyVectorImplementation_WritesTheSameBytes()
    {
        double[] doubles = [1.5, -2, double.NaN];
        Color[] colors = [Color.Blue, Color.Red];
        Vec3[] vectors = [TestData.Vec(1, 2, 3), TestData.Vec(4, 5, 6)];
        Sink Make(
            Func<double[], IReadOnlyList<double>> d,
            Func<Color[], IReadOnlyList<Color>> c,
            Func<Vec3[], IReadOnlyList<Vec3>> v
        )
        {
            var sink = TestData.MinimalSink();
            sink.Doubles = d(doubles);
            sink.Colors = c(colors);
            sink.Vectors = v(vectors);
            return sink;
        }

        var fromArrays = Sink.Serializer.WriteToArray(Make(a => a, a => a, a => a));
        var fromLists = Sink.Serializer.WriteToArray(
            Make(a => new List<double>(a), a => new List<Color>(a), a => new List<Vec3>(a))
        );
        var fromOthers = Sink.Serializer.WriteToArray(
            Make(a => a.AsReadOnly(), a => a.AsReadOnly(), a => a.AsReadOnly())
        );

        fromLists.Should().Equal(fromArrays);
        fromOthers.Should().Equal(fromArrays);
        var parsed = Sink.Serializer.Parse(fromLists);
        parsed.Doubles.Should().Equal(doubles);
        parsed.Colors.Should().Equal(colors);
        parsed.Vectors.Should().Equal(vectors);
    }

    [Fact]
    public void WriteToArray_EmptyTable_RoundTrips()
    {
        var bytes = Empty.Serializer.WriteToArray(new Empty());

        Empty.Serializer.Parse(bytes).Should().NotBeNull();
    }

    /// <summary>
    /// SCH-006: the largest struct the generator accepts still fits in a table, whose inline data is at most 65,535 bytes.
    /// </summary>
    [Fact]
    public void RoundTrip_LargestStruct_FitsInTable()
    {
        var largest = new Largest();
        largest.A.A.A.A.A.A.A.A.A.A.A.A.A = 1.5;
        largest.L.B = 2.5;

        var parsed = LargestHolder.Serializer.Parse(
            LargestHolder.Serializer.WriteToArray(new LargestHolder { Largest = largest })
        );

        parsed.Largest.Should().Be(largest);
        parsed.Largest.L.B.Should().Be(2.5);
    }
}
