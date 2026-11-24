using Bequadro.Generator.Emit;

namespace Bequadro.Generator.Tests;

/// <summary>
/// CON-003: schemas are the AdditionalFiles whose path ends in <c>.fbs</c>, in any case.
/// </summary>
public class ConsumptionTests
{
    [Fact]
    public void Generate_NonSchemaAdditionalFiles_AreIgnored()
    {
        var result = GeneratorHarness.Run(
            ("schema.fbs", "namespace N; table T {}"),
            ("notes.txt", "this is not { a schema"),
            ("schema.fbs.bak", "table Backup {}"),
            ("fbs", "table NoExtension {}")
        );

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        result.HintNames.Should().Equal("N.T.g.cs", FingerprintEmitter.HintName);
    }

    [Fact]
    public void Generate_NoSchemaFiles_GeneratesNothing()
    {
        var withOthers = GeneratorHarness.Run(("notes.txt", "table T {}"));
        var withNone = GeneratorHarness.Run();

        foreach (var result in new[] { withOthers, withNone })
        {
            result.GeneratorDiagnostics.Should().BeEmpty();
            result.CompilationDiagnostics.Should().BeEmpty();
            result.HintNames.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("schema.FBS")]
    [InlineData("schema.Fbs")]
    public void Generate_ExtensionInAnyCase_IsASchema(string path)
    {
        var result = GeneratorHarness.Run(
            (path, "namespace N; table T {}"),
            ("other.fbs", $"include \"{path}\"; namespace N; table U {{ t: T; }}")
        );

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        result.HintNames.Should().BeEquivalentTo("N.T.g.cs", "N.U.g.cs", FingerprintEmitter.HintName);
    }
}
