using Bequadro.Generator.Emit;
using Bequadro.Schema.Common;
using Bequadro.Schema.Naming;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Common;

internal static class CSharpModels
{
    /// <summary>
    /// Renames the bound models for C# (API-002), and gives each a generated file name that differs from every other
    /// ignoring case, and from the fingerprint's, as Roslyn requires.
    /// </summary>
    public static BindResult Prepare(BindResult bind)
    {
        var files = new NameScope();
        files.Fix(FingerprintEmitter.ClassName, isFile: true);
        var models = ModelRenamer
            .Rename(bind.Declarations, CSharpNamingRules.Instance)
            .Select(model => model is UnionModel union ? CSharpNamingRules.Instance.SeparateLabelGetters(union) : model)
            .Select(WithoutUnusedReferenceDetails)
            .Select(model =>
            {
                var fileName = files.Claim(model.FullName, _ => true, isFile: true);
                return fileName == model.FullName ? model : model with { FileName = fileName };
            })
            .ToEquatableArray();
        return bind with { Declarations = models };
    }

    /// <summary>
    /// Clears what C# emitters don't use from references to other types, so a change to it doesn't regenerate the
    /// declarations that refer to them (INC-001). C# compares a referenced table with its own <c>Equals</c>, so whether
    /// it has value equality doesn't matter.
    /// </summary>
    private static DeclarationModel WithoutUnusedReferenceDetails(DeclarationModel model) =>
        model switch
        {
            TableModel table => table with
            {
                Fields = table.Fields.Select(f => f with { Type = WithoutUnusedDetails(f.Type) }).ToEquatableArray(),
            },
            UnionModel union => union with
            {
                Members = union.Members.Select(m => m with { Type = WithoutUnusedDetails(m.Type) }).ToEquatableArray(),
            },
            _ => model,
        };

    private static TypeModel WithoutUnusedDetails(TypeModel type) =>
        type with
        {
            TableHasValueEquality = false,
            Element = type.Element is { } element ? WithoutUnusedDetails(element) : null,
        };
}
