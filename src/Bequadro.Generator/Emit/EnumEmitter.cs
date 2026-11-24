using Bequadro.Generator.Common;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Emit;

internal static class EnumEmitter
{
    public static void Emit(CodeWriter writer, EnumModel model)
    {
        writer.OpenBlock(
            $"public enum {Naming.EscapeTypeName(model.Name)} : {ScalarInfo.CSharpName(model.Underlying)}"
        );
        foreach (var member in model.Members)
        {
            writer.DocComment(member.Documentation, member.RenamedFrom);
            writer.Line($"{Naming.Escape(member.Name)} = {Constants.FormatInteger(member.Value, model.Underlying)},");
        }

        writer.CloseBlock();
    }
}
