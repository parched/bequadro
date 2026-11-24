using Bequadro.Generator.Emit;
using Bequadro.Schema.Common;
using Bequadro.Schema.Naming;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Common;

/// <summary>
/// The names C# can't use as written (API-002). Keywords aren't among them: emitters escape those with <c>@</c>.
/// </summary>
/// <remarks>
/// Most of the rules follow from one C# rule: the members of a type, and the type itself, need distinct names, where a
/// member's names are its metadata names too. A property <c>P</c> also takes <c>get_P</c> (and <c>set_P</c> if it has
/// a setter), and an operator takes its <c>op_</c> name. Members must also not hide what every type inherits from
/// <c>object</c>.
/// </remarks>
internal sealed class CSharpNamingRules : NamingRules
{
    public static CSharpNamingRules Instance { get; } = new();

    /// <summary>
    /// What every class and struct inherits from <c>object</c>, which a member with the same name would hide (CS0108).
    /// </summary>
    private static readonly string[] ObjectMembers =
    [
        "ToString",
        "Equals",
        "GetHashCode",
        "GetType",
        "MemberwiseClone",
        "Finalize",
        "ReferenceEquals",
    ];

    /// <summary>
    /// The operator methods generated for value equality.
    /// </summary>
    private static readonly string[] EqualityOperators = ["op_Equality", "op_Inequality"];

    /// <summary>
    /// The member names generated in a table, besides its fields.
    /// </summary>
    private static readonly string[] TableMembers = [.. Property("Serializer", setter: false), "ToString"];

    private static readonly string[] ValueEqualityTableMembers =
    [
        .. TableMembers,
        "Equals",
        "GetHashCode",
        .. EqualityOperators,
    ];

    private static readonly string[] StructMembers = ["Equals", "GetHashCode", "ToString", .. EqualityOperators];

    /// <summary>
    /// The member names generated in a union, besides its labels and the fields that keep struct members
    /// (<see cref="IsStorageField"/>). <c>HasValue</c> is reserved whether or not the union has it, so adding
    /// <c>bequadro_exclude_none</c> doesn't rename a label.
    /// </summary>
    private static readonly string[] UnionMembers =
    [
        "ItemKind",
        .. Property("Kind", setter: false),
        .. Property("Value", setter: false),
        .. Property("HasValue", setter: false),
        "TryGetValue",
        "Equals",
        "GetHashCode",
        "ToString",
        "op_Implicit",
        .. EqualityOperators,
        "_kind",
        "_reference",
    ];

    /// <summary>
    /// Generated code refers to .NET and runtime types as <c>global::System...</c> and <c>global::Bequadro...</c>, which a
    /// root declaration with these names would capture.
    /// </summary>
    private static readonly HashSet<string> ReservedRootNames = new(StringComparer.Ordinal) { "System", "Bequadro" };

    /// <summary>
    /// The runtime's public types, and its <c>Internal</c> namespace, which a declaration or namespace in
    /// <c>Bequadro</c> would conflict with.
    /// </summary>
    private static readonly HashSet<string> RuntimeNames = new(StringComparer.Ordinal)
    {
        "FlatBufferSerializer",
        "UnionNone",
        "UnionUnknown",
        "Internal",
    };

    /// <summary>
    /// The name .NET gives an enum's value field, which no enum member may have (CS0076).
    /// </summary>
    private const string EnumValueField = "value__";

    /// <summary>
    /// The class that holds the schema set's fingerprint (SER-015), which no root declaration or top-level namespace
    /// may take.
    /// </summary>
    public override IEnumerable<string> FixedRootNames => [FingerprintEmitter.ClassName];

    /// <summary>
    /// A schema namespace inside <c>System</c> could conflict with the .NET types generated code uses, and one in
    /// <c>Bequadro</c> with the runtime's types.
    /// </summary>
    public override bool IsUsableNamespaceSegment(TargetNamespace parent, string segment) =>
        !(parent.IsRoot && segment == "System") && !(parent.Is("Bequadro") && RuntimeNames.Contains(segment));

    /// <summary>
    /// A declaration can't be named like a member generated in it (CS0542).
    /// </summary>
    public override bool IsUsableDeclarationName(DeclarationModel declaration, TargetNamespace ns, string name) =>
        !(ns.IsRoot && ReservedRootNames.Contains(name))
        && !(ns.Is("Bequadro") && RuntimeNames.Contains(name))
        && !GeneratedMembers(declaration).Contains(name)
        && !(declaration is UnionModel && IsStorageField(name));

    public override string FieldCandidate(string schemaName) => Naming.ToPascalCase(schemaName);

    public override IEnumerable<string> FixedMemberNames(DeclarationModel declaration) =>
        declaration is EnumModel ? [] : GeneratedMembers(declaration).Concat(ObjectMembers);

    /// <summary>
    /// A field can't have its type's name (CS0542), and neither can its property's accessors.
    /// </summary>
    public override bool IsUsableFieldName(DeclarationModel declaration, string name) =>
        declaration is TableModel ? !Property(name, setter: true).Contains(declaration.Name) : name != declaration.Name;

    public override bool IsUsableEnumMember(EnumModel declaration, string name) => name != EnumValueField;

    /// <summary>
    /// A label is a property, and a member of the nested <c>ItemKind</c> enum. Neither it nor its getter can have the
    /// type's name, the label can't be the name of a field that keeps a struct member, and it can't be the getter of
    /// another label.
    /// </summary>
    public override bool IsUsableUnionLabel(UnionModel declaration, string label) =>
        !Property(label, setter: false).Contains(declaration.Name) && !IsStorageField(label) && label != EnumValueField;

    /// <summary>
    /// Renames each label that is the getter of another label, <c>get_L</c> beside <c>L</c>, which C# can't tell apart
    /// (CS0102). This depends on the other labels' final names, so it runs after renaming. The getter-shaped label
    /// gets trailing <c>_</c>s, whichever comes first, until it is usable and free.
    /// </summary>
    public UnionModel SeparateLabelGetters(UnionModel union)
    {
        var labels = union.Members.Select(m => m.Label).ToArray();
        var fixedNames = new HashSet<string>(FixedMemberNames(union), StringComparer.Ordinal);
        bool IsGetterOfOther(int index) =>
            labels[index].StartsWith("get_", StringComparison.Ordinal)
            && Array.IndexOf(labels, labels[index].Substring("get_".Length)) is >= 0 and var other
            && other != index;

        // Shortest first: a label can only be the getter of a shorter one, which is final by then. Appending only makes
        // a label longer, so it can't become the getter of one already done.
        foreach (var i in Enumerable.Range(0, labels.Length).OrderBy(i => labels[i].Length).ToList())
        {
            while (
                IsGetterOfOther(i)
                || !IsUsableUnionLabel(union, labels[i])
                || fixedNames.Contains(labels[i])
                || labels.Where((label, j) => j != i && label == labels[i]).Any()
            )
            {
                labels[i] += "_";
            }
        }

        return union with
        {
            Members = union
                .Members.Select(
                    (member, i) =>
                        member.Label == labels[i]
                            ? member
                            : member with
                            {
                                Label = labels[i],
                                RenamedFrom = member.RenamedFrom ?? member.Label,
                            }
                )
                .ToEquatableArray(),
        };
    }

    private static IEnumerable<string> GeneratedMembers(DeclarationModel declaration) =>
        declaration switch
        {
            TableModel { HasValueEquality: true } => ValueEqualityTableMembers,
            TableModel => TableMembers,
            StructModel => StructMembers,
            UnionModel => UnionMembers,
            _ => [],
        };

    /// <summary>
    /// The names a property takes: its own and its accessors'.
    /// </summary>
    private static string[] Property(string name, bool setter) =>
        setter ? [name, "get_" + name, "set_" + name] : [name, "get_" + name];

    private static bool IsStorageField(string label) =>
        label.Length > "_value".Length
        && label.StartsWith("_value", StringComparison.Ordinal)
        && label.Skip("_value".Length).All(c => c is >= '0' and <= '9');
}
