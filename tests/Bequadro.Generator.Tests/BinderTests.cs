using Bequadro.Generator.Emit;
using Bequadro.Schema.Diagnostics;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Tests;

public class BinderTests
{
    [Fact]
    public void Bind_QualifiedReference_ResolvesThroughEnclosingNamespaces()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(
            """
            namespace A.Geo;
            struct P { x: double; }
            namespace A.B.C;
            table T { p: Geo.P; q: A.Geo.P; }
            """
        );

        diagnostics.Should().BeEmpty();
        var table = bind.Declarations.OfType<TableModel>().Single();
        table.Fields.Select(f => f.Type.FullName).Should().Equal("A.Geo.P", "A.Geo.P");
    }

    [Fact]
    public void Bind_InnerNamespaceDeclaration_ShadowsOuter()
    {
        var (bind, _) = GeneratorHarness.Bind(
            """
            namespace A;
            table X {}
            namespace A.B;
            table X {}
            table T { x: X; }
            """
        );

        bind.Declarations.OfType<TableModel>().Single(t => t.Name == "T").Fields[0].Type.FullName.Should().Be("A.B.X");
    }

    [Fact]
    public void Bind_IncludeWithParentSegments_ResolvesRelativeToIncludingFile()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("Shared/Geometry.fbs", "namespace G; struct P { x: double; }"),
            ("Model/Sub/Main.fbs", "include \"../../Shared/Geometry.fbs\"; table T { p: G.P; }")
        );

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// SCH-003: <c>\</c> separates segments as <c>/</c> does, written <c>\\</c> because a backslash in a string starts an
    /// escape, and an absolute path is used as given.
    /// </summary>
    [Theory]
    [InlineData("""include "..\\Shared\\Geometry.fbs";""")]
    [InlineData("""include "/schemas/Shared/Geometry.fbs";""")]
    [InlineData("""include "\\schemas\\Shared\\.\\Geometry.fbs";""")]
    public void Bind_IncludeWithBackslashesOrAbsolutePath_Resolves(string include)
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("Shared/Geometry.fbs", "namespace G; struct P { x: double; }"),
            ("Model/Main.fbs", include + " table T { p: G.P; }")
        );

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Bind_IncludeWithDifferentCase_ResolvesUniqueMatch()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("Geometry.fbs", "struct P { x: double; }"),
            ("main.fbs", "include \"geometry.FBS\"; table T { p: P; }")
        );

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Bind_IncludeCycle_IsAllowed()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("a.fbs", "include \"b.fbs\"; table A { b: B; }"),
            ("b.fbs", "include \"a.fbs\"; table B { a: A; }")
        );

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Bind_Struct_ComputesFlatBuffersLayout()
    {
        var (bind, _) = GeneratorHarness.Bind(
            """
            struct Inner { a: short; b: double; }
            struct Outer { flag: bool; inner: Inner; tail: ubyte; }
            """
        );

        var inner = bind.Declarations.OfType<StructModel>().Single(s => s.Name == "Inner");
        inner.Fields.Select(f => f.Offset).Should().Equal(0, 8);
        (inner.Size, inner.Alignment).Should().Be((16, 8));
        var outer = bind.Declarations.OfType<StructModel>().Single(s => s.Name == "Outer");
        outer.Fields.Select(f => f.Offset).Should().Equal(0, 8, 24);
        (outer.Size, outer.Alignment).Should().Be((32, 8));
    }

    [Fact]
    public void Bind_Enum_AssignsImplicitValues()
    {
        var (bind, errors) = GeneratorHarness.Bind("enum E : short { A, B = 10, C, D = -1 }");

        // D = -1 isn't ascending, which is reported, but C# allows it, so it is still generated (DIA-001)
        errors.Should().ContainSingle().Which.Id.Should().Be(DiagnosticIds.InvalidEnum);
        bind.Declarations.OfType<EnumModel>().Single().Members.Select(m => m.Value).Should().Equal(0m, 10m, 11m, -1m);
        var (valid, diagnostics) = GeneratorHarness.Bind("enum E : short { A = -5, B, C = 10, D }");
        diagnostics.Should().BeEmpty();
        valid.Declarations.OfType<EnumModel>().Single().Members.Select(m => m.Value).Should().Equal(-5m, -4m, 10m, 11m);
    }

    [Fact]
    public void Bind_Union_AssignsDiscriminatorsAndLabels()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(
            """
            namespace N;
            table A {} table B {} struct S { x: int; }
            union U { A, Second: B, S = 7 }
            """
        );

        diagnostics.Should().BeEmpty();
        var union = bind.Declarations.OfType<UnionModel>().Single();
        union
            .Members.Select(m => (m.Label, m.Discriminator, m.Type.Kind))
            .Should()
            .Equal(("A", 1, TypeKind.Table), ("Second", 2, TypeKind.Table), ("S", 7, TypeKind.Struct));
    }

    [Fact]
    public void Bind_TableWithUnions_AssignsTwoSlotsPerUnion()
    {
        var (bind, _) = GeneratorHarness.Bind("table A {} union U { A } table T { a: int; u: U; v: [U]; b: int; }");

        var table = bind.Declarations.OfType<TableModel>().Single(t => t.Name == "T");
        table.Fields.Select(f => f.Slot).Should().Equal(0, 2, 4, 5);
        table.SlotCount.Should().Be(6);
    }

    [Theory]
    [InlineData("double", "1", "1d")]
    [InlineData("double", "0.925", "0.925d")]
    [InlineData("double", "-inf", "double.NegativeInfinity")]
    [InlineData("float", "nan", "float.NaN")]
    [InlineData("float", "0.1", "0.1f")]
    [InlineData("float", "1e39", "float.PositiveInfinity")]
    [InlineData("float", "-1e39", "float.NegativeInfinity")]
    [InlineData("float", "1e-50", null)]
    [InlineData("int", "0x10", "16")]
    [InlineData("long", "-9223372036854775808", "long.MinValue")]
    [InlineData("ulong", "18446744073709551615", "18446744073709551615UL")]
    [InlineData("bool", "true", "true")]
    [InlineData("bool", "0", null)]
    [InlineData("int", "0", null)]
    [InlineData("double", "0.0", null)]
    public void Bind_ScalarDefault_ProducesCSharpLiteral(string type, string value, string? expected)
    {
        var (bind, diagnostics) = GeneratorHarness.Bind($"table T {{ x: {type} = {value}; }}");

        diagnostics.Should().BeEmpty();
        TableEmitter.DefaultLiteral(bind.Declarations.OfType<TableModel>().Single().Fields[0]).Should().Be(expected);
    }

    [Fact]
    public void Bind_EnumDefaultByNameOrValue_ProducesMemberReference()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(
            "namespace N; enum E : ubyte { A, B } table T { x: E = B; y: E = 1; z: E = A; }"
        );

        diagnostics.Should().BeEmpty();
        bind.Declarations.OfType<TableModel>()
            .Single()
            .Fields.Select(TableEmitter.DefaultLiteral)
            .Should()
            .Equal("global::N.E.B", "global::N.E.B", null);
    }

    [Fact]
    public void Bind_NullDefault_MakesOptionalScalar()
    {
        var (bind, _) = GeneratorHarness.Bind("enum E : ubyte { A = 1 } table T { x: int = null; e: E = null; }");

        bind.Declarations.OfType<TableModel>()
            .Single()
            .Fields.Should()
            .AllSatisfy(f => f.IsOptionalScalar.Should().BeTrue());
    }

    [Fact]
    public void Bind_FieldNames_ConvertToPascalCase()
    {
        var (bind, _) = GeneratorHarness.Bind(
            "table T { parent_index: int; force_11: int; x: int; fooBar: int; _lead: int; }"
        );

        bind.Declarations.OfType<TableModel>()
            .Single()
            .Fields.Select(f => f.Name)
            .Should()
            .Equal("ParentIndex", "Force11", "X", "FooBar", "Lead");
    }

    /// <summary>DIA-001: no input may overflow the stack. Each struct contains the one declared before it.</summary>
    [Fact]
    public void Bind_DeeplyNestedStructs_LaysThemOutWithoutDeepRecursion()
    {
        const int depth = 30_000;
        var text = new System.Text.StringBuilder("struct S0 { x: ubyte; }");
        for (var i = 1; i <= depth; i++)
        {
            text.Append(FormattableString.Invariant($" struct S{i} {{ s: S{i - 1}; }}"));
        }

        var (bind, diagnostics) = GeneratorHarness.Bind(text.ToString());

        diagnostics.Should().BeEmpty();
        bind.Declarations.OfType<StructModel>()
            .Should()
            .HaveCount(depth + 1)
            .And.AllSatisfy(s => s.Size.Should().Be(1));
    }

    [Fact]
    public void Bind_DeeplyNestedStructsDeclaredInReverse_ReportsWithoutDeepRecursion()
    {
        const int depth = 30_000;
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            text.Append(FormattableString.Invariant($"struct S{i} {{ s: S{i + 1}; }} "));
        }

        text.Append(FormattableString.Invariant($"struct S{depth} {{ x: ubyte; }}"));

        var (_, diagnostics) = GeneratorHarness.Bind(text.ToString());

        diagnostics.Should().HaveCount(depth).And.AllSatisfy(d => d.Id.Should().Be(DiagnosticIds.DeclarationOrder));
    }

    [Fact]
    public void Bind_NamespaceWithVeryManySegments_BindsAndRenamesWithoutDeepRecursion()
    {
        var text =
            "namespace "
            + string.Join(".", Enumerable.Repeat("a", 100_000))
            + "; table X {} table T { x: X; y: [X]; z: a.X; } union U { X } root_type T;";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (bind, diagnostics) = GeneratorHarness.Bind(text);

        diagnostics.Should().BeEmpty();
        bind.Declarations.Select(d => d.Name).Should().Equal("X", "T", "U");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Declarations are dictionary keys by reference: hashing a record would hash its whole syntax tree, which made
    /// binding quadratic in the size of a declaration.
    /// </summary>
    [Fact]
    public void Bind_LargeEnumUsedByManyFields_BindsQuickly()
    {
        const int count = 10_000;
        var values = string.Join(", ", Enumerable.Range(0, count).Select(i => FormattableString.Invariant($"V{i}")));
        var fields = string.Concat(Enumerable.Range(0, count).Select(i => FormattableString.Invariant($" f{i}: E;")));
        var text = $"enum E : short {{ {values} }} table T {{{fields} }}";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (_, diagnostics) = GeneratorHarness.Bind(text);

        diagnostics.Should().BeEmpty();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Bind_EnumValueInError_KeepsDocumentationWithItsValue()
    {
        var (bind, _) = GeneratorHarness.Bind("enum E : byte {\n/// doc A\nA = \"x\",\n/// doc B\nB,\n/// doc C\nC }");

        bind.Declarations.OfType<EnumModel>()
            .Single()
            .Members.Select(m => (m.Name, m.Documentation.Single()))
            .Should()
            .Equal(("B", "doc B"), ("C", "doc C"));
    }

    [Fact]
    public void Bind_FilesWithSameNormalizedPath_ReportsDuplicateFile()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(("d/../a.fbs", "table X {}"), ("a.fbs", "table Y {}"));

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be(DiagnosticIds.DuplicateFile);
        diagnostic.Span.Path.Should().Be(GeneratorHarness.Root + "d/../a.fbs");
        (diagnostic.Span.Start, diagnostic.Span.Length).Should().Be((0, 0));
        bind.Declarations.Select(d => d.Name).Should().Equal("Y");
    }

    /// <summary>NAM-002: a declaration in a renamed namespace says so in its documentation.</summary>
    [Fact]
    public void Bind_DeclarationInRenamedNamespace_RecordsSchemaFullName()
    {
        var (bind, _) = GeneratorHarness.Bind("namespace System.Foo; table T {} namespace Other; table U {}");

        var models = bind.Declarations.ToDictionary(d => d.Name);
        (models["T"].FullName, models["T"].RenamedFrom).Should().Be(("System_.Foo.T", "System.Foo.T"));
        models["U"].RenamedFrom.Should().BeNull();
    }

    [Fact]
    public void Bind_FieldWhoseNameComesBackAfterCaseConversion_RecordsNoRename()
    {
        var (bind, _) = GeneratorHarness.Bind("table T { _1: int; __2: int; }");

        bind.Declarations.OfType<TableModel>()
            .Single()
            .Fields.Select(f => (f.Name, f.RenamedFrom))
            .Should()
            .Equal(("_1", null), ("_2", "__2"));
    }

    /// <summary>NAM-002: a case conversion that would leave no name keeps the name as written.</summary>
    [Fact]
    public void Bind_AllUnderscoreFieldNames_KeepTheirNames()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind("table T { _: int; __: int; } struct S { _: int; }");

        diagnostics.Should().BeEmpty();
        bind.Declarations.OfType<TableModel>()
            .Single()
            .Fields.Select(f => (f.Name, f.RenamedFrom))
            .Should()
            .Equal(("_", null), ("__", null));
        bind.Declarations.OfType<StructModel>().Single().Fields.Single().Name.Should().Be("_");
    }
}
