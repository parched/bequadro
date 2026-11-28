using Bequadro.Schema.Common;

namespace Bequadro.Schema.Semantics;

internal enum ScalarKind
{
    Bool,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
}

internal enum TypeKind
{
    Scalar,
    String,
    Enum,
    Struct,
    Table,
    Union,
    Vector,
}

/// <summary>
/// A resolved type. Only the information that code generation needs is kept, so that unrelated changes to a
/// referenced declaration don't invalidate the cached output of the referencing declaration.
/// </summary>
/// <param name="Kind">The kind of type.</param>
/// <param name="Scalar">For scalars the scalar kind; for enums the underlying kind.</param>
/// <param name="FullName">
/// The fully qualified name for enums, structs, tables and unions: the schema name from the binder, the target's name
/// after renaming (<see cref="Naming.ModelRenamer"/>).
/// </param>
/// <param name="Element">The element type of a vector.</param>
/// <param name="StructSize">The size in bytes of a struct.</param>
/// <param name="StructAlignment">The alignment in bytes of a struct.</param>
/// <param name="UnionAllowsNone">For unions, whether <c>NONE</c> is a valid value (no <c>bequadro_exclude_none</c>).</param>
/// <param name="TableHasValueEquality">For tables, whether the table has <c>bequadro_value_equality</c>.</param>
internal sealed record TypeModel(
    TypeKind Kind,
    ScalarKind Scalar = ScalarKind.Bool,
    string? FullName = null,
    TypeModel? Element = null,
    int StructSize = 0,
    int StructAlignment = 0,
    bool UnionAllowsNone = false,
    bool TableHasValueEquality = false
)
{
    public static readonly TypeModel String = new(TypeKind.String);

    public static TypeModel ForScalar(ScalarKind scalar) => new(TypeKind.Scalar, scalar);

    /// <summary>
    /// True for types stored inline in a table: scalars, enums and structs.
    /// </summary>
    public bool IsInline => Kind is TypeKind.Scalar or TypeKind.Enum or TypeKind.Struct;

    /// <summary>
    /// The size in bytes when stored inline, or 4 (a uoffset) for offset types.
    /// </summary>
    public int InlineSize =>
        Kind switch
        {
            TypeKind.Scalar or TypeKind.Enum => ScalarInfo.Size(Scalar),
            TypeKind.Struct => StructSize,
            _ => 4,
        };

    public int InlineAlignment =>
        Kind switch
        {
            TypeKind.Scalar or TypeKind.Enum => ScalarInfo.Size(Scalar),
            TypeKind.Struct => StructAlignment,
            _ => 4,
        };
}

/// <summary>
/// A declaration ready for code generation. <see cref="Namespace"/> and <see cref="Name"/> are the schema's until
/// <see cref="Naming.ModelRenamer"/> replaces them with the target's.
/// </summary>
internal abstract record DeclarationModel(string Namespace, string Name, EquatableArray<string> Documentation)
{
    public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

    /// <summary>The fully qualified schema name, which renaming doesn't change. Used in messages.</summary>
    public string SchemaFullName { get; init; } = "";

    /// <summary>The schema name, when renaming changed it by more than the target's case conversion.</summary>
    public string? RenamedFrom { get; init; }

    /// <summary>A type name the target derives from this declaration, such as TypeScript's input type.</summary>
    public string? DerivedName { get; init; }

    /// <summary>The name of the generated C# file, without the extension, when it isn't <see cref="FullName"/>.</summary>
    public string? FileName { get; init; }

    public string HintName => (FileName ?? FullName) + ".g.cs";
}

/// <summary>
/// A constant: the default of a scalar or enum field. Which member is meaningful depends on the field's type.
/// </summary>
/// <param name="Integer">The value of an integer or enum constant.</param>
/// <param name="Float">The value of a floating-point constant, already rounded to the field's precision.</param>
/// <param name="Bool">The value of a bool constant.</param>
/// <param name="EnumMember">For an enum constant, the name of the member, renamed with it.</param>
internal sealed record ConstantModel(
    decimal Integer = 0,
    double Float = 0,
    bool Bool = false,
    string? EnumMember = null
);

internal sealed record EnumMemberModel(string Name, decimal Value, EquatableArray<string> Documentation)
{
    /// <inheritdoc cref="DeclarationModel.RenamedFrom"/>
    public string? RenamedFrom { get; init; }
}

internal sealed record EnumModel(
    string Namespace,
    string Name,
    EquatableArray<string> Documentation,
    ScalarKind Underlying,
    EquatableArray<EnumMemberModel> Members
) : DeclarationModel(Namespace, Name, Documentation);

/// <param name="Name">The target's name for the field.</param>
/// <param name="SchemaName">The field name as written in the schema.</param>
/// <param name="Type">The field type.</param>
/// <param name="Offset">The offset of the field in the struct.</param>
/// <param name="Documentation">Documentation lines.</param>
internal sealed record StructFieldModel(
    string Name,
    string SchemaName,
    TypeModel Type,
    int Offset,
    EquatableArray<string> Documentation
)
{
    /// <inheritdoc cref="DeclarationModel.RenamedFrom"/>
    public string? RenamedFrom { get; init; }
}

internal sealed record StructModel(
    string Namespace,
    string Name,
    EquatableArray<string> Documentation,
    int Size,
    int Alignment,
    EquatableArray<StructFieldModel> Fields
) : DeclarationModel(Namespace, Name, Documentation);

/// <param name="Name">The target's name for the field.</param>
/// <param name="SchemaName">The field name as written in the schema, used in error messages.</param>
/// <param name="Type">The field type.</param>
/// <param name="Slot">The vtable slot. For union fields this is the value slot; the type slot is <c>Slot - 1</c>.</param>
/// <param name="IsRequired">Whether the field is <c>required</c> in the schema, which makes it required on the wire.</param>
/// <param name="IsRequiredMember">
/// Whether the field must be given when constructing (a C# <c>required</c> property): when the field is required in the
/// schema, or is a scalar or enum without a default and <see cref="SchemaOptions.RequireScalarsWithoutDefault"/> is set.
/// </param>
/// <param name="IsOptionalScalar">Whether the field is a scalar or enum with a <c>null</c> default.</param>
/// <param name="Default">The default of a scalar or enum field when it isn't zero, otherwise null.</param>
/// <param name="Documentation">Documentation lines.</param>
internal sealed record TableFieldModel(
    string Name,
    string SchemaName,
    TypeModel Type,
    int Slot,
    bool IsRequired,
    bool IsRequiredMember,
    bool IsOptionalScalar,
    ConstantModel? Default,
    EquatableArray<string> Documentation
)
{
    /// <inheritdoc cref="DeclarationModel.RenamedFrom"/>
    public string? RenamedFrom { get; init; }

    /// <summary>
    /// Whether the field is <c>deprecated</c>. The binder keeps a deprecated field so that naming treats it as any other
    /// field, and deprecating a field renames nothing. <see cref="Naming.ModelRenamer"/> then removes it, so code
    /// generation never sees one. Its slot stays unused.
    /// </summary>
    public bool IsDeprecated { get; init; }
}

/// <param name="Namespace">The schema namespace.</param>
/// <param name="Name">The table name.</param>
/// <param name="Documentation">Documentation lines.</param>
/// <param name="Fields">The fields in declaration order.</param>
/// <param name="HasValueEquality">Whether the table has value equality: true with <c>bequadro_value_equality</c>.</param>
internal sealed record TableModel(
    string Namespace,
    string Name,
    EquatableArray<string> Documentation,
    EquatableArray<TableFieldModel> Fields,
    bool HasValueEquality
) : DeclarationModel(Namespace, Name, Documentation)
{
    /// <summary>
    /// The number of vtable slots, counting two for each union field, up to the last field in <see cref="Fields"/>.
    /// After renaming, which drops deprecated fields, that is the last field that isn't deprecated.
    /// </summary>
    public int SlotCount => Fields.Count == 0 ? 0 : Fields.Items.Max(f => f.Slot) + 1;
}

/// <param name="Label">The target's name for the member.</param>
/// <param name="Type">The member type.</param>
/// <param name="Discriminator">The discriminator.</param>
/// <param name="Documentation">Documentation lines.</param>
internal sealed record UnionMemberModel(
    string Label,
    TypeModel Type,
    int Discriminator,
    EquatableArray<string> Documentation
)
{
    /// <inheritdoc cref="DeclarationModel.RenamedFrom"/>
    public string? RenamedFrom { get; init; }
}

/// <param name="Namespace">The schema namespace.</param>
/// <param name="Name">The union name.</param>
/// <param name="Documentation">Documentation lines.</param>
/// <param name="Members">The members in declaration order.</param>
/// <param name="AllowsNone">Whether <c>NONE</c> is a valid value: false with <c>bequadro_exclude_none</c>.</param>
/// <param name="AllowsUnknown">Whether unknown discriminators are read as <c>UnionUnknown</c>: false with <c>bequadro_exhaustive</c>.</param>
internal sealed record UnionModel(
    string Namespace,
    string Name,
    EquatableArray<string> Documentation,
    EquatableArray<UnionMemberModel> Members,
    bool AllowsNone,
    bool AllowsUnknown
) : DeclarationModel(Namespace, Name, Documentation);
