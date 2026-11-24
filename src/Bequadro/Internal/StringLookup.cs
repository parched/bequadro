using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Bequadro.Internal;

/// <summary>
/// Finds the string a parse already read from a buffer position, so each string in a buffer is decoded once.
/// </summary>
/// <remarks>
/// Most buffers never refer to one string twice, and a dictionary insert per string would slow parsing them
/// noticeably. So at first, positions are only marked in a bitmap, and the strings read are kept in a list. The
/// first time a marked position is read again, the list is indexed in a dictionary, which serves the rest of the
/// parse.
/// </remarks>
internal sealed class StringLookup
{
    private const int MaxRetainedEntries = 65536;

    private readonly Dictionary<int, string> _indexed = [];
    private readonly List<KeyValuePair<int, string>> _unindexed = [];
    private ulong[]? _marks;
    private int _bufferLength;
    private bool _isIndexed;

    /// <summary>
    /// Prepares for a parse of a buffer of <paramref name="bufferLength"/> bytes.
    /// </summary>
    public void Start(int bufferLength) => _bufferLength = bufferLength;

    /// <summary>
    /// Returns the string already read from <paramref name="position"/>, if there is one. Otherwise the caller
    /// reads it and passes it to <see cref="Add"/>.
    /// </summary>
    public bool TryGet(int position, [NotNullWhen(true)] out string? value)
    {
        if (!_isIndexed)
        {
            // One bit per 4 bytes, since strings in valid buffers are 4-byte aligned. When unaligned positions share
            // a bit, the dictionary is just built earlier than needed.
            var marks = _marks ??= RentMarks();
            ref var word = ref marks[position >> 8];
            var bit = 1UL << ((position >> 2) & 63);
            if ((word & bit) == 0)
            {
                word |= bit;
                value = null;
                return false;
            }

            Index();
        }

        return _indexed.TryGetValue(position, out value);
    }

    public void Add(int position, string value)
    {
        if (_isIndexed)
        {
            _indexed.Add(position, value);
        }
        else
        {
            _unindexed.Add(new(position, value));
        }
    }

    /// <summary>
    /// Empties the lookup, so it doesn't keep parsed strings alive, and trims storage that grew unusually large.
    /// </summary>
    public void Reset()
    {
        if (_marks is not null)
        {
            ArrayPool<ulong>.Shared.Return(_marks);
            _marks = null;
        }

        var indexedCount = _indexed.Count;
        _indexed.Clear();
        if (indexedCount > MaxRetainedEntries)
        {
            _indexed.TrimExcess(MaxRetainedEntries);
        }

        var unindexedCount = _unindexed.Count;
        _unindexed.Clear();
        if (unindexedCount > MaxRetainedEntries)
        {
            _unindexed.Capacity = MaxRetainedEntries;
        }

        _isIndexed = false;
    }

    private ulong[] RentMarks()
    {
        var length = (_bufferLength >> 8) + 1;
        var marks = ArrayPool<ulong>.Shared.Rent(length);
        Array.Clear(marks, 0, length);
        return marks;
    }

    private void Index()
    {
        foreach (var (position, value) in _unindexed)
        {
            _indexed.Add(position, value);
        }

        _unindexed.Clear();
        _isIndexed = true;
    }
}
