using System.ComponentModel;

namespace Bequadro.Internal;

/// <summary>
/// Gives generated structs access to the explicitly implemented serialization members of nested structs.
/// Used by generated code; not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class StructHelpers
{
    public static void Write<T>(Span<byte> destination, in T value)
        where T : unmanaged, IFlatBufferStruct<T> => T.Write(destination, value);

    public static T Read<T>(ReadOnlySpan<byte> source)
        where T : unmanaged, IFlatBufferStruct<T> => T.Read(source);
}
