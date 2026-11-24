namespace Bequadro.Schema.Common;

/// <summary>
/// Lexical path manipulation. Generators must not touch the file system, so paths are never resolved against disk.
/// </summary>
internal static class PathUtilities
{
    /// <summary>
    /// Normalizes separators to '/', and removes '.' and '..' segments where possible.
    /// </summary>
    public static string Normalize(string path)
    {
        var unified = path.Replace('\\', '/');
        var isRooted = unified.StartsWith("/", StringComparison.Ordinal);
        var segments = new List<string>();

        foreach (var segment in unified.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == ".." && segments.Count > 0 && segments[segments.Count - 1] != ".." && !IsDriveRoot(segments))
            {
                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        var joined = string.Join("/", segments);
        return isRooted ? "/" + joined : joined;
    }

    /// <summary>
    /// Resolves <paramref name="relativeOrAbsolute"/> against the directory containing <paramref name="filePath"/>.
    /// </summary>
    public static string ResolveRelativeTo(string filePath, string relativeOrAbsolute)
    {
        var target = relativeOrAbsolute.Replace('\\', '/');
        if (IsAbsolute(target))
        {
            return Normalize(target);
        }

        var normalizedFile = Normalize(filePath);
        var lastSlash = normalizedFile.LastIndexOf('/');
        var directory = lastSlash >= 0 ? normalizedFile.Substring(0, lastSlash + 1) : string.Empty;
        return Normalize(directory + target);
    }

    private static bool IsAbsolute(string path) =>
        path.StartsWith("/", StringComparison.Ordinal)
        || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

    private static bool IsDriveRoot(List<string> segments) =>
        segments.Count == 1 && segments[0].Length == 2 && segments[0][1] == ':';
}
