namespace Bequadro.Generator.Tests;

/// <summary>
/// QUA-001: generated source for one representative schema per declaration kind.
/// </summary>
public class SnapshotTests
{
    internal const string Schema = """
        attribute "category";

        namespace Sample.Geometry;

        /// Documented enum.
        enum Color : ubyte
        {
            Red,
            /// Green <is> & "quoted".
            Green = 3,
        }

        /// A point.
        struct Point (category: "Geometry")
        {
            x: double;
            flag: bool;
            color: Color;
        }

        struct Box
        {
            min: Point;
            max: Point;
            id: int;
        }

        table Label
        {
            text: string;
        }

        union Shape
        {
            Label,
            Boxed: Box,
        }

        /// Strict, with overlapping struct members.
        union Strict (bequadro_exclude_none, bequadro_exhaustive)
        {
            Label,
            Point,
            Boxed: Box = 5,
        }

        /// Every field kind.
        table Drawing
        {
            /// The title.
            title: string (required);
            scale: double = 1.5;
            layer: int = null;
            color: Color = Green;
            origin: Point;
            bounds: Box (required);
            label: Label;
            shape: Shape;
            required_shape: Shape (required);
            weights: [float];
            colors: [Color];
            names: [string];
            points: [Point];
            labels: [Label] (required);
            shapes: [Shape];
            strict: Strict;
            visible: bool = true;
        }

        /// Value equality.
        table Measure (bequadro_value_equality)
        {
            name: string (required);
            amount: double;
            limit: int = null;
            origin: Point;
            label: Label;
            shape: Shape;
            strict: Strict;
            weights: [float];
        }
        """;

    [Theory]
    [InlineData("Sample.Geometry.Color")]
    [InlineData("Sample.Geometry.Point")]
    [InlineData("Sample.Geometry.Box")]
    [InlineData("Sample.Geometry.Label")]
    [InlineData("Sample.Geometry.Shape")]
    [InlineData("Sample.Geometry.Strict")]
    [InlineData("Sample.Geometry.Drawing")]
    [InlineData("Sample.Geometry.Measure")]
    public void Generate_SampleSchema_MatchesSnapshot(string typeName)
    {
        var result = GeneratorHarness.Run(Schema);

        result.GeneratorDiagnostics.Should().BeEmpty();
        Snapshot.Match(typeName, result.Source(typeName + ".g.cs"));
    }

    [Fact]
    public void Generate_RootNamespace_EmitsGlobalTypes()
    {
        var result = GeneratorHarness.Run("table Root { x: int; }");

        Snapshot.Match("Root", result.Source("Root.g.cs"));
    }
}
