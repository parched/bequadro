using Bequadro.Generator.Common;
using Bequadro.Generator.Diagnostics;
using Bequadro.Generator.Emit;
using Bequadro.Schema;
using Bequadro.Schema.Common;
using Bequadro.Schema.Diagnostics;
using Bequadro.Schema.Semantics;
using Bequadro.Schema.Syntax;
using Microsoft.CodeAnalysis;

namespace Bequadro.Generator;

/// <summary>
/// Generates C# types and FlatBuffers serialization code from the .fbs files in AdditionalFiles.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class BequadroGenerator : IIncrementalGenerator
{
    /// <summary>Tracking name of the per-file parse step.</summary>
    public const string ParseStep = "Parse";

    /// <summary>Tracking name of the step that reads the MSBuild properties.</summary>
    public const string OptionsStep = "Options";

    /// <summary>Tracking name of the step that binds the whole schema set.</summary>
    public const string BindStep = "Bind";

    /// <summary>Tracking name of the step that selects declarations to emit.</summary>
    public const string DeclarationsStep = "Declarations";

    /// <summary>Tracking name of the step that computes the schema set's fingerprint.</summary>
    public const string FingerprintStep = "Fingerprint";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var parsedFiles = context
            .AdditionalTextsProvider.Where(static text =>
                text.Path.EndsWith(".fbs", StringComparison.OrdinalIgnoreCase)
            )
            .Select(
                static (text, cancellationToken) =>
                    Parser.Parse(text.Path, text.GetText(cancellationToken)?.ToString() ?? string.Empty)
            )
            .WithTrackingName(ParseStep);

        var options = context
            .AnalyzerConfigOptionsProvider.Select(static (provider, _) => GeneratorOptions.From(provider.GlobalOptions))
            .WithTrackingName(OptionsStep);

        var schemaSet = parsedFiles
            .Collect()
            .Combine(options)
            .Select(
                static (input, _) =>
                    new SchemaSetResult(
                        input.Left.SelectMany(f => f.Diagnostics).ToEquatableArray(),
                        CSharpModels.Prepare(Binder.Bind(input.Left, input.Right))
                    )
            )
            .WithTrackingName(BindStep);

        context.RegisterSourceOutput(
            schemaSet.Select(static (result, _) => result.AllDiagnostics),
            static (context, diagnostics) =>
            {
                foreach (var diagnostic in diagnostics)
                {
                    context.ReportDiagnostic(DiagnosticDescriptors.ToDiagnostic(diagnostic));
                }
            }
        );

        // Generated even when the schema set has errors: the binder leaves out what is in error, and everything that
        // refers to it, so the generated code still compiles
        var declarations = schemaSet
            .SelectMany(static (result, _) => result.Bind.Declarations.Items)
            .WithTrackingName(DeclarationsStep);

        context.RegisterSourceOutput(declarations, static (context, model) =>
            context.AddSource(model.HintName, Emitter.Emit(model))
        );

        // From the files' digests only, so an edit that changes no token, such as to a comment, leaves it unchanged
        var fingerprint = parsedFiles
            .Select(static (file, _) => file.Digest)
            .Collect()
            .Select(static (digests, _) => digests.IsEmpty ? null : SchemaFingerprint.Combine(digests))
            .WithTrackingName(FingerprintStep);

        context.RegisterSourceOutput(fingerprint, static (context, value) =>
        {
            if (value is not null)
            {
                context.AddSource(FingerprintEmitter.HintName, FingerprintEmitter.Emit(value));
            }
        });
    }

    private sealed record SchemaSetResult(EquatableArray<SchemaDiagnostic> ParseDiagnostics, BindResult Bind)
    {
        public EquatableArray<SchemaDiagnostic> AllDiagnostics { get; } =
            ParseDiagnostics.Items.AddRange(Bind.Diagnostics.Items).ToEquatableArray();
    }
}
