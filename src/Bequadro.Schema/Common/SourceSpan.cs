namespace Bequadro.Schema.Common;

/// <summary>
/// A location in a schema file. Equatable replacement for Roslyn's <c>Location</c> in pipeline values.
/// Lines and columns are zero-based.
/// </summary>
internal sealed record SourceSpan(
    string Path,
    int Start,
    int Length,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn
)
{
    public override string ToString() => $"{Path}({StartLine + 1},{StartColumn + 1})";
}
