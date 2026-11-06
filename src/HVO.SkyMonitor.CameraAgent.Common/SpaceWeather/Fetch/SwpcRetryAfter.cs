namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

/// <summary>Exact v1 delta-seconds and IMF-fixdate parsing, followed by the policy delay clamp.</summary>
public static class SwpcRetryAfter
{
    /// <summary>
    /// Resolves one raw header without trimming or accepting obsolete date forms. Date differences use whole
    /// seconds truncated toward zero, including when the supplied clock has sub-second precision.
    /// </summary>
    public static TimeSpan Resolve(string? raw, DateTimeOffset nowUtc)
    {
        long seconds = (long)SwpcSource.RateLimitedDefault.TotalSeconds;
        if (raw is { Length: > 0 and <= SwpcSource.MaximumRetryAfterLength })
        {
            if (raw.Length <= 9 && TryDigits(raw, out var delta)) seconds = delta;
            else if (TryDate(raw, out var when)) seconds = (when.UtcTicks - nowUtc.UtcTicks) / TimeSpan.TicksPerSecond;
        }
        return TimeSpan.FromSeconds(Math.Clamp(seconds,
            (long)SwpcSource.RetryAfterMinimum.TotalSeconds, (long)SwpcSource.RetryAfterMaximum.TotalSeconds));
    }

    private static bool TryDigits(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        foreach (var character in text)
        {
            if (character is < '0' or > '9') return false;
            value = value * 10 + character - '0';
        }
        return true;
    }

    private static bool TryDate(string raw, out DateTimeOffset when)
    {
        when = default;
        if (raw.Length != 29 || raw[3] != ',' || raw[4] != ' ' || raw[7] != ' ' || raw[11] != ' ' ||
            raw[16] != ' ' || raw[19] != ':' || raw[22] != ':' || !raw.AsSpan(25).SequenceEqual(" GMT")) return false;
        var text = raw.AsSpan();
        var monthIndex = "JanFebMarAprMayJunJulAugSepOctNovDec".AsSpan().IndexOf(text.Slice(8, 3), StringComparison.Ordinal);
        var weekdayIndex = "SunMonTueWedThuFriSat".AsSpan().IndexOf(text[..3], StringComparison.Ordinal);
        if (monthIndex < 0 || monthIndex % 3 != 0 || weekdayIndex < 0 || weekdayIndex % 3 != 0 ||
            !TryDigits(text.Slice(5, 2), out var day) || !TryDigits(text.Slice(12, 4), out var year) ||
            !TryDigits(text.Slice(17, 2), out var hour) || !TryDigits(text.Slice(20, 2), out var minute) ||
            !TryDigits(text.Slice(23, 2), out var second)) return false;
        var month = monthIndex / 3 + 1;
        if (year < 1 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return false;
        when = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        return (int)when.DayOfWeek == weekdayIndex / 3;
    }
}
