namespace Bequadro.Schema.Naming;

/// <summary>
/// The names claimed in one scope, such as the members of a type (NAM-002). Names claimed as files are also compared
/// ignoring case with each other, because file systems may not tell them apart.
/// </summary>
internal sealed class NameScope
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Takes a name that is never renamed, such as one of the target's fixed API.
    /// </summary>
    public void Fix(string name, bool isFile = false)
    {
        _names.Add(name);
        if (isFile)
        {
            _files.Add(name);
        }
    }

    /// <summary>
    /// Claims the name for a candidate: with a leading <c>_</c> if it starts with a digit, then with trailing <c>_</c>s
    /// until it is both usable and free.
    /// </summary>
    public string Claim(string candidate, Func<string, bool> isUsable, bool isFile = false)
    {
        var name = candidate.Length > 0 && candidate[0] is >= '0' and <= '9' ? "_" + candidate : candidate;
        while (!isUsable(name) || _names.Contains(name) || (isFile && _files.Contains(name)))
        {
            name += "_";
        }

        Fix(name, isFile);
        return name;
    }
}
