using Bequadro.Generator.Common;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Emit;

internal static class StructEmitter
{
    public static void Emit(CodeWriter writer, StructModel model)
    {
        var name = Naming.EscapeTypeName(model.Name);
        var fullType = Naming.GlobalTypeName(model.FullName);
        var structInterface = $"global::Bequadro.Internal.IFlatBufferStruct<{fullType}>";

        // Fields are read and written one by one, never copied as memory, so their order in memory doesn't matter. Auto
        // layout says so, which lets a consumer partial add fields without warning CS0282.
        writer.Line(
            "[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Auto)]"
        );
        writer.OpenBlock($"public partial struct {name} : global::System.IEquatable<{fullType}>, {structInterface}");

        foreach (var field in model.Fields)
        {
            writer.DocComment(field.Documentation, field.RenamedFrom);
            writer.Line($"public {TypeNames.Of(field.Type)} {field.Name};");
            writer.Line();
        }

        EmitEquality(writer, model, name, fullType);
        writer.Line();
        writer.Line($"public override readonly string ToString() => {ToStringExpression(model)};");
        writer.Line();
        writer.Line($"static int {structInterface}.Size => {model.Size};");
        writer.Line();
        writer.Line($"static int {structInterface}.Alignment => {model.Alignment};");
        writer.Line();
        EmitWrite(writer, model, structInterface, fullType);
        writer.Line();
        EmitRead(writer, model, structInterface, fullType);
        writer.CloseBlock();
    }

    private static void EmitEquality(CodeWriter writer, StructModel model, string name, string fullType)
    {
        var comparisons = model.Fields.Select(f =>
            $"global::System.Collections.Generic.EqualityComparer<{TypeNames.Of(f.Type)}>.Default.Equals(this.{f.Name}, other.{f.Name})"
        );
        writer.OpenBlock($"public readonly bool Equals({fullType} other)");
        var comparisonList = comparisons.ToList();
        for (var i = 0; i < comparisonList.Count; i++)
        {
            var prefix = i == 0 ? "return " : "    && ";
            var suffix = i == comparisonList.Count - 1 ? ";" : "";
            writer.Line(prefix + comparisonList[i] + suffix);
        }

        writer.CloseBlock();
        writer.Line();
        writer.Line(
            $"public override readonly bool Equals(object? obj) => obj is {fullType} other && this.Equals(other);"
        );
        writer.Line();
        writer.OpenBlock("public override readonly int GetHashCode()");
        writer.Line("global::System.HashCode hash = new();");
        foreach (var field in model.Fields)
        {
            writer.Line($"hash.Add(this.{field.Name});");
        }

        writer.Line("return hash.ToHashCode();");
        writer.CloseBlock();
        writer.Line();
        writer.Line($"public static bool operator ==({name} left, {name} right) => left.Equals(right);");
        writer.Line();
        writer.Line($"public static bool operator !=({name} left, {name} right) => !left.Equals(right);");
    }

    private static string ToStringExpression(StructModel model)
    {
        var members = string.Join(", ", model.Fields.Select(f => $"{f.Name} = {{this.{f.Name}}}"));
        return $"$\"{model.Name} {{{{ {members} }}}}\"";
    }

    private static void EmitWrite(CodeWriter writer, StructModel model, string structInterface, string fullType)
    {
        writer.OpenBlock(
            $"static void {structInterface}.Write(global::System.Span<byte> destination, in {fullType} value)"
        );
        foreach (var field in model.Fields)
        {
            var target = $"value.{field.Name}";
            writer.Line(WriteStatement(field.Type, field.Offset, target));
        }

        writer.CloseBlock();
    }

    private static string WriteStatement(TypeModel type, int offset, string value)
    {
        if (type.Kind == TypeKind.Struct)
        {
            return $"global::Bequadro.Internal.StructHelpers.Write(destination.Slice({offset}, {type.StructSize}), {value});";
        }

        var scalar = type.Scalar;
        var underlying = ScalarInfo.CSharpName(scalar);
        var raw = type.Kind == TypeKind.Enum ? $"({underlying}){value}" : value;
        return scalar switch
        {
            ScalarKind.Bool => $"destination[{offset}] = {value} ? (byte)1 : (byte)0;",
            ScalarKind.UInt8 => $"destination[{offset}] = {raw};",
            ScalarKind.Int8 => $"destination[{offset}] = unchecked((byte){raw});",
            _ =>
                $"global::System.Buffers.Binary.BinaryPrimitives.Write{TypeNames.BinaryPrimitivesSuffix(scalar)}LittleEndian(destination.Slice({offset}), {raw});",
        };
    }

    private static void EmitRead(CodeWriter writer, StructModel model, string structInterface, string fullType)
    {
        writer.OpenBlock($"static {fullType} {structInterface}.Read(global::System.ReadOnlySpan<byte> source)");
        writer.OpenBlock($"return new {fullType}");
        foreach (var field in model.Fields)
        {
            writer.Line($"{field.Name} = {ReadExpression(field.Type, field.Offset)},");
        }

        writer.CloseBlock(";");
        writer.CloseBlock();
    }

    private static string ReadExpression(TypeModel type, int offset)
    {
        if (type.Kind == TypeKind.Struct)
        {
            return $"global::Bequadro.Internal.StructHelpers.Read<{TypeNames.Of(type)}>(source.Slice({offset}, {type.StructSize}))";
        }

        var scalar = type.Scalar;
        var expression = scalar switch
        {
            ScalarKind.Bool => $"source[{offset}] != 0",
            ScalarKind.UInt8 => $"source[{offset}]",
            ScalarKind.Int8 => $"unchecked((sbyte)source[{offset}])",
            _ =>
                $"global::System.Buffers.Binary.BinaryPrimitives.Read{TypeNames.BinaryPrimitivesSuffix(scalar)}LittleEndian(source.Slice({offset}))",
        };
        return type.Kind == TypeKind.Enum ? $"({TypeNames.Of(type)}){expression}" : expression;
    }
}
