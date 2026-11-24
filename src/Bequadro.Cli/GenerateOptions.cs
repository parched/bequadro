using Bequadro.Schema;

namespace Bequadro.Cli;

/// <summary>
/// The options of <c>bequadro generate</c> (CLI-002).
/// </summary>
/// <param name="Target">The language to generate.</param>
/// <param name="Output">The output directory, as given.</param>
/// <param name="Inputs">The input files and directories, as given.</param>
/// <param name="Schema">Settings for the schema set.</param>
/// <param name="ImportExtension">TypeScript only: the extension of relative imports, <c>ts</c> or <c>js</c>.</param>
/// <param name="Check">Whether to compare with the output directory instead of writing it.</param>
internal sealed record GenerateOptions(
    Target Target,
    string Output,
    IReadOnlyList<string> Inputs,
    SchemaOptions Schema,
    string ImportExtension,
    bool Check
);
