using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>An immutable JSON root, or the first body rejection reason.</summary>
/// <param name="Root">The independently owned immutable root.</param>
/// <param name="RejectionReason">The first failed check, or null.</param>
public sealed record SwpcJsonBodyResult(JsonElement? Root, string? RejectionReason);

/// <summary>Checks byte limits, media type, UTF-8 and JSON syntax in contract order.</summary>
public static class SwpcJsonBody
{
    /// <summary>Reads JSON with a 64-container depth limit and preserves number and record source text.</summary>
    public static SwpcJsonBodyResult Read(ReadOnlySpan<byte> decoded, SwpcProduct product, string? mediaType)
    {
        if (product == SwpcProduct.ThreeDayForecast) throw new ArgumentOutOfRangeException(nameof(product));
        var reason = ValidateEnvelope(decoded, product, mediaType);
        if (reason is not null) return new(null, reason);
        try
        {
            var reader = new Utf8JsonReader(decoded, new JsonReaderOptions { MaxDepth = SwpcLimits.JsonDepth });
            using var document = JsonDocument.ParseValue(ref reader);
            // ParseValue consumes one value only; trailing tokens must also be rejected.
            if (reader.Read()) return new(null, "malformed-json");
            return new(document.RootElement.Clone(), null);
        }
        catch (JsonException) { return new(null, "malformed-json"); }
    }

    internal static string? ValidateEnvelope(ReadOnlySpan<byte> decoded, SwpcProduct product, string? mediaType)
    {
        if (decoded.Length > SwpcLimits.BodyBytes(product)) return "oversize";
        var media = mediaType.AsSpan();
        var semicolon = media.IndexOf(';');
        if (semicolon >= 0) media = media[..semicolon];
        if (!media.Trim().Equals(product == SwpcProduct.ThreeDayForecast ? "text/plain" : "application/json", StringComparison.OrdinalIgnoreCase)) return "content-type";
        try { SwpcText.Utf8.GetCharCount(decoded); }
        catch (DecoderFallbackException) { return "encoding"; }
        return null;
    }

    internal static bool String(JsonElement obj, string key, out string value)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) return false;
        try
        {
            value = field.GetString()!;
            SwpcText.Utf8.GetByteCount(value);
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static bool NullableString(JsonElement obj, string key, out string? value)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var field)) return false;
        if (field.ValueKind == JsonValueKind.Null) return true;
        if (!String(obj, key, out var text)) return false;
        value = text;
        return true;
    }

    internal static SwpcBodyResult<T> Rejected<T>(string reason, System.Collections.Immutable.ImmutableArray<SwpcRecordRejection> rejections = default)
        => new([], rejections.IsDefault ? [] : rejections, reason, null, null, 0);
}
