using System.Text.RegularExpressions;

namespace Bequadro.Cli.Tests;

/// <summary>
/// PY-001 to PY-003: the shape of generated Python. Its behavior is tested in tests/python.
/// </summary>
public sealed class PythonTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private (ExitCode Code, string Output, string Error) GeneratePython(params string[] extra) =>
        _cli.Run(["generate", "--target", "python", "--out", "out", .. extra]);

    private string Module(string schema, string modulePath = "bequadro_root.py")
    {
        _cli.Write("schema.fbs", schema);
        var result = GeneratePython("schema.fbs");
        result.Error.Should().BeEmpty();
        result.Code.Should().Be(ExitCode.Success);
        return _cli.Read(Path.Combine("out", modulePath));
    }

    [Fact]
    public void Generate_RepresentativeSchema_MatchesSnapshot()
    {
        _cli.Write("sample.fbs", TypeScriptTests.Schema);

        var result = GeneratePython("sample.fbs");

        result.Code.Should().Be(ExitCode.Success);
        _cli.OutputFiles()
            .Should()
            .Equal(
                "Sample/Geometry/__init__.py",
                "Sample/Other/__init__.py",
                "Sample/__init__.py",
                "__init__.py",
                "bequadro.py",
                "bequadro_schema.py"
            );
        Snapshot.Match("Sample.Geometry", ".py", _cli.Read("out/Sample/Geometry/__init__.py"));
        Snapshot.Match("Sample.Other", ".py", _cli.Read("out/Sample/Other/__init__.py"));
    }

    [Fact]
    public void Generate_RootNamespace_IsAModuleThePackageReExports()
    {
        _cli.Write("schema.fbs", "table Root {} enum E : ubyte { A } namespace N; table T { root: Root; }");

        GeneratePython("schema.fbs").Code.Should().Be(ExitCode.Success);

        _cli.OutputFiles()
            .Should()
            .Equal("N/__init__.py", "__init__.py", "bequadro.py", "bequadro_root.py", "bequadro_schema.py");
        _cli.Read("out/__init__.py")
            .Should()
            .Contain("from .bequadro_root import Root as Root\nfrom .bequadro_root import E as E\n");
        _cli.Read("out/N/__init__.py").Should().Contain("from .. import bequadro_root as _bq_n0");
    }

    [Fact]
    public void Generate_EnumsComeBeforeImportsOfOtherNamespaces()
    {
        var module = Module(
            "namespace A; enum E : ubyte { X } table T { b: B.U; } namespace B; table U { e: A.E = X; }",
            "A/__init__.py"
        );

        module
            .IndexOf("class E(_bq.IntEnum):", StringComparison.Ordinal)
            .Should()
            .BeLessThan(module.IndexOf("from .. import B as _bq_n0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("table T { from: int; }", "    from_: int = 0")]
    [InlineData("table T { from: int; from_: int; }", "    from__: int = 0")]
    [InlineData("table T { match: int; type: int; }", "    match: int = 0\n    type: int = 0")]
    [InlineData("table T { __x: int; }", "    __x___: int = 0")]
    [InlineData("table T { __init__: int; }", "    __init___: int = 0")]
    [InlineData("table T { int: int; }", "    int_: int = 0")]
    [InlineData("table T { list: [int]; }", "    list_: list[int] | None = None")]
    [InlineData("table T { _bq: int; }", "    _bq_: int = 0")]
    [InlineData("table T { U: U; } table U {}", "    U_: U | None = None")]
    [InlineData("table class {}", "class class_(_bq.Table):")]
    [InlineData("table eval {}", "class eval_(_bq.Table):")]
    [InlineData("table type {}", "class type_(_bq.Table):")]
    [InlineData("table _bq7 {}", "class _bq7_(_bq.Table):")]
    [InlineData("enum E : ubyte { None }", "    None_ = 0")]
    [InlineData("enum E : ubyte { name, value }", "    name_ = 0")]
    [InlineData("enum E : ubyte { name, value }", "    value_ = 1")]
    [InlineData("enum E : ubyte { _x_ }", "    _x__ = 0")]
    [InlineData("enum E : ubyte { __y }", "    __y___ = 0")]
    public void Generate_Names_FollowThePythonRules(string schema, string expected)
    {
        Module(schema).Should().Contain(expected);
    }

    [Theory]
    [InlineData("namespace from.x; table T {}", "from_/x/__init__.py")]
    [InlineData("namespace bequadro; table T {}", "bequadro_/__init__.py")]
    [InlineData("namespace a; table T {} namespace A; table U {}", "A_/__init__.py")]
    public void Generate_NamespacesThatCannotBePackages_AreRenamed(string schema, string module)
    {
        _cli.Write("schema.fbs", schema);

        GeneratePython("schema.fbs").Code.Should().Be(ExitCode.Success);

        _cli.OutputFiles().Should().Contain(module);
    }

    [Theory]
    [InlineData("enum E : ubyte { mro, real }", "    mro_ = 0")]
    [InlineData("enum E : ubyte { mro, real }", "    real_ = 1")]
    [InlineData("enum E : ubyte { imag, numerator, denominator }", "    denominator_ = 2")]
    [InlineData("enum E : ubyte { bit_length, to_bytes, from_bytes }", "    from_bytes_ = 2")]
    [InlineData("enum E : ubyte { real_, real }", "    real__ = 1")]
    [InlineData("table annotations {}", "class annotations_(_bq.Table):")]
    [InlineData("table T { annotations: int; }", "    annotations: int = 0")]
    [InlineData("table bequadro {}", "class bequadro_(_bq.Table):")]
    [InlineData("table bequadro_root {}", "class bequadro_root_(_bq.Table):")]
    [InlineData("table bequadro_schema {}", "class bequadro_schema_(_bq.Table):")]
    [InlineData(
        "table data {} table t { d: data; }",
        "def _bq_fill_t_1(_bq_r: _bq.Reader, _bq_t: int, _bq_o: t) -> None:"
    )]
    public void Generate_Names_FollowThePythonRules_ForGeneratedNames(string schema, string expected)
    {
        Module(schema).Should().Contain(expected);
    }

    [Theory]
    [InlineData("namespace a.__init__; table T {}", "a/__init___/__init__.py")]
    [InlineData("namespace _bq.x; table T {}", "_bq_/x/__init__.py")]
    [InlineData("namespace a._bqx; table T {}", "a/_bqx_/__init__.py")]
    [InlineData("namespace a.annotations; table T {}", "a/annotations_/__init__.py")]
    [InlineData("namespace annotations; table T {}", "annotations_/__init__.py")]
    [InlineData("namespace bequadro_root; table T {}", "bequadro_root_/__init__.py")]
    [InlineData("namespace BEQUADRO_ROOT.x; table T {}", "BEQUADRO_ROOT_/x/__init__.py")]
    [InlineData("namespace BEQUADRO_SCHEMA; table T {}", "BEQUADRO_SCHEMA_/__init__.py")]
    [InlineData("namespace a; table b {} namespace a.b; table T {}", "a/b_/__init__.py")]
    [InlineData("table x {} namespace x; table T {}", "x_/__init__.py")]
    public void Generate_NamespacesThatCannotBePackages_AreRenamed_ForPythonNames(string schema, string module)
    {
        _cli.Write("schema.fbs", schema);

        GeneratePython("schema.fbs").Code.Should().Be(ExitCode.Success);

        _cli.OutputFiles().Should().Contain(module);
    }

    [Fact]
    public void Generate_RootDeclarationNamedLikeTheRuntime_IsRenamedInTheReExports()
    {
        _cli.Write("schema.fbs", "table bequadro {} table bequadro_root {}");

        GeneratePython("schema.fbs").Code.Should().Be(ExitCode.Success);

        _cli.Read("out/__init__.py")
            .Should()
            .Contain(
                "from .bequadro_root import bequadro_ as bequadro_\n"
                    + "from .bequadro_root import bequadro_root_ as bequadro_root_\n"
            );
    }

    [Fact]
    public void Generate_FieldAddedLater_LeavesExistingNamesAlone()
    {
        var before = Module("enum E : ubyte { class_, real_ } table T { from_: int; e: E; }");
        var after = Module("enum E : ubyte { class_, real_, class, real } table T { from_: int; e: E; from: int; }");

        before.Should().Contain("    from_: int = 0").And.Contain("    class_ = 0\n    real_ = 1");
        after
            .Should()
            .Contain("    from_: int = 0\n    e: E = E.class_\n    from__: int = 0")
            .And.Contain("    class_ = 0\n    real_ = 1\n    class__ = 2")
            .And.Contain("    real__ = 3");
    }

    /// <summary>
    /// PY-003: every parameter and local of generated code, and every comprehension variable, starts with <c>_bq</c>,
    /// which no declaration can, so no declaration can shadow one or be shadowed by one.
    /// </summary>
    [Fact]
    public void Generate_ParametersAndLocals_AllStartWithBq()
    {
        _cli.Write("sample.fbs", TypeScriptTests.Schema + "\ntable Equal (bequadro_value_equality) { f: float; }");
        GeneratePython("sample.fbs").Code.Should().Be(ExitCode.Success);
        var modules = _cli.OutputFiles()
            .Where(f => f.EndsWith(".py", StringComparison.Ordinal) && f != "bequadro.py")
            .Select(f => _cli.Read(Path.Combine("out", f)))
            .ToList();

        var names = modules.SelectMany(GeneratedLocals).ToList();

        names.Should().NotBeEmpty();
        names.Should().AllSatisfy(n => n.Should().StartWith("_bq"));
    }

    private static IEnumerable<string> GeneratedLocals(string module)
    {
        // The indentation of the def whose body the line is in, if any
        int? function = null;
        foreach (var line in module.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            if (indent <= function)
            {
                function = null;
            }

            var definition = Regex.Match(line, @"^\s*def \w+\((.*)\)");
            if (definition.Success)
            {
                function = indent;
                foreach (var parameter in definition.Groups[1].Value.Split(", "))
                {
                    yield return parameter.Split(':')[0];
                }

                continue;
            }

            if (function is null)
            {
                continue;
            }

            var assignment = Regex.Match(line, @"^\s+([A-Za-z_]\w*)(:[^=]*)? = ");
            if (assignment.Success)
            {
                yield return assignment.Groups[1].Value;
            }

            foreach (Match comprehension in Regex.Matches(line, @"\bfor (\w+) in\b"))
            {
                yield return comprehension.Groups[1].Value;
            }
        }
    }

    [Fact]
    public void Generate_EnumMemberStartingWithAnUnderscore_IsReferredToByValue()
    {
        Module("enum E : ubyte { _a } table T { e: E; }").Should().Contain("    e: E = E(0)");
    }

    [Fact]
    public void Generate_RenamedField_NotesTheSchemaName()
    {
        Module("table T { from: int; }").Should().Contain("    from_: int = 0\n    \"\"\"Schema name: `from`.\"\"\"");
    }

    [Fact]
    public void Generate_RequireScalarsWithoutDefault_LeavesThoseFieldsWithoutADefault()
    {
        _cli.Write("schema.fbs", "table T { a: int; b: int = 0; c: int = null; }");

        _cli.Run("generate", "--target", "python", "--out", "out", "--require-scalars-without-default", "schema.fbs");

        _cli.Read("out/bequadro_root.py").Should().Contain("    a: int\n    b: int = 0\n    c: int | None = None");
    }
}
