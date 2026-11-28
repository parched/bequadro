using Microsoft.CodeAnalysis;

namespace Bequadro.Generator.Tests;

/// <summary>
/// API-001: generated code compiles without warnings, and supports consumer partial declarations.
/// </summary>
public class CompilationTests
{
    private static readonly (string Path, string Text)[] EveryFeatureFiles =
    [
        ("common.fbs", "namespace Lib.Common; struct V { x: float; y: float; } enum K : byte { A = -1, B }"),
        (
            "main.fbs",
            """
            include "common.fbs";
            namespace Lib.Main;
            union U { T, Other, V: Common.V }
            table T
            {
                s: string; rs: string (required); v: Common.V; rv: Common.V (required);
                k: Common.K = B; ok: Common.K = null; child: T; u: U; ru: U (required);
                vb: [bool]; vk: [Common.K]; vs: [string]; vv: [Common.V]; vt: [T]; vu: [U];
                b: byte; ub: ubyte; sh: short; ush: ushort; i: int; ui: uint; l: long; ul: ulong; f: float; d: double;
            }
            table Other {}
            struct W { a: double; b: ubyte; }
            union Strict (bequadro_exclude_none, bequadro_exhaustive) { T, V: Common.V, W }
            table H { s: Strict; rs: Strict (required); vs: [Strict]; }
            table Q (bequadro_value_equality)
            {
                s: string; rs: string (required); v: Common.V; rv: Common.V (required);
                k: Common.K = B; ok: Common.K = null; t: T; self: Q; u: U; ru: U (required); st: Strict;
                vb: [bool]; vk: [Common.K]; vs: [string]; vv: [Common.V]; vt: [T]; vq: [Q]; vu: [U]; vst: [Strict];
                b: byte; d: double; f: float = nan;
            }
            table EmptyQ (bequadro_value_equality) {}
            """
        ),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generate_EveryFeature_CompilesWithoutWarnings(bool requireScalars)
    {
        var result = RunEveryFeature(requireScalars);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// CON-002: older language versions treat [Union] as an ordinary attribute, so unions are used through their members.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generate_EveryFeature_CompilesWithCSharp11(bool requireScalars)
    {
        var result = RunEveryFeature(requireScalars);
        var options = GeneratorHarness.ParseOptions.WithLanguageVersion(
            Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp11
        );
        var compilation = result
            .Output.RemoveAllSyntaxTrees()
            // Parsed again, because some language version checks happen while parsing
            .AddSyntaxTrees(
                result.Output.SyntaxTrees.Select(t =>
                    Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(t.GetText(), options, t.FilePath)
                )
            )
            .AddSyntaxTrees(
                Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    """
                    namespace Lib.Main;
                    public static class Use
                    {
                        public static int Run(U u) =>
                            u.Kind == U.ItemKind.T ? 1 : u.TryGetValue(out Common.V v) ? (int)v.X : 0;
                    }
                    """,
                    options
                )
            );

        result.GeneratorDiagnostics.Should().BeEmpty();
        compilation.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
    }

    [Fact]
    public void Generate_ConsumerPartialsWithConstructors_Compile()
    {
        var result = GeneratorHarness.Run(
            (
                "a.fbs",
                "namespace N; struct P { x: double; } table T { name: string (required); } table A {} union U { A }"
            )
        );
        var compilation = result.Output.AddSyntaxTrees(
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                """
                using System.Diagnostics.CodeAnalysis;
                namespace N;
                public interface IEntity { }
                public partial struct P { public P(double x) { X = x; } public double Doubled => X * 2; }
                public partial class T : IEntity
                {
                    [SetsRequiredMembers]
                    public T(string name) { Name = name; }
                }
                public readonly partial struct U : IEntity { }
                public static class Use
                {
                    public static object Run() => (new T("a"), new T { Name = "b" }, new P(1).Doubled, new U(new A()).Kind);
                }
                """,
                GeneratorHarness.ParseOptions
            )
        );

        compilation.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
    }

    /// <summary>
    /// API-004: a consumer partial may add fields to a struct, which C# otherwise warns about for a partial struct with
    /// fields in more than one declaration (CS0282).
    /// </summary>
    [Fact]
    public void Generate_ConsumerFieldsOnStruct_CompileWithoutWarnings()
    {
        var result = GeneratorHarness.Run(
            "namespace N; struct P { x: int; } struct Q { y: long; } table T {} union U { P, Q, T }"
        );
        var compilation = result.Output.AddSyntaxTrees(
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                "namespace N; public partial struct P { private long _extra; public long Extra { get => _extra; set => _extra = value; } }",
                GeneratorHarness.ParseOptions
            )
        );

        compilation.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
    }

    [Fact]
    public void Generate_KeywordIdentifiers_AreEscaped()
    {
        var result = GeneratorHarness.Run(
            "namespace event.base; enum class : ubyte { int, string } table T { e: class; }"
        );

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        result.Source("event.base.T.g.cs").Should().Contain("namespace @event.@base;");
    }

    [Theory]
    [InlineData("U", "A => 1, B => 2, P => 3, Bequadro.UnionNone => 4, Bequadro.UnionUnknown => 5", null)]
    [InlineData("U", "A => 1, B => 2, P => 3, Bequadro.UnionNone => 4", "Bequadro.UnionUnknown")]
    [InlineData("U", "A => 1, B => 2, P => 3, Bequadro.UnionUnknown => 5", "Bequadro.UnionNone")]
    [InlineData("U", "A => 1, P => 3, Bequadro.UnionNone => 4, Bequadro.UnionUnknown => 5", "N.B")]
    [InlineData("S", "A => 1, B => 2, P => 3", null)]
    [InlineData("S", "A => 1, B => 2", "N.P")]
    [InlineData("X", "A => 1, B => 2, P => 3, Bequadro.UnionUnknown => 5", null)]
    [InlineData("X", "A => 1, B => 2, P => 3", "Bequadro.UnionUnknown")]
    [InlineData("Y", "A => 1, B => 2, P => 3, Bequadro.UnionNone => 4", null)]
    [InlineData("Y", "A => 1, B => 2, P => 3", "Bequadro.UnionNone")]
    public void Generate_UnionSwitch_IsCheckedForExhaustiveness(string union, string arms, string? missing)
    {
        var result = GeneratorHarness.Run(
            "namespace N; table A {} table B {} struct P { x: int; } union U { A, B, P }"
                + " union S (bequadro_exclude_none, bequadro_exhaustive) { A, B, P }"
                + " union X (bequadro_exclude_none) { A, B, P } union Y (bequadro_exhaustive) { A, B, P }"
        );
        var compilation = result.Output.AddSyntaxTrees(
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                $"namespace N; public static class Use {{ public static int Run({union} u) => u switch {{ {arms} }}; }}",
                GeneratorHarness.ParseOptions
            )
        );

        var warnings = compilation.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();

        if (missing is null)
        {
            warnings.Should().BeEmpty();
        }
        else
        {
            warnings
                .Should()
                .ContainSingle()
                .Which.Should()
                .Match<Diagnostic>(d => d.Id == "CS8509" && d.GetMessage().Contains($"'{missing}'"));
        }
    }

    [Fact]
    public void Generate_NamesCloseToReservedOnes_Compile()
    {
        var result = GeneratorHarness.Run(
            "namespace N; table A {} table B {} table C {} struct P { x: int; }"
                + " union U { _value: A, _value1x: B, _kinds: C, P } table T { _x: int; y_1: int; }"
                + " table System {} table Bequadro {} namespace Bequadro.Schemas; table UnionNone {} table Internal {}"
        );

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// API-002, expected behavior: a root type named like a referenced namespace gets the same warning as in hand-written C#.
    /// </summary>
    [Fact]
    public void Generate_RootTypeNamedLikeReferencedNamespace_WarnsLikeHandWrittenCode()
    {
        var result = GeneratorHarness.Run("table Microsoft {}");
        var handWritten = GeneratorHarness.CreateCompilation(
            "public class Microsoft { } public static class Use { public static global::Microsoft Make() => new(); }"
        );

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().NotBeEmpty().And.OnlyContain(d => d.Id == "CS0437");
        handWritten.GetDiagnostics().Should().Contain(d => d.Id == "CS0437");
    }

    /// <summary>
    /// SER-004: a NaN default is decided by the field's type and constant, not by how the default is spelled, so an
    /// enum member named <c>NaN</c> compares as an enum.
    /// </summary>
    [Theory]
    [InlineData("enum E : byte { Zero, NaN } table T { e: E = NaN; }", "if (value.E != global::N.E.NaN)")]
    [InlineData("enum E : byte { Zero, NaN } table T { e: E = 1; }", "if (value.E != global::N.E.NaN)")]
    [InlineData("table T { f: float = nan; }", "if (!float.IsNaN(value.F))")]
    [InlineData("table T { d: double = -nan; }", "if (!double.IsNaN(value.D))")]
    [InlineData("table T { d: double = inf; }", "if (value.D != double.PositiveInfinity)")]
    public void Generate_NaNDefaults_CompareByType(string schema, string condition)
    {
        var result = GeneratorHarness.Run("namespace N; " + schema);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        result.Source("N.T.g.cs").Should().Contain(condition);
    }

    /// <summary>
    /// API-001: doc comments with characters that end a C# line, or that XML can't contain, still compile without
    /// warnings with documentation checked. A line terminator starts a new documentation line, and characters XML
    /// can't contain become U+FFFD. A carriage return ends the comment, as in flatc (SCH-001), so the lines after one
    /// need their own <c>///</c>.
    /// </summary>
    [Theory]
    [InlineData("a\u2028b", "a\nb")]
    [InlineData("a\u2029b", "a\nb")]
    [InlineData("a\u0085b", "a\nb")]
    [InlineData("a\r/// b", "a\nb")]
    [InlineData("a \r///  b", "a\nb")]
    [InlineData("a\u0001b", "a\uFFFDb")]
    [InlineData("a\u000Bb\u001Fc", "a\uFFFDb\uFFFDc")]
    [InlineData("a\u0000b", "a\uFFFDb")]
    [InlineData("a\uFFFEb\uFFFFc", "a\uFFFDb\uFFFDc")]
    [InlineData("a\uD83D\uDE00b\tc", "a\uD83D\uDE00b\tc")]
    [InlineData("<a> & \"b\" 'c'", "<a> & \"b\" 'c'")]
    public void Generate_DocCommentWithSpecialCharacters_CompilesWithoutWarnings(string doc, string expected) =>
        AssertDocComment(doc, expected);

    /// <summary>
    /// Unpaired surrogates, which theory data can't carry intact.
    /// </summary>
    [Fact]
    public void Generate_DocCommentWithUnpairedSurrogates_CompilesWithoutWarnings() =>
        AssertDocComment("a\uD800b\uDC00c", "a�b�c");

    private static void AssertDocComment(string doc, string expected)
    {
        var result = GeneratorHarness.Run($"namespace N;\n/// {doc}\ntable T {{\n/// {doc}\nx: int; }}");

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        var type = result.Output.GetTypeByMetadataName("N.T")!;
        foreach (var symbol in new ISymbol[] { type, type.GetMembers("X").Single() })
        {
            var summary = System
                .Xml.Linq.XElement.Parse(symbol.GetDocumentationCommentXml()!)
                .Element("summary")!
                .Value.Trim();
            summary.Split('\n').Select(line => line.Trim()).Should().Equal(expected.Split('\n'));
        }
    }

    /// <summary>
    /// Error messages name unions and labels as the schema does, like those of tables, even when C# renames them.
    /// </summary>
    [Fact]
    public void Generate_RenamedUnion_MessagesUseSchemaNames()
    {
        var result = GeneratorHarness.Run(
            "namespace N; table A {} union Kind (bequadro_exclude_none, bequadro_exhaustive) { Value: A }"
        );

        var source = result.Source("N.Kind_.g.cs");
        source.Should().Contain("\"The union 'N.Kind' does not hold 'Value'.\"");
        source.Should().Contain("\"Cannot serialize the union 'N.Kind' because it has no value.\"");
        source.Should().Contain("UnknownUnionType(\"N.Kind\", type)");
    }

    [Fact]
    public void Generate_RequiredMemberNotSet_IsCompileError()
    {
        var result = GeneratorHarness.Run("namespace N; table T { name: string (required); }");
        var compilation = result.Output.AddSyntaxTrees(
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                "public static class Use { public static object Run() => new N.T(); }",
                GeneratorHarness.ParseOptions
            )
        );

        compilation.GetDiagnostics().Should().Contain(d => d.Id == "CS9035");
    }

    private static GeneratorResult RunEveryFeature(bool requireScalars) =>
        GeneratorHarness.Run(
            requireScalars ? GeneratorHarness.RequireScalars : new Dictionary<string, string>(),
            EveryFeatureFiles
        );
}
