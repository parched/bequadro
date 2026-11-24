using System.ComponentModel;

namespace Bequadro.Internal;

/// <summary>
/// Implemented by generated structs. Not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IFlatBufferStruct<T>
    where T : unmanaged, IFlatBufferStruct<T>
{
    static abstract int Size { get; }

    static abstract int Alignment { get; }

    /// <summary>
    /// Writes the struct into <paramref name="destination"/>, which is exactly <see cref="Size"/> bytes and zeroed.
    /// </summary>
    static abstract void Write(Span<byte> destination, in T value);

    /// <summary>
    /// Reads the struct from <paramref name="source"/>, which is exactly <see cref="Size"/> bytes.
    /// </summary>
    static abstract T Read(ReadOnlySpan<byte> source);
}
