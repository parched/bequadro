using Bequadro.Schema;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Bequadro.Generator;

/// <summary>
/// Reads the settings that a consuming project gives as MSBuild properties (CON-006). The package's props file makes
/// them visible to the compiler, which passes them to the generator as <c>build_property.*</c> global options.
/// </summary>
internal static class GeneratorOptions
{
    public const string RequireScalarsWithoutDefaultProperty = "BequadroRequireScalarsWithoutDefault";

    public static SchemaOptions From(AnalyzerConfigOptions globalOptions) =>
        new(RequireScalarsWithoutDefault: IsTrue(globalOptions, RequireScalarsWithoutDefaultProperty));

    /// <summary>
    /// Like an MSBuild condition such as <c>'$(Property)' == 'true'</c>, only <c>true</c>, in any case, is true.
    /// </summary>
    private static bool IsTrue(AnalyzerConfigOptions globalOptions, string property) =>
        globalOptions.TryGetValue("build_property." + property, out var value)
        && string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
