using Bequadro.Schema.Common;
using Bequadro.Schema.Naming;
using Bequadro.Schema.Semantics;

namespace Bequadro.Cli.Python;

/// <summary>
/// Generates Python: one package per namespace (PY-002), plus the runtime (CLI-006).
/// </summary>
internal static class PythonTarget
{
    public const string RuntimeFile = "bequadro.py";

    /// <summary>
    /// The module of the root namespace's declarations, which the output package's <c>__init__.py</c> re-exports. A
    /// package can't import its own <c>__init__</c> relatively, so the declarations need a module of their own.
    /// </summary>
    public const string RootModule = "bequadro_root";

    /// <summary>
    /// The module that holds the schema set's fingerprint (SER-015).
    /// </summary>
    public const string FingerprintFile = "bequadro_schema.py";

    public static IReadOnlyList<GeneratedFile> Generate(EquatableArray<DeclarationModel> models, string? fingerprint)
    {
        var renamed = ModelRenamer.Rename(models, PythonNamingRules.Instance);
        var declarations = renamed.Items.ToDictionary(m => m.FullName, StringComparer.Ordinal);
        var files = new List<GeneratedFile> { new(RuntimeFile, Runtime.Read(RuntimeFile)) };
        if (fingerprint is not null)
        {
            files.Add(new GeneratedFile(FingerprintFile, FingerprintModule(fingerprint)));
        }

        var modules = renamed.Items.GroupBy(m => m.Namespace, StringComparer.Ordinal).ToList();

        var root = modules.FirstOrDefault(m => m.Key.Length == 0)?.ToList() ?? [];
        files.Add(new GeneratedFile("__init__.py", RootInit(root)));
        foreach (var module in modules)
        {
            var emitter = new PythonModuleEmitter(module.Key, declarations);
            files.Add(new GeneratedFile(ModulePath(module.Key), emitter.Emit(module.ToList())));
        }

        // Every namespace on the way to one with declarations is a package too
        var packages = modules
            .Where(m => m.Key.Length > 0)
            .SelectMany(m => Prefixes(m.Key))
            .Distinct(StringComparer.Ordinal)
            .Where(ns => !modules.Any(m => m.Key == ns));
        files.AddRange(
            packages.Select(ns => new GeneratedFile(ModulePath(ns), Header + $"\"\"\"Namespace {ns}.\"\"\"\n"))
        );
        return files;
    }

    public const string Header = "# " + OutputDirectory.GeneratedComment + "\n";

    /// <summary>
    /// Schema names may start with an underscore, which makes them private to pyright, but generated code has to use
    /// them across classes and modules, and a private enum may be used nowhere in its module (PY-001).
    /// </summary>
    public const string PyrightDirective = "# pyright: reportPrivateUsage=false, reportUnusedClass=false";

    /// <summary>
    /// The module of a namespace: <c>a/b/c/__init__.py</c> for <c>a.b.c</c>, and <c>bequadro_root.py</c> for the root
    /// namespace.
    /// </summary>
    public static string ModulePath(string ns) =>
        ns.Length == 0 ? RootModule + ".py" : ns.Replace('.', '/') + "/__init__.py";

    /// <summary>
    /// Typed <c>Final[str]</c>, not as its literal, so comparing it with another schema set's fingerprint type-checks.
    /// </summary>
    private static string FingerprintModule(string fingerprint)
    {
        var writer = new SourceWriter("    ");
        writer.Line(Header.TrimEnd('\n'));
        writer.Line("\"\"\"The schema set this package was generated from.\"\"\"");
        writer.Line();
        writer.Line("from typing import Final");
        writer.Line();
        writer.Line($"FINGERPRINT: Final[str] = \"{fingerprint}\"");
        writer.Line("\"\"\"A fingerprint of the schema set this code was generated from.");
        writer.Line();
        writer.Line(
            "Code generated from the same schemas, ignoring white space and comments, has the same fingerprint in every"
        );
        writer.Line("language and on every platform. It may change between Bequadro versions.");
        writer.Line("\"\"\"");
        return writer.ToString();
    }

    private static IEnumerable<string> Prefixes(string ns)
    {
        var segments = ns.Split('.');
        for (var i = 1; i <= segments.Length; i++)
        {
            yield return string.Join(".", segments[..i]);
        }
    }

    /// <summary>
    /// The output package's <c>__init__.py</c>, which re-exports the root namespace's declarations.
    /// </summary>
    private static string RootInit(IReadOnlyList<DeclarationModel> root)
    {
        var writer = new SourceWriter("    ");
        writer.Line(Header.TrimEnd('\n'));
        writer.Line(PyrightDirective);
        writer.Line("\"\"\"The root namespace.\"\"\"");
        if (root.Count > 0)
        {
            writer.Line();
            foreach (var model in root)
            {
                writer.Line($"from .{RootModule} import {model.Name} as {model.Name}");
            }
        }

        return writer.ToString();
    }
}
