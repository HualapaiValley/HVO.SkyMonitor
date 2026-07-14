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

    internal static void ThrowIfFailure(ProcessingOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status is ProcessingOutcomeStatus.RetryableFailure or ProcessingOutcomeStatus.TerminalFailure)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Canonical processing returned {outcome.Status}: {outcome.ReasonCode} ({outcome.Field})."));
        }
    }

    public static ProcessingArtifact CreateArtifact(
        CameraModuleConfig config,
        FrameArtifact artifact,
        string variant)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(artifact);
        var frame = artifact.Frame;
        var layout = CreateLayout(config, frame);
        return new ProcessingArtifact(
            artifact.ArtifactId,
            artifact.Role,
            variant,
            NormalizeLegacyRecipeIdentity(artifact.RecipeVersion),
            "application/x-hvo-frame",
            layout,
            frame.PixelData,
            frame.TimestampUtc,
            frame.Metadata.Exposure,
            CreateCompatibility(config, frame));
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
            source.Metadata with { SourceId = sourceId });
    }

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
        var whiteLevel = adcDepth is > 0 and <= 16
            ? Math.Pow(2, adcDepth.Value) - 1
            : is16Bit ? ushort.MaxValue : byte.MaxValue;
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
        var processing = JsonSerializer.SerializeToElement(config.ResolveProcessingSteps());
        var temperatureSetpoint = config.Rig.ControlPolicy?.Temperature.Mode == TemperatureControlMode.Target
            ? config.Rig.ControlPolicy.Temperature.TargetC
            : null;
        var setpoint = string.Create(CultureInfo.InvariantCulture,
            $"exposure={frame.Metadata.Exposure.TotalMilliseconds:R};gain={frame.Metadata.Gain:R};offset={frame.Metadata.Offset:R};temperatureSetpoint={temperatureSetpoint:R}");
        return new ProcessingCompatibilityIdentity(
            RigProjectionContextFactory.CreateProfileHashSha256(config.Rig),
            RigProjectionContextFactory.CreateProfileHashSha256(config.Rig),
            HashText($"calibration:{config.Rig.Optics.CalibrationVersion}"),
            HashText("mask:none"),
            CaptureContractJson.ComputeCanonicalJsonSha256(
                JsonSerializer.SerializeToElement(config.Rig.Sensor)),
            setpoint,
            CaptureContractJson.ComputeCanonicalJsonSha256(processing));
    }

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
