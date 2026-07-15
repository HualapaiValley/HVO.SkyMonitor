using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal static class RawCaptureDescriptorFactory
{
    private static readonly JsonElement NoneOptions = JsonSerializer.SerializeToElement(new { mode = "none" });
    private static readonly RecipeIdentityDescriptor RawRecipe = RecipeIdentityDescriptor.Create(
        "capture-raw",
        "1.0.0",
        "raw-ingress-v1",
        JsonSerializer.SerializeToElement(new { normalization = "module-native-immutable" }));

    internal static (Guid CaptureId, Guid ArtifactId) CreateStableIds(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission)
    {
        var frame = submission.Result.Frame ?? throw new ArgumentException("A raw frame is required.", nameof(submission));
        var key = string.Join('\n',
            configuration.AgentId,
            submission.Request.RequestedStartUtc.ToUniversalTime().ToString("O"),
            frame.TimestampUtc.ToUniversalTime().ToString("O"),
            frame.Metadata.SourceId);
        var captureId = CreateStableGuid("capture", key);
        return (captureId, CreateStableGuid("raw-artifact", captureId.ToString("N")));
    }

    internal static DateTimeOffset ResolveExposureStartedUtc(CaptureLoopSubmission submission, CameraFrame frame)
        => ToMilliseconds(submission.Result.AcquisitionTiming?.ExposureStartedUtc ?? frame.TimestampUtc);

    internal static ReconstructionDescriptor Create(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission,
        RawCaptureIdentity identity,
        string payloadSha256,
        DateTimeOffset durableIngressUtc)
    {
        var frame = submission.Result.Frame ?? throw new ArgumentException("A raw frame is required.", nameof(submission));
        var timing = ResolveTiming(submission, frame, durableIngressUtc);
        var requestedSetpoint = submission.Request.RequestedSetpoint;
        var rigElement = JsonSerializer.SerializeToElement(configuration.Rig);
        var sensorElement = JsonSerializer.SerializeToElement(configuration.Rig.Sensor);
        var processingSteps = configuration.ResolveProcessingSteps();
        var processingElement = JsonSerializer.SerializeToElement(processingSteps);
        var calibrationSteps = processingSteps
            .Where(static step => step.Type.Contains("Calibration", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var calibrationElement = calibrationSteps.Length == 0
            ? NoneOptions
            : JsonSerializer.SerializeToElement(calibrationSteps);
        var rigHash = CaptureContractJson.ComputeCanonicalJsonSha256(rigElement);
        var stride = frame.StrideBytes ?? GetPackedStride(frame.Width, frame.PixelFormat);
        var (byteOrder, sampleDepth, containerDepth, cfa) = LayoutFacts(
            frame.PixelFormat,
            configuration.Rig.Sensor.ByteOrder);

        return new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(identity.AgentId, $"rig-{rigHash[..16].ToUpperInvariant()}", identity.CaptureSequence, identity.CaptureId),
            timing,
            new CaptureControlDescriptor(
                requestedSetpoint?.Exposure ?? frame.Metadata.Exposure,
                frame.Metadata.Exposure,
                requestedSetpoint?.Gain ?? frame.Metadata.Gain,
                frame.Metadata.Gain,
                null,
                frame.Metadata.Offset,
                configuration.Rig.ControlPolicy?.Temperature.TargetC,
                double.IsFinite(frame.Metadata.TemperatureC) ? frame.Metadata.TemperatureC : null),
            new CaptureProfileSet(
                Profile("rig", configuration.Rig.ProfileVersion, rigElement),
                Profile(
                    calibrationSteps.Length == 0 ? "calibration" : "configured-calibration",
                    calibrationSteps.Length == 0 ? "none-v1" : "configured-v1",
                    calibrationElement),
                Profile("mask", "none-v1", NoneOptions),
                Profile(configuration.Rig.Sensor.Name, configuration.Rig.Sensor.SensorRecipeVersion, sensorElement),
                Profile("processing", "configured-v1", processingElement)),
            new FrameLayoutDescriptor(
                frame.Width,
                frame.Height,
                stride,
                frame.PixelFormat,
                byteOrder,
                sampleDepth,
                containerDepth,
                FrameSamplePacking.ByteAligned,
                cfa,
                ResolveLevel(frame, "blackLevelAdu"),
                ResolveLevel(frame, "whiteLevelAdu"),
                frame.PixelData.Length),
            new ArtifactDescriptor(
                identity.ArtifactId,
                FrameArtifactRole.Raw,
                frame.Metadata.SourceId ?? configuration.ModuleType,
                "source",
                timing.ReadoutCompletedUtc,
                [],
                RawRecipe,
                MediaTypeFor(frame.PixelFormat),
                payloadSha256))
        {
            CycleEvidence = NormalizeEvidence(submission.CycleEvidence)
        };
    }

    private static CaptureTimingDescriptor ResolveTiming(
        CaptureLoopSubmission submission,
        CameraFrame frame,
        DateTimeOffset durableIngressUtc)
    {
        var reported = submission.Result.AcquisitionTiming;
        var exposureStarted = ResolveExposureStartedUtc(submission, frame);
        var exposureEnded = ToMilliseconds(reported?.ExposureEndedUtc ?? frame.TimestampUtc);
        var readoutCompleted = ToMilliseconds(reported?.ReadoutCompletedUtc ?? frame.TimestampUtc);
        var requested = ToMilliseconds(submission.Request.RequestedStartUtc);
        durableIngressUtc = ToMilliseconds(durableIngressUtc);
        if (exposureStarted > exposureEnded || exposureEnded > readoutCompleted || readoutCompleted > durableIngressUtc)
        {
            throw new InvalidOperationException("Camera module acquisition timing is not ordered before durable ingress.");
        }
        return new CaptureTimingDescriptor(requested, exposureStarted, exposureEnded, readoutCompleted, durableIngressUtc)
        {
            SetpointAppliedUtc = reported?.SetpointAppliedUtc is { } applied ? ToMilliseconds(applied) : null
        };
    }

    private static CaptureCycleEvidence? NormalizeEvidence(CaptureCycleEvidence? evidence)
    {
        if (evidence is null)
        {
            return null;
        }
        var metering = evidence.Metering is null
            ? null
            : evidence.Metering with
            {
                StartedUtc = ToMilliseconds(evidence.Metering.StartedUtc),
                CompletedUtc = ToMilliseconds(evidence.Metering.CompletedUtc)
            };
        return evidence with
        {
            ModuleCallStartedUtc = ToMilliseconds(evidence.ModuleCallStartedUtc),
            Metering = metering,
            Decision = evidence.Decision with
            {
                StartedUtc = ToMilliseconds(evidence.Decision.StartedUtc),
                CompletedUtc = ToMilliseconds(evidence.Decision.CompletedUtc),
                SetpointAppliedUtc = evidence.Decision.SetpointAppliedUtc is { } applied
                    ? ToMilliseconds(applied)
                    : null
            },
            IngressHandoffStartedUtc = ToMilliseconds(evidence.IngressHandoffStartedUtc)
        };
    }

    private static DateTimeOffset ToMilliseconds(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUniversalTime().ToUnixTimeMilliseconds());

    private static ProfileIdentityDescriptor Profile(string name, string version, JsonElement value)
        => new(name, version, CaptureContractJson.ComputeCanonicalJsonSha256(value));

    private static Guid CreateStableGuid(string domain, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(domain, "\0", value)));
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static int GetPackedStride(int width, CameraPixelFormat pixelFormat)
        => checked(width * (pixelFormat switch
        {
            CameraPixelFormat.Mono8 => 1,
            CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 => 2,
            CameraPixelFormat.Rgb24 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
        }));

    private static (FrameByteOrder, int, int, ColorFilterArrayPattern) LayoutFacts(
        CameraPixelFormat pixelFormat,
        SampleByteOrder configuredByteOrder)
        // Layout describes persisted samples; virtual native ADC values are scaled into the declared frame container.
        => pixelFormat switch
        {
            CameraPixelFormat.Mono8 => (FrameByteOrder.NotApplicable, 8, 8, ColorFilterArrayPattern.None),
            CameraPixelFormat.Mono16 => (ToFrameByteOrder(configuredByteOrder), 16, 16, ColorFilterArrayPattern.None),
            CameraPixelFormat.Rgb24 => (FrameByteOrder.NotApplicable, 8, 8, ColorFilterArrayPattern.None),
            CameraPixelFormat.BayerRggb16 => (ToFrameByteOrder(configuredByteOrder), 16, 16, ColorFilterArrayPattern.Rggb),
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
        };

    private static FrameByteOrder ToFrameByteOrder(SampleByteOrder byteOrder) => byteOrder switch
    {
        SampleByteOrder.LittleEndian => FrameByteOrder.LittleEndian,
        SampleByteOrder.BigEndian => FrameByteOrder.BigEndian,
        _ => throw new ArgumentOutOfRangeException(nameof(byteOrder))
    };

    private static double? ResolveLevel(CameraFrame frame, string key)
        => frame.Metadata.Extra is not null && frame.Metadata.Extra.TryGetValue(key, out var value) &&
           double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string MediaTypeFor(CameraPixelFormat pixelFormat) => pixelFormat switch
    {
        CameraPixelFormat.Mono8 => "application/x-skymonitor-mono8",
        CameraPixelFormat.Mono16 => "application/x-skymonitor-mono16",
        CameraPixelFormat.Rgb24 => "application/x-skymonitor-rgb24",
        CameraPixelFormat.BayerRggb16 => "application/x-skymonitor-bayer-rggb16",
        _ => "application/octet-stream"
    };
}
