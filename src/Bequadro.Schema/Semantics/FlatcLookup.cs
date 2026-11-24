using Bequadro.Schema.Syntax;

namespace Bequadro.Schema.Semantics;

/// <summary>
/// Replays, for one file, the symbol lookups <c>flatc</c> makes while it parses that file (<c>idl_parser.cpp</c>), so the
/// binder can tell where <c>flatc</c> would resolve a name differently or not at all (SCH-012). <c>flatc</c> reads a file
/// once, from top to bottom, after the files it includes, and looks names up among what it has read so far:
/// <list type="bullet">
/// <item><c>ParseTypeIdent</c> looks for an enum or union in the current namespace and then each enclosing one, and only
/// if there is none for a table or struct (<c>LookupCreateStruct</c>).</item>
/// <item><c>LookupCreateStruct</c> returns a pending forward reference with the name as written, then a table or struct
/// in the current namespace, then one in each enclosing namespace. If there is none, it creates a forward reference
/// under the name as written, which the next table or struct declared with that name, or with that full name, takes
/// over (<c>StartStruct</c>), in whatever namespace it is.</item>
/// <item><c>ParseField</c> rejects a field whose name <c>LookupCreateStruct</c> finds, forward references included.</item>
/// <item><c>SetRootType</c> looks the name up as a full name, then in the current namespace only.</item>
/// </list>
/// The declarations of other files it can see are the ones <c>flatc</c> has read before this file whichever file it
/// compiles. Names are looked up through <see cref="NameCandidates"/>, so a lookup doesn't build every candidate name.
/// </summary>
internal sealed class FlatcLookup
{
    private readonly Func<string, IEnumerable<Declaration>> _earlier;

    // The tables, structs and forward references under the keys flatc keeps them, and those keys by their last segment
    private readonly Dictionary<string, Entry> _structs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _structKeys = new(StringComparer.Ordinal);

    // The enums and unions this file has declared so far, by name
    private readonly Dictionary<string, List<Declaration>> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<Declaration, Entry> _earlierEntries = [];
    private readonly Dictionary<int, Result> _types = [];
    private readonly Dictionary<int, Entry> _fieldNames = [];
    private readonly Dictionary<int, Result> _rootTypes = [];
    private string _namespace = "";

    private FlatcLookup(Func<string, IEnumerable<Declaration>> earlier) => _earlier = earlier;

    /// <param name="file">The file.</param>
    /// <param name="declarations">The declarations of the file that the binder registered, in order.</param>
    /// <param name="earlier">The registered declarations named like a name's last segment that are in files flatc reads
    /// before this one.</param>
    public static FlatcLookup Replay(
        SchemaFile file,
        IReadOnlyList<Declaration> declarations,
        Func<string, IEnumerable<Declaration>> earlier
    )
    {
        var lookup = new FlatcLookup(earlier);
        var rootTypes = 0;
        foreach (var declaration in declarations)
        {
            for (
                ;
                rootTypes < file.RootTypes.Count && file.RootTypes[rootTypes].Span.Start < declaration.Position;
                rootTypes++
            )
            {
                lookup.ReplayRootType(file.RootTypes[rootTypes]);
            }

            lookup.ReplayDeclaration(declaration);
        }

        for (; rootTypes < file.RootTypes.Count; rootTypes++)
        {
            lookup.ReplayRootType(file.RootTypes[rootTypes]);
        }

        return lookup;
    }

    /// <summary>What flatc found for a type reference, or null if the reference names a scalar or wasn't replayed.</summary>
    public Result? Type(TypeSyntax type) => _types.TryGetValue(type.Span.Start, out var result) ? result : null;

    /// <summary>The table or struct, or forward reference to one, that makes flatc reject a field's name.</summary>
    public Entry? FieldNameClash(FieldSyntax field) =>
        _fieldNames.TryGetValue(field.NameSpan.Start, out var entry) ? entry : null;

    /// <summary>What flatc found for a <c>root_type</c> statement, or null if it found nothing.</summary>
    public Result? RootType(RootTypeSyntax rootType) =>
        _rootTypes.TryGetValue(rootType.Span.Start, out var result) ? result : null;

    private void ReplayRootType(RootTypeSyntax rootType)
    {
        _namespace = rootType.Namespace;
        if ((Struct(rootType.Name) ?? QualifiedStruct(rootType.Name)) is { } entry)
        {
            _rootTypes[rootType.Span.Start] = new Result(null, entry, entry.IsPredeclared);
        }
    }

    private void ReplayDeclaration(Declaration declaration)
    {
        var syntax = declaration.Syntax;
        _namespace = syntax.Namespace;
        switch (syntax)
        {
            case ObjectSyntax objectSyntax:
                Define(declaration);
                foreach (var field in objectSyntax.Fields)
                {
                    if (LookupCreateStruct(field.Name, create: false) is { } clash)
                    {
                        _fieldNames[field.NameSpan.Start] = clash;
                    }

                    ReplayType(field.Type);
                }

                break;
            case EnumSyntax:
                AddEnum(declaration);
                break;
            case UnionSyntax union:
                AddEnum(declaration);
                foreach (var member in union.Members)
                {
                    if (member.Label is null && member.Type.Name is { } name)
                    {
                        var entry = LookupCreateStruct(name, create: true)!;
                        _types[member.Type.Span.Start] = new Result(null, entry, entry.IsPredeclared);
                    }
                    else
                    {
                        ReplayType(member.Type);
                    }
                }

                break;
        }
    }

    private void ReplayType(TypeSyntax type)
    {
        if (type.Element is { } element)
        {
            ReplayType(element);
            return;
        }

        if (type.Name is not { } name || name == "string" || ScalarInfo.TryGet(name, out _))
        {
            return;
        }

        if (LookupEnum(name) is { } enumDeclaration)
        {
            _types[type.Span.Start] = new Result(enumDeclaration, null, false);
            return;
        }

        var entry = LookupCreateStruct(name, create: true)!;
        _types[type.Span.Start] = new Result(null, entry, entry.IsPredeclared);
    }

    /// <summary><c>StartStruct</c>: a declaration takes over a forward reference written as its name or full name.</summary>
    private void Define(Declaration declaration)
    {
        var name = declaration.Syntax.Name;
        var qualified = declaration.FullName;
        if (Local(name) is { IsPredeclared: true } byName)
        {
            _structs.Remove(name);
            if (_structs.TryGetValue(qualified, out var replaced) && replaced.IsPredeclared)
            {
                // SymbolTable::Move overwrites it, so nothing can take it over any more
                replaced.IsOrphaned = true;
            }

            SetStruct(qualified, byName);
            byName.Take(declaration);
            return;
        }

        if (Local(qualified) is { IsPredeclared: true } byQualifiedName)
        {
            byQualifiedName.Take(declaration);
            return;
        }

        SetStruct(qualified, new Entry(qualified, declaration));
    }

    private Entry? LookupCreateStruct(string name, bool create)
    {
        if (Struct(name) is { IsPredeclared: true } byName)
        {
            return byName;
        }

        var entry = QualifiedStruct(name);
        if (entry is not null)
        {
            return entry;
        }

        // LookupStructThruParentNamespaces: the enclosing namespaces, then the root namespace when there is one
        if (_namespace.Length > 0)
        {
            entry = InnermostStruct(name, _namespace.Length - 1)?.Entry;
        }

        if (entry is null && create)
        {
            entry = new Entry(name, null);
            SetStruct(name, entry);
        }

        return entry;
    }

    /// <summary><c>LookupEnum</c>: an enum or union in the current namespace or the innermost enclosing one.</summary>
    private Declaration? LookupEnum(string name)
    {
        var last = NameCandidates.LastSegment(name);
        var local = _enums.TryGetValue(last, out var named) ? named : [];
        return NameCandidates
            .Innermost(local.Concat(_earlier(name).Where(d => d.IsEnumOrUnion)), d => d.FullName, _namespace, name)
            ?.Item;
    }

    private void AddEnum(Declaration declaration)
    {
        if (!_enums.TryGetValue(declaration.Syntax.Name, out var named))
        {
            _enums[declaration.Syntax.Name] = named = [];
        }

        named.Add(declaration);
    }

    /// <summary>The entry under the name qualified by the current namespace.</summary>
    private Entry? QualifiedStruct(string name) =>
        InnermostStruct(name, _namespace.Length) is { } found && found.Rank == _namespace.Length ? found.Entry : null;

    /// <summary>
    /// The entry under the innermost candidate for a name no further in than <paramref name="maxRank"/>
    /// (<see cref="NameCandidates.Rank"/>): one of this file's, or a table or struct of a file read before it.
    /// </summary>
    private (Entry Entry, int Rank)? InnermostStruct(string name, int maxRank)
    {
        var local = _structKeys.TryGetValue(NameCandidates.LastSegment(name), out var keys)
            ? keys.Where(_structs.ContainsKey).Select(key => (Key: key, Entry: _structs[key]))
            : [];
        var earlier = _earlier(name)
            .Where(d => d.IsTableOrStruct)
            .Select(d => (Key: d.FullName, Entry: EarlierEntry(d)));
        return NameCandidates.Innermost(local.Concat(earlier), pair => pair.Key, _namespace, name, maxRank) is { } found
            ? (found.Item.Entry, found.Rank)
            : null;
    }

    /// <summary>The entry under a key: one of this file's, or a table or struct of a file read before it.</summary>
    private Entry? Struct(string key) =>
        Local(key)
        ?? (
            _earlier(key).FirstOrDefault(d => d.IsTableOrStruct && d.FullName == key) is { } declaration
                ? EarlierEntry(declaration)
                : null
        );

    private Entry EarlierEntry(Declaration declaration)
    {
        if (!_earlierEntries.TryGetValue(declaration, out var entry))
        {
            _earlierEntries[declaration] = entry = new Entry(declaration.FullName, declaration);
        }

        return entry;
    }

    private Entry? Local(string key) => _structs.TryGetValue(key, out var entry) ? entry : null;

    private void SetStruct(string key, Entry entry)
    {
        _structs[key] = entry;
        var last = NameCandidates.LastSegment(key);
        if (!_structKeys.TryGetValue(last, out var keys))
        {
            _structKeys[last] = keys = [];
        }

        keys.Add(key);
    }

    /// <summary>A table or struct, or a forward reference to one, under the key flatc keeps it.</summary>
    internal sealed class Entry(string key, Declaration? declaration)
    {
        /// <summary>The name it was first looked up or declared by.</summary>
        public string Key { get; } = key;

        /// <summary>The declaration, once one has taken over the forward reference.</summary>
        public Declaration? Declaration { get; private set; } = declaration;

        public bool IsPredeclared => Declaration is null;

        /// <summary>Whether a declaration replaced it under its key before taking it over, so flatc reports it.</summary>
        public bool IsOrphaned { get; set; }

        public void Take(Declaration declaration) => Declaration = declaration;
    }

    /// <param name="Enum">The enum or union found.</param>
    /// <param name="Struct">Otherwise, the table or struct, or forward reference, found.</param>
    /// <param name="WasForward">Whether <see cref="Struct"/> was a forward reference when it was looked up.</param>
    internal sealed record Result(Declaration? Enum, Entry? Struct, bool WasForward);
}
