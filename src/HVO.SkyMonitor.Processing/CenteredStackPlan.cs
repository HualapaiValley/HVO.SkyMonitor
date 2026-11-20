using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public enum CenteredSlotState
{
    Ready,
    Missing,
    NotReadyAtClosure,
    Ineligible
}

/// <summary>Registration's pin to an existing immutable artifact, independent of storage location.</summary>
public sealed record CenteredStackArtifactPin(Guid ArtifactId, string ProducerSha256, string PayloadSha256,
    string? DescriptorSha256, string? ContentSha256, string EnvelopeSha256)
{
    public static CenteredStackArtifactPin From(ProcessingArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new(artifact.ArtifactId, artifact.RecipeIdentitySha256,
            ProcessingIdentity.ComputePayloadSha256(artifact.Payload), artifact.DescriptorIdentitySha256, artifact.ContentIdentitySha256,
            ProcessingIdentity.ComputePayloadSha256(CenteredStackPlanV1.Serialize(new
            {
                artifact.Role, artifact.Variant, artifact.MediaType, artifact.Layout, artifact.CaptureId, artifact.CaptureSequence,
                artifact.Integration, artifact.ObservationStartedUtc, artifact.ObservationEndedUtc, artifact.Conditions,
                artifact.Compatibility, artifact.SourceArtifactIds, artifact.ProductKind, artifact.SchemaVersion
            })));
    }

    internal bool Matches(ProcessingArtifact artifact) => this == From(artifact);

    internal bool IsValid() => ArtifactId != Guid.Empty && IsSha256(ProducerSha256) && IsSha256(PayloadSha256) && IsSha256(EnvelopeSha256) &&
        (DescriptorSha256 is null || IsSha256(DescriptorSha256)) && (ContentSha256 is null || IsSha256(ContentSha256));

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>The exact retained #523 request parents and solution for this capture.</summary>
public sealed record CenteredStackPlatePins(CenteredStackArtifactPin Associations, CenteredStackArtifactPin Scene,
    CenteredStackArtifactPin Context, CenteredStackArtifactPin Solution);

/// <summary>Frozen geometry for the retained #1920 measurement input binding.</summary>
public sealed record CenteredStackMeasurementGeometry(double? NativePixelPitchMicrometers, ProjectionContext? ApertureProjection);

/// <summary>Qualified pixels plus frozen measurement geometry reconstructed from the admitted profile.</summary>
public sealed record CenteredStackQualifiedExposure(LinearExposureInputV1 Linear, CenteredStackMeasurementGeometry MeasurementGeometry,
    string? MeasurementCalibrationReason = null)
{
    internal string MeasurementBindingIdentity() => ProcessingIdentity.ComputePayloadSha256(
        CenteredStackPlanV1.Serialize(new { geometry = MeasurementGeometry, calibrationReason = MeasurementCalibrationReason }));
}

/// <summary>
/// Registration's references to qualified pixels and their science dependencies. The row-major validity mask uses
/// 0 for invalid and 1 for valid and covers source-domain and calibration validity before RGB reconstruction.
/// </summary>
public sealed record CenteredStackExposurePin(ExposureKey Key, long CaptureSequence, DateTimeOffset ExposureStartedUtc,
    DateTimeOffset ExposureEndedUtc, long IntegrationTicks, CenteredStackArtifactPin Raw,
    CenteredStackArtifactPin Linear, CenteredStackArtifactPin ValidityMask, CenteredStackArtifactPin? Measurement,
    CenteredStackPlatePins? Plate, string GeometrySha256, string LinearProvenanceSha256, string MeasurementBindingSha256)
{
    public static CenteredStackExposurePin Create(CenteredStackQualifiedExposure retained, ProcessingArtifact raw,
        ProcessingArtifact mask, CenteredStackArtifactPin? measurement, CenteredStackPlatePins? plate = null)
    {
        ArgumentNullException.ThrowIfNull(retained);
        var qualified = retained.Linear;
        ArgumentNullException.ThrowIfNull(raw);
        var descriptor = qualified.Exposure.Raw;
        var start = descriptor.Timing.ExposureStartedUtc;
        var integration = descriptor.Controls.EffectiveExposure;
        return new(qualified.Exposure.Key, descriptor.Capture.CaptureSequence, start,
            ProcessingArtifact.ResolveObservationEndedUtc(start, descriptor.Timing.ExposureEndedUtc, integration),
            integration.Ticks, CenteredStackArtifactPin.From(raw), CenteredStackArtifactPin.From(qualified.Linear),
            CenteredStackArtifactPin.From(mask), measurement, plate,
            ProcessingIdentity.ComputePayloadSha256(CenteredStackPlanV1.Serialize(qualified.Exposure.Geometry)),
            ProcessingIdentity.ComputePayloadSha256(CenteredStackPlanV1.Serialize(qualified.Provenance)),
            retained.MeasurementBindingIdentity());
    }
}

public sealed record CenteredStackSlot(int Offset, DateTimeOffset ExpectedStartUtc, CenteredSlotState State,
    Guid? CaptureId, CenteredStackExposurePin? Exposure, string? ClosureReason = null);

/// <summary>
/// Exactly five frozen slots, oldest to newest. Offset zero explicitly binds the reference capture and linear
/// artifact. The caller owns cadence, terminal slot decisions, retention holds and closure.
/// </summary>
public sealed record CenteredStackPlanV1(CameraKey Camera, Guid RunId, string GenerationIdentity,
    Guid? ReferenceCaptureId, Guid? ReferenceLinearArtifactId, DateTimeOffset CloseDeadlineUtc,
    DateTimeOffset EvidenceCutoffUtc, string ClosureIdentity, IReadOnlyList<CenteredStackSlot> Slots)
{
    public const string SchemaVersion = "centered-stack-plan-v1";
    public const string AuxiliaryName = "centered-stack-plan";
    public const int MaximumBytes = 48 * 1024;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public ProcessingAuxiliaryInput ToAuxiliaryInput()
    {
        Validate();
        var bytes = Serialize(this);
        if (bytes.Length > MaximumBytes)
        {
            throw new ArgumentException("Centered plan exceeds its byte bound.");
        }
        return new(AuxiliaryName, ProcessingAuxiliaryInputKind.CanonicalJson, SchemaVersion: SchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(bytes), Payload: bytes);
    }

    public static CenteredStackPlanV1 Read(ProcessingAuxiliaryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Name != AuxiliaryName || input.Kind != ProcessingAuxiliaryInputKind.CanonicalJson ||
            input.SchemaVersion != SchemaVersion || input.Payload.Length is < 1 or > MaximumBytes ||
            input.IdentitySha256 != ProcessingIdentity.ComputePayloadSha256(input.Payload))
        {
            throw new ArgumentException("Invalid centered plan envelope.", nameof(input));
        }
        var plan = JsonSerializer.Deserialize<CenteredStackPlanV1>(input.Payload.Span, Json)
            ?? throw new ArgumentException("Missing centered plan.", nameof(input));
        plan.Validate();
        if (!Serialize(plan).AsSpan().SequenceEqual(input.Payload.Span))
        {
            throw new ArgumentException("Centered plan bytes must be canonical and complete.", nameof(input));
        }
        return plan;
    }

    internal static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(
        CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value, Json)), Json);

    internal static IEnumerable<CenteredStackArtifactPin> Pins(CenteredStackExposurePin exposure) =>
        ArtifactBounds(exposure).Select(item => item.Pin);

    internal static IEnumerable<(CenteredStackArtifactPin Pin, int MaximumBytes)> ArtifactBounds(CenteredStackExposurePin exposure)
    {
        yield return (exposure.Raw, 16 * 1024 * 1024); // Delivered values plus row padding.
        yield return (exposure.Linear, LinearRegisteredMean.MaximumPixels * sizeof(ushort));
        yield return (exposure.ValidityMask, LinearRegisteredMean.MaximumPixels);
        if (exposure.Measurement is { } measurement)
        {
            yield return (measurement, StructuredProcessingProductDescriptorV1.MaximumPayloadBytes);
        }
        if (exposure.Plate is { } plate)
        {
            yield return (plate.Associations, MeasuredStellarAssociationJson.MaximumPayloadBytes);
            yield return (plate.Scene, ProjectedSceneJson.MaximumPayloadBytes);
            yield return (plate.Context, MeasuredAstrometryJson.MaximumContextBytes);
            yield return (plate.Solution, MeasuredAstrometryJson.MaximumSolutionBytes);
        }
    }

    private void Validate()
    {
        if (Camera is null || !Bounded(Camera.AgentId) || !Bounded(Camera.CameraId) || RunId == Guid.Empty || !Bounded(GenerationIdentity) || !Bounded(ClosureIdentity) ||
            ReferenceCaptureId == Guid.Empty || ReferenceLinearArtifactId == Guid.Empty ||
            CloseDeadlineUtc.Offset != TimeSpan.Zero || EvidenceCutoffUtc.Offset != TimeSpan.Zero ||
            EvidenceCutoffUtc > CloseDeadlineUtc || Slots is not { Count: 5 })
        {
            throw new ArgumentException("Invalid centered plan identity.");
        }
        var captures = new HashSet<Guid>();
        var artifacts = new HashSet<Guid>();
        DateTimeOffset? previousStart = null;
        long previousSequence = -1;
        DateTimeOffset? previousEnd = null;
        for (var index = 0; index < Slots.Count; index++)
        {
            var slot = Slots[index];
            if (slot is null || slot.Offset != index - 2 || !Enum.IsDefined(slot.State) ||
                slot.ExpectedStartUtc.Offset != TimeSpan.Zero || slot.ExpectedStartUtc <= previousStart ||
                slot.ExpectedStartUtc > CloseDeadlineUtc || slot.CaptureId == Guid.Empty ||
                slot.ClosureReason is { } reason && !Bounded(reason) ||
                slot.CaptureId is { } capture && !captures.Add(capture) ||
                (slot.State == CenteredSlotState.Ready) != (slot.Exposure is not null) ||
                slot.State == CenteredSlotState.Missing && slot.CaptureId is not null)
            {
                throw new ArgumentException("Invalid centered slot ordering or state.");
            }
            previousStart = slot.ExpectedStartUtc;
            if (slot.Exposure is not { } exposure)
            {
                continue;
            }
            if (exposure.Key is null || exposure.Key.Camera != Camera || exposure.Key.RunId != RunId ||
                exposure.Key.CaptureId != slot.CaptureId || exposure.CaptureSequence <= previousSequence ||
                !IsSha256(exposure.GeometrySha256) || !IsSha256(exposure.LinearProvenanceSha256) ||
                !IsSha256(exposure.MeasurementBindingSha256) ||
                exposure.Plate is { } plate && (plate.Associations is null || plate.Scene is null ||
                    plate.Context is null || plate.Solution is null) ||
                exposure.ExposureStartedUtc.Offset != TimeSpan.Zero || exposure.ExposureEndedUtc.Offset != TimeSpan.Zero ||
                exposure.ExposureStartedUtc < previousEnd || exposure.ExposureEndedUtc <= exposure.ExposureStartedUtc ||
                exposure.ExposureEndedUtc > EvidenceCutoffUtc || exposure.IntegrationTicks is < 1 or > TimeSpan.TicksPerDay ||
                Pins(exposure).Any(pin => pin is null || !pin.IsValid() || !artifacts.Add(pin.ArtifactId)))
            {
                throw new ArgumentException("Invalid centered exposure identity or artifact pins.");
            }
            previousSequence = exposure.CaptureSequence;
            previousEnd = exposure.ExposureEndedUtc;
        }
        if (Slots[2].CaptureId != ReferenceCaptureId ||
            Slots[2].Exposure?.Linear.ArtifactId != ReferenceLinearArtifactId)
        {
            throw new ArgumentException("Offset zero must match the explicit reference.");
        }

        static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    }
}
