using Bequadro.Generator.Common;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Emit;

/// <summary>
/// Emits a union as a <c>[Union]</c> struct, so C# pattern matching (<c>switch</c>, <c>is</c>) works on it with
/// exhaustiveness checking. The case types are the members plus, unless excluded by attributes, <c>UnionNone</c>
/// and <c>UnionUnknown</c>.
/// </summary>
internal static class UnionEmitter
{
    private const string NoneType = "global::Bequadro.UnionNone";
    private const string UnknownType = "global::Bequadro.UnionUnknown";
    private const string Interop = "global::System.Runtime.InteropServices";

    public static void Emit(CodeWriter writer, UnionModel model)
    {
        var name = Naming.EscapeTypeName(model.Name);
        var fullType = Naming.GlobalTypeName(model.FullName);
        var unionInterface = $"global::Bequadro.Internal.IFlatBufferUnion<{fullType}>";
        var layout = new Layout(model);

        writer.Line("[global::System.Runtime.CompilerServices.Union]");
        if (layout.IsExplicit)
        {
            writer.Line($"[{Interop}.StructLayout({Interop}.LayoutKind.Explicit)]");
        }

        writer.OpenBlock(
            $"public readonly partial struct {name} : global::System.IEquatable<{fullType}>, global::System.Runtime.CompilerServices.IUnion, {unionInterface}"
        );
        EmitFields(writer, model, layout);
        EmitItemKind(writer, model);
        writer.Line();
        writer.Line("public ItemKind Kind => (ItemKind)_kind;");
        writer.Line();

        foreach (var member in model.Members)
        {
            EmitMember(writer, model, member, name);
        }

        if (model.AllowsNone)
        {
            EmitNone(writer, name);
        }
        else
        {
            writer.Line("public bool HasValue => _kind != 0;");
            writer.Line();
        }

        if (model.AllowsUnknown)
        {
            EmitUnknown(writer, model, name);
        }

        EmitValue(writer, model);
        writer.Line();
        EmitEquality(writer, model, name, fullType);
        writer.Line();
        writer.Line($"public override string ToString() => $\"{model.Name} {{{{ {{this.Value}} }}}}\";");
        writer.Line();
        EmitWrite(writer, model, unionInterface, fullType);
        writer.Line();
        EmitRead(writer, model, unionInterface, fullType);
        writer.CloseBlock();
    }

    /// <summary>
    /// Table members share one reference field. Each struct member gets its own field, so no member is boxed. With two
    /// or more struct members the layout is explicit and the struct fields overlap, which keeps the union no larger
    /// than its largest struct. That is safe because structs are unmanaged (<c>IFlatBufferStruct</c> requires it). The
    /// struct fields come after the discriminator, so consumer fields that make a struct larger can't overlap it.
    /// </summary>
    private static void EmitFields(CodeWriter writer, UnionModel model, Layout layout)
    {
        if (layout.HasTables)
        {
            writer.Line($"{layout.Offset(0)}private readonly object? _reference;");
            writer.Line();
        }

        writer.Line($"{layout.Offset(layout.KindOffset)}private readonly byte _kind;");
        writer.Line();

        foreach (var member in model.Members.Where(m => m.Type.Kind == TypeKind.Struct))
        {
            writer.Line(
                $"{layout.Offset(layout.StructsOffset)}private readonly {TypeNames.Of(member.Type)} {StructField(member)};"
            );
            writer.Line();
        }
    }

    private static void EmitItemKind(CodeWriter writer, UnionModel model)
    {
        writer.OpenBlock("public enum ItemKind : byte");
        writer.Line("NONE = 0,");
        foreach (var member in model.Members)
        {
            writer.DocComment(member.Documentation, member.RenamedFrom);
            writer.Line($"{Naming.Escape(member.Label)} = {member.Discriminator},");
        }

        writer.CloseBlock();
    }

    private static void EmitMember(CodeWriter writer, UnionModel model, UnionMemberModel member, string name)
    {
        var type = TypeNames.Of(member.Type);
        var label = Naming.Escape(member.Label);
        var isTable = member.Type.Kind == TypeKind.Table;
        var test = $"_kind == {member.Discriminator}";
        var get = Get(member);

        writer.OpenBlock($"public {name}({type} value)");
        writer.Line(
            isTable
                ? "_reference = value ?? throw new global::System.ArgumentNullException(nameof(value));"
                : $"{StructField(member)} = value;"
        );
        writer.Line($"_kind = {member.Discriminator};");
        writer.CloseBlock();
        writer.Line();
        writer.Line($"public static implicit operator {name}({type} value) => new(value);");
        writer.Line();
        writer.DocComment(member.Documentation, member.RenamedFrom);
        var notHeld = TypeNames.Literal(
            $"The union '{model.SchemaFullName}' does not hold '{member.RenamedFrom ?? member.Label}'."
        );
        writer.Line(
            $"public {type} {label} => {test} ? {get} : throw new {TypeNames.InvalidOperationException}({notHeld});"
        );
        writer.Line();
        var parameter = isTable
            ? $"[global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out {type}? value"
            : $"out {type} value";
        EmitTryGetValue(writer, parameter, test, get);
    }

    private static void EmitNone(CodeWriter writer, string name)
    {
        writer.Line($"public {name}({NoneType} value) {{ }}");
        writer.Line();
        writer.Line($"public static implicit operator {name}({NoneType} value) => default;");
        writer.Line();
        EmitTryGetValue(writer, $"out {NoneType} value", "_kind == 0", "default");
    }

    private static void EmitUnknown(CodeWriter writer, UnionModel model, string name)
    {
        var known = KnownDiscriminators(model);
        var invalid = TypeNames.Literal(
            $"An unknown member of union '{model.SchemaFullName}' needs a discriminator that is neither 0 (NONE) nor a member's."
        );
        writer.OpenBlock($"public {name}({UnknownType} value)");
        writer.Line(
            $"_kind = value.Discriminator is not ({known}) ? value.Discriminator : throw new global::System.ArgumentOutOfRangeException(nameof(value), {invalid});"
        );
        writer.CloseBlock();
        writer.Line();
        EmitTryGetValue(writer, $"out {UnknownType} value", $"_kind is not ({known})", "new(_kind)");
    }

    private static void EmitTryGetValue(CodeWriter writer, string parameter, string test, string get)
    {
        writer.OpenBlock($"public bool TryGetValue({parameter})");
        writer.OpenBlock($"if ({test})");
        writer.Line($"value = {get};");
        writer.Line("return true;");
        writer.CloseBlock();
        writer.Line();
        writer.Line("value = default;");
        writer.Line("return false;");
        writer.CloseBlock();
        writer.Line();
    }

    /// <summary>
    /// <c>IUnion.Value</c>: the held case as an object. Pattern matching on the union's case types doesn't use it, so
    /// boxing a struct member here costs nothing on the usual paths.
    /// </summary>
    private static void EmitValue(CodeWriter writer, UnionModel model)
    {
        writer.OpenBlock("public object? Value => _kind switch");
        foreach (var member in model.Members)
        {
            var value = member.Type.Kind == TypeKind.Table ? "_reference" : StructField(member);
            writer.Line($"{member.Discriminator} => {value},");
        }

        if (model.AllowsNone)
        {
            writer.Line($"0 => new {NoneType}(),");
        }
        else if (model.AllowsUnknown)
        {
            // Without a NONE case, default(T) holds nothing, and 0 is not an unknown discriminator
            writer.Line("0 => null,");
        }

        writer.Line(model.AllowsUnknown ? $"_ => new {UnknownType}(_kind)," : "_ => null,");
        writer.CloseBlock(";");
    }

    private static void EmitEquality(CodeWriter writer, UnionModel model, string name, string fullType)
    {
        writer.OpenBlock($"public bool Equals({fullType} other) => _kind == other._kind && _kind switch");
        foreach (var member in model.Members)
        {
            var equals =
                member.Type.Kind == TypeKind.Table
                    ? "global::System.Object.Equals(_reference, other._reference)"
                    : $"{StructField(member)}.Equals(other.{StructField(member)})";
            writer.Line($"{member.Discriminator} => {equals},");
        }

        writer.Line("_ => true,");
        writer.CloseBlock(";");
        writer.Line();
        writer.Line($"public override bool Equals(object? obj) => obj is {fullType} other && this.Equals(other);");
        writer.Line();
        writer.OpenBlock("public override int GetHashCode() => _kind switch");
        foreach (var member in model.Members)
        {
            var value = member.Type.Kind == TypeKind.Table ? "_reference" : StructField(member);
            writer.Line($"{member.Discriminator} => global::System.HashCode.Combine(_kind, {value}),");
        }

        writer.Line("_ => _kind,");
        writer.CloseBlock(";");
        writer.Line();
        writer.Line($"public static bool operator ==({name} left, {name} right) => left.Equals(right);");
        writer.Line();
        writer.Line($"public static bool operator !=({name} left, {name} right) => !left.Equals(right);");
    }

    /// <summary>
    /// Returns (0, 0) for <c>NONE</c> when the union allows it, which callers treat as nothing to write.
    /// </summary>
    private static void EmitWrite(CodeWriter writer, UnionModel model, string unionInterface, string fullType)
    {
        writer.OpenBlock(
            $"static (byte Type, int Offset) {unionInterface}.Write({TypeNames.Writer} writer, in {fullType} value)"
        );
        writer.OpenBlock("return value._kind switch");
        foreach (var member in model.Members)
        {
            var write =
                member.Type.Kind == TypeKind.Table
                    ? $"writer.WriteTable(({TypeNames.Of(member.Type)})value._reference!)"
                    : $"writer.WriteStruct(value.{StructField(member)})";
            writer.Line($"{member.Discriminator} => ((byte){member.Discriminator}, {write}),");
        }

        var none = model.AllowsNone
            ? "default"
            : $"throw new {TypeNames.InvalidOperationException}({TypeNames.Literal($"Cannot serialize the union '{model.SchemaFullName}' because it has no value.")})";
        if (model.AllowsUnknown)
        {
            var unknown =
                $"$\"Cannot serialize the union '{model.SchemaFullName}' because it holds an unknown member (discriminator {{value._kind}}), whose data is not kept when parsing.\"";
            writer.Line($"0 => {none},");
            writer.Line($"_ => throw new {TypeNames.InvalidOperationException}({unknown}),");
        }
        else
        {
            writer.Line($"_ => {none},");
        }

        writer.CloseBlock(";");
        writer.CloseBlock();
    }

    /// <summary>
    /// Fields pass only non-zero discriminators; vector elements can be <c>NONE</c>.
    /// </summary>
    private static void EmitRead(CodeWriter writer, UnionModel model, string unionInterface, string fullType)
    {
        writer.OpenBlock(
            $"static {fullType} {unionInterface}.Read(ref {TypeNames.Reader} reader, byte type, int position)"
        );
        writer.OpenBlock("switch (type)");
        foreach (var member in model.Members)
        {
            var type = TypeNames.Of(member.Type);
            var read =
                member.Type.Kind == TypeKind.Table
                    ? $"reader.ReadTable<{type}>(position)"
                    : $"reader.ReadStructAt<{type}>(position)";
            writer.Line($"case {member.Discriminator}:");
            writer.Line($"    return new {fullType}({read});");
        }

        var unionName = TypeNames.Literal(model.SchemaFullName);
        writer.Line("case 0:");
        writer.Line(
            model.AllowsNone ? "    return default;" : $"    throw {TypeNames.Reader}.UnionNoneNotAllowed({unionName});"
        );
        writer.Line("default:");
        writer.Line(
            model.AllowsUnknown
                ? $"    return new {fullType}(new {UnknownType}(type));"
                : $"    throw {TypeNames.Reader}.UnknownUnionType({unionName}, type);"
        );
        writer.CloseBlock();
        writer.CloseBlock();
    }

    private static string Get(UnionMemberModel member) =>
        member.Type.Kind == TypeKind.Table ? $"({TypeNames.Of(member.Type)})_reference!" : StructField(member);

    private static string StructField(UnionMemberModel member) => $"_value{member.Discriminator}";

    private static string KnownDiscriminators(UnionModel model) =>
        string.Join(" or ", new[] { 0 }.Concat(model.Members.Select(m => m.Discriminator)));

    private sealed class Layout
    {
        public Layout(UnionModel model)
        {
            var structs = model.Members.Where(m => m.Type.Kind == TypeKind.Struct).ToList();
            HasTables = model.Members.Any(m => m.Type.Kind == TypeKind.Table);
            IsExplicit = structs.Count >= 2;
            // The struct fields come last, because a consumer partial can make a struct larger than its schema size
            // (API-004), and nothing can follow a field whose size the generator doesn't know. The reference is given
            // 8 bytes so it stays aligned on 64-bit platforms, and the structs start at their schema alignment.
            KindOffset = HasTables ? 8 : 0;
            var alignment = structs.Count == 0 ? 1 : structs.Max(m => m.Type.StructAlignment);
            StructsOffset = (KindOffset + 1 + alignment - 1) / alignment * alignment;
        }

        public bool HasTables { get; }

        public bool IsExplicit { get; }

        public int StructsOffset { get; }

        public int KindOffset { get; }

        public string Offset(int offset) => IsExplicit ? $"[{Interop}.FieldOffset({offset})] " : "";
    }
}
