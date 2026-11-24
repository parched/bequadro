using Bequadro.Schema.Common;
using Bequadro.Schema.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Bequadro.Generator.Diagnostics;

/// <summary>
/// Roslyn descriptors for the diagnostics in <see cref="DiagnosticCatalog"/>.
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "Bequadro";

    private static readonly Dictionary<string, DiagnosticDescriptor> ById = DiagnosticCatalog.All.ToDictionary(
        info => info.Id,
        info => new DiagnosticDescriptor(
            info.Id,
            info.Title,
            info.MessageFormat,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true
        ),
        StringComparer.Ordinal
    );

    public static IEnumerable<DiagnosticDescriptor> All => ById.Values;

    public static DiagnosticDescriptor Get(string id) => ById[id];

    public static Diagnostic ToDiagnostic(this SchemaDiagnostic diagnostic) =>
        Diagnostic.Create(
            Get(diagnostic.Id),
            diagnostic.Span.ToLocation(),
            diagnostic.Arguments.Items.ToArray<object>()
        );

    public static Location ToLocation(this SourceSpan span) =>
        Location.Create(
            span.Path,
            new TextSpan(span.Start, span.Length),
            new LinePositionSpan(
                new LinePosition(span.StartLine, span.StartColumn),
                new LinePosition(span.EndLine, span.EndColumn)
            )
        );
}
