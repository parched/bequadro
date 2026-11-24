using Bequadro.Schema.Common;

namespace Bequadro.Schema.Syntax;

/// <param name="Kind">The kind of token.</param>
/// <param name="Text">The token text. For string literals this is the unescaped value.</param>
/// <param name="Start">The offset of the first character.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="DocComment">The <c>///</c> lines directly preceding this token.</param>
internal sealed record Token(TokenKind Kind, string Text, int Start, int Length, EquatableArray<string> DocComment)
{
    public int End => Start + Length;
}
