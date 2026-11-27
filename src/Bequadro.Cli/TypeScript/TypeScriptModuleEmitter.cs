using System.Globalization;
using Bequadro.Schema.Common;
using Bequadro.Schema.Semantics;

namespace Bequadro.Cli.TypeScript;

/// <summary>
/// Emits the module of one namespace (TS-002 to TS-010). Generated code refers to the runtime as <c>$bq</c>, to other
/// namespaces as <c>$n0</c>, <c>$n1</c>, ..., and to codecs as <c>$c_Name</c>: names a schema can't spell, because
/// schema identifiers have no <c>$</c>.
/// </summary>
internal sealed class TypeScriptModuleEmitter
{
    private readonly string _path;
    private readonly string _namespace;
    private readonly Dictionary<string, DeclarationModel> _declarations;
    private readonly GenerateOptions _options;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _valueImports = new(StringComparer.Ordinal);
    private bool _usesRuntime;

    public TypeScriptModuleEmitter(
        string path,
        string ns,
        Dictionary<string, DeclarationModel> declarations,
        GenerateOptions options
    )
    {
        _path = path;
        _namespace = ns;
        _declarations = declarations;
        _options = options;
    }

    public string Emit(IReadOnlyList<DeclarationModel> models)
    {
        var referenced = models
            .SelectMany(ReferencedTypes)
            .Select(t => NamespaceOf(t.FullName!))
            .Where(ns => ns != _namespace)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < referenced.Count; i++)
        {
            _aliases[referenced[i]] = "$n" + i.ToString(CultureInfo.InvariantCulture);
        }

        // The declarations come first and their codecs last, so that a reader sees the API without the codecs. The
        // companions only use the codecs inside functions, which run after the module has been evaluated
        var body = new SourceWriter("  ");
        var codecs = new SourceWriter("  ");
        foreach (var model in models)
        {
            body.Line();
            if (model is not EnumModel)
            {
                codecs.Line();
            }

            switch (model)
            {
                case EnumModel enumModel:
                    EmitEnum(body, enumModel);
                    break;
                case StructModel structModel:
                    EmitStruct(body, structModel);
                    EmitStructCodec(codecs, structModel);
                    break;
                case TableModel table:
                    EmitTable(body, table);
                    EmitTableCodec(codecs, table);
                    break;
                case UnionModel union:
                    EmitUnion(body, union);
                    EmitUnionCodec(codecs, union);
                    break;
            }
        }

        if (!codecs.IsEmpty)
        {
            body.Line();
            body.Line("// " + OutputDirectory.CodecsComment);
        }

        var file = new SourceWriter("  ");
        file.Line("// " + OutputDirectory.GeneratedComment);
        if (_usesRuntime || _aliases.Count > 0)
        {
            file.Line();
        }

        if (_usesRuntime)
        {
            file.Line($"import * as $bq from \"{Import(TypeScriptTarget.RuntimeFile)}\";");
        }

        foreach (var (ns, alias) in _aliases)
        {
            var kind = _valueImports.Contains(ns) ? "import" : "import type";
            file.Line($"{kind} * as {alias} from \"{Import(TypeScriptTarget.ModulePath(ns))}\";");
        }

        return file + body.ToString() + codecs;
    }

    private string Import(string target) => TypeScriptTarget.ImportSpecifier(_path, target, _options.ImportExtension);

    private static IEnumerable<TypeModel> ReferencedTypes(DeclarationModel model)
    {
        IEnumerable<TypeModel> types = model switch
        {
            StructModel structModel => structModel.Fields.Select(f => f.Type),
            TableModel table => table.Fields.Select(f => f.Type),
            UnionModel union => union.Members.Select(m => m.Type),
            _ => [],
        };
        return types.Select(t => t.Element ?? t).Where(t => t.FullName is not null);
    }

    // Enums (TS-006)

    private static void EmitEnum(SourceWriter w, EnumModel model)
    {
        Doc(w, model.Documentation, model.RenamedFrom);
        w.Open($"export const {model.Name} = {{");
        foreach (var member in model.Members)
        {
            Doc(w, member.Documentation, member.RenamedFrom);
            w.Line($"{member.Name}: {IntegerLiteral(member.Value, model.Underlying)},");
        }

        w.Close("} as const;");
        w.Line($"export type {model.Name} = (typeof {model.Name})[keyof typeof {model.Name}];");
    }

    // Structs (TS-005)

    private void EmitStruct(SourceWriter w, StructModel model)
    {
        _usesRuntime = true;
        var name = model.Name;

        Doc(w, model.Documentation, model.RenamedFrom);
        w.Open($"export interface {name} {{");
        foreach (var field in model.Fields)
        {
            Doc(w, field.Documentation, field.RenamedFrom);
            w.Line($"{field.Name}: {FieldType(field.Type, input: false)};");
        }

        w.Close("}");
        w.Line();
        w.Open($"export interface {model.DerivedName} {{");
        foreach (var field in model.Fields)
        {
            w.Line($"{field.Name}?: {FieldType(field.Type, input: true)} | undefined;");
        }

        w.Close("}");
        w.Line();

        Doc(w, model.Documentation, model.RenamedFrom);
        w.Open($"export const {name} = {{");
        var createType = string.Join(
            " ",
            model.Fields.Select(f => $"{f.Name}?: {FieldType(f.Type, false)} | undefined;")
        );
        w.Open($"create($init?: {{ {createType} }}): {name} {{");
        w.Open("return {");
        foreach (var field in model.Fields)
        {
            var zero = field.Type.Kind switch
            {
                TypeKind.Struct => $"{CompanionRef(field.Type)}.create()",
                TypeKind.Enum => EnumMember(field.Type, null),
                _ => ZeroOrConstant(field.Type.Scalar, null),
            };
            w.Line($"{field.Name}: $init?.{field.Name} ?? {zero},");
        }

        w.Close("};");
        w.Close("},");
        w.Open($"equals($a: {name}, $b: {name}): boolean {{");
        w.Line("return (");
        w.Indent();
        var comparisons = model.Fields.Select(f => FieldEquals(f.Type, f.Name, optional: false)).ToList();
        for (var i = 0; i < comparisons.Count; i++)
        {
            w.Line((i == 0 ? "" : "&& ") + comparisons[i]);
        }

        w.Unindent();
        w.Line(");");
        w.Close("},");
        w.Close("};");
    }

    private void EmitStructCodec(SourceWriter w, StructModel model)
    {
        var name = model.Name;
        w.Open($"export const {CodecName(name)}: $bq.StructCodec<{name}, {model.DerivedName}> = {{");
        w.Line($"name: {Literal(model.SchemaFullName)},");
        w.Line($"size: {model.Size},");
        w.Line($"alignment: {model.Alignment},");
        w.Open(
            $"write($view: $bq.View, $offset: number, $value: {model.DerivedName}, $owner: string, $field: string): void {{"
        );
        foreach (var field in model.Fields)
        {
            var at = field.Offset == 0 ? "$offset" : $"$offset + {field.Offset}";
            // The member's path, for messages. The first attempt at writing a struct passes "" as $field, and V8 joins
            // "" + ".x" without allocating; only a retry after an error passes the real path (TS-011)
            var member = $"\".{field.SchemaName}\"";
            w.Line(
                field.Type.Kind == TypeKind.Struct
                    ? $"$bq.writeNestedStruct({CodecRef(field.Type)}, $view, {at}, $value.{field.Name}, $owner, $field, {member});"
                    : $"$bq.writeStructField($view, {KindConstant(field.Type.Scalar)}, {at}, $value.{field.Name}, $owner, $field + {member});"
            );
        }

        w.Close("},");
        w.Open($"read($view: $bq.View, $offset: number): {name} {{");
        w.Open("return {");
        foreach (var field in model.Fields)
        {
            var at = field.Offset == 0 ? "$offset" : $"$offset + {field.Offset}";
            w.Line(
                field.Type.Kind == TypeKind.Struct
                    ? $"{field.Name}: {CodecRef(field.Type)}.read($view, {at}),"
                    : $"{field.Name}: $bq.cast<{FieldType(field.Type, false)}>($bq.readStructField($view, {KindConstant(field.Type.Scalar)}, {at})),"
            );
        }

        w.Close("};");
        w.Close("},");
        w.Close("};");
    }

    // Tables (TS-004)

    private void EmitTable(SourceWriter w, TableModel model)
    {
        _usesRuntime = true;
        var name = model.Name;
        var input = model.DerivedName!;

        Doc(w, model.Documentation, model.RenamedFrom);
        w.Open($"export interface {name} {{");
        foreach (var field in model.Fields)
        {
            Doc(w, field.Documentation, field.RenamedFrom);
            w.Line(Property(field, FieldType(field.Type, false), IsAlwaysPresent(field)));
        }

        w.Close("}");
        w.Line();
        Doc(w, model.Documentation, model.RenamedFrom);
        w.Open($"export interface {input} {{");
        foreach (var field in model.Fields)
        {
            w.Line(Property(field, FieldType(field.Type, true), field.IsRequired || field.IsRequiredMember));
        }

        w.Close("}");
        w.Line();

        Doc(w, model.Documentation, model.RenamedFrom);
        w.Open($"export const {name} = {{");
        EmitCreate(w, model);
        w.Open($"write($value: {input}, $options?: $bq.WriteOptions): $bq.OwnedBytes {{");
        w.Line($"return $bq.write({CodecName(name)}, $value, $options);");
        w.Close("},");
        w.Open($"parse($buffer: $bq.Bytes, $options?: $bq.ParseOptions): {name} {{");
        w.Line($"return $bq.parse({CodecName(name)}, $buffer, $options);");
        w.Close("},");
        if (model.HasValueEquality)
        {
            w.Open($"equals($a: {name}, $b: {name}): boolean {{");
            if (model.Fields.Count == 0)
            {
                w.Line("return $a === $b || ($a !== undefined && $b !== undefined);");
            }
            else
            {
                w.Line("return (");
                w.Indent();
                w.Line("$a === $b ||");
                w.Line("(");
                w.Indent();
                var comparisons = model.Fields.Select(f => FieldEquals(f.Type, f.Name, !IsAlwaysPresent(f))).ToList();
                for (var i = 0; i < comparisons.Count; i++)
                {
                    w.Line((i == 0 ? "" : "&& ") + comparisons[i]);
                }

                w.Unindent();
                w.Line(")");
                w.Unindent();
                w.Line(");");
            }

            w.Close("},");
        }

        w.Close("};");
    }

    private void EmitTableCodec(SourceWriter w, TableModel model)
    {
        var name = model.Name;
        w.Open($"export const {CodecName(name)}: $bq.TableCodec<{name}, {model.DerivedName}> = {{");
        w.Line($"name: {Literal(model.SchemaFullName)},");
        EmitTableWrite(w, model);
        w.Open($"empty(): {name} {{");
        w.Open($"return $bq.cast<{name}>({{");
        foreach (var field in model.Fields)
        {
            w.Line($"{field.Name}: {InitialValue(field)},");
        }

        w.Close("});");
        w.Close("},");
        EmitTableFill(w, model);
        w.Close("};");
    }

    /// <summary>
    /// Whether a field is always present on the table type: scalars and enums that aren't optional, and required fields.
    /// </summary>
    private static bool IsAlwaysPresent(TableFieldModel field) =>
        field.IsRequired || (field.Type.Kind is TypeKind.Scalar or TypeKind.Enum && !field.IsOptionalScalar);

    private static string Property(TableFieldModel field, string type, bool present) =>
        present ? $"{field.Name}: {type};" : $"{field.Name}?: {type} | undefined;";

    private void EmitCreate(SourceWriter w, TableModel model)
    {
        var parameters = model
            .Fields.Select(f => Property(f, FieldType(f.Type, false), f.IsRequired || f.IsRequiredMember))
            .ToList();
        var required = model.Fields.Any(f => f.IsRequired || f.IsRequiredMember);
        var init = required ? "$init" : "$init?";
        if (model.Fields.Count == 0)
        {
            w.Open($"create(): {model.Name} {{");
            w.Line("return {};");
        }
        else
        {
            w.Open($"create({init}: {{ {string.Join(" ", parameters)} }}): {model.Name} {{");
            w.Open("return {");
            foreach (var field in model.Fields)
            {
                var given = $"{init}.{field.Name}";
                var value =
                    field.Type.Kind is TypeKind.Scalar or TypeKind.Enum
                    && !field.IsOptionalScalar
                    && !field.IsRequiredMember
                        ? $"{given} ?? {DefaultLiteral(field)}"
                        : given;
                w.Line($"{field.Name}: {value},");
            }

            w.Close("};");
        }

        w.Close("},");
    }

    /// <summary>
    /// The most out-of-line fields a table's <c>write</c> keeps in locals. A wider table keeps them in an array, so that
    /// its stack frame doesn't grow with the table, and 64 nested tables fit on the default stack however wide they are
    /// (SER-003, TS-011).
    /// </summary>
    private const int MaxChildLocals = 64;

    /// <summary>
    /// Emits the codec's <c>write</c>. It writes the out-of-line children first and keeps their offsets until the table
    /// is started: in a local each (<c>$o2</c>, or <c>$u3</c> for a union's pair) for a table with up to
    /// <see cref="MaxChildLocals"/> of them, which is faster, and in the array <c>$o</c>, indexed by slot, for a wider
    /// one. Each field is read once, by the runtime call that writes it.
    /// </summary>
    private void EmitTableWrite(SourceWriter w, TableModel model)
    {
        var table = Literal(model.SchemaFullName);
        if (model.Fields.Count == 0)
        {
            w.Open("write($writer: $bq.Writer): number {");
            w.Line("$writer.startTable(0);");
            w.Line($"return $writer.endTable({table});");
            w.Close("},");
            return;
        }

        w.Open($"write($writer: $bq.Writer, $value: {model.DerivedName}): number {{");

        // Out-of-line children first, in declaration order, as the C# writer does (SER-005)
        var children = model.Fields.Where(f => !f.Type.IsInline).ToList();
        var locals = children.Count <= MaxChildLocals;
        if (children.Count > 0 && !locals)
        {
            w.Line("const $o: number[] = [];");
        }

        foreach (var field in children)
        {
            var slot = field.Slot;
            var fieldName = Literal(field.SchemaName);
            var value = field.IsRequired
                ? $"$bq.required($value.{field.Name}, {table}, {fieldName})"
                : $"$value.{field.Name}";
            var write = WriteChild(field.Type, value, table, fieldName);
            w.Line(
                (IsPair(field), locals) switch
                {
                    (true, true) => $"const $u{slot} = {write};",
                    (false, true) => $"const $o{slot} = {write};",
                    (true, false) => $"$bq.pair($o, {slot}, {write});",
                    (false, false) => $"$o[{slot}] = {write};",
                }
            );
        }

        w.Line($"$writer.startTable({model.SlotCount});");

        // Largest alignment first, then by slot; a union's discriminator sorts with the bytes (SER-005)
        var parts = model
            .Fields.SelectMany(f =>
                f.Type.Kind == TypeKind.Union
                    ? new[] { (Field: f, Slot: f.Slot - 1, Alignment: 1), (Field: f, f.Slot, Alignment: 4) }
                    : [(Field: f, f.Slot, Alignment: f.Type.InlineAlignment)]
            )
            .OrderByDescending(p => p.Alignment)
            .ThenBy(p => p.Slot);
        foreach (var (field, slot, _) in parts)
        {
            EmitAddField(w, field, slot, table, locals);
        }

        w.Line($"return $writer.endTable({table});");
        w.Close("},");
    }

    /// <summary>
    /// The call that writes an out-of-line field's value, which gives 0, or <c>[0, 0]</c> for a union or a vector of
    /// unions, when it is absent.
    /// </summary>
    private string WriteChild(TypeModel type, string value, string table, string field) =>
        type.Kind switch
        {
            TypeKind.String => $"$writer.writeString({value}, {table}, {field})",
            TypeKind.Table => $"$writer.writeTable({CodecRef(type)}, {value})",
            TypeKind.Union => $"{CodecRef(type)}.write($writer, {value})",
            TypeKind.Vector => type.Element!.Kind switch
            {
                TypeKind.Scalar when type.Element.Scalar == ScalarKind.UInt8 =>
                    $"$writer.writeBytes({value}, {table}, {field})",
                TypeKind.Scalar or TypeKind.Enum =>
                    $"$writer.writeScalarVector({KindConstant(type.Element.Scalar)}, {value}, {table}, {field})",
                TypeKind.Struct => $"$writer.writeStructVector({CodecRef(type.Element)}, {value}, {table}, {field})",
                TypeKind.String => $"$writer.writeStringVector({value}, {table}, {field})",
                TypeKind.Table => $"$writer.writeTableVector({CodecRef(type.Element)}, {value}, {table}, {field})",
                TypeKind.Union => $"$writer.writeUnionVector({CodecRef(type.Element)}, {value}, {table}, {field})",
                _ => throw new InvalidOperationException($"Unexpected vector element {type.Element.Kind}."),
            },
            _ => throw new InvalidOperationException($"Unexpected child type {type.Kind}."),
        };

    /// <summary>
    /// Whether an out-of-line field is written as a pair of offsets: a union's discriminator and value, or a union
    /// vector's discriminator and value vectors, in the slots before and at its own.
    /// </summary>
    private static bool IsPair(TableFieldModel field) =>
        field.Type.Kind == TypeKind.Union || field.Type.Element?.Kind == TypeKind.Union;

    /// <summary>
    /// Where <c>write</c> kept the offset of the child in slot, which is the field's own slot or, for a pair, the one
    /// before it.
    /// </summary>
    private static string ChildOffset(TableFieldModel field, int slot, bool locals) =>
        (locals, IsPair(field)) switch
        {
            (true, true) => $"$u{field.Slot}[{(slot == field.Slot ? 1 : 0)}]",
            (true, false) => $"$o{slot}",
            (false, _) => $"$o[{slot}]!",
        };

    private void EmitAddField(SourceWriter w, TableFieldModel field, int slot, string table, bool locals)
    {
        var fieldName = Literal(field.SchemaName);
        var value = $"$value.{field.Name}";
        switch (field.Type.Kind)
        {
            case TypeKind.Scalar or TypeKind.Enum when field.IsOptionalScalar:
                w.Line(
                    $"$writer.addOptionalScalarField({slot}, {KindConstant(field.Type.Scalar)}, {value}, {table}, {fieldName});"
                );
                break;
            case TypeKind.Scalar or TypeKind.Enum:
                w.Line(
                    $"$writer.addScalarField({slot}, {KindConstant(field.Type.Scalar)}, {value}, {DefaultConstant(field)}, {table}, {fieldName});"
                );
                break;
            case TypeKind.Struct:
                var structValue = field.IsRequired ? $"$bq.required({value}, {table}, {fieldName})" : value;
                w.Line($"$writer.addStruct({slot}, {CodecRef(field.Type)}, {structValue}, {table}, {fieldName});");
                break;
            case TypeKind.Union when slot != field.Slot:
                w.Line($"$writer.addUnionType({slot}, {ChildOffset(field, slot, locals)});");
                break;
            case TypeKind.Vector when field.Type.Element!.Kind == TypeKind.Union:
                w.Line($"$writer.addOffset({slot - 1}, {ChildOffset(field, slot - 1, locals)});");
                w.Line($"$writer.addOffset({slot}, {ChildOffset(field, slot, locals)});");
                break;
            default:
                w.Line($"$writer.addOffset({slot}, {ChildOffset(field, slot, locals)});");
                break;
        }
    }

    /// <summary>
    /// The default a scalar field is compared with, to leave it out when equal (SER-004). The runtime compares as the
    /// C# writer does: a NaN default skips only NaN, and every other default compares with <c>!==</c>, so <c>-0</c>
    /// equals a zero default and NaN is written. An enum's default is its number, which needs no import.
    /// </summary>
    private static string DefaultConstant(TableFieldModel field) =>
        field.Type.Scalar switch
        {
            ScalarKind.Bool => field.Default is null ? "false" : "true",
            ScalarKind.Float32 or ScalarKind.Float64 => FloatLiteral(field.Default?.Float ?? 0),
            _ => IntegerLiteral(field.Default?.Integer ?? 0, field.Type.Scalar),
        };

    /// <summary>
    /// Emits the codec's <c>fill</c>, which like <c>write</c> has no local per field: <c>$p</c> holds each field's
    /// position in turn, and union fields, whose discriminator is a second field, are read by one runtime call.
    /// </summary>
    private void EmitTableFill(SourceWriter w, TableModel model)
    {
        var table = Literal(model.SchemaFullName);
        if (model.Fields.Count == 0)
        {
            w.Line("fill(): void {},");
            return;
        }

        w.Open($"fill($reader: $bq.Reader, $table: number, $value: {model.Name}): void {{");
        if (model.Fields.Any(f => f.Type.Kind != TypeKind.Union && f.Type.Element?.Kind != TypeKind.Union))
        {
            w.Line("let $p: number;");
        }

        foreach (var field in model.Fields)
        {
            var slot = field.Slot;
            var fieldName = Literal(field.SchemaName);
            string? union = null;
            if (field.Type.Kind == TypeKind.Union)
            {
                union = $"$reader.readUnion({CodecRef(field.Type)}, $table, {slot})";
            }
            else if (field.Type.Element?.Kind == TypeKind.Union)
            {
                union = $"$reader.readUnionVector({CodecRef(field.Type.Element)}, $table, {slot})";
                if (!field.Type.Element.UnionAllowsNone)
                {
                    union = $"$bq.cast<{FieldType(field.Type, false)} | undefined>({union})";
                }
            }

            if (union is not null)
            {
                w.Line(
                    field.IsRequired
                        ? $"$value.{field.Name} = $reader.required({union}, {table}, {fieldName});"
                        : $"$value.{field.Name} = {union};"
                );
                continue;
            }

            w.Line($"$p = $reader.field($table, {slot});");
            var read = ReadExpression(field.Type, "$p");
            if (field.IsRequired)
            {
                w.Line($"if ($p === 0) throw $reader.missingRequiredField({table}, {fieldName});");
                w.Line($"$value.{field.Name} = {read};");
            }
            else
            {
                w.Line($"if ($p !== 0) $value.{field.Name} = {read};");
            }
        }

        w.Close("},");
    }

    private string ReadExpression(TypeModel type, string position) =>
        type.Kind switch
        {
            TypeKind.Scalar or TypeKind.Enum =>
                $"$bq.cast<{FieldType(type, false)}>($reader.readScalar({KindConstant(type.Scalar)}, {position}))",
            TypeKind.Struct => $"$reader.readStruct({CodecRef(type)}, {position})",
            TypeKind.String => $"$reader.readString({position})",
            TypeKind.Table => $"$reader.readTable({CodecRef(type)}, {position})",
            TypeKind.Vector => type.Element!.Kind switch
            {
                TypeKind.Scalar when type.Element.Scalar == ScalarKind.UInt8 => $"$reader.readBytes({position})",
                TypeKind.Scalar or TypeKind.Enum =>
                    $"$reader.readScalarVector<{FieldType(type.Element, false)}>({KindConstant(type.Element.Scalar)}, {position})",
                TypeKind.Struct => $"$reader.readStructVector({CodecRef(type.Element)}, {position})",
                TypeKind.String => $"$reader.readStringVector({position})",
                TypeKind.Table => $"$reader.readTableVector({CodecRef(type.Element)}, {position})",
                _ => throw new InvalidOperationException($"Unexpected vector element {type.Element.Kind}."),
            },
            _ => throw new InvalidOperationException($"Unexpected field type {type.Kind}."),
        };

    /// <summary>
    /// The value of a field in a table object before parsing fills it in: its default, or undefined.
    /// </summary>
    private string InitialValue(TableFieldModel field) =>
        field.Type.Kind is TypeKind.Scalar or TypeKind.Enum && !field.IsOptionalScalar
            ? DefaultLiteral(field)
            : "undefined";

    // Unions (TS-007)

    private void EmitUnion(SourceWriter w, UnionModel model)
    {
        _usesRuntime = true;
        var name = model.Name;

        foreach (var (typeName, input) in new[] { (name, false), (model.DerivedName!, true) })
        {
            Doc(w, model.Documentation, model.RenamedFrom);
            w.Line($"export type {typeName} =");
            w.Indent();
            for (var i = 0; i < model.Members.Count; i++)
            {
                var member = model.Members[i];
                if (!input)
                {
                    Doc(w, member.Documentation, member.RenamedFrom);
                }

                var last = i == model.Members.Count - 1 && !model.AllowsUnknown;
                w.Line(
                    $"| {{ readonly kind: {Literal(member.Label)}; value: {FieldType(member.Type, input)} }}{(last ? ";" : "")}"
                );
            }

            if (model.AllowsUnknown)
            {
                w.Line("| { readonly kind: \"unknown\"; readonly discriminator: number };");
            }

            w.Unindent();
            w.Line();
        }

        Doc(w, model.Documentation, model.RenamedFrom);
        w.Open($"export const {name} = {{");
        w.Open($"equals($a: {name} | undefined, $b: {name} | undefined): boolean {{");
        w.Line("if ($a === undefined || $b === undefined) return $a === $b;");
        w.Open("switch ($a.kind) {");
        foreach (var member in model.Members)
        {
            var label = Literal(member.Label);
            w.Line($"case {label}: return $b.kind === {label} && {ValueEquals(member.Type, "$a.value", "$b.value")};");
        }

        if (model.AllowsUnknown)
        {
            w.Line("case \"unknown\": return $b.kind === \"unknown\" && $a.discriminator === $b.discriminator;");
        }

        w.Close("}");
        w.Close("},");
        w.Close("};");
    }

    private void EmitUnionCodec(SourceWriter w, UnionModel model)
    {
        var name = model.Name;
        var union = Literal(model.SchemaFullName);
        w.Open($"export const {CodecName(name)}: $bq.UnionCodec<{name}, {model.DerivedName}> = {{");
        w.Line($"name: {union},");
        w.Line($"allowsNone: {(model.AllowsNone ? "true" : "false")},");
        w.Open($"write($writer: $bq.Writer, $value: {model.DerivedName} | undefined): readonly [number, number] {{");
        w.Line("if ($value == null) return [0, 0];");
        w.Open("switch ($value.kind) {");
        foreach (var member in model.Members)
        {
            var write =
                member.Type.Kind == TypeKind.Table
                    ? $"$writer.writeTable({CodecRef(member.Type)}, $bq.memberValue($value.value, {union}, {Literal(member.Label)}))"
                    : $"$writer.writeStruct({CodecRef(member.Type)}, $value.value, {union}, {Literal(member.Label)})";
            w.Line($"case {Literal(member.Label)}: return [{member.Discriminator}, {write}];");
        }

        w.Line($"default: return $bq.unwritableUnion({union}, $value);");
        w.Close("}");
        w.Close("},");
        w.Open($"read($reader: $bq.Reader, $type: number, $position: number): {name} | undefined {{");
        w.Open("switch ($type) {");
        w.Line(model.AllowsNone ? "case 0: return undefined;" : $"case 0: throw $reader.unionNoneNotAllowed({union});");
        foreach (var member in model.Members)
        {
            var read =
                member.Type.Kind == TypeKind.Table
                    ? $"$reader.readTable({CodecRef(member.Type)}, $position)"
                    : $"$reader.readStructAt({CodecRef(member.Type)}, $position)";
            w.Line($"case {member.Discriminator}: return {{ kind: {Literal(member.Label)}, value: {read} }};");
        }

        w.Line(
            model.AllowsUnknown
                ? "default: return { kind: \"unknown\", discriminator: $type };"
                : $"default: throw $reader.unknownUnionType({union}, $type);"
        );
        w.Close("}");
        w.Close("},");
        w.Close("};");
    }

    // Equality (TS-010)

    private string FieldEquals(TypeModel type, string field, bool optional)
    {
        var a = $"$a.{field}";
        var b = $"$b.{field}";
        return type.Kind switch
        {
            TypeKind.Vector => $"$bq.equalVectors({a}, {b}, {ElementEquals(type.Element!)})",
            TypeKind.Struct when optional => $"$bq.equalOptional({a}, {b}, {CompanionRef(type)}.equals)",
            TypeKind.Table when type.TableHasValueEquality && optional =>
                $"$bq.equalOptional({a}, {b}, {CompanionRef(type)}.equals)",
            _ => ValueEquals(type, a, b),
        };
    }

    private string ValueEquals(TypeModel type, string a, string b) =>
        type.Kind switch
        {
            TypeKind.Scalar when ScalarInfo.IsFloat(type.Scalar) => $"$bq.equalNumbers({a}, {b})",
            TypeKind.Struct or TypeKind.Union => $"{CompanionRef(type)}.equals({a}, {b})",
            TypeKind.Table when type.TableHasValueEquality => $"{CompanionRef(type)}.equals({a}, {b})",
            _ => $"{a} === {b}",
        };

    private string ElementEquals(TypeModel element) =>
        element.Kind switch
        {
            TypeKind.Scalar when ScalarInfo.IsFloat(element.Scalar) => "$bq.equalNumbers",
            TypeKind.Struct or TypeKind.Union => $"{CompanionRef(element)}.equals",
            TypeKind.Table when element.TableHasValueEquality => $"{CompanionRef(element)}.equals",
            _ => "$bq.equalIdentical",
        };

    // Types and references (TS-009)

    private string FieldType(TypeModel type, bool input) =>
        type.Kind switch
        {
            TypeKind.Scalar => ScalarType(type.Scalar),
            TypeKind.String => "string",
            TypeKind.Enum => TypeRef(type),
            TypeKind.Struct or TypeKind.Table or TypeKind.Union => input ? InputRef(type) : TypeRef(type),
            TypeKind.Vector when type.Element!.Kind == TypeKind.Scalar && type.Element.Scalar == ScalarKind.UInt8 =>
                "$bq.Bytes",
            TypeKind.Vector => (input ? "readonly " : "") + ElementType(type.Element!, input) + "[]",
            _ => throw new InvalidOperationException($"Unexpected type {type.Kind}."),
        };

    private string ElementType(TypeModel element, bool input) =>
        element.Kind == TypeKind.Union && element.UnionAllowsNone
            ? $"({FieldType(element, input)} | undefined)"
            : FieldType(element, input);

    private static string ScalarType(ScalarKind scalar) =>
        scalar switch
        {
            ScalarKind.Bool => "boolean",
            ScalarKind.Int64 or ScalarKind.UInt64 => "bigint",
            _ => "number",
        };

    private string TypeRef(TypeModel type) => Qualify(type.FullName!, NameOf(type.FullName!), value: false);

    private string InputRef(TypeModel type) =>
        Qualify(type.FullName!, _declarations[type.FullName!].DerivedName!, value: false);

    private string CompanionRef(TypeModel type) => Qualify(type.FullName!, NameOf(type.FullName!), value: true);

    private string CodecRef(TypeModel type) => Qualify(type.FullName!, CodecName(NameOf(type.FullName!)), value: true);

    private string Qualify(string fullName, string name, bool value)
    {
        var ns = NamespaceOf(fullName);
        if (ns == _namespace)
        {
            return name;
        }

        if (value)
        {
            _valueImports.Add(ns);
        }

        return _aliases[ns] + "." + name;
    }

    private static string CodecName(string name) => "$c_" + name;

    private static string NamespaceOf(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        return dot < 0 ? "" : fullName[..dot];
    }

    private static string NameOf(string fullName) => fullName[(fullName.LastIndexOf('.') + 1)..];

    private static string KindConstant(ScalarKind scalar) =>
        "$bq."
        + scalar switch
        {
            ScalarKind.Bool => "Bool",
            ScalarKind.Int8 => "Int8",
            ScalarKind.UInt8 => "Uint8",
            ScalarKind.Int16 => "Int16",
            ScalarKind.UInt16 => "Uint16",
            ScalarKind.Int32 => "Int32",
            ScalarKind.UInt32 => "Uint32",
            ScalarKind.Int64 => "Int64",
            ScalarKind.UInt64 => "Uint64",
            ScalarKind.Float32 => "Float32",
            ScalarKind.Float64 => "Float64",
            _ => throw new ArgumentOutOfRangeException(nameof(scalar)),
        };

    // Literals

    private string DefaultLiteral(TableFieldModel field)
    {
        var type = field.Type;
        return type.Kind == TypeKind.Enum
            ? EnumMember(type, field.Default)
            : ZeroOrConstant(type.Scalar, field.Default);
    }

    /// <summary>
    /// The enum member a default names, or the member equal to 0 without a default. A table field always has one
    /// (BQ2014), but a struct field's enum may not, and then its zero is 0 cast to the enum.
    /// </summary>
    private string EnumMember(TypeModel type, ConstantModel? constant)
    {
        var member = constant?.EnumMember
            ?? ((EnumModel)_declarations[type.FullName!]).Members.FirstOrDefault(m => m.Value == 0)?.Name;
        return member is null
            ? $"$bq.cast<{TypeRef(type)}>({IntegerLiteral(0, type.Scalar)})"
            : $"{CompanionRef(type)}.{member}";
    }

    private static string ZeroOrConstant(ScalarKind scalar, ConstantModel? constant) =>
        scalar switch
        {
            ScalarKind.Bool => constant is null ? "false" : "true",
            ScalarKind.Float32 or ScalarKind.Float64 => FloatLiteral(constant?.Float ?? 0),
            _ => IntegerLiteral(constant?.Integer ?? 0, scalar),
        };

    private static string IntegerLiteral(decimal value, ScalarKind scalar) =>
        value.ToString(CultureInfo.InvariantCulture) + (scalar is ScalarKind.Int64 or ScalarKind.UInt64 ? "n" : "");

    private static string FloatLiteral(double value)
    {
        if (double.IsNaN(value))
        {
            return "$bq.notANumber";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "$bq.positiveInfinity" : "$bq.negativeInfinity";
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A double-quoted string literal. Schema names are identifiers, so only paths need escaping.
    /// </summary>
    private static string Literal(string text) =>
        "\""
        + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        + "\"";

    private static void Doc(SourceWriter w, EquatableArray<string> lines, string? renamedFrom)
    {
        if (lines.Count == 0 && renamedFrom is null)
        {
            return;
        }

        w.Line("/**");
        foreach (var line in lines)
        {
            w.Line((" * " + line.Replace("*/", "*\\/", StringComparison.Ordinal)).TrimEnd());
        }

        if (renamedFrom is not null)
        {
            w.Line($" * Schema name: `{renamedFrom}`.");
        }

        w.Line(" */");
    }
}
