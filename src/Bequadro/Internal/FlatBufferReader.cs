using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Bequadro.Internal;

/// <summary>
/// Reads a FlatBuffer with bounds checking, materializing tables once per buffer position.
/// Every malformed access throws <see cref="InvalidDataException"/>.
/// Used by generated code; not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public ref struct FlatBufferReader
{
    /// <summary>
    /// The deepest table nesting that can be parsed, and therefore written.
    /// </summary>
    internal const int MaxDepth = 64;

    private readonly ReadOnlySpan<byte> _buffer;
    private readonly Dictionary<int, object> _tables;
    private readonly StringLookup? _strings;
    private int _depth;

    /// <summary>
    /// Creates a reader. With <paramref name="shareStrings"/>, each string position is decoded once.
    /// </summary>
    internal FlatBufferReader(ReadOnlySpan<byte> buffer, ReaderLookups lookups, bool shareStrings)
    {
        _buffer = buffer;
        _tables = lookups.Tables;
        _strings = shareStrings ? lookups.Strings : null;
        _strings?.Start(buffer.Length);
        _depth = 0;
    }

    internal T ReadRoot<T>()
        where T : class, IFlatBufferTable<T> => ReadTable<T>(0);

    /// <summary>
    /// Returns the absolute position of a table field, or 0 if the field is absent.
    /// </summary>
    public readonly int GetFieldPosition(int tablePosition, int slot)
    {
        var vtablePosition = (long)tablePosition - ReadInt32(tablePosition);
        if (vtablePosition < 0 || vtablePosition > _buffer.Length - 4)
        {
            ThrowHelper.OutOfBounds((int)Math.Clamp(vtablePosition, int.MinValue, int.MaxValue), 4);
        }

        var vtable = (int)vtablePosition;
        var vtableSize = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[vtable..]);
        var entry = 4 + (2 * slot);
        if (entry + 2 > vtableSize)
        {
            return 0;
        }

        var fieldOffset = ReadUInt16(vtable + entry);
        return fieldOffset == 0 ? 0 : tablePosition + fieldOffset;
    }

    public readonly T ReadScalar<T>(int position)
        where T : unmanaged
    {
        CheckBounds(position, Unsafe.SizeOf<T>());
        return MemoryMarshal.Read<T>(_buffer[position..]);
    }

    public readonly bool ReadBool(int position) => ReadScalar<byte>(position) != 0;

    public readonly T ReadStruct<T>(int position)
        where T : unmanaged, IFlatBufferStruct<T>
    {
        CheckBounds(position, T.Size);
        return T.Read(_buffer.Slice(position, T.Size));
    }

    /// <summary>
    /// Reads a struct stored out of line (as a union member) whose uoffset is at <paramref name="uoffsetPosition"/>.
    /// </summary>
    public readonly T ReadStructAt<T>(int uoffsetPosition)
        where T : unmanaged, IFlatBufferStruct<T> => ReadStruct<T>(FollowUOffset(uoffsetPosition));

    /// <summary>
    /// Reads a string. When strings are shared, returns the existing instance if the string at that position was
    /// already read.
    /// </summary>
    public readonly string ReadString(int uoffsetPosition)
    {
        var position = FollowUOffset(uoffsetPosition);
        if (_strings is null)
        {
            return DecodeString(position);
        }

        if (!_strings.TryGet(position, out var value))
        {
            value = DecodeString(position);
            _strings.Add(position, value);
        }

        return value;
    }

    private readonly string DecodeString(int position)
    {
        var (start, length) = GetVectorAt(position, 1);
        return Encoding.UTF8.GetString(_buffer.Slice(start, length));
    }

    /// <summary>
    /// Reads a table, returning the existing instance if the table at that position was already read.
    /// </summary>
    public T ReadTable<T>(int uoffsetPosition)
        where T : class, IFlatBufferTable<T>
    {
        var tablePosition = FollowUOffset(uoffsetPosition);
        CheckBounds(tablePosition, 4);

        // One lookup for both finding and adding. The entry is set before reading the table's fields, which may add
        // entries and so move it
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_tables, tablePosition, out var exists);
        if (exists)
        {
            return entry as T
                ?? ThrowHelper.InvalidData<T>(
                    $"The buffer is malformed: the table at {tablePosition} is read both as '{T.SchemaName}' and as another table."
                );
        }

        var value = T.CreateForRead();
        entry = value;
        if (++_depth > MaxDepth)
        {
            ThrowHelper.InvalidData($"The buffer exceeds the maximum table nesting depth of {MaxDepth}.");
        }

        T.Read(ref this, tablePosition, value);
        _depth--;
        return value;
    }

    public T ReadUnion<T>(byte type, int uoffsetPosition)
        where T : struct, IFlatBufferUnion<T> => T.Read(ref this, type, uoffsetPosition);

    public readonly T[] ReadScalarVector<T>(int uoffsetPosition)
        where T : unmanaged
    {
        var (start, count) = GetVector(uoffsetPosition, Unsafe.SizeOf<T>());
        return MemoryMarshal.Cast<byte, T>(_buffer.Slice(start, count * Unsafe.SizeOf<T>())).ToArray();
    }

    public readonly bool[] ReadBoolVector(int uoffsetPosition)
    {
        var (start, count) = GetVector(uoffsetPosition, 1);
        var result = new bool[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = _buffer[start + i] != 0;
        }

        return result;
    }

    public readonly T[] ReadStructVector<T>(int uoffsetPosition)
        where T : unmanaged, IFlatBufferStruct<T>
    {
        var (start, count) = GetVector(uoffsetPosition, T.Size);
        var result = new T[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = T.Read(_buffer.Slice(start + (i * T.Size), T.Size));
        }

        return result;
    }

    public readonly string[] ReadStringVector(int uoffsetPosition)
    {
        var (start, count) = GetVector(uoffsetPosition, 4);
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = ReadString(start + (4 * i));
        }

        return result;
    }

    public T[] ReadTableVector<T>(int uoffsetPosition)
        where T : class, IFlatBufferTable<T>
    {
        var (start, count) = GetVector(uoffsetPosition, 4);
        var result = new T[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = ReadTable<T>(start + (4 * i));
        }

        return result;
    }

    public T[] ReadUnionVector<T>(int typesUOffsetPosition, int valuesUOffsetPosition)
        where T : struct, IFlatBufferUnion<T>
    {
        var (typesStart, typesCount) = GetVector(typesUOffsetPosition, 1);
        var (valuesStart, valuesCount) = GetVector(valuesUOffsetPosition, 4);
        if (typesCount != valuesCount)
        {
            ThrowHelper.InvalidData(
                "The buffer is malformed: a union vector's type and value vectors differ in length."
            );
        }

        var result = new T[valuesCount];
        for (var i = 0; i < valuesCount; i++)
        {
            result[i] = T.Read(ref this, _buffer[typesStart + i], valuesStart + (4 * i));
        }

        return result;
    }

    public static InvalidDataException MissingRequiredField(string table, string field) =>
        new($"The buffer is malformed: required field '{field}' of table '{table}' is missing.");

    public static InvalidDataException UnknownUnionType(string union, byte type) =>
        new($"The buffer contains an unknown discriminator {type} for union '{union}'.");

    public static InvalidDataException UnionNoneNotAllowed(string union) =>
        new(
            $"The buffer contains a NONE element in a vector of union '{union}', which is declared with bequadro_exclude_none."
        );

    /// <summary>
    /// Returns the position of the first element and the element count of the vector whose uoffset is at
    /// <paramref name="uoffsetPosition"/>, after checking the elements are within the buffer.
    /// </summary>
    private readonly (int Start, int Count) GetVector(int uoffsetPosition, int elementSize) =>
        GetVectorAt(FollowUOffset(uoffsetPosition), elementSize);

    private readonly (int Start, int Count) GetVectorAt(int vectorPosition, int elementSize)
    {
        var count = ReadInt32(vectorPosition);
        var start = vectorPosition + 4;
        if (count < 0 || (long)count * elementSize > _buffer.Length - start)
        {
            ThrowHelper.InvalidData(
                $"The buffer is malformed: the vector at {vectorPosition} with {count} elements is out of bounds."
            );
        }

        return (start, count);
    }

    private readonly int FollowUOffset(int uoffsetPosition)
    {
        var target = (long)uoffsetPosition + ReadUInt32(uoffsetPosition);
        if (target >= _buffer.Length)
        {
            ThrowHelper.OutOfBounds((int)Math.Min(target, int.MaxValue), 0);
        }

        return (int)target;
    }

    private readonly int ReadInt32(int position)
    {
        CheckBounds(position, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(_buffer[position..]);
    }

    private readonly uint ReadUInt32(int position)
    {
        CheckBounds(position, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(_buffer[position..]);
    }

    private readonly ushort ReadUInt16(int position)
    {
        CheckBounds(position, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(_buffer[position..]);
    }

    private readonly void CheckBounds(int position, int size)
    {
        if (position < 0 || position > _buffer.Length - size)
        {
            ThrowHelper.OutOfBounds(position, size);
        }
    }
}
