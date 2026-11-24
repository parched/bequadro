using Bequadro.Schema.Naming;
using Bequadro.Schema.Semantics;

namespace Bequadro.Cli.TypeScript;

/// <summary>
/// The names TypeScript can't use as written (TS-003).
/// </summary>
internal sealed class TypeScriptNamingRules : NamingRules
{
    public static TypeScriptNamingRules Instance { get; } = new();

    /// <summary>
    /// Names a declaration can't have: reserved words, names strict-mode modules can't bind, and predefined types.
    /// </summary>
    private static readonly HashSet<string> ReservedDeclarationNames = new(StringComparer.Ordinal)
    {
        "break",
        "case",
        "catch",
        "class",
        "const",
        "continue",
        "debugger",
        "default",
        "delete",
        "do",
        "else",
        "enum",
        "export",
        "extends",
        "false",
        "finally",
        "for",
        "function",
        "if",
        "import",
        "in",
        "instanceof",
        "new",
        "null",
        "return",
        "super",
        "switch",
        "this",
        "throw",
        "true",
        "try",
        "typeof",
        "var",
        "void",
        "while",
        "with",
        "implements",
        "interface",
        "let",
        "package",
        "private",
        "protected",
        "public",
        "static",
        "yield",
        "await",
        "eval",
        "arguments",
        "any",
        "bigint",
        "boolean",
        "never",
        "number",
        "object",
        "string",
        "symbol",
        "undefined",
        "unknown",
        "as",
        "infer",
        "keyof",
        "readonly",
        "unique",
    };

    /// <summary>
    /// <c>Object.prototype</c> members, which an input object that omits the field would inherit.
    /// </summary>
    private static readonly HashSet<string> ObjectPrototypeMembers = new(StringComparer.Ordinal)
    {
        "constructor",
        "hasOwnProperty",
        "isPrototypeOf",
        "propertyIsEnumerable",
        "toLocaleString",
        "toString",
        "valueOf",
    };

    /// <summary>
    /// The runtime module, the root namespace's module and the fingerprint's module.
    /// </summary>
    public override IEnumerable<string> FixedRootNames => ["bequadro", "index", "bequadro_schema"];

    public override bool NamespacesAreFiles => true;

    /// <summary>
    /// Namespaces are files, and declarations are names inside a module, so the two never collide.
    /// </summary>
    public override bool DeclarationsShareScopeWithNamespaces => false;

    public override bool IsUsableDeclarationName(DeclarationModel declaration, TargetNamespace ns, string name) =>
        !ReservedDeclarationNames.Contains(name);

    public override string? DerivedNameCandidate(DeclarationModel declaration, string name) =>
        declaration is EnumModel ? null : name + "Init";

    public override string FieldCandidate(string schemaName) => ToCamelCase(schemaName);

    public override bool IsUsableFieldName(DeclarationModel declaration, string name) =>
        !ObjectPrototypeMembers.Contains(name);

    public override bool IsUsableEnumMember(EnumModel declaration, string name) => name != "__proto__";

    public override bool IsUsableUnionLabel(UnionModel declaration, string label) => label != "unknown";

    /// <summary>
    /// Splits on <c>_</c>, drops empty segments, lower-cases the first character of the first segment and upper-cases
    /// the first character of each later one (TS-003).
    /// </summary>
    public static string ToCamelCase(string name)
    {
        var segments = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return name;
        }

        return string.Concat(
            segments.Select(
                (segment, index) =>
                    (index == 0 ? char.ToLowerInvariant(segment[0]) : char.ToUpperInvariant(segment[0])) + segment[1..]
            )
        );
    }
}
