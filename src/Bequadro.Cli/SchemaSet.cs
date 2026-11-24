using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Bequadro.Schema;
using Bequadro.Schema.Diagnostics;
using Bequadro.Schema.Semantics;
using Bequadro.Schema.Syntax;

namespace Bequadro.Cli;

/// <summary>
/// The schema set the inputs make up (CLI-003), parsed and bound.
/// </summary>
internal sealed class SchemaSet
{
    /// <summary>
    /// Message formats that differ from the catalog's, which gives the source generator's advice.
    /// </summary>
    private static readonly Dictionary<string, string> MessageFormats = new(StringComparer.Ordinal)
    {
        [DiagnosticIds.IncludeNotFound] =
            "Included file '{0}' (resolved to '{1}') is not in the schema set; add it to the inputs",
    };

    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    private readonly Dictionary<string, string> _displayPaths;
    private readonly string _workingDirectory;

    private SchemaSet(
        BindResult bind,
        string? fingerprint,
        ImmutableArray<SchemaDiagnostic> diagnostics,
        Dictionary<string, string> paths,
        string workingDirectory
    )
    {
        Bind = bind;
        Fingerprint = fingerprint;
        Diagnostics = diagnostics;
        _displayPaths = paths;
        _workingDirectory = workingDirectory;
    }

    public BindResult Bind { get; }

    /// <summary>
    /// The schema set's fingerprint (SER-015), or null if the inputs contribute no file.
    /// </summary>
    public string? Fingerprint { get; }

    public ImmutableArray<SchemaDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Reads every file the inputs contribute and binds them as one schema set. Files are parsed under their full
    /// paths, so includes resolve however the inputs were written, and diagnostics show them as the inputs gave them.
    /// </summary>
    public static SchemaSet Load(IReadOnlyList<string> inputs, SchemaOptions options, string workingDirectory)
    {
        // Every path that reaches a file, by the file's real path, so a file reached through a symbolic link, or
        // through two inputs, is counted once
        var reached = new Dictionary<string, List<(string FullPath, string Display)>>(StringComparer.Ordinal);
        void Add(string fullPath, string display)
        {
            var real = FileErrors.Guard("read", display, () => RealPath(fullPath));
            if (!reached.TryGetValue(real, out var paths))
            {
                reached[real] = paths = [];
            }

            paths.Add((fullPath, display));
        }

        foreach (var input in inputs)
        {
            var fullPath = Path.GetFullPath(input, workingDirectory);
            if (Directory.Exists(fullPath))
            {
                var real = FileErrors.Guard("read", input, () => RealPath(fullPath));
                Walk(fullPath, input, [real], Add);
            }
            else if (File.Exists(fullPath))
            {
                Add(fullPath, input);
            }
            else
            {
                throw new UsageException($"Input '{input}' does not exist.");
            }
        }

        // Under its real path if an input reached it that way, otherwise under the first path in ordinal order, so
        // the choice doesn't depend on the order of the inputs
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (real, paths) in reached)
        {
            var fullPath = paths.Any(p => p.FullPath == real)
                ? real
                : paths.Select(p => p.FullPath).Min(StringComparer.Ordinal)!;
            files[fullPath] = paths
                .Where(p => p.FullPath == fullPath)
                .Select(p => p.Display)
                .Min(StringComparer.Ordinal)!;
        }

        var parsed = files
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => Parser.Parse(file.Key, Read(file.Key, file.Value)))
            .ToImmutableArray();
        var bind = Binder.Bind(parsed, options);
        var diagnostics = parsed.SelectMany(p => p.Diagnostics).Concat(bind.Diagnostics).ToImmutableArray();
        var fingerprint = parsed.IsEmpty ? null : SchemaFingerprint.Combine(parsed.Select(p => p.Digest));
        return new SchemaSet(bind, fingerprint, diagnostics, files, workingDirectory);
    }

    /// <summary>
    /// Adds every <c>.fbs</c> file under a directory input. Symbolic links are followed, except to a directory the walk
    /// is already inside, which would loop.
    /// </summary>
    private static void Walk(
        string directory,
        string display,
        ImmutableList<string> ancestors,
        Action<string, string> add
    )
    {
        var entries = FileErrors.Guard("read", display, () =>
            new DirectoryInfo(directory).EnumerateFileSystemInfos("*", AllEntries).ToList()
        );
        foreach (var entry in entries)
        {
            var entryDisplay = Path.Combine(display, entry.Name);
            if (Directory.Exists(entry.FullName))
            {
                var real = FileErrors.Guard("read", entryDisplay, () => RealPath(entry.FullName));
                if (!ancestors.Contains(real, StringComparer.Ordinal))
                {
                    Walk(entry.FullName, entryDisplay, ancestors.Add(real), add);
                }
            }
            else if (entry.Name.EndsWith(".fbs", StringComparison.OrdinalIgnoreCase) && File.Exists(entry.FullName))
            {
                add(entry.FullName, entryDisplay);
            }
        }
    }

    /// <summary>
    /// A full path with every symbolic link in it resolved.
    /// </summary>
    private static string RealPath(string fullPath, int depth = 0)
    {
        if (depth > 40)
        {
            throw new IOException("Too many levels of symbolic links.");
        }

        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (
            var part in fullPath[root.Length..]
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries
                )
        )
        {
            current = Path.Combine(current, part);
            var info = new FileInfo(current);
            if (info.LinkTarget is { } target)
            {
                current = RealPath(Path.GetFullPath(target, Path.GetDirectoryName(current)!), depth + 1);
            }
        }

        return current;
    }

    /// <summary>
    /// Reads a file as UTF-8, whatever byte order mark it starts with, ignoring a UTF-8 one (CLI-003).
    /// </summary>
    private static string Read(string path, string display) => FileErrors.Guard("read", display, () =>
        {
            // Editors may write or replace a schema while a watcher-triggered run is reading it.
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            return reader.ReadToEnd();
        });

    /// <summary>
    /// The diagnostics as <c>{path}:{line}:{column}: error {id}: {message}</c>, sorted by path, then by position (CLI-004).
    /// Paths in messages are shown as the inputs gave them too.
    /// </summary>
    public IEnumerable<string> FormatDiagnostics() =>
        Diagnostics
            .Select(d => (Path: DisplayPath(d.Span.Path), Diagnostic: d))
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Diagnostic.Span.Start)
            .Select(d =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{d.Path}:{d.Diagnostic.Span.StartLine + 1}:{d.Diagnostic.Span.StartColumn + 1}: error {d.Diagnostic.Id}: {Message(d.Diagnostic, d.Path)}"
                )
            );

    private string Message(SchemaDiagnostic diagnostic, string displayPath)
    {
        var arguments = diagnostic.Arguments.Items.Select(DisplayArgument).ToArray<object>();
        if (diagnostic.Id == DiagnosticIds.IncludeNotFound && arguments.Length == 2)
        {
            // The resolved path of a file that isn't in the set, shown relative to the working directory if the
            // including file's input was relative
            var resolved = Path.GetFullPath((string)arguments[1]);
            arguments[1] = Path.IsPathRooted(displayPath)
                ? resolved
                : Path.GetRelativePath(_workingDirectory, resolved);
        }

        var format = MessageFormats.TryGetValue(diagnostic.Id, out var own)
            ? own
            : DiagnosticCatalog.Get(diagnostic.Id).MessageFormat;
        return string.Format(CultureInfo.InvariantCulture, format, arguments);
    }

    /// <summary>
    /// An argument with a schema file's full path, alone or followed by a position such as <c>(2,7)</c>, replaced by
    /// the path as the inputs gave it.
    /// </summary>
    private string DisplayArgument(string argument)
    {
        if (_displayPaths.TryGetValue(argument, out var display))
        {
            return display;
        }

        var position = argument.LastIndexOf('(');
        return position > 0 && _displayPaths.TryGetValue(argument[..position], out display)
            ? display + argument[position..]
            : argument;
    }

    private string DisplayPath(string path) => _displayPaths.TryGetValue(path, out var display) ? display : path;
}
