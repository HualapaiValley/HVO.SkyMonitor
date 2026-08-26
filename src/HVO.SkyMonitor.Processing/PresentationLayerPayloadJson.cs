using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>The result of strict canonical presentation-payload parsing.</summary>
public sealed record PresentationLayerPayloadParseResult(PresentationLayerPayloadV1? Payload, string? ErrorPath)
{
    public bool IsValid => Payload is not null;
}

/// <summary>Canonical wire format and content identity for typed presentation primitives.</summary>
public static class PresentationLayerPayloadJson
{
    public const string MediaType = "application/vnd.hvo.presentation-layer-payload+json";
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Snapshots bounded primitives, computes identity from that snapshot, and rejects canonical JSON over 4 MiB.</summary>
    public static PresentationLayerPayloadV1 Create(
        string sourceIdentitySha256,
        int widthPixels,
        int heightPixels,
        IEnumerable<PresentationMarkerV1>? markers = null,
        IEnumerable<PresentationSegmentV1>? segments = null,
        IEnumerable<PresentationEllipseV1>? ellipses = null,
        IEnumerable<PresentationTextBlockV1>? textBlocks = null,
        PresentationTileMaskV1? tileMask = null)
    {
        var value = new PresentationLayerPayloadV1(
            PresentationLayerPayloadV1.CurrentSchemaVersion, string.Empty, NormalizeSha256(sourceIdentitySha256),
            widthPixels, heightPixels, Freeze(markers, PresentationLayerPayloadV1.MaximumMarkers),
            Freeze(segments, PresentationLayerPayloadV1.MaximumSegments),
            Freeze(ellipses, PresentationLayerPayloadV1.MaximumEllipses),
            Freeze(textBlocks, PresentationLayerPayloadV1.MaximumTextBlocks), Freeze(tileMask));
        value = FreezePayload(value);
        value = value with { ContentIdentitySha256 = ComputeIdentityCore(value) };
        var bytes = CanonicalBytes(value);
        if (bytes.Length > MaximumPayloadBytes)
        {
            throw new ArgumentException("Presentation layer payload exceeds 4 MiB.", nameof(markers));
        }
        ValidateFrozen(value);
        return value;
    }

    /// <summary>Serializes a defensive snapshot as canonical UTF-8 JSON after semantic identity validation.</summary>
    public static byte[] Serialize(PresentationLayerPayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var frozen = FreezePayload(payload);
        ValidateFrozen(frozen);
        var bytes = CanonicalBytes(frozen);
        if (bytes.Length > MaximumPayloadBytes) throw new ArgumentException("Presentation layer payload exceeds 4 MiB.", nameof(payload));
        return bytes;
    }

    /// <summary>Strictly parses canonical UTF-8 JSON, rejecting duplicates, unknown members, invalid bounds, and identity mismatch.</summary>
    public static PresentationLayerPayloadParseResult Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length > MaximumPayloadBytes) return new(null, "$payload");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (HasDuplicates(document.RootElement)) return new(null, "$json");
            var value = JsonSerializer.Deserialize<PresentationLayerPayloadV1>(json.Span, Options);
            if (value is null) return new(null, "$payload");
            value = FreezePayload(value);
            ValidateFrozen(value);
            return json.Span.SequenceEqual(CanonicalBytes(value)) ? new(value, null) : new(null, "$canonical");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or
            NullReferenceException or OverflowException or FormatException)
        {
            return new(null, "$payload");
        }
    }

    /// <summary>Computes uppercase SHA-256 over canonical content with the content-identity field blank.</summary>
    public static string ComputeIdentity(PresentationLayerPayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ComputeIdentityCore(FreezePayload(payload));
    }

    /// <summary>Validates a defensive snapshot and its canonical content identity.</summary>
    public static void Validate(PresentationLayerPayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateFrozen(FreezePayload(payload));
    }

    private static void ValidateFrozen(PresentationLayerPayloadV1 payload)
    {
        payload.ValidateStructure();
        if (!string.Equals(payload.ContentIdentitySha256, ComputeIdentityCore(payload), StringComparison.Ordinal))
            throw new ArgumentException("Presentation payload identity does not match its content.", nameof(payload));
    }

    private static string ComputeIdentityCore(PresentationLayerPayloadV1 payload) =>
        CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(
            payload with { ContentIdentitySha256 = string.Empty }, Options));

    private static PresentationLayerPayloadV1 FreezePayload(PresentationLayerPayloadV1 value) => value with
    {
        Markers = Freeze(value.Markers, PresentationLayerPayloadV1.MaximumMarkers),
        Segments = Freeze(value.Segments, PresentationLayerPayloadV1.MaximumSegments),
        Ellipses = Freeze(value.Ellipses, PresentationLayerPayloadV1.MaximumEllipses),
        TextBlocks = new ReadOnlyCollection<PresentationTextBlockV1>(value.TextBlocks.Select(static block => block with
        {
            Lines = new ReadOnlyCollection<string>(block.Lines.ToArray())
        }).ToArray()),
        TileMask = Freeze(value.TileMask)
    };

    private static PresentationTileMaskV1? Freeze(PresentationTileMaskV1? value) =>
        value is null ? null : value with { Bits = value.Bits.ToArray() };

    private static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T>? source, int maximum)
    {
        if (source is null) return new([]);
        var values = new List<T>();
        foreach (var value in source)
        {
            if (value is null || values.Count == maximum) throw new ArgumentException("Primitive collection exceeds its bound.", nameof(source));
            values.Add(value);
        }
        return new(values.ToArray());
    }

    private static byte[] CanonicalBytes(PresentationLayerPayloadV1 value) => JsonSerializer.SerializeToUtf8Bytes(
        CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value, Options)));

    private static bool HasDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name) || HasDuplicates(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(HasDuplicates)) return true;
        return false;
    }

    private static string NormalizeSha256(string value)
    {
        if (value is null || value.Length != 64 || value.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Source identity must be SHA-256.", nameof(value));
        return value.ToUpperInvariant();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
