using System.Buffers;
using System.Runtime.CompilerServices;
using Bequadro.Internal;
using Bequadro.Tests.Interop;
using Bequadro.Tests.Kitchen;
using Bequadro.Tests.Shapes;
using Google.FlatBuffers;

namespace Bequadro.Tests;

/// <summary>
/// SER-001, SER-005, SER-007 and SER-013: entry points, determinism, alignment and memory held between calls.
/// </summary>
public class SerializerTests
{
    [Fact]
    public void Instance_IsTheTablesSerializer()
    {
        FlatBufferSerializer<Sink>.Instance.Should().BeSameAs(Sink.Serializer);
    }

    [Fact]
    public void Write_BufferWriter_AppendsSameBytesAsWriteToArray()
    {
        var sink = TestData.FullSink();
        var destination = new ArrayBufferWriter<byte>();
        destination.Write<byte>([1, 2, 3]);

        Sink.Serializer.Write(destination, sink);

        destination.WrittenSpan[3..].ToArray().Should().Equal(Sink.Serializer.WriteToArray(sink));
    }

    [Fact]
    public void WriteToMemory_Sink_MatchesWriteToArray()
    {
        var sink = TestData.FullSink();

        Sink.Serializer.WriteToMemory(sink).ToArray().Should().Equal(Sink.Serializer.WriteToArray(sink));
    }

    [Fact]
    public void Parse_MemoryAndArrayOverloads_ParseSameBuffer()
    {
        var bytes = Sink.Serializer.WriteToArray(TestData.FullSink());

        Sink.Serializer.Parse(bytes).Name.Should().Be("full");
        Sink.Serializer.Parse(new ReadOnlyMemory<byte>(bytes)).Name.Should().Be("full");
        Sink.Serializer.Parse(bytes.AsSpan()).Name.Should().Be("full");
    }

    [Fact]
    public void Parse_NullArray_ThrowsArgumentNull()
    {
        var parse = () => Sink.Serializer.Parse((byte[])null!);

        parse.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Write_NullDestination_ThrowsArgumentNull()
    {
        var write = () => Sink.Serializer.Write(null!, TestData.FullSink());

        write.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteToArray_EqualGraphs_ProducesIdenticalBytes(bool shareStrings)
    {
        var first = Sink.Serializer.WriteToArray(TestData.FullSink(), shareStrings);
        var second = Sink.Serializer.WriteToArray(TestData.FullSink(), shareStrings);

        second.Should().Equal(first);
    }

    [Fact]
    public void WriteToArray_ConcurrentWrites_ProduceIdenticalBytes()
    {
        var expected = Sink.Serializer.WriteToArray(TestData.FullSink());

        var results = Enumerable
            .Range(0, 64)
            .AsParallel()
            .Select(_ => Sink.Serializer.WriteToArray(TestData.FullSink()))
            .ToList();

        results.Should().AllSatisfy(r => r.Should().Equal(expected));
    }

    [Fact]
    public void WriteToArray_Sink_AlignsEightByteValues()
    {
        var bytes = Sink.Serializer.WriteToArray(TestData.FullSink());
        var table = GoogleTable.Root(bytes);

        bytes.Length.Should().Be(bytes.Length / 8 * 8);
        (table.FieldPosition(13) % 8).Should().Be(0, "doubles are 8-byte aligned");
        (table.FieldPosition(10) % 8).Should().Be(0, "longs are 8-byte aligned");
        (table.FieldPosition(23) % 8).Should().Be(0, "structs are aligned to their alignment");
        (table.VectorStart(31) % 8).Should().Be(0, "double vector elements are 8-byte aligned");
        (table.VectorStart(34) % 8).Should().Be(0, "struct vector elements are aligned to the struct alignment");
        (table.Position % 4).Should().Be(0);
    }

    /// <summary>
    /// SER-007: the returned memory is a whole array, whose data .NET aligns to 8 bytes on 64-bit platforms, the
    /// largest alignment a schema can use. So 8-byte values in it are aligned in memory, not only in the buffer.
    /// </summary>
    [Fact]
    public void WriteToMemory_Sink_StartsAtAnAlignedAddress()
    {
        Assert.SkipUnless(
            Environment.Is64BitProcess,
            "The alignment of array data is only promised on 64-bit platforms."
        );
        for (var i = 0; i < 100; i++)
        {
            var memory = Sink.Serializer.WriteToMemory(TestData.FullSink());

            System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment).Should().BeTrue();
            segment.Offset.Should().Be(0);
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(
                segment.Array,
                System.Runtime.InteropServices.GCHandleType.Pinned
            );
            try
            {
                (handle.AddrOfPinnedObject() % 8).Should().Be(0);
            }
            finally
            {
                handle.Free();
            }
        }
    }

    [Fact]
    public void WriteToArray_LargeGraph_GrowsBuffer()
    {
        var nodes = Enumerable.Range(0, 20_000).Select(i => new Node { Name = $"node {i}" }).ToArray();

        var parsed = Graph.Serializer.Parse(Graph.Serializer.WriteToArray(new Graph { Nodes = nodes, Lines = [] }));

        parsed.Nodes.Should().HaveCount(20_000);
        parsed.Nodes[^1].Name.Should().Be("node 19999");
    }

    [Fact]
    public void WriteToArray_RepeatedLargeWrites_AllocatesLittleBeyondTheResult()
    {
        var graph = new Graph
        {
            Nodes = Enumerable.Range(0, 20_000).Select(i => new Node { Name = $"node {i}" }).ToArray(),
            Lines = [],
        };
        Graph.Serializer.WriteToArray(graph);
        Graph.Serializer.WriteToArray(graph);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var bytes = Graph.Serializer.WriteToArray(graph);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(bytes.Length + (64 * 1024), "the working buffer and lookup tables are reused");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteToArray_Returned_KeepsNoReferenceToGraph(bool shareStrings)
    {
        var (graph, name) = WriteAndForget(shareStrings, fail: false);

        CollectGarbage();

        graph.IsAlive.Should().BeFalse("the writer cached for the thread must not keep the written graph alive");
        name.IsAlive.Should().BeFalse("the writer cached for the thread must not keep written strings alive");
    }

    [Fact]
    public void WriteToArray_Thrown_KeepsNoReferenceToGraph()
    {
        var (graph, name) = WriteAndForget(shareStrings: true, fail: true);

        CollectGarbage();

        graph.IsAlive.Should().BeFalse("a failed write must not leave the graph in the cached writer");
        name.IsAlive.Should().BeFalse("a failed write must not leave strings in the cached writer");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_Returned_KeepsNoReferenceToGraph(bool shareStrings)
    {
        var bytes = Graph.Serializer.WriteToArray(SharedNameGraph(), shareStrings: true);

        var (graph, name) = ParseAndForget(bytes, shareStrings);
        CollectGarbage();

        graph.IsAlive.Should().BeFalse("the lookups cached for the thread must not keep the parsed graph alive");
        name.IsAlive.Should().BeFalse("the lookups cached for the thread must not keep parsed strings alive");
    }

    [Fact]
    public void Parse_Thrown_KeepsNoReferenceToGraph()
    {
        // The root table is added to the lookups before its missing required field is detected
        var builder = new FlatBufferBuilder(16);
        builder.StartTable(2);
        builder.Finish(builder.EndTable());
        var parse = () => Circle.Serializer.Parse(builder.SizedByteArray(), shareStrings: true);
        parse.Should().Throw<InvalidDataException>();

        // The partly read graph never reaches the caller, so look at the lookups cached for this thread instead
        var lookups = ReaderCache.Rent();
        try
        {
            lookups.Tables.Should().BeEmpty("a failed parse must not leave tables in the cached lookups");
        }
        finally
        {
            ReaderCache.Return(lookups);
        }
    }

    [Fact]
    public void WriteToArray_TablesWithSameShape_ShareVtable()
    {
        var one = Graph.Serializer.WriteToArray(new Graph { Nodes = [new Node { Name = "a" }], Lines = [] });
        var two = Graph.Serializer.WriteToArray(
            new Graph { Nodes = [new Node { Name = "a" }, new Node { Name = "b" }], Lines = [] }
        );

        // The second node adds a 4 byte uoffset, a 4 byte soffset and an 8 byte string, but no new vtable
        (two.Length - one.Length).Should().BeLessThanOrEqualTo(16);
    }

    [Fact]
    public void WriteToArray_TablesOfOneTypeWithDifferentShapes_EachGetAVtableOfTheirOwnFieldsWrittenOnce()
    {
        // Shapes in turn, so the vtable the previous Node used is usually the wrong one
        var nodes = Enumerable
            .Range(0, 12)
            .Select(i =>
                (i % 3) switch
                {
                    0 => new Node { Name = "a" },
                    1 => new Node(),
                    _ => new Node { Name = "b", Neighbors = [] },
                }
            )
            .ToList();
        var bytes = Graph.Serializer.WriteToArray(new Graph { Nodes = nodes, Lines = [] });
        var graph = GoogleTable.Root(bytes);

        var vtables = new List<(int Position, string Bytes)>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = graph.VectorTable(0, i);
            node.Has(0).Should().Be(nodes[i].Name is not null);
            node.Has(1).Should().Be(nodes[i].Neighbors is not null);
            var position = node.Position - node.Buffer.GetInt(node.Position);
            vtables.Add((position, Convert.ToHexString(bytes, position, node.Buffer.GetShort(position))));
        }

        // Identical vtables are written once, so equal bytes are always at the same position
        vtables
            .GroupBy(v => v.Bytes)
            .Should()
            .AllSatisfy(group => group.Select(v => v.Position).Distinct().Should().ContainSingle());
        vtables.Select(v => v.Position).Distinct().Should().HaveCountGreaterThan(2).And.HaveCountLessThan(nodes.Count);
    }

    [Fact]
    public void WriteToArray_AfterALargerWriteOfTheSameShapes_WritesTheSameBytes()
    {
        static Graph Small() => new() { Nodes = [new Node { Name = "a" }], Lines = [] };
        var before = Graph.Serializer.WriteToArray(Small());

        // The cached writer then holds the vtables of this write, at offsets beyond the end of the next one, which
        // rents the same pooled buffer with this write's bytes still in it
        Graph.Serializer.WriteToArray(
            new Graph { Nodes = [.. Enumerable.Range(0, 1000).Select(i => new Node { Name = $"n{i}" })], Lines = [] }
        );

        Graph.Serializer.WriteToArray(Small()).Should().Equal(before);
    }

    [Fact]
    public void WriteToArray_UnionFields_PlaceDiscriminatorsTogether()
    {
        var bytes = Holder.Serializer.WriteToArray(
            new Holder { RequiredShape = new Marker(), RequiredStrict = new Marker() }
        );
        var table = GoogleTable.Root(bytes);
        var vtable = table.Position - table.Buffer.GetInt(table.Position);

        // A 4 byte soffset, two 4 byte uoffsets, two 1 byte discriminators and 2 bytes of padding. With each
        // discriminator next to its uoffset, both would need 3 bytes of padding.
        table.Buffer.GetShort(vtable + 2).Should().Be(16);
    }

    // The graph is built and dropped in a method that isn't inlined, so no local in the test keeps it alive
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Graph, WeakReference Name) WriteAndForget(bool shareStrings, bool fail)
    {
        var graph = SharedNameGraph();
        if (fail)
        {
            graph.Lines = [new Line { Start = graph.Nodes[0], End = null! }];
        }

        try
        {
            Graph.Serializer.WriteToArray(graph, shareStrings);
        }
        catch (InvalidOperationException) when (fail) { }

        return (new WeakReference(graph), new WeakReference(graph.Nodes[0].Name));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Graph, WeakReference Name) ParseAndForget(byte[] bytes, bool shareStrings)
    {
        var graph = Graph.Serializer.Parse(bytes, shareStrings);
        return (new WeakReference(graph), new WeakReference(graph.Nodes[0].Name));
    }

    // Two nodes with equal names, built separately so that string sharing has something to share. The names are
    // built at run time because string literals are interned and never collected.
    private static Graph SharedNameGraph() =>
        new()
        {
            Nodes = [new Node { Name = new string('x', 100) }, new Node { Name = new string('x', 100) }],
            Lines = [],
        };

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
