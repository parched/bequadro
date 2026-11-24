using Bequadro.Generator.Common;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Emit;

internal static class TypeNames
{
    public const string Writer = "global::Bequadro.Internal.FlatBufferWriter";
    public const string Reader = "global::Bequadro.Internal.FlatBufferReader";
    public const string InvalidOperationException = "global::System.InvalidOperationException";

    /// <summary>
    /// The C# type of a value of the given schema type, without nullability.
    /// </summary>
    public static string Of(TypeModel type) =>
        type.Kind switch
        {
            TypeKind.Scalar => ScalarInfo.CSharpName(type.Scalar),
            TypeKind.String => "string",
            TypeKind.Vector => $"global::System.Collections.Generic.IReadOnlyList<{Of(type.Element!)}>",
            _ => Naming.GlobalTypeName(type.FullName!),
        };

    /// <summary>
    /// The suffix of the <c>BinaryPrimitives</c> read and write methods for a multi-byte scalar.
    /// </summary>
    public static string BinaryPrimitivesSuffix(ScalarKind scalar) =>
        scalar switch
        {
            ScalarKind.Int16 => "Int16",
            ScalarKind.UInt16 => "UInt16",
            ScalarKind.Int32 => "Int32",
            ScalarKind.UInt32 => "UInt32",
            ScalarKind.Int64 => "Int64",
            ScalarKind.UInt64 => "UInt64",
            ScalarKind.Float32 => "Single",
            ScalarKind.Float64 => "Double",
            _ => throw new ArgumentOutOfRangeException(nameof(scalar)),
        };

    /// <summary>
    /// A C# string literal for the given text.
    /// </summary>
    public static string Literal(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
