using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public enum TransientCandidateExtractionStatus
{
    Produced,
    NoCandidate,
    Invalid,
    LimitExceeded
}

public sealed record TransientCandidateExtractionOptionsV1(
    [property: JsonRequired] ushort MinimumResidualAdu,
    [property: JsonRequired] int MinimumComponentPixels,
    [property: JsonRequired] long MinimumIntegratedSignalAdu,
    [property: JsonRequired] int MaximumCandidates,
    [property: JsonRequired] int ProfileSampleCount,
    [property: JsonRequired] int MaximumSaturationBridgePixels,
    [property: JsonRequired] int MaximumForegroundPixels,
    [property: JsonRequired] double MaximumFragmentGapPixels,
    [property: JsonRequired] double MinimumFragmentAlignmentCosine);

public static class TransientCandidateExtractionProfiles
{
    public static TransientCandidateExtractionOptionsV1 EdgeV1 { get; } = new(
        50, 4, 500, 32, 16, 4_096, 1_000_000, 4, 0.9);
}

public sealed record TransientCandidateIdentitySlot(Guid CandidateId, Guid EventId);

public sealed record TransientCandidateExtractionSourceV1(
    [property: JsonRequired] TransientTemporalPosition Position,
    [property: JsonRequired] string DetectorInputIdentitySha256,
    [property: JsonRequired] TransientSourceEvidenceReferenceV1 Source);

/// <summary>Host-neutral extraction request over a verified target and temporal-background product.</summary>
public sealed record TransientCandidateExtractionRequest(
    string AgentId,
    DateTimeOffset CreatedUtc,
    TransientTemporalSource Target,
    TransientTemporalBackgroundProduct Background,
    IReadOnlyList<TransientTemporalSource> OrderedSources,
    IReadOnlyList<TransientCandidateIdentitySlot> IdentitySlots,
    TransientCandidateExtractionOptionsV1 Options,
    bool CenteredContextConverged = false);

/// <summary>Persistable receipt binding options, background lineage, masks, algorithms, and exact candidates.</summary>
public sealed record TransientCandidateExtractionDescriptorV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string ExtractionIdentitySha256,
    [property: JsonRequired] string OptionsIdentitySha256,
    [property: JsonRequired] TransientCandidateExtractionOptionsV1 Options,
    [property: JsonRequired] string TargetDetectorInputIdentitySha256,
    [property: JsonRequired] TransientTemporalBackgroundDescriptorV1 Background,
    [property: JsonRequired] string HardExclusionMaskChecksumSha256,
    [property: JsonRequired] string NoSupportMaskChecksumSha256,
    [property: JsonRequired] string SaturationMaskChecksumSha256,
    [property: JsonRequired] bool CenteredContextConverged,
    [property: JsonRequired] IReadOnlyList<TransientCandidateExtractionSourceV1> OrderedSources,
    [property: JsonRequired] IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    [property: JsonRequired] IReadOnlyList<TransientCandidateV1> Candidates)
{
    public const string CurrentSchemaVersion = "transient-candidate-extraction-v1";
}

public sealed record TransientCandidateExtractionOutcome(
    TransientCandidateExtractionStatus Status,
    string? ReasonCode,
    string? Field,
    TransientCandidateExtractionDescriptorV1? Descriptor,
    IReadOnlyList<TransientCandidateV1> Candidates,
    int ForegroundPixelCount,
    int HardMaskedPixelCount,
    int SaturatedPixelCount,
    long BytesScanned);

public static class TransientCandidateExtractionReasonCodes
{
    public const string InvalidRequest = "transient-extraction.invalid-request";
    public const string InvalidBackground = "transient-extraction.invalid-background";
    public const string InvalidLineage = "transient-extraction.invalid-lineage";
    public const string InvalidMask = "transient-extraction.invalid-mask";
    public const string NoCandidate = "transient-extraction.no-candidate";
    public const string CandidateLimit = "transient-extraction.candidate-limit";
    public const string ResourceLimit = "transient-extraction.resource-limit";
}

public static class TransientCandidateExtractionFactory
{
    public const string ProducerName = "linear-component-extractor";
    public const string ProducerVersion = "linear-component-extractor-v1";
    private const int MaximumIdentityLength = 256;

    public static TransientCandidateExtractionOutcome Create(
        TransientCandidateExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return Failure(validation.Value.ReasonCode, validation.Value.Field);
        }

        var background = request.Background;
        var target = request.Target;
        var layout = target.Input.Descriptor.Layout;
        if (checked((long)layout.Width * layout.Height) > Linear16TransientExtraction.MaximumDetectorPixels)
        {
            return LimitFailure(TransientCandidateExtractionReasonCodes.ResourceLimit, "target.input.descriptor.layout");
        }
        var persistentMasks = target.Masks
            .OrderBy(static mask => mask.Kind)
            .ThenBy(static mask => mask.MaskIdentitySha256, StringComparer.Ordinal)
            .Select(static mask => mask.Mask)
            .ToArray();
        Linear16PixelMask hardMask;
        Linear16TransientExtractionResult extraction;
        try
        {
            hardMask = Linear16TransientExtraction.CreateHardExclusionMask(
                background.EffectiveMask,
                target.Input.SaturationMask,
                background.NoSupportMask,
                persistentMasks,
                cancellationToken);
            extraction = Linear16TransientExtraction.Extract(
                new Linear16Frame(
                    layout.Width,
                    layout.Height,
                    layout.StrideBytes,
                    CameraPixelFormat.Mono16,
                    target.Input.Pixels),
                new Linear16Frame(
                    background.Descriptor.Layout.Width,
                    background.Descriptor.Layout.Height,
                    background.Descriptor.Layout.StrideBytes,
                    CameraPixelFormat.Mono16,
                    background.Pixels),
                hardMask,
                target.Input.SaturationMask,
                new Linear16TransientExtractionOptions(
                    request.Options.MinimumResidualAdu,
                    request.Options.MinimumComponentPixels,
                    request.Options.MinimumIntegratedSignalAdu,
                    request.Options.MaximumCandidates,
                    request.Options.ProfileSampleCount,
                    request.Options.MaximumSaturationBridgePixels,
                    request.Options.MaximumForegroundPixels,
                    request.Options.MaximumFragmentGapPixels,
                    request.Options.MinimumFragmentAlignmentCosine),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return Failure(TransientCandidateExtractionReasonCodes.InvalidRequest, "request");
        }

        if (extraction.CandidateLimitExceeded)
        {
            var foregroundLimit = extraction.Limit == Linear16TransientExtractionLimit.ForegroundPixels;
            return new TransientCandidateExtractionOutcome(
                TransientCandidateExtractionStatus.LimitExceeded,
                foregroundLimit
                    ? TransientCandidateExtractionReasonCodes.ResourceLimit
                    : TransientCandidateExtractionReasonCodes.CandidateLimit,
                foregroundLimit ? "options.maximumForegroundPixels" : "options.maximumCandidates",
                null,
                [],
                extraction.ForegroundPixelCount,
                extraction.HardMaskedPixelCount,
                extraction.SaturatedPixelCount,
                extraction.BytesScanned);
        }

        var optionsIdentity = ComputeOptionsIdentitySha256(request.Options);
        var recipeIdentity = ComputeRecipeIdentitySha256(request.Options);
        var candidates = new TransientCandidateV1[extraction.Components.Count];
        for (var index = 0; index < extraction.Components.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates[index] = CreateCandidate(
                request,
                request.IdentitySlots[index],
                extraction.Components[index],
                recipeIdentity);
            var candidateValidation = TransientContractJson.Validate(candidates[index]);
            if (!candidateValidation.IsValid)
            {
                return Failure(TransientCandidateExtractionReasonCodes.InvalidRequest, candidateValidation.FieldPath ?? "candidate");
            }
        }

        var descriptor = new TransientCandidateExtractionDescriptorV1(
            TransientCandidateExtractionDescriptorV1.CurrentSchemaVersion,
            string.Empty,
            optionsIdentity,
            request.Options,
            target.Input.Descriptor.InputIdentitySha256.ToUpperInvariant(),
            background.Descriptor,
            Sha256(hardMask.Bits.Span),
            Sha256(background.NoSupportMask.Bits.Span),
            target.Input.Descriptor.SaturationMaskChecksumSha256.ToUpperInvariant(),
            request.CenteredContextConverged,
            request.OrderedSources.Select(static source => new TransientCandidateExtractionSourceV1(
                source.Position,
                source.Input.Descriptor.InputIdentitySha256.ToUpperInvariant(),
                NormalizeSource(source.Input.Descriptor.Source))).ToArray(),
            [
                new ProcessingAlgorithmIdentity(ProducerName, ProducerVersion),
                new ProcessingAlgorithmIdentity("linear16-transient-components", extraction.AlgorithmVersion)
            ],
            candidates);
        descriptor = descriptor with { ExtractionIdentitySha256 = ComputeExtractionIdentitySha256(descriptor) };
        TransientCandidateExtractionJson.Validate(descriptor);
        if (!TransientCandidateExtractionJson.IsWithinSizeLimit(descriptor))
        {
            return new TransientCandidateExtractionOutcome(
                TransientCandidateExtractionStatus.LimitExceeded,
                TransientCandidateExtractionReasonCodes.CandidateLimit,
                "descriptor",
                null,
                [],
                extraction.ForegroundPixelCount,
                extraction.HardMaskedPixelCount,
                extraction.SaturatedPixelCount,
                extraction.BytesScanned);
        }
        return new TransientCandidateExtractionOutcome(
            candidates.Length == 0 ? TransientCandidateExtractionStatus.NoCandidate : TransientCandidateExtractionStatus.Produced,
            candidates.Length == 0 ? TransientCandidateExtractionReasonCodes.NoCandidate : null,
            candidates.Length == 0 ? "target" : null,
            descriptor,
            candidates,
            extraction.ForegroundPixelCount,
            extraction.HardMaskedPixelCount,
            extraction.SaturatedPixelCount,
            extraction.BytesScanned);
    }

    public static string ComputeOptionsIdentitySha256(TransientCandidateExtractionOptionsV1 options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "transient-candidate-extraction-options-v1",
            options
        });
    }

    public static string ComputeRecipeIdentitySha256(TransientCandidateExtractionOptionsV1 options)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "transient-candidate-extraction-recipe-v1",
            producer = new { name = ProducerName, version = ProducerVersion },
            optionsIdentitySha256 = ComputeOptionsIdentitySha256(options)
        });

    public static string ComputeExtractionIdentitySha256(TransientCandidateExtractionDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "transient-candidate-extraction-identity-v1",
            descriptor.SchemaVersion,
            descriptor.OptionsIdentitySha256,
            descriptor.Options,
            descriptor.TargetDetectorInputIdentitySha256,
            backgroundIdentitySha256 = descriptor.Background.BackgroundIdentitySha256,
            descriptor.HardExclusionMaskChecksumSha256,
            descriptor.NoSupportMaskChecksumSha256,
            descriptor.SaturationMaskChecksumSha256,
            descriptor.CenteredContextConverged,
            descriptor.OrderedSources,
            descriptor.Algorithms,
            descriptor.Candidates
        });
    }

    private static TransientCandidateV1 CreateCandidate(
        TransientCandidateExtractionRequest request,
        TransientCandidateIdentitySlot identity,
        Linear16TransientComponent component,
        string recipeIdentity)
    {
        var source = request.Target.Input.Descriptor.Source;
        var geometry = new TransientGeometryV1(
            source.EvidenceId,
            request.Target.Input.Descriptor.Layout.Width,
            request.Target.Input.Descriptor.Layout.Height,
            new TransientBoundingRegionV1(
                component.BoundsX,
                component.BoundsY,
                component.BoundsWidth,
                component.BoundsHeight),
            [
                new TransientPointV1(component.StartX, component.StartY),
                new TransientPointV1(component.EndX, component.EndY)
            ]);
        var features = new TransientFeaturesV1(
            source.EvidenceId,
            component.LengthPixels,
            component.MeanWidthPixels,
            component.MaximumWidthPixels,
            component.IntegratedSignalAdu,
            component.PeakSignalAdu,
            component.SaturatedSampleCount,
            component.FragmentCount,
            component.WidthProfile.Select(static sample =>
                new TransientProfileSampleV1(sample.PositionMillionths, sample.Value)).ToArray(),
            component.BrightnessProfile.Select(static sample =>
                new TransientProfileSampleV1(sample.PositionMillionths, sample.Value)).ToArray());
        var reasons = new List<TransientReasonV1>
        {
            new("transient.residual-component", TransientReasonKind.Supporting, [])
        };
        if (component.SaturatedSampleCount > 0)
        {
            reasons.Add(new("transient.saturated-photometry-unrecoverable", TransientReasonKind.Limitation, []));
        }
        if (component.FragmentCount > 1)
        {
            reasons.Add(new("transient.fragmented-support", TransientReasonKind.Supporting, []));
        }
        var complete = request.Background.Descriptor.Kind == TransientTemporalBackgroundKind.CenteredFinal &&
            request.CenteredContextConverged;
        if (!complete)
        {
            reasons.Add(new("transient.pending-centered-context", TransientReasonKind.Limitation, []));
        }
        var compatibility = request.Target.Input.Descriptor.Compatibility;
        return new TransientCandidateV1(
            TransientCandidateV1.CurrentSchemaVersion,
            identity.CandidateId,
            identity.EventId,
            request.AgentId,
            complete ? TransientCandidateState.Complete : TransientCandidateState.Provisional,
            request.CreatedUtc,
            source.EvidenceId,
            request.OrderedSources.Select(static value => NormalizeSource(value.Input.Descriptor.Source)).ToArray(),
            new TransientObservationProvenanceV1(
                request.Target.Input.Descriptor.InputIdentitySha256.ToUpperInvariant(),
                compatibility.Calibration,
                compatibility.Mask,
                compatibility.ProcessingProfile),
            new TransientObservationExtractionV1(
                identity.CandidateId,
                new TransientExtractionProducerV1(
                    TransientExtractionProducerV1.CurrentSchemaVersion,
                    TransientExtractionProducerKind.DeterministicAlgorithm,
                    ProducerName,
                    ProducerVersion),
                recipeIdentity),
            geometry,
            features,
            reasons);
    }

    private static (string ReasonCode, string Field)? ValidateRequest(TransientCandidateExtractionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId) || request.AgentId.Length > MaximumIdentityLength ||
            request.AgentId != request.AgentId.Trim() || request.CreatedUtc == default ||
            request.CreatedUtc.Offset != TimeSpan.Zero || request.Target is null || request.Background is null ||
            request.OrderedSources is null || request.IdentitySlots is null || request.Options is null)
        {
            return (TransientCandidateExtractionReasonCodes.InvalidRequest, "request");
        }
        if (!ValidOptions(request.Options) || request.IdentitySlots.Count < request.Options.MaximumCandidates ||
            request.IdentitySlots.Count > 4096 || request.IdentitySlots.Any(static slot =>
                slot is null || slot.CandidateId == Guid.Empty || slot.EventId == Guid.Empty || slot.CandidateId == slot.EventId) ||
            request.IdentitySlots.Select(static slot => slot.CandidateId).Distinct().Count() != request.IdentitySlots.Count ||
            request.IdentitySlots.Select(static slot => slot.EventId).Distinct().Count() != request.IdentitySlots.Count)
        {
            return (TransientCandidateExtractionReasonCodes.InvalidRequest, "identitySlots");
        }
        if (request.Target.Position != TransientTemporalPosition.N || request.Target.Input is null ||
            request.Target.Input.Descriptor is null || request.Target.Input.SaturationMask is null ||
            !TransientContractJson.Validate(request.Target.Input.Descriptor).IsValid)
        {
            return (TransientCandidateExtractionReasonCodes.InvalidRequest, "target");
        }
        var descriptor = request.Background.Descriptor;
        try
        {
            TransientTemporalBackgroundJson.Validate(new TransientTemporalBackgroundOutcomeDescriptorV1(
                TransientTemporalBackgroundOutcomeDescriptorV1.CurrentSchemaVersion,
                TransientTemporalBackgroundStatus.Produced,
                null,
                null,
                descriptor.Sources,
                descriptor));
        }
        catch (ArgumentException)
        {
            return (TransientCandidateExtractionReasonCodes.InvalidBackground, "background.descriptor");
        }
        if (!string.Equals(
                descriptor.TargetDetectorInputIdentitySha256,
                request.Target.Input.Descriptor.InputIdentitySha256,
                StringComparison.OrdinalIgnoreCase) ||
            request.Background.Pixels.Length != descriptor.Layout.ByteLength ||
            request.Background.EffectiveMask.Width != descriptor.Layout.Width ||
            request.Background.EffectiveMask.Height != descriptor.Layout.Height ||
            request.Background.NoSupportMask.Width != descriptor.Layout.Width ||
            request.Background.NoSupportMask.Height != descriptor.Layout.Height ||
            !string.Equals(Sha256(request.Background.Pixels.Span), descriptor.BackgroundChecksumSha256, StringComparison.Ordinal) ||
            !string.Equals(Sha256(request.Background.EffectiveMask.Bits.Span), descriptor.EffectiveMaskChecksumSha256, StringComparison.Ordinal) ||
            !string.Equals(Sha256(request.Background.NoSupportMask.Bits.Span), descriptor.NoSupportMaskChecksumSha256, StringComparison.Ordinal) ||
            !MaskIsSubset(request.Background.NoSupportMask.Bits.Span, request.Background.EffectiveMask.Bits.Span))
        {
            return (TransientCandidateExtractionReasonCodes.InvalidBackground, "background");
        }
        if (request.OrderedSources.Count != descriptor.Sources.Count ||
            request.OrderedSources.Any(static source => source?.Input?.Descriptor?.Source is null) ||
            request.OrderedSources.Where(static source => source?.Input?.Descriptor is not null)
                .Any(source => !TransientContractJson.Validate(source.Input.Descriptor).IsValid) ||
            !request.OrderedSources.Select(static source => source.Position)
                .SequenceEqual(descriptor.Sources.Select(static source => source.Position)) ||
            request.OrderedSources.Where(static source => source?.Input?.Descriptor?.Source is not null)
                .Zip(descriptor.Sources).Any(pair =>
                    pair.First.CaptureSequence != pair.Second.CaptureSequence ||
                    pair.First.Input.Descriptor.Source.EvidenceId != pair.Second.EvidenceId ||
                    !string.Equals(
                        pair.First.Input.Descriptor.InputIdentitySha256,
                        pair.Second.DetectorInputIdentitySha256,
                        StringComparison.OrdinalIgnoreCase) ||
                    pair.First.Input.Descriptor.Source.ObservationStartedUtc != pair.Second.ObservationStartedUtc ||
                    pair.First.Input.Descriptor.Source.ObservationEndedUtc != pair.Second.ObservationEndedUtc) ||
            request.OrderedSources.SingleOrDefault(source => source.Position == TransientTemporalPosition.N) is not { } targetSource ||
            !JsonSerializer.SerializeToUtf8Bytes(NormalizeSource(targetSource.Input.Descriptor.Source)).AsSpan().SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(NormalizeSource(request.Target.Input.Descriptor.Source))) ||
            request.CreatedUtc < request.OrderedSources.Max(static source => source.Input.Descriptor.Source.ObservationEndedUtc))
        {
            return (TransientCandidateExtractionReasonCodes.InvalidLineage, "orderedSources");
        }
        if (request.Target.Masks is null || request.Target.Masks.Count == 0 ||
            request.Target.Masks.Any(static mask => mask?.Mask is null))
        {
            return (TransientCandidateExtractionReasonCodes.InvalidMask, "target.masks");
        }
        var saturationIdentity = TransientTemporalBackgroundFactory.ComputeMaskIdentitySha256(
            TransientDetectorMaskKind.Saturation,
            new ProcessingAlgorithmIdentity("linear16-saturation-mask", "inclusive-threshold-v1"),
            request.Target.Input.SaturationMask);
        if (request.Target.Masks.Any(mask => mask.Algorithm is null ||
            !string.Equals(
                TransientTemporalBackgroundFactory.ComputeMaskIdentitySha256(mask.Kind, mask.Algorithm, mask.Mask),
                mask.MaskIdentitySha256,
                StringComparison.Ordinal)))
        {
            return (TransientCandidateExtractionReasonCodes.InvalidMask, "target.masks");
        }
        var expectedMaskIdentities = request.Target.Masks.Select(static mask => mask.MaskIdentitySha256)
            .Append(saturationIdentity)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var targetLineage = descriptor.Sources.Single(static source => source.Position == TransientTemporalPosition.N);
        if (!expectedMaskIdentities.SequenceEqual(targetLineage.MaskIdentitySha256s.Order(StringComparer.Ordinal)) ||
            targetLineage.CaptureSequence != request.Target.CaptureSequence ||
            targetLineage.EvidenceId != request.Target.Input.Descriptor.Source.EvidenceId)
        {
            return (TransientCandidateExtractionReasonCodes.InvalidMask, "target.masks");
        }
        return null;
    }

    internal static bool ValidOptions(TransientCandidateExtractionOptionsV1 options)
        => options.MinimumResidualAdu > 0 && options.MinimumComponentPixels > 0 &&
           options.MinimumIntegratedSignalAdu > 0 && options.MaximumCandidates is > 0 and <= 64 &&
           options.ProfileSampleCount is >= 2 and <= 64 &&
           options.MaximumSaturationBridgePixels is > 0 and <= 1_000_000 &&
           options.MaximumForegroundPixels is > 0 and <= 10_000_000 &&
           double.IsFinite(options.MaximumFragmentGapPixels) && options.MaximumFragmentGapPixels is >= 0 and <= 1024 &&
           !NegativeZero(options.MaximumFragmentGapPixels) &&
           double.IsFinite(options.MinimumFragmentAlignmentCosine) && options.MinimumFragmentAlignmentCosine is >= 0 and <= 1 &&
           !NegativeZero(options.MinimumFragmentAlignmentCosine);

    private static bool NegativeZero(double value)
        => value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;

    private static TransientSourceEvidenceReferenceV1 NormalizeSource(TransientSourceEvidenceReferenceV1 source)
        => TransientContractJson.NormalizeSourceEvidence(source);

    private static string Sha256(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value));

    private static bool MaskIsSubset(ReadOnlySpan<byte> subset, ReadOnlySpan<byte> superset)
    {
        if (subset.Length != superset.Length)
        {
            return false;
        }
        for (var index = 0; index < subset.Length; index++)
        {
            if ((subset[index] & ~superset[index]) != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static TransientCandidateExtractionOutcome Failure(string reasonCode, string field)
        => new(TransientCandidateExtractionStatus.Invalid, reasonCode, field, null, [], 0, 0, 0, 0);

    private static TransientCandidateExtractionOutcome LimitFailure(string reasonCode, string field)
        => new(TransientCandidateExtractionStatus.LimitExceeded, reasonCode, field, null, [], 0, 0, 0, 0);
}

public static class TransientCandidateExtractionJson
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static byte[] Serialize(TransientCandidateExtractionDescriptorV1 descriptor)
    {
        Validate(descriptor);
        var output = SerializeCanonical(descriptor);
        if (output.Length > MaximumBytes)
        {
            throw new ArgumentException("Candidate extraction receipt exceeds its size limit.", nameof(descriptor));
        }
        return output;
    }

    internal static bool IsWithinSizeLimit(TransientCandidateExtractionDescriptorV1 descriptor)
        => SerializeCanonical(descriptor).Length <= MaximumBytes;

    public static TransientCandidateExtractionDescriptorV1 Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaximumBytes)
        {
            throw new ArgumentException("Candidate extraction receipt is empty or oversized.", nameof(utf8Json));
        }
        try
        {
            var descriptor = JsonSerializer.Deserialize<TransientCandidateExtractionDescriptorV1>(utf8Json, JsonOptions)
                ?? throw new ArgumentException("Candidate extraction receipt is null.", nameof(utf8Json));
            Validate(descriptor);
            return descriptor;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Candidate extraction receipt JSON is invalid.", nameof(utf8Json), exception);
        }
    }

    public static void Validate(TransientCandidateExtractionDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.Equals(descriptor.SchemaVersion, TransientCandidateExtractionDescriptorV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !CanonicalSha256(descriptor.ExtractionIdentitySha256) || !CanonicalSha256(descriptor.OptionsIdentitySha256) ||
            descriptor.Options is null || descriptor.Background is null ||
            !CanonicalSha256(descriptor.TargetDetectorInputIdentitySha256) ||
            !CanonicalSha256(descriptor.HardExclusionMaskChecksumSha256) ||
            !CanonicalSha256(descriptor.NoSupportMaskChecksumSha256) ||
            !CanonicalSha256(descriptor.SaturationMaskChecksumSha256) || descriptor.OrderedSources is null ||
            descriptor.Algorithms is null || descriptor.Candidates is null ||
            !TransientCandidateExtractionFactory.ValidOptions(descriptor.Options) ||
            !string.Equals(
                descriptor.OptionsIdentitySha256,
                TransientCandidateExtractionFactory.ComputeOptionsIdentitySha256(descriptor.Options),
                StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.ExtractionIdentitySha256,
                TransientCandidateExtractionFactory.ComputeExtractionIdentitySha256(descriptor),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Candidate extraction receipt identity is invalid.", nameof(descriptor));
        }
        try
        {
            TransientTemporalBackgroundJson.Validate(new TransientTemporalBackgroundOutcomeDescriptorV1(
                TransientTemporalBackgroundOutcomeDescriptorV1.CurrentSchemaVersion,
                TransientTemporalBackgroundStatus.Produced,
                null,
                null,
                descriptor.Background.Sources,
                descriptor.Background));
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Candidate extraction background is invalid.", nameof(descriptor), exception);
        }
        if (descriptor.OrderedSources.Any(static source => source?.Source is null))
        {
            throw new ArgumentException("Candidate extraction source lineage is invalid.", nameof(descriptor));
        }
        if (!string.Equals(
                descriptor.TargetDetectorInputIdentitySha256,
                descriptor.Background.TargetDetectorInputIdentitySha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.NoSupportMaskChecksumSha256,
                descriptor.Background.NoSupportMaskChecksumSha256,
                StringComparison.Ordinal) ||
            descriptor.OrderedSources.Count != descriptor.Background.Sources.Count ||
            !descriptor.OrderedSources.Select(static source => source.Source.EvidenceId)
                .SequenceEqual(descriptor.Background.Sources.Select(static source => source.EvidenceId)) ||
            descriptor.Algorithms.Count != 2 || descriptor.Algorithms.Any(static algorithm =>
                algorithm is null || string.IsNullOrWhiteSpace(algorithm.Name) || string.IsNullOrWhiteSpace(algorithm.Version)) ||
            !string.Equals(descriptor.Algorithms[0].Name, TransientCandidateExtractionFactory.ProducerName, StringComparison.Ordinal) ||
            !string.Equals(descriptor.Algorithms[0].Version, TransientCandidateExtractionFactory.ProducerVersion, StringComparison.Ordinal) ||
            !string.Equals(descriptor.Algorithms[1].Name, "linear16-transient-components", StringComparison.Ordinal) ||
            !string.Equals(descriptor.Algorithms[1].Version, Linear16TransientExtraction.AlgorithmVersion, StringComparison.Ordinal) ||
            descriptor.OrderedSources.Any(source => !Enum.IsDefined(source.Position) ||
                !CanonicalSha256(source.DetectorInputIdentitySha256) || source.Source is null ||
                !TransientContractJson.ValidateSourceEvidence(source.Source).IsValid || !CanonicalSource(source.Source)) ||
            descriptor.OrderedSources.Zip(descriptor.Background.Sources).Any(pair =>
                pair.First.Position != pair.Second.Position ||
                pair.First.Source.EvidenceId != pair.Second.EvidenceId ||
                !string.Equals(pair.First.DetectorInputIdentitySha256, pair.Second.DetectorInputIdentitySha256, StringComparison.Ordinal) ||
                pair.First.Source.ObservationStartedUtc != pair.Second.ObservationStartedUtc ||
                pair.First.Source.ObservationEndedUtc != pair.Second.ObservationEndedUtc) ||
            descriptor.Candidates.Count > descriptor.Options.MaximumCandidates ||
            descriptor.Candidates.Any(candidate => !TransientContractJson.Validate(candidate).IsValid) ||
            descriptor.Candidates.Any(candidate => !ValidCandidateProfiles(candidate, descriptor.Options.ProfileSampleCount)) ||
            descriptor.Candidates.Any(candidate => !CanonicalSha256(candidate.Provenance.DetectorInputIdentitySha256) ||
                !CanonicalSha256(candidate.Extraction.RecipeIdentitySha256) ||
                candidate.ContextSources.Any(static source => !CanonicalSource(source))) ||
            descriptor.Candidates.Any(candidate => !candidate.ContextSources.Select(static source => source.EvidenceId)
                .SequenceEqual(descriptor.OrderedSources.Select(static source => source.Source.EvidenceId))) ||
            descriptor.Candidates.Select(static candidate => candidate.CandidateId).Distinct().Count() != descriptor.Candidates.Count ||
            descriptor.Candidates.Select(static candidate => candidate.EventId).Distinct().Count() != descriptor.Candidates.Count ||
            descriptor.Candidates.Any(candidate => !string.Equals(
                candidate.Extraction.RecipeIdentitySha256,
                TransientCandidateExtractionFactory.ComputeRecipeIdentitySha256(descriptor.Options),
                StringComparison.Ordinal)) ||
            descriptor.Candidates.Any(candidate =>
                candidate.CenterEvidenceId != descriptor.Background.Sources.Single(
                    static source => source.Position == TransientTemporalPosition.N).EvidenceId ||
                !string.Equals(
                    candidate.Provenance.DetectorInputIdentitySha256,
                    descriptor.TargetDetectorInputIdentitySha256,
                    StringComparison.Ordinal) ||
                candidate.Geometry?.CoordinateWidth != descriptor.Background.Layout.Width ||
                candidate.Geometry?.CoordinateHeight != descriptor.Background.Layout.Height ||
                candidate.Extraction.Producer.Kind != TransientExtractionProducerKind.DeterministicAlgorithm ||
                !string.Equals(candidate.Extraction.Producer.Name, TransientCandidateExtractionFactory.ProducerName, StringComparison.Ordinal) ||
                !string.Equals(candidate.Extraction.Producer.Version, TransientCandidateExtractionFactory.ProducerVersion, StringComparison.Ordinal)) ||
            descriptor.Candidates.Any(candidate => candidate.State !=
                (descriptor.Background.Kind == TransientTemporalBackgroundKind.CenteredFinal && descriptor.CenteredContextConverged
                    ? TransientCandidateState.Complete
                    : TransientCandidateState.Provisional)))
        {
            throw new ArgumentException("Candidate extraction receipt lineage is invalid.", nameof(descriptor));
        }
    }

    private static bool CanonicalSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit) &&
           string.Equals(value, value.ToUpperInvariant(), StringComparison.Ordinal);

    private static bool CanonicalSource(TransientSourceEvidenceReferenceV1 source)
        => CanonicalSha256(source.Locator.Artifact.RecipeIdentitySha256) &&
            CanonicalSha256(source.Locator.Artifact.ChecksumSha256);

    private static bool ValidCandidateProfiles(TransientCandidateV1 candidate, int sampleCount)
    {
        if (candidate.Geometry?.Bounds is null || candidate.Features is null ||
            NegativeZero(candidate.Geometry.Bounds.X) || NegativeZero(candidate.Geometry.Bounds.Y) ||
            NegativeZero(candidate.Geometry.Bounds.Width) || NegativeZero(candidate.Geometry.Bounds.Height) ||
            candidate.Geometry.Polyline.Any(static point => NegativeZero(point.X) || NegativeZero(point.Y)) ||
            NegativeZero(candidate.Features.LengthPixels) || NegativeZero(candidate.Features.MeanWidthPixels) ||
            NegativeZero(candidate.Features.MaximumWidthPixels) ||
            candidate.Features.WidthProfile.Count != sampleCount || candidate.Features.BrightnessProfile.Count != sampleCount)
        {
            return false;
        }
        for (var index = 0; index < sampleCount; index++)
        {
            var expectedPosition = (int)((long)index * 1_000_000 / (sampleCount - 1));
            if (candidate.Features.WidthProfile[index].PositionMillionths != expectedPosition ||
                candidate.Features.BrightnessProfile[index].PositionMillionths != expectedPosition ||
                NegativeZero(candidate.Features.WidthProfile[index].Value) ||
                NegativeZero(candidate.Features.BrightnessProfile[index].Value))
            {
                return false;
            }
        }
        return true;
    }

    private static bool NegativeZero(double value)
        => value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;

    private static byte[] SerializeCanonical(TransientCandidateExtractionDescriptorV1 descriptor)
        => JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            JsonSerializer.SerializeToElement(descriptor, JsonOptions)));
}
