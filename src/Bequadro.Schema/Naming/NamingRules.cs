using Bequadro.Schema.Semantics;

namespace Bequadro.Schema.Naming;

/// <summary>
/// What a target can't use as a name, and the names it fixes or derives (NAM-002). <see cref="ModelRenamer"/> applies
/// them.
/// </summary>
internal abstract class NamingRules
{
    /// <summary>
    /// Whether each namespace is a file or directory, so namespaces in one scope must differ ignoring case.
    /// </summary>
    public virtual bool NamespacesAreFiles => false;

    /// <summary>
    /// Whether a namespace's declarations and its nested namespaces share one scope, as in C#, where a type and a
    /// namespace can't have the same name, and in Python, where both are attributes of a package.
    /// </summary>
    public virtual bool DeclarationsShareScopeWithNamespaces => true;

    /// <summary>
    /// Names fixed in the root namespace's scope, such as the runtime module.
    /// </summary>
    public virtual IEnumerable<string> FixedRootNames => [];

    /// <param name="parent">The target's name for the enclosing namespace.</param>
    /// <param name="segment">The segment to check.</param>
    public virtual bool IsUsableNamespaceSegment(TargetNamespace parent, string segment) => true;

    /// <param name="declaration">The declaration.</param>
    /// <param name="ns">The target's name for its namespace.</param>
    /// <param name="name">The name to check.</param>
    public virtual bool IsUsableDeclarationName(DeclarationModel declaration, TargetNamespace ns, string name) => true;

    /// <summary>
    /// The candidate for a type name derived from a declaration, given its final name, or null if there is none.
    /// </summary>
    public virtual string? DerivedNameCandidate(DeclarationModel declaration, string name) => null;

    /// <summary>
    /// The candidate for a table or struct field: the schema name with the target's case conversion.
    /// </summary>
    public virtual string FieldCandidate(string schemaName) => schemaName;

    /// <summary>
    /// The members of a table, struct or union that are part of the target's fixed API.
    /// </summary>
    public virtual IEnumerable<string> FixedMemberNames(DeclarationModel declaration) => [];

    /// <param name="declaration">The table or struct, already renamed, with its type references renamed too.</param>
    /// <param name="name">The name to check.</param>
    public virtual bool IsUsableFieldName(DeclarationModel declaration, string name) => true;

    public virtual bool IsUsableEnumMember(EnumModel declaration, string name) => true;

    public virtual bool IsUsableUnionLabel(UnionModel declaration, string label) => true;
}
