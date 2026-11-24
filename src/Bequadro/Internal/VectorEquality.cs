using System.ComponentModel;

namespace Bequadro.Internal;

/// <summary>
/// Compares and hashes vectors element by element, for tables with value equality. Used by generated code; not
/// intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class VectorEquality
{
    /// <summary>
    /// Whether two vectors are both null, or have the same length and equal elements under
    /// <see cref="EqualityComparer{T}.Default"/>. The implementations don't matter, so an array equals a list.
    /// </summary>
    public static bool AreEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        // Parsing produces arrays, so this is the common case
        if (left is T[] leftArray && right is T[] rightArray)
        {
            return leftArray.AsSpan().SequenceEqual(rightArray, comparer: null);
        }

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < left.Count; i++)
        {
            if (!comparer.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A hash code consistent with <see cref="AreEqual{T}"/>, combining the length and every element.
    /// </summary>
    public static int Hash<T>(IReadOnlyList<T>? vector)
    {
        if (vector is null)
        {
            return 0;
        }

        var hash = new HashCode();
        hash.Add(vector.Count);
        if (vector is T[] array)
        {
            foreach (var item in array)
            {
                hash.Add(item);
            }
        }
        else
        {
            for (var i = 0; i < vector.Count; i++)
            {
                hash.Add(vector[i]);
            }
        }

        return hash.ToHashCode();
    }
}
