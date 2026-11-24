namespace Bequadro.Cli.Tests;

/// <summary>
/// SCH-010: a default of <c>-0.0</c> is kept, as flatc keeps it, so an absent field reads as negative zero in every target.
/// </summary>
public sealed class NegativeZeroDefaultTests : IDisposable
{
    private const string Schema = "table T { f: float = -0.0; d: double = -0; }";

    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public void TypeScript_NegativeZeroDefault_IsWritten()
    {
        var module = _cli.TypeScriptModule(Schema);

        module.Should().Contain("f: -0,").And.Contain("d: -0,");
    }

    [Fact]
    public void Python_NegativeZeroDefault_IsWritten()
    {
        _cli.Write("schema.fbs", Schema);

        var result = _cli.Run("generate", "--target", "python", "--out", "out", "schema.fbs");

        result.Code.Should().Be(ExitCode.Success, result.Error);
        var module = _cli.Read(Path.Combine("out", "bequadro_root.py"));
        module.Should().Contain("f: float = -0.0").And.Contain("d: float = -0.0");
    }
}
