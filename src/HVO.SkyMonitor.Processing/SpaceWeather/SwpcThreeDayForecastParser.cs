using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Reads the issued three-day forecast and keeps S and R probabilities separate.</summary>
public static class SwpcThreeDayForecastParser
{
    /// <summary>Parses the required tables after text normalization.</summary>
    public static SwpcBodyResult<IssuedForecast> Parse(ReadOnlySpan<byte> decoded, string? mediaType)
    {
        var reason = SwpcJsonBody.ValidateEnvelope(decoded, SwpcProduct.ThreeDayForecast, mediaType);
        if (reason is not null) return SwpcJsonBody.Rejected<IssuedForecast>(reason);
        var text = SwpcText.Normalize(SwpcText.Utf8.GetString(decoded));
        var issuedMatch = SwpcText.Match(text, "(?m)^:Issued: (.+)$");
        var issued = issuedMatch.Success ? SwpcText.SourceTime(issuedMatch.Groups[1].Value) : null;
        if (!text.StartsWith(":Product: 3-Day Forecast\n", StringComparison.Ordinal) || issued is null)
            return SwpcJsonBody.Rejected<IssuedForecast>("schema");
        var lines = text.Split('\n');
        var kp = Table(lines, @"NOAA Kp index breakdown [^\s\u001c-\u001f].*", issued.Value);
        var radiation = Table(lines, @"Solar Radiation Storm Forecast for [^\s\u001c-\u001f].*", issued.Value);
        var blackout = Table(lines, @"Radio Blackout Forecast for [^\s\u001c-\u001f].*", issued.Value);
        if (kp is null || radiation is null || blackout is null) return SwpcJsonBody.Rejected<IssuedForecast>("schema");
        var bins = ImmutableSortedDictionary.CreateBuilder<DateTimeOffset, SwpcForecastKpBin>();
        var starts = new List<int>();
        foreach (var row in kp.Rows)
        {
            var rm = SwpcText.Match(row, @"\A([0-9]{2})-[0-9]{2}UT +(.+)\z");
            if (!rm.Success) return SwpcJsonBody.Rejected<IssuedForecast>("schema");
            var cm = SwpcText.Match(rm.Groups[2].Value, @"\A([0-9]\.[0-9]{2})(?: \((G[1-5])\))? +([0-9]\.[0-9]{2})(?: \((G[1-5])\))? +([0-9]\.[0-9]{2})(?: \((G[1-5])\))?\z");
            if (!cm.Success) return SwpcJsonBody.Rejected<IssuedForecast>("schema");
            var hour = SwpcText.Number(rm.Groups[1].Value);
            starts.Add(hour);
            for (var column = 0; column < 3; column++)
            {
                var start = new DateTimeOffset(kp.Dates[column].ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(hour);
                var scale = cm.Groups[column * 2 + 2];
                bins[start] = new(start, cm.Groups[column * 2 + 1].Value, scale.Success ? scale.Value : null);
            }
        }
        if (!starts.SequenceEqual(Enumerable.Range(0, 8).Select(i => i * 3))) return SwpcJsonBody.Rejected<IssuedForecast>("schema");
        var s = Probabilities(radiation);
        var r = Probabilities(blackout);
        if (s is null || r is null) return SwpcJsonBody.Rejected<IssuedForecast>("schema");
        return new([new(issued.Value, bins.ToImmutable(), s, r, text)], [], null,
            SwpcHash.Sha256UpperHex(SwpcText.Utf8.GetBytes(text)), issued, 1);
    }

    private sealed record ForecastTable(ImmutableArray<DateOnly> Dates, ImmutableArray<string> Rows);

    private static ForecastTable? Table(string[] lines, string title, DateTimeOffset issued)
    {
        var index = Array.FindIndex(lines, line => SwpcText.Whole(line, title));
        if (index < 0) return null;
        index++;
        while (index < lines.Length && SwpcText.IsBlank(lines[index])) index++;
        if (index >= lines.Length) return null;
        var header = SwpcText.Match(lines[index], @"\A *([A-Z][a-z]{2}) ([0-9]{2}) +([A-Z][a-z]{2}) ([0-9]{2}) +([A-Z][a-z]{2}) ([0-9]{2})\z");
        if (!header.Success) return null;
        var dates = ImmutableArray.CreateBuilder<DateOnly>(3);
        for (var column = 0; column < 3; column++)
        {
            var day = SwpcText.Yearless(header.Groups[column * 2 + 1].Value, header.Groups[column * 2 + 2].Value, issued);
            if (day is null) return null;
            dates.Add(day.Value);
        }
        var rows = ImmutableArray.CreateBuilder<string>();
        for (index++; index < lines.Length && !SwpcText.IsBlank(lines[index]); index++) rows.Add(lines[index]);
        return new(dates.ToImmutable(), rows.ToImmutable());
    }

    private static ImmutableDictionary<string, ImmutableDictionary<DateOnly, string>>? Probabilities(ForecastTable table)
    {
        if (table.Rows.IsEmpty) return null;
        var rows = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<DateOnly, string>>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            var m = SwpcText.Match(row, @"\A([^ \t\r\n\f\v].*?) {2,}([0-9]{1,3})% +([0-9]{1,3})% +([0-9]{1,3})%\z");
            if (!m.Success) return null;
            var values = ImmutableDictionary.CreateBuilder<DateOnly, string>();
            for (var i = 0; i < 3; i++) values[table.Dates[i]] = m.Groups[i + 2].Value;
            rows[m.Groups[1].Value] = values.ToImmutable();
        }
        return rows.ToImmutable();
    }
}
