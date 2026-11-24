using Bequadro.Internal;
using Bequadro.Tests.Interop;
using Bequadro.Tests.Kitchen;
using Bequadro.Tests.Shapes;
using Google.FlatBuffers;

namespace Bequadro.Tests;

/// <summary>
/// SER-003 and SER-011: shared table instances are written once and read back as shared instances, strings in the
/// buffer are read once, and nesting is limited.
/// </summary>
public class IdentityTests
{
    [Fact]
    public void WriteToArray_SharedTables_WritesEachInstanceOnce()
    {
        var shared = new Node { Name = new string('x', 1000) };
        var once = new Graph { Nodes = [shared], Lines = [] };
        var many = new Graph
        {
            Nodes = [shared, shared, shared],
            Lines = [new Line { Start = shared, End = shared }],
            Root = shared,
        };

        var onceSize = Graph.Serializer.WriteToArray(once).Length;
        var bytes = Graph.Serializer.WriteToArray(many);

        // Every reference, from a field, a vector or another table, points at the same position
        var graph = GoogleTable.Root(bytes);
        var line = graph.VectorTable(1, 0);
        int[] positions =
        [
            graph.VectorIndirect(0, 0),
            graph.VectorIndirect(0, 1),
            graph.VectorIndirect(0, 2),
            graph.GetIndirect(2),
            line.GetIndirect(0),
            line.GetIndirect(1),
        ];
        positions.Should().AllBeEquivalentTo(positions[0]);
        graph.VectorTable(0, 0).GetString(0).Should().HaveLength(1000);
        bytes.Length.Should().BeLessThan(onceSize + 100, "the 1000 character node must only be written once");
    }

    /// <summary>
    /// Sharing follows instances, not equality, even for tables with value equality.
    /// </summary>
    [Fact]
    public void Parse_EqualButSeparateValueEqualityTables_ReturnsSeparateInstances()
    {
        var shared = new Equality.Unit { Symbol = "m" };
        var measure = new Equality.Measure { Unit = shared, Units = [shared, new Equality.Unit { Symbol = "m" }] };

        var parsed = Equality.Measure.Serializer.Parse(Equality.Measure.Serializer.WriteToArray(measure));

        parsed.Units![0].Should().BeSameAs(parsed.Unit);
        parsed.Units[1].Should().Be(parsed.Unit).And.NotBeSameAs(parsed.Unit);
    }

    [Fact]
    public void Parse_SharedTables_ReturnsSameInstances()
    {
        var a = new Node { Name = "a" };
        var b = new Node { Name = "b", Neighbors = [a] };
        var graph = new Graph
        {
            Nodes = [a, b],
            Lines = [new Line { Start = a, End = b }, new Line { Start = b, End = a }],
            Root = b,
        };

        var parsed = Graph.Serializer.Parse(Graph.Serializer.WriteToArray(graph));

        var (parsedA, parsedB) = (parsed.Nodes[0], parsed.Nodes[1]);
        parsedA.Should().NotBeSameAs(parsedB);
        parsed.Lines[0].Start.Should().BeSameAs(parsedA);
        parsed.Lines[0].End.Should().BeSameAs(parsedB);
        parsed.Lines[1].Start.Should().BeSameAs(parsedB);
        parsed.Lines[1].End.Should().BeSameAs(parsedA);
        parsed.Root.Should().BeSameAs(parsedB);
        parsedB.Neighbors.Should().ContainSingle().Which.Should().BeSameAs(parsedA);
    }

    [Fact]
    public void Parse_SharedTableInsideUnions_ReturnsSameInstance()
    {
        var circle = new Circle { Center = TestData.Vec(1, 2, 3) };
        var sink = TestData.MinimalSink();
        sink.Shape = circle;
        sink.RequiredShape = circle;
        sink.Shapes = [circle, circle];

        var parsed = Sink.Serializer.Parse(Sink.Serializer.WriteToArray(sink));

        parsed.Shape.Circle.Should().BeSameAs(parsed.RequiredShape.Circle);
        parsed.Shapes!.Select(s => s.Circle).Should().AllSatisfy(c => c.Should().BeSameAs(parsed.RequiredShape.Circle));
    }

    [Fact]
    public void Parse_RootReferencedByChild_ReturnsRootInstance()
    {
        var sink = TestData.MinimalSink();
        var child = TestData.MinimalSink();
        sink.Children = [child];
        sink.Child = child;

        var parsed = Sink.Serializer.Parse(Sink.Serializer.WriteToArray(sink));

        parsed.Child.Should().BeSameAs(parsed.Children![0]);
    }

    [Fact]
    public void Parse_TwoBuffers_DoesNotShareInstances()
    {
        var bytes = Graph.Serializer.WriteToArray(new Graph { Nodes = [new Node { Name = "n" }], Lines = [] });

        var first = Graph.Serializer.Parse(bytes, shareStrings: true);
        var second = Graph.Serializer.Parse(bytes, shareStrings: true);

        first.Nodes[0].Should().NotBeSameAs(second.Nodes[0]);
        first.Nodes[0].Name.Should().NotBeSameAs(second.Nodes[0].Name);
    }

    [Fact]
    public void Parse_SharedStringsWithShareStrings_ReturnsSameInstances()
    {
        var builder = new FlatBufferBuilder(1024);
        var first = builder.CreateSharedString("first");
        var second = builder.CreateSharedString("second");
        var bytes = BuildGraph(builder, [first, second, first, second, first]);

        var nodes = Graph.Serializer.Parse(bytes, shareStrings: true).Nodes;

        nodes.Select(n => n.Name).Should().Equal("first", "second", "first", "second", "first");
        nodes[0].Name.Should().NotBeSameAs(nodes[1].Name);
        nodes[2].Name.Should().BeSameAs(nodes[0].Name).And.BeSameAs(nodes[4].Name);
        nodes[3].Name.Should().BeSameAs(nodes[1].Name);
    }

    [Fact]
    public void Parse_SharedStringsByDefault_ReturnsSeparateCopies()
    {
        var builder = new FlatBufferBuilder(1024);
        var shared = builder.CreateSharedString("shared");
        var bytes = BuildGraph(builder, [shared, shared]);

        var nodes = Graph.Serializer.Parse(bytes).Nodes;

        nodes[0].Name.Should().Be("shared").And.NotBeSameAs(nodes[1].Name);
        nodes[1].Name.Should().Be("shared");
    }

    [Fact]
    public void WriteToArray_ShareStrings_WritesEqualStringsOnce()
    {
        var sink = TestData.MinimalSink();
        sink.Name = new string('x', 1000);
        sink.Title = new string('x', 1000);
        sink.Strings = [new string('x', 1000), "other", new string('x', 1000)];

        var shared = Sink.Serializer.WriteToArray(sink, shareStrings: true);
        var separate = Sink.Serializer.WriteToArray(sink);

        separate.Length.Should().BeGreaterThan(shared.Length + 3000, "by default each of the four has its own copy");
        var google = GoogleTable.Root(shared);
        google.GetString(0).Should().Be(sink.Name, "the official reader follows shared strings");
        google.GetString(1).Should().Be(sink.Name);
        var parsed = Sink.Serializer.Parse(shared, shareStrings: true);
        parsed.Name.Should().Be(sink.Name);
        parsed.Title.Should().BeSameAs(parsed.Name);
        parsed.Strings.Should().Equal(sink.Name, "other", sink.Name);
        parsed.Strings![0].Should().BeSameAs(parsed.Name);
        parsed.Strings[2].Should().BeSameAs(parsed.Name);
    }

    [Fact]
    public void Parse_LongStringReferencedManyTimesWithShareStrings_DecodesItOnce()
    {
        var builder = new FlatBufferBuilder(1024);
        var shared = builder.CreateSharedString(new string('x', 100_000));
        var bytes = BuildGraph(builder, Enumerable.Repeat(shared, 1000).ToArray());
        Graph.Serializer.Parse(bytes, shareStrings: true);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var nodes = Graph.Serializer.Parse(bytes, shareStrings: true).Nodes;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        nodes.Should().AllSatisfy(n => n.Name.Should().BeSameAs(nodes[0].Name));
        allocated
            .Should()
            .BeLessThan(1024 * 1024, "one copy of the string is 200 KB, but one per node would be 200 MB");
    }

    [Fact]
    public void StringLookup_UnalignedPositionsSharingAMark_KeepTheirOwnStrings()
    {
        var lookup = new StringLookup();
        lookup.Start(64);

        lookup.TryGet(8, out _).Should().BeFalse();
        lookup.Add(8, "aligned");
        lookup.TryGet(9, out _).Should().BeFalse("position 9 shares a mark with 8, but was never read");
        lookup.Add(9, "unaligned");

        lookup.TryGet(8, out var aligned).Should().BeTrue();
        aligned.Should().Be("aligned");
        lookup.TryGet(9, out var unaligned).Should().BeTrue();
        unaligned.Should().Be("unaligned");

        lookup.Reset();
        lookup.Start(64);
        lookup.TryGet(8, out _).Should().BeFalse("a reset lookup holds nothing from the previous parse");
    }

    [Fact]
    public void WriteToArray_TablesNestedToParseLimit_RoundTrips()
    {
        var parsed = Node.Serializer.Parse(Node.Serializer.WriteToArray(Chain(64)));

        var depth = 1;
        for (var node = parsed; node.Self is { } next; node = next)
        {
            depth++;
        }

        depth.Should().Be(64);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(100_000)]
    public void WriteToArray_TablesNestedBeyondParseLimit_ThrowsInvalidOperation(int length)
    {
        var write = () => Node.Serializer.WriteToArray(Chain(length));

        write.Should().Throw<InvalidOperationException>().WithMessage("*more than 64 levels*Node' is at level 65.");
        Node.Serializer.WriteToArray(Chain(64)).Should().NotBeEmpty("a failed write leaves the writer reusable");
    }

    [Fact]
    public void WriteToArray_ParsedGraph_PreservesSharing()
    {
        var node = new Node { Name = "n" };
        var graph = new Graph { Nodes = [node], Lines = [new Line { Start = node, End = node }] };
        var parsed = Graph.Serializer.Parse(Graph.Serializer.WriteToArray(graph));

        var reparsed = Graph.Serializer.Parse(Graph.Serializer.WriteToArray(parsed));

        reparsed.Lines[0].Start.Should().BeSameAs(reparsed.Nodes[0]).And.BeSameAs(reparsed.Lines[0].End);
    }

    [Fact]
    public void WriteToArray_DirectCycle_ThrowsInvalidOperation()
    {
        var node = new Node();
        node.Self = node;

        var write = () => Graph.Serializer.WriteToArray(new Graph { Nodes = [node], Lines = [] });

        write.Should().Throw<InvalidOperationException>().WithMessage("*cycle*Node*");
    }

    [Fact]
    public void WriteToArray_IndirectCycle_ThrowsInvalidOperation()
    {
        var a = new Node();
        var b = new Node { Neighbors = [a] };
        a.Neighbors = [b];

        var write = () => Graph.Serializer.WriteToArray(new Graph { Nodes = [a], Lines = [] });

        write.Should().Throw<InvalidOperationException>().WithMessage("*cycle*");
    }

    [Fact]
    public void WriteToArray_AfterFailedWrite_SucceedsOnSameThread()
    {
        var node = new Node();
        node.Self = node;
        var failing = () => Graph.Serializer.WriteToArray(new Graph { Nodes = [node], Lines = [] });
        failing.Should().Throw<InvalidOperationException>();

        var bytes = Graph.Serializer.WriteToArray(new Graph { Nodes = [new Node { Name = "ok" }], Lines = [] });

        Graph.Serializer.Parse(bytes).Nodes[0].Name.Should().Be("ok");
    }

    private static Node Chain(int length)
    {
        var node = new Node();
        for (var i = 1; i < length; i++)
        {
            node = new Node { Self = node };
        }

        return node;
    }

    /// <summary>
    /// Builds a Graph with the official builder, with one node per name, so names can share a string.
    /// </summary>
    private static byte[] BuildGraph(FlatBufferBuilder builder, StringOffset[] names)
    {
        var nodes = new int[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            builder.StartTable(3);
            builder.AddOffset(0, names[i].Value, 0);
            nodes[i] = builder.EndTable();
        }

        var nodesVector = CreateOffsetVector(builder, nodes);
        var linesVector = CreateOffsetVector(builder, []);
        builder.StartTable(3);
        builder.AddOffset(0, nodesVector, 0);
        builder.AddOffset(1, linesVector, 0);
        builder.Finish(builder.EndTable());
        return builder.SizedByteArray();
    }

    private static int CreateOffsetVector(FlatBufferBuilder builder, int[] offsets)
    {
        builder.StartVector(4, offsets.Length, 4);
        for (var i = offsets.Length - 1; i >= 0; i--)
        {
            builder.AddOffset(offsets[i]);
        }

        return builder.EndVector().Value;
    }
}
