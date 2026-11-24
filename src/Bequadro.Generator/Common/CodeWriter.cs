using System.Text;
using Bequadro.Schema.Common;

namespace Bequadro.Generator.Common;

/// <summary>
/// Builds indented C# source text.
/// </summary>
internal sealed class CodeWriter
{
    private const string IndentUnit = "    ";

    private readonly StringBuilder _builder = new();
    private int _indent;

    public void Line(string text = "")
    {
        if (text.Length > 0)
        {
            for (var i = 0; i < _indent; i++)
            {
                _builder.Append(IndentUnit);
            }

            _builder.Append(text);
        }

        _builder.Append('\n');
    }

    public void OpenBlock(string header)
    {
        Line(header);
        Line("{");
        _indent++;
    }

    public void CloseBlock(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
    }

    public void Indent() => _indent++;

    public void Unindent() => _indent--;

    /// <summary>
    /// Writes a summary from schema documentation lines, noting the schema name of an element that was renamed (NAM-002).
    /// </summary>
    public void DocComment(EquatableArray<string> lines, string? renamedFrom = null)
    {
        if (lines.Count == 0 && renamedFrom is null)
        {
            return;
        }

        Line("/// <summary>");
        foreach (var line in lines.SelectMany(line => line.Split(LineTerminators)))
        {
            var text = EscapeXml(line.Trim());
            Line(text.Length == 0 ? "///" : "/// " + text);
        }

        if (renamedFrom is not null)
        {
            Line($"/// Schema name: <c>{EscapeXml(renamedFrom)}</c>.");
        }

        Line("/// </summary>");
    }

    public override string ToString() => _builder.ToString();

    /// <summary>
    /// The characters that end a line in C#, besides <c>\n</c>, which schema doc comments end at. A doc comment line
    /// containing one continues as code, so it becomes a line of its own.
    /// </summary>
    private static readonly char[] LineTerminators = ['\r', '\u0085', '\u2028', '\u2029'];

    /// <summary>
    /// Escapes the XML markup characters, and replaces with U+FFFD what XML 1.0 can't contain even escaped: control
    /// characters other than tab, unpaired surrogates, U+FFFE and U+FFFF.
    /// </summary>
    private static string EscapeXml(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case < ' ' and not '\t':
                case '\uFFFE' or '\uFFFF':
                    builder.Append('\uFFFD');
                    break;
                case var _ when char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]):
                    builder.Append(c).Append(text[++i]);
                    break;
                case var _ when char.IsSurrogate(c):
                    builder.Append('\uFFFD');
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
