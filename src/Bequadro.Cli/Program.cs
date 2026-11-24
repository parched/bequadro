using System.Text;

namespace Bequadro.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Redirected output is UTF-8 on every platform, which is what the npm package and editors read (CLI-004). A
        // console keeps its own encoding: setting Console.OutputEncoding would change the code page of the console the
        // CLI shares with its parent on Windows.
        using var output = Console.IsOutputRedirected ? Utf8Writer(Console.OpenStandardOutput()) : null;
        using var error = Console.IsErrorRedirected ? Utf8Writer(Console.OpenStandardError()) : null;
        return (int)CommandLine.Run(args, output ?? Console.Out, error ?? Console.Error, Environment.CurrentDirectory);
    }

    private static StreamWriter Utf8Writer(Stream stream) =>
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
}
