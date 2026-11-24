namespace Bequadro.Internal;

/// <summary>
/// The lookups a parse uses to return one instance per table and string in the buffer.
/// </summary>
internal sealed class ReaderLookups
{
    private const int MaxRetainedEntries = 65536;

    public Dictionary<int, object> Tables { get; } = [];

    public StringLookup Strings { get; } = new();

    /// <summary>
    /// Empties the lookups, so they don't keep a parsed graph alive, and trims any that grew unusually large.
    /// </summary>
    public void Start()
    {
        // Sized for as many tables as the last parse on this thread read, so a large buffer doesn't grow the map
        // through several resizes, each rehashing every entry
        Tables.EnsureCapacity(_lastTableCount);
    }

    private int _lastTableCount;

    public void Reset()
    {
        var count = Tables.Count;
        _lastTableCount = count;
        Tables.Clear();
        if (count > MaxRetainedEntries)
        {
            Tables.TrimExcess(MaxRetainedEntries);
        }

        Strings.Reset();
    }
}

/// <summary>
/// Keeps one set of reader lookups per thread so repeated parsing reuses their storage instead of growing new
/// dictionaries every time.
/// </summary>
internal static class ReaderCache
{
    [ThreadStatic]
    private static ReaderLookups? _cached;

    public static ReaderLookups Rent()
    {
        var lookups = _cached ?? new ReaderLookups();
        _cached = null;
        lookups.Start();
        return lookups;
    }

    public static void Return(ReaderLookups lookups)
    {
        lookups.Reset();
        _cached = lookups;
    }
}
