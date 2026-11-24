namespace Bequadro.Internal;

/// <summary>
/// Keeps one writer per thread so repeated serialization reuses its lookup tables.
/// The byte buffer itself is rented from the shared array pool for the duration of each write.
/// </summary>
internal static class WriterCache
{
    [ThreadStatic]
    private static FlatBufferWriter? _cached;

    public static FlatBufferWriter Rent(int initialCapacity, bool shareStrings)
    {
        var writer = _cached ?? new FlatBufferWriter();
        _cached = null;
        writer.Start(initialCapacity, shareStrings);
        return writer;
    }

    public static void Return(FlatBufferWriter writer)
    {
        writer.Release();
        _cached = writer;
    }
}
