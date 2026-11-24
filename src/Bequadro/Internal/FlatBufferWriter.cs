using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Bequadro.Internal;

/// <summary>
/// Builds a FlatBuffer from the back to the front, like the official builders.
/// Positions are tracked as offsets from the end of the buffer, which stay stable as the buffer grows.
/// Children must be written before the table that references them.
/// Used by generated code; not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class FlatBufferWriter
{
    private const int MinimumCapacity = 1024;
    private const int MaxRetainedLookupEntries = 65536;
    private const int InProgress = -1;

    private readonly Dictionary<object, int> _tableOffsets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, List<int>> _vtablesByHash = [];
    private readonly Dictionary<string, int> _stringOffsets = new(StringComparer.Ordinal);

    // The offset of the vtable a table with each number of slots last used in this write, or 0
    private int[] _lastVtables = new int[16];
    private byte[] _buffer = [];
    private int _head;
    private int _minAlign = 1;
    private int[] _slots = new int[16];
    private int _slotCount;
    private int _tableStart;
    private int _depth;
    private bool _shareStrings;

    internal FlatBufferWriter() { }

    /// <summary>
    /// The number of bytes written so far, which is also the offset from the end of the buffer of the most
    /// recently written byte.
    /// </summary>
    public int Offset => _buffer.Length - _head;

    /// <summary>
    /// Prepares for a new buffer, renting at least <paramref name="initialCapacity"/> bytes from the shared pool.
    /// With <paramref name="shareStrings"/>, equal strings are written once.
    /// </summary>
    internal void Start(int initialCapacity, bool shareStrings)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, MinimumCapacity));
        _head = _buffer.Length;
        _minAlign = 1;
        _slotCount = 0;
        _depth = 0;
        _shareStrings = shareStrings;
        // Sized for as many tables as the last write on this thread wrote, so a large graph doesn't grow the map
        // through several resizes, each rehashing every entry
        _tableOffsets.EnsureCapacity(_lastTableCount);
    }

    private int _lastTableCount;

    /// <summary>
    /// Returns the buffer to the pool and empties the lookup tables, so a cached writer doesn't keep the written
    /// graph or its strings alive. Lookup tables that grew unusually large are also trimmed, so a cached writer
    /// holds little memory between writes. Called after every write, whether it succeeded or threw.
    /// </summary>
    internal void Release()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = [];
        _head = 0;
        _lastTableCount = _tableOffsets.Count;
        EmptyLookup(_tableOffsets);
        EmptyLookup(_vtablesByHash);
        Array.Clear(_lastVtables);
        EmptyLookup(_stringOffsets);
    }

    private static void EmptyLookup<TKey, TValue>(Dictionary<TKey, TValue> lookup)
        where TKey : notnull
    {
        var count = lookup.Count;
        lookup.Clear();
        if (count > MaxRetainedLookupEntries)
        {
            lookup.TrimExcess(MaxRetainedLookupEntries);
        }
    }

    /// <summary>
    /// Writes the root uoffset and returns the finished buffer.
    /// </summary>
    internal ReadOnlySpan<byte> Finish(int rootOffset)
    {
        Prep(Math.Max(_minAlign, sizeof(int)), sizeof(int));
        PutUOffset(rootOffset);
        return _buffer.AsSpan(_head);
    }

    /// <summary>
    /// Writes a table once per object instance; later calls with the same instance return the same offset.
    /// </summary>
    public int WriteTable<T>(T value)
        where T : class, IFlatBufferTable<T>
    {
        ref var offset = ref CollectionsMarshal.GetValueRefOrAddDefault(_tableOffsets, value, out var exists);
        if (exists)
        {
            return offset != InProgress
                ? offset
                : throw new InvalidOperationException(
                    $"Cannot serialize a reference cycle: an instance of '{T.SchemaName}' contains itself."
                );
        }

        offset = InProgress;
        if (++_depth > FlatBufferReader.MaxDepth)
        {
            throw new InvalidOperationException(
                $"Cannot serialize tables nested more than {FlatBufferReader.MaxDepth} levels deep, because the buffer couldn't be parsed: an instance of '{T.SchemaName}' is at level {_depth}."
            );
        }

        var written = T.Write(this, value);
        _depth--;
        _tableOffsets[value] = written;
        return written;
    }

    public (byte Type, int Offset) WriteUnion<T>(in T value)
        where T : struct, IFlatBufferUnion<T> => T.Write(this, value);

    /// <summary>
    /// Writes a struct out of line, as used for union members.
    /// </summary>
    public int WriteStruct<T>(in T value)
        where T : unmanaged, IFlatBufferStruct<T>
    {
        Prep(T.Alignment, T.Size);
        T.Write(Allocate(T.Size), value);
        return Offset;
    }

    public int WriteString(string value)
    {
        if (_shareStrings)
        {
            return WriteSharedString(value);
        }

        return WriteNewString(value);
    }

    private int WriteSharedString(string value)
    {
        ref var offset = ref CollectionsMarshal.GetValueRefOrAddDefault(_stringOffsets, value, out var exists);
        if (!exists)
        {
            offset = WriteNewString(value);
        }

        return offset;
    }

    private int WriteNewString(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Prep(sizeof(int), byteCount + 1);
        Allocate(1);
        Encoding.UTF8.GetBytes(value, Allocate(byteCount));
        return PutLength(byteCount);
    }

    public int WriteScalarVector<T>(IReadOnlyList<T> values)
        where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var count = values.Count;
        var byteCount = VectorByteCount(size, count);
        Prep(Math.Max(sizeof(int), size), byteCount);
        var destination = Allocate(byteCount);

        switch (values)
        {
            case T[] array:
                MemoryMarshal.AsBytes(array.AsSpan()).CopyTo(destination);
                break;
            case List<T> list:
                MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(list)).CopyTo(destination);
                break;
            default:
                for (var i = 0; i < count; i++)
                {
                    MemoryMarshal.Write(destination[(i * size)..], values[i]);
                }

                break;
        }

        return PutLength(count);
    }

    public int WriteStructVector<T>(IReadOnlyList<T> values)
        where T : unmanaged, IFlatBufferStruct<T>
    {
        var count = values.Count;
        var byteCount = VectorByteCount(T.Size, count);
        Prep(Math.Max(sizeof(int), T.Alignment), byteCount);
        var destination = Allocate(byteCount);
        for (var i = 0; i < count; i++)
        {
            T.Write(destination.Slice(i * T.Size, T.Size), values[i]);
        }

        return PutLength(count);
    }

    public int WriteStringVector(IReadOnlyList<string> values)
    {
        var count = values.Count;
        var offsets = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                offsets[i] = WriteString(values[i] ?? throw NullElement("string"));
            }

            return WriteOffsetVector(offsets.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(offsets);
        }
    }

    public int WriteTableVector<T>(IReadOnlyList<T> values)
        where T : class, IFlatBufferTable<T>
    {
        var count = values.Count;
        var offsets = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                offsets[i] = WriteTable(values[i] ?? throw NullElement(T.SchemaName));
            }

            return WriteOffsetVector(offsets.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(offsets);
        }
    }

    /// <summary>
    /// Writes a vector of unions as its discriminator vector and value vector.
    /// </summary>
    public (int TypesOffset, int ValuesOffset) WriteUnionVector<T>(IReadOnlyList<T> values)
        where T : struct, IFlatBufferUnion<T>
    {
        var count = values.Count;
        var types = ArrayPool<byte>.Shared.Rent(count);
        var offsets = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                (types[i], offsets[i]) = T.Write(this, values[i]);
            }

            var valuesOffset = WriteOffsetVector(offsets.AsSpan(0, count));
            Prep(sizeof(int), count);
            types.AsSpan(0, count).CopyTo(Allocate(count));
            return (PutLength(count), valuesOffset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(types);
            ArrayPool<int>.Shared.Return(offsets);
        }
    }

    /// <summary>
    /// Writes a vector of uoffsets. An offset of 0 (a union element that is <c>NONE</c>) is written as a literal 0.
    /// </summary>
    public int WriteOffsetVector(ReadOnlySpan<int> offsets)
    {
        Prep(sizeof(int), VectorByteCount(sizeof(int), offsets.Length));
        for (var i = offsets.Length - 1; i >= 0; i--)
        {
            if (offsets[i] == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(Allocate(sizeof(int)), 0);
            }
            else
            {
                PutUOffset(offsets[i]);
            }
        }

        return PutLength(offsets.Length);
    }

    public void StartTable(int slotCount)
    {
        if (_slots.Length < slotCount)
        {
            _slots = new int[Math.Max(slotCount, _slots.Length * 2)];
        }

        Array.Clear(_slots, 0, slotCount);
        _slotCount = slotCount;
        _tableStart = Offset;
    }

    public void AddScalar<T>(int slot, T value)
        where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        Prep(size, size);
        MemoryMarshal.Write(Allocate(size), value);
        _slots[slot] = Offset;
    }

    public void AddStruct<T>(int slot, in T value)
        where T : unmanaged, IFlatBufferStruct<T>
    {
        Prep(T.Alignment, T.Size);
        T.Write(Allocate(T.Size), value);
        _slots[slot] = Offset;
    }

    public void AddOffset(int slot, int targetOffset)
    {
        Prep(sizeof(int), sizeof(int));
        PutUOffset(targetOffset);
        _slots[slot] = Offset;
    }

    /// <summary>
    /// Writes the table's soffset and vtable (sharing an identical existing vtable if there is one) and returns
    /// the table's offset.
    /// </summary>
    public int EndTable<T>()
        where T : class, IFlatBufferTable<T>
    {
        Prep(sizeof(int), sizeof(int));
        Allocate(sizeof(int));
        var tableOffset = Offset;

        var usedSlots = _slotCount;
        while (usedSlots > 0 && _slots[usedSlots - 1] == 0)
        {
            usedSlots--;
        }

        var vtableSize = (2 + usedSlots) * sizeof(ushort);
        var tableSize = tableOffset - _tableStart;
        if (vtableSize > ushort.MaxValue || tableSize > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"Cannot serialize an instance of '{T.SchemaName}': a table's inline fields can take at most {ushort.MaxValue} bytes, and its vtable at most {ushort.MaxValue}."
            );
        }

        Span<byte> vtable = vtableSize <= 256 ? stackalloc byte[vtableSize] : new byte[vtableSize];
        BinaryPrimitives.WriteUInt16LittleEndian(vtable, (ushort)vtableSize);
        BinaryPrimitives.WriteUInt16LittleEndian(vtable[2..], (ushort)tableSize);
        for (var i = 0; i < usedSlots; i++)
        {
            var fieldOffset = _slots[i] == 0 ? 0 : tableOffset - _slots[i];
            BinaryPrimitives.WriteUInt16LittleEndian(vtable[(4 + (2 * i))..], (ushort)fieldOffset);
        }

        // Tables of one type usually have the same vtable, so try the one the last table with as many slots used
        // before hashing: usually one of the same type. The slot count stands for the type, as looking up a per-type
        // static from this shared generic code costs about as much as the hash. Equal bytes mean it is the vtable
        // FindOrWriteVtable would return, as identical vtables are only written once
        if (_slotCount >= _lastVtables.Length)
        {
            Array.Resize(ref _lastVtables, Math.Max(_slotCount + 1, _lastVtables.Length * 2));
        }

        var lastVtable = _lastVtables[_slotCount];
        var vtableOffset =
            lastVtable != 0 && _buffer.AsSpan(_buffer.Length - lastVtable).StartsWith(vtable)
                ? lastVtable
                : FindOrWriteVtable(vtable);
        _lastVtables[_slotCount] = vtableOffset;
        BinaryPrimitives.WriteInt32LittleEndian(
            _buffer.AsSpan(_buffer.Length - tableOffset),
            vtableOffset - tableOffset
        );
        return tableOffset;
    }

    private int FindOrWriteVtable(ReadOnlySpan<byte> vtable)
    {
        var hash = ComputeHash(vtable);
        if (!_vtablesByHash.TryGetValue(hash, out var candidates))
        {
            _vtablesByHash[hash] = candidates = [];
        }

        foreach (var candidate in candidates)
        {
            // A candidate with the same hash can be shorter than this vtable, and end less than vtable.Length bytes
            // before the end of the buffer, so compare against the rest of the buffer rather than a fixed length
            if (_buffer.AsSpan(_buffer.Length - candidate).StartsWith(vtable))
            {
                return candidate;
            }
        }

        vtable.CopyTo(Allocate(vtable.Length));
        candidates.Add(Offset);
        return Offset;
    }

    private static int ComputeHash(ReadOnlySpan<byte> bytes)
    {
        var hash = default(HashCode);
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Pads so that after writing <paramref name="byteCount"/> more bytes the offset is a multiple of
    /// <paramref name="alignment"/>, and ensures capacity for the padding plus those bytes.
    /// </summary>
    private void Prep(int alignment, int byteCount)
    {
        if (alignment > _minAlign)
        {
            _minAlign = alignment;
        }

        var padding = (-(Offset + byteCount)) & (alignment - 1);
        EnsureCapacity(padding + byteCount);
        if (padding > 0)
        {
            Allocate(padding);
        }
    }

    /// <summary>
    /// Reserves <paramref name="byteCount"/> zeroed bytes in front of the current data and returns them.
    /// </summary>
    private Span<byte> Allocate(int byteCount)
    {
        EnsureCapacity(byteCount);
        _head -= byteCount;
        var span = _buffer.AsSpan(_head, byteCount);
        span.Clear();
        return span;
    }

    private void PutUOffset(int targetOffset)
    {
        var destination = Allocate(sizeof(int));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)(Offset - targetOffset));
    }

    private int PutLength(int count)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Allocate(sizeof(int)), count);
        return Offset;
    }

    private void EnsureCapacity(int byteCount)
    {
        if (_head >= byteCount)
        {
            return;
        }

        var used = Offset;
        var newBuffer = ArrayPool<byte>.Shared.Rent(GrowCapacity(_buffer.Length, (long)used + byteCount));
        var newHead = newBuffer.Length - used;
        _buffer.AsSpan(_head).CopyTo(newBuffer.AsSpan(newHead));
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
        _head = newHead;
    }

    /// <summary>
    /// The length to grow a buffer of <paramref name="currentLength"/> bytes to so that it holds
    /// <paramref name="required"/> bytes: double the current length, but no more than the largest possible array.
    /// </summary>
    internal static int GrowCapacity(int currentLength, long required)
    {
        if (required > Array.MaxLength)
        {
            throw TooLarge();
        }

        return (int)Math.Min(Array.MaxLength, Math.Max(required, (long)currentLength * 2));
    }

    /// <summary>
    /// The size of a vector's elements, which must fit in a buffer (SER-012) before anything is allocated for it.
    /// </summary>
    private static int VectorByteCount(int elementSize, int count)
    {
        var byteCount = (long)elementSize * count;
        return byteCount <= Array.MaxLength ? (int)byteCount : throw TooLarge();
    }

    private static InvalidOperationException TooLarge() =>
        new($"The serialized buffer would exceed the maximum size of {Array.MaxLength} bytes.");

    private static InvalidOperationException NullElement(string? elementType) =>
        new($"Cannot serialize a null element in a vector of '{elementType}'.");
}
