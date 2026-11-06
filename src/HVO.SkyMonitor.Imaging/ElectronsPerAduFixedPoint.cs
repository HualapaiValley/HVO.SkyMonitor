using System.Globalization;

namespace HVO.SkyMonitor.Imaging;

/// <summary>
/// Exact fixed-point form of a positive electrons-per-ADU conversion coefficient. A value is the integer count of
/// 10^-12 electrons per ADU. Each conversion rounds once, from the exact operand, to the nearest representable value
/// with ties to even, so every platform produces the same integer for the same input.
/// </summary>
public static class ElectronsPerAduFixedPoint
{
    public const string RepresentationVersion = "electrons-per-adu-fixed-e12-v1";
    public const long Scale = 1_000_000_000_000;
    public const int FractionDigits = 12;
    public const double MaximumElectronsPerAdu = 1_000_000;
    public const long MaximumScaledValue = 1_000_000 * Scale;

    private const int MaximumRationalTerm = ushort.MaxValue;

    public static bool IsValid(long scaledValue) => scaledValue is > 0 and <= MaximumScaledValue;

    /// <summary>Converts a finite positive coefficient to the representation.</summary>
    public static bool TryFromDouble(double electronsPerAdu, out long scaledValue)
        => TryFromDouble(electronsPerAdu, 1, 1, out scaledValue);

    /// <summary>
    /// Converts <c>electronsPerAdu × numerator / denominator</c> with one rounding. The rational term carries an
    /// exact stored-code mapping, such as a full-range scale from native samples into a wider container.
    /// </summary>
    public static bool TryFromDouble(double electronsPerAdu, int numerator, int denominator, out long scaledValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numerator, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(numerator, MaximumRationalTerm);
        ArgumentOutOfRangeException.ThrowIfLessThan(denominator, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(denominator, MaximumRationalTerm);
        scaledValue = 0;
        if (!double.IsFinite(electronsPerAdu) || electronsPerAdu <= 0 || electronsPerAdu > MaximumElectronsPerAdu)
        {
            return false;
        }

        // The operand is exactly significand × 2^-shift. Below the maximum the shift is at least 33, and the
        // numerator stays below 2^109, so both terms fit Int128 for every shift that can round to a non-zero value.
        var bits = BitConverter.DoubleToInt64Bits(electronsPerAdu);
        var biasedExponent = (int)((bits >> 52) & 0x7FF);
        var significand = bits & 0x000F_FFFF_FFFF_FFFFL;
        if (biasedExponent == 0)
        {
            biasedExponent = 1;
        }
        else
        {
            significand |= 1L << 52;
        }
        var shift = 1075 - biasedExponent;
        if (shift > 110)
        {
            return false;
        }
        var dividend = (Int128)significand * Scale * numerator;
        var divisor = (Int128)denominator << shift;
        var (quotient, remainder) = Int128.DivRem(dividend, divisor);
        var twiceRemainder = remainder << 1;
        if (twiceRemainder > divisor || twiceRemainder == divisor && !Int128.IsEvenInteger(quotient))
        {
            quotient++;
        }
        if (quotient < 1 || quotient > MaximumScaledValue)
        {
            return false;
        }
        scaledValue = (long)quotient;
        return true;
    }

    /// <summary>
    /// Returns whether <c>maximum / minimum</c> does not exceed <c>limitNumerator / limitDenominator</c>, compared
    /// exactly without division.
    /// </summary>
    public static bool IsRatioWithin(long minimum, long maximum, int limitNumerator, int limitDenominator)
    {
        if (!IsValid(minimum) || !IsValid(maximum) || minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "The coefficients must be valid and ordered.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(limitNumerator, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limitDenominator, 1);
        return (Int128)maximum * limitDenominator <= (Int128)minimum * limitNumerator;
    }

    /// <summary>Formats a value as an invariant decimal with exactly twelve fraction digits.</summary>
    public static string Format(long scaledValue)
    {
        if (!IsValid(scaledValue))
        {
            throw new ArgumentOutOfRangeException(nameof(scaledValue));
        }
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{scaledValue / Scale}.{scaledValue % Scale:D12}");
    }
}
