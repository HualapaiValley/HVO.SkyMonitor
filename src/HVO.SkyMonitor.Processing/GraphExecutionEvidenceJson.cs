using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Strict canonical JSON, hashing, validation, redaction, sequencing, and version negotiation for the
/// CameraAgent graph-execution evidence export contract. Every member is stateless and safe for concurrent
/// callers. SHA-256 members are uppercase hexadecimal in canonical form; a lowercase value is rejected rather
/// than silently normalized, so one payload has exactly one canonical byte sequence and one hash.
/// </summary>
public static class GraphExecutionEvidenceJson
{
    /// <summary>The placeholder that occupies the payload hash member while the payload hash is computed.</summary>
    public const string UnhashedPayloadSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Prefix of a redacted operator-identifying value.</summary>
    public const string RedactionPrefix = "redacted:";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    // ---------------------------------------------------------------------------------------------------------
    // Identity and hashing
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Computes the canonical origin identity over every origin member except the identity itself.</summary>
    /// <param name="origin">Origin to hash; it is not mutated.</param>
    /// <returns>Uppercase hexadecimal SHA-256.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="origin"/> is null.</exception>
    public static string ComputeOriginIdentitySha256(ExecutionEvidenceOriginV1 origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(
            origin with { IdentitySha256 = UnhashedPayloadSha256 }, SerializerOptions));
    }

    /// <summary>Returns the origin with its canonical <c>IdentitySha256</c> bound.</summary>
    /// <param name="origin">Origin whose identity is recomputed.</param>
    /// <returns>A new origin carrying the canonical identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="origin"/> is null.</exception>
    public static ExecutionEvidenceOriginV1 BindIdentity(ExecutionEvidenceOriginV1 origin)
        => (origin ?? throw new ArgumentNullException(nameof(origin))) with
        {
            IdentitySha256 = ComputeOriginIdentitySha256(origin)
        };

    /// <summary>
    /// Computes the canonical payload hash over the whole envelope with the payload hash member replaced by
    /// <see cref="UnhashedPayloadSha256"/>. Two envelopes with the same hash are byte-identical after
    /// canonicalization, which makes duplicate and conflict detection exact.
    /// </summary>
    /// <param name="envelope">Envelope to hash; it is not mutated.</param>
    /// <returns>Uppercase hexadecimal SHA-256.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is null.</exception>
    public static string ComputeCanonicalPayloadSha256(ExecutionEvidenceEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(
            envelope with { PayloadSha256 = UnhashedPayloadSha256 }, SerializerOptions));
    }

    /// <summary>Validates the envelope and returns it with the canonical payload hash bound.</summary>
    /// <param name="envelope">Envelope to seal; it is not mutated.</param>
    /// <returns>A sealed envelope whose payload hash matches its canonical bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is null.</exception>
    /// <exception cref="ArgumentException">The envelope is invalid or exceeds a limit.</exception>
    public static ExecutionEvidenceEnvelopeV1 Seal(ExecutionEvidenceEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var candidate = envelope with { PayloadSha256 = UnhashedPayloadSha256 };
        var validation = Validate(candidate);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Execution evidence envelope is invalid ({validation.ReasonCode}, {validation.FieldPath}).",
                nameof(envelope));
        }
        return envelope with { PayloadSha256 = ComputeCanonicalPayloadSha256(envelope) };
    }

    /// <summary>Computes the redaction token that replaces one operator-identifying value.</summary>
    /// <param name="value">Value to redact.</param>
    /// <returns>A stable non-reversible token prefixed by <see cref="RedactionPrefix"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null or whitespace.</exception>
    public static string RedactionToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return string.Concat(
            RedactionPrefix,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
    }

    /// <summary>
    /// Applies a redaction policy before sealing. Redaction changes the canonical bytes, so the caller seals the
    /// returned envelope; a redacted unit and its unredacted original are never the same evidence unit.
    /// </summary>
    /// <param name="envelope">Envelope to redact; it is not mutated.</param>
    /// <param name="policy">Policy describing which operator-identifying members are replaced.</param>
    /// <returns>A redacted envelope carrying <paramref name="policy"/> and an unbound payload hash.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ExecutionEvidenceEnvelopeV1 Redact(
        ExecutionEvidenceEnvelopeV1 envelope,
        ExecutionEvidenceRedactionPolicyV1 policy)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(policy);
        var execution = envelope.Execution;
        if (execution is not null)
        {
            if (policy.RedactTriggerReferences && execution.TriggerReference is { Length: > 0 } reference &&
                !reference.StartsWith(RedactionPrefix, StringComparison.Ordinal))
            {
                execution = execution with { TriggerReference = RedactionToken(reference) };
            }
            if (policy.RedactLeaseOwners)
            {
                execution = execution with
                {
                    Nodes = [.. execution.Nodes.Select(static node => node with
                    {
                        Attempts = [.. node.Attempts.Select(static attempt =>
                            attempt.LeaseOwner is { Length: > 0 } owner &&
                            !owner.StartsWith(RedactionPrefix, StringComparison.Ordinal)
                                ? attempt with { LeaseOwner = RedactionToken(owner) }
                                : attempt)]
                    })]
                };
            }
        }
        return envelope with
        {
            Execution = execution,
            Redaction = policy,
            PayloadSha256 = UnhashedPayloadSha256
        };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Serialization
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Validates one sealed envelope and returns canonical UTF-8 JSON.</summary>
    /// <param name="value">Envelope to serialize; it is not mutated.</param>
    /// <returns>Canonical JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Validation fails or the canonical payload exceeds its limit.</exception>
    public static byte[] Serialize(ExecutionEvidenceEnvelopeV1 value)
        => SerializeCore(
            value, ValidateSealed, GraphExecutionEvidenceLimits.MaximumEnvelopeBytes, nameof(value));

    /// <summary>Validates one feedback message and returns canonical UTF-8 JSON.</summary>
    /// <param name="value">Feedback to serialize; it is not mutated.</param>
    /// <returns>Canonical JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Validation fails or the canonical payload exceeds its limit.</exception>
    public static byte[] Serialize(ExecutionEvidenceFeedbackV1 value)
        => SerializeCore(
            value, Validate, GraphExecutionEvidenceLimits.MaximumFeedbackBytes, nameof(value));

    /// <summary>Validates one bounded resynchronization request and returns canonical UTF-8 JSON.</summary>
    /// <param name="value">Request to serialize; it is not mutated.</param>
    /// <returns>Canonical JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Validation fails or the canonical payload exceeds its limit.</exception>
    public static byte[] Serialize(ExecutionEvidenceResyncRequestV1 value)
        => SerializeCore(
            value, Validate, GraphExecutionEvidenceLimits.MaximumResyncRequestBytes, nameof(value));

    /// <summary>Validates one negotiation request and returns canonical UTF-8 JSON.</summary>
    /// <param name="value">Request to serialize; it is not mutated.</param>
    /// <returns>Canonical JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Validation fails or the canonical payload exceeds its limit.</exception>
    public static byte[] Serialize(ExecutionEvidenceNegotiationRequestV1 value)
        => SerializeCore(
            value, Validate, GraphExecutionEvidenceLimits.MaximumNegotiationBytes, nameof(value));

    /// <summary>Validates one negotiation response and returns canonical UTF-8 JSON.</summary>
    /// <param name="value">Response to serialize; it is not mutated.</param>
    /// <returns>Canonical JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Validation fails or the canonical payload exceeds its limit.</exception>
    public static byte[] Serialize(ExecutionEvidenceNegotiationResponseV1 value)
        => SerializeCore(
            value, Validate, GraphExecutionEvidenceLimits.MaximumNegotiationBytes, nameof(value));

    /// <summary>
    /// Strictly parses one bounded evidence envelope. An unknown or future schema version is rejected before any
    /// member is interpreted, so a forward-incompatible payload is never partially applied.
    /// </summary>
    /// <param name="utf8Json">UTF-8 JSON bytes to parse.</param>
    /// <returns>A validated envelope, otherwise a stable non-throwing validation failure.</returns>
    public static ExecutionEvidenceParseResult<ExecutionEvidenceEnvelopeV1> ParseEnvelope(
        ReadOnlyMemory<byte> utf8Json)
        => Parse<ExecutionEvidenceEnvelopeV1>(
            utf8Json,
            GraphExecutionEvidenceLimits.MaximumEnvelopeBytes,
            ExecutionEvidenceEnvelopeV1.CurrentSchemaVersion,
            ValidateSealed);

    /// <summary>Strictly parses one bounded feedback message.</summary>
    /// <param name="utf8Json">UTF-8 JSON bytes to parse.</param>
    /// <returns>A validated feedback message, otherwise a stable non-throwing validation failure.</returns>
    public static ExecutionEvidenceParseResult<ExecutionEvidenceFeedbackV1> ParseFeedback(
        ReadOnlyMemory<byte> utf8Json)
        => Parse<ExecutionEvidenceFeedbackV1>(
            utf8Json,
            GraphExecutionEvidenceLimits.MaximumFeedbackBytes,
            ExecutionEvidenceFeedbackV1.CurrentSchemaVersion,
            Validate);

    /// <summary>Strictly parses one bounded resynchronization request.</summary>
    /// <param name="utf8Json">UTF-8 JSON bytes to parse.</param>
    /// <returns>A validated request, otherwise a stable non-throwing validation failure.</returns>
    public static ExecutionEvidenceParseResult<ExecutionEvidenceResyncRequestV1> ParseResyncRequest(
        ReadOnlyMemory<byte> utf8Json)
        => Parse<ExecutionEvidenceResyncRequestV1>(
            utf8Json,
            GraphExecutionEvidenceLimits.MaximumResyncRequestBytes,
            ExecutionEvidenceResyncRequestV1.CurrentSchemaVersion,
            Validate);

    /// <summary>Strictly parses one bounded negotiation request.</summary>
    /// <param name="utf8Json">UTF-8 JSON bytes to parse.</param>
    /// <returns>A validated request, otherwise a stable non-throwing validation failure.</returns>
    public static ExecutionEvidenceParseResult<ExecutionEvidenceNegotiationRequestV1> ParseNegotiationRequest(
        ReadOnlyMemory<byte> utf8Json)
        => Parse<ExecutionEvidenceNegotiationRequestV1>(
            utf8Json,
            GraphExecutionEvidenceLimits.MaximumNegotiationBytes,
            ExecutionEvidenceNegotiationRequestV1.CurrentSchemaVersion,
            Validate);

    /// <summary>Strictly parses one bounded negotiation response.</summary>
    /// <param name="utf8Json">UTF-8 JSON bytes to parse.</param>
    /// <returns>A validated response, otherwise a stable non-throwing validation failure.</returns>
    public static ExecutionEvidenceParseResult<ExecutionEvidenceNegotiationResponseV1> ParseNegotiationResponse(
        ReadOnlyMemory<byte> utf8Json)
        => Parse<ExecutionEvidenceNegotiationResponseV1>(
            utf8Json,
            GraphExecutionEvidenceLimits.MaximumNegotiationBytes,
            ExecutionEvidenceNegotiationResponseV1.CurrentSchemaVersion,
            Validate);

    // ---------------------------------------------------------------------------------------------------------
    // Version negotiation
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Selects the most preferred schema version both sides support and publishes this build's limits. When no
    /// version is shared the disposition is <see cref="ExecutionEvidenceNegotiationDisposition.Unsupported"/> and
    /// the producer must not send evidence.
    /// </summary>
    /// <param name="request">Producer negotiation request.</param>
    /// <param name="serverTimeUtc">Receiver clock reading recorded in the response.</param>
    /// <returns>The negotiation response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static ExecutionEvidenceNegotiationResponseV1 Negotiate(
        ExecutionEvidenceNegotiationRequestV1 request,
        DateTimeOffset serverTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var offered = request.SupportedSchemaVersions.IsDefault
            ? []
            : request.SupportedSchemaVersions;
        var selected = GraphExecutionEvidenceSchemaVersions.Supported
            .FirstOrDefault(supported => offered.Contains(supported, StringComparer.Ordinal));
        return new(
            ExecutionEvidenceNegotiationResponseV1.CurrentSchemaVersion,
            selected is null
                ? ExecutionEvidenceNegotiationDisposition.Unsupported
                : ExecutionEvidenceNegotiationDisposition.Supported,
            GraphExecutionEvidenceSchemaVersions.Supported,
            ExecutionEvidenceLimitsV1.Current,
            serverTimeUtc,
            selected,
            selected is null ? GraphExecutionEvidenceReasonCodes.UnsupportedSchema : null);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Sequencing, idempotency, conflict, gaps, and bounded resynchronization
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Produces the ordered facts a conformant receiver records for one submitted unit.
    /// <list type="bullet">
    /// <item>An unseen sequence yields <c>Received</c>, <c>Validated</c>, <c>Accepted</c>, <c>Acknowledged</c>.</item>
    /// <item>A repeat of the same sequence with the same payload hash yields the same four facts marked
    /// <c>Duplicate</c>; the stored unit is never rewritten.</item>
    /// <item>A repeat with a different payload hash yields exactly one <c>Rejected</c> fact carrying
    /// <see cref="GraphExecutionEvidenceReasonCodes.SequenceConflict"/> and the stored hash.</item>
    /// <item>An invalid unit yields <c>Received</c> followed by <c>Rejected</c> with the validation reason.</item>
    /// </list>
    /// </summary>
    /// <param name="envelope">Submitted envelope.</param>
    /// <param name="storedPayloadSha256">Payload hash already stored for this origin sequence, or null.</param>
    /// <param name="occurredAtUtc">Receiver clock reading recorded on every produced fact.</param>
    /// <returns>The ordered facts for this submission.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is null.</exception>
    public static ImmutableArray<ExecutionEvidenceFactV1> CreateReceiverFacts(
        ExecutionEvidenceEnvelopeV1 envelope,
        string? storedPayloadSha256,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (storedPayloadSha256 is not null &&
            !string.Equals(storedPayloadSha256, envelope.PayloadSha256, StringComparison.Ordinal))
        {
            return
            [
                Fact(envelope, ExecutionEvidenceFactKind.Rejected, occurredAtUtc, duplicate: false,
                    GraphExecutionEvidenceReasonCodes.SequenceConflict, "originSequence", storedPayloadSha256)
            ];
        }
        var validation = ValidateSealed(envelope);
        if (!validation.IsValid)
        {
            return
            [
                Fact(envelope, ExecutionEvidenceFactKind.Received, occurredAtUtc, duplicate: false),
                Fact(envelope, ExecutionEvidenceFactKind.Rejected, occurredAtUtc, duplicate: false,
                    validation.ReasonCode, validation.FieldPath)
            ];
        }
        var duplicate = storedPayloadSha256 is not null;
        return
        [
            Fact(envelope, ExecutionEvidenceFactKind.Received, occurredAtUtc, duplicate),
            Fact(envelope, ExecutionEvidenceFactKind.Validated, occurredAtUtc, duplicate),
            Fact(envelope, ExecutionEvidenceFactKind.Accepted, occurredAtUtc, duplicate),
            Fact(envelope, ExecutionEvidenceFactKind.Acknowledged, occurredAtUtc, duplicate)
        ];
    }

    /// <summary>
    /// Detects the bounded missing ranges between the contiguous accepted prefix and the highest observed
    /// sequence. Ranges are inclusive, ordered, and truncated at
    /// <see cref="GraphExecutionEvidenceLimits.MaximumMissingRanges"/>.
    /// </summary>
    /// <param name="contiguousThroughSequence">Highest sequence with no gap below it; zero when nothing arrived.</param>
    /// <param name="observedSequences">Sequences the receiver already accepted above the contiguous prefix.</param>
    /// <param name="truncated">Set when more ranges exist than the limit allows.</param>
    /// <returns>The ordered missing ranges.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observedSequences"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contiguousThroughSequence"/> is negative.</exception>
    public static ImmutableArray<ExecutionEvidenceSequenceRangeV1> DetectGaps(
        long contiguousThroughSequence,
        IEnumerable<long> observedSequences,
        out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(observedSequences);
        ArgumentOutOfRangeException.ThrowIfNegative(contiguousThroughSequence);
        var observed = observedSequences
            .Where(sequence => sequence > contiguousThroughSequence)
            .Distinct()
            .Order()
            .ToArray();
        truncated = false;
        if (observed.Length == 0)
        {
            return [];
        }
        var ranges = ImmutableArray.CreateBuilder<ExecutionEvidenceSequenceRangeV1>();
        var expected = contiguousThroughSequence + 1;
        foreach (var sequence in observed)
        {
            if (sequence > expected)
            {
                if (ranges.Count == GraphExecutionEvidenceLimits.MaximumMissingRanges)
                {
                    truncated = true;
                    break;
                }
                ranges.Add(new(
                    ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, expected, sequence - 1));
            }
            expected = sequence + 1;
        }
        return ranges.ToImmutable();
    }

    /// <summary>
    /// Builds a bounded resynchronization request from receiver feedback. The request is clipped to
    /// <see cref="GraphExecutionEvidenceLimits.MaximumResyncRanges"/> ranges and
    /// <see cref="GraphExecutionEvidenceLimits.MaximumResyncUnits"/> total units, so one large gap resynchronizes
    /// over several bounded requests instead of one unbounded replay.
    /// </summary>
    /// <param name="feedback">Receiver feedback containing the missing ranges.</param>
    /// <returns>A bounded request, or null when the feedback reports no gap.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="feedback"/> is null.</exception>
    public static ExecutionEvidenceResyncRequestV1? CreateResyncRequest(ExecutionEvidenceFeedbackV1 feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (feedback.MissingRanges.IsDefaultOrEmpty)
        {
            return null;
        }
        var ranges = ImmutableArray.CreateBuilder<ExecutionEvidenceSequenceRangeV1>();
        var remaining = (long)GraphExecutionEvidenceLimits.MaximumResyncUnits;
        foreach (var range in feedback.MissingRanges)
        {
            if (remaining <= 0 || ranges.Count == GraphExecutionEvidenceLimits.MaximumResyncRanges)
            {
                break;
            }
            var length = range.ToSequence - range.FromSequence + 1;
            var take = Math.Min(length, remaining);
            ranges.Add(range with { ToSequence = range.FromSequence + take - 1 });
            remaining -= take;
        }
        return ranges.Count == 0
            ? null
            : new(
                ExecutionEvidenceResyncRequestV1.CurrentSchemaVersion,
                feedback.OriginIdentitySha256,
                ranges.ToImmutable(),
                GraphExecutionEvidenceLimits.MaximumResyncUnits,
                GraphExecutionEvidenceReasonCodes.SequenceGap);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Validates one envelope without requiring a bound payload hash.</summary>
    /// <param name="value">Envelope to validate.</param>
    /// <returns>The first failure, otherwise success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static ExecutionEvidenceValidationResult Validate(ExecutionEvidenceEnvelopeV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(
                value.SchemaVersion, ExecutionEvidenceEnvelopeV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (value.EvidenceId == Guid.Empty)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidIdentity, "evidenceId");
        }
        var origin = ValidateOrigin(value.Origin, "origin");
        if (!origin.IsValid)
        {
            return origin;
        }
        if (value.OriginSequence <= 0)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidSequence, "originSequence");
        }
        if (!Utc(value.ProducedAtUtc))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidTime, "producedAtUtc");
        }
        if (!Enum.IsDefined(value.Kind))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, "kind");
        }
        if (!Sha256(value.PayloadSha256))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidHash, "payloadSha256");
        }
        if (value.Redaction is null || !string.Equals(
                value.Redaction.SchemaVersion,
                ExecutionEvidenceRedactionPolicyV1.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidRedaction, "redaction.schemaVersion");
        }
        var bodyCount = (value.GraphRevision is null ? 0 : 1) +
            (value.Execution is null ? 0 : 1) +
            (value.Availability is null ? 0 : 1);
        if (bodyCount != 1)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, "$");
        }
        var body = value.Kind switch
        {
            ExecutionEvidenceBodyKind.GraphRevision => value.GraphRevision is null
                ? Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, "graphRevision")
                : ValidateRevision(value.GraphRevision, "graphRevision"),
            ExecutionEvidenceBodyKind.GraphExecution => value.Execution is null
                ? Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, "execution")
                : ValidateExecution(value.Execution, "execution"),
            _ => value.Availability is null
                ? Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, "availability")
                : ValidateAvailability(value.Availability, "availability")
        };
        if (!body.IsValid)
        {
            return body;
        }
        return ValidateCorrection(value, "correction");
    }

    /// <summary>Validates one feedback message.</summary>
    /// <param name="value">Feedback to validate.</param>
    /// <returns>The first failure, otherwise success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static ExecutionEvidenceValidationResult Validate(ExecutionEvidenceFeedbackV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(
                value.SchemaVersion, ExecutionEvidenceFeedbackV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (!Sha256(value.OriginIdentitySha256))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidOrigin, "originIdentitySha256");
        }
        if (!Utc(value.ServerTimeUtc))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidTime, "serverTimeUtc");
        }
        if (value.ContiguousThroughSequence < 0)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidSequence, "contiguousThroughSequence");
        }
        var ranges = ValidateRanges(
            value.MissingRanges,
            value.ContiguousThroughSequence,
            GraphExecutionEvidenceLimits.MaximumMissingRanges,
            "missingRanges");
        if (!ranges.IsValid)
        {
            return ranges;
        }
        if (value.Facts.IsDefault || value.Facts.Length > GraphExecutionEvidenceLimits.MaximumFactsPerFeedback)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, "facts");
        }
        for (var index = 0; index < value.Facts.Length; index++)
        {
            var fact = ValidateFact(value.Facts[index], $"facts[{index}]");
            if (!fact.IsValid)
            {
                return fact;
            }
        }
        return ValidateRetention(value.Retention, "retention");
    }

    /// <summary>Validates one bounded resynchronization request.</summary>
    /// <param name="value">Request to validate.</param>
    /// <returns>The first failure, otherwise success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static ExecutionEvidenceValidationResult Validate(ExecutionEvidenceResyncRequestV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(
                value.SchemaVersion,
                ExecutionEvidenceResyncRequestV1.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (!Sha256(value.OriginIdentitySha256))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidOrigin, "originIdentitySha256");
        }
        var ranges = ValidateRanges(
            value.Ranges, 0, GraphExecutionEvidenceLimits.MaximumResyncRanges, "ranges");
        if (!ranges.IsValid)
        {
            return ranges;
        }
        if (value.Ranges.IsDefaultOrEmpty)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidRange, "ranges");
        }
        if (value.MaximumUnits is <= 0 or > GraphExecutionEvidenceLimits.MaximumResyncUnits)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, "maximumUnits");
        }
        var total = value.Ranges.Sum(range => range.ToSequence - range.FromSequence + 1);
        if (total > value.MaximumUnits)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, "ranges");
        }
        return ReasonCode(value.ReasonCode)
            ? ExecutionEvidenceValidationResult.Success
            : Failure(GraphExecutionEvidenceReasonCodes.InvalidFact, "reasonCode");
    }

    /// <summary>Validates one negotiation request.</summary>
    /// <param name="value">Request to validate.</param>
    /// <returns>The first failure, otherwise success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static ExecutionEvidenceValidationResult Validate(ExecutionEvidenceNegotiationRequestV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(
                value.SchemaVersion,
                ExecutionEvidenceNegotiationRequestV1.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        var origin = ValidateOrigin(value.Origin, "origin");
        if (!origin.IsValid)
        {
            return origin;
        }
        return ValidateSchemaVersionList(value.SupportedSchemaVersions, "supportedSchemaVersions");
    }

    /// <summary>Validates one negotiation response.</summary>
    /// <param name="value">Response to validate.</param>
    /// <returns>The first failure, otherwise success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static ExecutionEvidenceValidationResult Validate(ExecutionEvidenceNegotiationResponseV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(
                value.SchemaVersion,
                ExecutionEvidenceNegotiationResponseV1.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (!Enum.IsDefined(value.Disposition))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidNegotiation, "disposition");
        }
        var versions = ValidateSchemaVersionList(value.SupportedSchemaVersions, "supportedSchemaVersions");
        if (!versions.IsValid)
        {
            return versions;
        }
        if (!Utc(value.ServerTimeUtc))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidTime, "serverTimeUtc");
        }
        if (value.Limits is null || !string.Equals(
                value.Limits.SchemaVersion,
                ExecutionEvidenceLimitsV1.CurrentSchemaVersion,
                StringComparison.Ordinal) ||
            value.Limits != ExecutionEvidenceLimitsV1.Current)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidNegotiation, "limits");
        }
        return value.Disposition switch
        {
            ExecutionEvidenceNegotiationDisposition.Supported =>
                value.SelectedSchemaVersion is { Length: > 0 } selected &&
                value.SupportedSchemaVersions.Contains(selected, StringComparer.Ordinal) &&
                value.ReasonCode is null
                    ? ExecutionEvidenceValidationResult.Success
                    : Failure(GraphExecutionEvidenceReasonCodes.InvalidNegotiation, "selectedSchemaVersion"),
            _ => value.SelectedSchemaVersion is null && ReasonCode(value.ReasonCode)
                ? ExecutionEvidenceValidationResult.Success
                : Failure(GraphExecutionEvidenceReasonCodes.InvalidNegotiation, "reasonCode")
        };
    }

    private static ExecutionEvidenceValidationResult ValidateSealed(ExecutionEvidenceEnvelopeV1 value)
    {
        var validation = Validate(value);
        if (!validation.IsValid)
        {
            return validation;
        }
        return string.Equals(
                value.PayloadSha256, ComputeCanonicalPayloadSha256(value), StringComparison.Ordinal)
            ? ExecutionEvidenceValidationResult.Success
            : Failure(GraphExecutionEvidenceReasonCodes.InvalidHash, "payloadSha256");
    }

    private static ExecutionEvidenceValidationResult ValidateOrigin(
        ExecutionEvidenceOriginV1? value,
        string path)
    {
        if (value is null || !string.Equals(
                value.SchemaVersion, ExecutionEvidenceOriginV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        if (value.OriginInstallationId == Guid.Empty || value.AgentInstanceId == Guid.Empty ||
            value.BootSessionId == Guid.Empty ||
            value.ObservatoryId == Guid.Empty || value.LogicalCameraInstallationId == Guid.Empty ||
            value.InstallationPublicId == Guid.Empty)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidOrigin, $"{path}.originInstallationId");
        }
        if (!Bounded(value.SoftwareVersion, GraphExecutionEvidenceLimits.MaximumSoftwareVersionLength))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidOrigin, $"{path}.softwareVersion");
        }
        if (!Sha256(value.IdentitySha256) || !string.Equals(
                value.IdentitySha256, ComputeOriginIdentitySha256(value), StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidOrigin, $"{path}.identitySha256");
        }
        return ExecutionEvidenceValidationResult.Success;
    }

    private static ExecutionEvidenceValidationResult ValidateRevision(GraphRevisionEvidenceV1 value, string path)
    {
        if (!string.Equals(
                value.SchemaVersion, GraphRevisionEvidenceV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        if (!Sha256(value.RevisionId) || !Sha256(value.DefinitionIdentitySha256) ||
            !Sha256(value.SharedPlanIdentitySha256) || !Sha256(value.LocalPlanIdentitySha256))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidHash, $"{path}.revisionId");
        }
        if (!Bounded(value.Name, GraphExecutionEvidenceLimits.MaximumIdentifierLength) ||
            !Bounded(value.Revision, GraphExecutionEvidenceLimits.MaximumIdentifierLength))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidIdentity, $"{path}.name");
        }
        if (!Enum.IsDefined(value.Origin))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, $"{path}.origin");
        }
        if (value.CanonicalDefinition.ValueKind != JsonValueKind.Object)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, $"{path}.canonicalDefinition");
        }
        if (value.FrozenPlan.ValueKind != JsonValueKind.Object)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, $"{path}.frozenPlan");
        }
        if (CanonicalLength(value.CanonicalDefinition) > GraphExecutionEvidenceLimits.MaximumDefinitionBytes)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, $"{path}.canonicalDefinition");
        }
        if (CanonicalLength(value.FrozenPlan) > GraphExecutionEvidenceLimits.MaximumFrozenPlanBytes)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, $"{path}.frozenPlan");
        }
        if (!string.Equals(
                value.DefinitionIdentitySha256,
                CaptureContractJson.ComputeCanonicalJsonSha256(value.CanonicalDefinition),
                StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidHash, $"{path}.definitionIdentitySha256");
        }
        if (!Utc(value.CreatedUtc) || !OptionalUtc(value.ValidatedUtc) || !OptionalUtc(value.ActivatedUtc) ||
            !OptionalUtc(value.RetiredUtc))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidTime, $"{path}.createdUtc");
        }
        return value.Origin switch
        {
            ExecutionEvidenceRevisionOrigin.LocalOnly => value.Assignment is null
                ? ExecutionEvidenceValidationResult.Success
                : Failure(GraphExecutionEvidenceReasonCodes.InvalidAssignment, $"{path}.assignment"),
            _ => ValidateAssignment(value.Assignment, $"{path}.assignment")
        };
    }

    private static ExecutionEvidenceValidationResult ValidateAssignment(
        ExecutionEvidenceAssignmentProvenanceV1? value,
        string path)
    {
        if (value is null || !string.Equals(
                value.SchemaVersion,
                ExecutionEvidenceAssignmentProvenanceV1.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidAssignment, $"{path}.schemaVersion");
        }
        if (value.ProposalId == Guid.Empty || value.CatalogRevisionId == Guid.Empty ||
            value.AssignmentId == Guid.Empty || value.RegistrationId == Guid.Empty ||
            value.LogicalCameraInstallationId == Guid.Empty || value.InstallationPublicId == Guid.Empty)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidAssignment, $"{path}.proposalId");
        }
        if (!Sha256(value.CapabilitySnapshotSha256))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidHash, $"{path}.capabilitySnapshotSha256");
        }
        return Utc(value.IssuedAtUtc) && Utc(value.AcceptedAtUtc) && value.AcceptedAtUtc >= value.IssuedAtUtc
            ? ExecutionEvidenceValidationResult.Success
            : Failure(GraphExecutionEvidenceReasonCodes.InvalidTime, $"{path}.issuedAtUtc");
    }

    private static ExecutionEvidenceValidationResult ValidateExecution(GraphExecutionEvidenceV1 value, string path)
    {
        if (!string.Equals(
                value.SchemaVersion, GraphExecutionEvidenceV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        if (value.ExecutionId == Guid.Empty || value.CaptureId == Guid.Empty ||
            value.PrimaryArtifactId == Guid.Empty)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidIdentity, $"{path}.executionId");
        }
        if (!Enum.IsDefined(value.ExecutionClass) || !Enum.IsDefined(value.Status))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, $"{path}.status");
        }
        if (!Sha256(value.GraphRevisionId) || !Sha256(value.DefinitionIdentitySha256) ||
            !Sha256(value.SharedPlanIdentitySha256) || !Sha256(value.LocalPlanIdentitySha256))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidHash, $"{path}.graphRevisionId");
        }
        if (!Bounded(value.TriggerKind, GraphExecutionEvidenceLimits.MaximumIdentifierLength) ||
            !OptionalBounded(value.TriggerReference, GraphExecutionEvidenceLimits.MaximumIdentifierLength) ||
            value.Priority is < -1000 or > 1000 || value.AttemptCount < 0)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, $"{path}.triggerKind");
        }
        if (!Utc(value.AcceptedUtc) || !Utc(value.AvailableUtc) || !Utc(value.DeadlineUtc) ||
            !Utc(value.MaximumAgeUtc) || !OptionalUtc(value.StartedUtc) || !OptionalUtc(value.CompletedUtc))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidTime, $"{path}.acceptedUtc");
        }
        if (!OptionalReasonCode(value.FailureReasonCode))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidBody, $"{path}.failureReasonCode");
        }
        if (value.Nodes.IsDefault || value.Nodes.Length > GraphExecutionEvidenceLimits.MaximumNodeCount)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, $"{path}.nodes");
        }
        if (value.Nodes.Select(static node => node.NodeId).Distinct(StringComparer.Ordinal).Count() !=
            value.Nodes.Length)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidNode, $"{path}.nodes");
        }
        var inputs = 0;
        var outputs = 0;
        for (var index = 0; index < value.Nodes.Length; index++)
        {
            var node = value.Nodes[index];
            var result = ValidateNode(node, $"{path}.nodes[{index}]");
            if (!result.IsValid)
            {
                return result;
            }
            inputs += node.Inputs.Length;
            outputs += node.Outputs.Length;
        }
        if (inputs > GraphExecutionEvidenceLimits.MaximumInputsPerExecution)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, $"{path}.nodes");
        }
        return outputs > GraphExecutionEvidenceLimits.MaximumOutputsPerExecution
            ? Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, $"{path}.nodes")
            : ExecutionEvidenceValidationResult.Success;
    }

    private static ExecutionEvidenceValidationResult ValidateNode(ExecutionEvidenceNodeV1 value, string path)
    {
        if (value is null || !string.Equals(
                value.SchemaVersion, ExecutionEvidenceNodeV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        if (!Bounded(value.NodeId, GraphExecutionEvidenceLimits.MaximumIdentifierLength) ||
            !Sha256(value.PlanSha256) || !Enum.IsDefined(value.Status) ||
            !OptionalReasonCode(value.ReasonCode) ||
            !OptionalUtc(value.StartedUtc) || !OptionalUtc(value.CompletedUtc))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidNode, $"{path}.nodeId");
        }
        if (value.Inputs.IsDefault || value.Inputs.Length > GraphExecutionEvidenceLimits.MaximumInputsPerNode ||
            value.Outputs.IsDefault || value.Outputs.Length > GraphExecutionEvidenceLimits.MaximumOutputsPerNode ||
            value.Attempts.IsDefault ||
            value.Attempts.Length > GraphExecutionEvidenceLimits.MaximumAttemptsPerNode)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, $"{path}.inputs");
        }
        var previousInputOrdinal = -1;
        var previousOutputOrdinal = -1;
        var previousAttemptNumber = 0;
        for (var index = 0; index < value.Inputs.Length; index++)
        {
            var input = value.Inputs[index];
            if (input is null || !string.Equals(
                    input.SchemaVersion, ExecutionEvidenceInputV1.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.inputs[{index}]");
            }
            if (input.Ordinal <= previousInputOrdinal ||
                input.Ordinal >= GraphExecutionEvidenceLimits.MaximumInputsPerNode ||
                !Enum.IsDefined(input.Kind))
            {
                return Failure(GraphExecutionEvidenceReasonCodes.InvalidInput, $"{path}.inputs[{index}]");
            }
            previousInputOrdinal = input.Ordinal;
            var artifact = ValidateArtifact(input.Artifact, $"{path}.inputs[{index}].artifact");
            if (!artifact.IsValid)
            {
                return artifact;
            }
            if ((input.Kind == ExecutionEvidenceInputKind.ProcessingOutput) !=
                (input.Artifact.OutputIdentitySha256 is not null))
            {
                return Failure(GraphExecutionEvidenceReasonCodes.InvalidInput, $"{path}.inputs[{index}].kind");
            }
        }
        for (var index = 0; index < value.Outputs.Length; index++)
        {
            var output = value.Outputs[index];
            if (output is null || !string.Equals(
                    output.SchemaVersion, ExecutionEvidenceOutputV1.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.outputs[{index}]");
            }
            if (output.Ordinal <= previousOutputOrdinal ||
                output.Ordinal >= GraphExecutionEvidenceLimits.MaximumOutputsPerNode ||
                output.Artifact?.OutputIdentitySha256 is null)
            {
                return Failure(GraphExecutionEvidenceReasonCodes.InvalidOutput, $"{path}.outputs[{index}]");
            }
            previousOutputOrdinal = output.Ordinal;
            var artifact = ValidateArtifact(output.Artifact, $"{path}.outputs[{index}].artifact");
            if (!artifact.IsValid)
            {
                return artifact;
            }
        }
        for (var index = 0; index < value.Attempts.Length; index++)
        {
            var attempt = value.Attempts[index];
            if (attempt is null || !string.Equals(
                    attempt.SchemaVersion,
                    ExecutionEvidenceAttemptV1.CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.attempts[{index}]");
            }
            if (attempt.AttemptNumber <= previousAttemptNumber ||
                attempt.AttemptNumber > GraphExecutionEvidenceLimits.MaximumAttemptsPerNode ||
                !Enum.IsDefined(attempt.Status) ||
                (attempt.Outcome is { } outcome && !Enum.IsDefined(outcome)) ||
                !Utc(attempt.StartedUtc) || !OptionalUtc(attempt.CompletedUtc) ||
                !OptionalReasonCode(attempt.ReasonCode) ||
                attempt.DurationTicks is < 0 ||
                !OptionalBounded(attempt.LeaseOwner, GraphExecutionEvidenceLimits.MaximumIdentifierLength))
            {
                return Failure(GraphExecutionEvidenceReasonCodes.InvalidAttempt, $"{path}.attempts[{index}]");
            }
            previousAttemptNumber = attempt.AttemptNumber;
        }
        return ExecutionEvidenceValidationResult.Success;
    }

    private static ExecutionEvidenceValidationResult ValidateArtifact(
        ExecutionEvidenceArtifactReferenceV1? value,
        string path)
    {
        if (value is null || !string.Equals(
                value.SchemaVersion,
                ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        if (value.ArtifactId == Guid.Empty || value.CaptureId == Guid.Empty)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidIdentity, $"{path}.artifactId");
        }
        if (!OptionalSha256(value.OutputIdentitySha256) || !OptionalSha256(value.PayloadSha256) ||
            !OptionalSha256(value.DescriptorSha256) ||
            (value.OutputIdentitySha256 is null && value.DescriptorSha256 is null))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidHash, $"{path}.outputIdentitySha256");
        }
        if (value.OutputIdentitySha256 is { } outputIdentity &&
            value.ArtifactId != ProcessingIdentity.CreateArtifactId(outputIdentity))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidIdentity, $"{path}.artifactId");
        }
        return (value.Role is not { } role || Enum.IsDefined(role)) &&
            OptionalBounded(value.Variant, GraphExecutionEvidenceLimits.MaximumIdentifierLength) &&
            value.PayloadLength is null or >= 0 &&
            OptionalBounded(value.MediaType, GraphExecutionEvidenceLimits.MaximumMediaTypeLength)
            ? ExecutionEvidenceValidationResult.Success
            : Failure(GraphExecutionEvidenceReasonCodes.InvalidOutput, $"{path}.role");
    }

    private static ExecutionEvidenceValidationResult ValidateAvailability(
        ArtifactAvailabilityReportV1 value,
        string path)
    {
        if (!string.Equals(
                value.SchemaVersion, ArtifactAvailabilityReportV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        if (value.ExecutionId == Guid.Empty)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidIdentity, $"{path}.executionId");
        }
        if (value.Observations.IsDefaultOrEmpty ||
            value.Observations.Length > GraphExecutionEvidenceLimits.MaximumAvailabilityObservations)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, $"{path}.observations");
        }
        for (var index = 0; index < value.Observations.Length; index++)
        {
            var observation = value.Observations[index];
            if (observation is null || !string.Equals(
                    observation.SchemaVersion,
                    ArtifactAvailabilityObservationV1.CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                return Failure(
                    GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.observations[{index}]");
            }
            if (!Enum.IsDefined(observation.State) || !Utc(observation.ObservedAtUtc) ||
                !OptionalReasonCode(observation.ReasonCode) ||
                (observation.State == ExecutionEvidenceAvailabilityState.Available &&
                    observation.ReasonCode is not null))
            {
                return Failure(
                    GraphExecutionEvidenceReasonCodes.InvalidAvailability, $"{path}.observations[{index}]");
            }
            var artifact = ValidateArtifact(
                observation.Artifact, $"{path}.observations[{index}].artifact");
            if (!artifact.IsValid)
            {
                return artifact;
            }
        }
        return value.Observations
                .Select(static observation => observation.Artifact.ArtifactId)
                .Distinct()
                .Count() == value.Observations.Length
            ? ExecutionEvidenceValidationResult.Success
            : Failure(GraphExecutionEvidenceReasonCodes.InvalidAvailability, $"{path}.observations");
    }

    private static ExecutionEvidenceValidationResult ValidateCorrection(
        ExecutionEvidenceEnvelopeV1 envelope,
        string path)
    {
        var value = envelope.Correction;
        if (value is null)
        {
            return ExecutionEvidenceValidationResult.Success;
        }
        if (envelope.Kind == ExecutionEvidenceBodyKind.ArtifactAvailability)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidCorrection, path);
        }
        if (!string.Equals(
                value.SchemaVersion,
                ExecutionEvidenceCorrectionV1.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        if (value.CorrectsEvidenceId == Guid.Empty || value.CorrectsEvidenceId == envelope.EvidenceId)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidCorrection, $"{path}.correctsEvidenceId");
        }
        if (value.CorrectsOriginSequence <= 0 || value.CorrectsOriginSequence >= envelope.OriginSequence)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidCorrection, $"{path}.correctsOriginSequence");
        }
        return ReasonCode(value.ReasonCode)
            ? ExecutionEvidenceValidationResult.Success
            : Failure(GraphExecutionEvidenceReasonCodes.InvalidCorrection, $"{path}.reasonCode");
    }

    private static ExecutionEvidenceValidationResult ValidateFact(ExecutionEvidenceFactV1? value, string path)
    {
        if (value is null || !string.Equals(
                value.SchemaVersion, ExecutionEvidenceFactV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        if (!Enum.IsDefined(value.Kind) || value.EvidenceId == Guid.Empty || value.OriginSequence <= 0 ||
            !Sha256(value.PayloadSha256) || !Utc(value.OccurredAtUtc))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidFact, path);
        }
        if (!OptionalReasonCode(value.ReasonCode) ||
            !OptionalBounded(value.FieldPath, GraphExecutionEvidenceLimits.MaximumIdentifierLength) ||
            !OptionalSha256(value.StoredPayloadSha256))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidFact, $"{path}.reasonCode");
        }
        if (value.Kind == ExecutionEvidenceFactKind.Rejected)
        {
            if (value.ReasonCode is null || value.Duplicate)
            {
                return Failure(GraphExecutionEvidenceReasonCodes.InvalidFact, $"{path}.reasonCode");
            }
            return (value.StoredPayloadSha256 is not null) == string.Equals(
                    value.ReasonCode,
                    GraphExecutionEvidenceReasonCodes.SequenceConflict,
                    StringComparison.Ordinal)
                ? ExecutionEvidenceValidationResult.Success
                : Failure(GraphExecutionEvidenceReasonCodes.InvalidFact, $"{path}.storedPayloadSha256");
        }
        return value.ReasonCode is null && value.FieldPath is null && value.StoredPayloadSha256 is null
            ? ExecutionEvidenceValidationResult.Success
            : Failure(GraphExecutionEvidenceReasonCodes.InvalidFact, $"{path}.reasonCode");
    }

    private static ExecutionEvidenceValidationResult ValidateRetention(
        ExecutionEvidenceRetentionV1? value,
        string path)
    {
        if (value is null || !string.Equals(
                value.SchemaVersion, ExecutionEvidenceRetentionV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        return value.AcknowledgedThroughSequence >= 0 &&
            value.MaximumRetainedAcknowledgements > 0 &&
            Utc(value.AcknowledgementsRetainedUntilUtc)
            ? ExecutionEvidenceValidationResult.Success
            : Failure(GraphExecutionEvidenceReasonCodes.InvalidRetention, path);
    }

    private static ExecutionEvidenceValidationResult ValidateRanges(
        ImmutableArray<ExecutionEvidenceSequenceRangeV1> ranges,
        long minimumExclusive,
        int maximumCount,
        string path)
    {
        if (ranges.IsDefault || ranges.Length > maximumCount)
        {
            return Failure(GraphExecutionEvidenceReasonCodes.LimitExceeded, path);
        }
        var previous = minimumExclusive;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range is null || !string.Equals(
                    range.SchemaVersion,
                    ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                return Failure(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, $"{path}[{index}]");
            }
            if (range.FromSequence <= previous || range.ToSequence < range.FromSequence)
            {
                return Failure(GraphExecutionEvidenceReasonCodes.InvalidRange, $"{path}[{index}]");
            }
            previous = range.ToSequence;
        }
        return ExecutionEvidenceValidationResult.Success;
    }

    private static ExecutionEvidenceValidationResult ValidateSchemaVersionList(
        ImmutableArray<string> versions,
        string path)
    {
        if (versions.IsDefaultOrEmpty ||
            versions.Length > GraphExecutionEvidenceLimits.MaximumSupportedSchemaVersions ||
            versions.Distinct(StringComparer.Ordinal).Count() != versions.Length ||
            versions.Any(static version =>
                !Bounded(version, GraphExecutionEvidenceLimits.MaximumIdentifierLength)))
        {
            return Failure(GraphExecutionEvidenceReasonCodes.InvalidNegotiation, path);
        }
        return ExecutionEvidenceValidationResult.Success;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------------------------------------

    private static ExecutionEvidenceFactV1 Fact(
        ExecutionEvidenceEnvelopeV1 envelope,
        ExecutionEvidenceFactKind kind,
        DateTimeOffset occurredAtUtc,
        bool duplicate,
        string? reasonCode = null,
        string? fieldPath = null,
        string? storedPayloadSha256 = null)
        => new(
            ExecutionEvidenceFactV1.CurrentSchemaVersion,
            kind,
            envelope.EvidenceId,
            envelope.OriginSequence,
            envelope.PayloadSha256,
            occurredAtUtc,
            duplicate,
            reasonCode,
            fieldPath,
            storedPayloadSha256);

    private static byte[] SerializeCore<T>(
        T value,
        Func<T, ExecutionEvidenceValidationResult> validate,
        int maximumBytes,
        string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var validation = validate(value);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Execution evidence contract is invalid ({validation.ReasonCode}, {validation.FieldPath}).",
                parameterName);
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(
            CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value, SerializerOptions)));
        return json.Length > maximumBytes
            ? throw new ArgumentException(
                "Execution evidence contract exceeds the maximum payload size.", parameterName)
            : json;
    }

    private static ExecutionEvidenceParseResult<T> Parse<T>(
        ReadOnlyMemory<byte> utf8Json,
        int maximumBytes,
        string expectedSchemaVersion,
        Func<T, ExecutionEvidenceValidationResult> validate)
        where T : class
    {
        if (utf8Json.Length > maximumBytes)
        {
            return ParseFailure<T>(GraphExecutionEvidenceReasonCodes.PayloadTooLarge, "$");
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                HasDuplicateProperties(document.RootElement))
            {
                return ParseFailure<T>(GraphExecutionEvidenceReasonCodes.InvalidJson, "$");
            }
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String)
            {
                return ParseFailure<T>(GraphExecutionEvidenceReasonCodes.InvalidJson, "schemaVersion");
            }
            if (!string.Equals(schema.GetString(), expectedSchemaVersion, StringComparison.Ordinal))
            {
                return ParseFailure<T>(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, "schemaVersion");
            }
            var value = document.RootElement.Deserialize<T>(SerializerOptions);
            if (value is null)
            {
                return ParseFailure<T>(GraphExecutionEvidenceReasonCodes.InvalidJson, "$");
            }
            var validation = validate(value);
            return new(validation.IsValid ? value : null, validation);
        }
        catch (JsonException)
        {
            return ParseFailure<T>(GraphExecutionEvidenceReasonCodes.InvalidJson, "$");
        }
        catch (OverflowException)
        {
            return ParseFailure<T>(GraphExecutionEvidenceReasonCodes.InvalidJson, "$");
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasDuplicateProperties(item))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static int CanonicalLength(JsonElement value)
        => JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(value)).Length;

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool OptionalUtc(DateTimeOffset? value) => value is null || Utc(value.Value);

    private static bool Sha256(string? value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool OptionalSha256(string? value) => value is null || Sha256(value);

    private static bool Bounded(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool OptionalBounded(string? value, int maximum) => value is null || Bounded(value, maximum);

    private static bool ReasonCode(string? value)
        => Bounded(value, GraphExecutionEvidenceLimits.MaximumReasonCodeLength);

    private static bool OptionalReasonCode(string? value) => value is null || ReasonCode(value);

    private static ExecutionEvidenceValidationResult Failure(string reasonCode, string path)
        => ExecutionEvidenceValidationResult.Failure(reasonCode, path);

    private static ExecutionEvidenceParseResult<T> ParseFailure<T>(string reasonCode, string path)
        where T : class
        => new(null, Failure(reasonCode, path));

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
