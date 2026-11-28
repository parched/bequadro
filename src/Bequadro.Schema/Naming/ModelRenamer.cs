using Bequadro.Schema.Common;
using Bequadro.Schema.Semantics;

namespace Bequadro.Schema.Naming;

/// <summary>
/// Renames the declaration models of a schema set for one target (NAM-002). Names are never rejected: a name the target
/// can't use, or that is taken, gets underscores. Within each scope, the target's fixed names come first, then names
/// written in the schema, then names derived from them, each in order of first appearance.
/// </summary>
internal static class ModelRenamer
{
    /// <param name="models">The models in declaration order, as the binder produces them.</param>
    /// <param name="rules">The target's rules.</param>
    public static EquatableArray<DeclarationModel> Rename(EquatableArray<DeclarationModel> models, NamingRules rules)
    {
        var root = BuildNamespaceTree(models.Items);
        var declarationNames = new Dictionary<DeclarationModel, (string Namespace, string Name, string? Derived)>(
            ReferenceComparer<DeclarationModel>.Instance
        );

        // An explicit stack instead of recursion, so a namespace with very many segments can't overflow the stack (DIA-001)
        var pending = new Stack<(NamespaceNode Node, TargetNamespace TargetNamespace)>();
        pending.Push((root, TargetNamespace.Root));
        while (pending.Count > 0)
        {
            var (node, targetNamespace) = pending.Pop();
            foreach (var child in NameNamespace(node, targetNamespace, rules, declarationNames))
            {
                pending.Push(child);
            }
        }

        var typeNames = models.Items.ToDictionary(
            m => m.FullName,
            m =>
            {
                var (ns, name, _) = declarationNames[m];
                return ns.Length == 0 ? name : ns + "." + name;
            },
            StringComparer.Ordinal
        );

        // Enum members first, because enum defaults of table fields refer to them
        var renamed = new List<DeclarationModel>(models.Count);
        var enumMembers = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var (ns, name, derived) = declarationNames[model];
            var declaration = model with
            {
                Namespace = ns,
                Name = name,
                // The full schema name when a namespace was renamed, so the documentation shows what changed
                RenamedFrom =
                    ns != model.Namespace ? model.FullName
                    : name != model.Name ? model.Name
                    : null,
                DerivedName = derived,
            };
            if (declaration is EnumModel enumModel)
            {
                var members = NameEnumMembers(enumModel, rules);
                enumMembers[model.FullName] = enumModel
                    .Members.Items.Zip(members, (m, r) => (m.Name, r.Name))
                    .ToDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal);
                declaration = enumModel with { Members = members.ToEquatableArray() };
            }

            renamed.Add(declaration);
        }

        return renamed
            .Select(model =>
                model switch
                {
                    TableModel table => NameTableFields(RenameTypes(table, typeNames, enumMembers), rules),
                    StructModel structModel => NameStructFields(RenameTypes(structModel, typeNames), rules),
                    UnionModel union => NameUnionLabels(RenameTypes(union, typeNames), rules),
                    _ => model,
                }
            )
            .ToEquatableArray();
    }

    private static NamespaceNode BuildNamespaceTree(IReadOnlyList<DeclarationModel> models)
    {
        var root = new NamespaceNode("");
        for (var index = 0; index < models.Count; index++)
        {
            var node = root;
            if (models[index].Namespace.Length > 0)
            {
                foreach (var segment in models[index].Namespace.Split('.'))
                {
                    if (!node.Children.TryGetValue(segment, out var child))
                    {
                        node.Children[segment] = child = new NamespaceNode(segment);
                        node.Order.Add(child);
                    }

                    node = child;
                }
            }

            node.Declarations.Add(models[index]);
            node.Order.Add(models[index]);
        }

        return root;
    }

    /// <summary>
    /// Names a namespace's nested namespaces and declarations, and returns the nested namespaces with their target names to
    /// name next. A nested namespace appears where its first declaration does, so <see cref="NamespaceNode.Order"/> is
    /// already in order of first appearance.
    /// </summary>
    private static IEnumerable<(NamespaceNode Node, TargetNamespace TargetNamespace)> NameNamespace(
        NamespaceNode node,
        TargetNamespace targetNamespace,
        NamingRules rules,
        Dictionary<DeclarationModel, (string, string, string?)> names
    )
    {
        var namespaces = new NameScope();
        var declarations = rules.DeclarationsShareScopeWithNamespaces ? namespaces : new NameScope();
        var isRoot = node.Segment.Length == 0;
        if (isRoot)
        {
            foreach (var name in rules.FixedRootNames)
            {
                namespaces.Fix(name, isFile: rules.NamespacesAreFiles);
            }
        }

        var claimed = new Dictionary<object, string>(ReferenceComparer<object>.Instance);
        foreach (var item in node.Order)
        {
            claimed[item] = item switch
            {
                NamespaceNode child => namespaces.Claim(
                    child.Segment,
                    s => rules.IsUsableNamespaceSegment(targetNamespace, s),
                    isFile: rules.NamespacesAreFiles
                ),
                DeclarationModel model => declarations.Claim(model.Name, n =>
                    rules.IsUsableDeclarationName(model, targetNamespace, n)
                ),
                _ => throw new InvalidOperationException("Unexpected namespace item."),
            };
        }

        foreach (var model in node.Declarations)
        {
            var name = claimed[model];
            var derived = rules.DerivedNameCandidate(model, name) is { } candidate
                ? declarations.Claim(candidate, n => rules.IsUsableDeclarationName(model, targetNamespace, n))
                : null;
            names[model] = (targetNamespace.ToString(), name, derived);
        }

        return node.Children.Values.Select(child => (child, targetNamespace.Child(claimed[child]))).ToList();
    }

    private static List<EnumMemberModel> NameEnumMembers(EnumModel model, NamingRules rules)
    {
        var scope = new NameScope();
        return model
            .Members.Select(member =>
            {
                var name = scope.Claim(member.Name, n => rules.IsUsableEnumMember(model, n));
                return member with { Name = name, RenamedFrom = name == member.Name ? null : member.Name };
            })
            .ToList();
    }

    /// <summary>
    /// Names the fields, then removes the deprecated ones (SCH-005). A deprecated field still claims its name, so
    /// deprecating a field never renames another.
    /// </summary>
    private static TableModel NameTableFields(TableModel model, NamingRules rules)
    {
        var scope = NewMemberScope(model, rules);
        var fields = model
            .Fields.Select(field =>
            {
                var candidate = rules.FieldCandidate(field.SchemaName);
                var name = scope.Claim(candidate, n => rules.IsUsableFieldName(model, n));
                return field with
                {
                    Name = name,
                    RenamedFrom = IsRenamed(name, candidate, field.SchemaName) ? field.SchemaName : null,
                };
            })
            .Where(field => !field.IsDeprecated);
        return model with { Fields = fields.ToEquatableArray() };
    }

    private static StructModel NameStructFields(StructModel model, NamingRules rules)
    {
        var scope = NewMemberScope(model, rules);
        var fields = model.Fields.Select(field =>
        {
            var candidate = rules.FieldCandidate(field.SchemaName);
            var name = scope.Claim(candidate, n => rules.IsUsableFieldName(model, n));
            return field with
            {
                Name = name,
                RenamedFrom = IsRenamed(name, candidate, field.SchemaName) ? field.SchemaName : null,
            };
        });
        return model with { Fields = fields.ToEquatableArray() };
    }

    private static UnionModel NameUnionLabels(UnionModel model, NamingRules rules)
    {
        var scope = NewMemberScope(model, rules);
        var members = model.Members.Select(member =>
        {
            var label = scope.Claim(member.Label, n => rules.IsUsableUnionLabel(model, n));
            return member with { Label = label, RenamedFrom = label == member.Label ? null : member.Label };
        });
        return model with { Members = members.ToEquatableArray() };
    }

    /// <summary>
    /// Whether a field's name differs from its schema name by more than the case conversion: it isn't the candidate, and
    /// isn't the schema name either, which a field such as <c>_1</c> gets back from its candidate <c>1</c>.
    /// </summary>
    private static bool IsRenamed(string name, string candidate, string schemaName) =>
        name != candidate && name != schemaName;

    private static NameScope NewMemberScope(DeclarationModel model, NamingRules rules)
    {
        var scope = new NameScope();
        foreach (var name in rules.FixedMemberNames(model))
        {
            scope.Fix(name);
        }

        return scope;
    }

    private static TableModel RenameTypes(
        TableModel model,
        Dictionary<string, string> typeNames,
        Dictionary<string, Dictionary<string, string>> enumMembers
    ) =>
        model with
        {
            Fields = model
                .Fields.Select(field =>
                    field with
                    {
                        Type = RenameType(field.Type, typeNames),
                        Default = field.Default is { EnumMember: { } member } constant
                            ? constant with
                            {
                                EnumMember = enumMembers[field.Type.FullName!][member],
                            }
                            : field.Default,
                    }
                )
                .ToEquatableArray(),
        };

    private static StructModel RenameTypes(StructModel model, Dictionary<string, string> typeNames) =>
        model with
        {
            Fields = model
                .Fields.Select(field => field with { Type = RenameType(field.Type, typeNames) })
                .ToEquatableArray(),
        };

    private static UnionModel RenameTypes(UnionModel model, Dictionary<string, string> typeNames) =>
        model with
        {
            Members = model
                .Members.Select(member => member with { Type = RenameType(member.Type, typeNames) })
                .ToEquatableArray(),
        };

    private static TypeModel RenameType(TypeModel type, Dictionary<string, string> typeNames) =>
        type with
        {
            FullName = type.FullName is { } fullName ? typeNames[fullName] : null,
            Element = type.Element is { } element ? RenameType(element, typeNames) : null,
        };

    private sealed class NamespaceNode(string segment)
    {
        public string Segment { get; } = segment;

        public Dictionary<string, NamespaceNode> Children { get; } = new(StringComparer.Ordinal);

        public List<DeclarationModel> Declarations { get; } = [];

        /// <summary>Nested namespaces and declarations, in order of first appearance.</summary>
        public List<object> Order { get; } = [];
    }
}
