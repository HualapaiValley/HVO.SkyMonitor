using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Parses bounded alert bodies and rejects invalid records independently.</summary>
public static class SwpcAlertsParser
{
    /// <summary>Parses all records without performing ingest, deduplication or I/O.</summary>
    public static SwpcBodyResult<SwpcMessageRecord> Parse(ReadOnlySpan<byte> decoded, string? mediaType)
    {
        var reason = SwpcJsonBody.Validate(decoded, SwpcProduct.Alerts, mediaType, out var count);
        if (reason is not null) return SwpcJsonBody.Rejected<SwpcMessageRecord>(reason);
        var records = ImmutableArray.CreateBuilder<SwpcMessageRecord>();
        var rejections = ImmutableArray.CreateBuilder<SwpcRecordRejection>();
        var index = 0;
        var reader = new Utf8JsonReader(decoded, new JsonReaderOptions { MaxDepth = SwpcLimits.JsonDepth });
        reader.Read();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            var message = ParseRecord(decoded[start..(int)reader.BytesConsumed], out reason);
            if (message is null) rejections.Add(new(index, reason!));
            else records.Add(message);
            index++;
        }
        if (records.Count == 0) return SwpcJsonBody.Rejected<SwpcMessageRecord>("no-valid-records", rejections.ToImmutable());
        return new(records.ToImmutable(), rejections.ToImmutable(), null, SwpcHash.Sha256UpperHex(decoded), records.Max(m => m.IssueMinuteUtc), count);
    }

    /// <summary>Returns record-check diagnostics for a body that the caller elects to merge.</summary>
    public static ImmutableArray<string> Diagnostics(SwpcBodyResult<SwpcMessageRecord> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!body.Accepted) return ["body-rejected:" + body.RejectionReason];
        var diagnostics = new List<string>();
        var reportedDuplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rejection in body.Rejections) diagnostics.Add($"record-rejected:{rejection.Reason}:{rejection.Index.ToString(CultureInfo.InvariantCulture)}");
        var seen = new HashSet<(string Identity, string Hash)>();
        foreach (var record in body.Records)
        {
            if (!string.Equals(record.IssueDatetimeText[..16], record.IssueMinuteUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal))
                diagnostics.Add("issue-time-mismatch:" + record.Identity);
            if (!seen.Add((record.Identity, record.ContentSha256)) && reportedDuplicates.Add(record.Identity)) diagnostics.Add("duplicate-record-in-body:" + record.Identity);
        }
        return [.. diagnostics.Order(StringComparer.Ordinal)];
    }

    private static SwpcMessageRecord? ParseRecord(ReadOnlySpan<byte> raw, out string? reason)
    {
        reason = "record-schema";
        var reader = new Utf8JsonReader(raw, new JsonReaderOptions { MaxDepth = SwpcLimits.JsonDepth });
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject) return null;
        ReadOnlySpan<byte> productRaw = default, issueRaw = default, messageRaw = default;
        var productString = false;
        var issueString = false;
        var messageString = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var field = SwpcJsonBody.Key(ref reader, "product_id"u8) ? 1
                : SwpcJsonBody.Key(ref reader, "issue_datetime"u8) ? 2
                : SwpcJsonBody.Key(ref reader, "message"u8) ? 3 : 0;
            reader.Read();
            var isString = reader.TokenType == JsonTokenType.String;
            var value = isString ? reader.ValueSpan : default;
            if (field == 1) { productRaw = value; productString = isString; }
            if (field == 2) { issueRaw = value; issueString = isString; }
            if (field == 3) { messageRaw = value; messageString = isString; }
            reader.Skip();
        }
        if (!productString || !issueString || !messageString
            || !SwpcJsonBody.Utf8Length(productRaw, out var productLength)
            || !SwpcJsonBody.Utf8Length(issueRaw, out var issueLength)
            || !SwpcJsonBody.Utf8Length(messageRaw, out var messageLength)
            || productLength is < 2 or > 8) return null;
        var product = Materialize(productRaw);
        if (!SwpcText.Whole(product, "[A-Z0-9]{2,8}")) return null;
        reason = "issue-datetime";
        if (issueLength != 23) return null;
        var issueText = Materialize(issueRaw);
        if (!SwpcText.Whole(issueText, @"[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}")) return null;
        reason = "record-oversize";
        if (messageLength > SwpcLimits.MessageBytes) return null;
        var message = Materialize(messageRaw);
        var text = SwpcText.Normalize(message);
        var lines = text.Split('\n');
        reason = "message-header";
        if (lines.Length < 5) return null;
        var code = SwpcText.Match(lines[0], @"\ASpace Weather Message Code: ([A-Z0-9]{4,8})\z");
        var serial = SwpcText.Match(lines[1], @"\ASerial Number: ([0-9]{1,6})\z");
        if (!code.Success || !serial.Success || !SwpcText.Whole(lines[2], "Issue Time: [0-9]{4} [A-Z][a-z]{2} [0-9]{2} [0-9]{4} UTC")) return null;
        var issue = SwpcText.SourceTime(lines[2][12..]);
        if (issue is null) return null;
        var codeText = code.Groups[1].Value;
        var serialNumber = SwpcText.Number(serial.Groups[1].Value);
        var identity = codeText + "/" + serialNumber.ToString(CultureInfo.InvariantCulture) + "/" + SwpcText.Utc(issue.Value);
        reason = null;
        return new(identity, codeText, serialNumber, issue.Value, issueText, SwpcHash.Sha256UpperHex(SwpcText.Utf8.GetBytes(text)),
            SwpcText.Utf8.GetString(raw), Header(codeText, text, lines, issue.Value), product, text);
    }

    private static string Materialize(ReadOnlySpan<byte> raw)
    {
        // Decode only bounded required fields. GetString also validates the
        // escape spelling, which the completed streaming pass has already read.
        if (!raw.Contains((byte)'\\')) return SwpcText.Utf8.GetString(raw);
        var quoted = new byte[raw.Length + 2];
        quoted[0] = quoted[^1] = (byte)'"';
        raw.CopyTo(quoted.AsSpan(1));
        var reader = new Utf8JsonReader(quoted);
        reader.Read();
        return reader.GetString()!;
    }

    private static SwpcMessageHeader Header(string code, string text, string[] lines, DateTimeOffset issue)
    {
        var headline = lines.Skip(3).FirstOrDefault(l => !SwpcText.IsBlank(l)) ?? "";
        var match = SwpcText.Match(SwpcText.TrimWhitespace(headline.AsSpan()).ToString(), @"\A(?:(CANCEL|EXTENDED|CONTINUED) )?(WATCH|WARNING|ALERT|SUMMARY): (.+)\z");
        var kind = match.Success ? match.Groups[2].Value : null;
        var modifier = match.Success && match.Groups[1].Success ? match.Groups[1].Value : null;
        var family = Family(code, kind);
        SwpcMessageLink? link = null;
        foreach (var (label, linkKind) in new[] { ("Extension to Serial Number", "extension"), ("Continuation of Serial Number", "continuation"), ("Cancel Serial Number", "cancel") })
        {
            var value = SwpcText.Field(text, label);
            if (value is not null && SwpcText.Whole(value, "[0-9]{1,6}")) link = new(linkKind, SwpcText.Number(value));
        }
        var pole = SwpcText.Match(text, "poleward of ([0-9]{1,2}) degrees Geomagnetic Latitude");
        var times = ImmutableDictionary.CreateBuilder<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (var label in new[] { "Valid From", "Valid To", "Now Valid Until", "Threshold Reached", "Begin Time" })
            times[label] = SwpcText.SourceTime(SwpcText.Field(text, label));
        return new(headline, kind, modifier, SwpcText.Match(text, "(?m)^CORRECTED$").Success, link, family,
            Level(code, family, headline, text), pole.Success ? SwpcText.Number(pole.Groups[1].Value) : null,
            family == SpaceWeatherFamily.Watch ? WatchDays(text, issue) : [], times.ToImmutable(), SwpcText.Field(text, "Synoptic Period"), SwpcText.Field(text, "NOAA Scale"));
    }

    private static SpaceWeatherFamily Family(string code, string? kind)
    {
        if (kind == "WATCH" && code.StartsWith("WATA", StringComparison.Ordinal)) return SpaceWeatherFamily.Watch;
        if (kind == "WARNING" && code.StartsWith("WAR", StringComparison.Ordinal)) return SpaceWeatherFamily.Warning;
        if (kind == "ALERT")
        {
            if (SwpcText.Whole(code, "ALTK0[4-9]")) return SpaceWeatherFamily.KAlert;
            if (code.StartsWith("ALTX", StringComparison.Ordinal)) return SpaceWeatherFamily.XrayAlert;
            if (code is "ALTTP2" or "ALTTP4") return SpaceWeatherFamily.RadioSweep;
            if (code is "ALTEF3" or "ALTPC0" || code.StartsWith("ALTPX", StringComparison.Ordinal)) return SpaceWeatherFamily.FluxAlert;
        }
        return kind == "SUMMARY" && code.StartsWith("SUM", StringComparison.Ordinal) ? SpaceWeatherFamily.Summary : SpaceWeatherFamily.Unclassified;
    }

    private static string? Level(string code, SpaceWeatherFamily family, string headline, string text)
    {
        var k = SwpcText.Match(code, @"\A(?:WARK|ALTK)0([4-9])\z");
        if (k.Success) return k.Groups[1].Value == "4" ? "K4" : "G" + (SwpcText.Number(k.Groups[1].Value) - 4).ToString(CultureInfo.InvariantCulture);
        var level = family == SpaceWeatherFamily.Watch ? SwpcText.Match(headline, "Category (G[1-5])")
            : SwpcText.Match(text, "(?m)^[nN][oO][aA][aA] [sS][cC][aA][lL][eE]: ([GgRrSs][1-5])(?:[^A-Za-z0-9_]|\\z)");
        return level.Success ? level.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static ImmutableArray<SwpcWatchDay>? WatchDays(string text, DateTimeOffset issue)
    {
        var days = ImmutableArray.CreateBuilder<SwpcWatchDay>();
        foreach (var line in text.Split('\n'))
            foreach (Match m in SwpcText.Pattern(@"([A-Z][a-z]{2}) ([0-9]{2})[ \t\r\n\f\v]*:[ \t\r\n\f\v]+(G[1-5]|None)(?:[^A-Za-z0-9_]|\z)").Matches(line))
            {
                if (SwpcText.Month(m.Groups[1].Value) == 0) continue;
                var day = SwpcText.Yearless(m.Groups[1].Value, m.Groups[2].Value, issue);
                if (day is null) return null;
                days.Add(new(day.Value, m.Groups[3].Value));
            }
        return days.ToImmutable();
    }
}
