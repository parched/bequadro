using System.Text;

namespace Bequadro.Generator.Common;

internal static class Naming
{
    private static readonly HashSet<string> Keywords =
    [
        "abstract",
        "as",
        "base",
        "bool",
        "break",
        "byte",
        "case",
        "catch",
        "char",
        "checked",
        "class",
        "const",
        "continue",
        "decimal",
        "default",
        "delegate",
        "do",
        "double",
        "else",
        "enum",
        "event",
        "explicit",
        "extern",
        "false",
        "finally",
        "fixed",
        "float",
        "for",
        "foreach",
        "goto",
        "if",
        "implicit",
        "in",
        "int",
        "interface",
        "internal",
        "is",
        "lock",
        "long",
        "namespace",
        "new",
        "null",
        "object",
        "operator",
        "out",
        "override",
        "params",
        "private",
        "protected",
        "public",
        "readonly",
        "ref",
        "return",
        "sbyte",
        "sealed",
        "short",
        "sizeof",
        "stackalloc",
        "static",
        "string",
        "struct",
        "switch",
        "this",
        "throw",
        "true",
        "try",
        "typeof",
        "uint",
        "ulong",
        "unchecked",
        "unsafe",
        "ushort",
        "using",
        "virtual",
        "void",
        "volatile",
        "while",
        "__arglist",
        "__makeref",
        "__reftype",
        "__refvalue",
    ];

    /// <summary>
    /// Words that are keywords only in some contexts. Escaping them is never wrong, and avoids every context.
    /// </summary>
    private static readonly HashSet<string> ContextualKeywords =
    [
        "add",
        "allows",
        "alias",
        "and",
        "ascending",
        "args",
        "async",
        "await",
        "by",
        "descending",
        "dynamic",
        "equals",
        "extension",
        "field",
        "file",
        "from",
        "get",
        "global",
        "group",
        "init",
        "into",
        "join",
        "let",
        "managed",
        "nameof",
        "nint",
        "not",
        "notnull",
        "nuint",
        "on",
        "or",
        "orderby",
        "partial",
        "record",
        "remove",
        "required",
        "scoped",
        "select",
        "set",
        "union",
        "unmanaged",
        "value",
        "var",
        "when",
        "where",
        "with",
        "yield",
    ];

    /// <summary>
    /// Converts a schema field name to a C# property name: split on '_', upper-case the first character of each
    /// segment and keep the rest as written.
    /// </summary>
    public static string ToPascalCase(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var segment in name.Split('_'))
        {
            if (segment.Length > 0)
            {
                builder.Append(char.ToUpperInvariant(segment[0])).Append(segment, 1, segment.Length - 1);
            }
        }

        return builder.Length > 0 ? builder.ToString() : name;
    }

    /// <summary>
    /// Prefixes an identifier with '@' when it is a C# keyword, contextual or not.
    /// </summary>
    public static string Escape(string identifier) =>
        Keywords.Contains(identifier) || ContextualKeywords.Contains(identifier) ? "@" + identifier : identifier;

    /// <summary>
    /// Prefixes a type name with '@' when it is a keyword or made only of lowercase ASCII letters. C# warns about a type
    /// with such a name (CS8981), because later versions may make it a keyword, and every contextual keyword is one. The
    /// escaped name compiles in every version.
    /// </summary>
    public static string EscapeTypeName(string name) =>
        Keywords.Contains(name) || name.All(c => c is >= 'a' and <= 'z') ? "@" + name : name;

    /// <summary>
    /// Converts a fully qualified schema name to a fully qualified C# type name with a global:: prefix.
    /// </summary>
    public static string GlobalTypeName(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        return dot < 0
            ? "global::" + EscapeTypeName(fullName)
            : "global::"
                + NamespaceName(fullName.Substring(0, dot))
                + "."
                + EscapeTypeName(fullName.Substring(dot + 1));
    }

    /// <summary>
    /// Converts a schema namespace to a C# namespace, escaping keywords.
    /// </summary>
    public static string NamespaceName(string schemaNamespace) =>
        string.Join(".", schemaNamespace.Split('.').Select(Escape));
}
