using Bequadro.Internal;
using Bequadro.Tests.Kitchen;
using Bequadro.Tests.Limits;
using Bequadro.Tests.Shapes;

namespace Bequadro.Tests;

/// <summary>
/// SER-004: object graphs that cannot be serialized fail with a clear InvalidOperationException.
/// </summary>
public class WriteErrorTests
{
    [Fact]
    public void WriteToArray_NullRequiredString_ThrowsNamingField()
    {
        var sink = TestData.MinimalSink();
        sink.Title = null!;

        var write = () => Sink.Serializer.WriteToArray(sink);

        write.Should().Throw<InvalidOperationException>().WithMessage("*'title'*'Bequadro.Tests.Kitchen.Sink'*");
    }

    [Fact]
    public void WriteToArray_NullRequiredVector_Throws()
    {
        var sink = TestData.MinimalSink();
        sink.RequiredChildren = null!;

        var write = () => Sink.Serializer.WriteToArray(sink);

        write.Should().Throw<InvalidOperationException>().WithMessage("*'required_children'*");
    }

    [Fact]
    public void WriteToArray_NullRequiredTable_Throws()
    {
        var write = () =>
            Graph.Serializer.WriteToArray(
                new Graph { Nodes = [], Lines = [new Line { Start = new Node(), End = null! }] }
            );

        write.Should().Throw<InvalidOperationException>().WithMessage("*'end'*");
    }

    [Fact]
    public void WriteToArray_RequiredUnionIsNone_ThrowsNamingField()
    {
        var sink = TestData.MinimalSink();
        sink.RequiredShape = default;

        var write = () => Sink.Serializer.WriteToArray(sink);

        write.Should().Throw<InvalidOperationException>().WithMessage("*'required_shape'*Sink*NONE*");
    }

    [Fact]
    public void WriteToArray_ExcludeNoneUnionWithNoValue_Throws()
    {
        var holder = new Holder { RequiredShape = new Marker(), RequiredStrict = default };

        var write = () => Holder.Serializer.WriteToArray(holder);

        write
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Required field 'required_strict' of table 'Bequadro.Tests.Shapes.Holder' has no value.");
    }

    [Fact]
    public void WriteToArray_OptionalExcludeNoneUnionWithNoValue_ThrowsNamingField()
    {
        var holder = new Holder
        {
            RequiredShape = new Marker(),
            RequiredStrict = new Marker(),
            Strict = default(StrictShape),
        };

        var write = () => Holder.Serializer.WriteToArray(holder);

        write
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Field 'strict' of table 'Bequadro.Tests.Shapes.Holder' holds a union with no value.*");
    }

    [Fact]
    public void WriteToArray_ExcludeNoneUnionVectorWithNoValue_Throws()
    {
        var holder = new Holder
        {
            RequiredShape = new Marker(),
            RequiredStrict = new Marker(),
            Stricts = [new Marker(), default],
        };

        var write = () => Holder.Serializer.WriteToArray(holder);

        write.Should().Throw<InvalidOperationException>().WithMessage("*union*StrictShape*no value*");
    }

    [Fact]
    public void WriteToArray_UnknownUnionMember_Throws()
    {
        var holder = new Holder
        {
            RequiredShape = new Marker(),
            RequiredStrict = new Marker(),
            Shape = new UnionUnknown(9),
        };

        var write = () => Holder.Serializer.WriteToArray(holder);

        write.Should().Throw<InvalidOperationException>().WithMessage("*union*Shape*unknown member (discriminator 9)*");
    }

    [Fact]
    public void WriteToArray_NullVectorElement_Throws()
    {
        var sink = TestData.MinimalSink();
        sink.Strings = ["a", null!];

        var write = () => Sink.Serializer.WriteToArray(sink);

        write.Should().Throw<InvalidOperationException>().WithMessage("*null element*");
    }

    [Fact]
    public void WriteToArray_NullTableVectorElement_ThrowsNamingTable()
    {
        var sink = TestData.MinimalSink();
        sink.Children = [TestData.MinimalSink(), null!];

        var write = () => Sink.Serializer.WriteToArray(sink);

        write.Should().Throw<InvalidOperationException>().WithMessage("*null element*'Bequadro.Tests.Kitchen.Sink'*");
    }

    [Fact]
    public void WriteToArray_NullRoot_ThrowsArgumentNull()
    {
        var write = () => Sink.Serializer.WriteToArray(null!);

        write.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// SER-012, tested on the growth calculation because a real buffer of this size needs gigabytes of memory.
    /// </summary>
    [Fact]
    public void GrowCapacity_BeyondLargestArray_ThrowsInvalidOperation()
    {
        var grow = () => FlatBufferWriter.GrowCapacity(1 << 30, Array.MaxLength + 1L);

        grow.Should().Throw<InvalidOperationException>().WithMessage($"*{Array.MaxLength}*");
    }

    /// <summary>
    /// SER-012: a vector whose elements alone exceed the limit throws before anything is allocated for it, and
    /// before its elements are read.
    /// </summary>
    [Fact]
    public void WriteToArray_VectorBeyondLargestArray_ThrowsInvalidOperation()
    {
        var scalars = TestData.MinimalSink();
        scalars.Doubles = new CountOnlyList<double>(int.MaxValue / sizeof(double) + 1);
        var structs = TestData.MinimalSink();
        structs.Vectors = new CountOnlyList<Common.Vec3>(int.MaxValue / 24 + 1);

        var writeScalars = () => Sink.Serializer.WriteToArray(scalars);
        var writeStructs = () => Sink.Serializer.WriteToArray(structs);

        writeScalars.Should().Throw<InvalidOperationException>().WithMessage($"*{Array.MaxLength}*");
        writeStructs.Should().Throw<InvalidOperationException>().WithMessage($"*{Array.MaxLength}*");
    }

    /// <summary>
    /// A list that only has a count, so the write can't have read it.
    /// </summary>
    private sealed class CountOnlyList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;

        public T this[int index] => throw new InvalidOperationException("The element was read.");

        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("The list was enumerated.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void WriteToArray_TableWhoseInlineFieldsExceedAVtable_ThrowsNamingTable()
    {
        var fits = TooLarge.Serializer.WriteToArray(new TooLarge { Largest = new Largest() });
        var write = () => TooLarge.Serializer.WriteToArray(new TooLarge { Largest = new Largest(), Extra = new S0() });

        TooLarge.Serializer.Parse(fits).Largest.Should().NotBeNull();
        write
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot serialize an instance of 'Bequadro.Tests.Limits.TooLarge': a table's inline fields can take at most 65535 bytes*"
            );
    }

    [Theory]
    [InlineData(1024, 1025, 2048)]
    [InlineData(1024, 5000, 5000)]
    [InlineData(1 << 30, (1L << 30) + 1, 0x7FFFFFC7)]
    public void GrowCapacity_Required_DoublesUpToLargestArray(int current, long required, int expected)
    {
        FlatBufferWriter.GrowCapacity(current, required).Should().Be(expected);
    }
}
