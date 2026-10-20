using System.Collections.Immutable;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Reads Kp number spellings and provisional labels independently.</summary>
public static class SwpcKpForecastParser
{
    /// <summary>Rejects the whole body if any Kp row fails its schema.</summary>
    public static SwpcBodyResult<KpBin> Parse(ReadOnlySpan<byte> decoded, string? mediaType)
    {
        var body = SwpcJsonBody.Read(decoded, SwpcProduct.KpForecast, mediaType);
        if (body.RejectionReason is { } reason) return SwpcJsonBody.Rejected<KpBin>(reason);
        var root = body.Root!.Value;
        if (root.ValueKind != JsonValueKind.Array) return SwpcJsonBody.Rejected<KpBin>("schema");
        var count = root.GetArrayLength();
        if (count == 0) return SwpcJsonBody.Rejected<KpBin>("suspect-empty");
        var bins = new SortedDictionary<DateTimeOffset, KpBin>();
        DateTimeOffset? stamp = null;
        foreach (var row in root.EnumerateArray())
        {
            if (!SwpcJsonBody.String(row, "time_tag", out var time, maximumBytes: 19) || SwpcText.Stamp(time, true) is not { } start
                || !SwpcJsonBody.Property(row, "kp", out var kp) || kp.ValueKind != JsonValueKind.Number || JsonMarshal.GetRawUtf8Value(kp).Length != 4 || !SwpcText.Whole(kp.GetRawText(), @"[0-9]\.[0-9]{2}")
                || !SwpcJsonBody.String(row, "observed", out var label) || !SwpcJsonBody.NullableString(row, "noaa_scale", out var scale))
                return SwpcJsonBody.Rejected<KpBin>("schema");
            bins[start] = new(start, kp.GetRawText(), label, scale);
        }
        foreach (var bin in bins.Values)
            if (bin.ObservedKind == "observed" && (stamp is null || bin.StartUtc > stamp)) stamp = bin.StartUtc;
        return new([.. bins.Values], [], null, SwpcHash.Sha256UpperHex(decoded), stamp, count);
    }
}
