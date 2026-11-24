using Bequadro.Schema.Common;

namespace Bequadro.Schema.Syntax;

/// <summary>
/// Maps character offsets to line and column positions.
/// </summary>
internal sealed class LineMap
{
    private readonly string _path;
    private readonly List<int> _lineStarts = [0];

    public LineMap(string path, string text)
    {
        _path = path;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    public SourceSpan Span(int start, int length)
    {
        var (startLine, startColumn) = Position(start);
        var (endLine, endColumn) = Position(start + length);
        return new SourceSpan(_path, start, length, startLine, startColumn, endLine, endColumn);
    }

    public SourceSpan Span(Token token) => Span(token.Start, token.Length);

    public SourceSpan Span(Token first, Token last) => Span(first.Start, last.End - first.Start);

    private (int Line, int Column) Position(int offset)
    {
        var index = _lineStarts.BinarySearch(offset);
        var line = index >= 0 ? index : ~index - 1;
        return (line, offset - _lineStarts[line]);
    }
}
