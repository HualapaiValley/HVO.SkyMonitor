using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public sealed record TransientObservationPromotionRequest(
    Guid CandidateId,
    Guid ObservationId,
    int Ordinal,
    TransientCandidateExtractionDescriptorV1 Extraction);

/// <summary>Binds an observation to the candidate event and exact extraction receipt that produced it.</summary>
public sealed record TransientAssessmentObservationV1(
    Guid EventId,
    string ExtractionIdentitySha256,
    TransientObservationV1 Observation);

public static class TransientObservationFactory
{
    public static TransientAssessmentObservationV1 CreateAssessmentObservation(
        TransientObservationPromotionRequest request)
    {
        var observation = Create(request);
        var candidate = request.Extraction.Candidates.Single(value => value.CandidateId == request.CandidateId);
        return new TransientAssessmentObservationV1(
            candidate.EventId,
            request.Extraction.ExtractionIdentitySha256,
            observation);
    }

    /// <summary>Promotes one extracted candidate without changing its geometry, features, or extraction lineage.</summary>
    public static TransientObservationV1 Create(TransientObservationPromotionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ObservationId == Guid.Empty || request.Ordinal < 0 || request.Extraction is null)
        {
            throw new ArgumentException("Observation identity and extraction receipt are required.", nameof(request));
        }
        TransientCandidateExtractionJson.Validate(request.Extraction);
        var candidate = request.Extraction.Candidates.SingleOrDefault(candidate => candidate.CandidateId == request.CandidateId)
            ?? throw new ArgumentException("Candidate does not belong to the extraction receipt.", nameof(request));
        if (candidate.State is not (TransientCandidateState.Provisional or TransientCandidateState.Complete) ||
            candidate.Geometry is null || candidate.Features is null ||
            candidate.Extraction.OriginatingCandidateId != candidate.CandidateId)
        {
            throw new ArgumentException("Only a measured candidate can become an observation.", nameof(request));
        }
        var source = request.Extraction.OrderedSources.Single(value => value.Source.EvidenceId == candidate.CenterEvidenceId).Source;
        var includedEvidence = request.Extraction.Background.Sources
            .Where(static value => value.Disposition == TransientTemporalSourceDisposition.Included)
            .Select(static value => value.EvidenceId)
            .ToHashSet();
        var backgroundArtifacts = request.Extraction.OrderedSources
            .Where(value => includedEvidence.Contains(value.Source.EvidenceId))
            .Select(static value => value.Source.Locator.Artifact)
            .ToArray();
        return new TransientObservationV1(
            request.ObservationId,
            request.Ordinal,
            source,
            backgroundArtifacts,
            candidate.Provenance,
            candidate.Extraction,
            candidate.Geometry,
            candidate.Features);
    }
}

public sealed record TransientDeterministicAssessmentOptionsV1(
    [property: JsonRequired] double MinimumMeteorLengthPixels,
    [property: JsonRequired] double MinimumMeteorElongation,
    [property: JsonRequired] double MaximumMeteorMeanWidthPixels,
    [property: JsonRequired] double CompactSensorMaximumLengthPixels,
    [property: JsonRequired] double SensorArtifactMaximumMeanWidthPixels,
    [property: JsonRequired] double StationaryMaximumDisplacementPixels,
    [property: JsonRequired] double EnvironmentalMinimumMeanWidthPixels,
    [property: JsonRequired] long FireballMinimumIntegratedSignalAdu,
    [property: JsonRequired] double FlareMinimumPeakToEndpointRatio,
    [property: JsonRequired] int PersistentTrackMinimumObservations,
    [property: JsonRequired] double AircraftMinimumBrightnessRatio,
    [property: JsonRequired] long AircraftMinimumIntegratedSignalAdu,
    [property: JsonRequired] double SmoothMotionMaximumTurnDegrees,
    [property: JsonRequired] double SmoothMotionMaximumStepRatio);

public sealed record TransientAssessmentExecutionRequest(
    Guid EventId,
    Guid AssessmentId,
    DateTimeOffset CreatedUtc,
    TransientAssessmentAuthority Authority,
    IReadOnlyList<TransientAssessmentObservationV1> Observations,
    TransientDeterministicAssessmentOptionsV1 Options,
    IReadOnlyList<TransientAssessmentV1> PriorAssessments,
    Guid? SupersedesAssessmentId = null);

public sealed record TransientAssessmentExecutionDescriptorV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string ExecutionIdentitySha256,
    [property: JsonRequired] Guid EventId,
    [property: JsonRequired] string OptionsIdentitySha256,
    [property: JsonRequired] TransientDeterministicAssessmentOptionsV1 Options,
    [property: JsonRequired] IReadOnlyList<Guid> OrderedObservationIds,
    [property: JsonRequired] IReadOnlyList<string> OrderedObservationIdentitySha256s,
    [property: JsonRequired] IReadOnlyList<Guid> PriorAssessmentIds,
    [property: JsonRequired] IReadOnlyList<string> PriorAssessmentIdentitySha256s,
    [property: JsonRequired] TransientAssessmentV1 Assessment)
{
    public const string CurrentSchemaVersion = "transient-deterministic-assessment-v1";
}

public enum TransientAssessmentExecutionStatus
{
    Produced,
    Invalid,
    LimitExceeded
}

public sealed record TransientAssessmentExecutionOutcome(
    TransientAssessmentExecutionStatus Status,
    string? ReasonCode,
    string? Field,
    TransientAssessmentExecutionDescriptorV1? Descriptor,
    IReadOnlyList<TransientAssessmentV1> AssessmentHistory);

public static class TransientAssessmentReasonCodes
{
    public const string InvalidRequest = "transient-assessment.invalid-request";
    public const string InvalidHistory = "transient-assessment.invalid-history";
    public const string OutputLimit = "transient-assessment.output-limit";
    public const string ElongatedTrack = "transient.elongated-track";
    public const string FlareProfile = "transient.flare-profile";
    public const string FragmentedTrack = "transient.fragmented-track";
    public const string SaturatedBrightness = "transient.saturated-brightness";
    public const string SmoothPersistentTrack = "transient.smooth-persistent-track";
    public const string TemporalBlinking = "transient.temporal-blinking";
    public const string CompactResidual = "transient.compact-residual";
    public const string ThinDetectorResidual = "transient.thin-detector-residual";
    public const string StationaryResidual = "transient.stationary-residual";
    public const string BroadResidual = "transient.broad-residual";
    public const string InsufficientEvidence = "transient.insufficient-discriminating-evidence";
    public const string SingleObservation = "transient.single-observation";
}

public static class TransientAssessmentFactory
{
    public const string ProducerName = "deterministic-transient-assessor";
    public const string ProducerVersion = "deterministic-transient-assessor-v1";
    private const int MaximumObservations = 64;
    private const int MaximumPriorAssessments = 256;
    private const int MaximumProfileSamples = 64;
    private const int MaximumPolylinePoints = 64;
    private const int MaximumReasons = 32;
    private const int MaximumBackgroundArtifacts = 4;

    public static TransientAssessmentExecutionOutcome Create(
        TransientAssessmentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateRequest(request, cancellationToken);
        if (validation is not null)
        {
            return Failure(validation.Value.ReasonCode, validation.Value.Field, request.PriorAssessments ?? []);
        }

        var optionsIdentity = ComputeOptionsIdentitySha256(request.Options);
        var recipeIdentity = ComputeRecipeIdentitySha256(request.Options);
        var observations = request.Observations.Select(static value => value.Observation).ToArray();
        var classification = Classify(observations, request.Options, cancellationToken);
        var assessment = new TransientAssessmentV1(
            request.AssessmentId,
            request.CreatedUtc,
            request.Authority,
            classification.Classification,
            classification.Severity,
            classification.ConfidenceMillionths,
            classification.Reasons,
            new TransientAssessmentProducerV1(
                TransientAssessmentProducerV1.CurrentSchemaVersion,
                TransientAssessmentProducerKind.DeterministicAlgorithm,
                ProducerName,
                ProducerVersion),
            recipeIdentity,
            observations.Select(static observation => observation.ObservationId).ToArray(),
            request.SupersedesAssessmentId);
        var history = request.PriorAssessments.Append(assessment).ToArray();
        var descriptor = new TransientAssessmentExecutionDescriptorV1(
            TransientAssessmentExecutionDescriptorV1.CurrentSchemaVersion,
            string.Empty,
            request.EventId,
            optionsIdentity,
            request.Options,
            assessment.EvidenceObservationIds,
            ComputeObservationIdentities(request.Observations, cancellationToken),
            request.PriorAssessments.Select(static value => value.AssessmentId).ToArray(),
            ComputePriorAssessmentIdentities(request.PriorAssessments, cancellationToken),
            assessment);
        descriptor = descriptor with { ExecutionIdentitySha256 = ComputeExecutionIdentitySha256(descriptor) };
        TransientAssessmentJson.Validate(descriptor);
        if (!TransientAssessmentJson.IsWithinSizeLimit(descriptor))
        {
            return new TransientAssessmentExecutionOutcome(
                TransientAssessmentExecutionStatus.LimitExceeded,
                TransientAssessmentReasonCodes.OutputLimit,
                "descriptor",
                null,
                request.PriorAssessments);
        }
        return new TransientAssessmentExecutionOutcome(
            TransientAssessmentExecutionStatus.Produced,
            null,
            null,
            descriptor,
            history);
    }

    public static string ComputeOptionsIdentitySha256(TransientDeterministicAssessmentOptionsV1 options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "transient-deterministic-assessment-options-v1",
            options
        });
    }

    public static string ComputeRecipeIdentitySha256(TransientDeterministicAssessmentOptionsV1 options)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "transient-deterministic-assessment-recipe-v1",
            producer = new { name = ProducerName, version = ProducerVersion },
            optionsIdentitySha256 = ComputeOptionsIdentitySha256(options)
        });

    public static string ComputeExecutionIdentitySha256(TransientAssessmentExecutionDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "transient-deterministic-assessment-identity-v1",
            descriptor.SchemaVersion,
            descriptor.EventId,
            descriptor.OptionsIdentitySha256,
            descriptor.Options,
            descriptor.OrderedObservationIds,
            descriptor.OrderedObservationIdentitySha256s,
            descriptor.PriorAssessmentIds,
            descriptor.PriorAssessmentIdentitySha256s,
            descriptor.Assessment
        });
    }

    private static ClassificationResult Classify(
        TransientObservationV1[] observations,
        TransientDeterministicAssessmentOptionsV1 options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = observations.Select(static value => value.ObservationId).ToArray();
        var length = observations.Max(static value => value.Features.LengthPixels);
        var meanWidth = observations.Average(static value => value.Features.MeanWidthPixels);
        var elongation = length / Math.Max(meanWidth, double.Epsilon);
        long integrated = 0;
        var saturated = 0;
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            integrated = observation.Features.IntegratedSignalAdu > long.MaxValue - integrated
                ? long.MaxValue
                : integrated + observation.Features.IntegratedSignalAdu;
            saturated = observation.Features.SaturatedSampleCount > int.MaxValue - saturated
                ? int.MaxValue
                : saturated + observation.Features.SaturatedSampleCount;
        }
        var fragmented = observations.Any(static value => value.Features.FragmentCount > 1);
        var flare = observations.Any(value => FlareRatio(value.Features.BrightnessProfile) >=
            options.FlareMinimumPeakToEndpointRatio);
        var maximumDisplacement = MaximumDisplacement(observations);
        var stationary = maximumDisplacement <= options.StationaryMaximumDisplacementPixels;
        var compact = length <= options.CompactSensorMaximumLengthPixels;
        var thinDetectorResidual = observations.Length == 1 &&
            meanWidth <= options.SensorArtifactMaximumMeanWidthPixels;
        var broad = meanWidth >= options.EnvironmentalMinimumMeanWidthPixels;
        var elongated = length >= options.MinimumMeteorLengthPixels &&
            elongation >= options.MinimumMeteorElongation && meanWidth <= options.MaximumMeteorMeanWidthPixels;
        var smooth = observations.Length >= options.PersistentTrackMinimumObservations &&
            SmoothMotion(observations, options.SmoothMotionMaximumTurnDegrees, options.SmoothMotionMaximumStepRatio);
        var blinking = observations.Any(value =>
            value.Features.IntegratedSignalAdu >= options.AircraftMinimumIntegratedSignalAdu &&
            BlinkingProfile(value.Features.BrightnessProfile, options.AircraftMinimumBrightnessRatio));

        if ((compact && (observations.Length == 1 || stationary)) || thinDetectorResidual)
        {
            var sensorReason = thinDetectorResidual && !compact
                ? TransientAssessmentReasonCodes.ThinDetectorResidual
                : TransientAssessmentReasonCodes.CompactResidual;
            return Result(
                TransientClassification.SensorArtifact,
                null,
                900_000,
                [Reason(sensorReason, TransientReasonKind.Supporting, ids),
                 .. stationary && observations.Length > 1
                    ? new[] { Reason(TransientAssessmentReasonCodes.StationaryResidual, TransientReasonKind.Supporting, ids) }
                    : []],
                observations);
        }
        if (broad && !elongated)
        {
            return Result(
                TransientClassification.EnvironmentalArtifact,
                null,
                750_000,
                [Reason(TransientAssessmentReasonCodes.BroadResidual, TransientReasonKind.Supporting, ids)],
                observations);
        }
        if (elongated && integrated >= options.FireballMinimumIntegratedSignalAdu && (flare || fragmented || saturated > 0))
        {
            var reasons = new List<TransientReasonV1>
            {
                Reason(TransientAssessmentReasonCodes.ElongatedTrack, TransientReasonKind.Supporting, ids)
            };
            if (flare)
            {
                reasons.Add(Reason(TransientAssessmentReasonCodes.FlareProfile, TransientReasonKind.Supporting, ids));
            }
            if (fragmented)
            {
                reasons.Add(Reason(TransientAssessmentReasonCodes.FragmentedTrack, TransientReasonKind.Supporting, ids));
            }
            if (saturated > 0)
            {
                reasons.Add(Reason(TransientAssessmentReasonCodes.SaturatedBrightness, TransientReasonKind.Limitation, ids));
            }
            return Result(TransientClassification.Meteor, TransientMeteorSeverity.Fireball, 950_000, reasons, observations);
        }
        if (smooth && blinking)
        {
            var reasons = new List<TransientReasonV1>
            {
                Reason(TransientAssessmentReasonCodes.TemporalBlinking, TransientReasonKind.Supporting, ids)
            };
            reasons.Add(Reason(TransientAssessmentReasonCodes.SmoothPersistentTrack, TransientReasonKind.Supporting, ids));
            return Result(
                TransientClassification.Aircraft,
                null,
                850_000,
                reasons,
                observations);
        }
        if (smooth)
        {
            return Result(
                TransientClassification.Satellite,
                null,
                800_000,
                [Reason(TransientAssessmentReasonCodes.SmoothPersistentTrack, TransientReasonKind.Supporting, ids)],
                observations);
        }
        if (elongated)
        {
            var reasons = new List<TransientReasonV1>
            {
                Reason(TransientAssessmentReasonCodes.ElongatedTrack, TransientReasonKind.Supporting, ids)
            };
            if (flare)
            {
                reasons.Add(Reason(TransientAssessmentReasonCodes.FlareProfile, TransientReasonKind.Supporting, ids));
            }
            if (fragmented)
            {
                reasons.Add(Reason(TransientAssessmentReasonCodes.FragmentedTrack, TransientReasonKind.Supporting, ids));
            }
            return Result(TransientClassification.Meteor, TransientMeteorSeverity.Meteor, 800_000, reasons, observations);
        }
        return Result(
            TransientClassification.Unknown,
            null,
            300_000,
            [Reason(TransientAssessmentReasonCodes.InsufficientEvidence, TransientReasonKind.Limitation, ids)],
            observations);
    }

    private static ClassificationResult Result(
        TransientClassification classification,
        TransientMeteorSeverity? severity,
        int confidence,
        IReadOnlyList<TransientReasonV1> reasons,
        TransientObservationV1[] observations)
    {
        if (observations.Length == 1)
        {
            reasons = reasons.Append(Reason(
                TransientAssessmentReasonCodes.SingleObservation,
                TransientReasonKind.Limitation,
                [observations[0].ObservationId])).ToArray();
        }
        return new ClassificationResult(classification, severity, confidence, reasons);
    }

    private static double FlareRatio(IReadOnlyList<TransientProfileSampleV1> profile)
    {
        var endpoint = (profile[0].Value + profile[^1].Value) / 2;
        return profile.Max(static value => value.Value) / Math.Max(endpoint, 1);
    }

    private static bool BlinkingProfile(IReadOnlyList<TransientProfileSampleV1> profile, double minimumRatio)
    {
        var peak = profile.Max(static value => value.Value);
        for (var index = 1; index < profile.Count - 1; index++)
        {
            if (profile[index].Value * minimumRatio <= peak &&
                profile.Take(index).Any(value => value.Value > profile[index].Value * minimumRatio) &&
                profile.Skip(index + 1).Any(value => value.Value > profile[index].Value * minimumRatio))
            {
                return true;
            }
        }
        return false;
    }

    private static double MaximumDisplacement(TransientObservationV1[] observations)
    {
        var first = Center(observations[0]);
        return observations.Skip(1).Select(value => Distance(first, Center(value))).DefaultIfEmpty(0).Max();
    }

    private static bool SmoothMotion(
        TransientObservationV1[] observations,
        double maximumTurnDegrees,
        double maximumStepRatio)
    {
        var centers = observations.Select(Center).ToArray();
        var steps = new (double X, double Y, double Length)[centers.Length - 1];
        for (var index = 0; index < steps.Length; index++)
        {
            var x = centers[index + 1].X - centers[index].X;
            var y = centers[index + 1].Y - centers[index].Y;
            var length = Math.Sqrt(x * x + y * y);
            if (length <= double.Epsilon)
            {
                return false;
            }
            steps[index] = (x, y, length);
        }
        var minimumStep = steps.Min(static value => value.Length);
        if (steps.Max(static value => value.Length) / minimumStep > maximumStepRatio)
        {
            return false;
        }
        var minimumCosine = Math.Cos(maximumTurnDegrees * Math.PI / 180);
        for (var index = 1; index < steps.Length; index++)
        {
            var cosine = (steps[index - 1].X * steps[index].X + steps[index - 1].Y * steps[index].Y) /
                (steps[index - 1].Length * steps[index].Length);
            if (cosine < minimumCosine)
            {
                return false;
            }
        }
        return true;
    }

    private static (double X, double Y) Center(TransientObservationV1 observation)
        => (observation.Geometry.Bounds.X + observation.Geometry.Bounds.Width / 2,
            observation.Geometry.Bounds.Y + observation.Geometry.Bounds.Height / 2);

    private static double Distance((double X, double Y) first, (double X, double Y) second)
        => Math.Sqrt(Math.Pow(second.X - first.X, 2) + Math.Pow(second.Y - first.Y, 2));

    private static TransientReasonV1 Reason(string code, TransientReasonKind kind, IReadOnlyList<Guid> ids)
        => new(code, kind, ids);

    private static (string ReasonCode, string Field)? ValidateRequest(
        TransientAssessmentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EventId == Guid.Empty || request.AssessmentId == Guid.Empty || request.EventId == request.AssessmentId ||
            request.CreatedUtc == default || request.CreatedUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(request.Authority) ||
            request.Observations is null or { Count: 0 } || request.Observations.Count > MaximumObservations ||
            request.Options is null || request.PriorAssessments is null ||
            request.PriorAssessments.Count > MaximumPriorAssessments || !ValidOptions(request.Options))
        {
            return (TransientAssessmentReasonCodes.InvalidRequest, "request");
        }
        if (request.Observations.Any(static value => value is null) ||
            request.Observations.Any(value => value.EventId != request.EventId ||
                !CanonicalSha256(value.ExtractionIdentitySha256)) ||
            !request.Observations.Select(static value => value.Observation.Ordinal)
                .SequenceEqual(Enumerable.Range(0, request.Observations.Count)) ||
            request.Observations.Select(static value => value.Observation.ObservationId).Distinct().Count() !=
                request.Observations.Count ||
            request.Observations.Any(value => !ValidObservation(value.Observation, cancellationToken)) ||
            request.CreatedUtc < request.Observations.Max(static value => value.Observation.Source.ObservationEndedUtc))
        {
            return (TransientAssessmentReasonCodes.InvalidRequest, "observations");
        }
        var observationIds = request.Observations.Select(static value => value.Observation.ObservationId).ToHashSet();
        if (!ValidPriorHistory(request.PriorAssessments, observationIds, request.CreatedUtc, cancellationToken) ||
            request.PriorAssessments.Any(value => value.AssessmentId == request.AssessmentId))
        {
            return (TransientAssessmentReasonCodes.InvalidHistory, "priorAssessments");
        }
        if (request.SupersedesAssessmentId is { } priorId)
        {
            var prior = request.PriorAssessments.SingleOrDefault(value => value.AssessmentId == priorId);
            if (prior is null || request.PriorAssessments.Any(value => value.SupersedesAssessmentId == priorId) ||
                prior.Producer.Kind != TransientAssessmentProducerKind.DeterministicAlgorithm ||
                !string.Equals(prior.Producer.Name, ProducerName, StringComparison.Ordinal) ||
                !string.Equals(prior.Producer.Version, ProducerVersion, StringComparison.Ordinal))
            {
                return (TransientAssessmentReasonCodes.InvalidHistory, "supersedesAssessmentId");
            }
        }
        return null;
    }

    internal static bool ValidOptions(TransientDeterministicAssessmentOptionsV1 options)
        => FinitePositive(options.MinimumMeteorLengthPixels) && FinitePositive(options.MinimumMeteorElongation) &&
           FinitePositive(options.MaximumMeteorMeanWidthPixels) && FinitePositive(options.CompactSensorMaximumLengthPixels) &&
           FinitePositive(options.SensorArtifactMaximumMeanWidthPixels) &&
           FiniteNonNegative(options.StationaryMaximumDisplacementPixels) &&
           !NegativeZero(options.StationaryMaximumDisplacementPixels) &&
           FinitePositive(options.EnvironmentalMinimumMeanWidthPixels) && options.FireballMinimumIntegratedSignalAdu > 0 &&
           FinitePositive(options.FlareMinimumPeakToEndpointRatio) && options.PersistentTrackMinimumObservations >= 3 &&
           FinitePositive(options.AircraftMinimumBrightnessRatio) &&
           options.AircraftMinimumIntegratedSignalAdu > 0 &&
           options.SmoothMotionMaximumTurnDegrees is > 0 and <= 180 &&
           options.SmoothMotionMaximumStepRatio >= 1 && double.IsFinite(options.SmoothMotionMaximumStepRatio);

    private static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
    private static bool FiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;
    private static bool NegativeZero(double value) => value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;

    private static bool ValidObservation(TransientObservationV1? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null || value.ObservationId == Guid.Empty || value.Source is null ||
            value.Geometry is null || value.Features is null ||
            !TransientContractJson.ValidateSourceEvidence(value.Source).IsValid ||
            value.Geometry.SourceEvidenceId != value.Source.EvidenceId ||
            value.Features.SourceEvidenceId != value.Source.EvidenceId || value.Geometry.Bounds is null ||
            !CanonicalNonNegative(value.Geometry.Bounds.X) || !CanonicalNonNegative(value.Geometry.Bounds.Y) ||
            !FinitePositive(value.Geometry.Bounds.Width) || !FinitePositive(value.Geometry.Bounds.Height) ||
            value.Geometry.Bounds.X + value.Geometry.Bounds.Width > value.Geometry.CoordinateWidth ||
            value.Geometry.Bounds.Y + value.Geometry.Bounds.Height > value.Geometry.CoordinateHeight ||
            value.Geometry.Polyline is null or { Count: < 2 } || value.Geometry.Polyline.Count > MaximumPolylinePoints ||
            value.Geometry.Polyline.Any(point =>
                !CanonicalNonNegative(point.X) || !CanonicalNonNegative(point.Y) ||
                point.X > value.Geometry.CoordinateWidth || point.Y > value.Geometry.CoordinateHeight) ||
            !CanonicalNonNegative(value.Features.LengthPixels) ||
            !CanonicalNonNegative(value.Features.MeanWidthPixels) ||
            !CanonicalNonNegative(value.Features.MaximumWidthPixels) ||
            value.Features.MaximumWidthPixels < value.Features.MeanWidthPixels ||
            value.Features.IntegratedSignalAdu < 0 || value.Features.SaturatedSampleCount < 0 ||
            value.Features.FragmentCount < 1 || !ValidProfile(value.Features.WidthProfile, cancellationToken) ||
            !ValidProfile(value.Features.BrightnessProfile, cancellationToken) || value.BackgroundArtifacts is null ||
            value.BackgroundArtifacts.Count > MaximumBackgroundArtifacts ||
            value.BackgroundArtifacts.Any(static artifact => artifact is null || artifact.ArtifactId == Guid.Empty ||
                artifact.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated) ||
                !Bounded(artifact.Variant) || !Sha256(artifact.RecipeIdentitySha256) || !Sha256(artifact.ChecksumSha256)) ||
            value.BackgroundArtifacts.Select(static artifact => artifact.ArtifactId).Distinct().Count() !=
                value.BackgroundArtifacts.Count ||
            value.BackgroundArtifacts.Any(artifact => artifact.ArtifactId == value.Source.Locator.Artifact.ArtifactId) ||
            value.Provenance is null || !Sha256(value.Provenance.DetectorInputIdentitySha256) ||
            !Bounded(value.Provenance.CalibrationIdentity) || !Bounded(value.Provenance.MaskIdentity) ||
            !Bounded(value.Provenance.ProcessingProfileIdentity) || value.Extraction is null ||
            value.Extraction.OriginatingCandidateId == Guid.Empty || value.Extraction.Producer is null ||
            !string.Equals(value.Extraction.Producer.SchemaVersion, TransientExtractionProducerV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            value.Extraction.Producer.Kind != TransientExtractionProducerKind.DeterministicAlgorithm ||
            !Bounded(value.Extraction.Producer.Name) || !Bounded(value.Extraction.Producer.Version) ||
            !Sha256(value.Extraction.RecipeIdentitySha256))
        {
            return false;
        }
        return true;
    }

    private static bool ValidPriorHistory(
        IReadOnlyList<TransientAssessmentV1> history,
        HashSet<Guid> observationIds,
        DateTimeOffset requestCreatedUtc,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<Guid, TransientAssessmentV1>();
        var superseded = new HashSet<Guid>();
        DateTimeOffset? priorCreatedUtc = null;
        foreach (var assessment in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assessment is null || assessment.AssessmentId == Guid.Empty || byId.ContainsKey(assessment.AssessmentId) ||
                assessment.CreatedUtc.Offset != TimeSpan.Zero || assessment.CreatedUtc >= requestCreatedUtc ||
                priorCreatedUtc is { } priorTime && assessment.CreatedUtc <= priorTime ||
                !Enum.IsDefined(assessment.Authority) || !Enum.IsDefined(assessment.Classification) ||
                (assessment.Classification == TransientClassification.Meteor) != assessment.MeteorSeverity.HasValue ||
                assessment.ConfidenceMillionths is < 0 or > 1_000_000 || assessment.Reasons is null or { Count: > MaximumReasons } ||
                assessment.Reasons.Any(reason => !ValidReason(reason, observationIds)) ||
                assessment.Reasons.Select(static reason => reason.Code).Distinct(StringComparer.Ordinal).Count() !=
                    assessment.Reasons.Count || assessment.Producer is null ||
                !string.Equals(assessment.Producer.SchemaVersion, TransientAssessmentProducerV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
                assessment.Producer.Kind != TransientAssessmentProducerKind.DeterministicAlgorithm ||
                !Bounded(assessment.Producer.Name) || !Bounded(assessment.Producer.Version) ||
                !Sha256(assessment.RecipeIdentitySha256) || assessment.EvidenceObservationIds is null or { Count: 0 } ||
                assessment.EvidenceObservationIds.Any(id => !observationIds.Contains(id)) ||
                assessment.EvidenceObservationIds.Distinct().Count() != assessment.EvidenceObservationIds.Count ||
                assessment.SupersedesAssessmentId == assessment.AssessmentId ||
                assessment.SupersedesAssessmentId is { } predecessor &&
                    (!byId.TryGetValue(predecessor, out var predecessorAssessment) || !superseded.Add(predecessor) ||
                     predecessorAssessment.Producer.Kind != assessment.Producer.Kind ||
                     !string.Equals(predecessorAssessment.Producer.Name, assessment.Producer.Name, StringComparison.Ordinal) ||
                     !string.Equals(predecessorAssessment.Producer.Version, assessment.Producer.Version, StringComparison.Ordinal)))
            {
                return false;
            }
            byId.Add(assessment.AssessmentId, assessment);
            priorCreatedUtc = assessment.CreatedUtc;
        }
        return true;
    }

    private static bool ValidReason(TransientReasonV1? reason, HashSet<Guid> observationIds)
        => reason is not null && ReasonCode(reason.Code) && Enum.IsDefined(reason.Kind) &&
           reason.ObservationIds is not null && reason.ObservationIds.All(observationIds.Contains) &&
           reason.ObservationIds.Distinct().Count() == reason.ObservationIds.Count;

    private static bool ValidProfile(
        IReadOnlyList<TransientProfileSampleV1>? profile,
        CancellationToken cancellationToken)
    {
        if (profile is null or { Count: < 2 } || profile.Count > MaximumProfileSamples)
        {
            return false;
        }
        var prior = -1;
        foreach (var sample in profile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sample is null || sample.PositionMillionths is < 0 or > 1_000_000 ||
                sample.PositionMillionths <= prior || !CanonicalNonNegative(sample.Value))
            {
                return false;
            }
            prior = sample.PositionMillionths;
        }
        return true;
    }

    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool CanonicalSha256(string? value) => Sha256(value) &&
        string.Equals(value, value!.ToUpperInvariant(), StringComparison.Ordinal);
    private static bool CanonicalNonNegative(double value) => FiniteNonNegative(value) && !NegativeZero(value);
    private static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim();
    internal static bool ReasonCode(string? value) => Bounded(value) && value!.All(static character =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    private static string[] ComputeObservationIdentities(
        IReadOnlyList<TransientAssessmentObservationV1> observations,
        CancellationToken cancellationToken)
    {
        var identities = new string[observations.Count];
        for (var index = 0; index < observations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = observations[index];
            var observation = input.Observation;
            identities[index] = CaptureContractJson.ComputeCanonicalJsonSha256(input with
            {
                ExtractionIdentitySha256 = input.ExtractionIdentitySha256.ToUpperInvariant(),
                Observation = observation with
                {
                    Source = TransientContractJson.NormalizeSourceEvidence(observation.Source),
                    BackgroundArtifacts = observation.BackgroundArtifacts.Select(static artifact => artifact with
                    {
                        RecipeIdentitySha256 = artifact.RecipeIdentitySha256.ToUpperInvariant(),
                        ChecksumSha256 = artifact.ChecksumSha256.ToUpperInvariant()
                    }).ToArray(),
                    Provenance = observation.Provenance with
                    {
                        DetectorInputIdentitySha256 = observation.Provenance.DetectorInputIdentitySha256.ToUpperInvariant()
                    },
                    Extraction = observation.Extraction with
                    {
                        RecipeIdentitySha256 = observation.Extraction.RecipeIdentitySha256.ToUpperInvariant()
                    }
                }
            });
        }
        return identities;
    }

    private static string[] ComputePriorAssessmentIdentities(
        IReadOnlyList<TransientAssessmentV1> assessments,
        CancellationToken cancellationToken)
    {
        var identities = new string[assessments.Count];
        for (var index = 0; index < assessments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            identities[index] = CaptureContractJson.ComputeCanonicalJsonSha256(assessments[index] with
            {
                RecipeIdentitySha256 = assessments[index].RecipeIdentitySha256.ToUpperInvariant()
            });
        }
        return identities;
    }

    private static TransientAssessmentExecutionOutcome Failure(
        string reason,
        string field,
        IReadOnlyList<TransientAssessmentV1> history)
        => new(TransientAssessmentExecutionStatus.Invalid, reason, field, null, history);

    private sealed record ClassificationResult(
        TransientClassification Classification,
        TransientMeteorSeverity? Severity,
        int ConfidenceMillionths,
        IReadOnlyList<TransientReasonV1> Reasons);
}

public static class TransientAssessmentJson
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static byte[] Serialize(TransientAssessmentExecutionDescriptorV1 descriptor)
    {
        Validate(descriptor);
        var output = SerializeCanonical(descriptor);
        if (output.Length > MaximumBytes)
        {
            throw new ArgumentException("Assessment receipt exceeds its size limit.", nameof(descriptor));
        }
        return output;
    }

    internal static bool IsWithinSizeLimit(TransientAssessmentExecutionDescriptorV1 descriptor)
        => SerializeCanonical(descriptor).Length <= MaximumBytes;

    public static TransientAssessmentExecutionDescriptorV1 Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaximumBytes)
        {
            throw new ArgumentException("Assessment receipt is empty or oversized.", nameof(utf8Json));
        }
        try
        {
            var descriptor = JsonSerializer.Deserialize<TransientAssessmentExecutionDescriptorV1>(utf8Json, JsonOptions)
                ?? throw new ArgumentException("Assessment receipt is null.", nameof(utf8Json));
            Validate(descriptor);
            return descriptor;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Assessment receipt JSON is invalid.", nameof(utf8Json), exception);
        }
    }

    public static void Validate(TransientAssessmentExecutionDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.Equals(descriptor.SchemaVersion, TransientAssessmentExecutionDescriptorV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            descriptor.EventId == Guid.Empty || !CanonicalSha256(descriptor.ExecutionIdentitySha256) ||
            !CanonicalSha256(descriptor.OptionsIdentitySha256) || descriptor.Options is null ||
            descriptor.OrderedObservationIds is null or { Count: 0 } ||
            descriptor.OrderedObservationIdentitySha256s is null || descriptor.PriorAssessmentIds is null ||
            descriptor.PriorAssessmentIdentitySha256s is null ||
            descriptor.Assessment is null || descriptor.Assessment.AssessmentId == Guid.Empty ||
            descriptor.Assessment.EvidenceObservationIds is null ||
            !TransientAssessmentFactory.ValidOptions(descriptor.Options) ||
            !string.Equals(
                descriptor.OptionsIdentitySha256,
                TransientAssessmentFactory.ComputeOptionsIdentitySha256(descriptor.Options),
                StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.ExecutionIdentitySha256,
                TransientAssessmentFactory.ComputeExecutionIdentitySha256(descriptor),
                StringComparison.Ordinal) ||
            !descriptor.Assessment.EvidenceObservationIds.SequenceEqual(descriptor.OrderedObservationIds) ||
            descriptor.OrderedObservationIdentitySha256s.Count != descriptor.OrderedObservationIds.Count ||
            descriptor.OrderedObservationIdentitySha256s.Any(static value => !CanonicalSha256(value)) ||
            descriptor.OrderedObservationIds.Distinct().Count() != descriptor.OrderedObservationIds.Count ||
            descriptor.PriorAssessmentIdentitySha256s.Count != descriptor.PriorAssessmentIds.Count ||
            descriptor.PriorAssessmentIdentitySha256s.Any(static value => !CanonicalSha256(value)) ||
            descriptor.PriorAssessmentIds.Distinct().Count() != descriptor.PriorAssessmentIds.Count ||
            descriptor.PriorAssessmentIds.Contains(descriptor.Assessment.AssessmentId) ||
            !Enum.IsDefined(descriptor.Assessment.Authority) || !Enum.IsDefined(descriptor.Assessment.Classification) ||
            descriptor.Assessment.MeteorSeverity is { } severity && !Enum.IsDefined(severity) ||
            (descriptor.Assessment.Classification == TransientClassification.Meteor) != descriptor.Assessment.MeteorSeverity.HasValue ||
            descriptor.Assessment.CreatedUtc == default || descriptor.Assessment.CreatedUtc.Offset != TimeSpan.Zero ||
            descriptor.Assessment.ConfidenceMillionths is < 0 or > 1_000_000 ||
            descriptor.Assessment.Reasons is null or { Count: 0 or > 32 } || descriptor.Assessment.Reasons.Any(static reason =>
                reason is null || !TransientAssessmentFactory.ReasonCode(reason.Code) || !Enum.IsDefined(reason.Kind) ||
                reason.ObservationIds is null || reason.ObservationIds.Any(id => id == Guid.Empty)) ||
            descriptor.Assessment.Reasons.Select(static reason => reason.Code).Distinct(StringComparer.Ordinal).Count() !=
                descriptor.Assessment.Reasons.Count ||
            descriptor.Assessment.Reasons.Any(reason => reason.ObservationIds.Any(id =>
                !descriptor.OrderedObservationIds.Contains(id)) ||
                reason.ObservationIds.Distinct().Count() != reason.ObservationIds.Count) ||
            descriptor.Assessment.Producer is null ||
            descriptor.Assessment.Producer.Kind != TransientAssessmentProducerKind.DeterministicAlgorithm ||
            !string.Equals(descriptor.Assessment.Producer.SchemaVersion, TransientAssessmentProducerV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(descriptor.Assessment.Producer.Name, TransientAssessmentFactory.ProducerName, StringComparison.Ordinal) ||
            !string.Equals(descriptor.Assessment.Producer.Version, TransientAssessmentFactory.ProducerVersion, StringComparison.Ordinal) ||
            !CanonicalSha256(descriptor.Assessment.RecipeIdentitySha256) ||
            !string.Equals(
                descriptor.Assessment.RecipeIdentitySha256,
                TransientAssessmentFactory.ComputeRecipeIdentitySha256(descriptor.Options),
                StringComparison.Ordinal) ||
            descriptor.Assessment.SupersedesAssessmentId is { } priorId && !descriptor.PriorAssessmentIds.Contains(priorId))
        {
            throw new ArgumentException("Assessment receipt is invalid.", nameof(descriptor));
        }
    }

    private static bool CanonicalSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit) &&
           string.Equals(value, value.ToUpperInvariant(), StringComparison.Ordinal);

    private static byte[] SerializeCanonical(TransientAssessmentExecutionDescriptorV1 descriptor)
        => JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            JsonSerializer.SerializeToElement(descriptor, JsonOptions)));
}
