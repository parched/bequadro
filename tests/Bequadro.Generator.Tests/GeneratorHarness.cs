using System.Collections.Immutable;
using Bequadro.Generator.Common;
using Bequadro.Schema;
using Bequadro.Schema.Diagnostics;
using Bequadro.Schema.Semantics;
using Bequadro.Schema.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Bequadro.Generator.Tests;

/// <summary>
/// Runs the generator in memory over schema files given as (path, text) pairs.
/// </summary>
internal static class GeneratorHarness
{
    public const string Root = "/schemas/";

    /// <summary>
    /// The MSBuild properties of a project that sets <c>BequadroRequireScalarsWithoutDefault</c> to <c>true</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RequireScalars = new Dictionary<string, string>
    {
        [GeneratorOptions.RequireScalarsWithoutDefaultProperty] = "true",
    };

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(FlatBufferSerializer<>).Assembly.Location))
            .ToImmutableArray()
    );

    public static GeneratorResult Run(params (string Path, string Text)[] files) =>
        Run(ImmutableDictionary<string, string>.Empty, files);

    public static GeneratorResult Run(string schema) => Run(("schema.fbs", schema));

    /// <summary>
    /// Runs the generator in a project with the given MSBuild properties, which it sees as <c>build_property.*</c>
    /// global options, as the package's props file arranges.
    /// </summary>
    public static GeneratorResult Run(
        IReadOnlyDictionary<string, string> properties,
        params (string Path, string Text)[] files
    )
    {
        var driver = CreateDriver(properties);
        var compilation = CreateCompilation();
        driver = driver.AddAdditionalTexts(
            files.Select(f => (AdditionalText)new InMemoryText(Root + f.Path, f.Text)).ToImmutableArray()
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return new GeneratorResult(driver.GetRunResult(), output);
    }

    public static GeneratorResult Run(IReadOnlyDictionary<string, string> properties, string schema) =>
        Run(properties, ("schema.fbs", schema));

    public static GeneratorDriver CreateDriver(IReadOnlyDictionary<string, string>? properties = null) =>
        CSharpGeneratorDriver.Create(
            [new BequadroGenerator().AsSourceGenerator()],
            optionsProvider: new PropertiesOptionsProvider(properties ?? ImmutableDictionary<string, string>.Empty),
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true
            )
        );

    /// <summary>
    /// The parse options of a consumer project with <c>GenerateDocumentationFile</c>, so doc comments are checked. The
    /// generator driver parses generated sources with the options of the compilation's syntax trees.
    /// </summary>
    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.Diagnose);

    /// <summary>
    /// The options of a consumer that treats warnings as errors, at the highest warning level, so tests see at least
    /// what a <c>net11.0</c> project (warning level 11) sees, warning waves such as CS8981 included. Missing
    /// documentation (CS1591) isn't reported for the tests' own consumer sources; generated files disable it
    /// themselves.
    /// </summary>
    public static readonly CSharpCompilationOptions CompilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        nullableContextOptions: NullableContextOptions.Enable,
        generalDiagnosticOption: ReportDiagnostic.Error,
        warningLevel: 9999,
        specificDiagnosticOptions: [new("CS1591", ReportDiagnostic.Suppress)]
    );

    public static CSharpCompilation CreateCompilation(params string[] sources) =>
        CSharpCompilation.Create(
            "Consumer",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)),
            References.Value,
            CompilationOptions
        );

    /// <summary>
    /// Parses, binds and renames for C# without Roslyn, for focused semantic assertions.
    /// </summary>
    public static (BindResult Bind, ImmutableArray<SchemaDiagnostic> Diagnostics) Bind(
        params (string Path, string Text)[] files
    ) => Bind(SchemaOptions.Default, files);

    public static (BindResult Bind, ImmutableArray<SchemaDiagnostic> Diagnostics) Bind(string schema) =>
        Bind(("schema.fbs", schema));

    public static (BindResult Bind, ImmutableArray<SchemaDiagnostic> Diagnostics) Bind(
        SchemaOptions options,
        params (string Path, string Text)[] files
    )
    {
        var parsed = files.Select(f => Parser.Parse(Root + f.Path, f.Text)).ToImmutableArray();
        var bind = CSharpModels.Prepare(Binder.Bind(parsed, options));
        return (bind, parsed.SelectMany(p => p.Diagnostics).Concat(bind.Diagnostics).ToImmutableArray());
    }

    internal sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    /// <summary>
    /// Global options holding MSBuild properties under the <c>build_property.</c> prefix, the way the compiler passes
    /// a <c>CompilerVisibleProperty</c>. Keys compare ignoring case, as in the compiler.
    /// </summary>
    internal sealed class PropertiesOptionsProvider(IReadOnlyDictionary<string, string> properties)
        : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } =
            new DictionaryOptions(properties.ToDictionary(p => "build_property." + p.Key, p => p.Value));

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => DictionaryOptions.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => DictionaryOptions.Empty;

        private sealed class DictionaryOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public static readonly DictionaryOptions Empty = new([]);

            private readonly Dictionary<string, string> _values = new(values, StringComparer.OrdinalIgnoreCase);

            // A non-nullable out parameter, because the generator's polyfilled NotNullWhenAttribute hides the real one here
            public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
        }
    }
}

internal sealed record GeneratorResult(GeneratorDriverRunResult RunResult, Compilation Output)
{
    public ImmutableArray<Diagnostic> GeneratorDiagnostics => RunResult.Diagnostics;

    public ImmutableArray<Diagnostic> CompilationDiagnostics =>
        Output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToImmutableArray();

    public string Source(string hintName) =>
        RunResult.Results.Single().GeneratedSources.Single(s => s.HintName == hintName).SourceText.ToString();

    public IEnumerable<string> HintNames => RunResult.Results.Single().GeneratedSources.Select(s => s.HintName);
}
