using System.Globalization;
using Bequadro.Schema.Semantics;

namespace Bequadro.Generator.Tests;

/// <summary>
/// SCH-010's floating-point defaults. The generator also runs on .NET Framework, where <c>double.Parse</c> rejects a
/// number too large for a double and doesn't always round correctly, and "R" doesn't always round-trip. So
/// <see cref="Constants"/> converts without them, and these tests compare it with .NET's parsing, which is exact.
/// </summary>
public class ConstantsTests
{
    [Theory]
    [InlineData("1e400", double.PositiveInfinity)]
    [InlineData("-1e400", double.NegativeInfinity)]
    [InlineData("1e-400", 0d)]
    [InlineData("12", 12d)]
    [InlineData(".5", 0.5)]
    [InlineData("5.", 5d)]
    [InlineData("1.e2", 100d)]
    [InlineData("000.000", 0d)]
    [InlineData("1e999999999999", double.PositiveInfinity)]
    public void TryParseFloat_DecimalNumber_Parses(string text, double expected)
    {
        Constants.TryParseFloat(text, ScalarKind.Float64, out var value).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Theory]
    [InlineData("-0")]
    [InlineData("-0.0")]
    [InlineData("-1e-400")]
    public void TryParseFloat_NegativeZero_KeepsSign(string text)
    {
        Constants.TryParseFloat(text, ScalarKind.Float64, out var value).Should().BeTrue();
        Constants.IsNegativeZero(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("0x10")]
    [InlineData("1e")]
    [InlineData(".")]
    [InlineData("1.2.3")]
    [InlineData("+-3")]
    [InlineData("1,5")]
    [InlineData("∞")]
    public void TryParseFloat_OtherText_IsRejected(string text)
    {
        Constants.TryParseFloat(text, ScalarKind.Float64, out _).Should().BeFalse();
    }

    /// <summary>Halfway cases, both ends of the subnormal range and the edge of overflow, for both types.</summary>
    [Theory]
    [InlineData("9007199254740993")]
    [InlineData("9007199254740995")]
    [InlineData("16777217")]
    [InlineData("16777219")]
    [InlineData("0.1")]
    [InlineData("4.9406564584124654e-324")]
    [InlineData("2.4703282292062327e-324")]
    [InlineData("2.4703282292062328e-324")]
    [InlineData("2.2250738585072011e-308")]
    [InlineData("2.2250738585072014e-308")]
    [InlineData("1.7976931348623157e308")]
    [InlineData("1.7976931348623158e308")]
    [InlineData("1.7976931348623159e308")]
    [InlineData("1.40129846e-45")]
    [InlineData("7.006492321624085e-46")]
    [InlineData("7.006492321624086e-46")]
    [InlineData("1.17549435e-38")]
    [InlineData("3.4028234e38")]
    [InlineData("3.40282356779733661637539395458142568447e38")]
    [InlineData("3.40282356779733661637539395458142568448e38")]
    [InlineData("1.00000005960464477539062499")]
    [InlineData("1.00000005960464477539062500")]
    [InlineData("1.00000005960464477539062501")]
    [InlineData(
        "2.4703282292062327208828439643411068618252990130716238221279284125033775363510437593264991818081799618989828234772285886546332835517796989819938739800539093906315035659515570226392290858392449105184435931802849936536152500319370457678249219365623669863658480757001585769269903706311928279558551332927834338409351978015531246597263579574622766465272827220056374006485499977096599470454020828166226237857393450736339007967761930577506740176324673600968951340535537458516661134223766678604162159680461914467291840300530057530849048765391711386591646239524912623653881879636239373280423891018672348497668235089863388587925628302755995657524455507255189313690836254779186948667994968324049705821028513185451396213837722826145437693412532098591327667236328124"
    )]
    [InlineData(
        "2.4703282292062327208828439643411068618252990130716238221279284125033775363510437593264991818081799618989828234772285886546332835517796989819938739800539093906315035659515570226392290858392449105184435931802849936536152500319370457678249219365623669863658480757001585769269903706311928279558551332927834338409351978015531246597263579574622766465272827220056374006485499977096599470454020828166226237857393450736339007967761930577506740176324673600968951340535537458516661134223766678604162159680461914467291840300530057530849048765391711386591646239524912623653881879636239373280423891018672348497668235089863388587925628302755995657524455507255189313690836254779186948667994968324049705821028513185451396213837722826145437693412532098591327667236328125"
    )]
    [InlineData(
        "2.47032822920623272088284396434110686182529901307162382212792841250337753635104375932649918180817996189898282347722858865463328355177969898199387398005390939063150356595155702263922908583924491051844359318028499365361525003193704576782492193656236698636584807570015857692699037063119282795585513329278343384093519780155312465972635795746227664652728272200563740064854999770965994704540208281662262378573934507363390079677619305775067401763246736009689513405355374585166611342237666786041621596804619144672918403005300575308490487653917113865916462395249126236538818796362393732804238910186723484976682350898633885879256283027559956575244555072551893136908362547791869486679949683240497058210285131854513962138377228261454376934125320985913276672363281255"
    )]
    public void TryParseFloat_DecimalNumber_RoundsAsDotNetParse(string text)
    {
        AssertParsesAsDotNet(text);
    }

    /// <summary>Random numbers of up to 40 significant digits, across the whole range of both types.</summary>
    [Fact]
    public void TryParseFloat_RandomDecimalNumbers_RoundAsDotNetParse()
    {
        var random = new Random(20261117);
        for (var i = 0; i < 20_000; i++)
        {
            var digits = string.Concat(
                Enumerable.Range(0, random.Next(1, 41)).Select(_ => (char)('0' + random.Next(10)))
            );
            var point = random.Next(digits.Length + 1);
            var text = digits.Insert(point, ".") + FormattableString.Invariant($"e{random.Next(-360, 330)}");
            AssertParsesAsDotNet(text);
        }
    }

    [Theory]
    [InlineData(-0d, false, "-0d")]
    [InlineData(-0d, true, "-0f")]
    [InlineData(0.1, false, "0.1d")]
    [InlineData(0.1, true, "0.1f")]
    [InlineData(double.Epsilon, false, "5E-324d")]
    [InlineData(1e300, false, "1E+300d")]
    [InlineData(1.0000001192092896, true, "1.0000001f")]
    public void FormatFloat_Value_IsShortestRoundTrip(double value, bool isFloat, string expected)
    {
        Constants.FormatFloat(value, isFloat ? ScalarKind.Float32 : ScalarKind.Float64).Should().Be(expected);
    }

    private static void AssertParsesAsDotNet(string text)
    {
        Constants.TryParseFloat(text, ScalarKind.Float64, out var asDouble).Should().BeTrue(text);
        BitConverter
            .DoubleToInt64Bits(asDouble)
            .Should()
            .Be(BitConverter.DoubleToInt64Bits(double.Parse(text, CultureInfo.InvariantCulture)), text);

        Constants.TryParseFloat(text, ScalarKind.Float32, out var asSingle).Should().BeTrue(text);
        BitConverter
            .DoubleToInt64Bits(asSingle)
            .Should()
            .Be(BitConverter.DoubleToInt64Bits(float.Parse(text, CultureInfo.InvariantCulture)), text);
    }
}
