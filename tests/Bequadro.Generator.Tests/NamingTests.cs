using Bequadro.Generator.Emit;

namespace Bequadro.Generator.Tests;

/// <summary>
/// NAM-001, NAM-002 and API-002: names C# can't use are renamed, never rejected, and the generated code compiles.
/// </summary>
public class NamingTests
{
    /// <summary>
    /// <paramref name="expected"/> lists each generated type, by its C# metadata name, with its schema members, as in
    /// <see cref="ErrorRecoveryTests"/>.
    /// </summary>
    [Theory]
    [InlineData("table T { foo_bar: int; fooBar: int; }", "T: FooBar, FooBar_")]
    [InlineData("table T { a_: int; a: int; A: int; }", "T: A, A_, A__")]
    [InlineData("table T { t: int; }", "T: T_")]
    [InlineData("table T { serializer: int; to_string: int; get_type: int; }", "T: Serializer_, ToString_, GetType_")]
    [InlineData("struct S { s: int; equals: int; }", "S: S_, Equals_")]
    [InlineData("table T { _1: int; } struct S { _2d: int; }", "T: _1; S: _2d")]
    [InlineData(
        "table A {} table B {} table C {} table D {} table E {}"
            + " union U { Kind: A, Value: B, TryGetValue: C, _kind: D, _value1: E }",
        "A: ; B: ; C: ; D: ; E: ; U: Kind_, Value_, TryGetValue_, _kind_, _value1_"
    )]
    [InlineData("table A {} union U { U: A }", "A: ; U: U_")]
    [InlineData("table Serializer { a: int; }", "Serializer_: A")]
    [InlineData("struct Equals { x: int; }", "Equals_: X")]
    [InlineData("table A {} union ItemKind { A }", "A: ; ItemKind_: A")]
    [InlineData(
        "table Equals (bequadro_value_equality) {} table T (bequadro_value_equality) { equals: int; get_hash_code: int; }",
        "Equals_: ; T: Equals_, GetHashCode_"
    )]
    [InlineData(
        "table System {} struct Bequadro { x: int; } table T { a: System; b: Bequadro; }",
        "System_: ; Bequadro_: X; T: A, B"
    )]
    [InlineData("table System_ {} table System {}", "System_: ; System__: ")]
    [InlineData("table BequadroSchema { a: int; } table T { b: BequadroSchema; }", "BequadroSchema_: A; T: B")]
    [InlineData("namespace BequadroSchema.N; table T { a: int; }", "BequadroSchema_.N.T: A")]
    [InlineData("namespace System.Collections; table Generic { a: int; }", "System_.Collections.Generic: A")]
    [InlineData("namespace Bequadro.Internal; enum E : ubyte { A }", "Bequadro.Internal_.E: A")]
    [InlineData("namespace Bequadro; table UnionNone {} table Other {}", "Bequadro.UnionNone_: ; Bequadro.Other: ")]
    [InlineData("namespace Bequadro.Models; table T {}", "Bequadro.Models.T: ")]
    [InlineData("table T { reference_equals: int; }", "T: ReferenceEquals_")]
    [InlineData("table T (bequadro_value_equality) { reference_equals: int; }", "T: ReferenceEquals_")]
    [InlineData("struct S { reference_equals: int; finalize: int; }", "S: ReferenceEquals_, Finalize_")]
    [InlineData("enum E : byte { value__, A }", "E: value___, A")]
    [InlineData("enum value__ : byte { A }", "value__: A")]
    [InlineData(
        "table A {} table B {} table C {} table D {} table E {} table F {} table G {} table H {}"
            + " union U { ItemKind: A, HasValue: B, ToString: C, Equals: D, GetHashCode: E, GetType: F,"
            + " MemberwiseClone: G, Finalize: H }",
        "A: ; B: ; C: ; D: ; E: ; F: ; G: ; H: ; U: ItemKind_, HasValue_, ToString_, Equals_, GetHashCode_, GetType_,"
            + " MemberwiseClone_, Finalize_"
    )]
    [InlineData(
        "table A {} table B {} table C {} table D {} table E {} table F {}"
            + " union U { ReferenceEquals: A, value__: B, op_Equality: C, op_Inequality: D, op_Implicit: E, _reference: F }",
        "A: ; B: ; C: ; D: ; E: ; F: ; U: ReferenceEquals_, value___, op_Equality_, op_Inequality_, op_Implicit_,"
            + " _reference_"
    )]
    [InlineData(
        "table A {} table B {} table C {} table D {} table E {} table F {}"
            + " union U { get_Kind: A, get_Value: B, get_HasValue: C, get_D: D, D: E, get_TryGetValue: F }",
        "A: ; B: ; C: ; D: ; E: ; F: ; U: get_Kind_, get_Value_, get_HasValue_, get_D_, D, get_TryGetValue"
    )]
    [InlineData("table A {} table B {} union U { Kind: A, get_Kind_: B }", "A: ; B: ; U: Kind_, get_Kind__")]
    [InlineData(
        "table A {} table B {} table C {} table D {} union U { get_get_A: A, A: B, get_A: C, get_A_: D }",
        "A: ; B: ; C: ; D: ; U: get_get_A, A, get_A__, get_A_"
    )]
    [InlineData(
        "table A {} table B {} union U (bequadro_exclude_none) { A, get_HasValue: B }",
        "A: ; B: ; U: A, get_HasValue_"
    )]
    [InlineData("table A {} union op_Implicit { A }", "A: ; op_Implicit_: A")]
    [InlineData("table A {} union op_Equality { A }", "A: ; op_Equality_: A")]
    [InlineData("struct op_Inequality { x: int; }", "op_Inequality_: X")]
    [InlineData("table op_Equality (bequadro_value_equality) { x: int; }", "op_Equality_: X")]
    [InlineData("table op_Equality { x: int; }", "op_Equality: X")]
    [InlineData("table get_Serializer { x: int; }", "get_Serializer_: X")]
    [InlineData("table get_X { x: int; } table set_Y { y: int; }", "get_X: X_; set_Y: Y_")]
    [InlineData("struct get_X { x: int; }", "get_X: X")]
    [InlineData("table ToString { x: int; }", "ToString_: X")]
    [InlineData(
        "table A {} union Kind { A } union Value { A } union HasValue { A } union TryGetValue { A }",
        "A: ; Kind_: A; Value_: A; HasValue_: A; TryGetValue_: A"
    )]
    [InlineData(
        "table A {} union get_Kind { A } union get_Value { A } union get_HasValue { A } union _kind { A }"
            + " union _reference { A } union get_A { A }",
        "A: ; get_Kind_: A; get_Value_: A; get_HasValue_: A; _kind_: A; _reference_: A; get_A: A_"
    )]
    [InlineData("struct P { x: int; } struct Q { x: int; } union _value1 { P, Q }", "P: X; Q: X; _value1_: P, Q")]
    [InlineData(
        "namespace Bequadro; table FlatBufferSerializer {} table UnionUnknown {} table Internal {}",
        "Bequadro.FlatBufferSerializer_: ; Bequadro.UnionUnknown_: ; Bequadro.Internal_: "
    )]
    [InlineData(
        "namespace Bequadro.UnionNone; table A {} union U { A } table T { u: U; }",
        "Bequadro.UnionNone_.A: ; Bequadro.UnionNone_.U: A; Bequadro.UnionNone_.T: U"
    )]
    [InlineData(
        "namespace Bequadro.UnionUnknown.X; table A {} union U { A } table T { u: U; }",
        "Bequadro.UnionUnknown_.X.A: ; Bequadro.UnionUnknown_.X.U: A; Bequadro.UnionUnknown_.X.T: U"
    )]
    [InlineData("namespace N; table M {} namespace N.M; table X { m: N.M; }", "N.M: ; N.M_.X: M")]
    [InlineData("namespace N.M; table X {} namespace N; table M { x: N.M.X; }", "N.M.X: ; N.M_: X")]
    public void Generate_NamesCSharpCannotUse_RenamesThem(string schema, string expected)
    {
        var expectedTypes = expected
            .Split(';', StringSplitOptions.TrimEntries)
            .Select(type => type.Split(':', StringSplitOptions.TrimEntries))
            .ToDictionary(
                parts => parts[0],
                parts => parts[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            );

        var result = GeneratorHarness.Run(schema);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        result
            .HintNames.Should()
            .BeEquivalentTo(expectedTypes.Keys.Select(name => name + ".g.cs").Append(FingerprintEmitter.HintName));
        foreach (var (name, members) in expectedTypes)
        {
            var type = result.Output.GetTypeByMetadataName(name);
            type.Should().NotBeNull($"{name} should be generated");
            ErrorRecoveryTests.SchemaMembers(type!).Should().Equal(members, $"the generated members of {name}");
        }
    }

    /// <summary>
    /// Every contextual keyword, and other all-lowercase names, which C# warns about as type names (CS8981).
    /// </summary>
    public static TheoryData<string> LowercaseNames { get; } =
    [
        "lower",
        "add",
        "allows",
        "alias",
        "and",
        "ascending",
        "args",
        "async",
        "await",
        "by",
        "descending",
        "dynamic",
        "equals",
        "extension",
        "field",
        "file",
        "from",
        "get",
        "global",
        "group",
        "init",
        "into",
        "join",
        "let",
        "managed",
        "nameof",
        "nint",
        "not",
        "notnull",
        "nuint",
        "on",
        "or",
        "orderby",
        "partial",
        "record",
        "remove",
        "required",
        "scoped",
        "select",
        "set",
        "union",
        "unmanaged",
        "value",
        "var",
        "when",
        "where",
        "with",
        "yield",
    ];

    /// <summary>
    /// API-001, API-002: a type named with lowercase letters only, as every contextual keyword is, is escaped with
    /// <c>@</c> rather than renamed, and generated code doesn't use the contextual keywords it could capture, such as
    /// <c>var</c>. Each declaration kind is used from the same namespace in every position generated code refers to it.
    /// </summary>
    [Theory]
    [MemberData(nameof(LowercaseNames))]
    public void Generate_LowercaseTypeNames_AreEscapedAndCompile(string name)
    {
        string[] schemas =
        [
            $"namespace N; table {name} {{ a: int; next: {name}; }}"
                + $" table T (bequadro_value_equality) {{ t: {name} (required); ts: [{name}]; }} union U {{ {name}, T }}",
            $"namespace N; struct {name} {{ a: int; }} struct Q {{ x: int; }}"
                + $" table T {{ s: {name}; r: {name} (required); ss: [{name}]; }} union U {{ {name}, Q, T }}",
            $"namespace N; enum {name} : byte {{ A, B }} struct S {{ e: {name}; }}"
                + $" table T {{ e: {name} = B; o: {name} = null; es: [{name}]; }}",
            $"namespace N; table A {{}} union {name} {{ A }} union X (bequadro_exclude_none) {{ A }}"
                + $" table T (bequadro_value_equality) {{ u: {name}; r: {name} (required); us: [{name}]; x: X; }}",
        ];

        foreach (var schema in schemas)
        {
            var result = GeneratorHarness.Run(schema);

            result.GeneratorDiagnostics.Should().BeEmpty(schema);
            result.CompilationDiagnostics.Should().BeEmpty(schema);
            result.Output.GetTypeByMetadataName("N." + name).Should().NotBeNull(schema);
            result.Source($"N.{name}.g.cs").Should().MatchRegex($@"(class|struct|enum) @{name}\b", schema);
        }
    }

    [Theory]
    [InlineData("foo_bar")]
    [InlineData("foo1")]
    [InlineData("_foo")]
    public void Generate_LowercaseTypeNamesWithOtherCharacters_AreNotEscaped(string name)
    {
        var result = GeneratorHarness.Run($"namespace N; table {name} {{ a: int; }} table T {{ x: {name}; }}");

        result.CompilationDiagnostics.Should().BeEmpty();
        result.Source($"N.{name}.g.cs").Should().Contain($"class {name} ");
    }

    /// <summary>
    /// API-001: keywords, contextual or not, are escaped wherever they are used as identifiers.
    /// </summary>
    [Fact]
    public void Generate_KeywordsAsOtherNames_AreEscaped()
    {
        var result = GeneratorHarness.Run(
            "namespace global.var.record.__arglist; enum E : byte { var, global, field, value, __arglist, nameof }"
                + " table A {} table B {} table C {} struct P { x: int; }"
                + " union U { var: A, field: B, value: C, __makeref: P } table T { e: E = field; u: U; }"
        );

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        result
            .Source("global.var.record.__arglist.T.g.cs")
            .Should()
            .Contain("namespace @global.@var.@record.@__arglist;");
        ErrorRecoveryTests
            .SchemaMembers(result.Output.GetTypeByMetadataName("global.var.record.__arglist.E")!)
            .Should()
            .Equal("var", "global", "field", "value", "__arglist", "nameof");
    }

    [Fact]
    public void Generate_DeclarationsDifferingOnlyInCase_RenamesOnlyTheLaterFile()
    {
        var result = GeneratorHarness.Run(
            ("a.fbs", "table Foo { a: int; }"),
            ("b.fbs", "include \"a.fbs\"; table foo { b: int; } table T { f: foo; g: Foo; }")
        );

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        result.HintNames.Should().BeEquivalentTo("Foo.g.cs", "foo_.g.cs", "T.g.cs", FingerprintEmitter.HintName);
        result.Output.GetTypeByMetadataName("foo").Should().NotBeNull();
    }

    [Fact]
    public void Generate_FieldAddedLater_DoesNotRenameExistingField()
    {
        var before = GeneratorHarness.Run("table T { fooBar: int; }");
        var after = GeneratorHarness.Run("table T { fooBar: int; foo_bar: int; }");

        ErrorRecoveryTests.SchemaMembers(before.Output.GetTypeByMetadataName("T")!).Should().Equal("FooBar");
        ErrorRecoveryTests.SchemaMembers(after.Output.GetTypeByMetadataName("T")!).Should().Equal("FooBar", "FooBar_");
    }

    /// <summary>
    /// SCH-005: a deprecated field isn't generated, but still claims its name, so deprecating a field never renames
    /// another. A <c>required</c> deprecated field isn't required.
    /// </summary>
    [Fact]
    public void Generate_FieldDeprecatedLater_DoesNotRenameOtherFields()
    {
        var before = GeneratorHarness.Run("table T { foo_bar: int; fooBar: int; }");
        var after = GeneratorHarness.Run(
            "table T { foo_bar: int (deprecated); fooBar: int; s: string (required, deprecated); }"
        );

        ErrorRecoveryTests.SchemaMembers(before.Output.GetTypeByMetadataName("T")!).Should().Equal("FooBar", "FooBar_");
        after.GeneratorDiagnostics.Should().BeEmpty();
        after.CompilationDiagnostics.Should().BeEmpty();
        ErrorRecoveryTests.SchemaMembers(after.Output.GetTypeByMetadataName("T")!).Should().Equal("FooBar_");
    }

    [Fact]
    public void Generate_RenamedElements_NoteTheSchemaNameInTheirDocumentation()
    {
        var result = GeneratorHarness.Run(
            "/// A table.\ntable Serializer { serializer_: int; fooBar: int; }\ntable A {}\nunion U { Kind: A }"
        );

        var table = result.Source("Serializer_.g.cs");
        table.Should().Contain("/// A table.\n/// Schema name: <c>Serializer</c>.\n/// </summary>");
        table.Should().Contain("/// Schema name: <c>serializer_</c>.");
        table.Should().NotContain("<c>fooBar</c>", "case conversion alone isn't renaming");
        result.Source("U.g.cs").Should().Contain("/// Schema name: <c>Kind</c>.");
    }
}
