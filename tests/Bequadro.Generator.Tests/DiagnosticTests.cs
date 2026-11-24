using Bequadro.Generator.Emit;
using Bequadro.Schema.Diagnostics;

namespace Bequadro.Generator.Tests;

/// <summary>
/// DIA-002: every diagnostic in the catalog, with its location.
/// The marker «...» in a schema shows where the diagnostic must be reported and is removed before parsing.
/// </summary>
public class DiagnosticTests
{
    [Theory]
    [InlineData(DiagnosticIds.SyntaxError, "table T { x «int»; }")]
    [InlineData(DiagnosticIds.UnterminatedLiteral, "attribute «\"abc»")]
    [InlineData(DiagnosticIds.InvalidNumericLiteral, "table T { x: int = «12abc»; }")]
    [InlineData(DiagnosticIds.IncludeAfterDeclarations, "table T {} «include» \"x.fbs\";")]
    [InlineData(DiagnosticIds.IncludeNotFound, "«include \"missing.fbs\"»;")]
    [InlineData(DiagnosticIds.DuplicateDeclaration, "table T {} struct «T» { x: int; }")]
    [InlineData(DiagnosticIds.UnknownType, "table T { x: «Nope»; }")]
    [InlineData(DiagnosticIds.UndeclaredAttribute, "table T («custom») {}")]
    [InlineData(DiagnosticIds.UndeclaredAttribute, "table T { x: int («custom: 1»); }")]
    [InlineData(DiagnosticIds.DuplicateMember, "table T { x: int; «x»: int; }")]
    [InlineData(DiagnosticIds.DuplicateMember, "enum E : ubyte { A, «A» }")]
    [InlineData(DiagnosticIds.DuplicateMember, "table A {} union U { A, «A» }")]
    [InlineData(DiagnosticIds.DuplicateMember, "table A {} union U { «NONE: A» }")]
    [InlineData(DiagnosticIds.DuplicateMember, "struct S { x: int; «x»: int; }")]
    [InlineData(DiagnosticIds.InvalidDefault, "table T { x: ubyte = «256»; }")]
    [InlineData(DiagnosticIds.InvalidDefault, "table T { x: int = «1.5»; }")]
    [InlineData(DiagnosticIds.InvalidDefault, "table T { x: bool = «2»; }")]
    [InlineData(DiagnosticIds.InvalidDefault, "table T { x: double = «\"a\"»; }")]
    [InlineData(DiagnosticIds.InvalidDefault, "enum E : ubyte { A } table T { x: E = «B»; }")]
    [InlineData(DiagnosticIds.InvalidDefault, "enum E : ubyte { A } table T { x: E = «3»; }")]
    [InlineData(DiagnosticIds.InvalidRequired, "table T { «x»: int (required); }")]
    [InlineData(DiagnosticIds.InvalidRequired, "struct S { x: int («required»); }")]
    [InlineData(DiagnosticIds.InvalidStructFieldType, "struct S { x: «string»; }")]
    [InlineData(DiagnosticIds.InvalidStructFieldType, "struct S { x: «[int]»; }")]
    [InlineData(DiagnosticIds.InvalidStructFieldType, "table T {} struct S { x: «T»; }")]
    [InlineData(DiagnosticIds.EmptyStruct, "struct «S» {}")]
    [InlineData(DiagnosticIds.RecursiveStruct, "struct «S» { x: S; }")]
    [InlineData(DiagnosticIds.InvalidEnum, "enum «E» { A }")]
    [InlineData(DiagnosticIds.InvalidEnum, "enum E : «double» { A }")]
    [InlineData(DiagnosticIds.InvalidEnum, "enum E : ubyte { A = 2, «B» = 1 }")]
    [InlineData(DiagnosticIds.InvalidEnum, "enum E : ubyte { «A» = 256 }")]
    [InlineData(DiagnosticIds.InvalidEnum, "enum E : byte { «A» = -129 }")]
    [InlineData(DiagnosticIds.InvalidUnion, "enum E : ubyte { A } union U { «E» }")]
    [InlineData(DiagnosticIds.InvalidUnion, "table A {} table B {} union U { A = 2, B = «1» }")]
    [InlineData(DiagnosticIds.InvalidUnion, "table A {} table B {} union U { A = 255, «B» }")]
    [InlineData(DiagnosticIds.InvalidUnion, "union «U» {}")]
    [InlineData(DiagnosticIds.InvalidUnion, "table A {} union U { A = «0» }")]
    [InlineData(DiagnosticIds.InvalidUnion, "table A {} union U { A = «256» }")]
    [InlineData(DiagnosticIds.EnumWithoutZero, "enum E : ubyte { A = 1 } table T { «x»: E; }")]
    [InlineData(DiagnosticIds.NestedVector, "table T { x: [«[int]»]; }")]
    [InlineData(DiagnosticIds.SyntaxError, "table T { x: [[«[»int]]]; }")]
    [InlineData(DiagnosticIds.DefaultNotAllowed, "table T { x: string = «a»; }")]
    [InlineData(DiagnosticIds.DefaultNotAllowed, "struct S { x: int = «1»; }")]
    [InlineData(DiagnosticIds.InvalidRootType, "struct S { x: int; } root_type «S»;")]
    [InlineData(DiagnosticIds.DeclarationOrder, "table T { e: «E»; } enum E : byte { A }")]
    [InlineData(DiagnosticIds.FieldNameClash, "table A {} union U { A } table T { u: U; «u_type»: ubyte; }")]
    [InlineData(DiagnosticIds.FieldNameClash, "table T { «T»: int; }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "«file_identifier» \"ABCD\";")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "«rpc_service» S {}")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "table T { x: int («id: 0»); }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "table T { x: int («deprecated»); }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "enum E : ubyte («bit_flags») { A }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "table T { x: int («key»); }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "struct S («force_align: 8») { x: int; }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "table T { x: [ubyte] («nested_flatbuffer: \"T\"»); }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "table T { x: [ubyte] («flexbuffer»); }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "table T { x: int («hash: \"fnv1_32\"»); }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "table T («original_order») {}")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "table T { x: [int] («offset64»); }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "table T { x: [int] («vector64»); }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "struct S { x: «[int:2]»; }")]
    [InlineData(DiagnosticIds.UnsupportedFeature, "union U { «string» }")]
    [InlineData(DiagnosticIds.AmbiguousUnionMembers, "table A {} union U { X: A, «Y: A» }")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "table A {} union U («bequadro_exhaustiv») { A }")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "table A («bequadro_exhaustive») {}")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "table A { x: int («bequadro_exclude_none»); }")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "table A {} union U { A («bequadro_exhaustive») }")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "table A {} union U («bequadro_exclude_none: 1») { A }")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "struct S («bequadro_value_equality») { x: int; }")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "table A {} union U («bequadro_value_equality») { A }")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "enum E : ubyte («bequadro_value_equality») { A }")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "table T { x: int («bequadro_value_equality»); }")]
    [InlineData(DiagnosticIds.InvalidBequadroAttribute, "table T («bequadro_value_equality: \"yes\"») {}")]
    public void Bind_InvalidSchema_ReportsDiagnosticAtMarker(string id, string markedSchema)
    {
        var start = markedSchema.IndexOf('«', StringComparison.Ordinal);
        var end = markedSchema.IndexOf('»', StringComparison.Ordinal) - 1;
        var schema = markedSchema.Replace("«", "").Replace("»", "");

        var (_, diagnostics) = GeneratorHarness.Bind(schema);

        diagnostics
            .Should()
            .Contain(d => d.Id == id, $"expected {id} for: {schema}")
            .Which.Span.Should()
            .Match<Bequadro.Schema.Common.SourceSpan>(
                s => s.Start == start && s.Length == end - start,
                "the location must cover the marked text"
            );
    }

    /// <summary>
    /// S0 is 16 bytes and each S{n} doubles it, so the fields of the last struct add up to 16 * (2^12 - 1) = 65,520 bytes,
    /// the maximum.
    /// </summary>
    private static string LargestStruct(string name, string extraField) =>
        "struct S0 { a: double; b: double; }"
        + string.Concat(
            Enumerable
                .Range(1, 11)
                .Select(i => FormattableString.Invariant($" struct S{i} {{ a: S{i - 1}; b: S{i - 1}; }}"))
        )
        + $" struct {name} {{ a: S11; b: S10; c: S9; d: S8; e: S7; f: S6; g: S5; h: S4; i: S3; j: S2; k: S1; l: S0;{extraField} }}";

    [Fact]
    public void Bind_StructOfMaximumSize_IsAccepted()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(LargestStruct("Largest", ""));

        diagnostics.Should().BeEmpty();
        bind.Declarations.Should().Contain(d => d.Name == "Largest");
    }

    [Fact]
    public void Bind_StructOverMaximumSize_ReportsStructTooLarge()
    {
        Bind_InvalidSchema_ReportsDiagnosticAtMarker(
            DiagnosticIds.StructTooLarge,
            LargestStruct("«Largest»", " m: ubyte;")
        );
    }

    [Fact]
    public void Bind_BitFlagsOnEnumValue_ReportsOnce()
    {
        var (_, diagnostics) = GeneratorHarness.Bind("enum E : ubyte { A (bit_flags) }");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be(DiagnosticIds.UnsupportedFeature);
    }

    [Fact]
    public void Bind_TypeFromFileNotIncluded_ReportsTypeNotIncluded()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("a.fbs", "namespace N; table A {}"),
            ("b.fbs", "namespace N; table B { a: A; }")
        );

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be(DiagnosticIds.TypeNotIncluded);
        AssertSpan(diagnostic, "b.fbs", "namespace N; table B { a: ".Length, "A".Length);
        diagnostic.Message.Should().Contain("N.A").And.Contain("a.fbs");
    }

    [Fact]
    public void Bind_InnerTypeNotIncludedButOuterTypeVisible_ReportsTypeNotIncluded()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("a.fbs", "namespace N; table A {}"),
            ("b.fbs", "table A {} namespace N; table B { a: A; }")
        );

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be(DiagnosticIds.TypeNotIncluded);
        AssertSpan(diagnostic, "b.fbs", "table A {} namespace N; table B { a: ".Length, "A".Length);
        diagnostic.Message.Should().Contain("N.A");
    }

    [Fact]
    public void Bind_DeclarationsWithSameNameInTwoFiles_ReportsDuplicateDeclarationInSecond()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("a.fbs", "namespace N; table T {}"),
            ("b.fbs", "include \"a.fbs\"; namespace N; struct T { x: int; }")
        );

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be(DiagnosticIds.DuplicateDeclaration);
        AssertSpan(diagnostic, "b.fbs", "include \"a.fbs\"; namespace N; struct ".Length, "T".Length);
        diagnostic.Message.Should().Contain("N.T").And.Contain(GeneratorHarness.Root + "a.fbs(1,20)");
    }

    /// <summary>SCH-003: a case-insensitive match is used only if it is the only one.</summary>
    [Fact]
    public void Bind_IncludeMatchingSeveralFilesIgnoringCase_ReportsIncludeNotFound()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("Shapes.fbs", "table A {}"),
            ("shapes.fbs", "table B {}"),
            ("main.fbs", "include \"SHAPES.fbs\"; table T {}")
        );

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be(DiagnosticIds.IncludeNotFound);
        AssertSpan(diagnostic, "main.fbs", 0, "include \"SHAPES.fbs\"".Length);
    }

    [Fact]
    public void Bind_AttributeDeclaredInIncludedFile_IsAccepted()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("attrs.fbs", "attribute \"custom\";"),
            ("mid.fbs", "include \"attrs.fbs\";"),
            ("main.fbs", "include \"mid.fbs\"; table T (custom) {}")
        );

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Bind_AttributeDeclaredInUnrelatedFile_ReportsUndeclared()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("attrs.fbs", "attribute \"custom\";"),
            ("main.fbs", "table T (custom) {}")
        );

        diagnostics.Should().ContainSingle(d => d.Id == DiagnosticIds.UndeclaredAttribute);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("native_inline")]
    [InlineData("csharp_partial")]
    public void Bind_IgnoredBuiltInAttribute_NeedsNoDeclaration(string attribute)
    {
        var (_, diagnostics) = GeneratorHarness.Bind($"table T ({attribute}) {{ s: string ({attribute}); }}");

        diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(
        "attribute \"bequadro_exclude_none\"; attribute \"bequadro_exhaustive\"; attribute \"bequadro_value_equality\";"
    )]
    public void Bind_BequadroAttributes_AreAcceptedWithOrWithoutDeclaration(string declarations)
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(
            $"{declarations} table A {{}} table V (bequadro_value_equality) {{}}"
                + " union U (bequadro_exclude_none, bequadro_exhaustive) { A } union D { A }"
        );

        diagnostics.Should().BeEmpty();
        var unions = bind.Declarations.OfType<Bequadro.Schema.Semantics.UnionModel>().ToDictionary(u => u.Name);
        (unions["U"].AllowsNone, unions["U"].AllowsUnknown).Should().Be((false, false));
        (unions["D"].AllowsNone, unions["D"].AllowsUnknown).Should().Be((true, true));
        var tables = bind.Declarations.OfType<Bequadro.Schema.Semantics.TableModel>().ToDictionary(t => t.Name);
        tables["V"].HasValueEquality.Should().BeTrue();
        tables["A"].HasValueEquality.Should().BeFalse();
    }

    /// <summary>
    /// <c>Equals</c> and <c>GetHashCode</c> are only generated members, and so reserved as names, for a table with value
    /// equality.
    /// </summary>
    [Fact]
    public void Generate_TablesNamedLikeEqualityMembersWithoutValueEquality_Compile()
    {
        var result = GeneratorHarness.Run("table Equals {} table GetHashCode {}");

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Generator_SchemaWithErrors_ReportsRoslynDiagnosticsAndGeneratesTheRest()
    {
        var result = GeneratorHarness.Run(("good.fbs", "table Good {}"), ("bad.fbs", "table Bad { x: Missing; }"));

        result.HintNames.Should().BeEquivalentTo("Good.g.cs", "Bad.g.cs", FingerprintEmitter.HintName);
        result.CompilationDiagnostics.Should().BeEmpty();
        var diagnostic = result.GeneratorDiagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be(DiagnosticIds.UnknownType);
        diagnostic.Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var span = diagnostic.Location.GetLineSpan();
        span.Path.Should().Be(GeneratorHarness.Root + "bad.fbs");
        span.StartLinePosition.Character.Should().Be(15);
    }

    [Fact]
    public void DiagnosticCatalog_EveryId_IsDocumented()
    {
        var requirements = File.ReadAllText(Path.Combine(RepositoryRoot(), "requirements", "diagnostics.md"));

        DiagnosticCatalog.All.Should().AllSatisfy(d => requirements.Should().Contain($"| {d.Id} | {d.Title} |"));
    }

    /// <summary>DIA-002 in the other direction: every ID the table documents is in the catalog, unless it is removed.</summary>
    [Fact]
    public void DiagnosticCatalog_EveryDocumentedId_IsInCatalog()
    {
        var rows = File.ReadAllLines(Path.Combine(RepositoryRoot(), "requirements", "diagnostics.md"))
            .Where(line => line.StartsWith("| BQ", StringComparison.Ordinal))
            .Select(line => line.Split('|').Select(cell => cell.Trim()).ToArray())
            .ToList();
        var catalog = DiagnosticCatalog.All.ToDictionary(d => d.Id, d => d.Title);

        rows.Should().NotBeEmpty();
        foreach (var row in rows.Where(row => !row[3].StartsWith("Removed", StringComparison.Ordinal)))
        {
            catalog.Should().ContainKey(row[1]).WhoseValue.Should().Be(row[2]);
        }

        rows.Where(row => row[3].StartsWith("Removed", StringComparison.Ordinal))
            .Should()
            .AllSatisfy(row => catalog.Should().NotContainKey(row[1]));
    }

    private static void AssertSpan(SchemaDiagnostic diagnostic, string file, int start, int length) =>
        (diagnostic.Span.Path, diagnostic.Span.Start, diagnostic.Span.Length)
            .Should()
            .Be((GeneratorHarness.Root + file, start, length));

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
