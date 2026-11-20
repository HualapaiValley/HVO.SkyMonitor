using System.Numerics;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public enum CenteredStackDisposition
{
    NoReference,
    SingleExposure,
    Registered
}

public enum CenteredRegistrationMethod
{
    ReferenceIdentity,
    VerifiedPlatePair,
    VerifiedCentroidSimilarity
}

public enum CenteredRegistrationReason
{
    Contributed,
    MissingSlot,
    NotReadyAtClosure,
    IneligibleAtClosure,
    ReferenceUnavailable,
    RetainedInputMissing,
    RetainedInputMismatch,
    IncompatibleExposure,
    MeasurementUnavailable,
    MeasurementEmpty,
    MeasurementUnsuitable,
    InvalidMeasurement,
    InvalidRetainedPlate,
    IncompatiblePlatePair,
    RegistrationRejected,
    NoValidSamples,
    NoSharedSupport
}

public sealed record CenteredStackPlateEvidence(string SolutionSha256, string AssessmentSha256, string AssessmentReason,
    string UncertaintyReason);

public sealed record CenteredStackSourceEvidence(int Offset, Guid? CaptureId, CenteredRegistrationReason Reason,
    CenteredRegistrationMethod? Method = null, ImageSimilarity? Similarity = null, int SupportedPixels = 0,
    DirectRegistrationCheck? PairCheck = null, CenteredStackPlateEvidence? Plate = null,
    ExposureMeasurementStatus? MeasurementStatus = null, string? MeasurementReason = null)
{
    public PixelPoint? MapToReference(PixelPoint source) => Method switch
    {
        CenteredRegistrationMethod.ReferenceIdentity => source,
        CenteredRegistrationMethod.VerifiedCentroidSimilarity => Similarity?.SourceToReference(source),
        _ => null
    };
}

public sealed record CenteredStackOutputPin(string Channel, Guid ArtifactId, string OutputIdentitySha256,
    string PayloadSha256);

/// <summary>
/// Frozen display-linear evidence. Counts and effective integration are derived per pixel from the support bits and
/// the actual exposure ticks in the corresponding slots. Overall integration sums the contributing slots.
/// </summary>
public sealed record CenteredRegisteredStackEvidence(string SchemaVersion, string PlanSha256, CenteredStackPlanV1 Plan,
    CenteredStackDisposition Disposition, DateTimeOffset? ReferenceMidpointUtc, DateTimeOffset? FirstExposureUtc,
    DateTimeOffset? LastExposureUtc, long TotalIntegrationTicks, int Width, int Height, int ValidPixelCount,
    string Units, IReadOnlyList<CenteredStackSourceEvidence> Sources, IReadOnlyList<CenteredStackOutputPin> Outputs)
{
    public const string CurrentSchemaVersion = "centered-registered-stack-evidence-v1";
    public const string MediaType = "application/vnd.hvo.centered-registered-stack-evidence+json";
    public const string LinearUnits = "calibrated-stored-code-unit-weight-mean-v1";
    public const string SupportMediaType = "application/x-hvo-centered-slot-support";

    public static int ContributorsAt(byte support)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(support, (byte)31);
        return BitOperations.PopCount(support);
    }

    public long IntegrationTicksAt(byte support)
    {
        _ = ContributorsAt(support);
        long ticks = 0;
        for (var index = 0; index < Plan.Slots.Count; index++)
        {
            if ((support & (1 << index)) == 0)
            {
                continue;
            }
            if (Sources[index].Reason != CenteredRegistrationReason.Contributed ||
                Plan.Slots[index].Exposure is not { } exposure)
            {
                throw new ArgumentException("A support bit identifies an excluded slot.", nameof(support));
            }
            ticks += exposure.IntegrationTicks;
        }
        return ticks;
    }
}
