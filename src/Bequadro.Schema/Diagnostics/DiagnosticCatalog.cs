namespace Bequadro.Schema.Diagnostics;

/// <summary>
/// The title and message format of each diagnostic. See requirements/diagnostics.md.
/// </summary>
internal sealed record DiagnosticInfo(string Id, string Title, string MessageFormat);

internal static class DiagnosticCatalog
{
    private static readonly Dictionary<string, DiagnosticInfo> ById = new[]
    {
        Create(DiagnosticIds.SyntaxError, "Syntax error", "Syntax error: {0}"),
        Create(DiagnosticIds.UnterminatedLiteral, "Unterminated literal or comment", "Unterminated {0}"),
        Create(DiagnosticIds.InvalidNumericLiteral, "Invalid numeric literal", "Invalid numeric literal '{0}'"),
        Create(
            DiagnosticIds.IncludeAfterDeclarations,
            "Include after declarations",
            "Include and native_include statements must appear before all other statements"
        ),
        Create(
            DiagnosticIds.IncludeNotFound,
            "Included file not found",
            "Included file '{0}' (resolved to '{1}') is not in the schema set; add it to AdditionalFiles"
        ),
        Create(DiagnosticIds.DuplicateDeclaration, "Duplicate declaration", "'{0}' is already declared at {1}"),
        Create(DiagnosticIds.UnknownType, "Unknown type", "Unknown type '{0}'"),
        Create(
            DiagnosticIds.TypeNotIncluded,
            "Type not included",
            "Type '{0}' is declared in '{1}', which is not included by this file"
        ),
        Create(
            DiagnosticIds.UndeclaredAttribute,
            "Undeclared attribute",
            "Attribute '{0}' is not declared before it is used; add 'attribute \"{0}\";' before it"
        ),
        Create(DiagnosticIds.DuplicateMember, "Duplicate member name", "'{0}' is already declared in '{1}'"),
        Create(
            DiagnosticIds.InvalidDefault,
            "Invalid default value",
            "Invalid default value '{0}' for field '{1}': {2}"
        ),
        Create(DiagnosticIds.InvalidRequired, "Invalid required field", "Field '{0}' cannot be required: {1}"),
        Create(
            DiagnosticIds.InvalidStructFieldType,
            "Invalid struct field type",
            "Struct field '{0}' has type '{1}'; struct fields must be scalars, enums or structs"
        ),
        Create(DiagnosticIds.EmptyStruct, "Empty struct", "Struct '{0}' must have at least one field"),
        Create(DiagnosticIds.RecursiveStruct, "Recursive struct", "Struct '{0}' contains itself"),
        Create(DiagnosticIds.InvalidEnum, "Invalid enum", "Invalid enum '{0}': {1}"),
        Create(DiagnosticIds.InvalidUnion, "Invalid union", "Invalid union '{0}': {1}"),
        Create(
            DiagnosticIds.EnumWithoutZero,
            "Enum field has no zero value",
            "Field '{0}' needs a default because enum '{1}' has no value equal to 0"
        ),
        Create(DiagnosticIds.NestedVector, "Nested vector", "Vectors of vectors are not allowed"),
        Create(DiagnosticIds.DefaultNotAllowed, "Default not allowed", "Field '{0}' cannot have a default: {1}"),
        Create(
            DiagnosticIds.InvalidRootType,
            "Invalid root type",
            "Root type '{0}' must name a table declared before it, by its full name or by its name in the current namespace"
        ),
        Create(DiagnosticIds.InvalidBequadroAttribute, "Invalid Bequadro attribute", "{0}"),
        Create(
            DiagnosticIds.StructTooLarge,
            "Struct too large",
            "Struct '{0}' is larger than the maximum of {1} bytes"
        ),
        Create(DiagnosticIds.DeclarationOrder, "Declaration order", "Type '{0}' can't be used here: {1}"),
        Create(DiagnosticIds.FieldNameClash, "Field name clash", "Field '{0}' of '{1}' {2}, which flatc rejects"),
        Create(
            DiagnosticIds.DuplicateFile,
            "Duplicate schema file",
            "'{0}' is the same file as '{1}', so it is ignored"
        ),
        Create(
            DiagnosticIds.DeprecatedStructField,
            "Deprecated struct field",
            "Field '{0}' of struct '{1}' can't be deprecated: only table fields can"
        ),
        Create(DiagnosticIds.UnsupportedFeature, "Unsupported feature", "{0} is not supported by Bequadro"),
        Create(
            DiagnosticIds.AmbiguousUnionMembers,
            "Ambiguous union members",
            "Union '{0}' has more than one member of type '{1}'"
        ),
    }.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public static IEnumerable<DiagnosticInfo> All => ById.Values;

    public static DiagnosticInfo Get(string id) => ById[id];

    private static DiagnosticInfo Create(string id, string title, string messageFormat) =>
        new(id, title, messageFormat);
}
