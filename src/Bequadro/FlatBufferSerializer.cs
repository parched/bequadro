using System.Buffers;
using Bequadro.Internal;

namespace Bequadro;

/// <summary>
/// Serializes and parses FlatBuffers whose root is the table <typeparamref name="T"/>.
/// Each generated table exposes an instance as <c>T.Serializer</c>. All members are thread-safe.
/// </summary>
public sealed class FlatBufferSerializer<T>
    where T : class, IFlatBufferTable<T>
{
    // The size of the most recent buffer, used as the starting capacity of the next write so large
    // buffers are rented at the right size instead of grown by repeated doubling.
    private static int _lastSize;

    private FlatBufferSerializer() { }

    public static FlatBufferSerializer<T> Instance { get; } = new();

    /// <summary>
    /// Parses a buffer into a new, fully materialized and mutable object graph.
    /// Each table in the buffer becomes exactly one instance, so shared references are preserved.
    /// </summary>
    /// <param name="buffer">The buffer to parse.</param>
    /// <param name="shareStrings">
    /// Whether every reference to the same string in the buffer yields one <see cref="string"/> instance. Use it for
    /// buffers written with <c>shareStrings</c>, so a string referenced many times is decoded once. When false, each
    /// reference gets its own copy.
    /// </param>
    /// <exception cref="InvalidDataException">The buffer is malformed.</exception>
    public T Parse(ReadOnlySpan<byte> buffer, bool shareStrings = false)
    {
        ThrowHelper.EnsureLittleEndian();
        var lookups = ReaderCache.Rent();
        try
        {
            var reader = new FlatBufferReader(buffer, lookups, shareStrings);
            return reader.ReadRoot<T>();
        }
        finally
        {
            ReaderCache.Return(lookups);
        }
    }

    /// <inheritdoc cref="Parse(ReadOnlySpan{byte}, bool)"/>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
    public T Parse(byte[] buffer, bool shareStrings = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Parse(buffer.AsSpan(), shareStrings);
    }

    /// <inheritdoc cref="Parse(ReadOnlySpan{byte}, bool)"/>
    public T Parse(ReadOnlyMemory<byte> buffer, bool shareStrings = false) => Parse(buffer.Span, shareStrings);

    /// <summary>
    /// Serializes <paramref name="value"/> to a new array of exactly the buffer's length.
    /// Each table instance is written once, however many times it is referenced.
    /// </summary>
    /// <param name="value">The root table.</param>
    /// <param name="shareStrings">
    /// Whether equal strings are written once, with every reference pointing at that copy. This makes buffers with
    /// repeated strings smaller, at the cost of a lookup per string. When false, each reference gets its own copy.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A required field is null, a union has no value, the graph contains a reference cycle, or tables are nested
    /// more than 64 levels deep.
    /// </exception>
    public byte[] WriteToArray(T value, bool shareStrings = false) =>
        Write(value, shareStrings, 0, static (span, _) => span.ToArray());

    /// <inheritdoc cref="WriteToArray"/>
    public ReadOnlyMemory<byte> WriteToMemory(T value, bool shareStrings = false) => WriteToArray(value, shareStrings);

    /// <summary>
    /// Serializes <paramref name="value"/> and appends the buffer to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writer to append the buffer to.</param>
    /// <param name="value">The root table.</param>
    /// <param name="shareStrings">
    /// Whether equal strings are written once, with every reference pointing at that copy. This makes buffers with
    /// repeated strings smaller, at the cost of a lookup per string. When false, each reference gets its own copy.
    /// </param>
    /// <inheritdoc cref="WriteToArray" path="/exception"/>
    public void Write(IBufferWriter<byte> destination, T value, bool shareStrings = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Write(value, shareStrings, destination, static (span, target) =>
        {
            target.Write(span);
            return 0;
        });
    }

    // The state parameter lets callers pass static lambdas, so no closure is allocated per write
    private static TResult Write<TState, TResult>(
        T value,
        bool shareStrings,
        TState state,
        ReadOnlySpanFunc<TState, TResult> consume
    )
    {
        ArgumentNullException.ThrowIfNull(value);
        ThrowHelper.EnsureLittleEndian();
        var writer = WriterCache.Rent(Volatile.Read(ref _lastSize), shareStrings);
        try
        {
            var root = writer.WriteTable(value);
            var buffer = writer.Finish(root);
            Volatile.Write(ref _lastSize, buffer.Length);
            return consume(buffer, state);
        }
        finally
        {
            WriterCache.Return(writer);
        }
    }

    private delegate TResult ReadOnlySpanFunc<in TState, out TResult>(ReadOnlySpan<byte> span, TState state);
}
