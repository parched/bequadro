using System.Text;

namespace Bequadro.Cli;

/// <summary>
/// Builds indented source text with LF line endings.
/// </summary>
internal sealed class SourceWriter(string indentUnit)
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public void Line(string text = "")
    {
        if (text.Length > 0)
        {
            for (var i = 0; i < _indent; i++)
            {
                _builder.Append(indentUnit);
            }

            _builder.Append(text);
        }

        _builder.Append('\n');
    }

    public void Indent() => _indent++;

    public void Unindent() => _indent--;

    /// <summary>
    /// Writes <paramref name="header"/>, then indents until the matching <see cref="Close"/>.
    /// </summary>
    public void Open(string header)
    {
        Line(header);
        _indent++;
    }

    public void Close(string footer)
    {
        _indent--;
        Line(footer);
    }

    public override string ToString() => _builder.ToString();
}
