using System.Diagnostics;
using System.Text;

namespace Bequadro.Cli.Tests;

/// <summary>
/// TS-001 to TS-004 and NAM-002: the shape and names of generated TypeScript, whose behavior is tested in
/// tests/typescript, and CLI-008 for both targets.
/// </summary>
public sealed class TypeScriptTests : IDisposable
{
    internal const string Schema = """
        attribute "category";

        namespace Sample.Geometry;

        /// Documented enum.
        enum Color : ubyte
        {
            Red,
            /// Green <is> & "quoted" */ closed.
            Green = 3,
        }

        enum Wide : long { Min = -9223372036854775808, Zero = 0 }

        /// A point.
        struct Point (category: "Geometry")
        {
            x: double;
            flag: bool;
            color: Color;
        }

        struct Box
        {
            min: Point;
            max: Point;
            id: int;
        }

        table Label
        {
            text: string;
        }

        union Shape
        {
            Label,
            Boxed: Box,
        }

        /// Strict, with overlapping struct members.
        union Strict (bequadro_exclude_none, bequadro_exhaustive)
        {
            Label,
            Point,
            Boxed: Box = 5,
        }

        /// Every field kind.
        table Drawing
        {
            /// The title.
            title: string (required);
            scale: double = 1.5;
            layer: int = null;
            color: Color = Green;
            wide: Wide;
            nan_default: float = nan;
            origin: Point;
            bounds: Box (required);
            label: Label;
            shape: Shape;
            required_shape: Shape (required);
            weights: [float];
            colors: [Color];
            bytes: [ubyte];
            names: [string];
            points: [Point];
            labels: [Label] (required);
            shapes: [Shape];
            strict: Strict;
            stricts: [Strict];
            visible: bool = true;
            big: ulong = 18446744073709551615;
        }

        /// Value equality.
        table Measure (bequadro_value_equality)
        {
            name: string (required);
            amount: double;
            limit: int = null;
            origin: Point;
            label: Label;
            measure: Measure;
            shape: Shape;
            weights: [float];
            measures: [Measure];
        }

        table Empty {}

        namespace Sample.Other;

        table UsesGeometry
        {
            drawing: Sample.Geometry.Drawing;
            color: Sample.Geometry.Color;
        }
        """;

    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public void Generate_RepresentativeSchema_MatchesSnapshot()
    {
        _cli.Write("sample.fbs", Schema);

        var result = _cli.GenerateTypeScript("sample.fbs");

        result.Code.Should().Be(ExitCode.Success);
        _cli.OutputFiles().Should().Equal("Sample/Geometry.ts", "Sample/Other.ts", "bequadro.ts", "bequadro_schema.ts");
        Snapshot.Match("Sample.Geometry", ".ts", _cli.Read("out/Sample/Geometry.ts"));
        Snapshot.Match("Sample.Other", ".ts", _cli.Read("out/Sample/Other.ts"));
    }

    [Fact]
    public void Generate_ImportExtensionJs_ImportsWithJs()
    {
        _cli.Write("sample.fbs", Schema);

        _cli.GenerateTypeScript("--import-extension", "js", "sample.fbs");

        var module = _cli.Read("out/Sample/Other.ts");
        module.Should().Contain("import * as $bq from \"../bequadro.js\";");
        module.Should().Contain("import * as $n0 from \"./Geometry.js\";");
    }

    [Fact]
    public void Generate_TypeOnlyReferences_UseImportType()
    {
        var module = _cli.TypeScriptModule(
            "namespace A; enum E : ubyte { X } namespace B; table T { e: A.E = X; } table U { e: [A.E]; }",
            "B.ts"
        );

        module.Should().Contain("import * as $n0 from \"./A.ts\";", "the default refers to the enum's value");
        _cli.TypeScriptModule("namespace A; enum E : ubyte { X } namespace B; table U { e: [A.E]; }", "B.ts")
            .Should()
            .Contain("import type * as $n0 from \"./A.ts\";");
    }

    [Theory]
    [InlineData("table T { parent_index: int; }", "  parentIndex: number;")]
    [InlineData("table T { YAxis: int; }", "  yAxis: number;")]
    [InlineData("table T { force_11: int; }", "  force11: number;")]
    [InlineData("table T { class: int; }", "  class: number;")]
    [InlineData("table T { constructor: int; }", "  constructor_: number;")]
    [InlineData("table T { to_string: int; }", "  toString_: number;")]
    [InlineData("table T { foo_bar: int; fooBar: int; }", "  fooBar_: number;")]
    [InlineData("table T { _1: int; }", "  _1: number;")]
    [InlineData("table T { _: int; __: int; }", "  _: number;\n  __: number;")]
    [InlineData("enum E : ubyte { __proto__ }", "  __proto___: 0,")]
    [InlineData("table A {} union U { unknown: A }", "| { readonly kind: \"unknown_\"; value: A }")]
    [InlineData("table class {}", "export interface class_ {")]
    [InlineData("table string {}", "export interface string_ {")]
    [InlineData("struct eval { x: int; }", "export interface eval_ {")]
    [InlineData("table keyof {}", "export interface keyof_ {")]
    [InlineData("struct readonly { x: int; }", "export interface readonly_ {")]
    [InlineData("table infer {}", "export interface infer_ {")]
    [InlineData("table A {} union unique { A }", "export type unique_ =")]
    [InlineData("enum as : int { A }", "export const as_ = {")]
    [InlineData("table type {}", "export interface type {")]
    [InlineData("table T { create: int; parse: int; }", "  create: number;\n  parse: number;")]
    public void Generate_Names_FollowTheTypeScriptRules(string schema, string expected)
    {
        _cli.TypeScriptModule(schema).Should().Contain(expected);
    }

    /// <summary>
    /// NAM-002: the declared table MonsterInit keeps its name and Monster's input type yields, whichever comes first.
    /// Under first appearance alone, Monster's derived MonsterInit would claim the name first in the first order.
    /// </summary>
    [Theory]
    [InlineData("table Monster {} table MonsterInit {}")]
    [InlineData("table MonsterInit {} table Monster {}")]
    public void Generate_DeclaredNameAndDerivedName_DeclaredNameWins(string schema)
    {
        var module = _cli.TypeScriptModule(schema);

        module.Should().Contain("export const MonsterInit = {", "the table keeps its name");
        module.Should().Contain("export const $c_MonsterInit: $bq.TableCodec<MonsterInit, MonsterInitInit> = {");
        module.Should().Contain("export const $c_Monster: $bq.TableCodec<Monster, MonsterInit_> = {");
        module.Should().NotContain("export const MonsterInit_");
    }

    /// <summary>
    /// NAM-002: a name that is taken or unusable keeps getting underscores until it is free and usable, and names claim
    /// in order: schema names first, in order of appearance, then derived names.
    /// </summary>
    [Theory]
    [InlineData(
        "table T { foo_bar: int; fooBar: int; fooBar_: int; }",
        "fooBar: number;",
        "fooBar_: number;",
        "fooBar__: number;"
    )]
    [InlineData("table T { constructor: int; constructor_: int; }", "constructor_: number;", "constructor__: number;")]
    [InlineData("table T { constructor_: int; constructor: int; }", "constructor_: number;", "constructor__: number;")]
    [InlineData("table class {} table class_ {}", "export interface class_ {", "export interface class__ {")]
    [InlineData(
        "table A {} table AInit {} table AInit_ {}",
        "$bq.TableCodec<A, AInit__>",
        "$bq.TableCodec<AInit, AInitInit>",
        "$bq.TableCodec<AInit_, AInit_Init>"
    )]
    public void Generate_ChainOfCollisions_RenamesUntilFree(string schema, params string[] expected)
    {
        var module = _cli.TypeScriptModule(schema);

        var positions = expected.Select(e => module.IndexOf(e, StringComparison.Ordinal)).ToList();
        positions.Should().NotContain(-1, "every name is generated: {0}", string.Join(", ", expected));
        positions.Should().BeInAscendingOrder();
    }

    /// <summary>
    /// NAM-002: fields are added at the end of a table, so a field that collides with an existing one is renamed, and
    /// the existing one keeps its name.
    /// </summary>
    [Theory]
    [InlineData("foo_bar: int;", "fooBar: int;", "fooBar", "fooBar_")]
    [InlineData("constructor_: int;", "constructor: int;", "constructor_", "constructor__")]
    [InlineData("fooBar: int;", "foo_bar: int;", "fooBar", "fooBar_")]
    public void Generate_FieldAddedLater_KeepsExistingNames(string first, string added, string kept, string renamed)
    {
        var before = _cli.TypeScriptModule($"table T {{ {first} }}");
        var after = _cli.TypeScriptModule($"table T {{ {first} {added} }}");

        before.Should().Contain($"  {kept}: number;");
        after.Should().Contain($"  {kept}: number;\n").And.Contain($"  {renamed}: number;\n");
        after
            .IndexOf($"  {kept}: number;", StringComparison.Ordinal)
            .Should()
            .BeLessThan(after.IndexOf($"  {renamed}: number;", StringComparison.Ordinal));
    }

    /// <summary>
    /// TS-004: with --require-scalars-without-default, scalar and enum fields with no default in the schema are required
    /// in the input type and create's parameter, and create doesn't fill them in.
    /// </summary>
    [Fact]
    public void Generate_RequireScalarsWithoutDefault_RequiresThemInTheInputType()
    {
        _cli.Write(
            "schema.fbs",
            "enum E : ubyte { A, B } table T { a: int; b: int = 0; c: int = null; e: E; f: E = B; }"
        );

        _cli.GenerateTypeScript("--require-scalars-without-default", "schema.fbs").Code.Should().Be(ExitCode.Success);

        var module = _cli.Read("out/index.ts");
        module
            .Should()
            .Contain(
                "export interface T {\n  a: number;\n  b: number;\n  c?: number | undefined;\n  e: E;\n  f: E;\n}"
            );
        module
            .Should()
            .Contain(
                "export interface TInit {\n  a: number;\n  b?: number | undefined;\n  c?: number | undefined;\n  e: E;\n  f?: E | undefined;\n}"
            );
        module
            .Should()
            .Contain(
                "create($init: { a: number; b?: number | undefined; c?: number | undefined; e: E; f?: E | undefined; }): T {"
            );
        module
            .Should()
            .Contain(
                "      a: $init.a,\n      b: $init.b ?? 0,\n      c: $init.c,\n      e: $init.e,\n      f: $init.f ?? E.B,"
            );
        _cli.TypeScriptModule("enum E : ubyte { A, B } table T { a: int; b: int = 0; c: int = null; e: E; f: E = B; }")
            .Should()
            .Contain("export interface TInit {\n  a?: number | undefined;")
            .And.Contain("create($init?: {");
    }

    [Fact]
    public void Generate_RenamedField_NotesTheSchemaName()
    {
        var module = _cli.TypeScriptModule("table T { constructor: int; }");

        module.Should().Contain("  /**\n   * Schema name: `constructor`.\n   */\n  constructor_: number;");
    }

    [Theory]
    [InlineData("namespace Bequadro; table T {}", "Bequadro_.ts")]
    [InlineData("namespace index; table T {}", "index_.ts")]
    [InlineData("namespace bequadro_schema; table T {}", "bequadro_schema_.ts")]
    [InlineData("namespace Bequadro_Schema.x; table T {}", "Bequadro_Schema_/x.ts")]
    [InlineData("namespace a; table T {} namespace A; table U {}", "A_.ts", "a.ts")]
    [InlineData("namespace A.x; table T {} namespace a.y; table U {}", "A/x.ts", "a_/y.ts")]
    public void Generate_NamespacesThatCollideAsFiles_AreRenamed(string schema, params string[] modules)
    {
        _cli.Write("schema.fbs", schema);

        _cli.GenerateTypeScript("schema.fbs").Code.Should().Be(ExitCode.Success);

        _cli.OutputFiles().Should().BeEquivalentTo([.. modules, "bequadro.ts", "bequadro_schema.ts"]);
    }

    /// <summary>
    /// CLI-008: a run of the executable, process start included, that generates every file of either target. The tests
    /// run the JIT-compiled build, which starts more slowly than the native executable CLI-001 publishes, so this is
    /// stricter than the requirement.
    /// </summary>
    [Theory]
    [InlineData("typescript", 52)]
    [InlineData("python", 54)]
    public async Task Generate_FiftyFilesFourHundredDeclarations_CompletesWithinBudget(string target, int files)
    {
        for (var i = 0; i < 50; i++)
        {
            _cli.Write($"schemas/file{i}.fbs", PerformanceFile(i));
        }

        var executable = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "bequadro.exe" : "bequadro"
        );
        var start = new ProcessStartInfo(executable, ["generate", "--target", target, "--out", "out", "schemas"])
        {
            WorkingDirectory = _cli.Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var cancellation = TestContext.Current.CancellationToken;
        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        stopwatch.Stop();

        await output;
        process.ExitCode.Should().Be(0, await error);
        _cli.OutputFiles().Should().HaveCount(files);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The schema of INC-003's test: each file includes the previous one and declares two structs, one enum, one
    /// union and four tables.
    /// </summary>
    internal static string PerformanceFile(int index)
    {
        var text = new StringBuilder();
        if (index > 0)
        {
            text.AppendLine($"include \"file{index - 1}.fbs\";");
        }

        text.AppendLine($"namespace Perf.F{index};");
        text.AppendLine($"struct S{index}a {{ x: double; y: double; z: double; }}");
        text.AppendLine($"struct S{index}b {{ a: S{index}a; flag: bool; count: int; }}");
        text.AppendLine($"enum E{index} : ubyte {{ A, B, C }}");
        text.AppendLine($"union U{index} {{ T{index}a, T{index}b, S{index}a }}");
        for (var t = 'a'; t <= 'd'; t++)
        {
            text.AppendLine($"table T{index}{t} {{");
            text.AppendLine($"  name: string (required); value: double = 1; kind: E{index} = B; position: S{index}b;");
            text.AppendLine($"  items: [S{index}a]; union_value: U{index}; children: [T{index}a]; tags: [string];");
            if (index > 0)
            {
                text.AppendLine($"  previous: Perf.F{index - 1}.T{index - 1}{t};");
            }

            text.AppendLine("}");
        }

        return text.ToString();
    }
}
