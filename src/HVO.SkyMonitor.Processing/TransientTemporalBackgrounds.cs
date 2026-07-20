using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public enum TransientTemporalBackgroundKind
{
    CausalProvisional,
    CenteredFinal
}

public enum TransientDetectorExecutionMode
{
    Off,
    Edge,
    Central,
    Hybrid
}

/// <summary>Prevents temporal request/window construction when transient processing is off.</summary>
public static class TransientTemporalWindowActivation
{
    public static TWindow? CreateWhenEnabled<TWindow>(
        TransientDetectorExecutionMode mode,
        Func<TWindow> createWindow)
        where TWindow : class
    {
        ArgumentNullException.ThrowIfNull(createWindow);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return mode == TransientDetectorExecutionMode.Off ? null : createWindow();
    }
}

public enum TransientTemporalPosition
{
    NMinus2 = -2,
    NMinus1 = -1,
    N = 0,
    NPlus1 = 1,
    NPlus2 = 2
}

public enum TransientDetectorMaskKind
{
    Sky,
    ImageCircle,
    Horizon,
    Obstruction,
    BadPixel,
    Saturation,
    Star
}

public enum TransientTemporalSourceDisposition
{
    Target,
    Available,
    Included,
    ExcludedKnownEvent
}

public enum TransientTemporalBackgroundStatus
{
    Produced,
    Missing,
    TimedOut,
    Incompatible
}

/// <summary>A calibrated relative linear response represented as an exact positive rational.</summary>
public sealed record TransientSensitivityV1(
    [property: JsonRequired] string ResponseIdentity,
    [property: JsonRequired] uint Numerator,
    [property: JsonRequired] uint Denominator);

/// <summary>One typed detector-coordinate mask and its immutable content identity.</summary>
public sealed record TransientDetectorMask(
    TransientDetectorMaskKind Kind,
    string MaskIdentitySha256,
    ProcessingAlgorithmIdentity Algorithm,
    Linear16PixelMask Mask)
{
    /// <summary>Creates a mask whose identity covers kind, algorithm, geometry, and exact bit storage.</summary>
    public static TransientDetectorMask Create(
        TransientDetectorMaskKind kind,
        ProcessingAlgorithmIdentity algorithm,
        Linear16PixelMask mask)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(mask);
        var identity = TransientTemporalBackgroundFactory.ComputeMaskIdentitySha256(kind, algorithm, mask);
        return new TransientDetectorMask(kind, identity, algorithm, mask);
    }
}

/// <summary>One exact temporal position with detector pixels, response calibration, masks, and sequence identity.</summary>
public sealed record TransientTemporalSource(
    TransientTemporalPosition Position,
    long CaptureSequence,
    TransientDetectorInput Input,
    TransientSensitivityV1 Sensitivity,
    IReadOnlyList<TransientDetectorMask> Masks);

/// <summary>Host-neutral request over already resolved whole-frame detector inputs.</summary>
public sealed record TransientTemporalBackgroundRequest(
    TransientTemporalBackgroundKind Kind,
    TransientTemporalSource Target,
    IReadOnlyList<TransientTemporalSource> Context,
    IReadOnlyList<Guid> KnownEventEvidenceIds,
    TimeSpan MaximumAdjacentStartInterval,
    bool DeadlineExpired = false);

/// <summary>Ordered source lineage, including exact normalization and event exclusions.</summary>
public sealed record TransientTemporalSourceLineageV1(
    [property: JsonRequired] TransientTemporalPosition Position,
    [property: JsonRequired] long CaptureSequence,
    [property: JsonRequired] Guid EvidenceId,
    [property: JsonRequired] string DetectorInputIdentitySha256,
    [property: JsonRequired] DateTimeOffset ObservationStartedUtc,
    [property: JsonRequired] DateTimeOffset ObservationEndedUtc,
    [property: JsonRequired] TransientSensitivityV1 Sensitivity,
    [property: JsonRequired] uint NormalizationNumerator,
    [property: JsonRequired] uint NormalizationDenominator,
    [property: JsonRequired] IReadOnlyList<string> MaskIdentitySha256s,
    [property: JsonRequired] TransientTemporalSourceDisposition Disposition);

/// <summary>Persistable identity and complete lineage for one produced temporal background.</summary>
public sealed record TransientTemporalBackgroundDescriptorV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string BackgroundIdentitySha256,
    [property: JsonRequired] TransientTemporalBackgroundKind Kind,
    [property: JsonRequired] string TargetDetectorInputIdentitySha256,
    [property: JsonRequired] FrameLayoutDescriptor Layout,
    [property: JsonRequired] string BackgroundChecksumSha256,
    [property: JsonRequired] string EffectiveMaskChecksumSha256,
    [property: JsonRequired] IReadOnlyList<TransientTemporalSourceLineageV1> Sources,
    [property: JsonRequired] IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms)
{
    public const string CurrentSchemaVersion = "transient-temporal-background-v1";
}

/// <summary>Owned produced pixels and effective target/no-support exclusion mask.</summary>
public sealed record TransientTemporalBackgroundProduct(
    TransientTemporalBackgroundDescriptorV1 Descriptor,
    ReadOnlyMemory<byte> Pixels,
    Linear16PixelMask EffectiveMask,
    long IncludedSamples,
    long MaskedSamples);

/// <summary>Reason-coded window result. Sources preserve all available target, included, and excluded lineage.</summary>
public sealed record TransientTemporalBackgroundOutcome(
    TransientTemporalBackgroundStatus Status,
    string? ReasonCode,
    string? Field,
    IReadOnlyList<TransientTemporalSourceLineageV1> Sources,
    TransientTemporalBackgroundProduct? Product);

/// <summary>Persistable produced or failed outcome without embedding full-frame pixel bytes.</summary>
public sealed record TransientTemporalBackgroundOutcomeDescriptorV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] TransientTemporalBackgroundStatus Status,
    string? ReasonCode,
    string? Field,
    [property: JsonRequired] IReadOnlyList<TransientTemporalSourceLineageV1> Sources,
    TransientTemporalBackgroundDescriptorV1? Product)
{
    public const string CurrentSchemaVersion = "transient-temporal-background-outcome-v1";
}

/// <summary>Strict bounded JSON persistence for temporal background outcomes and lineage.</summary>
public static class TransientTemporalBackgroundJson
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static TransientTemporalBackgroundOutcomeDescriptorV1 CreateDescriptor(
        TransientTemporalBackgroundOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new TransientTemporalBackgroundOutcomeDescriptorV1(
            TransientTemporalBackgroundOutcomeDescriptorV1.CurrentSchemaVersion,
            outcome.Status,
            outcome.ReasonCode,
            outcome.Field,
            outcome.Sources,
            outcome.Product?.Descriptor);
    }

    public static byte[] Serialize(TransientTemporalBackgroundOutcomeDescriptorV1 descriptor)
    {
        Validate(descriptor);
        var output = JsonSerializer.SerializeToUtf8Bytes(descriptor, JsonOptions);
        if (output.Length > MaximumBytes)
        {
            throw new ArgumentException("Temporal background outcome exceeds its size limit.", nameof(descriptor));
        }
        return output;
    }

    public static TransientTemporalBackgroundOutcomeDescriptorV1 Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaximumBytes)
        {
            throw new ArgumentException("Temporal background outcome JSON is empty or oversized.", nameof(utf8Json));
        }
        TransientTemporalBackgroundOutcomeDescriptorV1 descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<TransientTemporalBackgroundOutcomeDescriptorV1>(utf8Json, JsonOptions)
                ?? throw new ArgumentException("Temporal background outcome JSON is null.", nameof(utf8Json));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Temporal background outcome JSON is invalid.", nameof(utf8Json), exception);
        }
        Validate(descriptor);
        return descriptor;
    }

    public static void Validate(TransientTemporalBackgroundOutcomeDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.Equals(
                descriptor.SchemaVersion,
                TransientTemporalBackgroundOutcomeDescriptorV1.CurrentSchemaVersion,
                StringComparison.Ordinal) ||
            !Enum.IsDefined(descriptor.Status) || descriptor.Sources is null || descriptor.Sources.Count > 5 ||
            descriptor.Sources.Any(static source => source is null) ||
            descriptor.Sources.Count > 0 &&
            descriptor.Sources.Count(static source => source.Disposition == TransientTemporalSourceDisposition.Target) != 1)
        {
            throw new ArgumentException("Temporal background outcome schema or sources are invalid.", nameof(descriptor));
        }
        if (descriptor.Status == TransientTemporalBackgroundStatus.Produced)
        {
            if (descriptor.ReasonCode is not null || descriptor.Field is not null || descriptor.Product is null)
            {
                throw new ArgumentException("A produced outcome requires exactly one product and no failure.", nameof(descriptor));
            }
            ValidateProduct(descriptor.Product);
            if (!JsonSerializer.SerializeToUtf8Bytes(descriptor.Sources, JsonOptions).AsSpan().SequenceEqual(
                    JsonSerializer.SerializeToUtf8Bytes(descriptor.Product.Sources, JsonOptions)))
            {
                throw new ArgumentException("Outcome and product source lineage differ.", nameof(descriptor));
            }
        }
        else if (descriptor.Product is not null || string.IsNullOrWhiteSpace(descriptor.ReasonCode) ||
                 string.IsNullOrWhiteSpace(descriptor.Field) || descriptor.ReasonCode.Length > 256 || descriptor.Field.Length > 256)
        {
            throw new ArgumentException("A failed outcome requires a bounded reason and no product.", nameof(descriptor));
        }
        if (descriptor.Status switch
        {
            TransientTemporalBackgroundStatus.Missing => descriptor.ReasonCode != TransientTemporalBackgroundReasonCodes.MissingSource,
            TransientTemporalBackgroundStatus.TimedOut => descriptor.ReasonCode != TransientTemporalBackgroundReasonCodes.Timeout,
            TransientTemporalBackgroundStatus.Incompatible => descriptor.ReasonCode is
                TransientTemporalBackgroundReasonCodes.MissingSource or TransientTemporalBackgroundReasonCodes.Timeout,
            _ => false
        })
        {
            throw new ArgumentException("Temporal background status and reason differ.", nameof(descriptor));
        }
        foreach (var source in descriptor.Sources)
        {
            if (source is null || !Enum.IsDefined(source.Position) || !Enum.IsDefined(source.Disposition) ||
                source.EvidenceId == Guid.Empty || source.ObservationStartedUtc.Offset != TimeSpan.Zero ||
                source.ObservationEndedUtc.Offset != TimeSpan.Zero || source.ObservationStartedUtc > source.ObservationEndedUtc ||
                !CanonicalSha256(source.DetectorInputIdentitySha256) || source.Sensitivity is null ||
                string.IsNullOrWhiteSpace(source.Sensitivity.ResponseIdentity) || source.Sensitivity.ResponseIdentity.Length > 256 ||
                source.Sensitivity.Numerator == 0 || source.Sensitivity.Denominator == 0 ||
                source.NormalizationNumerator == 0 != (source.NormalizationDenominator == 0) ||
                descriptor.Status == TransientTemporalBackgroundStatus.Produced &&
                    (source.NormalizationNumerator == 0 || source.NormalizationDenominator == 0) ||
                source.MaskIdentitySha256s is null || source.MaskIdentitySha256s.Count is < 1 or > 7 ||
                source.MaskIdentitySha256s.Any(static value => !CanonicalSha256(value)))
            {
                throw new ArgumentException("Temporal background source lineage is invalid.", nameof(descriptor));
            }
        }
        if (!descriptor.Sources.Select(static source => (int)source.Position).SequenceEqual(
                descriptor.Sources.Select(static source => (int)source.Position).Order()) ||
            descriptor.Sources.Select(static source => source.Position).Distinct().Count() != descriptor.Sources.Count ||
            descriptor.Sources.Select(static source => source.EvidenceId).Distinct().Count() != descriptor.Sources.Count ||
            descriptor.Sources.Count > 0 &&
                (descriptor.Sources.Single(static source => source.Disposition == TransientTemporalSourceDisposition.Target).Position !=
                 TransientTemporalPosition.N))
        {
            throw new ArgumentException("Temporal background source ordering is invalid.", nameof(descriptor));
        }
    }

    private static void ValidateProduct(TransientTemporalBackgroundDescriptorV1 product)
    {
        var expectedPositions = product.Kind == TransientTemporalBackgroundKind.CausalProvisional
            ? new[] { TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1, TransientTemporalPosition.N }
            : new[] { TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1, TransientTemporalPosition.N,
                TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2 };
        if (!string.Equals(product.SchemaVersion, TransientTemporalBackgroundDescriptorV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !Enum.IsDefined(product.Kind) || !CanonicalSha256(product.BackgroundIdentitySha256) ||
            !CanonicalSha256(product.TargetDetectorInputIdentitySha256) ||
            !CanonicalSha256(product.BackgroundChecksumSha256) || !CanonicalSha256(product.EffectiveMaskChecksumSha256) ||
            product.Layout is null || product.Layout.Width <= 0 || product.Layout.Height <= 0 ||
            product.Layout.StrideBytes != (long)product.Layout.Width * 2 ||
            product.Layout.ByteLength != (long)product.Layout.StrideBytes * product.Layout.Height ||
            product.Layout.PixelFormat != CameraPixelFormat.Mono16 || product.Layout.ByteOrder != FrameByteOrder.LittleEndian ||
            product.Layout.SampleDepthBits != 16 || product.Layout.ContainerDepthBits != 16 ||
            product.Sources is null || product.Sources.Any(static source => source is null) ||
            !product.Sources.Select(static source => source.Position).SequenceEqual(expectedPositions) ||
            product.Algorithms is null || product.Algorithms.Count == 0 || product.Algorithms.Any(static algorithm =>
                algorithm is null || string.IsNullOrWhiteSpace(algorithm.Name) || algorithm.Name.Length > 256 ||
                string.IsNullOrWhiteSpace(algorithm.Version) || algorithm.Version.Length > 256) ||
            !string.Equals(
                product.BackgroundIdentitySha256,
                TransientTemporalBackgroundFactory.ComputeBackgroundIdentitySha256(product),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Temporal background product descriptor is invalid.", nameof(product));
        }
        var target = product.Sources.Single(static source => source.Position == TransientTemporalPosition.N);
        if (target.Disposition != TransientTemporalSourceDisposition.Target ||
            !string.Equals(target.DetectorInputIdentitySha256, product.TargetDetectorInputIdentitySha256, StringComparison.Ordinal) ||
            product.Sources.Any(static source => source.Position != TransientTemporalPosition.N &&
                source.Disposition is not (TransientTemporalSourceDisposition.Included or
                    TransientTemporalSourceDisposition.ExcludedKnownEvent)) ||
            !product.Sources.Any(static source => source.Disposition == TransientTemporalSourceDisposition.Included))
        {
            throw new ArgumentException("Produced source dispositions are invalid.", nameof(product));
        }
        for (var index = 0; index < product.Sources.Count; index++)
        {
            var source = product.Sources[index];
            if ((decimal)source.CaptureSequence != (decimal)target.CaptureSequence + (int)source.Position ||
                index > 0 &&
                (source.ObservationStartedUtc <= product.Sources[index - 1].ObservationStartedUtc ||
                 source.ObservationStartedUtc < product.Sources[index - 1].ObservationEndedUtc))
            {
                throw new ArgumentException("Produced source sequence or timing is invalid.", nameof(product));
            }
        }
    }

    private static bool Sha256(string value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool CanonicalSha256(string value)
        => Sha256(value) && string.Equals(value, value.ToUpperInvariant(), StringComparison.Ordinal);
}

public static class TransientTemporalBackgroundReasonCodes
{
    public const string MissingSource = "transient-background.missing-source";
    public const string Timeout = "transient-background.timeout";
    public const string InvalidRequest = "transient-background.invalid-request";
    public const string SequenceGap = "transient-background.sequence-gap";
    public const string TemporalGap = "transient-background.temporal-gap";
    public const string IncompatibleLayout = "transient-background.incompatible-layout";
    public const string IncompatibleProfile = "transient-background.incompatible-profile";
    public const string IncompatibleResponse = "transient-background.incompatible-response";
    public const string IncompatibleMask = "transient-background.incompatible-mask";
    public const string NoUsableContext = "transient-background.no-usable-context";
}

/// <summary>Validates resolved windows and delegates pure mask/background arithmetic to Imaging.</summary>
public static class TransientTemporalBackgroundFactory
{
    private const int MaximumIdentityLength = 256;
    private static readonly TransientTemporalPosition[] CausalPositions =
        [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1];
    private static readonly TransientTemporalPosition[] CenteredPositions =
        [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
         TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2];
    private static readonly TransientDetectorMaskKind[] RequiredPersistentMaskKinds =
        [TransientDetectorMaskKind.Sky, TransientDetectorMaskKind.ImageCircle, TransientDetectorMaskKind.Horizon,
         TransientDetectorMaskKind.Obstruction, TransientDetectorMaskKind.BadPixel, TransientDetectorMaskKind.Star];

    /// <summary>Produces a causal or centered background, or a bounded missing/timeout/incompatibility outcome.</summary>
    public static TransientTemporalBackgroundOutcome Create(
        TransientTemporalBackgroundRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(request.Kind) || request.Target is null || request.Context is null ||
            request.KnownEventEvidenceIds is null || request.MaximumAdjacentStartInterval <= TimeSpan.Zero)
        {
            return Failure(TransientTemporalBackgroundReasonCodes.InvalidRequest, "request", []);
        }
        if (!TryValidateSource(request.Target, TransientTemporalPosition.N, out var targetFailure))
        {
            return Failure(targetFailure!, "target", []);
        }

        var expected = request.Kind == TransientTemporalBackgroundKind.CausalProvisional
            ? CausalPositions
            : CenteredPositions;
        var byPosition = new Dictionary<TransientTemporalPosition, TransientTemporalSource>();
        foreach (var source in request.Context)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source is null || !expected.Contains(source.Position) || !byPosition.TryAdd(source.Position, source))
            {
                return Failure(TransientTemporalBackgroundReasonCodes.InvalidRequest, "context.position", TargetLineage(request.Target));
            }
        }

        if (request.KnownEventEvidenceIds.Count > expected.Length ||
            request.KnownEventEvidenceIds.Any(static id => id == Guid.Empty) ||
            request.KnownEventEvidenceIds.Distinct().Count() != request.KnownEventEvidenceIds.Count)
        {
            return Failure(TransientTemporalBackgroundReasonCodes.InvalidRequest, "knownEventEvidenceIds", TargetLineage(request.Target));
        }
        var knownEvents = request.KnownEventEvidenceIds.ToHashSet();
        var availableEvidenceValues = byPosition.Values
            .Where(static source => source?.Input?.Descriptor?.Source is not null)
            .Select(static source => source.Input.Descriptor.Source.EvidenceId)
            .ToArray();
        if (availableEvidenceValues.Distinct().Count() != availableEvidenceValues.Length)
        {
            return Failure(TransientTemporalBackgroundReasonCodes.InvalidRequest, "context.source.evidenceId", TargetLineage(request.Target));
        }
        var availableEvidence = availableEvidenceValues.ToHashSet();
        if (!knownEvents.IsSubsetOf(availableEvidence))
        {
            return Failure(TransientTemporalBackgroundReasonCodes.InvalidRequest, "knownEventEvidenceIds", TargetLineage(request.Target));
        }

        foreach (var position in expected)
        {
            if (!byPosition.ContainsKey(position))
            {
                return Failure(
                    request.DeadlineExpired ? TransientTemporalBackgroundReasonCodes.Timeout : TransientTemporalBackgroundReasonCodes.MissingSource,
                    $"context.{position}",
                    AvailableLineage(request.Target, byPosition.Values, knownEvents));
            }
        }

        var ordered = expected.Select(position => byPosition[position]).ToArray();
        foreach (var source in ordered)
        {
            if (!TryValidateSource(source, source.Position, out var sourceFailure))
            {
                return Failure(sourceFailure!, $"context.{source.Position}", AvailableLineage(request.Target, ordered, knownEvents));
            }
            long expectedSequence;
            try
            {
                expectedSequence = checked(request.Target.CaptureSequence + (int)source.Position);
            }
            catch (OverflowException)
            {
                return Failure(TransientTemporalBackgroundReasonCodes.SequenceGap, $"context.{source.Position}.captureSequence", AvailableLineage(request.Target, ordered, knownEvents));
            }
            if (source.CaptureSequence != expectedSequence)
            {
                return Failure(TransientTemporalBackgroundReasonCodes.SequenceGap, $"context.{source.Position}.captureSequence", AvailableLineage(request.Target, ordered, knownEvents));
            }
        }

        var completeWindow = ordered.Append(request.Target).OrderBy(static source => (int)source.Position).ToArray();
        for (var index = 1; index < completeWindow.Length; index++)
        {
            var previous = completeWindow[index - 1].Input.Descriptor.Source;
            var current = completeWindow[index].Input.Descriptor.Source;
            if (current.ObservationStartedUtc <= previous.ObservationStartedUtc ||
                current.ObservationStartedUtc - previous.ObservationStartedUtc > request.MaximumAdjacentStartInterval ||
                current.ObservationStartedUtc < previous.ObservationEndedUtc)
            {
                return Failure(TransientTemporalBackgroundReasonCodes.TemporalGap, $"context.{completeWindow[index].Position}.observationStartedUtc", AvailableLineage(request.Target, ordered, knownEvents));
            }
        }

        foreach (var source in ordered)
        {
            var compatibility = ValidateCompatibility(request.Target, source);
            if (compatibility is not null)
            {
                return Failure(compatibility.Value.Reason, $"context.{source.Position}.{compatibility.Value.Field}", AvailableLineage(request.Target, ordered, knownEvents));
            }
        }

        var lineages = new List<TransientTemporalSourceLineageV1> { CreateLineage(request.Target, TransientTemporalSourceDisposition.Target, 1, 1) };
        var imagingSources = new List<Linear16TemporalFrame>(ordered.Length);
        foreach (var source in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalization(request.Target.Sensitivity, source.Sensitivity, out var numerator, out var denominator))
            {
                return Failure(TransientTemporalBackgroundReasonCodes.IncompatibleResponse, $"context.{source.Position}.sensitivity", AvailableLineage(request.Target, ordered, knownEvents));
            }
            var excluded = knownEvents.Contains(source.Input.Descriptor.Source.EvidenceId);
            lineages.Add(CreateLineage(
                source,
                excluded ? TransientTemporalSourceDisposition.ExcludedKnownEvent : TransientTemporalSourceDisposition.Included,
                numerator,
                denominator));
            if (excluded)
            {
                continue;
            }

            var masks = EffectiveMasks(source)
                .OrderBy(static mask => mask.Kind)
                .ThenBy(static mask => mask.MaskIdentitySha256, StringComparer.Ordinal)
                .Select(static mask => mask.Mask)
                .ToArray();
            var combined = masks.Length == 1
                ? masks[0]
                : Linear16MaskOperations.Combine(masks, cancellationToken);
            var layout = source.Input.Descriptor.Layout;
            imagingSources.Add(new Linear16TemporalFrame(
                new Linear16Frame(layout.Width, layout.Height, layout.StrideBytes, CameraPixelFormat.Mono16, source.Input.Pixels),
                combined,
                source.Input.Descriptor.Levels.BlackLevel,
                numerator,
                denominator));
        }

        if (imagingSources.Count == 0)
        {
            return Failure(TransientTemporalBackgroundReasonCodes.NoUsableContext, "knownEventEvidenceIds", lineages);
        }

        var background = Linear16TemporalBackground.Compute(
            imagingSources,
            request.Target.Input.Descriptor.Levels.BlackLevel,
            cancellationToken);
        var targetMasks = EffectiveMasks(request.Target)
            .OrderBy(static mask => mask.Kind)
            .ThenBy(static mask => mask.MaskIdentitySha256, StringComparer.Ordinal)
            .Select(static mask => mask.Mask)
            .Append(background.NoSupportMask)
            .ToArray();
        var effectiveMask = Linear16MaskOperations.Combine(targetMasks, cancellationToken);
        var backgroundChecksum = Convert.ToHexString(SHA256.HashData(background.PixelData.Span));
        var maskChecksum = Convert.ToHexString(SHA256.HashData(effectiveMask.Bits.Span));
        var targetLayout = request.Target.Input.Descriptor.Layout;
        var outputLayout = targetLayout with
        {
            StrideBytes = background.StrideBytes,
            ByteLength = background.PixelData.Length
        };
        var descriptor = new TransientTemporalBackgroundDescriptorV1(
            TransientTemporalBackgroundDescriptorV1.CurrentSchemaVersion,
            string.Empty,
            request.Kind,
            request.Target.Input.Descriptor.InputIdentitySha256.ToUpperInvariant(),
            outputLayout,
            backgroundChecksum,
            maskChecksum,
            lineages.OrderBy(static source => (int)source.Position).ToArray(),
            [
                new ProcessingAlgorithmIdentity("linear16-temporal-background", background.AlgorithmVersion),
                new ProcessingAlgorithmIdentity("linear16-exclusion-mask", Linear16MaskOperations.AlgorithmVersion)
            ]);
        descriptor = descriptor with { BackgroundIdentitySha256 = ComputeBackgroundIdentitySha256(descriptor) };
        var product = new TransientTemporalBackgroundProduct(
            descriptor,
            background.PixelData,
            effectiveMask,
            background.IncludedSamples,
            background.MaskedSamples);
        return new TransientTemporalBackgroundOutcome(
            TransientTemporalBackgroundStatus.Produced,
            null,
            null,
            descriptor.Sources,
            product);
    }

    internal static string ComputeMaskIdentitySha256(
        TransientDetectorMaskKind kind,
        ProcessingAlgorithmIdentity algorithm,
        Linear16PixelMask mask)
    {
        if (!Enum.IsDefined(kind) || !Bounded(algorithm.Name) || !Bounded(algorithm.Version))
        {
            throw new ArgumentException("Mask kind and algorithm identity are required.", nameof(algorithm));
        }
        _ = Linear16MaskOperations.IsExcluded(mask, 0, 0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(number, (int)kind);
        hash.AppendData(number);
        BinaryPrimitives.WriteInt32LittleEndian(number, mask.Width);
        hash.AppendData(number);
        BinaryPrimitives.WriteInt32LittleEndian(number, mask.Height);
        hash.AppendData(number);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(algorithm.Name));
        hash.AppendData([0]);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(algorithm.Version));
        hash.AppendData([0]);
        hash.AppendData(mask.Bits.Span);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (string Reason, string Field)? ValidateCompatibility(
        TransientTemporalSource target,
        TransientTemporalSource source)
    {
        var expected = target.Input.Descriptor;
        var actual = source.Input.Descriptor;
        if (actual.Layout != expected.Layout || actual.Representation != expected.Representation ||
            actual.Conversion != expected.Conversion || actual.SourceToDetectorTransform != expected.SourceToDetectorTransform)
        {
            return (TransientTemporalBackgroundReasonCodes.IncompatibleLayout, "layout");
        }
        if (actual.Levels != expected.Levels || !CompatibleProfiles(expected.Compatibility, actual.Compatibility))
        {
            return (TransientTemporalBackgroundReasonCodes.IncompatibleProfile, "compatibility");
        }
        if (!string.Equals(source.Sensitivity.ResponseIdentity, target.Sensitivity.ResponseIdentity, StringComparison.Ordinal))
        {
            return (TransientTemporalBackgroundReasonCodes.IncompatibleResponse, "sensitivity.responseIdentity");
        }
        var targetMasks = target.Masks.ToDictionary(static mask => mask.Kind);
        foreach (var mask in source.Masks)
        {
            if (!targetMasks.TryGetValue(mask.Kind, out var targetMask) ||
                !string.Equals(mask.MaskIdentitySha256, targetMask.MaskIdentitySha256, StringComparison.OrdinalIgnoreCase))
            {
                return (TransientTemporalBackgroundReasonCodes.IncompatibleMask, $"masks.{mask.Kind}");
            }
        }
        return null;
    }

    private static bool CompatibleProfiles(ProcessingCompatibilityIdentity expected, ProcessingCompatibilityIdentity actual)
        => string.Equals(expected.Rig, actual.Rig, StringComparison.Ordinal) &&
           string.Equals(expected.Orientation, actual.Orientation, StringComparison.Ordinal) &&
           string.Equals(expected.Calibration, actual.Calibration, StringComparison.Ordinal) &&
           string.Equals(expected.Mask, actual.Mask, StringComparison.Ordinal) &&
           string.Equals(expected.Sensor, actual.Sensor, StringComparison.Ordinal) &&
           string.Equals(expected.ProcessingProfile, actual.ProcessingProfile, StringComparison.Ordinal);

    private static bool TryValidateSource(
        TransientTemporalSource source,
        TransientTemporalPosition requiredPosition,
        out string? failure)
    {
        failure = null;
        if (source.Position != requiredPosition || source.Input is null || source.Input.Descriptor is null ||
            source.Input.CaptureSequence != source.CaptureSequence ||
            source.Sensitivity is null || source.Masks is null || source.Sensitivity.Numerator == 0 ||
            source.Sensitivity.Denominator == 0 || !Bounded(source.Sensitivity.ResponseIdentity))
        {
            failure = TransientTemporalBackgroundReasonCodes.InvalidRequest;
            return false;
        }
        var descriptor = source.Input.Descriptor;
        var descriptorValidation = TransientContractJson.Validate(descriptor);
        if (!descriptorValidation.IsValid || source.Input.Pixels.Length != descriptor.Layout.ByteLength ||
            descriptor.Layout.PixelFormat != CameraPixelFormat.Mono16 ||
            descriptor.Source.ObservationStartedUtc.Offset != TimeSpan.Zero ||
            descriptor.Source.ObservationEndedUtc.Offset != TimeSpan.Zero ||
            descriptor.Source.ObservationStartedUtc > descriptor.Source.ObservationEndedUtc)
        {
            failure = TransientTemporalBackgroundReasonCodes.InvalidRequest;
            return false;
        }
        if (source.Masks.Count != RequiredPersistentMaskKinds.Length || source.Input.SaturationMask is null ||
            source.Input.SaturationMask.Width != descriptor.Layout.Width ||
            source.Input.SaturationMask.Height != descriptor.Layout.Height)
        {
            failure = TransientTemporalBackgroundReasonCodes.IncompatibleMask;
            return false;
        }
        try
        {
            _ = Linear16MaskOperations.IsExcluded(source.Input.SaturationMask, 0, 0);
        }
        catch (ArgumentException)
        {
            failure = TransientTemporalBackgroundReasonCodes.IncompatibleMask;
            return false;
        }
        var saturationChecksum = Convert.ToHexString(SHA256.HashData(source.Input.SaturationMask.Bits.Span));
        if (!string.Equals(
                saturationChecksum,
                descriptor.SaturationMaskChecksumSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            failure = TransientTemporalBackgroundReasonCodes.IncompatibleMask;
            return false;
        }
        var maskIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maskKinds = new HashSet<TransientDetectorMaskKind>();
        foreach (var mask in source.Masks)
        {
            if (mask is null || !Enum.IsDefined(mask.Kind) || !Sha256(mask.MaskIdentitySha256) ||
                mask.Kind == TransientDetectorMaskKind.Saturation || mask.Algorithm is null ||
                !Bounded(mask.Algorithm.Name) || !Bounded(mask.Algorithm.Version) || mask.Mask is null ||
                mask.Mask.Width != descriptor.Layout.Width || mask.Mask.Height != descriptor.Layout.Height)
            {
                failure = TransientTemporalBackgroundReasonCodes.IncompatibleMask;
                return false;
            }
            if (!maskIdentities.Add(mask.MaskIdentitySha256) || !maskKinds.Add(mask.Kind))
            {
                failure = TransientTemporalBackgroundReasonCodes.IncompatibleMask;
                return false;
            }
            string computed;
            try
            {
                computed = ComputeMaskIdentitySha256(mask.Kind, mask.Algorithm, mask.Mask);
            }
            catch (ArgumentException)
            {
                failure = TransientTemporalBackgroundReasonCodes.IncompatibleMask;
                return false;
            }
            if (!string.Equals(computed, mask.MaskIdentitySha256, StringComparison.OrdinalIgnoreCase))
            {
                failure = TransientTemporalBackgroundReasonCodes.IncompatibleMask;
                return false;
            }
        }
        if (!RequiredPersistentMaskKinds.All(maskKinds.Contains))
        {
            failure = TransientTemporalBackgroundReasonCodes.IncompatibleMask;
            return false;
        }
        return true;
    }

    private static bool TryNormalization(
        TransientSensitivityV1 target,
        TransientSensitivityV1 source,
        out uint numerator,
        out uint denominator)
    {
        numerator = 0;
        denominator = 0;
        if (!string.Equals(target.ResponseIdentity, source.ResponseIdentity, StringComparison.Ordinal) ||
            target.Numerator == 0 || target.Denominator == 0 || source.Numerator == 0 || source.Denominator == 0)
        {
            return false;
        }
        var wideNumerator = (ulong)target.Numerator * source.Denominator;
        var wideDenominator = (ulong)target.Denominator * source.Numerator;
        var divisor = GreatestCommonDivisor(wideNumerator, wideDenominator);
        wideNumerator /= divisor;
        wideDenominator /= divisor;
        if (wideNumerator > uint.MaxValue || wideDenominator > uint.MaxValue)
        {
            return false;
        }
        numerator = (uint)wideNumerator;
        denominator = (uint)wideDenominator;
        return true;
    }

    private static ulong GreatestCommonDivisor(ulong left, ulong right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return left;
    }

    private static TransientTemporalSourceLineageV1 CreateLineage(
        TransientTemporalSource source,
        TransientTemporalSourceDisposition disposition,
        uint normalizationNumerator,
        uint normalizationDenominator)
    {
        var descriptor = source.Input.Descriptor;
        return new TransientTemporalSourceLineageV1(
            source.Position,
            source.CaptureSequence,
            descriptor.Source.EvidenceId,
            descriptor.InputIdentitySha256.ToUpperInvariant(),
            descriptor.Source.ObservationStartedUtc,
            descriptor.Source.ObservationEndedUtc,
            source.Sensitivity,
            normalizationNumerator,
            normalizationDenominator,
            EffectiveMasks(source)
                .Select(static mask => mask.MaskIdentitySha256.ToUpperInvariant())
                .Order(StringComparer.Ordinal)
                .ToArray(),
            disposition);
    }

    private static TransientDetectorMask[] EffectiveMasks(TransientTemporalSource source)
    {
        var output = new TransientDetectorMask[source.Masks.Count + 1];
        for (var index = 0; index < source.Masks.Count; index++)
        {
            output[index] = source.Masks[index];
        }
        output[^1] = TransientDetectorMask.Create(
            TransientDetectorMaskKind.Saturation,
            new ProcessingAlgorithmIdentity("linear16-saturation-mask", "inclusive-threshold-v1"),
            source.Input.SaturationMask);
        return output;
    }

    private static IReadOnlyList<TransientTemporalSourceLineageV1> TargetLineage(TransientTemporalSource target)
        => [CreateLineage(target, TransientTemporalSourceDisposition.Target, 1, 1)];

    private static TransientTemporalSourceLineageV1[] AvailableLineage(
        TransientTemporalSource target,
        IEnumerable<TransientTemporalSource> context,
        HashSet<Guid> knownEvents)
    {
        var output = new List<TransientTemporalSourceLineageV1> { CreateLineage(target, TransientTemporalSourceDisposition.Target, 1, 1) };
        foreach (var source in context.Where(static source => source is not null).OrderBy(static source => (int)source.Position))
        {
            if (!TryValidateSource(source, source.Position, out _))
            {
                continue;
            }
            var normalized = TryNormalization(target.Sensitivity, source.Sensitivity, out var numerator, out var denominator);
            output.Add(CreateLineage(
                source,
                knownEvents.Contains(source.Input.Descriptor.Source.EvidenceId)
                    ? TransientTemporalSourceDisposition.ExcludedKnownEvent
                    : TransientTemporalSourceDisposition.Available,
                normalized ? numerator : 0,
                normalized ? denominator : 0));
        }
        return output.OrderBy(static source => (int)source.Position).ToArray();
    }

    private static TransientTemporalBackgroundOutcome Failure(
        string reason,
        string field,
        IReadOnlyList<TransientTemporalSourceLineageV1> sources)
    {
        var status = reason switch
        {
            TransientTemporalBackgroundReasonCodes.MissingSource => TransientTemporalBackgroundStatus.Missing,
            TransientTemporalBackgroundReasonCodes.Timeout => TransientTemporalBackgroundStatus.TimedOut,
            _ => TransientTemporalBackgroundStatus.Incompatible
        };
        return new TransientTemporalBackgroundOutcome(status, reason, field, sources, null);
    }

    internal static string ComputeBackgroundIdentitySha256(TransientTemporalBackgroundDescriptorV1 descriptor)
    {
        var normalized = descriptor with
        {
            BackgroundIdentitySha256 = string.Empty,
            TargetDetectorInputIdentitySha256 = descriptor.TargetDetectorInputIdentitySha256.ToUpperInvariant(),
            BackgroundChecksumSha256 = descriptor.BackgroundChecksumSha256.ToUpperInvariant(),
            EffectiveMaskChecksumSha256 = descriptor.EffectiveMaskChecksumSha256.ToUpperInvariant(),
            Sources = descriptor.Sources.Select(static source => source with
            {
                DetectorInputIdentitySha256 = source.DetectorInputIdentitySha256.ToUpperInvariant(),
                MaskIdentitySha256s = source.MaskIdentitySha256s
                    .Select(static value => value.ToUpperInvariant())
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            }).ToArray()
        };
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = "hvo-transient-temporal-background-identity-v1",
            normalized.SchemaVersion,
            normalized.Kind,
            normalized.TargetDetectorInputIdentitySha256,
            normalized.Layout,
            normalized.BackgroundChecksumSha256,
            normalized.EffectiveMaskChecksumSha256,
            normalized.Sources,
            normalized.Algorithms
        });
    }

    private static bool Sha256(string value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool Bounded(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdentityLength && value == value.Trim();
}
