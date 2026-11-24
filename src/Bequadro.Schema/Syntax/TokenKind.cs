namespace Bequadro.Schema.Syntax;

internal enum TokenKind
{
    EndOfFile,
    Identifier,
    StringLiteral,
    IntegerLiteral,
    FloatLiteral,
    OpenBrace,
    CloseBrace,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Colon,
    Semicolon,
    Comma,
    Equals,
    Dot,
    Plus,
    Minus,
    Invalid,
}
