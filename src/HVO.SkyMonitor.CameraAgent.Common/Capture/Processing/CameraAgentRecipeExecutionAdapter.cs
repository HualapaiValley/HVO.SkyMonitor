using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed class CameraAgentRecipeExecutionAdapter(IProcessingRecipeExecutor executor)
{
    private readonly IProcessingRecipeExecutor _executor = executor;

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(request, cancellationToken);

    public async ValueTask<ProcessingOutcome> ExecuteAsync(
        CaptureProcessingContext context,
        ProcessingExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RecordExecutionRequest(request);
        var outcome = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        context.RecordExecutionOutcome(outcome);
        return outcome;
    }

    public static ProcessingArtifact CreateArtifact(
        CameraModuleConfig config,
        FrameArtifact artifact,
        string variant,
        CaptureAcquisitionTiming? acquisitionTiming = null,
        ReconstructionDescriptor? reconstructionDescriptor = null,
        ProcessingProduct? product = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(artifact);
        var frame = artifact.Frame;
        var layout = product?.Layout ?? (reconstructionDescriptor?.Artifact.ArtifactId == artifact.ArtifactId
            ? reconstructionDescriptor.Layout
            : frame.Layout ?? CreateLayout(config, frame));
        var integration = product?.TotalIntegration ?? frame.Metadata.Exposure;
        var observationStartedUtc = reconstructionDescriptor?.Timing.ExposureStartedUtc ??
            acquisitionTiming?.ExposureStartedUtc ?? frame.TimestampUtc;
        var observationEndedUtc = reconstructionDescriptor?.Timing.ExposureEndedUtc ??
            acquisitionTiming?.ExposureEndedUtc ??
            observationStartedUtc.Add(integration);
        observationEndedUtc = ProcessingArtifact.ResolveObservationEndedUtc(
            observationStartedUtc, observationEndedUtc, integration);
        return new ProcessingArtifact(
            artifact.ArtifactId,
            artifact.Role,
            variant,
            product?.Recipe.IdentitySha256 ?? NormalizeLegacyRecipeIdentity(artifact.RecipeVersion),
            "application/x-hvo-frame",
            layout,
            frame.PixelData,
            frame.TimestampUtc,
            integration,
            product?.Compatibility ?? (reconstructionDescriptor is null
                ? CreateCompatibility(config, frame)
                : CreateCompatibility(reconstructionDescriptor)),
            CaptureSequence: reconstructionDescriptor?.Capture.CaptureSequence,
            SourceArtifactIds: artifact.SourceArtifactIds,
            ObservationStartedUtc: observationStartedUtc.ToUniversalTime(),
            ObservationEndedUtc: observationEndedUtc.ToUniversalTime(),
            Conditions: reconstructionDescriptor is null
                ? new ProcessingCaptureConditions(
                    frame.Metadata.Gain,
                    frame.Metadata.Offset,
                    double.IsFinite(frame.Metadata.TemperatureC) ? frame.Metadata.TemperatureC : null)
                : new ProcessingCaptureConditions(
                    reconstructionDescriptor.Controls.EffectiveGain,
                    reconstructionDescriptor.Controls.EffectiveOffset,
                    reconstructionDescriptor.Controls.EffectiveTemperatureC))
        {
            ProductKind = product?.Kind ?? ProcessingProductKind.PixelData,
            SchemaVersion = product?.SchemaVersion,
            ContentIdentitySha256 = product?.ContentIdentitySha256,
            CaptureId = reconstructionDescriptor?.Capture.CaptureId,
            DescriptorIdentitySha256 = reconstructionDescriptor is null
                ? null
                : CaptureContractJson.ComputeDescriptorSha256(reconstructionDescriptor)
        };
    }

    internal static ProcessingArtifact CreateArtifact(CaptureProcessingContext context, ProcessingProduct product)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(product);
        var descriptor = context.ReconstructionDescriptor;
        var startedUtc = descriptor?.Timing.ExposureStartedUtc ?? context.Frame?.TimestampUtc ?? DateTimeOffset.UnixEpoch;
        var endedUtc = descriptor?.Timing.ExposureEndedUtc ?? startedUtc.Add(product.TotalIntegration);
        return new ProcessingArtifact(
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
            product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload,
            context.Frame?.TimestampUtc ?? startedUtc, product.TotalIntegration, product.Compatibility,
            descriptor?.Capture.CaptureSequence, product.SourceArtifactIds, startedUtc,
            ProcessingArtifact.ResolveObservationEndedUtc(startedUtc, endedUtc, product.TotalIntegration),
            descriptor is null
                ? null
                : new ProcessingCaptureConditions(
                    descriptor.Controls.EffectiveGain,
                    descriptor.Controls.EffectiveOffset,
                    descriptor.Controls.EffectiveTemperatureC))
        {
            ProductKind = product.Kind,
            SchemaVersion = product.SchemaVersion,
            ContentIdentitySha256 = product.ContentIdentitySha256 ?? product.OutputIdentitySha256,
            CaptureId = descriptor?.Capture.CaptureId,
            DescriptorIdentitySha256 = null
        };
    }

    public static CameraFrame CreateFrame(ProcessingProduct product, CameraFrame source, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(source);
        if (product.Layout is not { } layout)
        {
            throw new ArgumentException("A packed processing product is required.", nameof(product));
        }
        return new CameraFrame(
            source.TimestampUtc,
            layout.Width,
            layout.Height,
            layout.PixelFormat,
            product.Payload,
            source.Metadata with { SourceId = sourceId },
            layout.StrideBytes)
        {
            Layout = layout
        };
    }

    internal static ProcessingInputSelector CreateSelector(
        FrameArtifact artifact,
        ProcessingProduct? product,
        string fallbackVariant)
        => artifact.Role switch
        {
            FrameArtifactRole.Raw => ProcessingInputSelector.Raw(fallbackVariant),
            FrameArtifactRole.Calibrated => ProcessingInputSelector.Calibrated(product?.Variant ?? fallbackVariant),
            FrameArtifactRole.Combined => ProcessingInputSelector.Combined(product?.Variant ?? fallbackVariant),
            _ when product is not null => ProcessingInputSelector.RecipeResult(
                artifact.Role, product.Variant, product.Recipe.IdentitySha256),
            _ => throw new InvalidOperationException(
                $"Artifact role '{artifact.Role}' requires an exact canonical recipe identity.")
        };

    private static FrameLayoutDescriptor CreateLayout(CameraModuleConfig config, CameraFrame frame)
    {
        var stride = frame.StrideBytes ?? checked(frame.Width * ImageLayout.BytesPerPixel(frame.PixelFormat));
        var is16Bit = frame.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16;
        var byteOrder = !is16Bit
            ? FrameByteOrder.NotApplicable
            : config.Rig.Sensor.ByteOrder == SampleByteOrder.LittleEndian
                ? FrameByteOrder.LittleEndian
                : FrameByteOrder.BigEndian;
        var blackLevel = is16Bit ? TryGetLevel(frame.Metadata.Extra, "blackLevelAdu") : null;
        var adcDepth = is16Bit
            ? TryGetInteger(frame.Metadata.Extra, "sensorAdcBitDepth") ??
                TryGetInteger(frame.Metadata.Extra, "adcBitDepth")
            : null;
        var containerMaximum = is16Bit ? ushort.MaxValue : byte.MaxValue;
        var declaredWhiteLevel = is16Bit ? TryGetLevel(frame.Metadata.Extra, "whiteLevelAdu") : null;
        var whiteLevel = declaredWhiteLevel is { } declared &&
            double.IsFinite(declared) && declared > (blackLevel ?? -1) && declared <= containerMaximum
                ? declared
                : adcDepth is > 0 and <= 16
                    ? Math.Pow(2, adcDepth.Value) - 1
                    : containerMaximum;
        return new FrameLayoutDescriptor(
            frame.Width,
            frame.Height,
            stride,
            frame.PixelFormat,
            byteOrder,
            is16Bit ? 16 : 8,
            is16Bit ? 16 : 8,
            FrameSamplePacking.ByteAligned,
            frame.PixelFormat == CameraPixelFormat.BayerRggb16
                ? ColorFilterArrayPattern.Rggb
                : ColorFilterArrayPattern.None,
            blackLevel,
            whiteLevel,
            checked((long)stride * frame.Height));
    }

    private static ProcessingCompatibilityIdentity CreateCompatibility(
        CameraModuleConfig config,
        CameraFrame frame)
    {
        var processing = JsonSerializer.SerializeToElement(config.Pipeline.Steps);
        var temperatureSetpoint = config.Rig.ControlPolicy?.Temperature.Mode == TemperatureControlMode.Target
            ? config.Rig.ControlPolicy.Temperature.TargetC
            : null;
        var setpoint = string.Create(CultureInfo.InvariantCulture,
            $"exposure={frame.Metadata.Exposure.TotalMilliseconds:R};gain={frame.Metadata.Gain:R};offset={frame.Metadata.Offset:R};temperatureSetpoint={temperatureSetpoint:R}");
        // Manifest v2 identifies orientation within the aggregate rig profile rather than as a separate profile.
        var rigProfile = RigProjectionContextFactory.CreateProfileHashSha256(config.Rig);
        return new ProcessingCompatibilityIdentity(
            rigProfile,
            rigProfile,
            HashText($"calibration:{config.Rig.Optics.CalibrationVersion}"),
            HashText("mask:none"),
            CaptureContractJson.ComputeCanonicalJsonSha256(
                JsonSerializer.SerializeToElement(config.Rig.Sensor)),
            setpoint,
            CaptureContractJson.ComputeCanonicalJsonSha256(processing),
            config.DeploymentLocation?.ToProvenance().IdentitySha256);
    }

    internal static ProcessingCompatibilityIdentity CreateCompatibility(ReconstructionDescriptor descriptor)
        => new(
            descriptor.Profiles.Rig.Sha256,
            descriptor.Profiles.Rig.Sha256,
            descriptor.Profiles.Calibration.Sha256,
            descriptor.Profiles.Mask.Sha256,
            descriptor.Profiles.Sensor.Sha256,
            string.Create(CultureInfo.InvariantCulture,
                $"exposure={descriptor.Controls.EffectiveExposure.TotalMilliseconds:R};gain={descriptor.Controls.EffectiveGain:R};offset={descriptor.Controls.EffectiveOffset:R};temperatureSetpoint={descriptor.Controls.TemperatureSetpointC:R}"),
            descriptor.Profiles.Processing.Sha256,
            descriptor.Location?.IdentitySha256);

    private static double? TryGetLevel(IReadOnlyDictionary<string, string>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static int? TryGetInteger(IReadOnlyDictionary<string, string>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NormalizeLegacyRecipeIdentity(string? recipeVersion)
    {
        if (recipeVersion is { Length: 64 } && recipeVersion.All(Uri.IsHexDigit))
        {
            return recipeVersion.ToUpperInvariant();
        }
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(recipeVersion ?? "legacy-raw-source-v1")));
    }
}
