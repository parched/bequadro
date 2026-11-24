using Bequadro.Schema.Naming;
using Bequadro.Schema.Semantics;

namespace Bequadro.Cli.Python;

/// <summary>
/// The names Python can't use as written (PY-003). Every rule is satisfied by appending underscores, so renaming always
/// ends: a name starting with <c>__</c> becomes usable once it ends with three underscores, and one starting with
/// <c>_bq</c> once it ends with one.
/// </summary>
internal sealed class PythonNamingRules : NamingRules
{
    public static PythonNamingRules Instance { get; } = new();

    public static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "False",
        "None",
        "True",
        "and",
        "as",
        "assert",
        "async",
        "await",
        "break",
        "class",
        "continue",
        "def",
        "del",
        "elif",
        "else",
        "except",
        "finally",
        "for",
        "from",
        "global",
        "if",
        "import",
        "in",
        "is",
        "lambda",
        "nonlocal",
        "not",
        "or",
        "pass",
        "raise",
        "return",
        "try",
        "while",
        "with",
        "yield",
    };

    /// <summary>
    /// Built-ins that class bodies refer to in annotations.
    /// </summary>
    private static readonly HashSet<string> AnnotationBuiltIns = new(StringComparer.Ordinal)
    {
        "bool",
        "int",
        "float",
        "str",
        "bytearray",
        "list",
        "object",
    };

    /// <summary>
    /// Enum members that <c>enum</c> or type checkers reserve: <c>name</c> and <c>value</c>, and the public
    /// attributes of an <c>IntEnum</c> class or member, which type checkers report a member as overriding. Those are
    /// the names without a leading underscore in <c>dir(int)</c>, <c>dir(enum.IntEnum)</c> and <c>dir(type)</c>, the
    /// same in every version from 3.12 to 3.15. tests/python checks the list against the running version.
    /// </summary>
    private static readonly HashSet<string> EnumNames = new(StringComparer.Ordinal)
    {
        "name",
        "value",
        "as_integer_ratio",
        "bit_count",
        "bit_length",
        "conjugate",
        "denominator",
        "from_bytes",
        "imag",
        "is_integer",
        "mro",
        "numerator",
        "real",
        "to_bytes",
    };

    /// <summary>
    /// The name that <c>from __future__ import annotations</c>, at the start of every generated module (PY-001), binds
    /// in the module.
    /// </summary>
    private const string FutureImport = "annotations";

    /// <summary>
    /// The runtime module, the module of the root namespace's declarations and the fingerprint's module (PY-002).
    /// </summary>
    public override IEnumerable<string> FixedRootNames => ["bequadro", "bequadro_root", "bequadro_schema"];

    public override bool NamespacesAreFiles => true;

    public override bool IsUsableNamespaceSegment(TargetNamespace parent, string segment) =>
        !Keywords.Contains(segment) && !IsDunder(segment) && !IsInternal(segment) && segment != FutureImport;

    public override bool IsUsableDeclarationName(DeclarationModel declaration, TargetNamespace ns, string name) =>
        !Keywords.Contains(name)
        && name != FutureImport
        && !PythonBuiltIns.Names.Contains(name)
        && !IsInternal(name)
        && !IsMangled(name)
        && !IsDunder(name);

    public override IEnumerable<string> FixedMemberNames(DeclarationModel declaration) => ["__bequadro__"];

    /// <summary>
    /// A field can't shadow a name its class's annotations or defaults use: a built-in, the runtime alias, or a
    /// declaration of its own namespace that its fields refer to.
    /// </summary>
    public override bool IsUsableFieldName(DeclarationModel declaration, string name) =>
        !Keywords.Contains(name)
        && !AnnotationBuiltIns.Contains(name)
        && !IsInternal(name)
        && !IsMangled(name)
        && !IsDunder(name)
        && !ReferencedNames(declaration).Contains(name);

    public override bool IsUsableEnumMember(EnumModel declaration, string name) =>
        !Keywords.Contains(name) && !EnumNames.Contains(name) && !IsMangled(name) && !IsDunder(name) && !IsSunder(name);

    /// <summary>
    /// The names of the declarations in the same namespace that a table's or struct's fields refer to, itself included.
    /// </summary>
    private static HashSet<string> ReferencedNames(DeclarationModel declaration)
    {
        IEnumerable<TypeModel> types = declaration switch
        {
            TableModel table => table.Fields.Select(f => f.Type),
            StructModel structModel => structModel.Fields.Select(f => f.Type),
            _ => [],
        };
        var prefix = declaration.Namespace.Length == 0 ? "" : declaration.Namespace + ".";
        var names = new HashSet<string>(StringComparer.Ordinal) { declaration.Name };
        foreach (var type in types.Select(t => t.Element ?? t))
        {
            if (
                type.FullName is { } fullName
                && fullName.StartsWith(prefix, StringComparison.Ordinal)
                && fullName.IndexOf('.', prefix.Length) < 0
            )
            {
                names.Add(fullName[prefix.Length..]);
            }
        }

        return names;
    }

    /// <summary>
    /// Generated modules name their internals with the <c>_bq</c> prefix and never end them with <c>_</c>.
    /// </summary>
    private static bool IsInternal(string name) =>
        name.StartsWith("_bq", StringComparison.Ordinal) && !name.EndsWith('_');

    /// <summary>
    /// A name Python mangles in a class body: it starts with <c>__</c> and doesn't end with <c>__</c>.
    /// </summary>
    private static bool IsMangled(string name) =>
        name.StartsWith("__", StringComparison.Ordinal) && !name.EndsWith("__", StringComparison.Ordinal);

    /// <summary>
    /// A special name: it starts with <c>__</c> and ends with exactly two underscores.
    /// </summary>
    private static bool IsDunder(string name) =>
        name.Length > 4
        && name.StartsWith("__", StringComparison.Ordinal)
        && name.EndsWith("__", StringComparison.Ordinal)
        && name[^3] != '_';

    /// <summary>
    /// A name <c>enum</c> reserves: it starts and ends with a single underscore.
    /// </summary>
    private static bool IsSunder(string name) =>
        name.Length > 2 && name[0] == '_' && name[1] != '_' && name[^1] == '_' && name[^2] != '_';
}
