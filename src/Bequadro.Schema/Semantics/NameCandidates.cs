namespace Bequadro.Schema.Semantics;

/// <summary>
/// The full names a type name written in a namespace can refer to (SCH-012): the name qualified by the namespace and by
/// each enclosing one, innermost first, and then the name itself. Instead of building every candidate, which takes time
/// and memory quadratic in the depth of the namespace, a lookup checks the declared names that end with the name.
/// </summary>
internal static class NameCandidates
{
    /// <summary>
    /// The item that is the innermost candidate, among those no further in than <paramref name="maxRank"/>, and its
    /// <see cref="Rank"/>. The first of several items with the same full name wins.
    /// </summary>
    /// <param name="items">The items to choose from, whose keys need only end with the name's last segment.</param>
    /// <param name="key">An item's full name.</param>
    /// <param name="currentNamespace">The namespace the name is written in.</param>
    /// <param name="name">The name as written, which may be qualified.</param>
    /// <param name="maxRank">The largest <see cref="Rank"/> to accept.</param>
    public static (T Item, int Rank)? Innermost<T>(
        IEnumerable<T> items,
        Func<T, string> key,
        string currentNamespace,
        string name,
        int maxRank = int.MaxValue
    )
    {
        (T Item, int Rank)? best = null;
        foreach (var item in items)
        {
            var rank = Rank(currentNamespace, name, key(item));
            if (rank > (best?.Rank ?? -1) && rank <= maxRank)
            {
                best = (item, rank);
            }
        }

        return best;
    }

    /// <summary>
    /// Where a full name is among the candidates: the length of the enclosing namespace it qualifies the name with, 0 if
    /// it is the name itself, or -1 if it isn't a candidate. The innermost candidate has the largest rank, and the rank
    /// of the name qualified by the whole namespace is the namespace's length.
    /// </summary>
    public static int Rank(string currentNamespace, string name, string fullName)
    {
        if (fullName.Length == name.Length)
        {
            return fullName == name ? 0 : -1;
        }

        var prefixLength = fullName.Length - name.Length - 1;
        var isCandidate =
            prefixLength > 0
            && prefixLength <= currentNamespace.Length
            && fullName[prefixLength] == '.'
            && (prefixLength == currentNamespace.Length || currentNamespace[prefixLength] == '.')
            && string.CompareOrdinal(fullName, prefixLength + 1, name, 0, name.Length) == 0
            && string.CompareOrdinal(fullName, 0, currentNamespace, 0, prefixLength) == 0;
        return isCandidate ? prefixLength : -1;
    }

    /// <summary>The last segment of a name, under which declarations are indexed.</summary>
    public static string LastSegment(string name) => name.Substring(name.LastIndexOf('.') + 1);
}
