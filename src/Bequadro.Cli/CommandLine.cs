using System.Reflection;
using Bequadro.Schema;

namespace Bequadro.Cli;

/// <summary>
/// Parses and runs a command line (CLI-002).
/// </summary>
internal static class CommandLine
{
    public const string Usage = """
        Usage: bequadro generate --target <typescript|python> --out <directory> [options] <input>...

        Generates TypeScript or Python from FlatBuffers schemas. Each input is a .fbs file, or a directory whose
        .fbs files are all used.

        Options:
          --target <typescript|python>         The language to generate.
          --out <directory>                    The output directory.
          --require-scalars-without-default    Scalar and enum fields without a default must be given when
                                               constructing.
          --import-extension <ts|js>           TypeScript only: the extension of relative imports. Default: ts.
          --check                              Compare with the output directory instead of writing it.
          --help                               Show this help.

        Other commands:
          bequadro --version                   Show the version.
        """;

    public static ExitCode Run(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        try
        {
            switch (args)
            {
                case ["--version"]:
                    output.WriteLine(Version);
                    return ExitCode.Success;
                case ["--version", var extra, ..]:
                    throw new UsageException($"Unexpected argument '{extra}' after --version.");
                case ["--help" or "-h", ..]:
                    output.WriteLine(Usage);
                    return ExitCode.Success;
                case ["generate", .. var rest] when AsksForHelp(rest):
                    output.WriteLine(Usage);
                    return ExitCode.Success;
                case ["generate", .. var rest]:
                    return Generator.Run(ParseGenerate(rest), output, error, workingDirectory);
                case []:
                    throw new UsageException("No command given.");
                default:
                    throw new UsageException($"Unknown command '{args[0]}'.");
            }
        }
        catch (UsageException exception)
        {
            error.WriteLine($"bequadro: {exception.Message}");
            if (args is not ([] or ["generate", ..]))
            {
                error.WriteLine("Run 'bequadro --help' for usage.");
            }

            return ExitCode.UsageOrIOError;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Every file operation reports its own errors with the path; this is a last resort, which can't rely on
            // exception messages (see FileErrors)
            error.WriteLine($"bequadro: {FileErrors.Reason(exception)}.");
            return ExitCode.UsageOrIOError;
        }
    }

    /// <summary>
    /// The version of the NuGet package built from the same source, without build metadata.
    /// </summary>
    public static string Version
    {
        get
        {
            var version = typeof(CommandLine)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? "0.0.0";
            var plus = version.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? version : version[..plus];
        }
    }

    /// <summary>
    /// The options that take a value.
    /// </summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--target",
        "--out",
        "--import-extension",
    };

    /// <summary>
    /// Whether <c>--help</c> or <c>-h</c> is among the arguments of <c>generate</c> as an option, not as a value.
    /// </summary>
    private static bool AsksForHelp(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                return true;
            }

            if (ValueOptions.Contains(args[i]))
            {
                i++;
            }
        }

        return false;
    }

    private static GenerateOptions ParseGenerate(string[] args)
    {
        Target? target = null;
        string? output = null;
        string? importExtension = null;
        var requireScalars = false;
        var check = false;
        var inputs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-') && args[i] != "-" && !seen.Add(args[i]))
            {
                throw new UsageException($"{args[i]} is given more than once.");
            }

            switch (args[i])
            {
                case "--target":
                    target = Value(args, ref i) switch
                    {
                        "typescript" => Target.TypeScript,
                        "python" => Target.Python,
                        var other => throw new UsageException(
                            $"Unknown target '{other}'; expected 'typescript' or 'python'."
                        ),
                    };
                    break;
                case "--out":
                    output = Value(args, ref i);
                    break;
                case "--import-extension":
                    importExtension = Value(args, ref i) switch
                    {
                        "ts" or "js" => args[i],
                        var other => throw new UsageException(
                            $"Unknown import extension '{other}'; expected 'ts' or 'js'."
                        ),
                    };
                    break;
                case "--require-scalars-without-default":
                    requireScalars = true;
                    break;
                case "--check":
                    check = true;
                    break;
                case var option when option.StartsWith('-') && option != "-":
                    throw new UsageException($"Unknown option '{option}'.");
                case "":
                    // As an unset variable gives: it must not mean the working directory
                    throw new UsageException("An input is empty.");
                default:
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (target is null)
        {
            throw new UsageException("Missing --target.");
        }

        if (output is null)
        {
            throw new UsageException("Missing --out.");
        }

        if (inputs.Count == 0)
        {
            throw new UsageException("No inputs given.");
        }

        if (importExtension is not null && target != Target.TypeScript)
        {
            throw new UsageException("--import-extension only applies to --target typescript.");
        }

        return new GenerateOptions(
            target.Value,
            output,
            inputs,
            new SchemaOptions(RequireScalarsWithoutDefault: requireScalars),
            importExtension ?? "ts",
            check
        );
    }

    private static string Value(string[] args, ref int i)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new UsageException($"Missing value for {args[i]}.");
        }

        if (args[i + 1].Length == 0)
        {
            // As an unset variable gives: an empty --out must not mean the working directory
            throw new UsageException($"Empty value for {args[i]}.");
        }

        return args[++i];
    }
}
