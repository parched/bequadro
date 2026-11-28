using System.Text.Json;
using Bequadro.Tests.Common;
using Bequadro.Tests.Equality;
using Bequadro.Tests.Interop;
using Bequadro.Tests.Kitchen;
using Bequadro.Tests.Limits;

namespace Bequadro.Tests;

/// <summary>
/// QUA-003, SER-005: writes the buffers that the TypeScript and Python tests parse and write back, which must give
/// exactly <c>expected</c>. Each fixture's input is either written by Bequadro, so expected is the input itself, or
/// built with the official builder, so expected is what Bequadro writes for it. Parse fixtures, listed separately, are
/// buffers that the other languages' tests parse and check by name, such as unions with unknown discriminators.
/// </summary>
public class CrossLanguageFixtureTests
{
    /// <summary>
    /// Where the fixtures are written, under the repository's artifacts directory.
    /// </summary>
    public static readonly string Directory = Path.Combine(RepositoryRoot(), "artifacts", "cross-language");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void WriteFixtures_EveryFixture_RoundTripsInCSharp()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var manifest = new List<Dictionary<string, object>>();

        void Add<T>(string name, T graph, bool shareStrings = false)
            where T : class, Internal.IFlatBufferTable<T>
        {
            var bytes = FlatBufferSerializer<T>.Instance.WriteToArray(graph, shareStrings);
            AddBuffers(name, typeof(T), bytes, bytes, shareStrings);
        }

        void AddBuffers(string name, Type root, byte[] input, byte[] expected, bool shareStrings)
        {
            File.WriteAllBytes(Path.Combine(Directory, name + ".input.bin"), input);
            File.WriteAllBytes(Path.Combine(Directory, name + ".expected.bin"), expected);
            manifest.Add(
                new()
                {
                    ["name"] = name,
                    ["root"] = root.FullName!,
                    ["shareStrings"] = shareStrings,
                }
            );
        }

        Add("sink-minimal", TestData.MinimalSink());
        Add("sink-full", FullSinkWithoutNaN());
        Add("sink-full-shared-strings", FullSinkWithoutNaN(), shareStrings: true);
        Add("strings", StringsSink());
        Add("strings-shared", StringsSink(), shareStrings: true);
        Add("graph-shared-nodes", SharedGraph());
        Add("node-chain-64", NodeChain(64));
        Add("measure", FullMeasure());
        Add("largest-struct", new LargestHolder { Largest = default });

        var google = GoogleInteropTests.BuildGoogleSink();
        AddBuffers(
            "google-sink",
            typeof(Sink),
            google,
            Sink.Serializer.WriteToArray(Sink.Serializer.Parse(google)),
            shareStrings: false
        );

        // SCH-005: a buffer with fields that the table read it with has deprecated
        var beforeDeprecation = Evolution.Before.Serializer.WriteToArray(DeprecatedFieldTests.FullBefore());
        AddBuffers(
            "deprecated-fields",
            typeof(Evolution.After),
            beforeDeprecation,
            Rewrite<Evolution.After>(beforeDeprecation, shareStrings: false),
            shareStrings: false
        );

        File.WriteAllText(Path.Combine(Directory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));

        WriteParseFixture(
            "holder-unknown",
            UnionSerializationTests.HolderBuffer(
                shapeTypes: [200, UnionSerializationTests.MarkerType],
                requiredShapeType: 201
            )
        );
        WriteParseFixture(
            "holder-none-in-vector",
            UnionSerializationTests.HolderBuffer(shapeTypes: [0, UnionSerializationTests.MarkerType])
        );
        WriteParseFixture("holder-exhaustive-unknown", UnionSerializationTests.HolderBuffer(requiredStrictType: 200));
        WriteParseFixture(
            "holder-none-in-strict-vector",
            UnionSerializationTests.HolderBuffer(strictTypes: [UnionSerializationTests.StrictMarkerType, 0])
        );

        // Writing a parsed fixture gives exactly the expected bytes in C#, which is what the other languages check
        foreach (var entry in manifest)
        {
            var name = (string)entry["name"];
            var input = File.ReadAllBytes(Path.Combine(Directory, name + ".input.bin"));
            var expected = File.ReadAllBytes(Path.Combine(Directory, name + ".expected.bin"));
            var shareStrings = (bool)entry["shareStrings"];
            var rewritten = (string)entry["root"] switch
            {
                var root when root == typeof(Sink).FullName => Rewrite<Sink>(input, shareStrings),
                var root when root == typeof(Graph).FullName => Rewrite<Graph>(input, shareStrings),
                var root when root == typeof(Node).FullName => Rewrite<Node>(input, shareStrings),
                var root when root == typeof(Measure).FullName => Rewrite<Measure>(input, shareStrings),
                var root when root == typeof(LargestHolder).FullName => Rewrite<LargestHolder>(input, shareStrings),
                var root when root == typeof(Evolution.After).FullName => Rewrite<Evolution.After>(input, shareStrings),
                var root => throw new InvalidOperationException($"No root type {root}."),
            };
            rewritten.Should().Equal(expected, $"rewriting fixture {name}");
        }
    }

    /// <summary>
    /// SER-015: the fingerprint the source generator gave these schemas, which <c>build.py</c> checks the CLI gives them
    /// in every target.
    /// </summary>
    [Fact]
    public void WriteFingerprint_GeneratedFingerprint_Is32HexDigits()
    {
        global::BequadroSchema.Fingerprint.Should().MatchRegex("^[0-9a-f]{32}$");

        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path.Combine(Directory, "fingerprint.txt"), global::BequadroSchema.Fingerprint);
    }

    private static void WriteParseFixture(string name, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(Directory, name + ".parse.bin"), bytes);

    private static byte[] Rewrite<T>(byte[] input, bool shareStrings)
        where T : class, Internal.IFlatBufferTable<T> =>
        FlatBufferSerializer<T>.Instance.WriteToArray(
            FlatBufferSerializer<T>.Instance.Parse(input, shareStrings),
            shareStrings
        );

    /// <summary>
    /// NaN payloads differ between languages: JavaScript can only write the canonical NaN (SER-005).
    /// </summary>
    private static Sink FullSinkWithoutNaN()
    {
        var sink = TestData.FullSink();
        sink.Doubles = [1.5, -2.5, double.PositiveInfinity, -0.0];
        sink.Strings = ["a", "", "héllo ✓", "a", "😀 astral"];
        return sink;
    }

    private static Graph SharedGraph()
    {
        var a = new Node { Name = "a" };
        var b = new Node { Name = "b", Neighbors = [a] };
        var c = new Node { Name = "c", Neighbors = [a, b] };
        return new Graph
        {
            Nodes = [a, b, c, a],
            Lines = [new Line { Start = a, End = b }, new Line { Start = b, End = c }],
            Root = c,
        };
    }

    /// <summary>
    /// A Sink with strings that take each of the TypeScript writer's paths: ASCII of up to 12 characters is copied a
    /// character at a time, other strings of up to 65,536 characters are encoded into a reused scratch array, and longer
    /// ones on their own. Lone surrogates are written as U+FFFD.
    /// </summary>
    private static Sink StringsSink()
    {
        var sink = TestData.MinimalSink();
        sink.Name = new string('n', 12);
        sink.Strings =
        [
            "",
            "a",
            "\u00e9",
            "\u65e5\u672c\u8a9e",
            "\U0001F600",
            "\uD800",
            "a\uDC00b",
            "\uFEFFbom",
            "\u007F\u0080",
            "a\0b",
            new string('x', 11),
            new string('x', 12),
            new string('x', 13),
            new string('x', 12) + "\u00e9",
            "\u00e9" + new string('x', 11),
            string.Concat(Enumerable.Repeat("\u00e9", 100)),
            "after a long one",
            new string('x', 65536),
            new string('x', 65537),
            new string('x', 65535) + "\u00e9",
            "a",
            new string('x', 12),
        ];
        return sink;
    }

    private static Node NodeChain(int depth)
    {
        var node = new Node { Name = "leaf" };
        for (var i = 1; i < depth; i++)
        {
            node = new Node { Self = node };
        }

        return node;
    }

    private static Measure FullMeasure()
    {
        var unit = new Unit { Symbol = "m" };
        var source = new Source { Name = "sensor" };
        return new Measure
        {
            Name = "length",
            Amount = 12.5,
            OptionalCount = 0,
            Color = Color.Red,
            Position = TestData.Vec(1, 2, 3),
            Unit = unit,
            Source = source,
            Item = TestData.Vec(4, 5, 6),
            StrictItem = unit,
            Values = [1, 2],
            Names = ["x", "y"],
            Units = [unit, new Unit { Symbol = "s" }],
            Sources = [source],
            Items = [unit, source, default],
        };
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
