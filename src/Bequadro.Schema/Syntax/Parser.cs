using System.Text;
using Bequadro.Schema.Common;
using Bequadro.Schema.Diagnostics;
using Bequadro.Schema.Semantics;

namespace Bequadro.Schema.Syntax;

/// <summary>
/// The result of parsing one schema file.
/// </summary>
/// <param name="File">The syntax.</param>
/// <param name="Diagnostics">The lexer's and parser's diagnostics.</param>
/// <param name="Digest">The digest of the file's tokens, for the schema set's fingerprint (SER-015).</param>
internal sealed record ParsedFile(SchemaFile File, EquatableArray<SchemaDiagnostic> Diagnostics, string Digest);

/// <summary>
/// A recursive descent parser for the supported subset of the FlatBuffers schema language.
/// It recovers from syntax errors at statement and field boundaries so that one file can report several errors.
/// </summary>
internal sealed class Parser
{
    private readonly LineMap _lineMap;
    private readonly List<Token> _tokens;
    private readonly List<SchemaDiagnostic> _diagnostics;
    private readonly List<IncludeSyntax> _includes = [];
    private readonly List<AttributeDeclarationSyntax> _attributes = [];
    private readonly List<DeclarationSyntax> _declarations = [];
    private readonly List<RootTypeSyntax> _rootTypes = [];
    private string _namespace = string.Empty;
    private bool _seenNonInclude;
    private int _position;

    private Parser(string path, string text)
    {
        _lineMap = new LineMap(path, text);
        _diagnostics = [];
        _tokens = new Lexer(text, _lineMap, _diagnostics).Tokenize();
    }

    public static ParsedFile Parse(string path, string text)
    {
        var parser = new Parser(path, text);
        parser.ParseFile();
        var file = new SchemaFile(
            path,
            parser._includes.ToEquatableArray(),
            parser._attributes.ToEquatableArray(),
            parser._declarations.ToEquatableArray(),
            parser._rootTypes.ToEquatableArray()
        );
        return new ParsedFile(file, parser._diagnostics.ToEquatableArray(), SchemaFingerprint.OfFile(parser._tokens));
    }

    private Token Current => _tokens[_position];

    private void ParseFile()
    {
        // flatc rejects a file without statements: "input file is empty"
        if (Current.Kind == TokenKind.EndOfFile)
        {
            Report(
                DiagnosticIds.SyntaxError,
                _lineMap.Span(Current),
                "a schema file must contain at least one statement"
            );
        }

        while (Current.Kind != TokenKind.EndOfFile)
        {
            var start = _position;
            try
            {
                ParseStatement();
            }
            catch (SyntaxException)
            {
                RecoverAtStatement(start);
            }
        }
    }

    private void ParseStatement()
    {
        var keyword = Current;
        if (keyword.Kind != TokenKind.Identifier)
        {
            throw Error(keyword, "expected a statement");
        }

        if (keyword.Text is "include" or "native_include")
        {
            ParseInclude();
            return;
        }

        _seenNonInclude = true;
        switch (keyword.Text)
        {
            case "namespace":
                Advance();
                // "namespace;" returns to the root namespace, as in flatc
                _namespace = Current.Kind == TokenKind.Semicolon ? string.Empty : ParseQualifiedName().Name;
                Expect(TokenKind.Semicolon, "';'");
                break;
            case "attribute":
                Advance();
                var name = Current.Kind is TokenKind.StringLiteral or TokenKind.Identifier
                    ? Advance()
                    : throw Error(Current, "expected an attribute name");
                Expect(TokenKind.Semicolon, "';'");
                _attributes.Add(new AttributeDeclarationSyntax(name.Text, _lineMap.Span(name)));
                break;
            case "table":
                _declarations.Add(ParseObject(DeclarationKind.Table));
                break;
            case "struct":
                _declarations.Add(ParseObject(DeclarationKind.Struct));
                break;
            case "enum":
                _declarations.Add(ParseEnum());
                break;
            case "union":
                _declarations.Add(ParseUnion());
                break;
            case "root_type":
                Advance();
                var (rootName, rootSpan) = ParseQualifiedName();
                Expect(TokenKind.Semicolon, "';'");
                _rootTypes.Add(new RootTypeSyntax(_namespace, rootName, rootSpan));
                break;
            case "file_identifier":
                Advance();
                Expect(TokenKind.StringLiteral, "a string");
                Expect(TokenKind.Semicolon, "';'");
                ReportUnsupported(keyword, "file_identifier");
                break;
            case "file_extension":
                Advance();
                Expect(TokenKind.StringLiteral, "a string");
                Expect(TokenKind.Semicolon, "';'");
                break;
            case "rpc_service":
                ReportUnsupported(keyword, "rpc_service");
                SkipBlockStatement();
                break;
            default:
                throw Error(keyword, $"unexpected '{keyword.Text}', expected a statement");
        }
    }

    /// <summary>
    /// Parses an <c>include</c> or <c>native_include</c> statement. Both must come before every other statement in flatc.
    /// </summary>
    private void ParseInclude()
    {
        var keyword = Advance();
        if (_seenNonInclude)
        {
            Report(DiagnosticIds.IncludeAfterDeclarations, _lineMap.Span(keyword));
        }

        var path = Expect(TokenKind.StringLiteral, "a string");
        Expect(TokenKind.Semicolon, "';'");
        if (keyword.Text == "include")
        {
            _includes.Add(new IncludeSyntax(path.Text, _lineMap.Span(keyword, path)));
        }
    }

    private ObjectSyntax ParseObject(DeclarationKind kind)
    {
        var keyword = Advance();
        var name = Expect(TokenKind.Identifier, "a name");
        var attributes = ParseAttributes();
        Expect(TokenKind.OpenBrace, "'{'");

        var fields = new List<FieldSyntax>();
        while (Current.Kind is not TokenKind.CloseBrace and not TokenKind.EndOfFile)
        {
            var start = _position;
            try
            {
                fields.Add(ParseField());
            }
            catch (SyntaxException)
            {
                RecoverAtMember(start);
            }
        }

        Expect(TokenKind.CloseBrace, "'}'");
        return new ObjectSyntax(
            kind,
            _namespace,
            name.Text,
            _lineMap.Span(name),
            attributes,
            keyword.DocComment,
            fields.ToEquatableArray()
        );
    }

    private FieldSyntax ParseField()
    {
        var name = Expect(TokenKind.Identifier, "a field name");
        Expect(TokenKind.Colon, "':'");
        var type = ParseType();
        ValueSyntax? defaultValue = null;
        if (Current.Kind == TokenKind.Equals)
        {
            Advance();
            defaultValue = ParseValue();
        }

        var attributes = ParseAttributes();
        Expect(TokenKind.Semicolon, "';'");
        return new FieldSyntax(name.Text, _lineMap.Span(name), type, defaultValue, attributes, name.DocComment);
    }

    /// <summary>
    /// Parses a type. A vector of vectors is still parsed, so the binder can report it (BQ2015), but deeper nesting is a
    /// syntax error. That keeps the recursion shallow whatever the input.
    /// </summary>
    private TypeSyntax ParseType(int depth = 0)
    {
        if (Current.Kind == TokenKind.OpenBracket)
        {
            if (depth == 2)
            {
                throw Error(Current, "expected a type name, because vectors can't be nested");
            }

            var open = Advance();
            var element = ParseType(depth + 1);
            string? length = null;
            if (Current.Kind == TokenKind.Colon)
            {
                Advance();
                length = Expect(TokenKind.IntegerLiteral, "an array length").Text;
            }

            var close = Expect(TokenKind.CloseBracket, "']'");
            return new TypeSyntax(null, element, length, _lineMap.Span(open, close));
        }

        var (name, span) = ParseQualifiedName();
        return new TypeSyntax(name, null, null, span);
    }

    private EnumSyntax ParseEnum()
    {
        var keyword = Advance();
        var name = Expect(TokenKind.Identifier, "a name");
        TypeSyntax? underlying = null;
        if (Current.Kind == TokenKind.Colon)
        {
            Advance();
            underlying = ParseType();
        }

        var attributes = ParseAttributes();
        Expect(TokenKind.OpenBrace, "'{'");

        var values = new List<EnumValueSyntax>();
        while (Current.Kind != TokenKind.CloseBrace)
        {
            var valueName = Expect(TokenKind.Identifier, "an enum value name");
            ValueSyntax? value = null;
            if (Current.Kind == TokenKind.Equals)
            {
                Advance();
                value = ParseValue();
            }

            var valueAttributes = ParseAttributes();
            values.Add(
                new EnumValueSyntax(
                    valueName.Text,
                    _lineMap.Span(valueName),
                    value,
                    valueAttributes,
                    valueName.DocComment
                )
            );

            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            Advance();
        }

        Expect(TokenKind.CloseBrace, "'}'");
        return new EnumSyntax(
            _namespace,
            name.Text,
            _lineMap.Span(name),
            attributes,
            keyword.DocComment,
            underlying,
            values.ToEquatableArray()
        );
    }

    private UnionSyntax ParseUnion()
    {
        var keyword = Advance();
        var name = Expect(TokenKind.Identifier, "a name");
        var attributes = ParseAttributes();
        Expect(TokenKind.OpenBrace, "'{'");

        var members = new List<UnionMemberSyntax>();
        while (Current.Kind != TokenKind.CloseBrace)
        {
            var first = Current;
            var (memberName, memberSpan) = ParseQualifiedName();
            string? label = null;
            TypeSyntax type;
            if (Current.Kind == TokenKind.Colon)
            {
                if (memberName.Contains('.'))
                {
                    throw Error(first, "a union member label cannot be qualified");
                }

                Advance();
                label = memberName;
                type = ParseType();
            }
            else
            {
                type = new TypeSyntax(memberName, null, null, memberSpan);
            }

            ValueSyntax? value = null;
            if (Current.Kind == TokenKind.Equals)
            {
                Advance();
                value = ParseValue();
            }

            var memberAttributes = ParseAttributes();
            var last = _tokens[_position - 1];
            members.Add(
                new UnionMemberSyntax(
                    label,
                    type,
                    value,
                    memberAttributes,
                    first.DocComment,
                    _lineMap.Span(first, last)
                )
            );

            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            Advance();
        }

        Expect(TokenKind.CloseBrace, "'}'");
        return new UnionSyntax(
            _namespace,
            name.Text,
            _lineMap.Span(name),
            attributes,
            keyword.DocComment,
            members.ToEquatableArray()
        );
    }

    private EquatableArray<AttributeSyntax> ParseAttributes()
    {
        if (Current.Kind != TokenKind.OpenParen)
        {
            return EquatableArray<AttributeSyntax>.Empty;
        }

        Advance();
        var attributes = new List<AttributeSyntax>();
        while (Current.Kind != TokenKind.CloseParen)
        {
            var name = Current.Kind is TokenKind.Identifier or TokenKind.StringLiteral
                ? Advance()
                : throw Error(Current, "expected an attribute name");
            ValueSyntax? value = null;
            if (Current.Kind == TokenKind.Colon)
            {
                Advance();
                value = ParseValue();
                CheckAttributeValue(value);
            }

            var last = _tokens[_position - 1];
            attributes.Add(new AttributeSyntax(name.Text, value, _lineMap.Span(name, last)));

            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            Advance();
        }

        Expect(TokenKind.CloseParen, "')'");
        return attributes.ToEquatableArray();
    }

    /// <summary>
    /// flatc accepts only a string or an integer that fits an <c>int</c> as an attribute value (<c>ParseSingleValue</c>, with
    /// no type). The attribute is kept, because user attributes have no effect anyway.
    /// </summary>
    private void CheckAttributeValue(ValueSyntax value)
    {
        var valid = value.Kind switch
        {
            ValueKind.String => true,
            ValueKind.Integer => Constants.TryParseInteger(value.Text, out var number)
                && number is >= int.MinValue and <= int.MaxValue,
            _ => false,
        };

        if (!valid)
        {
            Report(
                DiagnosticIds.SyntaxError,
                value.Span,
                $"expected a string or an integer from {int.MinValue} to {int.MaxValue} as the attribute value, but found '{value.Text}'"
            );
        }
    }

    private ValueSyntax ParseValue()
    {
        var first = Current;
        var sign = string.Empty;
        if (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            sign = Advance().Text;
        }

        var token = Current;
        if (sign.Length > 0 && token.Start != first.End)
        {
            // flatc lexes the sign as part of the number, so it must be adjacent
            throw Error(token, "expected a number directly after the sign");
        }

        var kind = token.Kind switch
        {
            TokenKind.IntegerLiteral => ValueKind.Integer,
            TokenKind.FloatLiteral => ValueKind.Float,
            TokenKind.Identifier => ValueKind.Identifier,
            TokenKind.StringLiteral when sign.Length == 0 => ValueKind.String,
            _ => throw Error(token, "expected a value"),
        };

        if (kind == ValueKind.Identifier && sign.Length > 0 && !IsSignableIdentifier(token.Text))
        {
            throw Error(token, "expected a number after the sign");
        }

        Advance();
        return new ValueSyntax(kind, sign + token.Text, _lineMap.Span(first, token));
    }

    /// <summary>
    /// The identifiers that flatc reads as floating-point constants, through <c>strtod</c>, which ignores case.
    /// </summary>
    private static bool IsSignableIdentifier(string text) => Constants.IsFloatConstantName(text);

    private (string Name, SourceSpan Span) ParseQualifiedName()
    {
        var first = Expect(TokenKind.Identifier, "a name");
        if (Current.Kind != TokenKind.Dot)
        {
            return (first.Text, _lineMap.Span(first, first));
        }

        var last = first;
        var name = new StringBuilder(first.Text);
        while (Current.Kind == TokenKind.Dot)
        {
            Advance();
            last = Expect(TokenKind.Identifier, "a name");
            name.Append('.').Append(last.Text);
        }

        return (name.ToString(), _lineMap.Span(first, last));
    }

    private void SkipBlockStatement()
    {
        Advance();
        while (Current.Kind is not TokenKind.OpenBrace and not TokenKind.EndOfFile)
        {
            Advance();
        }

        var depth = 0;
        while (Current.Kind != TokenKind.EndOfFile)
        {
            var token = Advance();
            if (token.Kind == TokenKind.OpenBrace)
            {
                depth++;
            }
            else if (token.Kind == TokenKind.CloseBrace && --depth == 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Skips to the end of the current top-level statement: a ';' outside braces, or the '}' closing the first brace.
    /// </summary>
    private void RecoverAtStatement(int start)
    {
        if (_position == start)
        {
            Advance();
        }

        var depth = 0;
        for (var i = start; i < _position; i++)
        {
            depth += _tokens[i].Kind switch
            {
                TokenKind.OpenBrace => 1,
                TokenKind.CloseBrace => -1,
                _ => 0,
            };
        }

        if (depth <= 0 && _tokens[_position - 1].Kind is TokenKind.Semicolon or TokenKind.CloseBrace)
        {
            return;
        }

        while (Current.Kind != TokenKind.EndOfFile)
        {
            var token = Advance();
            if (token.Kind == TokenKind.OpenBrace)
            {
                depth++;
            }
            else if (token.Kind == TokenKind.CloseBrace && --depth <= 0)
            {
                return;
            }
            else if (token.Kind == TokenKind.Semicolon && depth <= 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Skips to the end of the current field: past the next ';', or up to (not past) the enclosing '}'.
    /// </summary>
    private void RecoverAtMember(int start)
    {
        if (_position == start && Current.Kind != TokenKind.CloseBrace)
        {
            Advance();
        }

        while (Current.Kind is not TokenKind.EndOfFile and not TokenKind.CloseBrace)
        {
            if (Advance().Kind == TokenKind.Semicolon)
            {
                return;
            }
        }
    }

    private Token Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return token;
    }

    private Token Expect(TokenKind kind, string description)
    {
        if (Current.Kind != kind)
        {
            throw Error(Current, $"expected {description}");
        }

        return Advance();
    }

    private SyntaxException Error(Token token, string message)
    {
        var found = token.Kind == TokenKind.EndOfFile ? "end of file" : $"'{token.Text}'";
        var full = message.Contains("unexpected") ? message : $"{message} but found {found}";
        Report(DiagnosticIds.SyntaxError, _lineMap.Span(token), full);
        return new SyntaxException();
    }

    private void ReportUnsupported(Token token, string feature) =>
        Report(DiagnosticIds.UnsupportedFeature, _lineMap.Span(token), $"'{feature}'");

    private void Report(string id, SourceSpan span, params string[] arguments) =>
        _diagnostics.Add(new SchemaDiagnostic(id, span, arguments.ToEquatableArray()));

    /// <summary>
    /// Unwinds to the nearest recovery point after a syntax error has been reported.
    /// </summary>
    private sealed class SyntaxException : Exception;
}
