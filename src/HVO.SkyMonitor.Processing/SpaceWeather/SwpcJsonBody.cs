using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices;

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
        var reason = Validate(decoded, product, mediaType, out _);
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

    // Validate the entire token stream before reporting schema/count failures: a
    // malformed token after record 2001 must still win. No DOM or record array.
    internal static string? Validate(ReadOnlySpan<byte> decoded, SwpcProduct product, string? mediaType, out int count)
    {
        count = 0;
        var reason = ValidateEnvelope(decoded, product, mediaType);
        if (reason is not null) return reason;
        try
        {
            var reader = new Utf8JsonReader(decoded, new JsonReaderOptions { MaxDepth = SwpcLimits.JsonDepth });
            if (!reader.Read()) return "malformed-json";
            var array = reader.TokenType == JsonTokenType.StartArray;
            while (reader.Read())
                if (array && reader.CurrentDepth == 1 && reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject)) count++;
            if (product == SwpcProduct.Alerts)
            {
                if (!array) return "schema";
                if (count == 0) return "suspect-empty";
                if (count > SwpcLimits.Records) return "too-many-records";
            }
            return null;
        }
        catch (JsonException) { return "malformed-json"; }
    }

    internal static string? ValidateEnvelope(ReadOnlySpan<byte> decoded, SwpcProduct product, string? mediaType)
    {
        if (decoded.Length > SwpcLimits.BodyBytes(product)) return "oversize";
        var media = mediaType.AsSpan();
        var semicolon = media.IndexOf(';');
        if (semicolon >= 0) media = media[..semicolon];
        media = SwpcText.TrimWhitespace(media);
        foreach (var character in media)
            if (character > 127) return "content-type";
        if (!media.Equals(product == SwpcProduct.ThreeDayForecast ? "text/plain" : "application/json", StringComparison.OrdinalIgnoreCase)) return "content-type";
        try { SwpcText.Utf8.GetCharCount(decoded); }
        catch (DecoderFallbackException) { return "encoding"; }
        return null;
    }

    internal static bool String(JsonElement obj, string key, out string value, int maximumBytes = int.MaxValue)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object || !Property(obj, key, out var field) || field.ValueKind != JsonValueKind.String) return false;
        if (maximumBytes != int.MaxValue && (!Utf8Length(JsonMarshal.GetRawUtf8Value(field)[1..^1], out var length) || length > maximumBytes)) return false;
        try { value = field.GetString()!; }
        catch (InvalidOperationException) { value = DecodeEscapedString(field.GetRawText()); }
        return true;
    }

    // Last duplicate wins, as in Python's dict. An unread property name with an
    // unpaired surrogate cannot equal an ASCII schema key and must not throw.
    internal static bool Property(JsonElement obj, string key, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        foreach (var property in obj.EnumerateObject())
        {
            try
            {
                if (!property.NameEquals(key)) continue;
            }
            catch (InvalidOperationException) { continue; }
            value = property.Value;
            found = true;
        }
        return found;
    }

    internal static bool Key(ref Utf8JsonReader reader, ReadOnlySpan<byte> key)
    {
        try { return reader.ValueTextEquals(key); }
        catch (InvalidOperationException) { return false; }
    }

    // Input is a syntax-validated raw JSON string value, without its quotes.
    // Count its unescaped UTF-8 bytes without allocating, including surrogate
    // pairing even when an earlier prefix already exceeds a field's byte cap.
    internal static bool Utf8Length(ReadOnlySpan<byte> raw, out int length)
    {
        length = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'\\') { length++; continue; }
            if (raw[++i] != (byte)'u') { length++; continue; }
            var code = Hex(raw.Slice(i + 1, 4));
            i += 4;
            if (code is >= 0xd800 and <= 0xdbff)
            {
                if (i + 6 >= raw.Length || raw[i + 1] != (byte)'\\' || raw[i + 2] != (byte)'u'
                    || Hex(raw.Slice(i + 3, 4)) is not (>= 0xdc00 and <= 0xdfff)) return false;
                i += 6;
                length += 4;
            }
            else if (code is >= 0xdc00 and <= 0xdfff) return false;
            else length += code < 0x80 ? 1 : code < 0x800 ? 2 : 3;
        }
        return true;

        static int Hex(ReadOnlySpan<byte> digits)
        {
            var value = 0;
            foreach (var digit in digits)
                value = value * 16 + (digit <= '9' ? digit - '0' : (digit | 32) - 'a' + 10);
            return value;
        }
    }

    // The reference model permits unpaired escapes in non-alert string values.
    // JsonElement.GetString rejects those UTF-16 values; syntax was already validated,
    // so preserve each escaped code unit without applying an extra schema restriction.
    private static string DecodeEscapedString(string raw)
    {
        var result = new StringBuilder(raw.Length - 2);
        for (var i = 1; i < raw.Length - 1; i++)
        {
            var c = raw[i];
            if (c != '\\') { result.Append(c); continue; }
            c = raw[++i];
            if (c == 'u')
            {
                result.Append((char)ushort.Parse(raw.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                i += 4;
            }
            else result.Append(c switch { 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => c });
        }
        return result.ToString();
    }

    internal static bool NullableString(JsonElement obj, string key, out string? value)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object || !Property(obj, key, out var field)) return false;
        if (field.ValueKind == JsonValueKind.Null) return true;
        if (!String(obj, key, out var text)) return false;
        value = text;
        return true;
    }

    internal static SwpcBodyResult<T> Rejected<T>(string reason, System.Collections.Immutable.ImmutableArray<SwpcRecordRejection> rejections = default)
        => new([], rejections.IsDefault ? [] : rejections, reason, null, null, 0);
}
