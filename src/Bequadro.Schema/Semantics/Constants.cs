using System.Globalization;
using System.Numerics;

namespace Bequadro.Schema.Semantics;

/// <summary>
/// Parsing and formatting of schema constants. Nothing here relies on behavior that differs between .NET and
/// .NET Framework, because the source generator also runs in Visual Studio's .NET Framework compiler host. There,
/// <c>double.Parse</c> rejects a number too large for a <see cref="double"/> and doesn't always round correctly, and
/// <c>"R"</c> doesn't always round-trip or keep the sign of negative zero.
/// </summary>
internal static class Constants
{
    /// <summary>
    /// The most significant digits of a decimal number that are converted exactly. Converting to the nearest double
    /// never needs more than 768; the rest only decide whether the number is above a halfway point.
    /// </summary>
    private const int MaxSignificantDigits = 800;

    private static readonly BinaryFormat DoubleFormat = new(Precision: 53, MinExponent: -1074, MaxExponent: 1024);

    private static readonly BinaryFormat SingleFormat = new(Precision: 24, MinExponent: -149, MaxExponent: 128);

    /// <summary>
    /// Parses a decimal or hexadecimal integer with an optional sign. Decimal is used because it represents the
    /// full range of both <see cref="long"/> and <see cref="ulong"/> exactly.
    /// </summary>
    public static bool TryParseInteger(string text, out decimal value)
    {
        value = 0;
        var (negative, digits) = SplitSign(text);
        ulong magnitude;
        var parsed = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(
                digits.Substring(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out magnitude
            )
            : ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out magnitude);

        if (!parsed)
        {
            return false;
        }

        value = negative ? -(decimal)magnitude : magnitude;
        return true;
    }

    public static bool IsInRange(decimal value, ScalarKind kind)
    {
        var (min, max) = ScalarInfo.IntegerRange(kind);
        return value >= min && value <= max;
    }

    /// <summary>
    /// Whether an identifier is a floating-point constant: <c>nan</c>, <c>inf</c> or <c>infinity</c>, in any case. flatc
    /// converts these with <c>strtod</c>, which ignores case.
    /// </summary>
    public static bool IsFloatConstantName(string text) =>
        text.Equals("nan", StringComparison.OrdinalIgnoreCase)
        || text.Equals("inf", StringComparison.OrdinalIgnoreCase)
        || text.Equals("infinity", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a floating-point constant with an optional sign, as flatc does with <c>strtod</c> or <c>strtof</c>: a
    /// decimal integer, a decimal number with an optional fraction and exponent, or a name from
    /// <see cref="IsFloatConstantName"/>. A hexadecimal integer isn't one, because flatc rejects it as the default of a
    /// floating-point field. The number is rounded once, to the nearest value of the given type, ties to even: one too
    /// large for the type is infinity with its sign, and one too small is zero with its sign.
    /// </summary>
    public static bool TryParseFloat(string text, ScalarKind kind, out double value)
    {
        value = 0;
        var (negative, unsigned) = SplitSign(text);
        if (IsFloatConstantName(unsigned))
        {
            value =
                unsigned.Equals("nan", StringComparison.OrdinalIgnoreCase) ? double.NaN
                : negative ? double.NegativeInfinity
                : double.PositiveInfinity;
            return true;
        }

        if (!TryParseDecimal(unsigned, out var significand, out var digitCount, out var exponent))
        {
            return false;
        }

        value = ToBinary(significand, digitCount, exponent, kind == ScalarKind.Float32 ? SingleFormat : DoubleFormat);
        value = negative ? -value : value;
        return true;
    }

    /// <summary>
    /// Rounds a floating-point value to the precision of the given type: a value too large for a <c>float</c> becomes
    /// infinity, and one too small becomes zero with its sign.
    /// </summary>
    public static double ToPrecision(double value, ScalarKind kind) =>
        kind == ScalarKind.Float32 ? (float)value : value;

    /// <summary>Whether a value is zero with a negative sign, which compares equal to zero.</summary>
    public static bool IsNegativeZero(double value) => value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;

    /// <summary>
    /// Formats a floating-point value as a C# expression of the given type.
    /// </summary>
    public static string FormatFloat(double value, ScalarKind kind)
    {
        value = ToPrecision(value, kind);
        var typeName = ScalarInfo.CSharpName(kind);
        if (double.IsNaN(value))
        {
            return typeName + ".NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return typeName + ".PositiveInfinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return typeName + ".NegativeInfinity";
        }

        var suffix = kind == ScalarKind.Float32 ? "f" : "d";
        if (value == 0)
        {
            // .NET Framework formats negative zero without its sign
            return (IsNegativeZero(value) ? "-0" : "0") + suffix;
        }

        return FormatRoundTrip(value, kind) + suffix;
    }

    public static string FormatInteger(decimal value, ScalarKind kind) =>
        kind == ScalarKind.Int64 && value == long.MinValue
            ? "long.MinValue"
            : value.ToString(CultureInfo.InvariantCulture)
                + (kind == ScalarKind.UInt64 && value > long.MaxValue ? "UL" : "");

    /// <summary>
    /// The shortest text that parses back to the value, which is what "R" gives on .NET. On .NET Framework "R" doesn't
    /// always round-trip, so the text is checked with <see cref="TryParseFloat"/>, and 17 (or 9) significant digits,
    /// which always round-trip, are used if it doesn't.
    /// </summary>
    private static string FormatRoundTrip(double value, ScalarKind kind)
    {
        var isSingle = kind == ScalarKind.Float32;
        var text = isSingle
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
        if (TryParseFloat(text, kind, out var parsed) && parsed.Equals(value))
        {
            return text;
        }

        return isSingle
            ? ((float)value).ToString("G9", CultureInfo.InvariantCulture)
            : value.ToString("G17", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Splits off one leading sign. A value's text has at most one, because the parser only accepts a sign directly
    /// before a number.
    /// </summary>
    private static (bool Negative, string Unsigned) SplitSign(string text) =>
        text.Length > 0 && text[0] is '+' or '-' ? (text[0] == '-', text.Substring(1)) : (false, text);

    /// <summary>
    /// Parses <c>digits[.digits][e[+-]digits]</c> or <c>.digits[e[+-]digits]</c>, with digits on at least one side of the
    /// point, as the lexer reads them, into <paramref name="significand"/> × 10^<paramref name="exponent"/>. Only the
    /// first <see cref="MaxSignificantDigits"/> significant digits are kept, followed by a 1 if any of the others isn't
    /// zero, which rounds the same.
    /// </summary>
    private static bool TryParseDecimal(string text, out BigInteger significand, out int digitCount, out long exponent)
    {
        significand = BigInteger.Zero;
        digitCount = 0;
        exponent = 0;
        var digits = new System.Text.StringBuilder();
        var sticky = false;
        var mantissaDigits = 0;
        var position = 0;
        var afterPoint = false;
        for (; position < text.Length; position++)
        {
            var c = text[position];
            if (c == '.' && !afterPoint)
            {
                afterPoint = true;
                continue;
            }

            if (c is < '0' or > '9')
            {
                break;
            }

            mantissaDigits++;
            if (digits.Length == 0 && c == '0')
            {
                // A leading zero only moves the point
                exponent -= afterPoint ? 1 : 0;
                continue;
            }

            if (digits.Length < MaxSignificantDigits)
            {
                digits.Append(c);
                exponent -= afterPoint ? 1 : 0;
            }
            else
            {
                sticky |= c != '0';
                exponent += afterPoint ? 0 : 1;
            }
        }

        if (mantissaDigits == 0)
        {
            return false;
        }

        if (position < text.Length && text[position] is 'e' or 'E')
        {
            position++;
            var exponentNegative = position < text.Length && text[position] == '-';
            if (position < text.Length && text[position] is '+' or '-')
            {
                position++;
            }

            var exponentStart = position;
            long written = 0;
            for (; position < text.Length && text[position] is >= '0' and <= '9'; position++)
            {
                // Saturated: anything this large is already infinity or zero
                written = Math.Min(written * 10 + (text[position] - '0'), 1_000_000_000);
            }

            if (position == exponentStart)
            {
                return false;
            }

            exponent += exponentNegative ? -written : written;
        }

        if (position != text.Length)
        {
            return false;
        }

        if (sticky)
        {
            digits.Append('1');
            exponent--;
        }

        digitCount = digits.Length;
        significand =
            digitCount == 0 ? BigInteger.Zero : BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// The value of <paramref name="significand"/> × 10^<paramref name="exponent"/> rounded once to the nearest value of
    /// a binary format, ties to even, computed exactly with integers.
    /// </summary>
    private static double ToBinary(BigInteger significand, int digitCount, long exponent, BinaryFormat format)
    {
        if (significand.IsZero || digitCount + exponent < -400)
        {
            return 0;
        }

        if (digitCount + exponent > 400)
        {
            return double.PositiveInfinity;
        }

        // The value is numerator / denominator
        var numerator = exponent >= 0 ? significand * BigInteger.Pow(10, (int)exponent) : significand;
        var denominator = exponent >= 0 ? BigInteger.One : BigInteger.Pow(10, (int)-exponent);

        // Choose e so that the quotient q = value / 2^e has Precision bits, or fewer if the value is subnormal
        var e = Math.Max(BitLength(numerator) - BitLength(denominator) - format.Precision, format.MinExponent);
        var (quotient, remainder, divisor) = Divide(numerator, denominator, e);
        if (BitLength(quotient) > format.Precision)
        {
            e++;
            (quotient, remainder, divisor) = Divide(numerator, denominator, e);
        }

        var comparison = (remainder << 1).CompareTo(divisor);
        if (comparison > 0 || (comparison == 0 && !quotient.IsEven))
        {
            quotient++;
            if (BitLength(quotient) > format.Precision)
            {
                quotient >>= 1;
                e++;
            }
        }

        if (e + format.Precision > format.MaxExponent)
        {
            return double.PositiveInfinity;
        }

        return Compose(quotient, e);
    }

    /// <summary>numerator / (denominator × 2^e), with the remainder and the divisor it is a remainder of.</summary>
    private static (BigInteger Quotient, BigInteger Remainder, BigInteger Divisor) Divide(
        BigInteger numerator,
        BigInteger denominator,
        int e
    )
    {
        if (e < 0)
        {
            numerator <<= -e;
        }
        else
        {
            denominator <<= e;
        }

        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return (quotient, remainder, denominator);
    }

    /// <summary>The double q × 2^e, which must be exactly representable.</summary>
    private static double Compose(BigInteger quotient, int e)
    {
        if (quotient.IsZero)
        {
            return 0;
        }

        // A subnormal double: the significand is the bits as they are
        var length = BitLength(quotient);
        if (e == DoubleFormat.MinExponent && length < DoubleFormat.Precision)
        {
            return BitConverter.Int64BitsToDouble((long)quotient);
        }

        var shift = DoubleFormat.Precision - length;
        var significand = (long)(quotient << shift);
        var biasedExponent = (long)(e - shift + DoubleFormat.Precision - 1 + 1023);
        return BitConverter.Int64BitsToDouble((biasedExponent << 52) | (significand & ((1L << 52) - 1)));
    }

    /// <summary>The number of bits of a non-negative integer. (<c>GetBitLength</c> isn't in netstandard2.0.)</summary>
    private static int BitLength(BigInteger value)
    {
        var bytes = value.ToByteArray();
        var top = bytes.Length - 1;
        while (top > 0 && bytes[top] == 0)
        {
            top--;
        }

        var bits = top * 8;
        for (int b = bytes[top]; b != 0; b >>= 1)
        {
            bits++;
        }

        return bits;
    }

    /// <param name="Precision">The bits of the significand, with the implicit one.</param>
    /// <param name="MinExponent">The exponent of the least significant bit of the smallest subnormal value.</param>
    /// <param name="MaxExponent">The power of two that is the first value too large.</param>
    private sealed record BinaryFormat(int Precision, int MinExponent, int MaxExponent);
}
