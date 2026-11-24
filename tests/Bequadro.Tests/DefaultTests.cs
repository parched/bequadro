using Bequadro.Tests.Kitchen;

namespace Bequadro.Tests;

/// <summary>
/// SCH-010 and SER-004: a negative zero default is kept, and a value is compared with a default numerically, so 0 and
/// -0 are both equal to either default and aren't written.
/// </summary>
public class DefaultTests
{
    private static readonly byte[] AllDefaults = SignedZero.Serializer.WriteToArray(new SignedZero());

    [Fact]
    public void Construct_NegativeZeroDefault_IsNegativeZero()
    {
        var value = new SignedZero();

        double.IsNegative(value.Negative).Should().BeTrue();
        float.IsNegative(value.NegativeFloat).Should().BeTrue();
        double.IsNegative(value.Positive).Should().BeFalse();
    }

    [Fact]
    public void Parse_AbsentNegativeZeroDefault_ReadsNegativeZero()
    {
        var parsed = SignedZero.Serializer.Parse(Empty.Serializer.WriteToArray(new Empty()));

        double.IsNegative(parsed.Negative).Should().BeTrue();
        float.IsNegative(parsed.NegativeFloat).Should().BeTrue();
    }

    [Fact]
    public void Write_ZeroOfOtherSignThanDefault_IsNotWritten()
    {
        var bytes = SignedZero.Serializer.WriteToArray(
            new SignedZero
            {
                Negative = 0.0,
                NegativeFloat = 0f,
                Positive = -0.0,
            }
        );

        bytes.Should().Equal(AllDefaults);
        var parsed = SignedZero.Serializer.Parse(bytes);
        double.IsNegative(parsed.Negative).Should().BeTrue();
        double.IsNegative(parsed.Positive).Should().BeFalse();
    }

    [Fact]
    public void Write_NonZeroValue_IsWritten()
    {
        var parsed = SignedZero.Serializer.Parse(
            SignedZero.Serializer.WriteToArray(new SignedZero { Negative = 1.5, NegativeFloat = -2f })
        );

        (parsed.Negative, parsed.NegativeFloat).Should().Be((1.5, -2f));
    }
}
