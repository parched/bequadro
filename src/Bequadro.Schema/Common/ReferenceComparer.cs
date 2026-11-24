using System.Runtime.CompilerServices;

namespace Bequadro.Schema.Common;

/// <summary>
/// Compares by reference, for dictionaries keyed by records whose structural hash would cost the size of the record.
/// (<c>ReferenceEqualityComparer</c> isn't in netstandard2.0.)
/// </summary>
internal sealed class ReferenceComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static readonly ReferenceComparer<T> Instance = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
