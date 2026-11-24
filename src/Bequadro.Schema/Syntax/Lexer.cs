using System.Globalization;
using System.Text;
using Bequadro.Schema.Common;
using Bequadro.Schema.Diagnostics;

namespace Bequadro.Schema.Syntax;

/// <summary>
/// Converts schema text into tokens. Comments are skipped, except that runs of <c>///</c> comments
/// are attached to the following token as documentation. What it accepts follows <c>flatc</c>'s lexer
/// (<c>Parser::Next</c> in <c>idl_parser.cpp</c>), except that it supports fewer escapes and only <c>"</c> strings.
/// </summary>
internal sealed class Lexer
{
    private readonly string _text;
    private readonly LineMap _lineMap;
    private readonly List<SchemaDiagnostic> _diagnostics;
    private readonly List<string> _pendingDocComment = [];
    private int _position;

    // Whether a line feed has been skipped since the previous token, or no token has been read and the text has no byte
    // order mark. flatc only accepts a documentation comment there: it doesn't count a line feed inside a block comment.
    private bool _atLineStart = true;

    public Lexer(string text, LineMap lineMap, List<SchemaDiagnostic> diagnostics)
    {
        _text = text;
        _lineMap = lineMap;
        _diagnostics = diagnostics;
        if (_text.Length > 0 && _text[0] == '﻿')
        {
            _position = 1;
            _atLineStart = false;
        }
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        Token token;
        do
        {
            token = Next();
            if (token.Kind != TokenKind.Invalid)
            {
                tokens.Add(token);
            }
        } while (token.Kind != TokenKind.EndOfFile);

        return tokens;
    }

    private Token Next()
    {
        SkipTrivia();

        if (_position >= _text.Length)
        {
            return Create(TokenKind.EndOfFile, string.Empty, _position, 0);
        }

        var start = _position;
        var c = _text[_position];

        if (IsIdentifierStart(c))
        {
            while (_position < _text.Length && IsIdentifierPart(_text[_position]))
            {
                _position++;
            }

            return Create(TokenKind.Identifier, _text.Substring(start, _position - start), start, _position - start);
        }

        if (IsDigit(c) || (c == '.' && IsDigit(Peek(1))))
        {
            return LexNumber();
        }

        if (c == '"')
        {
            return LexString();
        }

        _position++;
        var kind = c switch
        {
            '{' => TokenKind.OpenBrace,
            '}' => TokenKind.CloseBrace,
            '(' => TokenKind.OpenParen,
            ')' => TokenKind.CloseParen,
            '[' => TokenKind.OpenBracket,
            ']' => TokenKind.CloseBracket,
            ':' => TokenKind.Colon,
            ';' => TokenKind.Semicolon,
            ',' => TokenKind.Comma,
            '=' => TokenKind.Equals,
            '.' => TokenKind.Dot,
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            _ => TokenKind.Invalid,
        };

        if (kind == TokenKind.Invalid)
        {
            Report(DiagnosticIds.SyntaxError, start, 1, $"unexpected character '{c}'");
        }

        return Create(kind, c.ToString(), start, 1);
    }

    private void SkipTrivia()
    {
        while (_position < _text.Length)
        {
            var c = _text[_position];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                _atLineStart |= c == '\n';
                _position++;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                SkipLineComment();
            }
            else if (c == '/' && Peek(1) == '*')
            {
                var start = _position;
                var end = _text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    Report(DiagnosticIds.UnterminatedLiteral, start, 2, "block comment");
                    _position = _text.Length;
                }
                else
                {
                    _position = end + 2;
                }

                _pendingDocComment.Clear();
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>
    /// Skips a <c>//</c> comment, which ends at a line feed or carriage return, as in flatc. A comment starting with
    /// <c>///</c> is a documentation comment to flatc, which rejects one that doesn't start a line. Bequadro rejects it
    /// too, but takes only <c>///</c> followed by anything other than <c>/</c> as documentation, so that lines of
    /// slashes aren't documentation. Any other comment ends the run of documentation lines.
    /// </summary>
    private void SkipLineComment()
    {
        var start = _position;
        while (_position < _text.Length && _text[_position] is not '\n' and not '\r')
        {
            _position++;
        }

        var length = _position - start;
        if (length < 3 || _text[start + 2] != '/')
        {
            _pendingDocComment.Clear();
            return;
        }

        if (!_atLineStart)
        {
            Report(DiagnosticIds.SyntaxError, start, 3, "a documentation comment must be on a line of its own");
        }

        if (length > 3 && _text[start + 3] == '/')
        {
            _pendingDocComment.Clear();
            return;
        }

        _pendingDocComment.Add(_text.Substring(start + 3, _position - start - 3).Trim());
    }

    private Token LexNumber()
    {
        var start = _position;
        var isFloat = false;
        var wellFormed = true;

        if (_text[_position] == '0' && Peek(1) is 'x' or 'X')
        {
            _position += 2;
            while (_position < _text.Length && Uri.IsHexDigit(_text[_position]))
            {
                _position++;
            }

            wellFormed = _position > start + 2;
        }
        else
        {
            SkipDigits();
            if (_position < _text.Length && _text[_position] == '.')
            {
                isFloat = true;
                _position++;
                SkipDigits();
            }

            if (_position < _text.Length && _text[_position] is 'e' or 'E')
            {
                isFloat = true;
                _position++;
                if (_position < _text.Length && _text[_position] is '+' or '-')
                {
                    _position++;
                }

                var exponentStart = _position;
                SkipDigits();
                wellFormed = _position > exponentStart;
            }
        }

        // Swallow trailing identifier characters so "12abc" is reported as one malformed number
        while (_position < _text.Length && IsIdentifierPart(_text[_position]))
        {
            wellFormed = false;
            _position++;
        }

        var text = _text.Substring(start, _position - start);
        var kind = isFloat ? TokenKind.FloatLiteral : TokenKind.IntegerLiteral;

        // A float's syntax is checked here, not by double.TryParse, which rejects values too large for a double on
        // .NET Framework (Visual Studio's compiler host) but not on .NET. Such a value is infinity (Constants.ParseFloat).
        if (!wellFormed || (!isFloat && !IsIntegerInRange(text)))
        {
            Report(DiagnosticIds.InvalidNumericLiteral, start, text.Length, text);
        }

        return Create(kind, text, start, text.Length);
    }

    private void SkipDigits()
    {
        while (_position < _text.Length && IsDigit(_text[_position]))
        {
            _position++;
        }
    }

    private static bool IsIntegerInRange(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Lexes a string. As in flatc, it may not contain a control character, and <c>\u</c> escapes must not leave an
    /// unpaired surrogate.
    /// </summary>
    private Token LexString()
    {
        var start = _position;
        _position++;
        var value = new StringBuilder();
        var highSurrogateStart = -1;
        var highSurrogate = '\0';

        while (true)
        {
            if (_position >= _text.Length || _text[_position] == '\n')
            {
                Report(DiagnosticIds.UnterminatedLiteral, start, _position - start, "string literal");
                break;
            }

            var characterStart = _position;
            var c = _text[_position++];
            var isUnicodeEscape = c == '\\' && Peek(0) == 'u';
            if (highSurrogateStart >= 0 && !isUnicodeEscape)
            {
                ReportUnpairedSurrogate(highSurrogateStart, 6);
                highSurrogateStart = -1;
            }

            if (c == '"')
            {
                break;
            }

            if (c < ' ')
            {
                Report(DiagnosticIds.SyntaxError, characterStart, 1, "a string can't contain a control character");
                continue;
            }

            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && char.IsLowSurrogate(Peek(0)))
                {
                    value.Append(c).Append(_text[_position++]);
                }
                else
                {
                    ReportUnpairedSurrogate(characterStart, 1);
                }

                continue;
            }

            if (c != '\\')
            {
                value.Append(c);
                continue;
            }

            var escaped = _position < _text.Length ? _text[_position++] : '\0';
            switch (escaped)
            {
                case '"':
                case '\\':
                case '/':
                    value.Append(escaped);
                    break;
                case 'b':
                    value.Append('\b');
                    break;
                case 'f':
                    value.Append('\f');
                    break;
                case 'n':
                    value.Append('\n');
                    break;
                case 'r':
                    value.Append('\r');
                    break;
                case 't':
                    value.Append('\t');
                    break;
                case 'u'
                    when _position + 4 <= _text.Length
                        && int.TryParse(
                            _text.Substring(_position, 4),
                            NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture,
                            out var codePoint
                        ):
                    _position += 4;
                    var unit = (char)codePoint;
                    if (char.IsHighSurrogate(unit))
                    {
                        // Kept back until the low surrogate that must follow as the next escape
                        if (highSurrogateStart >= 0)
                        {
                            ReportUnpairedSurrogate(highSurrogateStart, 6);
                        }

                        highSurrogateStart = characterStart;
                        highSurrogate = unit;
                    }
                    else if (char.IsLowSurrogate(unit) && highSurrogateStart < 0)
                    {
                        ReportUnpairedSurrogate(characterStart, 6);
                    }
                    else
                    {
                        if (highSurrogateStart >= 0)
                        {
                            if (!char.IsLowSurrogate(unit))
                            {
                                ReportUnpairedSurrogate(highSurrogateStart, 6);
                            }
                            else
                            {
                                value.Append(highSurrogate);
                            }

                            highSurrogateStart = -1;
                        }

                        value.Append(unit);
                    }

                    break;
                default:
                    Report(
                        DiagnosticIds.SyntaxError,
                        characterStart,
                        _position - characterStart,
                        "invalid escape sequence"
                    );
                    break;
            }
        }

        return Create(TokenKind.StringLiteral, value.ToString(), start, _position - start);
    }

    private void ReportUnpairedSurrogate(int start, int length) =>
        Report(DiagnosticIds.SyntaxError, start, length, "a string can't contain an unpaired surrogate");

    private Token Create(TokenKind kind, string text, int start, int length)
    {
        var doc = _pendingDocComment.Count > 0 ? _pendingDocComment.ToEquatableArray() : EquatableArray<string>.Empty;
        _pendingDocComment.Clear();
        _atLineStart = false;
        return new Token(kind, text, start, length, doc);
    }

    private char Peek(int offset) =>
        _position + offset >= 0 && _position + offset < _text.Length ? _text[_position + offset] : '\0';

    private void Report(string id, int start, int length, string argument) =>
        _diagnostics.Add(new SchemaDiagnostic(id, _lineMap.Span(start, length), EquatableArray.Create(argument)));

    private static bool IsIdentifierStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || IsDigit(c);

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
