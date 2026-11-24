namespace Bequadro.Cli;

/// <summary>
/// A file to write to the output directory.
/// </summary>
/// <param name="Path">The path relative to the output directory, with <c>/</c> separators.</param>
/// <param name="Content">The content, starting with the generated comment (CLI-005).</param>
internal sealed record GeneratedFile(string Path, string Content);
