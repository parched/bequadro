using Bequadro.Cli.Python;
using Bequadro.Cli.TypeScript;

namespace Bequadro.Cli;

/// <summary>
/// Runs <c>bequadro generate</c>.
/// </summary>
internal static class Generator
{
    public static ExitCode Run(GenerateOptions options, TextWriter output, TextWriter error, string workingDirectory)
    {
        var schemaSet = SchemaSet.Load(options.Inputs, options.Schema, workingDirectory);
        var hasErrors = false;
        foreach (var diagnostic in schemaSet.FormatDiagnostics())
        {
            error.WriteLine(diagnostic);
            hasErrors = true;
        }

        // With errors, the output directory keeps the last good output (CLI-004)
        if (hasErrors)
        {
            return ExitCode.Failure;
        }

        var files = options.Target switch
        {
            Target.TypeScript => TypeScriptTarget.Generate(schemaSet.Bind.Declarations, schemaSet.Fingerprint, options),
            Target.Python => PythonTarget.Generate(schemaSet.Bind.Declarations, schemaSet.Fingerprint, options),
            _ => throw new UsageException($"Unknown target {options.Target}."),
        };

        var changes = OutputDirectory.Apply(
            Path.GetFullPath(options.Output, workingDirectory),
            options.Output,
            files,
            options.Check,
            output
        );
        return options.Check && changes > 0 ? ExitCode.Failure : ExitCode.Success;
    }
}
