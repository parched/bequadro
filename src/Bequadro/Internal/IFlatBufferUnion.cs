using System.ComponentModel;

namespace Bequadro.Internal;

/// <summary>
/// Implemented by generated unions. Not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IFlatBufferUnion<T>
    where T : struct, IFlatBufferUnion<T>
{
    /// <summary>
    /// Writes the union's value and returns its discriminator and offset from the end of the buffer.
    /// </summary>
    static abstract (byte Type, int Offset) Write(FlatBufferWriter writer, in T value);

    /// <summary>
    /// Reads a union value of the given discriminator whose uoffset is at <paramref name="uoffsetPosition"/>.
    /// </summary>
    static abstract T Read(ref FlatBufferReader reader, byte type, int uoffsetPosition);
}
