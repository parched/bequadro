namespace Bequadro.Schema.Semantics;

internal static class ScalarInfo
{
    private static readonly Dictionary<string, ScalarKind> ByName = new(StringComparer.Ordinal)
    {
        ["bool"] = ScalarKind.Bool,
        ["byte"] = ScalarKind.Int8,
        ["int8"] = ScalarKind.Int8,
        ["ubyte"] = ScalarKind.UInt8,
        ["uint8"] = ScalarKind.UInt8,
        ["short"] = ScalarKind.Int16,
        ["int16"] = ScalarKind.Int16,
        ["ushort"] = ScalarKind.UInt16,
        ["uint16"] = ScalarKind.UInt16,
        ["int"] = ScalarKind.Int32,
        ["int32"] = ScalarKind.Int32,
        ["uint"] = ScalarKind.UInt32,
        ["uint32"] = ScalarKind.UInt32,
        ["long"] = ScalarKind.Int64,
        ["int64"] = ScalarKind.Int64,
        ["ulong"] = ScalarKind.UInt64,
        ["uint64"] = ScalarKind.UInt64,
        ["float"] = ScalarKind.Float32,
        ["float32"] = ScalarKind.Float32,
        ["double"] = ScalarKind.Float64,
        ["float64"] = ScalarKind.Float64,
    };

    public static bool TryGet(string name, out ScalarKind kind) => ByName.TryGetValue(name, out kind);

    public static int Size(ScalarKind kind) =>
        kind switch
        {
            ScalarKind.Bool or ScalarKind.Int8 or ScalarKind.UInt8 => 1,
            ScalarKind.Int16 or ScalarKind.UInt16 => 2,
            ScalarKind.Int32 or ScalarKind.UInt32 or ScalarKind.Float32 => 4,
            _ => 8,
        };

    public static bool IsInteger(ScalarKind kind) =>
        kind is not (ScalarKind.Bool or ScalarKind.Float32 or ScalarKind.Float64);

    public static bool IsFloat(ScalarKind kind) => kind is ScalarKind.Float32 or ScalarKind.Float64;

    public static string CSharpName(ScalarKind kind) =>
        kind switch
        {
            ScalarKind.Bool => "bool",
            ScalarKind.Int8 => "sbyte",
            ScalarKind.UInt8 => "byte",
            ScalarKind.Int16 => "short",
            ScalarKind.UInt16 => "ushort",
            ScalarKind.Int32 => "int",
            ScalarKind.UInt32 => "uint",
            ScalarKind.Int64 => "long",
            ScalarKind.UInt64 => "ulong",
            ScalarKind.Float32 => "float",
            ScalarKind.Float64 => "double",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>
    /// The inclusive range of an integer scalar, as (min, max) where min is signed and max is unsigned.
    /// </summary>
    public static (long Min, ulong Max) IntegerRange(ScalarKind kind) =>
        kind switch
        {
            ScalarKind.Int8 => (sbyte.MinValue, (ulong)sbyte.MaxValue),
            ScalarKind.UInt8 => (0, byte.MaxValue),
            ScalarKind.Int16 => (short.MinValue, (ulong)short.MaxValue),
            ScalarKind.UInt16 => (0, ushort.MaxValue),
            ScalarKind.Int32 => (int.MinValue, int.MaxValue),
            ScalarKind.UInt32 => (0, uint.MaxValue),
            ScalarKind.Int64 => (long.MinValue, long.MaxValue),
            ScalarKind.UInt64 => (0, ulong.MaxValue),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
