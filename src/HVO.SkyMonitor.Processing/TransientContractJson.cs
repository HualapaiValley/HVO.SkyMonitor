using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public static class TransientContractReasonCodes
{
    public const string InvalidJson = "transient.invalid-json";
    public const string PayloadTooLarge = "transient.payload-too-large";
    public const string UnsupportedSchema = "transient.unsupported-schema";
    public const string UnsupportedLocator = "transient.unsupported-locator";
    public const string UnsupportedProducer = "transient.unsupported-producer";
    public const string InvalidIdentity = "transient.invalid-identity";
    public const string InvalidState = "transient.invalid-state";
    public const string InvalidTime = "transient.invalid-time";
    public const string InvalidSource = "transient.invalid-source";
    public const string InvalidLineage = "transient.invalid-lineage";
    public const string InvalidGeometry = "transient.invalid-geometry";
    public const string InvalidFeatures = "transient.invalid-features";
    public const string InvalidAssessment = "transient.invalid-assessment";
    public const string InvalidReview = "transient.invalid-review";
    public const string InvalidNotification = "transient.invalid-notification";
    public const string InvalidDerivative = "transient.invalid-derivative";
    public const string InvalidDetectorInput = "transient.invalid-detector-input";
}

/// <summary>Stable validation failure and field path for transient contracts.</summary>
public readonly record struct TransientContractValidationResult(
    bool IsValid,
    string? ReasonCode,
    string? FieldPath)
{
    public static TransientContractValidationResult Success => new(true, null, null);

    /// <summary>Creates a failed validation result without throwing.</summary>
    /// <param name="reasonCode">Stable machine-readable reason code.</param>
    /// <param name="fieldPath">Contract-relative field path associated with the failure.</param>
    /// <returns>A failed validation result.</returns>
    public static TransientContractValidationResult Failure(string reasonCode, string fieldPath)
        => new(false, reasonCode, fieldPath);
}

/// <summary>Strict parse result containing either a normalized contract or a bounded validation failure.</summary>
public sealed record TransientContractParseResult<T>(
    T? Value,
    TransientContractValidationResult Validation)
    where T : class;

/// <summary>
/// Strict canonical JSON, normalization, hashing, and validation for transient V1 contracts. All members are
/// stateless and safe for concurrent callers.
/// </summary>
public static class TransientContractJson
{
    public const int MaximumEventBytes = 4 * 1024 * 1024;
    public const int MaximumCandidateBytes = 1024 * 1024;
    public const int MaximumDetectorInputDescriptorBytes = 64 * 1024;
    private const int MaximumIdentityLength = 256;
    private const int MaximumReasonLength = 128;
    private const int OneMillion = 1_000_000;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>Validates and normalizes one event, then returns recursively ordered canonical UTF-8 JSON.</summary>
    /// <param name="value">Event to serialize; input records are not mutated.</param>
    /// <returns>Canonical JSON bytes with hashes uppercased and accepted floating signed zeros normalized.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Validation fails or the canonical payload exceeds its limit.</exception>
    public static byte[] Serialize(TransientEventV1 value)
        => Serialize(value, Validate, Normalize, MaximumEventBytes, nameof(value));

    /// <summary>Validates and normalizes one candidate, then returns canonical UTF-8 JSON.</summary>
    /// <param name="value">Candidate to serialize; input records are not mutated.</param>
    /// <returns>Canonical JSON bytes with hashes uppercased and accepted floating signed zeros normalized.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Validation fails or the canonical payload exceeds its limit.</exception>
    public static byte[] Serialize(TransientCandidateV1 value)
        => Serialize(value, Validate, Normalize, MaximumCandidateBytes, nameof(value));

    /// <summary>Validates and normalizes one detector descriptor, then returns canonical UTF-8 JSON.</summary>
    /// <param name="value">Descriptor to serialize; input records are not mutated.</param>
    /// <returns>Canonical JSON bytes with hashes uppercased and accepted floating signed zeros normalized.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Validation fails or the canonical payload exceeds its limit.</exception>
    public static byte[] Serialize(TransientDetectorInputDescriptorV1 value)
        => Serialize(value, Validate, Normalize, MaximumDetectorInputDescriptorBytes, nameof(value));

    /// <summary>Strictly parses and normalizes a bounded V1 event; root schema takes precedence over nested schemas.</summary>
    /// <param name="utf8Json">UTF-8 JSON bytes to parse.</param>
    /// <returns>A normalized event on success, otherwise a stable non-throwing validation failure.</returns>
    public static TransientContractParseResult<TransientEventV1> ParseEvent(ReadOnlyMemory<byte> utf8Json)
        => Parse<TransientEventV1>(
            utf8Json, MaximumEventBytes, TransientEventV1.CurrentSchemaVersion, Validate, Normalize);

    /// <summary>Strictly parses and normalizes a bounded V1 candidate; unknown members and numeric enums are rejected.</summary>
    /// <param name="utf8Json">UTF-8 JSON bytes to parse.</param>
    /// <returns>A normalized candidate on success, otherwise a stable non-throwing validation failure.</returns>
    public static TransientContractParseResult<TransientCandidateV1> ParseCandidate(ReadOnlyMemory<byte> utf8Json)
        => Parse<TransientCandidateV1>(
            utf8Json, MaximumCandidateBytes, TransientCandidateV1.CurrentSchemaVersion, Validate, Normalize);

    /// <summary>Strictly parses and normalizes a bounded V1 detector descriptor and verifies its canonical identity.</summary>
    /// <param name="utf8Json">UTF-8 JSON bytes to parse.</param>
    /// <returns>A normalized descriptor on success, otherwise a stable non-throwing validation failure.</returns>
    public static TransientContractParseResult<TransientDetectorInputDescriptorV1> ParseDetectorInputDescriptor(
        ReadOnlyMemory<byte> utf8Json)
        => Parse<TransientDetectorInputDescriptorV1>(
            utf8Json,
            MaximumDetectorInputDescriptorBytes,
            TransientDetectorInputDescriptorV1.CurrentSchemaVersion,
            Validate,
            Normalize);

    /// <summary>
    /// Computes the uppercase SHA-256 identity over normalized descriptor metadata, excluding the identity field.
    /// Negative floating zero is normalized to positive zero before hashing.
    /// </summary>
    /// <param name="descriptor">Descriptor metadata to normalize and hash; it is not mutated.</param>
    /// <returns>Uppercase hexadecimal SHA-256.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
    public static string ComputeDetectorInputIdentitySha256(TransientDetectorInputDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var normalized = Normalize(descriptor);
        // Descriptors persisted before saturation binding must retain their original V1 identity.
        if (normalized.SaturationMaskChecksumSha256 is null)
        {
            return CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                schema = "hvo-transient-detector-input-identity-v1",
                normalized.SchemaVersion,
                normalized.Source,
                normalized.Representation,
                normalized.Layout,
                normalized.Levels,
                normalized.Compatibility,
                normalized.Conversion,
                normalized.SourceToDetectorTransform
            });
        }
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "hvo-transient-detector-input-identity-v1",
            normalized.SchemaVersion,
            normalized.Source,
            normalized.Representation,
            normalized.Layout,
            normalized.Levels,
            normalized.SaturationMaskChecksumSha256,
            normalized.Compatibility,
            normalized.Conversion,
            normalized.SourceToDetectorTransform
        });
    }

    /// <summary>Validates candidate identity, ordered evidence, extraction, geometry, features, and bounded size.</summary>
    /// <param name="candidate">Candidate to validate without mutation.</param>
    /// <returns>Success or the first stable reason/path failure; semantic validation does not throw.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is null.</exception>
    public static TransientContractValidationResult Validate(TransientCandidateV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(candidate.SchemaVersion, TransientCandidateV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(TransientContractReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (candidate.CandidateId == Guid.Empty || candidate.EventId == Guid.Empty ||
            !Bounded(candidate.AgentId, MaximumIdentityLength) || candidate.CandidateId == candidate.EventId)
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "candidateId");
        }
        if (!Enum.IsDefined(candidate.State))
        {
            return Failure(TransientContractReasonCodes.InvalidState, "state");
        }
        if (!Utc(candidate.CreatedUtc))
        {
            return Failure(TransientContractReasonCodes.InvalidTime, "createdUtc");
        }
        if (candidate.ContextSources is null || candidate.ContextSources.Count == 0 ||
            candidate.ContextSources.Any(static source => source is null))
        {
            return Failure(TransientContractReasonCodes.InvalidSource, "contextSources");
        }
        var sourceIds = new HashSet<Guid>();
        for (var index = 0; index < candidate.ContextSources.Count; index++)
        {
            var sourceValidation = ValidateSourceEvidence(candidate.ContextSources[index], $"contextSources[{index}]");
            if (!sourceValidation.IsValid)
            {
                return sourceValidation;
            }
            if (!sourceIds.Add(candidate.ContextSources[index].EvidenceId))
            {
                return Failure(TransientContractReasonCodes.InvalidLineage, $"contextSources[{index}].evidenceId");
            }
        }
        if (!sourceIds.Contains(candidate.CenterEvidenceId))
        {
            return Failure(TransientContractReasonCodes.InvalidLineage, "centerEvidenceId");
        }
        if (candidate.ContextSources.Max(static source => source.ObservationEndedUtc) > candidate.CreatedUtc)
        {
            return Failure(TransientContractReasonCodes.InvalidTime, "createdUtc");
        }
        if (!ValidObservationProvenance(candidate.Provenance))
        {
            return Failure(TransientContractReasonCodes.InvalidSource, "provenance");
        }
        if ((candidate.Geometry is null) != (candidate.Features is null))
        {
            return Failure(TransientContractReasonCodes.InvalidGeometry, "geometry");
        }
        if (candidate.Geometry is not null)
        {
            var geometryValidation = ValidateGeometry(candidate.Geometry, candidate.CenterEvidenceId, "geometry");
            if (!geometryValidation.IsValid)
            {
                return geometryValidation;
            }
            var featureValidation = ValidateFeatures(candidate.Features!, candidate.CenterEvidenceId, "features");
            if (!featureValidation.IsValid)
            {
                return featureValidation;
            }
        }
        var reasonsValidation = ValidateReasons(candidate.Reasons, new HashSet<Guid>(), "reasons");
        if (!reasonsValidation.IsValid)
        {
            return reasonsValidation;
        }
        if (!ValidExtraction(candidate.Extraction))
        {
            return Failure(TransientContractReasonCodes.InvalidSource, "extraction");
        }
        if (candidate.Extraction.OriginatingCandidateId != candidate.CandidateId)
        {
            return Failure(TransientContractReasonCodes.InvalidLineage, "extraction.originatingCandidateId");
        }
        return Size(Normalize(candidate), MaximumCandidateBytes);
    }

    /// <summary>Validates one immutable event version and all ordered append-only histories.</summary>
    /// <param name="transientEvent">Event version to validate without mutation.</param>
    /// <returns>Success or the first stable reason/path failure; semantic validation does not throw.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transientEvent"/> is null.</exception>
    public static TransientContractValidationResult Validate(TransientEventV1 transientEvent)
    {
        ArgumentNullException.ThrowIfNull(transientEvent);
        if (!string.Equals(transientEvent.SchemaVersion, TransientEventV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(TransientContractReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (transientEvent.EventId == Guid.Empty)
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "eventId");
        }
        if (transientEvent.EventVersionId == Guid.Empty || transientEvent.EventId == transientEvent.EventVersionId)
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "eventVersionId");
        }
        if (transientEvent.PreviousEventVersionId == Guid.Empty ||
            transientEvent.PreviousEventVersionId == transientEvent.EventId ||
            transientEvent.PreviousEventVersionId == transientEvent.EventVersionId ||
            (transientEvent.Version == 1) != (transientEvent.PreviousEventVersionId is null))
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "previousEventVersionId");
        }
        if ((transientEvent.Version == 1) != (transientEvent.PreviousVersionCreatedUtc is null))
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "previousVersionCreatedUtc");
        }
        if (!Bounded(transientEvent.AgentId, MaximumIdentityLength))
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "agentId");
        }
        if (transientEvent.Version < 1)
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "version");
        }
        if (!Enum.IsDefined(transientEvent.State))
        {
            return Failure(TransientContractReasonCodes.InvalidState, "state");
        }
        if (!Utc(transientEvent.EventCreatedUtc) || !Utc(transientEvent.VersionCreatedUtc) ||
            !Utc(transientEvent.FirstObservedUtc) || !Utc(transientEvent.LastObservedUtc) ||
            transientEvent.FirstObservedUtc > transientEvent.LastObservedUtc ||
            transientEvent.FirstObservedUtc > transientEvent.EventCreatedUtc ||
            transientEvent.LastObservedUtc > transientEvent.VersionCreatedUtc ||
            transientEvent.EventCreatedUtc > transientEvent.VersionCreatedUtc ||
            transientEvent.PreviousVersionCreatedUtc is { } previousCreated &&
                (!Utc(previousCreated) || previousCreated < transientEvent.EventCreatedUtc ||
                 previousCreated >= transientEvent.VersionCreatedUtc))
        {
            return Failure(TransientContractReasonCodes.InvalidTime, "versionCreatedUtc");
        }
        if (transientEvent.Observations is null || transientEvent.Observations.Count == 0 ||
            transientEvent.Assessments is null || transientEvent.Reviews is null ||
            transientEvent.Notifications is null || transientEvent.Derivatives is null)
        {
            return Failure(TransientContractReasonCodes.InvalidLineage, "observations");
        }

        var observationIds = new HashSet<Guid>();
        var evidenceIds = new HashSet<Guid>();
        for (var index = 0; index < transientEvent.Observations.Count; index++)
        {
            var observation = transientEvent.Observations[index];
            if (observation is null || observation.ObservationId == Guid.Empty || observation.Ordinal != index ||
                !observationIds.Add(observation.ObservationId))
            {
                return Failure(TransientContractReasonCodes.InvalidLineage, $"observations[{index}]");
            }
            var sourceValidation = ValidateSourceEvidence(observation.Source, $"observations[{index}].source");
            if (!sourceValidation.IsValid)
            {
                return sourceValidation;
            }
            if (!evidenceIds.Add(observation.Source.EvidenceId))
            {
                return Failure(TransientContractReasonCodes.InvalidLineage, $"observations[{index}].source.evidenceId");
            }
            if (observation.Source.ObservationEndedUtc > transientEvent.VersionCreatedUtc ||
                observation.BackgroundArtifacts is null ||
                observation.BackgroundArtifacts.Any(background => !ValidArtifact(background) ||
                    !LinearEvidenceRole(background.Role) ||
                    background.ArtifactId == observation.Source.Locator.Artifact.ArtifactId) ||
                observation.BackgroundArtifacts.Select(static background => background.ArtifactId).Distinct().Count() !=
                    observation.BackgroundArtifacts.Count ||
                !ValidObservationProvenance(observation.Provenance) ||
                !ValidExtraction(observation.Extraction))
            {
                return Failure(TransientContractReasonCodes.InvalidSource, $"observations[{index}].provenance");
            }
            var geometryValidation = ValidateGeometry(
                observation.Geometry, observation.Source.EvidenceId, $"observations[{index}].geometry");
            if (!geometryValidation.IsValid)
            {
                return geometryValidation;
            }
            var featureValidation = ValidateFeatures(
                observation.Features, observation.Source.EvidenceId, $"observations[{index}].features");
            if (!featureValidation.IsValid)
            {
                return featureValidation;
            }
        }
        if (transientEvent.FirstObservedUtc != transientEvent.Observations.Min(static value => value.Source.ObservationStartedUtc) ||
            transientEvent.LastObservedUtc != transientEvent.Observations.Max(static value => value.Source.ObservationEndedUtc))
        {
            return Failure(TransientContractReasonCodes.InvalidTime, "firstObservedUtc");
        }

        var assessmentsById = new Dictionary<Guid, TransientAssessmentV1>();
        var supersededAssessmentIds = new HashSet<Guid>();
        for (var index = 0; index < transientEvent.Assessments.Count; index++)
        {
            var assessment = transientEvent.Assessments[index];
            var validation = ValidateAssessment(
                assessment,
                observationIds,
                assessmentsById,
                supersededAssessmentIds,
                transientEvent.EventCreatedUtc,
                transientEvent.VersionCreatedUtc,
                $"assessments[{index}]");
            if (!validation.IsValid)
            {
                return validation;
            }
            assessmentsById.Add(assessment.AssessmentId, assessment);
        }

        var reviewsById = new Dictionary<Guid, TransientReviewV1>();
        var supersededReviewIds = new HashSet<Guid>();
        for (var index = 0; index < transientEvent.Reviews.Count; index++)
        {
            var review = transientEvent.Reviews[index];
            if (review is null || review.ReviewId == Guid.Empty || reviewsById.ContainsKey(review.ReviewId) ||
                !Utc(review.CreatedUtc) || !Bounded(review.ReviewerIdentity, MaximumIdentityLength) ||
                review.CreatedUtc < transientEvent.EventCreatedUtc ||
                review.CreatedUtc > transientEvent.VersionCreatedUtc ||
                !Enum.IsDefined(review.Disposition) ||
                !assessmentsById.TryGetValue(review.AssessmentId, out var reviewAssessment) ||
                reviewAssessment.CreatedUtc > review.CreatedUtc ||
                review.SupersedesReviewId == review.ReviewId ||
                review.SupersedesReviewId is { } priorReview &&
                    (!reviewsById.TryGetValue(priorReview, out var priorReviewValue) ||
                     priorReviewValue.CreatedUtc >= review.CreatedUtc ||
                     priorReviewValue.AssessmentId != review.AssessmentId ||
                     !supersededReviewIds.Add(priorReview)) ||
                review.Disposition == TransientReviewDisposition.Overridden != (review.Override is not null) ||
                !ValidReviewOverride(review.Override) || !ValidReasonCodes(review.ReasonCodes))
            {
                return Failure(TransientContractReasonCodes.InvalidReview, $"reviews[{index}]");
            }
            reviewsById.Add(review.ReviewId, review);
        }

        var notificationsById = new Dictionary<Guid, TransientNotificationV1>();
        var supersededNotificationIds = new HashSet<Guid>();
        for (var index = 0; index < transientEvent.Notifications.Count; index++)
        {
            var notification = transientEvent.Notifications[index];
            if (notification is null || notification.NotificationId == Guid.Empty ||
                notificationsById.ContainsKey(notification.NotificationId) || !Utc(notification.CreatedUtc) ||
                notification.CreatedUtc < transientEvent.EventCreatedUtc ||
                notification.CreatedUtc > transientEvent.VersionCreatedUtc ||
                !Bounded(notification.Channel, MaximumIdentityLength) || !Enum.IsDefined(notification.State) ||
                !assessmentsById.TryGetValue(notification.AssessmentId, out var notificationAssessment) ||
                notificationAssessment.CreatedUtc > notification.CreatedUtc ||
                notification.SupersedesNotificationId == notification.NotificationId ||
                notification.SupersedesNotificationId is { } priorNotification &&
                    (!notificationsById.TryGetValue(priorNotification, out var priorNotificationValue) ||
                     priorNotificationValue.CreatedUtc >= notification.CreatedUtc ||
                     priorNotificationValue.AssessmentId != notification.AssessmentId ||
                     !string.Equals(priorNotificationValue.Channel, notification.Channel, StringComparison.Ordinal) ||
                     !supersededNotificationIds.Add(priorNotification)) ||
                notification.ReasonCode is not null && !ReasonCode(notification.ReasonCode) ||
                notification.State == TransientNotificationState.Sent && notification.ReasonCode is not null ||
                notification.State is TransientNotificationState.Failed or TransientNotificationState.Suppressed &&
                    notification.ReasonCode is null)
            {
                return Failure(TransientContractReasonCodes.InvalidNotification, $"notifications[{index}]");
            }
            notificationsById.Add(notification.NotificationId, notification);
        }

        var derivativeIds = new HashSet<Guid>();
        for (var index = 0; index < transientEvent.Derivatives.Count; index++)
        {
            var derivative = transientEvent.Derivatives[index];
            if (derivative is null || derivative.DerivativeId == Guid.Empty ||
                !derivativeIds.Add(derivative.DerivativeId) || !Utc(derivative.CreatedUtc) ||
                derivative.CreatedUtc < transientEvent.EventCreatedUtc ||
                derivative.CreatedUtc > transientEvent.VersionCreatedUtc ||
                !Enum.IsDefined(derivative.Kind) || !ValidArtifact(derivative.Artifact) ||
                derivative.Artifact.Role == FrameArtifactRole.Raw || !Sha256(derivative.RecipeIdentitySha256) ||
                !string.Equals(
                    derivative.Artifact.RecipeIdentitySha256,
                    derivative.RecipeIdentitySha256,
                    StringComparison.OrdinalIgnoreCase) ||
                derivative.OrderedSourceEvidenceIds is null || derivative.OrderedSourceEvidenceIds.Count == 0 ||
                derivative.OrderedSourceEvidenceIds.Any(id => !evidenceIds.Contains(id)) ||
                derivative.OrderedSourceEvidenceIds.Distinct().Count() != derivative.OrderedSourceEvidenceIds.Count ||
                derivative.Limitations is null || derivative.Limitations.Any(value => !Enum.IsDefined(value)) ||
                derivative.Limitations.Distinct().Count() != derivative.Limitations.Count ||
                !ValidDerivativeLimitations(derivative))
            {
                return Failure(TransientContractReasonCodes.InvalidDerivative, $"derivatives[{index}]");
            }
        }
        return Size(Normalize(transientEvent), MaximumEventBytes);
    }

    /// <summary>Validates detector layout, levels, provenance, source evidence, identity, and bounded size.</summary>
    /// <param name="descriptor">Detector descriptor to validate without mutation.</param>
    /// <returns>Success or the first stable reason/path failure; semantic validation does not throw.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
    public static TransientContractValidationResult Validate(TransientDetectorInputDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.Equals(
                descriptor.SchemaVersion,
                TransientDetectorInputDescriptorV1.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(TransientContractReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        var sourceValidation = ValidateSourceEvidence(descriptor.Source);
        if (!sourceValidation.IsValid)
        {
            return sourceValidation;
        }
        if (!Enum.IsDefined(descriptor.Representation) || descriptor.Layout is null ||
            !ValidDetectorLayout(descriptor.Layout))
        {
            return Failure(TransientContractReasonCodes.InvalidDetectorInput, "layout");
        }
        if (descriptor.Levels is null || descriptor.Levels.WhiteLevel <= descriptor.Levels.BlackLevel ||
            descriptor.Levels.SaturationLevel <= descriptor.Levels.BlackLevel ||
            descriptor.Levels.SaturationLevel > descriptor.Levels.WhiteLevel ||
            descriptor.Layout.BlackLevel != descriptor.Levels.BlackLevel ||
            descriptor.Layout.WhiteLevel != descriptor.Levels.WhiteLevel)
        {
            return Failure(TransientContractReasonCodes.InvalidDetectorInput, "levels");
        }
        if ((descriptor.SaturationMaskChecksumSha256 is not null && !Sha256(descriptor.SaturationMaskChecksumSha256)) ||
            !ValidCompatibility(descriptor.Compatibility) || !ValidDetectorProvenance(descriptor))
        {
            return Failure(TransientContractReasonCodes.InvalidDetectorInput, "provenance");
        }
        if (!Sha256(descriptor.InputIdentitySha256) || !string.Equals(
                descriptor.InputIdentitySha256,
                ComputeDetectorInputIdentitySha256(descriptor),
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, "inputIdentitySha256");
        }
        return Size(Normalize(descriptor), MaximumDetectorInputDescriptorBytes);
    }

    /// <summary>Validates one whole-artifact linear source reference and its ordered UTC observation interval.</summary>
    /// <param name="source">Source evidence to validate without mutation.</param>
    /// <returns>Success or the first stable reason/path failure, including null input; validation does not throw.</returns>
    public static TransientContractValidationResult ValidateSourceEvidence(TransientSourceEvidenceReferenceV1 source)
        => ValidateSourceEvidence(source, "source");

    internal static TransientSourceEvidenceReferenceV1 NormalizeSourceEvidence(
        TransientSourceEvidenceReferenceV1 source)
        => source with
        {
            Locator = source.Locator with
            {
                Artifact = Normalize(source.Locator.Artifact)
            }
        };

    private static byte[] Serialize<T>(
        T value,
        Func<T, TransientContractValidationResult> validate,
        Func<T, T> normalize,
        int maximumBytes,
        string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var validation = validate(value);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Transient contract is invalid ({validation.ReasonCode}, {validation.FieldPath}).",
                parameterName);
        }
        var element = JsonSerializer.SerializeToElement(normalize(value), SerializerOptions);
        var json = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
        if (json.Length > maximumBytes)
        {
            throw new ArgumentException("Transient contract exceeds the maximum payload size.", parameterName);
        }
        return json;
    }

    private static TransientContractParseResult<T> Parse<T>(
        ReadOnlyMemory<byte> utf8Json,
        int maximumBytes,
        string expectedSchemaVersion,
        Func<T, TransientContractValidationResult> validate,
        Func<T, T> normalize)
        where T : class
    {
        if (utf8Json.Length > maximumBytes)
        {
            return ParseFailure<T>(TransientContractReasonCodes.PayloadTooLarge, "$");
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement))
            {
                return ParseFailure<T>(TransientContractReasonCodes.InvalidJson, "$");
            }
            if (!document.RootElement.TryGetProperty("schemaVersion", out var rootSchema) ||
                rootSchema.ValueKind != JsonValueKind.String)
            {
                return ParseFailure<T>(TransientContractReasonCodes.InvalidJson, "schemaVersion");
            }
            if (!string.Equals(rootSchema.GetString(), expectedSchemaVersion, StringComparison.Ordinal))
            {
                return ParseFailure<T>(TransientContractReasonCodes.UnsupportedSchema, "schemaVersion");
            }
            var nestedSchemaValidation = ValidateNestedSchemas(document.RootElement);
            if (!nestedSchemaValidation.IsValid)
            {
                return new(null, nestedSchemaValidation);
            }
            var value = document.RootElement.Deserialize<T>(SerializerOptions);
            if (value is null)
            {
                return ParseFailure<T>(TransientContractReasonCodes.InvalidJson, "$");
            }
            var validation = validate(value);
            return new(validation.IsValid ? normalize(value) : null, validation);
        }
        catch (JsonException)
        {
            return ParseFailure<T>(TransientContractReasonCodes.InvalidJson, "$");
        }
        catch (OverflowException)
        {
            return ParseFailure<T>(TransientContractReasonCodes.InvalidJson, "$");
        }
    }

    private static TransientContractValidationResult ValidateSourceEvidence(
        TransientSourceEvidenceReferenceV1? source,
        string path)
    {
        if (source is null || !string.Equals(
                source.SchemaVersion,
                TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return Failure(TransientContractReasonCodes.UnsupportedSchema, $"{path}.schemaVersion");
        }
        if (source.EvidenceId == Guid.Empty)
        {
            return Failure(TransientContractReasonCodes.InvalidIdentity, $"{path}.evidenceId");
        }
        if (source.Locator is null || !string.Equals(
                source.Locator.SchemaVersion,
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                StringComparison.Ordinal) ||
            !Enum.IsDefined(source.Locator.Kind) || source.Locator.Kind != TransientSourceLocatorKind.WholeArtifact)
        {
            return Failure(TransientContractReasonCodes.UnsupportedLocator, $"{path}.locator.schemaVersion");
        }
        if (!ValidArtifact(source.Locator.Artifact) || !LinearEvidenceRole(source.Locator.Artifact.Role))
        {
            return Failure(TransientContractReasonCodes.InvalidSource, $"{path}.locator.artifact");
        }
        if (!Utc(source.ObservationStartedUtc) || !Utc(source.ObservationEndedUtc) ||
            source.ObservationStartedUtc > source.ObservationEndedUtc || !Enum.IsDefined(source.TimingQuality))
        {
            return Failure(TransientContractReasonCodes.InvalidTime, $"{path}.observationStartedUtc");
        }
        if (source.TimingProvenance is null || !Bounded(source.TimingProvenance.Source, MaximumIdentityLength) ||
            !Bounded(source.TimingProvenance.Version, MaximumIdentityLength))
        {
            return Failure(TransientContractReasonCodes.InvalidSource, $"{path}.timingProvenance");
        }
        return TransientContractValidationResult.Success;
    }

    private static TransientContractValidationResult ValidateGeometry(
        TransientGeometryV1? geometry,
        Guid expectedEvidenceId,
        string path)
    {
        if (geometry is null || geometry.SourceEvidenceId != expectedEvidenceId ||
            geometry.CoordinateWidth < 1 || geometry.CoordinateHeight < 1 || geometry.Bounds is null ||
            !Finite(geometry.Bounds.X) || !Finite(geometry.Bounds.Y) ||
            !Finite(geometry.Bounds.Width) || !Finite(geometry.Bounds.Height) ||
            geometry.Bounds.X < 0 || geometry.Bounds.Y < 0 ||
            geometry.Bounds.Width <= 0 || geometry.Bounds.Height <= 0 ||
            geometry.Bounds.X + geometry.Bounds.Width > geometry.CoordinateWidth ||
            geometry.Bounds.Y + geometry.Bounds.Height > geometry.CoordinateHeight ||
            geometry.Polyline is null || geometry.Polyline.Count < 2 || geometry.Polyline.Any(point =>
                !Finite(point.X) || !Finite(point.Y) || point.X < 0 || point.Y < 0 ||
                point.X > geometry.CoordinateWidth || point.Y > geometry.CoordinateHeight))
        {
            return Failure(TransientContractReasonCodes.InvalidGeometry, path);
        }
        return TransientContractValidationResult.Success;
    }

    private static TransientContractValidationResult ValidateFeatures(
        TransientFeaturesV1? features,
        Guid expectedEvidenceId,
        string path)
    {
        if (features is null || features.SourceEvidenceId != expectedEvidenceId ||
            !Finite(features.LengthPixels) || !Finite(features.MeanWidthPixels) ||
            !Finite(features.MaximumWidthPixels) || features.LengthPixels < 0 ||
            features.MeanWidthPixels < 0 || features.MaximumWidthPixels < features.MeanWidthPixels ||
            features.IntegratedSignalAdu < 0 || features.SaturatedSampleCount < 0 || features.FragmentCount < 1 ||
            !ValidProfile(features.WidthProfile) || !ValidProfile(features.BrightnessProfile))
        {
            return Failure(TransientContractReasonCodes.InvalidFeatures, path);
        }
        return TransientContractValidationResult.Success;
    }

    private static TransientContractValidationResult ValidateAssessment(
        TransientAssessmentV1? assessment,
        HashSet<Guid> observationIds,
        Dictionary<Guid, TransientAssessmentV1> priorAssessments,
        HashSet<Guid> supersededAssessmentIds,
        DateTimeOffset eventCreatedUtc,
        DateTimeOffset versionCreatedUtc,
        string path)
    {
        if (assessment is null || assessment.AssessmentId == Guid.Empty ||
            priorAssessments.ContainsKey(assessment.AssessmentId) || !Utc(assessment.CreatedUtc) ||
            assessment.CreatedUtc < eventCreatedUtc || assessment.CreatedUtc > versionCreatedUtc ||
            !Enum.IsDefined(assessment.Authority) || !Enum.IsDefined(assessment.Classification) ||
            assessment.MeteorSeverity is { } severity && !Enum.IsDefined(severity) ||
            (assessment.Classification == TransientClassification.Meteor) != assessment.MeteorSeverity.HasValue ||
            assessment.ConfidenceMillionths is < 0 or > OneMillion || !Sha256(assessment.RecipeIdentitySha256) ||
            assessment.EvidenceObservationIds is null || assessment.EvidenceObservationIds.Count == 0 ||
            assessment.EvidenceObservationIds.Any(id => !observationIds.Contains(id)) ||
            assessment.EvidenceObservationIds.Distinct().Count() != assessment.EvidenceObservationIds.Count ||
            assessment.SupersedesAssessmentId == assessment.AssessmentId ||
            assessment.SupersedesAssessmentId is { } prior &&
                (!priorAssessments.TryGetValue(prior, out var priorAssessment) ||
                 priorAssessment.CreatedUtc >= assessment.CreatedUtc ||
                 !supersededAssessmentIds.Add(prior)))
        {
            return Failure(TransientContractReasonCodes.InvalidAssessment, path);
        }
        var producerValidation = ValidateProducer(assessment.Producer, $"{path}.producer");
        if (!producerValidation.IsValid)
        {
            return producerValidation;
        }
        return ValidateReasons(
            assessment.Reasons,
            assessment.EvidenceObservationIds.ToHashSet(),
            $"{path}.reasons");
    }

    private static TransientContractValidationResult ValidateProducer(
        TransientAssessmentProducerV1? producer,
        string path)
    {
        if (producer is null || !string.Equals(
                producer.SchemaVersion,
                TransientAssessmentProducerV1.CurrentSchemaVersion,
                StringComparison.Ordinal) ||
            !Enum.IsDefined(producer.Kind) || producer.Kind != TransientAssessmentProducerKind.DeterministicAlgorithm)
        {
            return Failure(TransientContractReasonCodes.UnsupportedProducer, $"{path}.schemaVersion");
        }
        return Bounded(producer.Name, MaximumIdentityLength) && Bounded(producer.Version, MaximumIdentityLength)
            ? TransientContractValidationResult.Success
            : Failure(TransientContractReasonCodes.InvalidIdentity, path);
    }

    private static TransientContractValidationResult ValidateReasons(
        IReadOnlyList<TransientReasonV1>? reasons,
        HashSet<Guid> observationIds,
        string path)
    {
        if (reasons is null)
        {
            return Failure(TransientContractReasonCodes.InvalidAssessment, path);
        }
        var reasonKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < reasons.Count; index++)
        {
            var reason = reasons[index];
            if (reason is null || !ReasonCode(reason.Code) || !reasonKeys.Add(reason.Code) ||
                !Enum.IsDefined(reason.Kind) || reason.ObservationIds is null ||
                reason.ObservationIds.Any(id => !observationIds.Contains(id)) ||
                reason.ObservationIds.Distinct().Count() != reason.ObservationIds.Count)
            {
                return Failure(TransientContractReasonCodes.InvalidAssessment, $"{path}[{index}]");
            }
        }
        return TransientContractValidationResult.Success;
    }

    private static bool ValidArtifact(TransientArtifactReferenceV1? artifact)
        => artifact is not null && artifact.ArtifactId != Guid.Empty && Enum.IsDefined(artifact.Role) &&
           Bounded(artifact.Variant, MaximumIdentityLength) && Sha256(artifact.RecipeIdentitySha256) &&
           Sha256(artifact.ChecksumSha256);

    private static bool LinearEvidenceRole(FrameArtifactRole role)
        => role is FrameArtifactRole.Raw or FrameArtifactRole.Calibrated;

    private static bool ValidExtraction(TransientObservationExtractionV1? extraction)
        => extraction is not null && extraction.OriginatingCandidateId != Guid.Empty &&
           extraction.Producer is not null && string.Equals(
               extraction.Producer.SchemaVersion,
               TransientExtractionProducerV1.CurrentSchemaVersion,
               StringComparison.Ordinal) &&
           extraction.Producer.Kind == TransientExtractionProducerKind.DeterministicAlgorithm &&
           Bounded(extraction.Producer.Name, MaximumIdentityLength) &&
           Bounded(extraction.Producer.Version, MaximumIdentityLength) &&
           Sha256(extraction.RecipeIdentitySha256);

    private static bool ValidReviewOverride(TransientReviewOverrideV1? value)
        => value is null || Enum.IsDefined(value.Classification) &&
           (value.MeteorSeverity is null || Enum.IsDefined(value.MeteorSeverity.Value)) &&
           (value.Classification == TransientClassification.Meteor) == value.MeteorSeverity.HasValue &&
           value.ConfidenceMillionths is >= 0 and <= OneMillion;

    private static bool ValidReasonCodes(IReadOnlyList<string>? values)
        => values is not null && values.All(ReasonCode) &&
           values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool ValidDerivativeLimitations(TransientDerivativeV1 derivative)
        => derivative.Kind == TransientDerivativeKind.Reconstruction
            ? derivative.Limitations.Count == 2 &&
              derivative.Limitations.Contains(TransientDerivativeLimitation.IntraExposureTimingUnavailable) &&
              derivative.Limitations.Contains(TransientDerivativeLimitation.SaturatedPhotometryUnrecoverable)
            : derivative.Limitations.Count == 0;

    private static bool ValidProfile(IReadOnlyList<TransientProfileSampleV1>? values)
    {
        if (values is null || values.Count == 0)
        {
            return false;
        }
        var prior = -1;
        foreach (var value in values)
        {
            if (value is null || value.PositionMillionths is < 0 or > OneMillion ||
                value.PositionMillionths <= prior || !Finite(value.Value) || value.Value < 0)
            {
                return false;
            }
            prior = value.PositionMillionths;
        }
        return true;
    }

    private static bool ValidCompatibility(ProcessingCompatibilityIdentity? value)
        => value is not null && Bounded(value.Rig, MaximumIdentityLength) &&
           Bounded(value.Orientation, MaximumIdentityLength) && Bounded(value.Calibration, MaximumIdentityLength) &&
           Bounded(value.Mask, MaximumIdentityLength) && Bounded(value.Sensor, MaximumIdentityLength) &&
           Bounded(value.SetpointRegime, MaximumIdentityLength) &&
           Bounded(value.ProcessingProfile, MaximumIdentityLength) &&
           (value.LocationIdentitySha256 is null || Sha256(value.LocationIdentitySha256));

    private static bool ValidObservationProvenance(TransientObservationProvenanceV1? value)
        => value is not null && Sha256(value.DetectorInputIdentitySha256) &&
           Bounded(value.CalibrationIdentity, MaximumIdentityLength) &&
           Bounded(value.MaskIdentity, MaximumIdentityLength) &&
           Bounded(value.ProcessingProfileIdentity, MaximumIdentityLength);

    private static bool ValidDetectorLayout(FrameLayoutDescriptor layout)
    {
        if (layout.Width < 1 || layout.Height < 1 || layout.PixelFormat != CameraPixelFormat.Mono16 ||
            layout.ByteOrder != FrameByteOrder.LittleEndian || layout.SampleDepthBits != 16 ||
            layout.ContainerDepthBits != 16 || layout.Packing != FrameSamplePacking.ByteAligned ||
            layout.CfaPattern != ColorFilterArrayPattern.None)
        {
            return false;
        }
        try
        {
            return layout.StrideBytes >= checked(layout.Width * 2) &&
                layout.ByteLength == checked((long)layout.StrideBytes * layout.Height);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool ValidDetectorProvenance(TransientDetectorInputDescriptorV1 descriptor)
    {
        if (descriptor.Conversion is null || descriptor.SourceToDetectorTransform is null ||
            !string.Equals(descriptor.Conversion.Name, "linear16-detector-input", StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.SourceToDetectorTransform.Version,
                Linear16DetectorInputConverter.TransformVersion,
                StringComparison.Ordinal))
        {
            return false;
        }
        return descriptor.Representation switch
        {
            TransientDetectorRepresentation.Mono16 =>
                string.Equals(
                    descriptor.Conversion.Version,
                    Linear16DetectorInputConverter.Mono16AlgorithmVersion,
                    StringComparison.Ordinal) &&
                descriptor.SourceToDetectorTransform.ScaleX == 1 &&
                descriptor.SourceToDetectorTransform.ScaleY == 1 &&
                descriptor.SourceToDetectorTransform.OffsetX == 0 &&
                descriptor.SourceToDetectorTransform.OffsetY == 0,
            TransientDetectorRepresentation.Rggb16CellAverage =>
                string.Equals(
                    descriptor.Conversion.Version,
                    Linear16DetectorInputConverter.Rggb16AlgorithmVersion,
                    StringComparison.Ordinal) &&
                descriptor.SourceToDetectorTransform.ScaleX == 0.5 &&
                descriptor.SourceToDetectorTransform.ScaleY == 0.5 &&
                descriptor.SourceToDetectorTransform.OffsetX == 0 &&
                descriptor.SourceToDetectorTransform.OffsetY == 0,
            _ => false
        };
    }

    private static TransientEventV1 Normalize(TransientEventV1 value)
        => value with
        {
            Observations = value.Observations.Select(static observation => observation with
            {
                Source = NormalizeSourceEvidence(observation.Source),
                BackgroundArtifacts = observation.BackgroundArtifacts.Select(Normalize).ToArray(),
                Provenance = observation.Provenance with
                {
                    DetectorInputIdentitySha256 = observation.Provenance.DetectorInputIdentitySha256.ToUpperInvariant()
                },
                Extraction = observation.Extraction with
                {
                    RecipeIdentitySha256 = observation.Extraction.RecipeIdentitySha256.ToUpperInvariant()
                },
                Geometry = Normalize(observation.Geometry),
                Features = Normalize(observation.Features)
            }).ToArray(),
            Assessments = value.Assessments.Select(static assessment => assessment with
            {
                RecipeIdentitySha256 = assessment.RecipeIdentitySha256.ToUpperInvariant()
            }).ToArray(),
            Derivatives = value.Derivatives.Select(static derivative => derivative with
            {
                Artifact = Normalize(derivative.Artifact),
                RecipeIdentitySha256 = derivative.RecipeIdentitySha256.ToUpperInvariant()
            }).ToArray()
        };

    private static TransientCandidateV1 Normalize(TransientCandidateV1 value)
        => value with
        {
            ContextSources = value.ContextSources.Select(NormalizeSourceEvidence).ToArray(),
            Provenance = value.Provenance with
            {
                DetectorInputIdentitySha256 = value.Provenance.DetectorInputIdentitySha256.ToUpperInvariant()
            },
            Extraction = value.Extraction with
            {
                RecipeIdentitySha256 = value.Extraction.RecipeIdentitySha256.ToUpperInvariant()
            },
            Geometry = value.Geometry is null ? null : Normalize(value.Geometry),
            Features = value.Features is null ? null : Normalize(value.Features)
        };

    private static TransientDetectorInputDescriptorV1 Normalize(TransientDetectorInputDescriptorV1 value)
        => value with
        {
            InputIdentitySha256 = value.InputIdentitySha256.ToUpperInvariant(),
            SaturationMaskChecksumSha256 = value.SaturationMaskChecksumSha256?.ToUpperInvariant(),
            Source = NormalizeSourceEvidence(value.Source),
            Layout = value.Layout with
            {
                BlackLevel = PositiveZero(value.Layout.BlackLevel),
                WhiteLevel = PositiveZero(value.Layout.WhiteLevel)
            },
            SourceToDetectorTransform = value.SourceToDetectorTransform with
            {
                ScaleX = PositiveZero(value.SourceToDetectorTransform.ScaleX),
                ScaleY = PositiveZero(value.SourceToDetectorTransform.ScaleY),
                OffsetX = PositiveZero(value.SourceToDetectorTransform.OffsetX),
                OffsetY = PositiveZero(value.SourceToDetectorTransform.OffsetY)
            }
        };

    private static TransientArtifactReferenceV1 Normalize(TransientArtifactReferenceV1 value)
        => value with
        {
            RecipeIdentitySha256 = value.RecipeIdentitySha256.ToUpperInvariant(),
            ChecksumSha256 = value.ChecksumSha256.ToUpperInvariant()
        };

    private static TransientGeometryV1 Normalize(TransientGeometryV1 value)
        => value with
        {
            Bounds = value.Bounds with
            {
                X = PositiveZero(value.Bounds.X),
                Y = PositiveZero(value.Bounds.Y),
                Width = PositiveZero(value.Bounds.Width),
                Height = PositiveZero(value.Bounds.Height)
            },
            Polyline = value.Polyline.Select(static point => new TransientPointV1(
                PositiveZero(point.X),
                PositiveZero(point.Y))).ToArray()
        };

    private static TransientFeaturesV1 Normalize(TransientFeaturesV1 value)
        => value with
        {
            LengthPixels = PositiveZero(value.LengthPixels),
            MeanWidthPixels = PositiveZero(value.MeanWidthPixels),
            MaximumWidthPixels = PositiveZero(value.MaximumWidthPixels),
            WidthProfile = value.WidthProfile.Select(static sample => sample with
            {
                Value = PositiveZero(sample.Value)
            }).ToArray(),
            BrightnessProfile = value.BrightnessProfile.Select(static sample => sample with
            {
                Value = PositiveZero(sample.Value)
            }).ToArray()
        };

    private static TransientContractValidationResult Size<T>(T value, int maximumBytes)
    {
        var element = JsonSerializer.SerializeToElement(value, SerializerOptions);
        var length = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element)).Length;
        return length <= maximumBytes
            ? TransientContractValidationResult.Success
            : Failure(TransientContractReasonCodes.PayloadTooLarge, "$");
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    private static TransientContractValidationResult ValidateNestedSchemas(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object &&
                    property.Value.TryGetProperty("schemaVersion", out var schema) &&
                    schema.ValueKind == JsonValueKind.String)
                {
                    if (property.NameEquals("locator") && !string.Equals(
                            schema.GetString(),
                            TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                            StringComparison.Ordinal))
                    {
                        return Failure(TransientContractReasonCodes.UnsupportedLocator, "locator.schemaVersion");
                    }
                    if (property.NameEquals("producer") &&
                        !string.Equals(
                            schema.GetString(),
                            TransientAssessmentProducerV1.CurrentSchemaVersion,
                            StringComparison.Ordinal) &&
                        !string.Equals(
                            schema.GetString(),
                            TransientExtractionProducerV1.CurrentSchemaVersion,
                            StringComparison.Ordinal))
                    {
                        return Failure(TransientContractReasonCodes.UnsupportedProducer, "producer.schemaVersion");
                    }
                }
                var validation = ValidateNestedSchemas(property.Value);
                if (!validation.IsValid)
                {
                    return validation;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var validation = ValidateNestedSchemas(item);
                if (!validation.IsValid)
                {
                    return validation;
                }
            }
        }
        return TransientContractValidationResult.Success;
    }

    private static bool Bounded(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim();

    private static bool ReasonCode(string? value)
        => Bounded(value, MaximumReasonLength) && value!.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    private static bool Sha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool Utc(DateTimeOffset value)
        => value != default && value.Offset == TimeSpan.Zero;

    private static bool Finite(double value) => double.IsFinite(value);
    private static double PositiveZero(double value) => value == 0 ? 0d : value;
    private static double? PositiveZero(double? value) => value is { } number ? PositiveZero(number) : null;

    private static TransientContractValidationResult Failure(string reasonCode, string path)
        => TransientContractValidationResult.Failure(reasonCode, path);

    private static TransientContractParseResult<T> ParseFailure<T>(string reasonCode, string path)
        where T : class
        => new(null, Failure(reasonCode, path));

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
