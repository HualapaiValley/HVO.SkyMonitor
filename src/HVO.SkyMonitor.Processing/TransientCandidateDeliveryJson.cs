using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public sealed record TransientDeliveryParseResult<T>(
    T? Value,
    TransientContractValidationResult Validation)
    where T : class;

/// <summary>Strict canonical serialization and identity validation for transient handoff messages.</summary>
public static class TransientCandidateDeliveryJson
{
    public const int MaximumSubmissionBytes = TransientContractJson.MaximumCandidateBytes + 8 * 1024;
    public const int MaximumAcknowledgementBytes = 4 * 1024;
    public const int MaximumFinalizationBytes = TransientContractJson.MaximumEventBytes + 4 * 1024;
    private const int MaximumIdentityLength = 256;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(TransientCandidateSubmissionEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var validation = Validate(envelope);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Transient submission is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(envelope));
        }
        return JsonSerializer.SerializeToUtf8Bytes(Normalize(envelope), SerializerOptions);
    }

    public static byte[] Serialize(TransientCandidateSubmissionAcknowledgementV1 acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        var validation = Validate(acknowledgement);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Transient acknowledgement is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(acknowledgement));
        }
        return JsonSerializer.SerializeToUtf8Bytes(acknowledgement, SerializerOptions);
    }

    public static byte[] Serialize(TransientFinalizationReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var validation = Validate(receipt);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Transient finalization receipt is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(receipt));
        }
        return JsonSerializer.SerializeToUtf8Bytes(Normalize(receipt), SerializerOptions);
    }

    public static string ComputeSubmissionIdentitySha256(TransientCandidateSubmissionEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var candidate = ParseCanonicalCandidate(envelope.Candidate);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "hvo-transient-candidate-submission-identity-v1",
            envelope.SchemaVersion,
            envelope.CandidateId,
            envelope.EventId,
            Candidate = candidate,
            RequestedRecipeIdentitySha256 = envelope.RequestedRecipeIdentitySha256.ToUpperInvariant(),
            envelope.RequestedProcessingProfileIdentity
        });
    }

    public static string ComputeFinalizationIdentitySha256(TransientFinalizationReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var transientEvent = ParseCanonicalEvent(receipt.Event);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "hvo-transient-finalization-receipt-identity-v1",
            receipt.SchemaVersion,
            receipt.CandidateId,
            receipt.EventId,
            Event = transientEvent
        });
    }

    public static TransientContractValidationResult Validate(TransientCandidateSubmissionEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(envelope.SchemaVersion, TransientCandidateSubmissionEnvelopeV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(TransientContractReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (envelope.CandidateId == Guid.Empty || envelope.EventId == Guid.Empty || envelope.Candidate is null ||
            envelope.Candidate.CandidateId != envelope.CandidateId || envelope.Candidate.EventId != envelope.EventId)
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "candidateId");
        }
        var candidateValidation = TransientContractJson.Validate(envelope.Candidate);
        if (!candidateValidation.IsValid)
        {
            return candidateValidation;
        }
        if (!UpperSha256(envelope.RequestedRecipeIdentitySha256) ||
            !Bounded(envelope.RequestedProcessingProfileIdentity, MaximumIdentityLength) ||
            !UpperSha256(envelope.SubmissionIdentitySha256))
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "submissionIdentitySha256");
        }
        return string.Equals(
            ComputeSubmissionIdentitySha256(envelope), envelope.SubmissionIdentitySha256, StringComparison.Ordinal)
            ? Size(envelope, MaximumSubmissionBytes)
            : Failure(TransientContractReasonCodes.InvalidIdentity, "submissionIdentitySha256");
    }

    public static TransientContractValidationResult Validate(TransientCandidateSubmissionAcknowledgementV1 acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!Enum.IsDefined(acknowledgement.Disposition))
        {
            return Failure(TransientContractReasonCodes.InvalidState, "disposition");
        }
        var expectedSchema = acknowledgement.Disposition == TransientCandidateSubmissionDisposition.Retired
            ? TransientCandidateSubmissionAcknowledgementV1.RetirementSchemaVersion
            : TransientCandidateSubmissionAcknowledgementV1.CurrentSchemaVersion;
        if (!string.Equals(
                acknowledgement.SchemaVersion,
                expectedSchema,
                StringComparison.Ordinal))
        {
            return Failure(TransientContractReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (acknowledgement.CandidateId == Guid.Empty || acknowledgement.EventId == Guid.Empty ||
            !UpperSha256(acknowledgement.SubmissionIdentitySha256))
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "submissionIdentitySha256");
        }
        if (acknowledgement.ReceivedAtUtc == default || acknowledgement.ReceivedAtUtc.Offset != TimeSpan.Zero)
        {
            return Failure(TransientContractReasonCodes.InvalidTime, "receivedAtUtc");
        }
        return Size(acknowledgement, MaximumAcknowledgementBytes);
    }

    public static TransientContractValidationResult Validate(TransientFinalizationReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.Equals(receipt.SchemaVersion, TransientFinalizationReceiptV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(TransientContractReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (receipt.CandidateId == Guid.Empty || receipt.EventId == Guid.Empty || receipt.Event is null ||
            receipt.Event.EventId != receipt.EventId || !UpperSha256(receipt.ReceiptIdentitySha256))
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "receiptIdentitySha256");
        }
        var eventValidation = TransientContractJson.Validate(receipt.Event);
        if (!eventValidation.IsValid)
        {
            return eventValidation;
        }
        if (!receipt.Event.Observations.Any(observation =>
                observation.Extraction.OriginatingCandidateId == receipt.CandidateId))
        {
            return Failure(
                TransientContractReasonCodes.InvalidLineage,
                "event.observations[].extraction.originatingCandidateId");
        }
        if (receipt.Event.State is not (TransientEventState.Validated or TransientEventState.Rejected or TransientEventState.NeedsReview))
        {
            return Failure(TransientContractReasonCodes.InvalidState, "event.state");
        }
        return string.Equals(
            ComputeFinalizationIdentitySha256(receipt), receipt.ReceiptIdentitySha256, StringComparison.Ordinal)
            ? Size(receipt, MaximumFinalizationBytes)
            : Failure(TransientContractReasonCodes.InvalidIdentity, "receiptIdentitySha256");
    }

    public static bool Matches(
        TransientCandidateSubmissionAcknowledgementV1 acknowledgement,
        TransientCandidateSubmissionEnvelopeV1 envelope)
        => Validate(acknowledgement).IsValid && Validate(envelope).IsValid &&
           acknowledgement.CandidateId == envelope.CandidateId &&
           acknowledgement.EventId == envelope.EventId &&
           string.Equals(
               acknowledgement.SubmissionIdentitySha256,
               envelope.SubmissionIdentitySha256,
               StringComparison.Ordinal);

    public static TransientDeliveryParseResult<TransientCandidateSubmissionEnvelopeV1> ParseSubmission(
        ReadOnlyMemory<byte> utf8Json)
        => Parse(
            utf8Json,
            MaximumSubmissionBytes,
            Validate,
            static bytes => JsonSerializer.Deserialize<TransientCandidateSubmissionEnvelopeV1>(bytes.Span, SerializerOptions));

    public static TransientDeliveryParseResult<TransientCandidateSubmissionAcknowledgementV1> ParseAcknowledgement(
        ReadOnlyMemory<byte> utf8Json)
        => Parse(
            utf8Json,
            MaximumAcknowledgementBytes,
            Validate,
            static bytes => JsonSerializer.Deserialize<TransientCandidateSubmissionAcknowledgementV1>(bytes.Span, SerializerOptions));

    public static TransientDeliveryParseResult<TransientFinalizationReceiptV1> ParseFinalization(
        ReadOnlyMemory<byte> utf8Json)
        => Parse(
            utf8Json,
            MaximumFinalizationBytes,
            Validate,
            static bytes => JsonSerializer.Deserialize<TransientFinalizationReceiptV1>(bytes.Span, SerializerOptions));

    private static TransientCandidateSubmissionEnvelopeV1 Normalize(TransientCandidateSubmissionEnvelopeV1 envelope)
        => envelope with
        {
            Candidate = ParseCanonicalCandidate(envelope.Candidate),
            RequestedRecipeIdentitySha256 = envelope.RequestedRecipeIdentitySha256.ToUpperInvariant(),
            SubmissionIdentitySha256 = envelope.SubmissionIdentitySha256.ToUpperInvariant()
        };

    private static TransientFinalizationReceiptV1 Normalize(TransientFinalizationReceiptV1 receipt)
        => receipt with
        {
            Event = ParseCanonicalEvent(receipt.Event),
            ReceiptIdentitySha256 = receipt.ReceiptIdentitySha256.ToUpperInvariant()
        };

    private static TransientCandidateV1 ParseCanonicalCandidate(TransientCandidateV1 candidate)
        => TransientContractJson.ParseCandidate(TransientContractJson.Serialize(candidate)).Value!;

    private static TransientEventV1 ParseCanonicalEvent(TransientEventV1 transientEvent)
        => TransientContractJson.ParseEvent(TransientContractJson.Serialize(transientEvent)).Value!;

    private static TransientDeliveryParseResult<T> Parse<T>(
        ReadOnlyMemory<byte> utf8Json,
        int maximumBytes,
        Func<T, TransientContractValidationResult> validate,
        Func<ReadOnlyMemory<byte>, T?> deserialize)
        where T : class
    {
        if (utf8Json.Length > maximumBytes)
        {
            return new(null, Failure(TransientContractReasonCodes.PayloadTooLarge, "$"));
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

    private static TransientContractValidationResult Size<T>(T value, int maximum)
        => JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions).Length <= maximum
            ? TransientContractValidationResult.Success
            : Failure(TransientContractReasonCodes.PayloadTooLarge, "$");

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

    private static TransientContractValidationResult Failure(string reason, string path)
        => TransientContractValidationResult.Failure(reason, path);

    private static TransientDeliveryParseResult<T> Invalid<T>() where T : class
        => new(null, Failure(TransientContractReasonCodes.InvalidJson, "$"));

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
