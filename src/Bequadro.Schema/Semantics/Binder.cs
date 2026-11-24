using System.Collections.Immutable;
using System.Globalization;
using Bequadro.Schema.Common;
using Bequadro.Schema.Diagnostics;
using Bequadro.Schema.Syntax;

namespace Bequadro.Schema.Semantics;

/// <summary>
/// The result of binding a schema set: semantic diagnostics plus a model per declaration.
/// Declaration models are only meaningful when the whole schema set (including parse results) has no errors.
/// </summary>
internal sealed record BindResult(
    EquatableArray<SchemaDiagnostic> Diagnostics,
    EquatableArray<DeclarationModel> Declarations
);

/// <summary>
/// Resolves includes, names and types across a schema set, validates it, and builds declaration models.
/// </summary>
internal sealed class Binder
{
    private static readonly HashSet<string> UnsupportedAttributes =
    [
        "id",
        "deprecated",
        "key",
        "bit_flags",
        "force_align",
        "nested_flatbuffer",
        "flexbuffer",
        "hash",
        "original_order",
        "offset64",
        "vector64",
    ];

    private static readonly HashSet<string> IgnoredBuiltInAttributes =
    [
        "required",
        "native_inline",
        "native_default",
        "native_custom_alloc",
        "native_type",
        "native_type_pack_name",
        "cpp_type",
        "cpp_ptr_type",
        "cpp_ptr_type_get",
        "cpp_str_type",
        "cpp_str_flex_ctor",
        "shared",
        "csharp_partial",
        "streaming",
        "idempotent",
        "private",
    ];

    /// <summary>
    /// Bequadro's own attributes. Every attribute starting with <see cref="BequadroAttributePrefix"/> is reserved.
    /// </summary>
    public const string BequadroAttributePrefix = "bequadro_";

    public const string ExcludeNoneAttribute = "bequadro_exclude_none";

    public const string ExhaustiveAttribute = "bequadro_exhaustive";

    public const string ValueEqualityAttribute = "bequadro_value_equality";

    /// <summary>
    /// The label of a union's implicit zero member, and the value flatc gives an enum declared without values.
    /// </summary>
    public const string NoneLabel = "NONE";

    /// <summary>
    /// The suffix of the field flatc adds for a union field's discriminator.
    /// </summary>
    private const string UnionTypeSuffix = "_type";

    /// <summary>
    /// The kind of declaration each Bequadro attribute is valid on.
    /// </summary>
    private static readonly Dictionary<string, DeclarationKind> BequadroAttributes = new(StringComparer.Ordinal)
    {
        [ExcludeNoneAttribute] = DeclarationKind.Union,
        [ExhaustiveAttribute] = DeclarationKind.Union,
        [ValueEqualityAttribute] = DeclarationKind.Table,
    };

    /// <summary>
    /// The field names flatc rejects next to a field of some kind, because its generated code would use them for that
    /// field (<c>CheckClash</c> in <c>idl_parser.cpp</c>): a suffix, and the kind of field it applies to.
    /// </summary>
    private static readonly (string Suffix, Func<TypeModel, bool> AppliesTo)[] GeneratedSuffixes =
    [
        ("_type", type => type.Kind == TypeKind.Union),
        ("Type", type => type.Kind == TypeKind.Union),
        ("_length", type => type.Kind == TypeKind.Vector),
        ("Length", type => type.Kind == TypeKind.Vector),
        ("_byte_vector", type => type.Kind == TypeKind.String),
        ("ByteVector", type => type.Kind == TypeKind.String),
    ];

    /// <summary>
    /// The largest struct that fits in a table as its only field. A table's inline data is at most 65,535 bytes, because
    /// vtable entries are 16-bit, and it also holds a 4-byte soffset and up to 4 bytes of padding before an 8-byte-aligned
    /// struct. The limit also keeps layout arithmetic far from overflowing.
    /// </summary>
    private const int MaxStructSize = 65520;

    private readonly List<SchemaDiagnostic> _diagnostics = [];
    private readonly Dictionary<string, SchemaFile> _filesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _keysIgnoringCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _visibleFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _earlierFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _earlierAttributes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Declaration> _declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Declaration>> _declarationsByFile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlatcLookup> _lookups = new(StringComparer.Ordinal);

    // The declarations with each unqualified name, to look names up by (NameCandidates)
    private readonly Dictionary<string, List<Declaration>> _declarationsByName = new(StringComparer.Ordinal);

    // In the order of their files' normalized paths, then of their positions: the order that renaming relies on (NAM-002)
    private readonly List<Declaration> _declarationOrder = [];
    private readonly Dictionary<Declaration, StructLayout?> _structLayouts = [];
    private readonly Dictionary<Declaration, List<ResolvedName?>> _structFieldTargets = [];
    private readonly Dictionary<Declaration, EnumInfo?> _enums = [];
    private readonly Dictionary<Declaration, UnionModel> _unions = [];

    private readonly SchemaOptions _options;

    private Binder(SchemaOptions options) => _options = options;

    /// <summary>
    /// Binds a schema set. Models are in declaration order: by the normalized path of their files, then by position.
    /// </summary>
    public static BindResult Bind(ImmutableArray<ParsedFile> parsedFiles, SchemaOptions options)
    {
        var binder = new Binder(options);
        var models = binder.BindAll(parsedFiles.Select(p => p.File).OrderBy(f => f.Path, StringComparer.Ordinal));
        return new BindResult(binder._diagnostics.ToEquatableArray(), models.ToEquatableArray());
    }

    private List<DeclarationModel> BindAll(IEnumerable<SchemaFile> files)
    {
        foreach (var file in files)
        {
            var key = PathUtilities.Normalize(file.Path);
            if (_filesByKey.TryGetValue(key, out var first))
            {
                Report(DiagnosticIds.DuplicateFile, new SourceSpan(file.Path, 0, 0, 0, 0, 0, 0), file.Path, first.Path);
                continue;
            }

            _filesByKey[key] = file;
            if (!_keysIgnoringCase.TryGetValue(key, out var keys))
            {
                _keysIgnoringCase[key] = keys = [];
            }

            keys.Add(key);
        }

        var includes = _filesByKey.ToDictionary(pair => pair.Key, pair => ResolveIncludes(pair.Value));
        foreach (var key in _filesByKey.Keys)
        {
            _visibleFiles[key] = Reachable(key, includes, skip: null);
        }

        foreach (var key in _filesByKey.Keys)
        {
            _earlierFiles[key] = EarlierFiles(key, includes);
            _earlierAttributes[key] = new HashSet<string>(
                _earlierFiles[key].SelectMany(k => _filesByKey[k].DeclaredAttributes.Select(a => a.Name)),
                StringComparer.Ordinal
            );
        }

        CollectDeclarations();

        var models = new List<DeclarationModel>();
        foreach (var declaration in _declarationOrder)
        {
            CheckAttributes(declaration);
            DeclarationModel? model = declaration.Syntax switch
            {
                ObjectSyntax { Kind: DeclarationKind.Table } table => BindTable(declaration, table),
                ObjectSyntax structSyntax => BindStruct(declaration, structSyntax),
                EnumSyntax => BindEnum(declaration),
                UnionSyntax => GetUnion(declaration) is { Members.Count: > 0 } union ? union : null,
                _ => null,
            };

            if (model is not null)
            {
                models.Add(model with { SchemaFullName = declaration.Syntax.FullName });
            }
        }

        foreach (var pair in _filesByKey)
        {
            foreach (var rootType in pair.Value.RootTypes)
            {
                BindRootType(pair.Key, rootType);
            }
        }

        return models;
    }

    private List<string> ResolveIncludes(SchemaFile file)
    {
        var resolved = new List<string>();
        foreach (var include in file.Includes)
        {
            var target = PathUtilities.ResolveRelativeTo(file.Path, include.Path);
            if (_filesByKey.ContainsKey(target))
            {
                resolved.Add(target);
            }
            else if (_keysIgnoringCase.TryGetValue(target, out var candidates) && candidates.Count == 1)
            {
                resolved.Add(candidates[0]);
            }
            else
            {
                Report(DiagnosticIds.IncludeNotFound, include.Span, include.Path, target);
            }
        }

        return resolved;
    }

    /// <summary>
    /// The files reachable from a file through includes, itself included, without passing through those in
    /// <paramref name="skip"/>.
    /// </summary>
    private static HashSet<string> Reachable(
        string start,
        Dictionary<string, List<string>> includes,
        HashSet<string>? skip
    )
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            foreach (var next in includes[stack.Pop()])
            {
                if (skip?.Contains(next) != true && visited.Add(next))
                {
                    stack.Push(next);
                }
            }
        }

        return visited;
    }

    /// <summary>
    /// The files that flatc has completely read before it reads a file, whichever file it compiles, other than the file
    /// itself. flatc reads a file's includes before the file, except a file it is in the middle of reading, which can
    /// only be one in an include cycle with it. So those are the files the file reaches without passing through a file
    /// that also reaches it.
    /// </summary>
    private HashSet<string> EarlierFiles(string key, Dictionary<string, List<string>> includes)
    {
        var cycle = new HashSet<string>(
            _visibleFiles[key].Where(other => other != key && _visibleFiles[other].Contains(key)),
            StringComparer.Ordinal
        );
        var earlier = Reachable(key, includes, cycle);
        earlier.Remove(key);
        return earlier;
    }

    private void CollectDeclarations()
    {
        foreach (var pair in _filesByKey.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var inFile = new List<Declaration>();
            _declarationsByFile[pair.Key] = inFile;
            foreach (var syntax in pair.Value.Declarations)
            {
                if (_declarations.TryGetValue(syntax.FullName, out var existing))
                {
                    Report(
                        DiagnosticIds.DuplicateDeclaration,
                        syntax.NameSpan,
                        syntax.FullName,
                        existing.Syntax.NameSpan.ToString()
                    );
                    continue;
                }

                var declaration = new Declaration(syntax, pair.Key);
                _declarations[syntax.FullName] = declaration;
                _declarationOrder.Add(declaration);
                inFile.Add(declaration);
                if (!_declarationsByName.TryGetValue(syntax.Name, out var named))
                {
                    _declarationsByName[syntax.Name] = named = [];
                }

                named.Add(declaration);
            }
        }
    }

    /// <summary>
    /// What flatc looks up while it reads a file, replayed once per file when first needed.
    /// </summary>
    private FlatcLookup Lookup(string fileKey)
    {
        if (!_lookups.TryGetValue(fileKey, out var lookup))
        {
            var earlier = _earlierFiles[fileKey];
            _lookups[fileKey] = lookup = FlatcLookup.Replay(_filesByKey[fileKey], _declarationsByFile[fileKey], name =>
                DeclarationsNamed(name).Where(d => earlier.Contains(d.FileKey))
            );
        }

        return lookup;
    }

    private void CheckAttributes(Declaration declaration)
    {
        var syntax = declaration.Syntax;
        CheckAttributes(declaration.FileKey, syntax.Attributes, syntax.Kind);
        switch (syntax)
        {
            case ObjectSyntax objectSyntax:
                foreach (var field in objectSyntax.Fields)
                {
                    CheckAttributes(declaration.FileKey, field.Attributes, declarationKind: null);
                }

                break;
            case EnumSyntax enumSyntax:
                foreach (var value in enumSyntax.Values)
                {
                    CheckAttributes(declaration.FileKey, value.Attributes, declarationKind: null);
                }

                break;
            case UnionSyntax unionSyntax:
                foreach (var member in unionSyntax.Members)
                {
                    CheckAttributes(declaration.FileKey, member.Attributes, declarationKind: null);
                }

                break;
        }
    }

    /// <param name="fileKey">The file the attributes are in.</param>
    /// <param name="attributes">The attributes.</param>
    /// <param name="declarationKind">The kind of declaration the attributes are on, or null if they are on a field, enum
    /// value or union member.</param>
    private void CheckAttributes(
        string fileKey,
        EquatableArray<AttributeSyntax> attributes,
        DeclarationKind? declarationKind
    )
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Name.StartsWith(BequadroAttributePrefix, StringComparison.Ordinal))
            {
                CheckBequadroAttribute(attribute, declarationKind);
            }
            else if (UnsupportedAttributes.Contains(attribute.Name))
            {
                Report(DiagnosticIds.UnsupportedFeature, attribute.Span, $"The '{attribute.Name}' attribute");
            }
            else if (
                !IgnoredBuiltInAttributes.Contains(attribute.Name)
                && !IsAttributeDeclaredBefore(fileKey, attribute.Name, attribute.Span.Start)
            )
            {
                Report(DiagnosticIds.UndeclaredAttribute, attribute.Span, attribute.Name);
            }
        }
    }

    /// <summary>
    /// Whether an attribute is declared before a position in a file, as flatc requires: earlier in the file, or in a file
    /// flatc reads before it.
    /// </summary>
    private bool IsAttributeDeclaredBefore(string fileKey, string name, int position) =>
        _earlierAttributes[fileKey].Contains(name)
        || _filesByKey[fileKey].DeclaredAttributes.Any(a => a.Name == name && a.Span.Start < position);

    /// <summary>
    /// Bequadro attributes need no declaration (a schema may still declare them for <c>flatc</c>), take no value,
    /// and are each valid on one kind of declaration. An unknown one is reported so that typos are caught.
    /// </summary>
    private void CheckBequadroAttribute(AttributeSyntax attribute, DeclarationKind? declarationKind)
    {
        if (!BequadroAttributes.TryGetValue(attribute.Name, out var validKind))
        {
            Report(
                DiagnosticIds.InvalidBequadroAttribute,
                attribute.Span,
                $"'{attribute.Name}' is not a Bequadro attribute; the supported attributes are '{ExcludeNoneAttribute}', '{ExhaustiveAttribute}' and '{ValueEqualityAttribute}'"
            );
        }
        else if (declarationKind != validKind)
        {
            Report(
                DiagnosticIds.InvalidBequadroAttribute,
                attribute.Span,
                $"The '{attribute.Name}' attribute is only valid on a {validKind.ToString().ToLowerInvariant()} declaration"
            );
        }
        else if (attribute.Value is not null)
        {
            Report(
                DiagnosticIds.InvalidBequadroAttribute,
                attribute.Span,
                $"The '{attribute.Name}' attribute does not take a value"
            );
        }
    }

    private TableModel BindTable(Declaration declaration, ObjectSyntax syntax)
    {
        // Resolve every field first: which names clash depends on the kinds of the other fields
        var types = syntax
            .Fields.Select(field => ResolveFieldType(declaration, field.Type, allowVector: true))
            .ToList();
        var unionTypeFields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < types.Count; i++)
        {
            if (IsUnionOrUnionVector(types[i]))
            {
                unionTypeFields[syntax.Fields[i].Name + UnionTypeSuffix] = syntax.Fields[i].Name;
            }
        }

        var fields = new List<TableFieldModel>();
        var slot = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < syntax.Fields.Count; i++)
        {
            var field = syntax.Fields[i];
            var type = types[i];
            if (IsUnionOrUnionVector(type))
            {
                slot++;
            }

            var fieldSlot = slot++;
            var named = AddMemberName(names, field.Name, field.NameSpan, syntax.FullName);
            var clash = named && CheckFieldNameClash(declaration, syntax, field, types, unionTypeFields);
            if (!named || clash || type is null)
            {
                continue;
            }

            var isRequired = field.Attributes.Any(a => a.Name == "required");
            if (isRequired && type.IsInline && type.Kind != TypeKind.Struct)
            {
                Report(
                    DiagnosticIds.InvalidRequired,
                    field.NameSpan,
                    field.Name,
                    "only non-scalar fields can be required"
                );
                isRequired = false;
            }

            // A scalar without a default is only required in C#: on the wire it is still absent when it equals 0
            var isRequiredMember =
                isRequired
                || (
                    _options.RequireScalarsWithoutDefault
                    && type.Kind is TypeKind.Scalar or TypeKind.Enum
                    && field.Default is null
                );
            var (isOptionalScalar, defaultValue) = BindDefault(field, type);
            fields.Add(
                new TableFieldModel(
                    field.Name,
                    field.Name,
                    type,
                    fieldSlot,
                    isRequired,
                    isRequiredMember,
                    isOptionalScalar,
                    defaultValue,
                    field.Documentation
                )
            );
        }

        return new TableModel(
            syntax.Namespace,
            syntax.Name,
            syntax.Documentation,
            fields.ToEquatableArray(),
            HasValueEquality: HasAttribute(syntax, ValueEqualityAttribute)
        );
    }

    private static bool IsUnionOrUnionVector(TypeModel? type) =>
        type?.Kind == TypeKind.Union || type?.Element?.Kind == TypeKind.Union;

    /// <summary>
    /// Reports a table field whose name flatc rejects although no other field has it, and returns whether it did: the
    /// name of the field flatc adds for a union's discriminator, a name flatc's generated code uses for another field, or
    /// the name of a table or struct.
    /// </summary>
    private bool CheckFieldNameClash(
        Declaration declaration,
        ObjectSyntax syntax,
        FieldSyntax field,
        List<TypeModel?> types,
        Dictionary<string, string> unionTypeFields
    )
    {
        string? reason = null;
        if (unionTypeFields.TryGetValue(field.Name, out var union))
        {
            reason = $"is the name of the field flatc adds for the discriminator of union field '{union}'";
        }
        else
        {
            foreach (var (suffix, appliesTo) in GeneratedSuffixes)
            {
                if (field.Name.Length <= suffix.Length || !field.Name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var prefix = field.Name.Substring(0, field.Name.Length - suffix.Length);
                var index = syntax.Fields.ToList().FindIndex(f => f.Name == prefix);
                var applies =
                    (index >= 0 && types[index] is { } type && appliesTo(type))
                    // A union vector's discriminator field is itself a vector
                    || (unionTypeFields.ContainsKey(prefix) && appliesTo(new TypeModel(TypeKind.Vector)));
                if (applies)
                {
                    reason = $"clashes with the code flatc generates for field '{prefix}'";
                    break;
                }
            }
        }

        reason ??= ObjectNamedLikeField(declaration, field) is { } objectName
            ? $"has the name of table or struct '{objectName}'"
            : null;

        if (reason is null)
        {
            return false;
        }

        Report(DiagnosticIds.FieldNameClash, field.NameSpan, field.Name, syntax.FullName, reason);
        return true;
    }

    /// <summary>
    /// A table or struct that flatc finds when it looks up a field's name, which makes it reject the field
    /// (<c>ParseField</c>): one it has read, or a forward reference with that name. Because flatc may read a file in an
    /// include cycle with this one first, any table or struct the name could refer to from the field's namespace in a
    /// visible file counts too, unless it is declared after the field in the same file.
    /// </summary>
    private string? ObjectNamedLikeField(Declaration declaration, FieldSyntax field)
    {
        if (Lookup(declaration.FileKey).FieldNameClash(field) is { } entry)
        {
            return entry.Declaration?.FullName ?? entry.Key;
        }

        var visible = _visibleFiles[declaration.FileKey];
        return Innermost(declaration.Syntax.Namespace, field.Name, found =>
            found.IsTableOrStruct
            && visible.Contains(found.FileKey)
            && !(found.FileKey == declaration.FileKey && found.Position > field.NameSpan.Start)
        )?.FullName;
    }

    private (bool IsOptionalScalar, ConstantModel? Value) BindDefault(FieldSyntax field, TypeModel type)
    {
        var value = field.Default;
        var isScalarLike = type.Kind is TypeKind.Scalar or TypeKind.Enum;

        if (value is null)
        {
            if (
                type.Kind == TypeKind.Enum
                && GetEnum(_declarations[type.FullName!]) is { } enumInfo
                && !enumInfo.HasZero
            )
            {
                Report(DiagnosticIds.EnumWithoutZero, field.NameSpan, field.Name, type.FullName!);
            }

            return (false, null);
        }

        if (!isScalarLike)
        {
            Report(
                DiagnosticIds.DefaultNotAllowed,
                value.Span,
                field.Name,
                "only scalar and enum fields can have defaults"
            );
            return (false, null);
        }

        if (value is { Kind: ValueKind.Identifier, Text: "null" })
        {
            if (
                type.Kind == TypeKind.Enum
                && GetEnum(_declarations[type.FullName!]) is { } enumInfo
                && enumInfo.Members.Any(m => m.Name == "null")
            )
            {
                // flatc: "the default 'null' is reserved for declaring optional scalar fields"
                ReportInvalidDefault(
                    field,
                    value,
                    $"'null' is a value of enum '{type.FullName}', so it can't make the field optional"
                );
                return (false, null);
            }

            return (true, null);
        }

        return (
            false,
            type.Kind == TypeKind.Enum
                ? BindEnumDefault(field, value, type)
                : BindScalarDefault(field, value, type.Scalar)
        );
    }

    private ConstantModel? BindScalarDefault(FieldSyntax field, ValueSyntax value, ScalarKind scalar)
    {
        if (scalar == ScalarKind.Bool)
        {
            switch (value)
            {
                case { Kind: ValueKind.Identifier, Text: "true" } or { Kind: ValueKind.Integer, Text: "1" }:
                    return new ConstantModel(Bool: true);
                case { Kind: ValueKind.Identifier, Text: "false" } or { Kind: ValueKind.Integer, Text: "0" }:
                    return null;
                default:
                    ReportInvalidDefault(field, value, "expected true, false, 0 or 1");
                    return null;
            }
        }

        if (ScalarInfo.IsFloat(scalar))
        {
            if (value.Kind is ValueKind.String || !Constants.TryParseFloat(value.Text, scalar, out var number))
            {
                var isHexadecimal =
                    value.Kind == ValueKind.Integer
                    && value.Text.IndexOf("0x", StringComparison.OrdinalIgnoreCase) >= 0;
                ReportInvalidDefault(
                    field,
                    value,
                    isHexadecimal ? "a floating-point field can't have a hexadecimal default" : "expected a number"
                );
                return null;
            }

            // The number is already at the field's precision: a literal too small for a float is zero, and one too
            // large is infinity. Negative zero is kept, as flatc keeps it, because it reads differently (SER-004).
            return number == 0 && !Constants.IsNegativeZero(number) ? null : new ConstantModel(Float: number);
        }

        if (value.Kind != ValueKind.Integer || !Constants.TryParseInteger(value.Text, out var integer))
        {
            ReportInvalidDefault(field, value, "expected an integer");
            return null;
        }

        if (!Constants.IsInRange(integer, scalar))
        {
            ReportInvalidDefault(field, value, $"out of range for {ScalarInfo.CSharpName(scalar)}");
            return null;
        }

        return integer == 0 ? null : new ConstantModel(Integer: integer);
    }

    private ConstantModel? BindEnumDefault(FieldSyntax field, ValueSyntax value, TypeModel type)
    {
        if (GetEnum(_declarations[type.FullName!]) is not { } enumInfo)
        {
            return null;
        }

        EnumMemberInfo? member = value.Kind switch
        {
            ValueKind.Identifier => enumInfo.Members.FirstOrDefault(m => m.Name == value.Text),
            ValueKind.Integer when Constants.TryParseInteger(value.Text, out var integer) =>
                enumInfo.Members.FirstOrDefault(m => m.Value == integer),
            _ => null,
        };

        if (member is null)
        {
            ReportInvalidDefault(field, value, $"not a value of enum '{type.FullName}'");
            return null;
        }

        return member.Value == 0 ? null : new ConstantModel(Integer: member.Value, EnumMember: member.Name);
    }

    private void ReportInvalidDefault(FieldSyntax field, ValueSyntax value, string reason) =>
        Report(DiagnosticIds.InvalidDefault, value.Span, value.Text, field.Name, reason);

    private StructModel? BindStruct(Declaration declaration, ObjectSyntax syntax)
    {
        var layout = GetStructLayout(declaration);
        foreach (var field in syntax.Fields)
        {
            if (field.Attributes.FirstOrDefault(a => a.Name == "required") is { } required)
            {
                Report(DiagnosticIds.InvalidRequired, required.Span, field.Name, "struct fields cannot be required");
            }

            if (field.Default is { } defaultValue)
            {
                Report(
                    DiagnosticIds.DefaultNotAllowed,
                    defaultValue.Span,
                    field.Name,
                    "struct fields cannot have defaults"
                );
            }
        }

        if (layout is null)
        {
            return null;
        }

        var fields = layout
            .Fields.Select(placed => new StructFieldModel(
                placed.Field.Name,
                placed.Field.Name,
                placed.Type,
                placed.Offset,
                placed.Field.Documentation
            ))
            .ToEquatableArray();
        return new StructModel(
            syntax.Namespace,
            syntax.Name,
            syntax.Documentation,
            layout.Size,
            layout.Alignment,
            fields
        );
    }

    /// <summary>
    /// Computes a struct's layout following the FlatBuffers rules, after those of the structs it contains. Fields in error
    /// are left out, and a struct left with no fields has no layout, so it isn't generated and fields of its type are left
    /// out too. The structs it contains are laid out with an explicit stack, not recursion, so a long chain of nested
    /// structs can't overflow the stack (DIA-001).
    /// </summary>
    private StructLayout? GetStructLayout(Declaration declaration)
    {
        if (_structLayouts.TryGetValue(declaration, out var cached))
        {
            return cached;
        }

        var inProgress = new HashSet<Declaration>();
        var stack = new Stack<Declaration>();
        stack.Push(declaration);
        while (stack.Count > 0)
        {
            var current = stack.Peek();
            if (_structLayouts.ContainsKey(current))
            {
                stack.Pop();
            }
            else if (inProgress.Add(current))
            {
                foreach (var target in StructFieldTargets(current))
                {
                    if (
                        target?.Declaration is { Syntax: ObjectSyntax { Kind: DeclarationKind.Struct } } nested
                        && !_structLayouts.ContainsKey(nested)
                        && !inProgress.Contains(nested)
                    )
                    {
                        stack.Push(nested);
                    }
                }
            }
            else
            {
                _structLayouts[current] = ComputeStructLayout(current, inProgress);
                inProgress.Remove(current);
                stack.Pop();
            }
        }

        return _structLayouts[declaration];
    }

    /// <param name="declaration">The struct, whose nested structs are already laid out unless they contain it.</param>
    /// <param name="inProgress">The structs being laid out, which a struct can't contain.</param>
    private StructLayout? ComputeStructLayout(Declaration declaration, HashSet<Declaration> inProgress)
    {
        var syntax = (ObjectSyntax)declaration.Syntax;
        if (syntax.Fields.Count == 0)
        {
            Report(DiagnosticIds.EmptyStruct, syntax.NameSpan, syntax.FullName);
        }

        var targets = StructFieldTargets(declaration);
        var fields = new List<PlacedField>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var alignment = 1;
        var tooLarge = false;
        for (var i = 0; i < syntax.Fields.Count; i++)
        {
            var field = syntax.Fields[i];

            // Both are checked for every field, to report all their errors
            var type = StructFieldType(targets[i], inProgress);
            var named =
                AddMemberName(names, field.Name, field.NameSpan, syntax.FullName)
                && !CheckStructFieldNameClash(declaration, field);
            if (type is null || !named || tooLarge)
            {
                continue;
            }

            var fieldOffset = Align(offset, type.InlineAlignment);
            if (fieldOffset + type.InlineSize > MaxStructSize)
            {
                Report(
                    DiagnosticIds.StructTooLarge,
                    syntax.NameSpan,
                    syntax.FullName,
                    MaxStructSize.ToString(CultureInfo.InvariantCulture)
                );
                tooLarge = true;
                continue;
            }

            fields.Add(new PlacedField(field, type, fieldOffset));
            offset = fieldOffset + type.InlineSize;
            alignment = Math.Max(alignment, type.InlineAlignment);
        }

        return fields.Count == 0 ? null : new StructLayout(Align(offset, alignment), alignment, fields);
    }

    private bool CheckStructFieldNameClash(Declaration declaration, FieldSyntax field)
    {
        if (ObjectNamedLikeField(declaration, field) is not { } objectName)
        {
            return false;
        }

        Report(
            DiagnosticIds.FieldNameClash,
            field.NameSpan,
            field.Name,
            declaration.FullName,
            $"has the name of table or struct '{objectName}'"
        );
        return true;
    }

    private TypeModel? StructFieldType(ResolvedName? target, HashSet<Declaration> inProgress)
    {
        switch (target)
        {
            case null:
                return null;
            case { Scalar: { } scalar }:
                return TypeModel.ForScalar(scalar);
            case { Declaration: { Syntax: EnumSyntax } enumDeclaration }:
                return EnumType(enumDeclaration);
            case { Declaration: { } nested } when inProgress.Contains(nested):
                Report(DiagnosticIds.RecursiveStruct, nested.Syntax.NameSpan, nested.FullName);
                return null;
            default:
                return StructType(target.Declaration!);
        }
    }

    /// <summary>
    /// Resolves the types of a struct's fields once, reporting their errors once: a scalar, an enum or a struct, or null.
    /// </summary>
    private List<ResolvedName?> StructFieldTargets(Declaration declaration)
    {
        if (_structFieldTargets.TryGetValue(declaration, out var cached))
        {
            return cached;
        }

        var targets = ((ObjectSyntax)declaration.Syntax)
            .Fields.Select(f => ResolveStructFieldType(declaration, f))
            .ToList();
        _structFieldTargets[declaration] = targets;
        return targets;
    }

    private ResolvedName? ResolveStructFieldType(Declaration declaration, FieldSyntax field)
    {
        if (field.Type.IsFixedArray)
        {
            Report(DiagnosticIds.UnsupportedFeature, field.Type.Span, "Fixed-size arrays");
            return null;
        }

        if (field.Type.IsVector || field.Type.Name == "string")
        {
            Report(DiagnosticIds.InvalidStructFieldType, field.Type.Span, field.Name, field.Type.ToString());
            return null;
        }

        var resolved = Resolve(declaration, field.Type, ReferenceKind.StructField);
        switch (resolved)
        {
            case null:
            case { Scalar: not null }:
            case { Declaration.Syntax: EnumSyntax or ObjectSyntax { Kind: DeclarationKind.Struct } }:
                return resolved;
            default:
                Report(DiagnosticIds.InvalidStructFieldType, field.Type.Span, field.Name, field.Type.ToString());
                return null;
        }
    }

    private EnumModel? BindEnum(Declaration declaration)
    {
        if (GetEnum(declaration) is not { } info)
        {
            return null;
        }

        var syntax = (EnumSyntax)declaration.Syntax;
        var members = info
            .Members.Select(member => new EnumMemberModel(member.Name, member.Value, member.Documentation))
            .ToEquatableArray();
        return new EnumModel(syntax.Namespace, syntax.Name, syntax.Documentation, info.Underlying, members);
    }

    /// <summary>
    /// Validates an enum and computes its member values. Returns null if it has no valid underlying type.
    /// </summary>
    private EnumInfo? GetEnum(Declaration declaration)
    {
        if (_enums.TryGetValue(declaration, out var cached))
        {
            return cached;
        }

        var syntax = (EnumSyntax)declaration.Syntax;
        var info = ComputeEnum(syntax);
        _enums[declaration] = info;
        return info;
    }

    private EnumInfo? ComputeEnum(EnumSyntax syntax)
    {
        if (syntax.UnderlyingType is null)
        {
            Report(
                DiagnosticIds.InvalidEnum,
                syntax.NameSpan,
                syntax.FullName,
                "an underlying integer type is required"
            );
            return null;
        }

        if (
            syntax.UnderlyingType.Name is not { } underlyingName
            || !ScalarInfo.TryGet(underlyingName, out var underlying)
            || !ScalarInfo.IsInteger(underlying)
        )
        {
            Report(
                DiagnosticIds.InvalidEnum,
                syntax.UnderlyingType.Span,
                syntax.FullName,
                "the underlying type must be an integer type"
            );
            return null;
        }

        // flatc gives an enum declared without values the value NONE = 0 (ParseEnum)
        if (syntax.Values.Count == 0)
        {
            return new EnumInfo(underlying, [new EnumMemberInfo(NoneLabel, 0, EquatableArray<string>.Empty)]);
        }

        // Values in error are left out if C# would reject them. Values that aren't ascending are kept, because C# allows them.
        var members = new List<EnumMemberInfo>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        decimal? previous = null;

        foreach (var value in syntax.Values)
        {
            var unique = names.Add(value.Name);
            if (!unique)
            {
                Report(DiagnosticIds.DuplicateMember, value.NameSpan, value.Name, syntax.FullName);
            }

            decimal number;
            if (value.Value is null)
            {
                number = previous is { } p ? p + 1 : 0;
            }
            else if (value.Value.Kind != ValueKind.Integer || !Constants.TryParseInteger(value.Value.Text, out number))
            {
                Report(
                    DiagnosticIds.InvalidEnum,
                    value.Value.Span,
                    syntax.FullName,
                    $"'{value.Value.Text}' is not an integer"
                );
                continue;
            }

            if (!Constants.IsInRange(number, underlying))
            {
                Report(
                    DiagnosticIds.InvalidEnum,
                    value.NameSpan,
                    syntax.FullName,
                    $"value {number} of '{value.Name}' is out of range for {underlyingName}"
                );
                previous = number;
                continue;
            }

            if (previous is { } last && number <= last)
            {
                Report(
                    DiagnosticIds.InvalidEnum,
                    value.NameSpan,
                    syntax.FullName,
                    $"value {number} of '{value.Name}' must be greater than the previous value {last}"
                );
            }

            previous = number;
            if (unique)
            {
                members.Add(new EnumMemberInfo(value.Name, number, value.Documentation));
            }
        }

        return new EnumInfo(underlying, members);
    }

    /// <summary>
    /// Binds a union once, however many fields refer to it, so its diagnostics are reported once.
    /// </summary>
    private UnionModel GetUnion(Declaration declaration)
    {
        if (!_unions.TryGetValue(declaration, out var union))
        {
            _unions[declaration] = union = BindUnion(declaration, (UnionSyntax)declaration.Syntax);
        }

        return union;
    }

    private UnionModel BindUnion(Declaration declaration, UnionSyntax syntax)
    {
        var members = new List<UnionMemberModel>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var flatcLabels = new HashSet<string>(StringComparer.Ordinal) { NoneLabel };
        var memberTypes = new HashSet<string>(StringComparer.Ordinal);
        var previous = 0;

        if (syntax.Members.Count == 0)
        {
            Report(
                DiagnosticIds.InvalidUnion,
                syntax.NameSpan,
                syntax.FullName,
                "a union must have at least one member"
            );
        }

        if (syntax.Members.Count > 255)
        {
            Report(
                DiagnosticIds.InvalidUnion,
                syntax.NameSpan,
                syntax.FullName,
                "a union can have at most 255 members"
            );
        }

        // Members in error are left out
        foreach (var member in syntax.Members)
        {
            var label = member.EffectiveLabel;
            var keep = true;

            // Every union implicitly has the member NONE, as in flatc. Labels must be unique both as Bequadro names them
            // and as flatc does, which keeps the namespace of a qualified type name
            if (label == NoneLabel || !labels.Add(label))
            {
                Report(DiagnosticIds.DuplicateMember, member.Span, label, syntax.FullName);
                keep = false;
            }
            else if (!flatcLabels.Add(member.FlatcLabel))
            {
                Report(DiagnosticIds.DuplicateMember, member.Span, member.FlatcLabel, syntax.FullName);
                keep = false;
            }

            var discriminator = previous + 1;
            if (member.Value is { } explicitValue)
            {
                // flatc requires an integer token (ParseEnum)
                if (
                    explicitValue.Kind != ValueKind.Integer
                    || !Constants.TryParseInteger(explicitValue.Text, out var number)
                    || number <= previous
                    || number > 255
                )
                {
                    Report(
                        DiagnosticIds.InvalidUnion,
                        explicitValue.Span,
                        syntax.FullName,
                        $"discriminator '{explicitValue.Text}' must be an integer greater than {previous} and at most 255"
                    );
                    keep = false;
                    discriminator = previous;
                }
                else
                {
                    discriminator = (int)number;
                }
            }
            else if (discriminator > 255)
            {
                // With more than 255 members, the count was reported once instead
                if (syntax.Members.Count <= 255)
                {
                    Report(
                        DiagnosticIds.InvalidUnion,
                        member.Span,
                        syntax.FullName,
                        $"the discriminator of '{label}' is {discriminator}, but discriminators must be at most 255"
                    );
                }

                keep = false;
            }

            previous = discriminator;
            var type = ResolveUnionMemberType(declaration, syntax, member);
            if (type is null)
            {
                continue;
            }

            if (!memberTypes.Add(type.FullName!))
            {
                Report(DiagnosticIds.AmbiguousUnionMembers, member.Span, syntax.FullName, type.FullName!);
                continue;
            }

            if (keep)
            {
                members.Add(new UnionMemberModel(label, type, discriminator, member.Documentation));
            }
        }

        return new UnionModel(
            syntax.Namespace,
            syntax.Name,
            syntax.Documentation,
            members.ToEquatableArray(),
            AllowsNone(syntax),
            AllowsUnknown: !HasAttribute(syntax, ExhaustiveAttribute)
        );
    }

    private static bool AllowsNone(UnionSyntax union) => !HasAttribute(union, ExcludeNoneAttribute);

    private static bool HasAttribute(DeclarationSyntax declaration, string name) =>
        declaration.Attributes.Any(a => a.Name == name);

    private TypeModel? ResolveUnionMemberType(Declaration declaration, UnionSyntax union, UnionMemberSyntax member)
    {
        if (member.Type.Element is not null)
        {
            Report(
                DiagnosticIds.InvalidUnion,
                member.Type.Span,
                union.FullName,
                "union members must be tables or structs"
            );
            return null;
        }

        if (member.Type.Name == "string")
        {
            Report(DiagnosticIds.UnsupportedFeature, member.Type.Span, "Strings in unions");
            return null;
        }

        var kind = member.Label is null ? ReferenceKind.UnionMember : ReferenceKind.Field;
        var resolved = Resolve(declaration, member.Type, kind);

        switch (resolved?.Declaration?.Syntax)
        {
            case null when resolved is null:
                return null;
            case ObjectSyntax { Kind: DeclarationKind.Table }:
                return TableType(resolved!.Declaration!);
            case ObjectSyntax { Kind: DeclarationKind.Struct }:
                return StructType(resolved!.Declaration!);
            default:
                Report(
                    DiagnosticIds.InvalidUnion,
                    member.Type.Span,
                    union.FullName,
                    "union members must be tables or structs"
                );
                return null;
        }
    }

    /// <summary>
    /// Checks a <c>root_type</c> statement as flatc does (<c>SetRootType</c>): it names a table that is already declared,
    /// by its full name or by its name in the current namespace.
    /// </summary>
    private void BindRootType(string fileKey, RootTypeSyntax rootType)
    {
        var found = Lookup(fileKey).RootType(rootType);
        if (found is not { WasForward: false, Struct.Declaration.Syntax: ObjectSyntax { Kind: DeclarationKind.Table } })
        {
            Report(DiagnosticIds.InvalidRootType, rootType.Span, rootType.Name);
        }
    }

    private TypeModel? ResolveFieldType(Declaration declaration, TypeSyntax type, bool allowVector)
    {
        if (type.IsFixedArray)
        {
            Report(DiagnosticIds.UnsupportedFeature, type.Span, "Fixed-size arrays");
            return null;
        }

        if (type.IsVector)
        {
            if (!allowVector)
            {
                Report(DiagnosticIds.NestedVector, type.Span);
                return null;
            }

            var element = ResolveFieldType(declaration, type.Element!, allowVector: false);
            return element is null ? null : new TypeModel(TypeKind.Vector, Element: element);
        }

        if (type.Name == "string")
        {
            return TypeModel.String;
        }

        var resolved = Resolve(declaration, type, ReferenceKind.Field);

        return resolved switch
        {
            null => null,
            { Scalar: { } scalar } => TypeModel.ForScalar(scalar),
            { Declaration: { } target } => target.Syntax switch
            {
                EnumSyntax => EnumType(target),
                ObjectSyntax { Kind: DeclarationKind.Struct } => StructType(target),
                ObjectSyntax => TableType(target),
                // A union left with no members isn't generated
                UnionSyntax union when GetUnion(target).Members.Count > 0 => new TypeModel(
                    TypeKind.Union,
                    FullName: union.FullName,
                    UnionAllowsNone: AllowsNone(union)
                ),
                _ => null,
            },
            _ => null,
        };
    }

    private static TypeModel TableType(Declaration declaration) =>
        new(
            TypeKind.Table,
            FullName: declaration.Syntax.FullName,
            TableHasValueEquality: HasAttribute(declaration.Syntax, ValueEqualityAttribute)
        );

    private TypeModel? EnumType(Declaration declaration) =>
        GetEnum(declaration) is { } info
            ? new TypeModel(TypeKind.Enum, info.Underlying, declaration.Syntax.FullName)
            : null;

    private TypeModel? StructType(Declaration declaration) =>
        GetStructLayout(declaration) is { } layout
            ? new TypeModel(
                TypeKind.Struct,
                FullName: declaration.Syntax.FullName,
                StructSize: layout.Size,
                StructAlignment: layout.Alignment
            )
            : null;

    /// <summary>
    /// Resolves a type name (SCH-012). The name is tried in the current namespace, then each enclosing one, as flatc does,
    /// first as an enum or union and then as a table or struct. The first declaration found must be visible through
    /// includes, and flatc, which only knows what it has read so far, must resolve the name to the same declaration
    /// whichever file it compiles (<see cref="CheckDeclarationOrder"/>).
    /// </summary>
    private ResolvedName? Resolve(Declaration context, TypeSyntax type, ReferenceKind kind)
    {
        var name = type.Name!;
        if (ScalarInfo.TryGet(name, out var scalar))
        {
            return new ResolvedName(scalar, null);
        }

        // A union member written without a label is looked up as a table or struct only (LookupCreateStruct)
        var ns = context.Syntax.Namespace;
        var target =
            kind == ReferenceKind.UnionMember
                ? Innermost(ns, name, d => d.IsTableOrStruct) ?? Innermost(ns, name, d => d.IsEnumOrUnion)
                : Innermost(ns, name, d => d.IsEnumOrUnion) ?? Innermost(ns, name, d => d.IsTableOrStruct);
        if (target is null)
        {
            Report(DiagnosticIds.UnknownType, type.Span, name);
            return null;
        }

        if (!_visibleFiles[context.FileKey].Contains(target.FileKey))
        {
            Report(DiagnosticIds.TypeNotIncluded, type.Span, target.FullName, _filesByKey[target.FileKey].Path);
            return null;
        }

        if (CheckDeclarationOrder(context, type, target, kind) is { } reason)
        {
            Report(DiagnosticIds.DeclarationOrder, type.Span, target.FullName, reason);
            return null;
        }

        return new ResolvedName(null, target);
    }

    /// <summary>
    /// Compares the declaration a name resolves to with what flatc finds while reading the file. Returns why they may
    /// differ, or null if they don't.
    /// </summary>
    private string? CheckDeclarationOrder(Declaration context, TypeSyntax type, Declaration target, ReferenceKind kind)
    {
        // A union member that isn't a table or struct is reported as such
        if (
            Lookup(context.FileKey).Type(type) is not { } found
            || found.Enum == target
            || (kind == ReferenceKind.UnionMember && target.IsEnumOrUnion)
        )
        {
            return null;
        }

        if (target.IsEnumOrUnion || found.Struct is null)
        {
            return $"{WhereDeclared(context, target)}, and flatc needs an enum or union to be declared before it is used";
        }

        var entry = found.Struct;
        if (entry.IsOrphaned)
        {
            return "flatc can't resolve it, because a declaration takes over another forward reference to it first; write the name the same way in every use before its declaration";
        }

        if (kind == ReferenceKind.StructField && found.WasForward)
        {
            return $"{WhereDeclared(context, target)}, and flatc needs a struct to be declared before a struct that contains it";
        }

        if (entry.Declaration is { } other && other != target)
        {
            return $"flatc resolves the name to '{other.FullName}', because it only knows the declarations before this use; qualify the name";
        }

        if (!found.WasForward)
        {
            return null;
        }

        // A forward reference: flatc must resolve it to the target, whichever files it has read first
        if (Innermost(context.Syntax.Namespace, type.Name!, d => d.IsTableOrStruct && d != target) is { } alternative)
        {
            return $"{WhereDeclared(context, target)}, and flatc may resolve the name to '{alternative.FullName}', which it can read before it; qualify the name";
        }

        if (entry.Declaration is null)
        {
            var name = type.Name!;
            var writtenAsDeclared = name == target.Syntax.Name || name == target.FullName;
            if (target.FileKey == context.FileKey)
            {
                return $"{WhereDeclared(context, target)}, and flatc only resolves a name used before its declaration if it is written as the declaration's name or as '{target.FullName}'";
            }

            if (!writtenAsDeclared || DeclarationsNamed(target.Syntax.Name).Count(d => d.IsTableOrStruct) > 1)
            {
                return $"{WhereDeclared(context, target)}, so it must be written as its name or as '{target.FullName}', and no other table or struct may be named '{target.Syntax.Name}'";
            }
        }

        return null;
    }

    private string WhereDeclared(Declaration context, Declaration target) =>
        target.FileKey == context.FileKey
            ? "it is declared after it is used"
            : $"it is declared in '{_filesByKey[target.FileKey].Path}', which flatc may read after this file because of an include cycle";

    /// <summary>
    /// The innermost declaration a name written in a namespace can refer to that matches a predicate (SCH-012).
    /// </summary>
    private Declaration? Innermost(string currentNamespace, string name, Func<Declaration, bool> predicate) =>
        NameCandidates
            .Innermost(DeclarationsNamed(name).Where(predicate), d => d.FullName, currentNamespace, name)
            ?.Item;

    /// <summary>The declarations whose unqualified name is a name's last segment.</summary>
    private IReadOnlyList<Declaration> DeclarationsNamed(string name) =>
        _declarationsByName.TryGetValue(NameCandidates.LastSegment(name), out var named) ? named : [];

    /// <summary>
    /// Records a field name, and reports it if the type already has a field with that name. Returns false if it does.
    /// </summary>
    private bool AddMemberName(HashSet<string> names, string name, SourceSpan span, string typeName)
    {
        if (names.Add(name))
        {
            return true;
        }

        Report(DiagnosticIds.DuplicateMember, span, name, typeName);
        return false;
    }

    private static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

    private void Report(string id, SourceSpan span, params string[] arguments) =>
        _diagnostics.Add(new SchemaDiagnostic(id, span, arguments.ToEquatableArray()));

    /// <summary>
    /// Where a type name is used, which decides how flatc looks it up.
    /// </summary>
    private enum ReferenceKind
    {
        /// <summary>A table field's type, or a union member written with a label.</summary>
        Field,

        /// <summary>A struct field's type.</summary>
        StructField,

        /// <summary>A union member written without a label.</summary>
        UnionMember,
    }

    private sealed record ResolvedName(ScalarKind? Scalar, Declaration? Declaration);

    private sealed record PlacedField(FieldSyntax Field, TypeModel Type, int Offset);

    private sealed record StructLayout(int Size, int Alignment, List<PlacedField> Fields);

    private sealed record EnumMemberInfo(string Name, decimal Value, EquatableArray<string> Documentation);

    private sealed record EnumInfo(ScalarKind Underlying, List<EnumMemberInfo> Members)
    {
        public bool HasZero => Members.Any(m => m.Value == 0);
    }
}
