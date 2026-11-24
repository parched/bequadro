using System.Text;

namespace Bequadro.Cli;

/// <summary>
/// The runtime files embedded in the CLI, which every run writes with its output (CLI-006).
/// </summary>
internal static class Runtime
{
    public static string Read(string name)
    {
        using var stream = typeof(Runtime).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The runtime file '{name}' is missing from the CLI.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
