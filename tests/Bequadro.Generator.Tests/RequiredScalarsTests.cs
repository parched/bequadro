using Bequadro.Schema;
using Bequadro.Schema.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bequadro.Generator.Tests;

/// <summary>
/// API-005 and CON-006: with <c>BequadroRequireScalarsWithoutDefault</c>, scalar and enum table fields without a default
/// in the schema are <c>required</c> C# properties. Nothing else changes, the wire format included.
/// </summary>
public class RequiredScalarsTests
{
    private const string Schema = """
        namespace N;
        enum Color : ubyte { Red, Green }
        enum Odd : ubyte { One = 1, Two }
        struct P { x: double; }
        table T
        {
            count: int;
            scale: double = 1.5;
            zero: int = 0;
            layer: int = null;
            visible: bool;
            color: Color;
            tint: Color = Green;
            odd: Odd = One;
            maybe_color: Color = null;
            origin: P;
            name: string;
            title: string (required);
            colors: [Color];
        }
        """;

    [Fact]
    public void Bind_PropertySet_RequiresOnlyScalarsAndEnumsWithoutDefault()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(
            new SchemaOptions(RequireScalarsWithoutDefault: true),
            ("schema.fbs", Schema)
        );

        diagnostics.Should().BeEmpty();
        var fields = bind.Declarations.OfType<TableModel>().Single().Fields;
        fields
            .Where(f => f.IsRequiredMember)
            .Select(f => f.SchemaName)
            .Should()
            .Equal("count", "visible", "color", "title");
        fields
            .Where(f => f.IsRequired)
            .Select(f => f.SchemaName)
            .Should()
            .Equal(new[] { "title" }, "required scalars stay optional on the wire");
    }

    [Fact]
    public void Bind_PropertyNotSet_RequiresOnlySchemaRequiredFields()
    {
        var (bind, _) = GeneratorHarness.Bind(("schema.fbs", Schema));

        var fields = bind.Declarations.OfType<TableModel>().Single().Fields;
        fields.Where(f => f.IsRequiredMember).Select(f => f.SchemaName).Should().Equal("title");
    }

    [Fact]
    public void Generate_PropertySet_MatchesSnapshot()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.RequireScalars, Schema);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        Snapshot.Match("RequiredScalars.N.T", result.Source("N.T.g.cs"));
    }

    /// <summary>
    /// The generated code differs only in the <c>required</c> modifiers, so writing and reading are unchanged.
    /// </summary>
    [Fact]
    public void Generate_PropertySet_ChangesOnlyRequiredModifiers()
    {
        var with = GeneratorHarness.Run(GeneratorHarness.RequireScalars, SnapshotTests.Schema);
        var without = GeneratorHarness.Run(SnapshotTests.Schema);

        with.HintNames.Should().Equal(without.HintNames);
        foreach (var hintName in with.HintNames)
        {
            var withSource = with.Source(hintName);
            var withoutSource = without.Source(hintName);
            withSource
                .Replace("public required ", "public ")
                .Should()
                .Be(withoutSource.Replace("public required ", "public "));
        }

        RequiredLines(with.Source("Sample.Geometry.Drawing.g.cs"))
            .Except(RequiredLines(without.Source("Sample.Geometry.Drawing.g.cs")))
            .Should()
            .BeEmpty("every Drawing scalar and enum field has a default");
        RequiredLines(with.Source("Sample.Geometry.Measure.g.cs"))
            .Except(RequiredLines(without.Source("Sample.Geometry.Measure.g.cs")))
            .Should()
            .Equal("public required double Amount { get; set; }");
        RequiredLines(with.Source("Sample.Geometry.Label.g.cs")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData(null, false)]
    public void Generate_PropertyValue_IsOnlyTrueWhenTrue(string? value, bool required)
    {
        var properties = value is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [GeneratorOptions.RequireScalarsWithoutDefaultProperty] = value };

        var result = GeneratorHarness.Run(properties, "namespace N; table T { count: int; }");

        result
            .Source("N.T.g.cs")
            .Should()
            .Contain(required ? "public required int Count { get; set; }" : "public int Count { get; set; }");
    }

    [Fact]
    public void Generate_PropertyNameInAnyCase_IsRead()
    {
        var properties = new Dictionary<string, string> { ["bequadrorequirescalarswithoutdefault"] = "true" };

        var result = GeneratorHarness.Run(properties, "namespace N; table T { count: int; }");

        result.Source("N.T.g.cs").Should().Contain("public required int Count { get; set; }");
    }

    [Fact]
    public void Generate_RequiredScalarNotSet_IsCompileError()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.RequireScalars, Schema);
        var compilation = result.Output.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(
                "public static class Use { public static object Run() => new N.T { Title = \"t\", Count = 1, Visible = true }; }",
                GeneratorHarness.ParseOptions,
                path: "Use.cs"
            )
        );

        var error = compilation
            .GetDiagnostics()
            .Should()
            .ContainSingle(d => d.Severity == DiagnosticSeverity.Error)
            .Which;
        error.Id.Should().Be("CS9035");
        error.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Contain("'T.Color'");
        error.Location.SourceTree!.FilePath.Should().Be("Use.cs");
    }

    [Fact]
    public void Generate_EveryRequiredScalarSet_Compiles()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.RequireScalars, Schema);
        var compilation = result.Output.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(
                """
                using System.Diagnostics.CodeAnalysis;
                namespace N;
                public partial class T
                {
                    [SetsRequiredMembers]
                    public T(string title) { Title = title; }
                }
                public static class Use
                {
                    public static object Run() =>
                        (new T { Title = "t", Count = 1, Visible = true, Color = Color.Green }, new T("t"));
                }
                """,
                GeneratorHarness.ParseOptions
            )
        );

        compilation.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
    }

    private static IEnumerable<string> RequiredLines(string source) =>
        source
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("public required ", StringComparison.Ordinal));
}
