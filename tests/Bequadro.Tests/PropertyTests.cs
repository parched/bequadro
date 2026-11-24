using System.Text;
using Bequadro.Tests.Common;
using Bequadro.Tests.Kitchen;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Bequadro.Tests;

/// <summary>
/// QUA-001: property-based round trips over generated inputs.
/// </summary>
public class PropertyTests
{
    [Property(MaxTest = 300)]
    public bool RoundTrip_ScalarsAndStrings_Preserved(
        sbyte small,
        byte tiny,
        short medium,
        ushort unsignedMedium,
        int count,
        uint unsignedCount,
        long wide,
        ulong unsignedWide,
        NormalFloat precise,
        bool flag,
        NonNull<string> name,
        int? optional
    )
    {
        var sink = TestData.MinimalSink();
        sink.Small = small;
        sink.Tiny = tiny;
        sink.Medium = medium;
        sink.UnsignedMedium = unsignedMedium;
        sink.Count = count;
        sink.UnsignedCount = unsignedCount;
        sink.Wide = wide;
        sink.UnsignedWide = unsignedWide;
        sink.Precise = precise.Get;
        sink.Flag = flag;
        sink.Name = name.Get;
        sink.OptionalInt = optional;

        var parsed = Sink.Serializer.Parse(Sink.Serializer.WriteToArray(sink));

        return parsed.Small == small
            && parsed.Tiny == tiny
            && parsed.Medium == medium
            && parsed.UnsignedMedium == unsignedMedium
            && parsed.Count == count
            && parsed.UnsignedCount == unsignedCount
            && parsed.Wide == wide
            && parsed.UnsignedWide == unsignedWide
            && parsed.Precise.Equals(precise.Get)
            && parsed.Flag == flag
            && parsed.Name == Utf8RoundTrip(name.Get)
            && parsed.OptionalInt == optional;
    }

    [Property(MaxTest = 200)]
    public bool RoundTrip_Vectors_Preserved(double[] doubles, bool[] bools, NonNull<string>[] strings, byte[] colors)
    {
        var sink = TestData.MinimalSink();
        sink.Doubles = doubles;
        sink.Bools = bools;
        sink.Strings = strings.Select(s => s.Get).ToList();
        sink.Colors = colors.Select(c => (Color)c).ToArray();
        sink.Vectors = doubles.Select(d => TestData.Vec(d, -d, 0)).ToArray();

        var parsed = Sink.Serializer.Parse(Sink.Serializer.WriteToArray(sink));

        return parsed.Doubles!.SequenceEqual(doubles)
            && parsed.Bools!.SequenceEqual(bools)
            && parsed.Strings!.SequenceEqual(strings.Select(s => Utf8RoundTrip(s.Get)))
            && parsed.Colors!.SequenceEqual(sink.Colors)
            && parsed.Vectors!.SequenceEqual(sink.Vectors);
    }

    [Property(MaxTest = 200)]
    public bool RoundTrip_RandomGraph_PreservesStructureAndSharing(PositiveInt nodeCount, int[] edges)
    {
        var nodes = Enumerable.Range(0, Math.Min(nodeCount.Get, 50)).Select(i => new Node { Name = $"n{i}" }).ToArray();
        var lines = new List<Line>();
        for (var i = 0; i + 1 < edges.Length; i += 2)
        {
            var start = nodes[Math.Abs(edges[i] % nodes.Length)];
            var end = nodes[Math.Abs(edges[i + 1] % nodes.Length)];
            lines.Add(new Line { Start = start, End = end });
            start.Neighbors = [.. start.Neighbors ?? [], end];
        }

        // Neighbors can form cycles, which cannot be serialized; keep only forward references
        foreach (var node in nodes)
        {
            var index = Array.IndexOf(nodes, node);
            node.Neighbors = node.Neighbors?.Where(n => Array.IndexOf(nodes, n) > index).ToArray();
        }

        var graph = new Graph { Nodes = nodes, Lines = lines };
        var parsed = Graph.Serializer.Parse(Graph.Serializer.WriteToArray(graph));

        var indexOf = parsed
            .Nodes.Select((n, i) => (n, i))
            .ToDictionary<(Node n, int i), Node, int>(p => p.n, p => p.i, ReferenceEqualityComparer.Instance);
        return parsed.Nodes.Count == nodes.Length
            && parsed.Lines.Count == lines.Count
            && lines
                .Zip(parsed.Lines)
                .All(pair =>
                    indexOf[pair.Second.Start] == Array.IndexOf(nodes, pair.First.Start)
                    && indexOf[pair.Second.End] == Array.IndexOf(nodes, pair.First.End)
                )
            && nodes
                .Zip(parsed.Nodes)
                .All(pair =>
                    (pair.First.Neighbors ?? [])
                        .Select(n => Array.IndexOf(nodes, n))
                        .SequenceEqual((pair.Second.Neighbors ?? []).Select(n => indexOf[n]))
                );
    }

    private static string Utf8RoundTrip(string value) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value));
}
