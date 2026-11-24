using Bequadro.Schema.Common;

namespace Bequadro.Schema.Syntax;

/// <summary>
/// The parsed form of one schema file. All syntax nodes are immutable, equatable records.
/// </summary>
internal sealed record SchemaFile(
    string Path,
    EquatableArray<IncludeSyntax> Includes,
    EquatableArray<AttributeDeclarationSyntax> DeclaredAttributes,
    EquatableArray<DeclarationSyntax> Declarations,
    EquatableArray<RootTypeSyntax> RootTypes
);

internal sealed record IncludeSyntax(string Path, SourceSpan Span);

/// <summary>An <c>attribute</c> statement. <c>Span</c> is the attribute name.</summary>
internal sealed record AttributeDeclarationSyntax(string Name, SourceSpan Span);

internal sealed record RootTypeSyntax(string Namespace, string Name, SourceSpan Span);

internal enum ValueKind
{
    Integer,
    Float,
    Identifier,
    String,
}

/// <summary>
/// A literal or identifier value. <see cref="Text"/> includes any leading sign.
/// </summary>
internal sealed record ValueSyntax(ValueKind Kind, string Text, SourceSpan Span);

internal sealed record AttributeSyntax(string Name, ValueSyntax? Value, SourceSpan Span);

/// <summary>
/// A type reference: a named type (scalar or declaration), a vector, or a fixed-size array.
/// </summary>
internal sealed record TypeSyntax(string? Name, TypeSyntax? Element, string? ArrayLength, SourceSpan Span)
{
    public bool IsVector => Element is not null && ArrayLength is null;

    public bool IsFixedArray => ArrayLength is not null;

    public override string ToString() =>
        Element is null ? Name ?? string.Empty
        : ArrayLength is null ? $"[{Element}]"
        : $"[{Element}:{ArrayLength}]";
}

internal enum DeclarationKind
{
    Table,
    Struct,
    Enum,
    Union,
}

internal abstract record DeclarationSyntax(
    DeclarationKind Kind,
    string Namespace,
    string Name,
    SourceSpan NameSpan,
    EquatableArray<AttributeSyntax> Attributes,
    EquatableArray<string> Documentation
)
{
    public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;
}

internal sealed record FieldSyntax(
    string Name,
    SourceSpan NameSpan,
    TypeSyntax Type,
    ValueSyntax? Default,
    EquatableArray<AttributeSyntax> Attributes,
    EquatableArray<string> Documentation
);

/// <summary>A table or struct.</summary>
internal sealed record ObjectSyntax(
    DeclarationKind Kind,
    string Namespace,
    string Name,
    SourceSpan NameSpan,
    EquatableArray<AttributeSyntax> Attributes,
    EquatableArray<string> Documentation,
    EquatableArray<FieldSyntax> Fields
) : DeclarationSyntax(Kind, Namespace, Name, NameSpan, Attributes, Documentation);

internal sealed record EnumValueSyntax(
    string Name,
    SourceSpan NameSpan,
    ValueSyntax? Value,
    EquatableArray<AttributeSyntax> Attributes,
    EquatableArray<string> Documentation
);

internal sealed record EnumSyntax(
    string Namespace,
    string Name,
    SourceSpan NameSpan,
    EquatableArray<AttributeSyntax> Attributes,
    EquatableArray<string> Documentation,
    TypeSyntax? UnderlyingType,
    EquatableArray<EnumValueSyntax> Values
) : DeclarationSyntax(DeclarationKind.Enum, Namespace, Name, NameSpan, Attributes, Documentation);

/// <summary>A union member. <c>Label</c> is null when the member is written as just a type name.</summary>
internal sealed record UnionMemberSyntax(
    string? Label,
    TypeSyntax Type,
    ValueSyntax? Value,
    EquatableArray<AttributeSyntax> Attributes,
    EquatableArray<string> Documentation,
    SourceSpan Span
)
{
    public string EffectiveLabel => Label ?? (Type.Name is { } name ? name.Substring(name.LastIndexOf('.') + 1) : "");

    /// <summary>
    /// The label flatc gives the member: <see cref="Label"/>, or else the type name as written with <c>_</c> for <c>.</c>
    /// (<c>union_value_namespacing</c>, which is on by default).
    /// </summary>
    public string FlatcLabel => Label ?? Type.Name?.Replace('.', '_') ?? "";
}

internal sealed record UnionSyntax(
    string Namespace,
    string Name,
    SourceSpan NameSpan,
    EquatableArray<AttributeSyntax> Attributes,
    EquatableArray<string> Documentation,
    EquatableArray<UnionMemberSyntax> Members
) : DeclarationSyntax(DeclarationKind.Union, Namespace, Name, NameSpan, Attributes, Documentation);
