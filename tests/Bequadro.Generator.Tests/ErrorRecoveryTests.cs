using Bequadro.Generator.Emit;
using Microsoft.CodeAnalysis;

namespace Bequadro.Generator.Tests;

/// <summary>
/// DIA-001: a schema set with errors still generates everything that isn't in error, leaving out what is and what
/// refers to it, and the generated code compiles.
/// </summary>
public class ErrorRecoveryTests
{
    /// <summary>
    /// <paramref name="expected"/> lists each generated type with its schema members: table properties, struct fields,
    /// enum values or union labels, as in <c>"T: A, B; E: X"</c>.
    /// </summary>
    [Theory]
    [InlineData("table T { a: int; b: Missing; }", "T: A")]
    [InlineData("table T { a: int; b: [Missing]; c: int (required); d: int = 1.5; }", "T: A, C, D")]
    [InlineData("table T { _1: int; a: int; a: long; }", "T: _1, A")]
    [InlineData("table T { a: int; b int; c: int; }", "T: A, C")]
    [InlineData("table Good { a: int; } table Bad { a: int", "Good: A")]
    [InlineData("enum E { A } table T { a: int; e: E; }", "T: A")]
    [InlineData("enum E : ubyte { A, B = 300, C = 5, A } table T { e: E = C; }", "E: A, C; T: E")]
    [InlineData("struct S { x: int; y: string; z: [int]; } table T { s: S; }", "S: X; T: S")]
    [InlineData("struct S { x: int; _1: int; x: long; } table T { s: S; }", "S: X, _1; T: S")]
    [InlineData("struct S {} struct P { s: S; } table T { s: S; p: P; a: int; }", "T: A")]
    [InlineData("struct S { x: int; s: S; } table T { s: S; }", "S: X; T: S")]
    [InlineData("table A {} union U { A, Missing, B: A, NONE: A } table T { u: U; v: [U]; }", "A: ; U: A; T: U, V")]
    [InlineData("union U { Missing } table T { u: U; v: [U]; a: int; }", "T: A")]
    [InlineData("table A {} union U (bequadro_exclude_none) { A = 300 } table T { u: U; }", "A: ; T: ")]
    [InlineData("union U (bequadro_exhaustive) {} table T { u: U; a: int; }", "T: A")]
    [InlineData("include \"missing.fbs\"; table T { a: int; }", "T: A")]
    public void Generate_SchemaWithErrors_GeneratesWhatIsNotInError(string schema, string expected)
    {
        var expectedTypes = expected
            .Split(';', StringSplitOptions.TrimEntries)
            .Select(type => type.Split(':', StringSplitOptions.TrimEntries))
            .ToDictionary(
                parts => parts[0],
                parts => parts[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            );

        var result = GeneratorHarness.Run(schema);

        result.GeneratorDiagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
        result.CompilationDiagnostics.Should().BeEmpty();
        result
            .HintNames.Should()
            .BeEquivalentTo(expectedTypes.Keys.Select(name => name + ".g.cs").Append(FingerprintEmitter.HintName));
        foreach (var (name, members) in expectedTypes)
        {
            var type = result.Output.GetTypeByMetadataName(name)!;
            SchemaMembers(type).Should().Equal(members, $"the generated members of {name}");
        }
    }

    internal static IEnumerable<string> SchemaMembers(INamedTypeSymbol type)
    {
        var members = type.GetMembers().Where(m => m.DeclaredAccessibility == Accessibility.Public);
        if (type.TypeKind == TypeKind.Enum)
        {
            return members.OfType<IFieldSymbol>().Select(f => f.Name);
        }

        var fields = members.OfType<IFieldSymbol>().Where(f => !f.IsStatic).Select(f => f.Name).ToList();
        if (fields.Count > 0)
        {
            return fields;
        }

        // A union also has Kind, Value and HasValue, which aren't schema members
        return members
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && p.Name is not ("Kind" or "Value" or "HasValue"))
            .Select(p => p.Name);
    }
}
