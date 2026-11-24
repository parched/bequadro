using Bequadro.Generator.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bequadro.Generator.Tests;

/// <summary>
/// INC-001: pipeline caching.
/// </summary>
public class IncrementalityTests
{
    private const string Geometry = "namespace G; struct P { x: double; } table Shape { p: P; }";
    private const string Model =
        "include \"geometry.fbs\"; namespace M;\n/// Doc\ntable Model { shapes: [G.Shape]; } table Other { x: int; }";

    [Fact]
    public void Rerun_UnrelatedCSharpChange_AllOutputsCached()
    {
        var (driver, compilation) = RunInitial();

        var updated = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated { }"));
        var result = driver.RunGenerators(updated).GetRunResult().Results.Single();

        result
            .TrackedOutputSteps.SelectMany(s => s.Value)
            .SelectMany(s => s.Outputs)
            .Should()
            .AllSatisfy(o =>
                o.Reason.Should().BeOneOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged)
            );
        result
            .TrackedSteps[BequadroGenerator.ParseStep]
            .SelectMany(s => s.Outputs)
            .Should()
            .AllSatisfy(o => o.Reason.Should().Be(IncrementalStepRunReason.Cached));
    }

    [Fact]
    public void Rerun_OneSchemaChanged_ReparsesOnlyThatFile()
    {
        var (driver, compilation) = RunInitial();

        driver = ReplaceText(driver, "model.fbs", Model.Replace("x: int", "x: long"));
        var result = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        var parseReasons = result
            .TrackedSteps[BequadroGenerator.ParseStep]
            .SelectMany(s => s.Outputs)
            .Select(o => o.Reason)
            .ToList();
        parseReasons.Should().ContainSingle(r => r == IncrementalStepRunReason.Modified);
        parseReasons.Count(r => r == IncrementalStepRunReason.Cached).Should().Be(1);
    }

    [Fact]
    public void Rerun_DocCommentChanged_RegeneratesOnlyThatDeclaration()
    {
        var (driver, compilation) = RunInitial();

        driver = ReplaceText(driver, "model.fbs", Model.Replace("/// Doc", "/// Changed doc"));
        var result = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        var declarations = result.TrackedSteps[BequadroGenerator.DeclarationsStep].SelectMany(s => s.Outputs).ToList();
        declarations.Should().ContainSingle(o => o.Reason == IncrementalStepRunReason.Modified);
        declarations.Count(o => o.Reason == IncrementalStepRunReason.Unchanged).Should().Be(declarations.Count - 1);
    }

    /// <summary>
    /// SER-015: the fingerprint depends only on the files' tokens, so a change to comments or white space leaves it
    /// unchanged, and any other change regenerates it.
    /// </summary>
    [Theory]
    [InlineData("/// Doc", "/// Changed doc\n// More\n\n", false)]
    [InlineData("x: int", "x: long", true)]
    public void Rerun_SchemaChanged_RegeneratesTheFingerprintOnlyIfATokenChanged(
        string from,
        string to,
        bool tokenChanged
    )
    {
        var (driver, compilation) = RunInitial();
        var before = driver.GetRunResult().Results.Single().GeneratedSources.Single(IsFingerprint).SourceText;

        driver = ReplaceText(driver, "model.fbs", Model.Replace(from, to));
        var result = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        var reason = result
            .TrackedSteps[BequadroGenerator.FingerprintStep]
            .SelectMany(s => s.Outputs)
            .Should()
            .ContainSingle()
            .Which.Reason;
        if (tokenChanged)
        {
            reason.Should().Be(IncrementalStepRunReason.Modified);
        }
        else
        {
            reason.Should().BeOneOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged);
        }

        var after = result.GeneratedSources.Single(IsFingerprint).SourceText;
        (after.ToString() == before.ToString()).Should().Be(!tokenChanged);
    }

    private static bool IsFingerprint(GeneratedSourceResult source) => source.HintName == FingerprintEmitter.HintName;

    [Fact]
    public void Rerun_ErrorInOneDeclaration_RegeneratesOnlyThatDeclaration()
    {
        var (driver, compilation) = RunInitial();

        driver = ReplaceText(driver, "model.fbs", Model.Replace("x: int", "x: Missing"));
        var result = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        result.Diagnostics.Should().ContainSingle().Which.Severity.Should().Be(DiagnosticSeverity.Error);
        result.GeneratedSources.Should().HaveCount(5, "the other declarations and the fingerprint are still generated");
        var declarations = result.TrackedSteps[BequadroGenerator.DeclarationsStep].SelectMany(s => s.Outputs).ToList();
        declarations.Should().ContainSingle(o => o.Reason == IncrementalStepRunReason.Modified);
        declarations.Count(o => o.Reason == IncrementalStepRunReason.Unchanged).Should().Be(declarations.Count - 1);
    }

    /// <summary>
    /// CON-006: changing an MSBuild property rebinds, but regenerates only the declarations whose code it changes.
    /// </summary>
    [Fact]
    public void Rerun_PropertyChanged_RegeneratesOnlyAffectedDeclarations()
    {
        var (driver, compilation) = RunInitial();

        driver = driver.WithUpdatedAnalyzerConfigOptions(
            new GeneratorHarness.PropertiesOptionsProvider(GeneratorHarness.RequireScalars)
        );
        var result = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        result
            .TrackedSteps[BequadroGenerator.ParseStep]
            .SelectMany(s => s.Outputs)
            .Should()
            .AllSatisfy(o => o.Reason.Should().Be(IncrementalStepRunReason.Cached));
        result
            .TrackedSteps[BequadroGenerator.OptionsStep]
            .SelectMany(s => s.Outputs)
            .Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(IncrementalStepRunReason.Modified);
        var declarations = result.TrackedSteps[BequadroGenerator.DeclarationsStep].SelectMany(s => s.Outputs).ToList();
        declarations
            .Should()
            .ContainSingle(o => o.Reason == IncrementalStepRunReason.Modified, "only Other has a scalar");
        declarations.Count(o => o.Reason == IncrementalStepRunReason.Unchanged).Should().Be(declarations.Count - 1);
        result
            .GeneratedSources.Single(s => s.HintName == "M.Other.g.cs")
            .SourceText.ToString()
            .Should()
            .Contain("public required int X { get; set; }");
    }

    /// <summary>
    /// C# compares tables with their own <c>Equals</c>, so whether a referenced table has value equality doesn't change
    /// the code of the declarations that refer to it.
    /// </summary>
    [Fact]
    public void Rerun_ValueEqualityToggledOnReferencedTable_RegeneratesOnlyThatTable()
    {
        const string before = "namespace N; table A { x: int; } table B { a: A; as: [A]; } union U { A }";
        var (driver, old, compilation) = RunWith(before);

        driver = driver.ReplaceAdditionalText(
            old,
            new GeneratorHarness.InMemoryText(old.Path, before.Replace("table A", "table A (bequadro_value_equality)"))
        );
        var result = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        Reasons(result).Should().Equal(IncrementalStepRunReason.Modified, Unchanged, Unchanged);
    }

    /// <summary>
    /// A declaration added after the others, or the last one removed, leaves the others' outputs unchanged. Roslyn
    /// compares the declarations by position, so adding or removing one before others re-emits those after it. Either
    /// way, the output is the same as a fresh run's.
    /// </summary>
    [Theory]
    [InlineData("table A {} table B { a: A; }", "table A {} table B { a: A; } table C {}", "UUN")]
    [InlineData("table A {} table B { a: A; } table C {}", "table A {} table B { a: A; }", "UUR")]
    [InlineData("table A {} table B { a: A; }", "table A {} table New {} table B { a: A; }", "UMN")]
    [InlineData("table A {} table Old {} table B { a: A; }", "table A {} table B { a: A; }", "UMR")]
    public void Rerun_DeclarationAddedOrRemoved_MatchesFreshRun(string before, string after, string expected)
    {
        var (driver, old, compilation) = RunWith("namespace N; " + before);

        driver = driver.ReplaceAdditionalText(
            old,
            new GeneratorHarness.InMemoryText(old.Path, "namespace N; " + after)
        );
        var rerun = driver.RunGenerators(compilation).GetRunResult().Results.Single();
        var fresh = GeneratorHarness.Run(("schema.fbs", "namespace N; " + after)).RunResult.Results.Single();

        string.Concat(Reasons(rerun).Select(reason => reason.ToString()[0])).Should().Be(expected);
        rerun
            .GeneratedSources.Select(s => (s.HintName, Text: s.SourceText.ToString()))
            .Should()
            .Equal(fresh.GeneratedSources.Select(s => (s.HintName, Text: s.SourceText.ToString())));
    }

    private const IncrementalStepRunReason Unchanged = IncrementalStepRunReason.Unchanged;

    private static (GeneratorDriver Driver, AdditionalText Text, Compilation Compilation) RunWith(string schema)
    {
        var compilation = GeneratorHarness.CreateCompilation("class Existing { }");
        var text = new GeneratorHarness.InMemoryText(GeneratorHarness.Root + "schema.fbs", schema);
        var driver = GeneratorHarness.CreateDriver().AddAdditionalTexts([text]).RunGenerators(compilation);
        return (driver, text, compilation);
    }

    private static List<IncrementalStepRunReason> Reasons(GeneratorRunResult result) =>
        result
            .TrackedSteps[BequadroGenerator.DeclarationsStep]
            .SelectMany(s => s.Outputs)
            .Select(o => o.Reason)
            .ToList();

    private readonly GeneratorHarness.InMemoryText _geometry = new(GeneratorHarness.Root + "geometry.fbs", Geometry);
    private readonly GeneratorHarness.InMemoryText _model = new(GeneratorHarness.Root + "model.fbs", Model);

    private (GeneratorDriver Driver, Compilation Compilation) RunInitial()
    {
        var compilation = GeneratorHarness.CreateCompilation("class Existing { }");
        var driver = GeneratorHarness.CreateDriver().AddAdditionalTexts([_geometry, _model]).RunGenerators(compilation);
        driver.GetRunResult().Diagnostics.Should().BeEmpty();
        return (driver, compilation);
    }

    private GeneratorDriver ReplaceText(GeneratorDriver driver, string name, string text)
    {
        var old = name == "model.fbs" ? _model : _geometry;
        return driver.ReplaceAdditionalText(old, new GeneratorHarness.InMemoryText(old.Path, text));
    }
}
