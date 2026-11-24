using Bequadro.Generator.Emit;
using Bequadro.Schema.Diagnostics;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Tests;

/// <summary>
/// The guiding rule of schema-language.md: a schema Bequadro accepts is accepted by flatc and means the same thing.
/// What flatc does is taken from its parser, idl_parser.cpp in flatbuffers v25.12.19; each test names the function.
/// </summary>
public class FlatcAgreementTests
{
    /// <summary>ParseTypeIdent looks for an enum in every enclosing namespace before it looks for a table.</summary>
    [Fact]
    public void Bind_OuterEnumAndInnerTableWithSameName_ResolvesToEnum()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(
            "namespace a; enum X : byte { A } namespace a.b; table X {} table T { x: X; }"
        );

        diagnostics.Should().BeEmpty();
        var field = bind.Declarations.OfType<TableModel>().Single(t => t.Name == "T").Fields.Single();
        (field.Type.Kind, field.Type.FullName).Should().Be((TypeKind.Enum, "a.X"));
    }

    [Fact]
    public void Bind_UnionMemberWithoutLabel_IgnoresEnumWithSameName()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(
            "namespace a; enum X : byte { A } namespace a.b; table X {} union U { X }"
        );

        diagnostics.Should().BeEmpty();
        bind.Declarations.OfType<UnionModel>().Single().Members.Single().Type.FullName.Should().Be("a.b.X");
    }

    /// <summary>
    /// ParseTypeIdent only finds enums already read; a later one leaves a forward reference to a table that is never
    /// declared ("type referenced but not defined").
    /// </summary>
    [Theory]
    [InlineData("table T { e: «E»; } enum E : byte { A }")]
    [InlineData("table T { e: [«E»]; } enum E : byte { A }")]
    [InlineData("table A {} table T { u: «U»; } union U { A }")]
    [InlineData("struct S { e: «E»; } enum E : byte { A }")]
    [InlineData("namespace N; table T { e: «M.E»; } namespace N.M; enum E : byte { A }")]
    public void Bind_EnumOrUnionUsedBeforeDeclaration_ReportsDeclarationOrder(string schema)
    {
        AssertDiagnostic(DiagnosticIds.DeclarationOrder, schema);
    }

    /// <summary>ParseField rejects an incomplete (forward-referenced) struct inside a struct.</summary>
    [Fact]
    public void Bind_StructUsedInStructBeforeDeclaration_ReportsDeclarationOrder()
    {
        AssertDiagnostic(DiagnosticIds.DeclarationOrder, "struct A { b: «B»; } struct B { x: int; }");
    }

    [Theory]
    [InlineData("table A { b: B; } table B { a: A; }")]
    [InlineData("table T { next: T; }")]
    [InlineData("namespace a; table T { u: b.U; } namespace b; table U {}")]
    [InlineData("table A {} union U { A, B, S } table B {} struct S { x: int; }")]
    [InlineData("table T { s: S; } struct S { x: int; }")]
    [InlineData("namespace a; table T { x: X; } namespace c; table Z {} namespace a; table X {}")]
    [InlineData("table T { x: X; } root_type T; table X {}")]
    public void Bind_TableOrStructUsedBeforeDeclaration_IsAccepted(string schema)
    {
        GeneratorHarness.Bind(schema).Diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// LookupCreateStruct only knows tables already read. When a nearer candidate comes later, it takes the one it knows.
    /// </summary>
    [Fact]
    public void Bind_LaterInnerTableAndEarlierOuterTable_ReportsDeclarationOrder()
    {
        var (_, diagnostics) = AssertDiagnostic(
            DiagnosticIds.DeclarationOrder,
            "table X {} namespace a; table T { x: «X»; } table X {}"
        );

        diagnostics.Single().Message.Should().Contain("'X'");
    }

    /// <summary>
    /// A forward reference written <c>N</c> is taken over by the next table or struct named <c>N</c>, in any namespace
    /// (StartStruct).
    /// </summary>
    [Fact]
    public void Bind_ForwardReferenceTakenOverByTableInOtherNamespace_ReportsDeclarationOrder()
    {
        var (_, diagnostics) = AssertDiagnostic(
            DiagnosticIds.DeclarationOrder,
            "namespace a; table T { x: «N»; } namespace c; table N {} namespace a; table N {}"
        );

        diagnostics.Single().Message.Should().Contain("'c.N'");
    }

    /// <summary>A forward reference written <c>b.N</c> is only taken over by a table whose full name is <c>b.N</c>.</summary>
    [Fact]
    public void Bind_PartlyQualifiedForwardReference_ReportsDeclarationOrder()
    {
        AssertDiagnostic(
            DiagnosticIds.DeclarationOrder,
            "namespace a; table T { x: «b.N»; } namespace a.b; table N {}"
        );
    }

    /// <summary>
    /// A pending forward reference is returned for any later use written the same way, whatever its namespace.
    /// </summary>
    [Fact]
    public void Bind_PendingForwardReferenceWrittenTheSameWay_ReportsDeclarationOrder()
    {
        AssertDiagnostic(
            DiagnosticIds.DeclarationOrder,
            "namespace c; table U { x: N; } namespace a.b; table T { y: «N»; } namespace c; table N {} namespace a; table N {}"
        );
    }

    /// <summary>
    /// StartStruct takes over the forward reference written <c>N</c> and moves it under <c>b.N</c>, replacing the one
    /// written <c>b.N</c>, which is then never declared.
    /// </summary>
    [Fact]
    public void Bind_ForwardReferenceReplacedByAnother_ReportsDeclarationOrder()
    {
        AssertDiagnostic(
            DiagnosticIds.DeclarationOrder,
            "namespace b; table U { y: N; } namespace a; table T { x: «b.N»; } namespace b; table N {}"
        );
    }

    [Fact]
    public void Bind_EnumFromFileInIncludeCycle_ReportsDeclarationOrder()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("a.fbs", "include \"b.fbs\"; enum E : byte { A } table A {}"),
            ("b.fbs", "include \"a.fbs\"; table B { e: E; a: A; }")
        );

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be(DiagnosticIds.DeclarationOrder);
        diagnostic.Span.Path.Should().Be(GeneratorHarness.Root + "b.fbs");
        diagnostic.Message.Should().Contain("include cycle");
    }

    [Fact]
    public void Bind_TableFromIncludedFileOutsideCycle_IsAccepted()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("base.fbs", "enum E : byte { A } struct S { x: int; } attribute \"custom\";"),
            ("a.fbs", "include \"base.fbs\"; include \"b.fbs\"; table A { e: E; b: B; }"),
            ("b.fbs", "include \"a.fbs\"; include \"base.fbs\"; struct T { s: S; } table B (custom) { a: A; e: E; }")
        );

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Bind_TableWithSharedNameFromFileInIncludeCycle_ReportsDeclarationOrder()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("a.fbs", "include \"b.fbs\"; namespace N; table A {}"),
            ("b.fbs", "include \"a.fbs\"; namespace N; table B { a: A; } namespace M; table A {}")
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be(DiagnosticIds.DeclarationOrder);
    }

    /// <summary>ParseMetaData: "user define attributes must be declared before use".</summary>
    [Theory]
    [InlineData("table T («foo») {} attribute \"foo\";")]
    [InlineData("table T { a: int («bar»); } attribute bar;")]
    public void Bind_AttributeDeclaredAfterUse_ReportsUndeclaredAttribute(string schema)
    {
        AssertDiagnostic(DiagnosticIds.UndeclaredAttribute, schema);
    }

    [Fact]
    public void Bind_AttributeFromFileInIncludeCycle_ReportsUndeclaredAttribute()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("a.fbs", "include \"b.fbs\"; attribute \"custom\";"),
            ("b.fbs", "include \"a.fbs\"; table T (custom) {}")
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be(DiagnosticIds.UndeclaredAttribute);
    }

    /// <summary>SetRootType looks the name up as a full name, then in the current namespace, among what it has read.</summary>
    [Theory]
    [InlineData("namespace a; table T {} namespace a.b; root_type «T»;")]
    [InlineData("root_type «T»; table T {}")]
    [InlineData("root_type «string»;")]
    [InlineData("root_type «int»;")]
    [InlineData("enum E : byte { A } root_type «E»;")]
    [InlineData("root_type «Missing»;")]
    public void Bind_InvalidRootType_ReportsInvalidRootType(string schema)
    {
        AssertDiagnostic(DiagnosticIds.InvalidRootType, schema);
    }

    [Theory]
    [InlineData("namespace a; table T {} root_type T;")]
    [InlineData("namespace a; table T {} namespace b; root_type a.T;")]
    public void Bind_RootTypeByFullOrRelativeName_IsAccepted(string schema)
    {
        GeneratorHarness.Bind(schema).Diagnostics.Should().BeEmpty();
    }

    /// <summary>ParseEnum gives an enum without values the value NONE = 0.</summary>
    [Fact]
    public void Bind_EnumWithoutValues_HasNoneValue()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind("enum E : byte {} table T { e: E; v: [E]; o: E = NONE; }");

        diagnostics.Should().BeEmpty();
        bind.Declarations.OfType<EnumModel>()
            .Single()
            .Members.Select(m => (m.Name, m.Value))
            .Should()
            .Equal(("NONE", 0m));
    }

    /// <summary>ParseEnum: the value must be an integer token.</summary>
    [Theory]
    [InlineData("table A {} table B {} union U { A = «\"2\"», B }")]
    [InlineData("table A {} table B {} union U { A, B = «\"+-3\"» }")]
    public void Bind_UnionDiscriminatorString_ReportsInvalidUnion(string schema)
    {
        AssertDiagnostic(DiagnosticIds.InvalidUnion, schema);
    }

    /// <summary>
    /// ParseField adds a <c>u_type</c> field for a union field <c>u</c>, and CheckClash rejects names its code generators
    /// use for another field.
    /// </summary>
    [Theory]
    [InlineData("table A {} union U { A } table T { «u_type»: ubyte; u: U; }")]
    [InlineData("table A {} union U { A } table T { u: U; «u_type»: ubyte; }")]
    [InlineData("table A {} union U { A } table T { u: [U]; «u_type»: [ubyte]; }")]
    [InlineData("table A {} union U { A } table T { u: U; «uType»: int; }")]
    [InlineData("table T { v: [int]; «v_length»: int; }")]
    [InlineData("table T { «vLength»: int; v: [string]; }")]
    [InlineData("table A {} union U { A } table T { u: [U]; «u_type_length»: int; }")]
    [InlineData("table T { s: string; «s_byte_vector»: int; }")]
    [InlineData("table T { s: string; «sByteVector»: int; }")]
    public void Bind_FieldNameFlatcGeneratesForAnotherField_ReportsFieldNameClash(string schema)
    {
        AssertDiagnostic(DiagnosticIds.FieldNameClash, schema);
    }

    /// <summary>ParseField: "field name can not be the same as table/struct name".</summary>
    [Theory]
    [InlineData("table T { «T»: int; }")]
    [InlineData("table A {} table B { «A»: int; }")]
    [InlineData("struct A { x: int; } struct B { «A»: int; }")]
    [InlineData("namespace n; table A {} namespace n.m; table B { «A»: int; }")]
    [InlineData("namespace a; table T { x: N; «N»: int; } namespace a; table N {}")]
    public void Bind_FieldNamedLikeTableOrStruct_ReportsFieldNameClash(string schema)
    {
        AssertDiagnostic(DiagnosticIds.FieldNameClash, schema);
    }

    /// <summary>
    /// flatc only reads included files, but it may read a file in an include cycle with this one before this one, so a
    /// table there counts.
    /// </summary>
    [Fact]
    public void Bind_FieldNamedLikeTableInIncludeCycle_ReportsFieldNameClash()
    {
        var (_, diagnostics) = GeneratorHarness.Bind(
            ("a.fbs", "include \"b.fbs\"; table x {}"),
            ("b.fbs", "include \"a.fbs\"; table T { x: int; }")
        );

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be(DiagnosticIds.FieldNameClash);
        (diagnostic.Span.Path, diagnostic.Span.Start)
            .Should()
            .Be((GeneratorHarness.Root + "b.fbs", "include \"a.fbs\"; table T { ".Length));
    }

    [Fact]
    public void Bind_FieldNamedLikeTableInFileNotIncluded_IsAccepted()
    {
        GeneratorHarness.Bind(("a.fbs", "table x {}"), ("b.fbs", "table T { x: int; }")).Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("table T { type: int; length: int; }")]
    [InlineData("table T { u_type: int; v_length: int; }")]
    [InlineData("table T { a: int; } table A {}")]
    [InlineData("table T { U: U; } table U {}")]
    [InlineData("table T { t: int; }")]
    public void Bind_FieldNamesFlatcAccepts_AreAccepted(string schema)
    {
        GeneratorHarness.Bind(schema).Diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// flatc labels a member <c>a.T</c> as <c>a_T</c> (union_value_namespacing). Bequadro labels it <c>T</c>, and requires
    /// both labels to be unique.
    /// </summary>
    [Theory]
    [InlineData("namespace a; table T {} namespace b; table T {} namespace; union V { a.T, «a_T: b.T» }")]
    [InlineData("namespace a; table T {} namespace b; table T {} namespace; union V { a.T, «b.T» }")]
    public void Bind_UnionLabelsDuplicateForFlatcOrBequadro_ReportsDuplicateMember(string schema)
    {
        AssertDiagnostic(DiagnosticIds.DuplicateMember, schema);
    }

    [Fact]
    public void Bind_NegativeZeroDefault_IsKept()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind(
            "table T { f: float = -0.0; d: double = -0; z: double = 0.0; }"
        );

        diagnostics.Should().BeEmpty();
        var fields = bind.Declarations.OfType<TableModel>().Single().Fields;
        fields.Select(TableEmitter.DefaultLiteral).Should().Equal("-0f", "-0d", null);
        Constants.IsNegativeZero(fields[0].Default!.Float).Should().BeTrue();
    }

    /// <summary>
    /// SCH-010 allows only plain literals: <c>true</c>, <c>false</c>, <c>0</c> and <c>1</c> for a bool. flatc also
    /// accepts a default in quotes, and any integer that fits, but Bequadro doesn't, for any scalar.
    /// </summary>
    [Theory]
    [InlineData("table T { b: bool = «\"true\"»; }")]
    [InlineData("table T { b: bool = «\"1\"»; }")]
    [InlineData("table T { b: bool = «True»; }")]
    [InlineData("table T { b: bool = «0x1»; }")]
    [InlineData("table T { i: int = «\"5\"»; }")]
    [InlineData("table T { f: float = «\"1.5\"»; }")]
    public void Bind_DefaultNotPlainLiteral_ReportsInvalidDefault(string schema)
    {
        AssertDiagnostic(DiagnosticIds.InvalidDefault, schema);
    }

    /// <summary>flatc converts these with strtod, which ignores case.</summary>
    [Theory]
    [InlineData("NaN", "double.NaN")]
    [InlineData("NAN", "double.NaN")]
    [InlineData("-nan", "double.NaN")]
    [InlineData("Infinity", "double.PositiveInfinity")]
    [InlineData("INFINITY", "double.PositiveInfinity")]
    [InlineData("Inf", "double.PositiveInfinity")]
    [InlineData("+inf", "double.PositiveInfinity")]
    [InlineData("-Infinity", "double.NegativeInfinity")]
    [InlineData("1e400", "double.PositiveInfinity")]
    [InlineData("-1e400", "double.NegativeInfinity")]
    [InlineData("5.", "5d")]
    [InlineData("1.e2", "100d")]
    public void Bind_FloatConstant_IsAccepted(string literal, string expected)
    {
        var (bind, diagnostics) = GeneratorHarness.Bind($"table T {{ x: double = {literal}; }}");

        diagnostics.Should().BeEmpty();
        TableEmitter.DefaultLiteral(bind.Declarations.OfType<TableModel>().Single().Fields[0]).Should().Be(expected);
    }

    /// <summary>TryTypedValue: a hexadecimal integer can't initialize a floating-point field.</summary>
    [Fact]
    public void Bind_HexadecimalFloatDefault_ReportsInvalidDefault()
    {
        AssertDiagnostic(DiagnosticIds.InvalidDefault, "table T { x: float = «0x10»; }");
    }

    /// <summary>ParseField: "the default 'null' is reserved for declaring optional scalar fields".</summary>
    [Fact]
    public void Bind_NullDefaultOfEnumWithValueNamedNull_ReportsInvalidDefault()
    {
        AssertDiagnostic(DiagnosticIds.InvalidDefault, "enum E : byte { null, A } table T { e: E = «null»; }");
    }

    [Theory]
    [InlineData("offset64")]
    [InlineData("vector64")]
    public void Bind_SixtyFourBitOffsetAttribute_ReportsUnsupportedFeature(string attribute)
    {
        AssertDiagnostic(DiagnosticIds.UnsupportedFeature, $"table T {{ v: [int] («{attribute}»); }}");
    }

    /// <summary>
    /// <c>required</c> is a known attribute to flatc, which checks it only on fields, so it is accepted and ignored on a
    /// declaration.
    /// </summary>
    [Fact]
    public void Bind_RequiredOnDeclaration_IsAcceptedAndIgnored()
    {
        var (bind, diagnostics) = GeneratorHarness.Bind("table T (required) {} struct S (required) { x: int; }");

        diagnostics.Should().BeEmpty();
        bind.Declarations.Should().HaveCount(2);
    }

    private static (BindResult Bind, IReadOnlyList<SchemaDiagnostic> Diagnostics) AssertDiagnostic(
        string id,
        string markedSchema
    )
    {
        var start = markedSchema.IndexOf('«', StringComparison.Ordinal);
        var end = markedSchema.IndexOf('»', StringComparison.Ordinal) - 1;
        var schema = markedSchema.Replace("«", "").Replace("»", "");

        var (bind, diagnostics) = GeneratorHarness.Bind(schema);

        diagnostics
            .Should()
            .ContainSingle($"exactly one diagnostic is expected for: {schema}")
            .Which.Should()
            .Match<SchemaDiagnostic>(d => d.Id == id && d.Span.Start == start && d.Span.Length == end - start);
        return (bind, diagnostics);
    }
}
