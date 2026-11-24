using Bequadro.Schema.Syntax;

namespace Bequadro.Schema.Semantics;

/// <summary>
/// A declaration the binder registered under its fully qualified name, and the file it is in. It has reference equality,
/// so using one as a dictionary key doesn't hash its whole syntax tree.
/// </summary>
internal sealed class Declaration(DeclarationSyntax syntax, string fileKey)
{
    public DeclarationSyntax Syntax { get; } = syntax;

    /// <summary>The normalized path of the file it is declared in.</summary>
    public string FileKey { get; } = fileKey;

    public string FullName => Syntax.FullName;

    /// <summary>Its position in its file: where its name starts.</summary>
    public int Position => Syntax.NameSpan.Start;

    public bool IsEnumOrUnion => Syntax is EnumSyntax or UnionSyntax;

    public bool IsTableOrStruct => Syntax is ObjectSyntax;
}
