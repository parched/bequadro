using System.ComponentModel;

namespace Bequadro.Internal;

/// <summary>
/// Implemented by generated table classes. Not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IFlatBufferTable<T>
    where T : class, IFlatBufferTable<T>
{
    /// <summary>
    /// The table's fully qualified name in the schema, which error messages use.
    /// </summary>
    static abstract string SchemaName { get; }

    /// <summary>
    /// Writes the table (after its children) and returns its offset from the end of the buffer.
    /// </summary>
    static abstract int Write(FlatBufferWriter writer, T value);

    /// <summary>
    /// Creates an instance whose fields will be populated by <see cref="Read"/>.
    /// </summary>
    static abstract T CreateForRead();

    /// <summary>
    /// Populates <paramref name="value"/> from the table at <paramref name="tablePosition"/>.
    /// </summary>
    static abstract void Read(ref FlatBufferReader reader, int tablePosition, T value);
}
