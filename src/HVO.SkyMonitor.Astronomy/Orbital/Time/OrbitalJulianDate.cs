namespace HVO.SkyMonitor.Astronomy;

/// <summary>A split Julian date. Keep the day and fraction separate during numerical evaluation.</summary>
public readonly record struct OrbitalJulianDate(double Day, double Fraction)
{
    /// <summary>Converts UTC ticks without first forming a large, rounded single-double Julian date.</summary>
    public static OrbitalJulianDate FromUtc(DateTimeOffset utc)
    {
        OrbitalContract.Require(OrbitalContract.Utc(utc), "time-not-utc");
        var ticks = utc.Ticks;
        return new(1721425.5 + ticks / TimeSpan.TicksPerDay,
            (double)(ticks % TimeSpan.TicksPerDay) / TimeSpan.TicksPerDay);
    }

    /// <summary>Adds a small number of seconds, preserving the split and handling day rollover.</summary>
    public OrbitalJulianDate AddSeconds(double seconds)
    {
        OrbitalContract.Require(double.IsFinite(Day) && double.IsFinite(Fraction) && double.IsFinite(seconds), "invalid-julian-date");
        var fraction = Fraction + seconds / 86400;
        var carry = Math.Floor(fraction);
        return new(Day + carry, fraction - carry);
    }
}
