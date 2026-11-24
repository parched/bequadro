using Bequadro.Schema.Diagnostics;
using Bequadro.Schema.Syntax;

namespace Bequadro.Generator.Tests;

public class LexerAndParserTests
{
    [Fact]
    public void Parse_AllStatementKinds_ProducesSyntax()
    {
        var parsed = Parser.Parse(
            "/a.fbs",
            """
            include "b.fbs";
            native_include "x.h";
            namespace A.B;
            attribute "custom";
            attribute plain;
            table T { x: int = 5 (custom); }
            struct S { y: double; }
            enum E : ubyte { One, Two = 4, }
            union U { T, Alias: S }
            root_type T;
            file_extension "bin";
            """
        );

        parsed.Diagnostics.Should().BeEmpty();
        var file = parsed.File;
        file.Includes.Select(i => i.Path).Should().Equal("b.fbs");
        file.DeclaredAttributes.Select(a => a.Name).Should().Equal("custom", "plain");
        file.Declarations.Select(d => (d.Kind, d.FullName))
            .Should()
            .Equal(
                (DeclarationKind.Table, "A.B.T"),
                (DeclarationKind.Struct, "A.B.S"),
                (DeclarationKind.Enum, "A.B.E"),
                (DeclarationKind.Union, "A.B.U")
            );
        var field = ((ObjectSyntax)file.Declarations[0]).Fields.Single();
        field.Default!.Text.Should().Be("5");
        field.Attributes.Single().Name.Should().Be("custom");
        var union = (UnionSyntax)file.Declarations[3];
        union.Members.Select(m => m.EffectiveLabel).Should().Equal("T", "Alias");
        file.RootTypes.Single().Name.Should().Be("T");
    }

    [Fact]
    public void Parse_Comments_AttachesOnlyDocCommentsDirectlyBeforeElements()
    {
        var parsed = Parser.Parse(
            "/a.fbs",
            """
            // ordinary
            /// Table doc
            /// second line
            table T {
              /* block */
              /// Field doc
              x: int;
              /// Orphaned
              // breaks the run
              y: int;
            }
            """
        );

        var table = (ObjectSyntax)parsed.File.Declarations.Single();
        table.Documentation.Should().Equal("Table doc", "second line");
        table.Fields[0].Documentation.Should().Equal("Field doc");
        table.Fields[1].Documentation.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Vectors_ParsesElementTypes()
    {
        var parsed = Parser.Parse("/a.fbs", "table T { a: [int]; b: [A.B]; c: [[int]]; d: [int:3]; }");

        var types = ((ObjectSyntax)parsed.File.Declarations.Single()).Fields.Select(f => f.Type.ToString());
        types.Should().Equal("[int]", "[A.B]", "[[int]]", "[int:3]");
    }

    [Theory]
    [InlineData("5", nameof(ValueKind.Integer), "5")]
    [InlineData("-5", nameof(ValueKind.Integer), "-5")]
    [InlineData("+0x1F", nameof(ValueKind.Integer), "+0x1F")]
    [InlineData("1.5e-3", nameof(ValueKind.Float), "1.5e-3")]
    [InlineData(".5", nameof(ValueKind.Float), ".5")]
    [InlineData("-inf", nameof(ValueKind.Identifier), "-inf")]
    [InlineData("nan", nameof(ValueKind.Identifier), "nan")]
    [InlineData("Red", nameof(ValueKind.Identifier), "Red")]
    [InlineData("null", nameof(ValueKind.Identifier), "null")]
    public void Parse_DefaultValue_ParsesKindAndText(string literal, string kind, string text)
    {
        var parsed = Parser.Parse("/a.fbs", $"table T {{ x: double = {literal}; }}");

        parsed.Diagnostics.Should().BeEmpty();
        var value = ((ObjectSyntax)parsed.File.Declarations.Single()).Fields.Single().Default!;
        value.Kind.ToString().Should().Be(kind);
        value.Text.Should().Be(text);
    }

    [Fact]
    public void Parse_StringEscapes_Unescapes()
    {
        var parsed = Parser.Parse("/a.fbs", """attribute "a\"b\\cA\n";""");

        parsed.File.DeclaredAttributes.Single().Name.Should().Be("a\"b\\cA\n");
    }

    [Fact]
    public void Parse_ByteOrderMark_IsIgnored()
    {
        Parser.Parse("/a.fbs", "﻿table T {}").Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SyntaxError_ReportsLocationAndRecovers()
    {
        var parsed = Parser.Parse(
            "/a.fbs",
            """
            table T { x int; y: int; }
            table U { z: ; }
            struct S { a: int; }
            """
        );

        parsed
            .Diagnostics.Select(d => (d.Id, d.Span.StartLine, d.Span.StartColumn))
            .Should()
            .Equal((DiagnosticIds.SyntaxError, 0, 12), (DiagnosticIds.SyntaxError, 1, 13));
        parsed.File.Declarations.Select(d => d.Name).Should().Equal("T", "U", "S");
        ((ObjectSyntax)parsed.File.Declarations[0]).Fields.Select(f => f.Name).Should().Equal("y");
    }

    [Fact]
    public void Parse_MissingSemicolonAtEnd_ReportsEndOfFile()
    {
        var parsed = Parser.Parse("/a.fbs", "namespace A");

        parsed.Diagnostics.Single().Message.Should().Contain("end of file");
    }

    [Theory]
    [InlineData("attribute \"abc", DiagnosticIds.UnterminatedLiteral)]
    [InlineData("/* never closed", DiagnosticIds.UnterminatedLiteral)]
    [InlineData("table T { x: int = 12abc; }", DiagnosticIds.InvalidNumericLiteral)]
    [InlineData("table T { x: int = 99999999999999999999999; }", DiagnosticIds.InvalidNumericLiteral)]
    [InlineData("table T { x: int = 0x; }", DiagnosticIds.InvalidNumericLiteral)]
    [InlineData("table T { x: int; } $", DiagnosticIds.SyntaxError)]
    [InlineData("table T {} include \"a.fbs\";", DiagnosticIds.IncludeAfterDeclarations)]
    [InlineData("file_identifier \"ABCD\";", DiagnosticIds.UnsupportedFeature)]
    [InlineData("rpc_service S { M(T):T; }", DiagnosticIds.UnsupportedFeature)]
    [InlineData("union U { A.B: C }", DiagnosticIds.SyntaxError)]
    [InlineData("table T { x: int = -Red; }", DiagnosticIds.SyntaxError)]
    public void Parse_InvalidInput_ReportsDiagnostic(string schema, string expectedId)
    {
        Parser.Parse("/a.fbs", schema).Diagnostics.Select(d => d.Id).Should().Contain(expectedId);
    }

    [Fact]
    public void Parse_DeeplyNestedVectorType_ReportsSyntaxErrorWithoutDeepRecursion()
    {
        var text = "table T { x: " + new string('[', 100_000) + "int" + new string(']', 100_000) + "; }";

        var (_, diagnostics) = GeneratorHarness.Bind(("deep.fbs", text));

        diagnostics.Should().ContainSingle().Which.Id.Should().Be(DiagnosticIds.SyntaxError);
    }

    [Fact]
    public void Parse_RandomText_NeverThrows()
    {
        var random = new Random(42);
        const string alphabet = "table struct enum union include namespace {}()[]:;,=.+-\"/*\n 0123456789abc_xyz";
        for (var i = 0; i < 2000; i++)
        {
            var text = new string(
                Enumerable.Range(0, random.Next(200)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()
            );

            var parse = () => GeneratorHarness.Bind(("random.fbs", text));

            parse.Should().NotThrow(text);
        }
    }

    [Fact]
    public void Parse_EveryEscape_Unescapes()
    {
        var parsed = Parser.Parse("/a.fbs", """attribute "\"\\\/\b\f\n\r\té😀";""");

        parsed.Diagnostics.Should().BeEmpty();
        parsed.File.DeclaredAttributes.Single().Name.Should().Be("\"\\/\b\f\n\r\té😀");
    }

    /// <summary>
    /// flatc rejects a control character in a string, and a <c>\u</c> escape that leaves an unpaired surrogate
    /// (<c>Parser::Next</c>). A line feed ends the string, which is then unterminated.
    /// </summary>
    [Theory]
    [InlineData("attribute \"a«\t»b\";", DiagnosticIds.SyntaxError)]
    [InlineData("attribute \"a«\r»b\";", DiagnosticIds.SyntaxError)]
    [InlineData("attribute \"«\\uD800»\";", DiagnosticIds.SyntaxError)]
    [InlineData("attribute \"«\\uD800»x\";", DiagnosticIds.SyntaxError)]
    [InlineData("attribute \"«\\uD800»\\u0041\";", DiagnosticIds.SyntaxError)]
    [InlineData("attribute \"«\\uDC00»\";", DiagnosticIds.SyntaxError)]
    [InlineData("include \"a«\\x».fbs\";", DiagnosticIds.SyntaxError)]
    [InlineData("attribute «\"ab»\ncd\";", DiagnosticIds.UnterminatedLiteral)]
    public void Parse_InvalidString_ReportsDiagnosticAtMarker(string markedSchema, string id)
    {
        var (schema, start, length) = Unmark(markedSchema);

        var diagnostics = Parser.Parse("/a.fbs", schema).Diagnostics;

        diagnostics
            .Should()
            .Contain(d => d.Id == id && d.Span.Start == start && (length == 0 || d.Span.Length == length), schema);
    }

    /// <summary>A raw unpaired surrogate can't come from UTF-8 text, but is reported like an escaped one.</summary>
    [Fact]
    public void Parse_RawUnpairedSurrogate_ReportsSyntaxError()
    {
        var diagnostics = Parser.Parse("/a.fbs", "attribute \"a" + (char)0xD800 + "\";").Diagnostics;

        diagnostics
            .Should()
            .ContainSingle(d => d.Id == DiagnosticIds.SyntaxError && d.Span.Start == 12 && d.Span.Length == 1);
    }

    /// <summary>flatc lexes a sign as part of the number, so nothing may come between them.</summary>
    [Theory]
    [InlineData("table T { a: int = - «5»; }")]
    [InlineData("table T { a: int = -/*c*/«5»; }")]
    [InlineData("table T { a: double = - «inf»; }")]
    public void Parse_SignSeparatedFromNumber_ReportsSyntaxError(string markedSchema)
    {
        var (schema, start, _) = Unmark(markedSchema);

        Parser
            .Parse("/a.fbs", schema)
            .Diagnostics.Should()
            .ContainSingle(d => d.Id == DiagnosticIds.SyntaxError && d.Span.Start == start);
    }

    [Theory]
    [InlineData("inf", nameof(ValueKind.Identifier))]
    [InlineData("infinity", nameof(ValueKind.Identifier))]
    [InlineData("-Infinity", nameof(ValueKind.Identifier))]
    [InlineData("+NaN", nameof(ValueKind.Identifier))]
    [InlineData("5.", nameof(ValueKind.Float))]
    [InlineData("1e400", nameof(ValueKind.Float))]
    public void Parse_FloatLiteral_IsAccepted(string literal, string kind)
    {
        var parsed = Parser.Parse("/a.fbs", $"table T {{ x: double = {literal}; }}");

        parsed.Diagnostics.Should().BeEmpty();
        ((ObjectSyntax)parsed.File.Declarations.Single()).Fields.Single().Default!.Kind.ToString().Should().Be(kind);
    }

    [Theory]
    [InlineData("table T { x: double = «1e»; }")]
    [InlineData("table T { x: double = «1.5x»; }")]
    [InlineData("table T { x: double = «1e+»; }")]
    public void Parse_MalformedFloat_ReportsInvalidNumericLiteral(string markedSchema)
    {
        var (schema, start, length) = Unmark(markedSchema);

        Parser
            .Parse("/a.fbs", schema)
            .Diagnostics.Should()
            .ContainSingle(d =>
                d.Id == DiagnosticIds.InvalidNumericLiteral && d.Span.Start == start && d.Span.Length == length
            );
    }

    /// <summary>
    /// flatc only accepts a documentation comment that starts a line: "a documentation comment should be on a line on its
    /// own". It counts neither a line feed in a block comment nor a byte order mark as one.
    /// </summary>
    [Theory]
    [InlineData("table T { a: int; «///» trailing\n b: int; }")]
    [InlineData("table T { a: int; «///»/ slashes\n}")]
    [InlineData("table T {} /*\n*/ «///» doc\ntable U {}")]
    [InlineData("﻿«///» doc\ntable T {}")]
    public void Parse_DocCommentNotStartingLine_ReportsSyntaxError(string markedSchema)
    {
        var (schema, start, length) = Unmark(markedSchema);

        Parser
            .Parse("/a.fbs", schema)
            .Diagnostics.Should()
            .ContainSingle(d => d.Id == DiagnosticIds.SyntaxError && d.Span.Start == start && d.Span.Length == length);
    }

    [Fact]
    public void Parse_DocComments_JoinAcrossBlankLinesAndIgnoreLinesOfSlashes()
    {
        var parsed = Parser.Parse("/a.fbs", "/// One\n\n/// Two\ntable T {\n  //////////\n  x: int;\n}");

        parsed.Diagnostics.Should().BeEmpty();
        var table = (ObjectSyntax)parsed.File.Declarations.Single();
        table.Documentation.Should().Equal("One", "Two");
        table.Fields.Single().Documentation.Should().BeEmpty();
    }

    /// <summary>As in flatc, a comment ends at a carriage return as well as a line feed.</summary>
    [Fact]
    public void Parse_CarriageReturn_EndsComment()
    {
        var parsed = Parser.Parse("/a.fbs", "// comment\rtable T {}\r/// doc\rtable U {}");

        parsed.File.Declarations.Select(d => d.Name).Should().Equal("T", "U");
        ((ObjectSyntax)parsed.File.Declarations[1]).Documentation.Should().Equal("doc");
    }

    /// <summary>flatc: "input file is empty".</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  // only a comment\n/* and another */")]
    public void Parse_FileWithoutStatements_ReportsSyntaxError(string schema)
    {
        Parser
            .Parse("/a.fbs", schema)
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be(DiagnosticIds.SyntaxError);
    }

    [Fact]
    public void Parse_EmptyNamespace_ReturnsToRootNamespace()
    {
        var parsed = Parser.Parse("/a.fbs", "namespace A; table T {} namespace; table U {}");

        parsed.Diagnostics.Should().BeEmpty();
        parsed.File.Declarations.Select(d => d.FullName).Should().Equal("A.T", "U");
    }

    [Fact]
    public void Parse_NativeIncludeBeforeInclude_IsAccepted()
    {
        var parsed = Parser.Parse(
            "/a.fbs",
            "native_include \"x.h\"; include \"b.fbs\"; native_include \"y.h\"; table T {}"
        );

        parsed.Diagnostics.Should().BeEmpty();
        parsed.File.Includes.Select(i => i.Path).Should().Equal("b.fbs");
    }

    [Fact]
    public void Parse_NativeIncludeAfterDeclaration_ReportsIncludeAfterDeclarations()
    {
        Parser
            .Parse("/a.fbs", "table T {} native_include \"x.h\";")
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be(DiagnosticIds.IncludeAfterDeclarations);
    }

    /// <summary>
    /// flatc's ParseSingleValue, with no type to go by, accepts only a string or an integer that fits an int as an
    /// attribute value.
    /// </summary>
    [Theory]
    [InlineData("table T (a: «b») {}")]
    [InlineData("table T (a: «true») {}")]
    [InlineData("table T (a: «-inf») {}")]
    [InlineData("table T (a: «1.5») {}")]
    [InlineData("table T (a: «2147483648») {}")]
    [InlineData("table T (a: «-2147483649») {}")]
    public void Parse_AttributeValueNotStringOrInt_ReportsSyntaxError(string markedSchema)
    {
        var (schema, start, length) = Unmark("attribute a; " + markedSchema);

        Parser
            .Parse("/a.fbs", schema)
            .Diagnostics.Should()
            .ContainSingle(d => d.Id == DiagnosticIds.SyntaxError && d.Span.Start == start && d.Span.Length == length);
    }

    [Theory]
    [InlineData("table T (a: \"x\") {}")]
    [InlineData("table T (a: -2147483648) {}")]
    [InlineData("table T (a: 0x7FFFFFFF) {}")]
    public void Parse_AttributeValueStringOrInt_IsAccepted(string schema)
    {
        Parser.Parse("/a.fbs", "attribute a; " + schema).Diagnostics.Should().BeEmpty();
    }

    private static (string Schema, int Start, int Length) Unmark(string markedSchema)
    {
        var start = markedSchema.IndexOf('«', StringComparison.Ordinal);
        var end = markedSchema.IndexOf('»', StringComparison.Ordinal) - 1;
        return (markedSchema.Replace("«", "").Replace("»", ""), start, end - start);
    }
}
