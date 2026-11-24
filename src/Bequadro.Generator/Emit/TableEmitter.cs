using Bequadro.Generator.Common;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Emit;

internal static class TableEmitter
{
    public static void Emit(CodeWriter writer, TableModel model)
    {
        var name = Naming.EscapeTypeName(model.Name);
        var fullType = Naming.GlobalTypeName(model.FullName);
        var tableInterface = $"global::Bequadro.Internal.IFlatBufferTable<{fullType}>";

        var interfaces = model.HasValueEquality
            ? $"{tableInterface}, global::System.IEquatable<{fullType}>"
            : tableInterface;
        writer.OpenBlock($"public sealed partial class {name} : {interfaces}");
        writer.Line($"public {name}() {{ }}");
        writer.Line();
        // Required members are assigned by Read before the instance is visible to user code
        writer.Line("#pragma warning disable CS8618");
        writer.Line("[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]");
        writer.Line($"private {name}(global::Bequadro.Internal.DeserializationConstructor _) {{ }}");
        writer.Line("#pragma warning restore CS8618");
        writer.Line();

        foreach (var field in model.Fields)
        {
            EmitProperty(writer, field);
            writer.Line();
        }

        writer.Line(
            $"public static global::Bequadro.FlatBufferSerializer<{fullType}> Serializer => global::Bequadro.FlatBufferSerializer<{fullType}>.Instance;"
        );
        writer.Line();
        var members = string.Join(", ", model.Fields.Select(f => $"{f.Name} = {{this.{f.Name}}}"));
        var body = model.Fields.Count == 0 ? "{{ }}" : $"{{{{ {members} }}}}";
        writer.Line($"public override string ToString() => $\"{model.Name} {body}\";");
        writer.Line();
        if (model.HasValueEquality)
        {
            EmitEquality(writer, model, name, fullType);
            writer.Line();
        }

        writer.Line($"static string {tableInterface}.SchemaName => {TypeNames.Literal(model.SchemaFullName)};");
        writer.Line();
        EmitWrite(writer, model, tableInterface, fullType);
        writer.Line();
        writer.Line(
            $"static {fullType} {tableInterface}.CreateForRead() => new(default(global::Bequadro.Internal.DeserializationConstructor));"
        );
        writer.Line();
        EmitRead(writer, model, tableInterface, fullType);
        writer.CloseBlock();
    }

    private static void EmitProperty(CodeWriter writer, TableFieldModel field)
    {
        writer.DocComment(field.Documentation, field.RenamedFrom);
        var required = field.IsRequiredMember ? "required " : "";
        var initializer = DefaultLiteral(field) is { } literal ? $" = {literal};" : "";
        writer.Line($"public {required}{PropertyType(field)} {field.Name} {{ get; set; }}{initializer}");
    }

    private static string PropertyType(TableFieldModel field)
    {
        // A union that allows NONE expresses absence as NONE, so its field is never nullable.
        var nullable =
            !field.IsRequired
            && (field.IsOptionalScalar || !(field.Type.Kind is TypeKind.Scalar or TypeKind.Enum))
            && !IsUnionAllowingNone(field.Type);
        return TypeNames.Of(field.Type) + (nullable ? "?" : "");
    }

    private static bool IsUnionAllowingNone(TypeModel type) => type.Kind == TypeKind.Union && type.UnionAllowsNone;

    /// <summary>
    /// Value equality for <c>bequadro_value_equality</c>: two instances are equal when every field is. Vectors compare element by element, and every other field under <c>EqualityComparer&lt;T&gt;.Default</c>,
    /// so a table field compares by the referenced table's own equality.
    /// </summary>
    private static void EmitEquality(CodeWriter writer, TableModel model, string name, string fullType)
    {
        writer.OpenBlock($"public bool Equals({fullType}? other)");
        writer.Line("if (global::System.Object.ReferenceEquals(this, other)) return true;");
        // Tables are sealed, so other is exactly this type and Equals is symmetric without comparing types.
        var conditions = model.Fields.Select(FieldEquals).ToList();
        writer.Line($"return other is not null{(conditions.Count == 0 ? ";" : "")}");
        for (var i = 0; i < conditions.Count; i++)
        {
            writer.Line($"    && {conditions[i]}{(i == conditions.Count - 1 ? ";" : "")}");
        }

        writer.CloseBlock();
        writer.Line();
        writer.Line($"public override bool Equals(object? obj) => this.Equals(obj as {fullType});");
        writer.Line();
        writer.OpenBlock("public override int GetHashCode()");
        writer.Line("global::System.HashCode hash = new();");
        foreach (var field in model.Fields)
        {
            var value =
                field.Type.Kind == TypeKind.Vector
                    ? $"global::Bequadro.Internal.VectorEquality.Hash(this.{field.Name})"
                    : $"this.{field.Name}";
            writer.Line($"hash.Add({value});");
        }

        writer.Line("return hash.ToHashCode();");
        writer.CloseBlock();
        writer.Line();
        writer.Line(
            $"public static bool operator ==({name}? left, {name}? right) => left is null ? right is null : left.Equals(right);"
        );
        writer.Line();
        writer.Line($"public static bool operator !=({name}? left, {name}? right) => !(left == right);");
    }

    private static string FieldEquals(TableFieldModel field) =>
        field.Type.Kind == TypeKind.Vector
            ? $"global::Bequadro.Internal.VectorEquality.AreEqual(this.{field.Name}, other.{field.Name})"
            : $"global::System.Collections.Generic.EqualityComparer<{PropertyType(field)}>.Default.Equals(this.{field.Name}, other.{field.Name})";

    private static void EmitWrite(CodeWriter writer, TableModel model, string tableInterface, string fullType)
    {
        writer.OpenBlock($"static int {tableInterface}.Write({TypeNames.Writer} writer, {fullType} value)");

        foreach (var field in model.Fields.Where(f => !f.Type.IsInline))
        {
            EmitWriteChild(writer, model, field);
        }

        writer.Line($"writer.StartTable({model.SlotCount});");

        // Largest alignment first minimizes padding, and offsets sort among the scalars. A union field is added in two
        // parts: its discriminator is a byte, which sorts with the other bytes, and its value is an offset.
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
            EmitAddField(writer, field, slot);
        }

        writer.Line($"return writer.EndTable<{fullType}>();");
        writer.CloseBlock();
    }

    /// <summary>
    /// Writes an out-of-line child (string, table, vector or union) into a local before the table is started.
    /// </summary>
    private static void EmitWriteChild(CodeWriter writer, TableModel model, TableFieldModel field)
    {
        var local = $"value{field.Slot}";
        var source = field.IsRequired
            ? $"value.{field.Name} ?? throw new {TypeNames.InvalidOperationException}({TypeNames.Literal($"Required field '{field.SchemaName}' of table '{model.SchemaFullName}' is null.")})"
            : null;

        var isUnionValue = field.Type.Kind == TypeKind.Union;
        var isUnionVector = field.Type.Element?.Kind == TypeKind.Union;
        if (isUnionValue && (field.IsRequired || field.Type.UnionAllowsNone))
        {
            source = $"value.{field.Name}";
        }

        var write = WriteChildExpression(field.Type, local);
        var resultType = isUnionValue || isUnionVector ? "(byte Type, int Offset)" : "int";
        if (isUnionVector)
        {
            resultType = "(int Type, int Offset)";
        }

        if (source is not null)
        {
            writer.Line($"{TypeNames.Of(field.Type)} {local} = {source};");
            if (isUnionValue && !field.Type.UnionAllowsNone)
            {
                var noValue = TypeNames.Literal(
                    $"Required field '{field.SchemaName}' of table '{model.SchemaFullName}' has no value."
                );
                writer.Line($"if (!{local}.HasValue) throw new {TypeNames.InvalidOperationException}({noValue});");
            }

            writer.Line($"{resultType} offset{field.Slot} = {write};");
            if (isUnionValue && field.IsRequired && field.Type.UnionAllowsNone)
            {
                var none = TypeNames.Literal(
                    $"Required field '{field.SchemaName}' of table '{model.SchemaFullName}' is NONE."
                );
                writer.Line(
                    $"if (offset{field.Slot}.Offset == 0) throw new {TypeNames.InvalidOperationException}({none});"
                );
            }
        }
        else if (isUnionValue)
        {
            // Nullable<union>: tested through Nullable members rather than a pattern, because patterns on a union
            // type follow the union's own cases.
            var property = $"value.{field.Name}";
            var noValue = TypeNames.Literal(
                $"Field '{field.SchemaName}' of table '{model.SchemaFullName}' holds a union with no value. Set the field to null to leave it out."
            );
            writer.Line(
                $"if ({property}.HasValue && !{property}.GetValueOrDefault().HasValue) throw new {TypeNames.InvalidOperationException}({noValue});"
            );
            writer.Line(
                $"{resultType} offset{field.Slot} = {property}.HasValue ? writer.WriteUnion({property}.GetValueOrDefault()) : default;"
            );
        }
        else
        {
            writer.Line($"{resultType} offset{field.Slot} = value.{field.Name} is {{ }} {local} ? {write} : default;");
        }
    }

    private static string WriteChildExpression(TypeModel type, string local) =>
        type.Kind switch
        {
            TypeKind.String => $"writer.WriteString({local})",
            TypeKind.Table => $"writer.WriteTable({local})",
            TypeKind.Union => $"writer.WriteUnion({local})",
            TypeKind.Vector => type.Element!.Kind switch
            {
                TypeKind.Scalar or TypeKind.Enum => $"writer.WriteScalarVector({local})",
                TypeKind.Struct => $"writer.WriteStructVector({local})",
                TypeKind.String => $"writer.WriteStringVector({local})",
                TypeKind.Table => $"writer.WriteTableVector({local})",
                TypeKind.Union => $"writer.WriteUnionVector({local})",
                _ => throw new InvalidOperationException($"Unexpected vector element {type.Element.Kind}."),
            },
            _ => throw new InvalidOperationException($"Unexpected child type {type.Kind}."),
        };

    /// <summary>
    /// Adds the field to the table being written. For a union field, <paramref name="slot"/> selects the part: the
    /// discriminator's slot or the value's.
    /// </summary>
    private static void EmitAddField(CodeWriter writer, TableFieldModel field, int slot)
    {
        var property = $"value.{field.Name}";
        switch (field.Type.Kind)
        {
            case TypeKind.Scalar or TypeKind.Enum when field.IsOptionalScalar:
                writer.Line($"if ({property} is {{ }} scalar{slot}) writer.AddScalar({slot}, scalar{slot});");
                break;
            case TypeKind.Scalar or TypeKind.Enum:
                writer.Line($"if ({NotDefaultCondition(field, property)}) writer.AddScalar({slot}, {property});");
                break;
            case TypeKind.Struct when field.IsRequired:
                writer.Line($"writer.AddStruct({slot}, {property});");
                break;
            case TypeKind.Struct:
                writer.Line($"if ({property} is {{ }} struct{slot}) writer.AddStruct({slot}, struct{slot});");
                break;
            case TypeKind.Union when slot != field.Slot:
                writer.Line($"if (offset{field.Slot}.Offset != 0) writer.AddScalar({slot}, offset{field.Slot}.Type);");
                break;
            case TypeKind.Union:
                writer.Line($"if (offset{slot}.Offset != 0) writer.AddOffset({slot}, offset{slot}.Offset);");
                break;
            case TypeKind.Vector when field.Type.Element!.Kind == TypeKind.Union:
                writer.OpenBlock($"if (offset{slot}.Offset != 0)");
                writer.Line($"writer.AddOffset({slot - 1}, offset{slot}.Type);");
                writer.Line($"writer.AddOffset({slot}, offset{slot}.Offset);");
                writer.CloseBlock();
                break;
            default:
                writer.Line($"if (offset{slot} != 0) writer.AddOffset({slot}, offset{slot});");
                break;
        }
    }

    /// <summary>
    /// The C# expression for a field's default, or null if it is zero.
    /// </summary>
    internal static string? DefaultLiteral(TableFieldModel field) =>
        field.Default switch
        {
            null => null,
            { EnumMember: { } member } => $"{TypeNames.Of(field.Type)}.{Naming.Escape(member)}",
            { } constant => field.Type.Scalar switch
            {
                ScalarKind.Bool => "true",
                ScalarKind.Float32 or ScalarKind.Float64 => Constants.FormatFloat(constant.Float, field.Type.Scalar),
                _ => Constants.FormatInteger(constant.Integer, field.Type.Scalar),
            },
        };

    private static string NotDefaultCondition(TableFieldModel field, string property)
    {
        if (DefaultLiteral(field) is { } literal)
        {
            // Every NaN equals a NaN default, though NaN != NaN
            var isNaN =
                field.Type.Kind == TypeKind.Scalar
                && field.Type.Scalar is ScalarKind.Float32 or ScalarKind.Float64
                && double.IsNaN(field.Default!.Float);
            return isNaN
                ? $"!{ScalarInfo.CSharpName(field.Type.Scalar)}.IsNaN({property})"
                : $"{property} != {literal}";
        }

        return field.Type.Kind == TypeKind.Enum ? $"{property} != default({TypeNames.Of(field.Type)})"
            : field.Type.Scalar == ScalarKind.Bool ? property
            : $"{property} != 0";
    }

    private static void EmitRead(CodeWriter writer, TableModel model, string tableInterface, string fullType)
    {
        writer.OpenBlock(
            $"static void {tableInterface}.Read(ref {TypeNames.Reader} reader, int table, {fullType} value)"
        );

        foreach (var field in model.Fields)
        {
            EmitReadField(writer, model, field);
        }

        writer.CloseBlock();
    }

    private static void EmitReadField(CodeWriter writer, TableModel model, TableFieldModel field)
    {
        var slot = field.Slot;
        var missing =
            $"throw {TypeNames.Reader}.MissingRequiredField({TypeNames.Literal(model.SchemaFullName)}, {TypeNames.Literal(field.SchemaName)});";
        var isUnionValue = field.Type.Kind == TypeKind.Union;
        var isUnionVector = field.Type.Element?.Kind == TypeKind.Union;

        if (isUnionValue || isUnionVector)
        {
            writer.Line($"int type{slot} = reader.GetFieldPosition(table, {slot - 1});");
            writer.Line($"int position{slot} = reader.GetFieldPosition(table, {slot});");
            var condition = isUnionValue
                ? $"type{slot} != 0 && position{slot} != 0 && reader.ReadScalar<byte>(type{slot}) is byte discriminator{slot} and not 0"
                : $"type{slot} != 0 && position{slot} != 0";
            var read = isUnionValue
                ? $"reader.ReadUnion<{TypeNames.Of(field.Type)}>(discriminator{slot}, position{slot})"
                : $"reader.ReadUnionVector<{TypeNames.Of(field.Type.Element!)}>(type{slot}, position{slot})";
            EmitAssignment(writer, field, condition, read, missing);
            return;
        }

        writer.Line($"int position{slot} = reader.GetFieldPosition(table, {slot});");
        EmitAssignment(writer, field, $"position{slot} != 0", ReadExpression(field.Type, $"position{slot}"), missing);
    }

    private static void EmitAssignment(
        CodeWriter writer,
        TableFieldModel field,
        string condition,
        string read,
        string missing
    )
    {
        if (field.IsRequired)
        {
            writer.Line($"value.{field.Name} = {condition} ? {read} : {missing.TrimEnd(';')};");
        }
        else
        {
            writer.Line($"if ({condition}) value.{field.Name} = {read};");
        }
    }

    private static string ReadExpression(TypeModel type, string position) =>
        type.Kind switch
        {
            TypeKind.Scalar when type.Scalar == ScalarKind.Bool => $"reader.ReadBool({position})",
            TypeKind.Scalar or TypeKind.Enum => $"reader.ReadScalar<{TypeNames.Of(type)}>({position})",
            TypeKind.Struct => $"reader.ReadStruct<{TypeNames.Of(type)}>({position})",
            TypeKind.String => $"reader.ReadString({position})",
            TypeKind.Table => $"reader.ReadTable<{TypeNames.Of(type)}>({position})",
            TypeKind.Vector => type.Element!.Kind switch
            {
                TypeKind.Scalar when type.Element.Scalar == ScalarKind.Bool => $"reader.ReadBoolVector({position})",
                TypeKind.Scalar or TypeKind.Enum =>
                    $"reader.ReadScalarVector<{TypeNames.Of(type.Element)}>({position})",
                TypeKind.Struct => $"reader.ReadStructVector<{TypeNames.Of(type.Element)}>({position})",
                TypeKind.String => $"reader.ReadStringVector({position})",
                TypeKind.Table => $"reader.ReadTableVector<{TypeNames.Of(type.Element)}>({position})",
                _ => throw new InvalidOperationException($"Unexpected vector element {type.Element.Kind}."),
            },
            _ => throw new InvalidOperationException($"Unexpected field type {type.Kind}."),
        };
}
