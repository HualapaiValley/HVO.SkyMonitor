using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Processing;

public sealed record EnvironmentalObservationDeliveryParseResult<T>(
    T? Value,
    EnvironmentalObservationValidationResult Validation)
    where T : class;

/// <summary>Strict JSON parsing and bounded validation for environmental delivery messages.</summary>
public static class EnvironmentalObservationDeliveryJson
{
    public const int MaximumEnvelopeBytes = EnvironmentalObservationJson.MaximumPayloadBytes + 4 * 1024;
    public const int MaximumAcknowledgementBytes = 4 * 1024;
    private const int MaximumDeviceIdLength = 256;
    private const int MaximumDeviceKeyLength = 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(EnvironmentalObservationDeliveryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
    }

    public static byte[] Serialize(EnvironmentalObservationAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        return JsonSerializer.SerializeToUtf8Bytes(acknowledgement, SerializerOptions);
    }

    public static EnvironmentalObservationDeliveryParseResult<EnvironmentalObservationDeliveryEnvelope> ParseEnvelope(
        ReadOnlyMemory<byte> utf8Json)
        => Parse(
            utf8Json,
            MaximumEnvelopeBytes,
            Validate,
            static bytes => JsonSerializer.Deserialize<EnvironmentalObservationDeliveryEnvelope>(bytes.Span, SerializerOptions));

    public static EnvironmentalObservationDeliveryParseResult<EnvironmentalObservationAcknowledgement> ParseAcknowledgement(
        ReadOnlyMemory<byte> utf8Json)
        => Parse(
            utf8Json,
            MaximumAcknowledgementBytes,
            Validate,
            static bytes => JsonSerializer.Deserialize<EnvironmentalObservationAcknowledgement>(bytes.Span, SerializerOptions));

    public static EnvironmentalObservationValidationResult Validate(EnvironmentalObservationDeliveryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(
                envelope.SchemaVersion,
                EnvironmentalObservationDeliveryEnvelope.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(EnvironmentalObservationReasonCodes.UnsupportedSchema, nameof(envelope.SchemaVersion));
        }
        if (!Bounded(envelope.DeviceId, MaximumDeviceIdLength) || !Bounded(envelope.DeviceKey, MaximumDeviceKeyLength))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidIdentity, "device");
        }
        if (envelope.Observation is null)
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidIdentity, nameof(envelope.Observation));
        }
        var validation = EnvironmentalObservationJson.Validate(envelope.Observation);
        if (!validation.IsValid)
        {
            return validation;
        }
        return Serialize(envelope).Length <= MaximumEnvelopeBytes
            ? EnvironmentalObservationValidationResult.Success
            : Failure(EnvironmentalObservationReasonCodes.PayloadTooLarge, "$");
    }

    public static EnvironmentalObservationValidationResult Validate(EnvironmentalObservationAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!string.Equals(
                acknowledgement.SchemaVersion,
                EnvironmentalObservationAcknowledgement.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(EnvironmentalObservationReasonCodes.UnsupportedSchema, nameof(acknowledgement.SchemaVersion));
        }
        if (acknowledgement.ObservationId == Guid.Empty ||
            !UpperSha256(acknowledgement.SourceIdentitySha256) ||
            !UpperSha256(acknowledgement.ContentSha256))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidIdentity, "acknowledgement");
        }
        if (acknowledgement.ReceivedAtUtc == default || acknowledgement.ReceivedAtUtc.Offset != TimeSpan.Zero)
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidTime, nameof(acknowledgement.ReceivedAtUtc));
        }
        return Enum.IsDefined(acknowledgement.Disposition)
            ? EnvironmentalObservationValidationResult.Success
            : Failure(EnvironmentalObservationReasonCodes.InvalidValue, nameof(acknowledgement.Disposition));
    }

    public static bool Matches(
        EnvironmentalObservationAcknowledgement acknowledgement,
        EnvironmentalObservationV1 observation)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        ArgumentNullException.ThrowIfNull(observation);
        return Validate(acknowledgement).IsValid &&
            acknowledgement.ObservationId == observation.ObservationId &&
            string.Equals(
                acknowledgement.SourceIdentitySha256,
                EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation),
                StringComparison.Ordinal) &&
            string.Equals(
                acknowledgement.ContentSha256,
                EnvironmentalObservationJson.ComputeContentSha256(observation),
                StringComparison.Ordinal);
    }

    private static EnvironmentalObservationDeliveryParseResult<T> Parse<T>(
        ReadOnlyMemory<byte> utf8Json,
        int maximumBytes,
        Func<T, EnvironmentalObservationValidationResult> validate,
        Func<ReadOnlyMemory<byte>, T?> deserialize)
        where T : class
    {
        if (utf8Json.Length > maximumBytes)
        {
            return new(null, Failure(EnvironmentalObservationReasonCodes.PayloadTooLarge, "$"));
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (HasDuplicateProperties(document.RootElement))
            {
                return Invalid<T>();
            }
            var value = deserialize(utf8Json);
            if (value is null)
            {
                return Invalid<T>();
            }
            var validation = validate(value);
            return new(validation.IsValid ? value : null, validation);
        }
        catch (JsonException)
        {
            return Invalid<T>();
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool Bounded(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim();

    private static bool UpperSha256(string? value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static EnvironmentalObservationValidationResult Failure(string reason, string path)
        => EnvironmentalObservationValidationResult.Failure(reason, path);

    private static EnvironmentalObservationDeliveryParseResult<T> Invalid<T>() where T : class
        => new(null, Failure(EnvironmentalObservationReasonCodes.InvalidJson, "$"));

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
