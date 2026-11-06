using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Reads separate R, S and G scale windows without coercing source values.</summary>
public static class SwpcScalesParser
{
    /// <summary>Parses the five required scale windows, keeping strings and nulls.</summary>
    public static SwpcBodyResult<ScaleReading> Parse(ReadOnlySpan<byte> decoded, string? mediaType)
    {
        var body = SwpcJsonBody.Read(decoded, SwpcProduct.Scales, mediaType);
        if (body.RejectionReason is { } reason) return SwpcJsonBody.Rejected<ScaleReading>(reason);
        var root = body.Root!.Value;
        if (root.ValueKind != JsonValueKind.Object) return SwpcJsonBody.Rejected<ScaleReading>("schema");
        var records = ImmutableArray.CreateBuilder<ScaleReading>();
        for (var day = -1; day <= 3; day++)
        {
            if (!SwpcJsonBody.Property(root, day.ToString(CultureInfo.InvariantCulture), out var obj)
                || !SwpcJsonBody.String(obj, "DateStamp", out var date, maximumBytes: 19) || !SwpcJsonBody.String(obj, "TimeStamp", out var time, maximumBytes: 19)
                || SwpcText.Stamp(date + " " + time, false) is not { } stamp
                || !SwpcJsonBody.Property(obj, "R", out var r) || !SwpcJsonBody.Property(obj, "S", out var s) || !SwpcJsonBody.Property(obj, "G", out var g)
                || !SwpcJsonBody.NullableString(r, "Scale", out var rs) || !SwpcJsonBody.NullableString(r, "MinorProb", out var minor)
                || !SwpcJsonBody.NullableString(r, "MajorProb", out var major) || !SwpcJsonBody.NullableString(s, "Scale", out var ss)
                || !SwpcJsonBody.NullableString(s, "Prob", out var prob) || !SwpcJsonBody.NullableString(g, "Scale", out var gs))
                return SwpcJsonBody.Rejected<ScaleReading>("schema");
            records.Add(new(day == -1 ? "observed24hMax" : day == 0 ? "current" : "predictedDay" + day.ToString(CultureInfo.InvariantCulture), stamp, rs, minor, major, ss, prob, gs));
        }
        return new(records.ToImmutable(), [], null, SwpcHash.Sha256UpperHex(decoded), records[1].StampUtc, 5);
    }
}
