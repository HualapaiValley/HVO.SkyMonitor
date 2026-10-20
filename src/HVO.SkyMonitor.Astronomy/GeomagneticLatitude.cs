using System.Numerics;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Approximate centred-dipole latitude with its pole at 80.8 N, 72.6 W.</summary>
public static class GeomagneticLatitude
{
    /// <summary>No model-version token is defined by the v1 contract or its reference model.</summary>
    public const string? ModelVersion = null;

    /// <summary>Computes geomagnetic latitude, rounded to a decimal tenth using the exact binary64 value.</summary>
    public static double Compute(double latitudeDeg, double longitudeDeg)
    {
        if (!double.IsFinite(latitudeDeg) || latitudeDeg is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(latitudeDeg));
        if (!double.IsFinite(longitudeDeg) || longitudeDeg is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(longitudeDeg));
        var toRadians = Math.PI / 180;
        var poleLatitude = 80.8 * toRadians;
        var poleLongitude = -72.6 * toRadians;
        var latitude = latitudeDeg * toRadians;
        var longitude = longitudeDeg * toRadians;
        var value = Math.Sin(latitude) * Math.Sin(poleLatitude) + Math.Cos(latitude) * Math.Cos(poleLatitude) * Math.Cos(longitude - poleLongitude);
        return RoundToTenth(Math.Asin(Math.Clamp(value, -1, 1)) * (180 / Math.PI));
    }

    /// <summary>Rounds the actual binary64 value to the nearest decimal tenth, ties to even, preserving signed zero.</summary>
    public static double RoundToTenth(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        // Scaling with double would round a second time before the tie decision (Math.Round's pitfall).
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = bits & 0x000f_ffff_ffff_ffff;
        if (exponent != 0) significand |= 1UL << 52;
        var power = exponent == 0 ? -1074 : exponent - 1023 - 52;
        var numerator = new BigInteger(significand) * 10;
        var denominator = BigInteger.One;
        if (power >= 0) numerator <<= power;
        else denominator <<= -power;
        var rounded = BigInteger.DivRem(numerator, denominator, out var remainder);
        var comparison = (remainder * 2).CompareTo(denominator);
        if (comparison > 0 || comparison == 0 && !rounded.IsEven) rounded++;
        // For large finite doubles no decimal-tenth adjustment is representable, and converting
        // the scaled integer to double could overflow before division.
        if (Math.Abs(value) >= 1e16) return value;
        return Math.CopySign((double)rounded / 10, value);
    }
}
