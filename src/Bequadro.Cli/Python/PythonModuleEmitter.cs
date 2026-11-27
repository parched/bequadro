using System.Globalization;
using System.Text;
using Bequadro.Schema.Common;
using Bequadro.Schema.Semantics;

namespace Bequadro.Cli.Python;

/// <summary>
/// Emits the module of one namespace (PY-002 to PY-010). Generated code refers to the runtime as <c>_bq</c>, to other
/// namespaces as <c>_bq_n0</c>, <c>_bq_n1</c>, ..., and to its codec functions as <c>_bq_write_Name_0</c> and so on,
/// and names every parameter and local the same way (<c>_bq_w</c>, <c>_bq_p</c>, <c>_bq_f3</c>, <c>_bq_self</c>):
/// internal names start with <c>_bq</c> and never end with <c>_</c>, which renaming keeps declarations from doing
/// (PY-003), so no declaration can shadow one or be shadowed by one. Union codecs are in the module's <c>__bequadro__</c> dictionary, and table and struct codecs in each
/// class's <c>__bequadro__</c> attribute, which other modules can use.
/// </summary>
/// <remarks>
/// Enums come before the imports of other namespaces, so that when namespaces import each other in a cycle, the
/// module that is imported first already has its enums, which class bodies use as field defaults. Then come structs and
/// tables, then unions, then the codecs of all of them.
/// </remarks>
internal sealed class PythonModuleEmitter(string ns, Dictionary<string, DeclarationModel> declarations)
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private int _index;

    public string Emit(IReadOnlyList<DeclarationModel> models)
    {
        var referenced = models
            .SelectMany(ReferencedTypes)
            .Select(t => NamespaceOf(t.FullName!))
            .Where(other => other != ns)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < referenced.Count; i++)
        {
            _aliases[referenced[i]] = "_bq_n" + i.ToString(CultureInfo.InvariantCulture);
        }

        var w = new SourceWriter("    ");
        w.Line(PythonTarget.Header.TrimEnd('\n'));
        w.Line(PythonTarget.PyrightDirective);
        w.Line(
            ns.Length == 0
                ? "\"\"\"The root namespace's declarations, which the package re-exports.\"\"\""
                : $"\"\"\"Namespace {ns}.\"\"\""
        );
        w.Line();
        w.Line("from __future__ import annotations");
        w.Line();
        var enums = models.OfType<EnumModel>().ToList();
        var memberNames = enums.SelectMany(e => e.Members).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var typing = "_bq_typing";
        if (memberNames.Any(NeedsEnumAnnotation))
        {
            // The annotation is in the enum body, where a member can shadow the imported module.
            while (memberNames.Contains(typing))
            {
                typing += "0";
            }

            w.Line($"import typing as {typing}");
            w.Line();
        }

        w.Line($"from {RootDots(ns)} import bequadro as _bq");

        foreach (var model in enums)
        {
            w.Line();
            w.Line();
            EmitEnum(w, model, typing);
        }

        if (_aliases.Count > 0)
        {
            w.Line();
            foreach (var (other, alias) in _aliases)
            {
                w.Line(ImportStatement(other, alias));
            }
        }

        // Unions come after the classes: mypy resolves a name in a type statement that comes before the class it names
        // to a built-in with that name, such as eval
        // The codecs come after all the declarations, so that a reader sees the API without them. Class bodies don't
        // use codecs, so they can be assigned to the classes at the end
        var codecs = new SourceWriter("    ");
        var unions = new List<(UnionModel Model, string Write, string Read)>();
        foreach (var model in models)
        {
            var index = _index++;
            switch (model)
            {
                case StructModel structModel:
                    EmitStruct(w, structModel);
                    EmitStructCodec(codecs, structModel, index);
                    break;
                case TableModel table:
                    EmitTable(w, table);
                    EmitTableCodec(codecs, table, index);
                    break;
            }
        }

        foreach (var model in models)
        {
            var index = _index++;
            if (model is UnionModel union)
            {
                EmitUnion(w, union);
                unions.Add(EmitUnionCodec(codecs, union, index));
            }
        }

        if (unions.Count > 0)
        {
            codecs.Line();
            codecs.Line();
            codecs.Line("__bequadro__: dict[str, _bq.UnionCodec] = {");
            foreach (var (model, write, read) in unions)
            {
                codecs.Line($"    \"{model.Name}\": _bq.UnionCodec({Literal(model.SchemaFullName)}, {write}, {read}),");
            }

            codecs.Line("}");
        }

        if (!codecs.IsEmpty)
        {
            w.Line();
            w.Line();
            w.Line("# " + OutputDirectory.CodecsComment);
        }

        return w.ToString() + codecs;
    }

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

    /// <summary>
    /// The relative-import prefix of the output package: <c>.</c> from the root namespace's module, and one more dot
    /// per segment from a namespace's package.
    /// </summary>
    private static string RootDots(string ns) => ns.Length == 0 ? "." : new string('.', ns.Split('.').Length + 1);

    private string ImportStatement(string other, string alias)
    {
        var dots = RootDots(ns);
        if (other.Length == 0)
        {
            return $"from {dots} import {PythonTarget.RootModule} as {alias}";
        }

        var dot = other.LastIndexOf('.');
        return dot < 0
            ? $"from {dots} import {other} as {alias}"
            : $"from {dots}{other[..dot]} import {other[(dot + 1)..]} as {alias}";
    }

    // Enums (PY-006)

    private static void EmitEnum(SourceWriter w, EnumModel model, string typing)
    {
        w.Line($"class {model.Name}(_bq.IntEnum):");
        w.Indent();
        Docstring(w, model.Documentation, model.RenamedFrom);
        foreach (var member in model.Members)
        {
            var value = member.Value.ToString(CultureInfo.InvariantCulture);
            if (NeedsEnumAnnotation(member.Name))
            {
                // mypy also reports an explicit annotation on a name such as _x__, even though it infers int for it.
                var errors = member.Name.StartsWith("__", StringComparison.Ordinal) ? "assignment" : "assignment,misc";
                w.Line($"{member.Name}: {typing}.ClassVar[{typing}.Self] = {value}  # type: ignore[{errors}]");
            }
            else
            {
                w.Line($"{member.Name} = {value}");
            }

            Docstring(w, member.Documentation, member.RenamedFrom, blankAfter: false);
        }

        w.Unindent();
    }

    // After renaming, both __x and _x_ start with an underscore and end with at least two. Type checkers infer int
    // for those names, although EnumMeta replaces the integer with the member.
    private static bool NeedsEnumAnnotation(string name) =>
        name.StartsWith('_') && name.EndsWith("__", StringComparison.Ordinal);

    // Structs (PY-005)

    private void EmitStruct(SourceWriter w, StructModel model)
    {
        var name = model.Name;
        ClassHeader(w, name, "_bq.Struct", model.Documentation, model.RenamedFrom);
        foreach (var field in model.Fields)
        {
            var zero = field.Type.Kind switch
            {
                TypeKind.Struct => $"_bq.field(default_factory=lambda: {TypeRef(field.Type)}())",
                TypeKind.Enum => EnumMember(field.Type, null),
                _ => ScalarLiteral(field.Type.Scalar, null),
            };
            w.Line($"{field.Name}: {FieldType(field.Type)} = {zero}");
            Docstring(w, field.Documentation, field.RenamedFrom, blankAfter: false);
        }

        EmitEquality(w, name, model.Fields.Select(f => FieldEquals(f.Type, f.Name)).ToList());
        w.Unindent();
    }

    private void EmitStructCodec(SourceWriter w, StructModel model, int index)
    {
        var name = model.Name;
        var write = $"_bq_write_{name}_{index}";
        var read = $"_bq_read_{name}_{index}";
        var schemaName = Literal(model.SchemaFullName);
        var runs = ScalarRuns(model);
        w.Line();
        w.Line();
        for (var i = 0; i < runs.Count; i++)
        {
            w.Line($"_bq_layout_{name}_{index}_{i} = _bq.layout(\"{runs[i].Format}\")");
        }

        // Each run of scalar members is checked in order, then packed in one call, and nested structs are written
        // between runs, so members are checked in the order of the fields
        w.Line();
        w.Line();
        w.Line($"def {write}(_bq_data: bytearray, _bq_p: int, _bq_v: {name}, _bq_owner: str, _bq_name: str) -> None:");
        w.Indent();
        foreach (var field in model.Fields)
        {
            var run = runs.FindIndex(r => r.Fields[0] == field);
            if (run >= 0)
            {
                w.Line($"_bq_layout_{name}_{index}_{run}.pack_into(");
                w.Indent();
                w.Line("_bq_data,");
                w.Line($"{At(runs[run].Offset)},");
                foreach (var member in runs[run].Fields)
                {
                    w.Line(
                        $"_bq.check({KindConstant(member.Type.Scalar)}, _bq_v.{member.Name}, _bq_owner, _bq_name + \".{member.SchemaName}\"),"
                    );
                }

                w.Unindent();
                w.Line(")");
            }
            else if (field.Type.Kind == TypeKind.Struct)
            {
                var path = $"_bq_name + \".{field.SchemaName}\"";
                w.Line(
                    $"_bq.write_nested_struct({TypeRef(field.Type)}.__bequadro__, _bq_data, {At(field.Offset)}, _bq_v.{field.Name}, _bq_owner, {path})"
                );
            }
        }

        w.Unindent();
        w.Line();
        w.Line();
        w.Line($"def {read}(_bq_data: memoryview, _bq_p: int) -> {name}:");
        w.Indent();
        for (var i = 0; i < runs.Count; i++)
        {
            w.Line($"_bq_u{i} = _bq_layout_{name}_{index}_{i}.unpack_from(_bq_data, {At(runs[i].Offset)})");
        }

        w.Line($"return {name}(");
        w.Indent();
        foreach (var field in model.Fields)
        {
            var run = runs.FindIndex(r => r.Fields.Contains(field));
            var unpacked = run < 0 ? "" : $"_bq_u{run}[{runs[run].Fields.IndexOf(field)}]";
            var value = field.Type.Kind switch
            {
                TypeKind.Struct => $"{TypeRef(field.Type)}.__bequadro__.read(_bq_data, {At(field.Offset)})",
                TypeKind.Enum => $"{TypeRef(field.Type)}({unpacked})",
                _ => unpacked,
            };
            w.Line($"{field.Name}={value},");
        }

        w.Unindent();
        w.Line(")");
        w.Unindent();
        w.Line();
        w.Line();
        w.Line(
            $"{name}.__bequadro__ = _bq.StructCodec({schemaName}, {name}, {model.Size}, {model.Alignment}, {write}, {read})"
        );
    }

    private static string At(int offset) => offset == 0 ? "_bq_p" : $"_bq_p + {offset}";

    /// <summary>
    /// A struct's scalar members, split into runs at its nested structs, each with its format for Python's
    /// <c>struct</c> module: <c>?bBhHiIqQfd</c> by scalar kind, and <c>x</c> for padding between members.
    /// </summary>
    private static List<ScalarRun> ScalarRuns(StructModel model)
    {
        var runs = new List<ScalarRun>();
        ScalarRun? current = null;
        var end = 0;
        foreach (var field in model.Fields)
        {
            if (field.Type.Kind == TypeKind.Struct)
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = new ScalarRun(field.Offset, new StringBuilder(), []);
                runs.Add(current);
            }
            else if (field.Offset > end)
            {
                current.Format.Append((field.Offset - end).ToString(CultureInfo.InvariantCulture)).Append('x');
            }

            current.Format.Append("?bBhHiIqQfd"[(int)field.Type.Scalar]);
            current.Fields.Add(field);
            end = field.Offset + ScalarInfo.Size(field.Type.Scalar);
        }

        return runs;
    }

    private sealed record ScalarRun(int Offset, StringBuilder Format, List<StructFieldModel> Fields);

    // Tables (PY-004)

    private void EmitTable(SourceWriter w, TableModel model)
    {
        var name = model.Name;
        ClassHeader(w, name, "_bq.Table", model.Documentation, model.RenamedFrom);
        foreach (var field in model.Fields)
        {
            var type = FieldType(field.Type);
            var annotation = IsAlwaysPresent(field) ? type : $"{type} | None";
            var hasDefault = !field.IsRequired && !field.IsRequiredMember;
            var value =
                field.Type.Kind is TypeKind.Scalar or TypeKind.Enum && !field.IsOptionalScalar
                    ? DefaultLiteral(field)
                    : "None";
            w.Line(hasDefault ? $"{field.Name}: {annotation} = {value}" : $"{field.Name}: {annotation}");
            Docstring(w, field.Documentation, field.RenamedFrom, blankAfter: false);
        }

        if (model.Fields.Count == 0 && model.Documentation.Count == 0 && model.RenamedFrom is null)
        {
            w.Line("pass");
        }

        if (model.HasValueEquality)
        {
            EmitEquality(w, name, model.Fields.Select(f => FieldEquals(f.Type, f.Name)).ToList());
        }

        w.Unindent();
    }

    private void EmitTableCodec(SourceWriter w, TableModel model, int index)
    {
        var name = model.Name;
        var write = $"_bq_write_{name}_{index}";
        var fill = $"_bq_fill_{name}_{index}";
        var table = Literal(model.SchemaFullName);
        EmitTableWrite(w, model, write, table);
        EmitTableFill(w, model, fill, table);
        w.Line();
        w.Line();
        w.Line($"{name}.__bequadro__ = _bq.TableCodec({table}, {name}, {write}, {fill})");
    }

    private static bool IsAlwaysPresent(TableFieldModel field) =>
        field.IsRequired || (field.Type.Kind is TypeKind.Scalar or TypeKind.Enum && !field.IsOptionalScalar);

    private void EmitTableWrite(SourceWriter w, TableModel model, string function, string table)
    {
        w.Line();
        w.Line();
        w.Line($"def {function}(_bq_w: _bq.Writer, _bq_v: {model.Name}) -> int:");
        w.Indent();

        // Out-of-line children first, in declaration order, as the C# writer does (SER-005)
        foreach (var field in model.Fields.Where(f => !f.Type.IsInline))
        {
            var slot = field.Slot;
            var fieldName = Literal(field.SchemaName);
            var writeChild = WriteChild(field.Type, $"_bq_f{slot}", table, fieldName);
            if (field.IsRequired)
            {
                w.Line($"_bq_f{slot} = _bq.required(_bq_v.{field.Name}, {table}, {fieldName})");
                w.Line($"_bq_o{slot} = {writeChild}");
            }
            else
            {
                w.Line($"_bq_f{slot} = _bq_v.{field.Name}");
                var isPair = field.Type.Kind == TypeKind.Union || field.Type.Element?.Kind == TypeKind.Union;
                w.Line($"_bq_o{slot} = {(isPair ? "(0, 0)" : "0")} if _bq_f{slot} is None else {writeChild}");
            }
        }

        w.Line($"_bq_w.start_table({model.SlotCount})");
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
            EmitAddField(w, field, slot, table);
        }

        w.Line($"return _bq_w.end_table({table})");
        w.Unindent();
    }

    private string WriteChild(TypeModel type, string value, string table, string field) =>
        type.Kind switch
        {
            TypeKind.String => $"_bq_w.write_string({value}, {table}, {field})",
            TypeKind.Table => $"_bq_w.write_table({TypeRef(type)}.__bequadro__, {value}, {table}, {field})",
            TypeKind.Union => $"{UnionCodec(type)}.write(_bq_w, {value})",
            TypeKind.Vector => type.Element!.Kind switch
            {
                TypeKind.Scalar when type.Element.Scalar == ScalarKind.UInt8 =>
                    $"_bq_w.write_bytes({value}, {table}, {field})",
                TypeKind.Scalar or TypeKind.Enum =>
                    $"_bq_w.write_scalar_vector({KindConstant(type.Element.Scalar)}, {value}, {table}, {field})",
                TypeKind.Struct =>
                    $"_bq_w.write_struct_vector({TypeRef(type.Element)}.__bequadro__, {value}, {table}, {field})",
                TypeKind.String => $"_bq_w.write_string_vector({value}, {table}, {field})",
                TypeKind.Table =>
                    $"_bq_w.write_table_vector({TypeRef(type.Element)}.__bequadro__, {value}, {table}, {field})",
                TypeKind.Union => $"_bq_w.write_union_vector({UnionCodec(type.Element)}, {value}, {table}, {field})",
                _ => throw new InvalidOperationException($"Unexpected vector element {type.Element.Kind}."),
            },
            _ => throw new InvalidOperationException($"Unexpected child type {type.Kind}."),
        };

    private void EmitAddField(SourceWriter w, TableFieldModel field, int slot, string table)
    {
        var fieldName = Literal(field.SchemaName);
        switch (field.Type.Kind)
        {
            case TypeKind.Scalar or TypeKind.Enum:
            {
                var kind = KindConstant(field.Type.Scalar);
                if (field.IsOptionalScalar)
                {
                    w.Line($"_bq_s{slot} = _bq_v.{field.Name}");
                    w.Line($"if _bq_s{slot} is not None:");
                    w.Line(
                        $"    _bq_w.add_scalar({slot}, {kind}, _bq.check({kind}, _bq_s{slot}, {table}, {fieldName}))"
                    );
                }
                else
                {
                    w.Line($"_bq_c{slot} = _bq.check({kind}, _bq_v.{field.Name}, {table}, {fieldName})");
                    w.Line($"if {NotDefault(field, $"_bq_c{slot}")}:");
                    w.Line($"    _bq_w.add_scalar({slot}, {kind}, _bq_c{slot})");
                }

                break;
            }

            case TypeKind.Struct:
                if (field.IsRequired)
                {
                    w.Line($"_bq_s{slot} = _bq.required(_bq_v.{field.Name}, {table}, {fieldName})");
                    w.Line(
                        $"_bq_w.add_struct({slot}, {TypeRef(field.Type)}.__bequadro__, _bq_s{slot}, {table}, {fieldName})"
                    );
                }
                else
                {
                    w.Line($"_bq_s{slot} = _bq_v.{field.Name}");
                    w.Line($"if _bq_s{slot} is not None:");
                    w.Line(
                        $"    _bq_w.add_struct({slot}, {TypeRef(field.Type)}.__bequadro__, _bq_s{slot}, {table}, {fieldName})"
                    );
                }

                break;
            case TypeKind.Union when slot != field.Slot:
                w.Line($"if _bq_o{field.Slot}[1] != 0:");
                w.Line($"    _bq_w.add_scalar({slot}, _bq.UINT8, _bq_o{field.Slot}[0])");
                break;
            case TypeKind.Union:
                w.Line($"if _bq_o{slot}[1] != 0:");
                w.Line($"    _bq_w.add_offset({slot}, _bq_o{slot}[1])");
                break;
            case TypeKind.Vector when field.Type.Element!.Kind == TypeKind.Union:
                w.Line($"if _bq_o{slot}[1] != 0:");
                w.Line($"    _bq_w.add_offset({slot - 1}, _bq_o{slot}[0])");
                w.Line($"    _bq_w.add_offset({slot}, _bq_o{slot}[1])");
                break;
            default:
                w.Line($"if _bq_o{slot} != 0:");
                w.Line($"    _bq_w.add_offset({slot}, _bq_o{slot})");
                break;
        }
    }

    /// <summary>
    /// The condition under which a checked scalar differs from the field's default and is written, matching the C#
    /// writer (SER-004): a NaN default skips only NaN, and <c>-0.0</c> equals a zero default.
    /// </summary>
    private static string NotDefault(TableFieldModel field, string value)
    {
        if (field.Default is { } constant && ScalarInfo.IsFloat(field.Type.Scalar) && double.IsNaN(constant.Float))
        {
            return $"{value} == {value}";
        }

        return $"{value} != {ScalarLiteral(field.Type.Scalar, field.Default)}";
    }

    private void EmitTableFill(SourceWriter w, TableModel model, string function, string table)
    {
        w.Line();
        w.Line();
        w.Line($"def {function}(_bq_r: _bq.Reader, _bq_t: int, _bq_o: {model.Name}) -> None:");
        w.Indent();
        if (model.Fields.Count == 0)
        {
            w.Line("pass");
        }

        foreach (var field in model.Fields)
        {
            var slot = field.Slot;
            var missing = $"raise _bq_r.missing_required_field({table}, {Literal(field.SchemaName)})";
            if (field.Type.Kind == TypeKind.Union)
            {
                w.Line($"_bq_t{slot} = _bq_r.field(_bq_t, {slot - 1})");
                w.Line($"_bq_p = _bq_r.field(_bq_t, {slot})");
                w.Line(
                    $"_bq_d{slot} = 0 if _bq_t{slot} == 0 or _bq_p == 0 else _bq_r.read_scalar(_bq.UINT8, _bq_t{slot})"
                );
                var read = $"{UnionCodec(field.Type)}.read(_bq_r, _bq_d{slot}, _bq_p)";
                if (field.IsRequired)
                {
                    w.Line($"if _bq_d{slot} == 0:");
                    w.Line($"    {missing}");
                    w.Line($"_bq_o.{field.Name} = {read}");
                }
                else
                {
                    w.Line($"_bq_o.{field.Name} = None if _bq_d{slot} == 0 else {read}");
                }

                continue;
            }

            if (field.Type.Element?.Kind == TypeKind.Union)
            {
                w.Line($"_bq_t{slot} = _bq_r.field(_bq_t, {slot - 1})");
                w.Line($"_bq_p = _bq_r.field(_bq_t, {slot})");
                var read = $"_bq_r.read_union_vector({UnionCodec(field.Type.Element)}, _bq_t{slot}, _bq_p)";
                if (field.IsRequired)
                {
                    w.Line($"if _bq_t{slot} == 0 or _bq_p == 0:");
                    w.Line($"    {missing}");
                    w.Line($"_bq_o.{field.Name} = {read}");
                }
                else
                {
                    w.Line($"_bq_o.{field.Name} = None if _bq_t{slot} == 0 or _bq_p == 0 else {read}");
                }

                continue;
            }

            w.Line($"_bq_p = _bq_r.field(_bq_t, {slot})");
            var readField = ReadExpression(field.Type);
            if (field.IsRequired)
            {
                w.Line("if _bq_p == 0:");
                w.Line($"    {missing}");
                w.Line($"_bq_o.{field.Name} = {readField}");
            }
            else
            {
                var absent =
                    field.Type.Kind is TypeKind.Scalar or TypeKind.Enum && !field.IsOptionalScalar
                        ? DefaultLiteral(field)
                        : "None";
                w.Line($"_bq_o.{field.Name} = {absent} if _bq_p == 0 else {readField}");
            }
        }

        w.Unindent();
    }

    private string ReadExpression(TypeModel type) =>
        type.Kind switch
        {
            TypeKind.Scalar => $"_bq_r.read_scalar({KindConstant(type.Scalar)}, _bq_p)",
            TypeKind.Enum => $"{TypeRef(type)}(_bq_r.read_scalar({KindConstant(type.Scalar)}, _bq_p))",
            TypeKind.Struct => $"_bq_r.read_struct({TypeRef(type)}.__bequadro__, _bq_p)",
            TypeKind.String => "_bq_r.read_string(_bq_p)",
            TypeKind.Table => $"_bq_r.read_table({TypeRef(type)}.__bequadro__, _bq_p)",
            TypeKind.Vector => type.Element!.Kind switch
            {
                TypeKind.Scalar when type.Element.Scalar == ScalarKind.UInt8 => "_bq_r.read_bytes(_bq_p)",
                TypeKind.Scalar => $"_bq_r.read_scalar_vector({KindConstant(type.Element.Scalar)}, _bq_p)",
                TypeKind.Enum =>
                    $"[{TypeRef(type.Element)}(_bq_x) for _bq_x in _bq_r.read_scalar_vector({KindConstant(type.Element.Scalar)}, _bq_p)]",
                TypeKind.Struct => $"_bq_r.read_struct_vector({TypeRef(type.Element)}.__bequadro__, _bq_p)",
                TypeKind.String => "_bq_r.read_string_vector(_bq_p)",
                TypeKind.Table => $"_bq_r.read_table_vector({TypeRef(type.Element)}.__bequadro__, _bq_p)",
                _ => throw new InvalidOperationException($"Unexpected vector element {type.Element.Kind}."),
            },
            _ => throw new InvalidOperationException($"Unexpected field type {type.Kind}."),
        };

    // Unions (PY-007)

    private void EmitUnion(SourceWriter w, UnionModel model)
    {
        var cases = model.Members.Select(m => TypeRef(m.Type)).ToList();
        if (model.AllowsUnknown)
        {
            cases.Add("_bq.UnionUnknown");
        }

        w.Line();
        w.Line();
        w.Line($"type {model.Name} = {string.Join(" | ", cases)}");
        Docstring(w, model.Documentation, model.RenamedFrom, blankAfter: false);
    }

    private (UnionModel, string, string) EmitUnionCodec(SourceWriter w, UnionModel model, int index)
    {
        var name = model.Name;
        var write = $"_bq_write_{name}_{index}";
        var read = $"_bq_read_{name}_{index}";
        var union = Literal(model.SchemaFullName);

        w.Line();
        w.Line();
        w.Line($"def {write}(_bq_w: _bq.Writer, _bq_v: object) -> tuple[int, int]:");
        w.Indent();
        w.Line("if _bq_v is None:");
        w.Line(model.AllowsNone ? "    return (0, 0)" : $"    raise _bq.union_has_no_value({union})");
        w.Line("_bq_c = _bq_v.__class__");
        foreach (var member in model.Members)
        {
            var type = TypeRef(member.Type);
            w.Line($"if _bq_c is {type}:");
            w.Line(
                member.Type.Kind == TypeKind.Table
                    ? $"    return ({member.Discriminator}, _bq_w.write_table({type}.__bequadro__, _bq_v))"
                    : $"    return ({member.Discriminator}, _bq_w.write_struct({type}.__bequadro__, _bq_v, {union}, {Literal(member.Label)}))"
            );
        }

        w.Line($"return _bq.unwritable_union({union}, _bq_v)");
        w.Unindent();

        w.Line();
        w.Line();
        w.Line($"def {read}(_bq_r: _bq.Reader, _bq_d: int, _bq_p: int) -> {name} | None:");
        w.Indent();
        w.Line("if _bq_d == 0:");
        w.Line(model.AllowsNone ? "    return None" : $"    raise _bq_r.union_none_not_allowed({union})");
        foreach (var member in model.Members)
        {
            var type = TypeRef(member.Type);
            var discriminator = member.Discriminator.ToString(CultureInfo.InvariantCulture);
            w.Line($"if _bq_d == {discriminator}:");
            w.Line(
                member.Type.Kind == TypeKind.Table
                    ? $"    _bq_m{discriminator}: {type} = _bq_r.read_table({type}.__bequadro__, _bq_p)"
                    : $"    _bq_m{discriminator}: {type} = _bq_r.read_struct_at({type}.__bequadro__, _bq_p)"
            );
            w.Line($"    return _bq_m{discriminator}");
        }

        w.Line(
            model.AllowsUnknown
                ? "return _bq.UnionUnknown(discriminator=_bq_d)"
                : $"raise _bq_r.unknown_union_type({union}, _bq_d)"
        );
        w.Unindent();
        return (model, write, read);
    }

    // Equality (PY-010)

    private static void EmitEquality(SourceWriter w, string name, IReadOnlyList<string> comparisons)
    {
        w.Line();
        w.Line("def __eq__(_bq_self, _bq_other: object) -> bool:");
        w.Indent();
        w.Line($"if not isinstance(_bq_other, {name}):");
        w.Line("    return NotImplemented");
        if (comparisons.Count == 0)
        {
            w.Line("return True");
        }
        else
        {
            w.Line("return (");
            w.Indent();
            for (var i = 0; i < comparisons.Count; i++)
            {
                w.Line((i == 0 ? "" : "and ") + comparisons[i]);
            }

            w.Unindent();
            w.Line(")");
        }

        w.Unindent();
        w.Line();
        w.Line("__hash__ = None  # type: ignore[assignment]");
    }

    /// <summary>
    /// How one field compares (PY-010): floats as they are written, so a <c>float</c> at 32-bit precision, vectors
    /// element by element whatever sequence holds them, and <c>[ubyte]</c> by its bytes.
    /// </summary>
    private static string FieldEquals(TypeModel type, string field)
    {
        var a = $"_bq_self.{field}";
        var b = $"_bq_other.{field}";
        return type.Kind switch
        {
            TypeKind.Scalar when ScalarInfo.IsFloat(type.Scalar) => $"{FloatEquality(type.Scalar)}({a}, {b})",
            TypeKind.Vector when type.Element!.Kind == TypeKind.Scalar && type.Element.Scalar == ScalarKind.UInt8 =>
                $"_bq.equal_bytes({a}, {b})",
            TypeKind.Vector when type.Element.Kind == TypeKind.Scalar && ScalarInfo.IsFloat(type.Element.Scalar) =>
                $"_bq.equal_vectors({a}, {b}, {FloatEquality(type.Element.Scalar)})",
            TypeKind.Vector => $"_bq.equal_vectors({a}, {b}, _bq.equal_values)",
            TypeKind.Table when !type.TableHasValueEquality => $"{a} is {b}",
            _ => $"{a} == {b}",
        };
    }

    private static string FloatEquality(ScalarKind scalar) =>
        scalar == ScalarKind.Float32 ? "_bq.equal_float32s" : "_bq.equal_floats";

    // Types and references (PY-009)

    private string FieldType(TypeModel type) =>
        type.Kind switch
        {
            TypeKind.Scalar => type.Scalar switch
            {
                ScalarKind.Bool => "bool",
                ScalarKind.Float32 or ScalarKind.Float64 => "float",
                _ => "int",
            },
            TypeKind.String => "str",
            TypeKind.Enum or TypeKind.Struct or TypeKind.Table or TypeKind.Union => TypeRef(type),
            TypeKind.Vector when type.Element!.Kind == TypeKind.Scalar && type.Element.Scalar == ScalarKind.UInt8 =>
                "bytearray",
            TypeKind.Vector => type.Element.Kind == TypeKind.Union && type.Element.UnionAllowsNone
                ? $"list[{FieldType(type.Element)} | None]"
                : $"list[{FieldType(type.Element)}]",
            _ => throw new InvalidOperationException($"Unexpected type {type.Kind}."),
        };

    private string TypeRef(TypeModel type)
    {
        var fullName = type.FullName!;
        var other = NamespaceOf(fullName);
        var name = fullName[(fullName.LastIndexOf('.') + 1)..];
        return other == ns ? name : _aliases[other] + "." + name;
    }

    private string UnionCodec(TypeModel type)
    {
        var fullName = type.FullName!;
        var other = NamespaceOf(fullName);
        var name = fullName[(fullName.LastIndexOf('.') + 1)..];
        return (other == ns ? "" : _aliases[other] + ".") + $"__bequadro__[\"{name}\"]";
    }

    private static string NamespaceOf(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        return dot < 0 ? "" : fullName[..dot];
    }

    private static string KindConstant(ScalarKind scalar) =>
        "_bq."
        + scalar switch
        {
            ScalarKind.Bool => "BOOL",
            ScalarKind.Int8 => "INT8",
            ScalarKind.UInt8 => "UINT8",
            ScalarKind.Int16 => "INT16",
            ScalarKind.UInt16 => "UINT16",
            ScalarKind.Int32 => "INT32",
            ScalarKind.UInt32 => "UINT32",
            ScalarKind.Int64 => "INT64",
            ScalarKind.UInt64 => "UINT64",
            ScalarKind.Float32 => "FLOAT32",
            ScalarKind.Float64 => "FLOAT64",
            _ => throw new ArgumentOutOfRangeException(nameof(scalar)),
        };

    // Literals

    private string DefaultLiteral(TableFieldModel field) =>
        field.Type.Kind == TypeKind.Enum
            ? EnumMember(field.Type, field.Default)
            : ScalarLiteral(field.Type.Scalar, field.Default);

    /// <summary>
    /// The enum member a default names, or the member equal to 0 without a default. A struct field's enum may have no
    /// value 0, and then its zero is the enum called with 0, which keeps the value (PY-006). A member whose name starts
    /// with <c>_</c> is also referred to by value, because type checkers don't treat such names as members.
    /// </summary>
    private string EnumMember(TypeModel type, ConstantModel? constant)
    {
        var value = constant?.Integer ?? 0;
        var member = constant?.EnumMember
            ?? ((EnumModel)declarations[type.FullName!]).Members.FirstOrDefault(m => m.Value == 0)?.Name;
        return member is null || member.StartsWith('_')
            ? $"{TypeRef(type)}({value.ToString(CultureInfo.InvariantCulture)})"
            : $"{TypeRef(type)}.{member}";
    }

    private static string ScalarLiteral(ScalarKind scalar, ConstantModel? constant) =>
        scalar switch
        {
            ScalarKind.Bool => constant is null ? "False" : "True",
            ScalarKind.Float32 or ScalarKind.Float64 => FloatLiteral(constant?.Float ?? 0),
            _ => (constant?.Integer ?? 0).ToString(CultureInfo.InvariantCulture),
        };

    private static string FloatLiteral(double value)
    {
        if (double.IsNaN(value))
        {
            return "_bq.NAN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "_bq.INFINITY" : "-_bq.INFINITY";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal)
            ? text
            : text + ".0";
    }

    private static string Literal(string text) =>
        "\""
        + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        + "\"";

    private static void ClassHeader(
        SourceWriter w,
        string name,
        string baseClass,
        EquatableArray<string> documentation,
        string? renamedFrom
    )
    {
        w.Line();
        w.Line();
        w.Line("@_bq.final");
        w.Line("@_bq.dataclass(slots=True, kw_only=True, eq=False)");
        w.Line($"class {name}({baseClass}):");
        w.Indent();
        Docstring(w, documentation, renamedFrom);
    }

    /// <summary>
    /// Writes a docstring from schema documentation lines, noting the schema name of a renamed element (NAM-002).
    /// </summary>
    private static void Docstring(
        SourceWriter w,
        EquatableArray<string> lines,
        string? renamedFrom,
        bool blankAfter = true
    )
    {
        var text = lines.Items.Select(l => l.Replace("\\", "\\\\", StringComparison.Ordinal)).ToList();
        if (renamedFrom is not null)
        {
            text.Add($"Schema name: `{renamedFrom}`.");
        }

        if (text.Count == 0)
        {
            return;
        }

        text = text.Select(l => l.Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal)).ToList();
        if (text.Count == 1)
        {
            w.Line($"\"\"\"{EndSafe(text[0])}\"\"\"");
        }
        else
        {
            w.Line("\"\"\"" + text[0]);
            foreach (var line in text.Skip(1))
            {
                w.Line(line);
            }

            w.Line("\"\"\"");
        }

        if (blankAfter)
        {
            w.Line();
        }
    }

    /// <summary>
    /// A docstring can't end with a quote, which would merge with the closing quotes.
    /// </summary>
    private static string EndSafe(string text) => text.EndsWith('"') ? text + " " : text;
}
