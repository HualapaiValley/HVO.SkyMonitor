using System.Security.Cryptography;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public enum TransientDetectorRepresentation
{
    Mono16,
    Rggb16CellAverage
}

public enum TransientDetectorInputOwnership
{
    Borrowed,
    Owned
}

/// <summary>Linear sample levels in ADU, inclusive of black, white, and saturation thresholds.</summary>
public sealed record TransientLinearLevelsV1(
    [property: JsonRequired] ushort BlackLevel,
    [property: JsonRequired] ushort WhiteLevel,
    [property: JsonRequired] ushort SaturationLevel);

/// <summary>Affine source-pixel to detector-pixel mapping using output = source * scale + offset.</summary>
public sealed record TransientDetectorTransformV1(
    [property: JsonRequired] string Version,
    [property: JsonRequired] double ScaleX,
    [property: JsonRequired] double ScaleY,
    [property: JsonRequired] double OffsetX,
    [property: JsonRequired] double OffsetY);

/// <summary>Canonical metadata and stable identity for a validated linear detector input.</summary>
public sealed record TransientDetectorInputDescriptorV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string InputIdentitySha256,
    [property: JsonRequired] TransientSourceEvidenceReferenceV1 Source,
    [property: JsonRequired] TransientDetectorRepresentation Representation,
    [property: JsonRequired] FrameLayoutDescriptor Layout,
    [property: JsonRequired] TransientLinearLevelsV1 Levels,
    [property: JsonRequired] ProcessingCompatibilityIdentity Compatibility,
    [property: JsonRequired] ProcessingAlgorithmIdentity Conversion,
    [property: JsonRequired] TransientDetectorTransformV1 SourceToDetectorTransform,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SaturationMaskChecksumSha256 = null)
{
    public const string CurrentSchemaVersion = "transient-detector-input-v1";
}

/// <summary>
/// Validated linear detector pixels. Borrowed pixels remain valid only while the caller keeps the source alive and
/// unchanged; owned pixels are retained by this record. Concurrent reads are safe under that lifetime rule.
/// </summary>
public sealed record TransientDetectorInput(
    TransientDetectorInputDescriptorV1 Descriptor,
    ReadOnlyMemory<byte> Pixels,
    TransientDetectorInputOwnership Ownership,
    long? CaptureSequence,
    Linear16PixelMask SaturationMask);

/// <summary>Detector-input creation result with explicit source scan and bounded-copy accounting.</summary>
public sealed record TransientDetectorInputCreationResult(
    TransientDetectorInput? Input,
    TransientContractValidationResult Validation,
    long BytesScanned,
    long BytesCopied);

/// <summary>Stable bounded failures returned by detector input construction.</summary>
public static class TransientDetectorInputReasonCodes
{
    public const string InvalidSource = "transient-input.invalid-source";
    public const string InvalidRole = "transient-input.invalid-role";
    public const string UnsupportedFormat = "transient-input.unsupported-format";
    public const string InvalidLayout = "transient-input.invalid-layout";
    public const string InvalidByteOrder = "transient-input.invalid-byte-order";
    public const string InvalidSampleDepth = "transient-input.invalid-sample-depth";
    public const string InvalidPacking = "transient-input.invalid-packing";
    public const string InvalidCfa = "transient-input.invalid-cfa";
    public const string InvalidLevels = "transient-input.invalid-levels";
    public const string InvalidProfile = "transient-input.invalid-profile";
    public const string ChecksumMismatch = "transient-input.checksum-mismatch";
}

/// <summary>
/// Builds host-neutral validated detector inputs from immutable Processing artifacts. The factory is stateless and
/// safe for concurrent callers when borrowed source memory is not mutated.
/// </summary>
public static class TransientDetectorInputFactory
{
    private const int MaximumIdentityLength = 256;

    /// <summary>
    /// Verifies the complete source reference and SHA-256, then creates a Mono16 detector representation. Mono16
    /// borrows the exact source memory; RGGB16 uses one bounded half-resolution allocation. Cancellation is thrown.
    /// </summary>
    /// <param name="artifact">Artifact with explicit canonical observation bounds and immutable payload.</param>
    /// <param name="source">Whole-artifact evidence whose interval and identity must exactly match the artifact.</param>
    /// <param name="levels">Inclusive linear black, white, and saturation levels.</param>
    /// <param name="cancellationToken">Cancellation checked before validation and conversion.</param>
    /// <returns>A detector input on success, otherwise a stable validation failure and scan/copy accounting.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static TransientDetectorInputCreationResult Create(
        ProcessingArtifact artifact,
        TransientSourceEvidenceReferenceV1 source,
        TransientLinearLevelsV1 levels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(levels);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceValidation = TransientContractJson.ValidateSourceEvidence(source);
        if (!sourceValidation.IsValid)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidSource, sourceValidation.FieldPath ?? "source");
        }
        if (artifact.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated))
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidRole, "artifact.role");
        }
        if (artifact.ArtifactId == Guid.Empty || string.IsNullOrWhiteSpace(artifact.Variant) ||
            !Bounded(artifact.MediaType) || !Sha256(artifact.RecipeIdentitySha256) || artifact.CreatedUtc == default ||
            artifact.CreatedUtc.Offset != TimeSpan.Zero || artifact.Integration < TimeSpan.Zero)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidSource, "artifact");
        }
        var sourceArtifactIds = artifact.SourceArtifactIds ?? [];
        if (sourceArtifactIds.Any(id => id == Guid.Empty || id == artifact.ArtifactId) ||
            sourceArtifactIds.Distinct().Count() != sourceArtifactIds.Count ||
            artifact.Role == FrameArtifactRole.Raw && sourceArtifactIds.Count != 0 ||
            artifact.Role == FrameArtifactRole.Calibrated && sourceArtifactIds.Count == 0)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidSource, "artifact.sourceArtifactIds");
        }

        var reference = source.Locator.Artifact;
        if (reference.ArtifactId != artifact.ArtifactId || reference.Role != artifact.Role ||
            !string.Equals(reference.Variant, artifact.Variant, StringComparison.Ordinal) ||
            !string.Equals(reference.RecipeIdentitySha256, artifact.RecipeIdentitySha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidSource, "source.locator.artifact");
        }
        if (artifact.ObservationStartedUtc is not { } observationStartedUtc ||
            artifact.ObservationEndedUtc is not { } observationEndedUtc ||
            observationStartedUtc == default || observationEndedUtc == default ||
            observationStartedUtc.Offset != TimeSpan.Zero || observationEndedUtc.Offset != TimeSpan.Zero ||
            observationStartedUtc > observationEndedUtc)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidSource, "artifact.observationStartedUtc");
        }
        if (source.ObservationStartedUtc != observationStartedUtc ||
            source.ObservationEndedUtc != observationEndedUtc)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidSource, "source.observationStartedUtc");
        }
        if (!ValidCompatibility(artifact.Compatibility))
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidProfile, "artifact.compatibility");
        }

        var layout = artifact.Layout;
        if (layout is null || layout.Width < 1 || layout.Height < 1 || layout.StrideBytes < 1)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidLayout, "artifact.layout");
        }
        if (layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            return Failure(TransientDetectorInputReasonCodes.UnsupportedFormat, "artifact.layout.pixelFormat");
        }
        if (layout.ByteOrder != FrameByteOrder.LittleEndian)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidByteOrder, "artifact.layout.byteOrder");
        }
        var storedCodesMatchLevels = layout.SampleDepthBits == layout.ContainerDepthBits ||
            layout.StoredCodeTransform == FrameStoredCodeTransform.RightAlignedV1 &&
            layout.LevelCodeSpace is FrameLevelCodeSpace.NativeSample or FrameLevelCodeSpace.StoredContainer ||
            layout.StoredCodeTransform is FrameStoredCodeTransform.LeftShiftedV1 or FrameStoredCodeTransform.FullRangeScaledV1 &&
            layout.LevelCodeSpace == FrameLevelCodeSpace.StoredContainer;
        if (layout.ContainerDepthBits != 16 || !storedCodesMatchLevels)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidSampleDepth, "artifact.layout.sampleDepthBits");
        }
        if (layout.Packing != FrameSamplePacking.ByteAligned)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidPacking, "artifact.layout.packing");
        }
        var expectedCfa = layout.PixelFormat == CameraPixelFormat.BayerRggb16
            ? ColorFilterArrayPattern.Rggb
            : ColorFilterArrayPattern.None;
        if (layout.CfaPattern != expectedCfa)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidCfa, "artifact.layout.cfaPattern");
        }
        if (layout.PixelFormat == CameraPixelFormat.BayerRggb16 &&
            ((layout.Width & 1) != 0 || (layout.Height & 1) != 0))
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidLayout, "artifact.layout");
        }

        long requiredLength;
        try
        {
            var minimumStride = checked(layout.Width * 2);
            requiredLength = checked((long)layout.StrideBytes * layout.Height);
            if (layout.StrideBytes < minimumStride || layout.ByteLength != requiredLength ||
                artifact.Payload.Length != requiredLength)
            {
                return Failure(TransientDetectorInputReasonCodes.InvalidLayout, "artifact.layout.byteLength");
            }
        }
        catch (OverflowException)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidLayout, "artifact.layout");
        }

        if (layout.BlackLevel is not { } declaredBlack || layout.WhiteLevel is not { } declaredWhite ||
            declaredBlack != levels.BlackLevel || declaredWhite != levels.WhiteLevel ||
            levels.WhiteLevel <= levels.BlackLevel || levels.SaturationLevel <= levels.BlackLevel ||
            levels.SaturationLevel > levels.WhiteLevel)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidLevels, "levels");
        }

        Linear16PixelMask? monoSaturationMask = null;
        string checksum;
        if (layout.PixelFormat == CameraPixelFormat.Mono16)
        {
            (checksum, monoSaturationMask) = ComputeSha256AndSaturationMask(
                artifact.Payload.Span,
                layout.Width,
                layout.Height,
                layout.StrideBytes,
                levels.SaturationLevel,
                cancellationToken);
        }
        else
        {
            checksum = ComputeSha256(artifact.Payload.Span, cancellationToken);
        }
        var checksumScanned = artifact.Payload.Length;
        if (!string.Equals(checksum, reference.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                TransientDetectorInputReasonCodes.ChecksumMismatch,
                "source.locator.artifact.checksumSha256",
                checksumScanned);
        }

        Linear16DetectorInputResult converted;
        try
        {
            var imageLayout = new ImageLayout(layout.Width, layout.Height, layout.PixelFormat, layout.StrideBytes);
            converted = layout.PixelFormat == CameraPixelFormat.BayerRggb16
                ? Linear16DetectorInputConverter.ConvertRggb16WithSaturationMask(
                    imageLayout,
                    layout.ByteOrder,
                    artifact.Payload,
                    levels.SaturationLevel,
                    cancellationToken)
                : Linear16DetectorInputConverter.Convert(
                    imageLayout,
                    layout.ByteOrder,
                    artifact.Payload,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return Failure(TransientDetectorInputReasonCodes.InvalidLayout, "artifact.layout", checksumScanned);
        }

        var outputLayout = new FrameLayoutDescriptor(
            converted.Layout.Width,
            converted.Layout.Height,
            converted.Layout.StrideBytes,
            CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian,
            16,
            16,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            levels.BlackLevel,
            levels.WhiteLevel,
            converted.PixelData.Length);
        var normalizedSource = TransientContractJson.NormalizeSourceEvidence(source);
        var saturationMask = converted.SourceSaturationMask ?? monoSaturationMask!;
        var transform = converted.SourceToOutputTransform;
        var descriptor = new TransientDetectorInputDescriptorV1(
            TransientDetectorInputDescriptorV1.CurrentSchemaVersion,
            string.Empty,
            normalizedSource,
            layout.PixelFormat == CameraPixelFormat.Mono16
                ? TransientDetectorRepresentation.Mono16
                : TransientDetectorRepresentation.Rggb16CellAverage,
            outputLayout,
            levels,
            artifact.Compatibility,
            new ProcessingAlgorithmIdentity("linear16-detector-input", converted.AlgorithmVersion),
            new TransientDetectorTransformV1(
                transform.Version,
                transform.ScaleX,
                transform.ScaleY,
                transform.OffsetX,
                transform.OffsetY),
            Convert.ToHexString(SHA256.HashData(saturationMask.Bits.Span)));
        descriptor = descriptor with
        {
            InputIdentitySha256 = TransientContractJson.ComputeDetectorInputIdentitySha256(descriptor)
        };
        return new TransientDetectorInputCreationResult(
            new TransientDetectorInput(
                descriptor,
                converted.PixelData,
                converted.Ownership == Linear16DetectorInputOwnership.Borrowed
                    ? TransientDetectorInputOwnership.Borrowed
                    : TransientDetectorInputOwnership.Owned,
                artifact.CaptureSequence,
                saturationMask),
            TransientContractValidationResult.Success,
            checked(checksumScanned + converted.BytesScanned),
            checked(converted.BytesCopied + saturationMask.Bits.Length));
    }

    private static bool ValidCompatibility(ProcessingCompatibilityIdentity? compatibility)
        => compatibility is not null &&
           Bounded(compatibility.Rig) && Bounded(compatibility.Orientation) &&
           Bounded(compatibility.Calibration) && Bounded(compatibility.Mask) &&
           Bounded(compatibility.Sensor) && Bounded(compatibility.SetpointRegime) &&
           Bounded(compatibility.ProcessingProfile) &&
           (compatibility.LocationIdentitySha256 is null || Sha256(compatibility.LocationIdentitySha256));

    private static bool Bounded(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdentityLength && value == value.Trim();

    private static bool Sha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string ComputeSha256(ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunkSize = 64 * 1024;
        var offset = 0;
        while (offset < payload.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, payload.Length - offset);
            hash.AppendData(payload.Slice(offset, length));
            offset += length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (string Checksum, Linear16PixelMask SaturationMask) ComputeSha256AndSaturationMask(
        ReadOnlySpan<byte> payload,
        int width,
        int height,
        int strideBytes,
        ushort saturationLevel,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var mask = new byte[Linear16MaskOperations.RequiredByteLength(width, height)];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = y * strideBytes;
            hash.AppendData(payload.Slice(row, strideBytes));
            for (var x = 0; x < width; x++)
            {
                var offset = row + x * 2;
                var sample = (ushort)(payload[offset] | payload[offset + 1] << 8);
                if (sample >= saturationLevel)
                {
                    var pixel = y * width + x;
                    mask[pixel >> 3] |= (byte)(1 << (pixel & 7));
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (
            Convert.ToHexString(hash.GetHashAndReset()),
            new Linear16PixelMask(width, height, mask));
    }

    private static TransientDetectorInputCreationResult Failure(
        string reasonCode,
        string fieldPath,
        long bytesScanned = 0)
        => new(null, TransientContractValidationResult.Failure(reasonCode, fieldPath), bytesScanned, 0);
}
