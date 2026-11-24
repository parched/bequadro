using Google.FlatBuffers;

namespace Bequadro.Tests.Interop;

/// <summary>
/// Reads tables by vtable slot with the official Google.FlatBuffers runtime, independently of Bequadro's reader.
/// </summary>
internal readonly struct GoogleTable
{
    private readonly Table _table;

    private GoogleTable(Table table)
    {
        _table = table;
    }

    public ByteBuffer Buffer => _table.bb;

    public int Position => _table.bb_pos;

    public static GoogleTable Root(byte[] bytes)
    {
        var buffer = new ByteBuffer(bytes);
        return new GoogleTable(new Table(buffer.GetInt(0), buffer));
    }

    public bool Has(int slot) => FieldOffset(slot) != 0;

    /// <summary>
    /// The absolute position of the field in the slot, or 0 if absent.
    /// </summary>
    public int FieldPosition(int slot) => FieldOffset(slot) is var offset and not 0 ? _table.bb_pos + offset : 0;

    public sbyte GetSbyte(int slot, sbyte defaultValue = 0) =>
        Has(slot) ? Buffer.GetSbyte(FieldPosition(slot)) : defaultValue;

    public byte GetByte(int slot, byte defaultValue = 0) => Has(slot) ? Buffer.Get(FieldPosition(slot)) : defaultValue;

    public short GetShort(int slot) => Has(slot) ? Buffer.GetShort(FieldPosition(slot)) : (short)0;

    public int GetInt(int slot, int defaultValue = 0) => Has(slot) ? Buffer.GetInt(FieldPosition(slot)) : defaultValue;

    public uint GetUint(int slot) => Has(slot) ? Buffer.GetUint(FieldPosition(slot)) : 0;

    public long GetLong(int slot, long defaultValue = 0) =>
        Has(slot) ? Buffer.GetLong(FieldPosition(slot)) : defaultValue;

    public float GetFloat(int slot, float defaultValue = 0) =>
        Has(slot) ? Buffer.GetFloat(FieldPosition(slot)) : defaultValue;

    public double GetDouble(int slot, double defaultValue = 0) =>
        Has(slot) ? Buffer.GetDouble(FieldPosition(slot)) : defaultValue;

    public string? GetString(int slot) => Has(slot) ? _table.__string(FieldPosition(slot)) : null;

    public GoogleTable? GetTable(int slot) =>
        Has(slot) ? new GoogleTable(new Table(_table.__indirect(FieldPosition(slot)), Buffer)) : null;

    /// <summary>
    /// The position of a struct stored out of line, such as a union member.
    /// </summary>
    public int GetIndirect(int slot) => _table.__indirect(FieldPosition(slot));

    public int VectorLength(int slot) => Has(slot) ? _table.__vector_len(FieldOffset(slot)) : -1;

    public int VectorStart(int slot) => _table.__vector(FieldOffset(slot));

    public string VectorString(int slot, int index) => _table.__string(VectorStart(slot) + (4 * index));

    public GoogleTable VectorTable(int slot, int index) =>
        new(new Table(_table.__indirect(VectorStart(slot) + (4 * index)), Buffer));

    /// <summary>
    /// The position of the indirect target of the element (a table or out-of-line struct).
    /// </summary>
    public int VectorIndirect(int slot, int index) => _table.__indirect(VectorStart(slot) + (4 * index));

    private int FieldOffset(int slot) => _table.__offset(4 + (2 * slot));
}
