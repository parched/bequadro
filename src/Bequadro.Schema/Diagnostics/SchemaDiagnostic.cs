using Bequadro.Schema.Common;

namespace Bequadro.Schema.Diagnostics;

/// <summary>
/// An equatable diagnostic for use in pipeline values. The generator converts it to a Roslyn diagnostic when reporting it.
/// </summary>
internal sealed record SchemaDiagnostic(string Id, SourceSpan Span, EquatableArray<string> Arguments)
{
    public string Message =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            DiagnosticCatalog.Get(Id).MessageFormat,
            Arguments.Items.ToArray<object>()
        );

    public override string ToString() => $"{Span}: {Id}: {Message}";
}
