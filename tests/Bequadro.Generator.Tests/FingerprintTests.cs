using System.Security.Cryptography;
using Bequadro.Generator.Emit;
using Bequadro.Schema;
using Bequadro.Schema.Common;
using Bequadro.Schema.Syntax;
using Microsoft.CodeAnalysis;

namespace Bequadro.Generator.Tests;

/// <summary>
/// SER-015: the schema set's fingerprint, and the C# class that holds it.
/// </summary>
public class FingerprintTests
{
    private const string Schema = """
        include "common.fbs";
        attribute "note";
        namespace Shapes;

        /// A shape.
        table Shape (bequadro_value_equality) {
          name: string (note: "a\tb");
          sides: int = 4;
          scale: float = 1.5;
          common: Common.Thing;
        }

        root_type Shape;
        """;

    private const string Common = "namespace Common; table Thing { x: int; }";

    [Fact]
    public void Sha256_AnyLength_MatchesDotNet()
    {
        var random = new Random(15);
        for (var length = 0; length < 300; length++)
        {
            var data = new byte[length];
            random.NextBytes(data);

            Sha256.Hash(data).Should().Equal(SHA256.HashData(data), $"for {length} bytes");
        }
    }

    [Fact]
    public void Combine_AnySchemaSet_Is32LowercaseHexDigits()
    {
        Fingerprint(("shape.fbs", Schema), ("common.fbs", Common)).Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Theory]
    [InlineData("windows line endings")]
    [InlineData("byte order mark")]
    [InlineData("white space")]
    [InlineData("comments")]
    [InlineData("string escapes")]
    [InlineData("paths")]
    [InlineData("file order")]
    public void Combine_ChangeThatKeepsTheTokens_KeepsTheFingerprint(string change)
    {
        var shape = ("shape.fbs", Schema);
        var common = ("common.fbs", Common);
        (string, string)[] changed = change switch
        {
            "windows line endings" => [("shape.fbs", Schema.Replace("\n", "\r\n")), common],
            "byte order mark" => [("shape.fbs", "\uFEFF" + Schema), common],
            "white space" => [("shape.fbs", Schema.Replace("\n", "\n\t \n").Replace(": ", " :  ")), common],
            "comments" =>
            [
                (
                    "shape.fbs",
                    "// Shapes\n"
                        + Schema
                            .Replace("/// A shape.", "/// Changed.\n/* block */")
                            .Replace("int = 4;", "int = 4; // four")
                ),
                common,
            ],
            "string escapes" => [("shape.fbs", Schema.Replace("\"a\\tb\"", "\"\\u0061\\u0009b\"")), common],
            "paths" => [(@"C:\Schemas\shape.fbs", Schema), (@"C:\Schemas\common.fbs", Common)],
            "file order" => [common, shape],
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        Fingerprint(changed).Should().Be(Fingerprint(shape, common));
    }

    [Theory]
    [InlineData("int = 4;", "int = 5;")]
    [InlineData("sides: int", "corners: int")]
    [InlineData("sides: int = 4;\n  scale: float = 1.5;", "scale: float = 1.5;\n  sides: int = 4;")]
    [InlineData("(bequadro_value_equality)", "")]
    [InlineData("\"a\\tb\"", "\"a b\"")]
    [InlineData("root_type Shape;", "")]
    public void Combine_ChangedToken_ChangesTheFingerprint(string from, string to)
    {
        Schema.Should().Contain(from);

        Fingerprint(("shape.fbs", Schema.Replace(from, to)), ("common.fbs", Common))
            .Should()
            .NotBe(Fingerprint(("shape.fbs", Schema), ("common.fbs", Common)));
    }

    [Fact]
    public void Combine_AddedFile_ChangesTheFingerprint()
    {
        Fingerprint(("shape.fbs", Schema), ("common.fbs", Common), ("other.fbs", "table Other {}"))
            .Should()
            .NotBe(Fingerprint(("shape.fbs", Schema), ("common.fbs", Common)));
    }

    [Fact]
    public void Generate_SchemaSet_EmitsTheFingerprintAsAConstant()
    {
        var result = GeneratorHarness.Run(("shape.fbs", Schema), ("common.fbs", Common));

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        var type = result.Output.GetTypeByMetadataName(FingerprintEmitter.ClassName)!;
        type.DeclaredAccessibility.Should().Be(Accessibility.Public);
        type.IsStatic.Should().BeTrue();
        type.ContainingNamespace.IsGlobalNamespace.Should().BeTrue();
        var field = type.GetMembers("Fingerprint").OfType<IFieldSymbol>().Single();
        field.DeclaredAccessibility.Should().Be(Accessibility.Public);
        field.HasConstantValue.Should().BeTrue();
        field.ConstantValue.Should().Be(Fingerprint(("shape.fbs", Schema), ("common.fbs", Common)));
    }

    [Fact]
    public void Generate_SchemaSetWithErrors_StillEmitsTheFingerprint()
    {
        var result = GeneratorHarness.Run(("schema.fbs", "table T { x: Missing; }"));

        result.GeneratorDiagnostics.Should().ContainSingle();
        result.CompilationDiagnostics.Should().BeEmpty();
        result
            .Source(FingerprintEmitter.HintName)
            .Should()
            .Contain(Fingerprint(("schema.fbs", "table T { x: Missing; }")));
    }

    /// <summary>
    /// API-002: the fingerprint's file name is taken first, ignoring case, so a declaration's file is renamed instead.
    /// </summary>
    [Fact]
    public void Generate_DeclarationWithTheFingerprintsFileNameIgnoringCase_GetsAnotherFileName()
    {
        var result = GeneratorHarness.Run(("schema.fbs", "table bequadroschema { x: int; }"));

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationDiagnostics.Should().BeEmpty();
        result.HintNames.Should().BeEquivalentTo("bequadroschema_.g.cs", FingerprintEmitter.HintName);
        result.Output.GetTypeByMetadataName("bequadroschema").Should().NotBeNull();
    }

    private static string Fingerprint(params (string Path, string Text)[] files) =>
        SchemaFingerprint.Combine(files.Select(f => Parser.Parse(f.Path, f.Text).Digest));
}
