using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Bounded SWPC text normalization and ASCII source parsing.</summary>
public static class SwpcText
{
    /// <summary>Normalizes line endings and trailing whitespace; empty text stays empty as in the reference model.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > SwpcLimits.AlertsBytes) throw new ArgumentOutOfRangeException(nameof(text));
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd(' ', '\t', '\r');
        var count = lines.Length;
        while (count > 0 && lines[count - 1].Length == 0) count--;
        return count == 0 ? "" : string.Join('\n', lines, 0, count) + "\n";
    }

    internal static Match Match(string text, string pattern) => Regex.Match(text, pattern,
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static bool Whole(string text, string pattern) => Match(text, "\\A(?:" + pattern + ")\\z").Success;
    internal static string? Field(string text, string label)
    {
        var match = Match(text, "(?m)^" + Regex.Escape(label) + ": ?(.*)$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    internal static int Month(string name) => name switch
    {
        "Jan" => 1,
        "Feb" => 2,
        "Mar" => 3,
        "Apr" => 4,
        "May" => 5,
        "Jun" => 6,
        "Jul" => 7,
        "Aug" => 8,
        "Sep" => 9,
        "Oct" => 10,
        "Nov" => 11,
        "Dec" => 12,
        _ => 0
    };
    internal static int Number(string text) => int.Parse(text, CultureInfo.InvariantCulture);
    internal static DateTimeOffset? SourceTime(string? text)
    {
        if (text is null) return null;
        var m = Match(text.Trim(), @"\A([0-9]{4}) ([A-Z][a-z]{2}) ([0-9]{2}) ([0-9]{2})([0-9]{2}) UTC\z");
        if (!m.Success || Month(m.Groups[2].Value) == 0) return null;
        return Date(Number(m.Groups[1].Value), Month(m.Groups[2].Value), Number(m.Groups[3].Value),
            Number(m.Groups[4].Value), Number(m.Groups[5].Value), 0);
    }
    internal static DateTimeOffset? Date(int year, int month, int day, int hour, int minute, int second)
    {
        if (year is < SwpcLimits.FirstYear or > SwpcLimits.LastYear) return null;
        try { return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    internal static DateTimeOffset? Stamp(string text, bool kp)
    {
        if (!Whole(text, kp ? @"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}" : @"[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}")) return null;
        return DateTimeOffset.TryParseExact(text, kp ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            && time.Year is >= SwpcLimits.FirstYear and <= SwpcLimits.LastYear ? time : null;
    }
    internal static DateOnly? Yearless(string month, string day, DateTimeOffset reference)
    {
        var m = Month(month);
        if (m == 0) return null;
        var delta = m - reference.Month;
        var year = reference.Year + (delta < -6 ? 1 : delta > 6 ? -1 : 0);
        try { return new DateOnly(year, m, Number(day)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    internal static string Utc(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    internal static UTF8Encoding Utf8 => new(false, true);
}
