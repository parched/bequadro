using Bequadro.Schema.Common;
using Bequadro.Schema.Naming;
using Bequadro.Schema.Semantics;

namespace Bequadro.Cli.TypeScript;

/// <summary>
/// Generates TypeScript: one module per namespace (TS-002), plus the runtime (CLI-006).
/// </summary>
internal static class TypeScriptTarget
{
    public const string RuntimeFile = "bequadro.ts";

    /// <summary>
    /// The module that exports the schema set's fingerprint (SER-015).
    /// </summary>
    public const string FingerprintFile = "bequadro_schema.ts";

    public static IReadOnlyList<GeneratedFile> Generate(
        EquatableArray<DeclarationModel> models,
        string? fingerprint,
        GenerateOptions options
    )
    {
        var renamed = ModelRenamer.Rename(models, TypeScriptNamingRules.Instance);
        var declarations = renamed.Items.ToDictionary(m => m.FullName, StringComparer.Ordinal);
        var files = new List<GeneratedFile> { new(RuntimeFile, Runtime.Read(RuntimeFile)) };
        if (fingerprint is not null)
        {
            files.Add(new GeneratedFile(FingerprintFile, FingerprintModule(fingerprint)));
        }

        foreach (var module in renamed.Items.GroupBy(m => m.Namespace, StringComparer.Ordinal))
        {
            var path = ModulePath(module.Key);
            var emitter = new TypeScriptModuleEmitter(path, module.Key, declarations, options);
            files.Add(new GeneratedFile(path, emitter.Emit(module.ToList())));
        }

        return files;
    }

    /// <summary>
    /// Typed <c>string</c>, not as its literal, so comparing it with another schema set's fingerprint type-checks.
    /// </summary>
    private static string FingerprintModule(string fingerprint)
    {
        var writer = new SourceWriter("  ");
        writer.Line("// " + OutputDirectory.GeneratedComment);
        writer.Line();
        writer.Line("/**");
        writer.Line(
            " * A fingerprint of the schema set this code was generated from. Code generated from the same schemas,"
        );
        writer.Line(
            " * ignoring white space and comments, has the same fingerprint in every language and on every platform. It"
        );
        writer.Line(" * may change between Bequadro versions.");
        writer.Line(" */");
        writer.Line($"export const fingerprint: string = \"{fingerprint}\";");
        return writer.ToString();
    }

    /// <summary>
    /// The module of a namespace: <c>a/b/c.ts</c> for <c>a.b.c</c>, and <c>index.ts</c> for the root namespace.
    /// </summary>
    public static string ModulePath(string ns) => ns.Length == 0 ? "index.ts" : ns.Replace('.', '/') + ".ts";

    /// <summary>
    /// The specifier for importing <paramref name="target"/> from <paramref name="from"/>, both relative to the output
    /// directory.
    /// </summary>
    public static string ImportSpecifier(string from, string target, string extension)
    {
        var fromDirectory = from.Split('/')[..^1];
        var targetParts = target.Split('/');
        var common = 0;
        while (
            common < fromDirectory.Length
            && common < targetParts.Length - 1
            && fromDirectory[common] == targetParts[common]
        )
        {
            common++;
        }

        var up = string.Concat(Enumerable.Repeat("../", fromDirectory.Length - common));
        var path = (up.Length == 0 ? "./" : up) + string.Join("/", targetParts[common..]);
        return path[..^".ts".Length] + "." + extension;
    }
}
