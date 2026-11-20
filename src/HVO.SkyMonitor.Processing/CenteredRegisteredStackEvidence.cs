using System.Numerics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
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
    ExposureMeasurementStatus? MeasurementStatus = null, string? MeasurementReason = null);

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

    /// <summary>Reads an exact retained evidence product and validates its canonical plan and product identity.</summary>
    public static CenteredRegisteredStackEvidence Read(ProcessingProduct retained)
    {
        ArgumentNullException.ThrowIfNull(retained);
        if (retained.Kind != ProcessingProductKind.Metadata || retained.Role != FrameArtifactRole.Metadata ||
            retained.SchemaVersion != CurrentSchemaVersion || retained.MediaType != MediaType || retained.Layout is not null ||
            retained.Payload.Length is < 1 or > 128 * 1024 || retained.ContentIdentitySha256 != retained.ChecksumSha256 ||
            retained.ChecksumSha256 != ProcessingIdentity.ComputePayloadSha256(retained.Payload) ||
            retained.Recipe.Descriptor.Name != CenteredRegisteredStackRecipe.RecipeName ||
            retained.Recipe.Descriptor.ImplementationVersion != CenteredRegisteredStackRecipe.RecipeVersion ||
            ProcessingIdentity.CreateRecipeIdentity(retained.Recipe.Descriptor).IdentitySha256 != retained.Recipe.IdentitySha256 ||
            retained.OutputIdentitySha256 != ProcessingIdentity.CreateOutputIdentity(retained.Role, retained.Variant,
                retained.Recipe.IdentitySha256, retained.SourceArtifactIds))
        {
            throw new InvalidDataException("Invalid retained registration evidence envelope.");
        }

        var evidence = JsonSerializer.Deserialize<CenteredRegisteredStackEvidence>(retained.Payload.Span, CenteredStackPlanV1.Json);
        if (evidence is null || evidence.SchemaVersion != CurrentSchemaVersion || evidence.Plan is null ||
            evidence.PlanSha256 != evidence.Plan.ToAuxiliaryInput().IdentitySha256 ||
            retained.Recipe.Descriptor.Options.GetProperty("auxiliaryInputs")[0].GetProperty("identitySha256").GetString() != evidence.PlanSha256 ||
            evidence.Units != LinearUnits || !Enum.IsDefined(evidence.Disposition) ||
            evidence.Sources is not { Count: 5 } || evidence.Outputs is null ||
            !CenteredStackPlanV1.Serialize(evidence).AsSpan().SequenceEqual(retained.Payload.Span))
        {
            throw new InvalidDataException("Invalid retained registration evidence.");
        }
        evidence.ValidateMembership();
        return evidence;
    }

    /// <summary>Checks the retained display and support planes against the same published evidence.</summary>
    public void VerifyPlanes(IReadOnlyList<ProcessingProduct> planes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planes);
        cancellationToken.ThrowIfCancellationRequested();
        if (planes.Count != Outputs.Count)
        {
            throw new InvalidDataException("Incomplete registered plane group.");
        }
        var contributorIds = Plan.Slots.Where((_, index) => Sources[index].Method is not null)
            .Select(slot => slot.Exposure!.Linear.ArtifactId).ToArray();
        for (var index = 0; index < planes.Count; index++)
        {
            var pin = Outputs[index];
            var plane = planes[index];
            var support = pin.Channel == "support";
            var format = support ? CameraPixelFormat.Mono8 : CameraPixelFormat.Mono16;
            var media = support ? SupportMediaType : "application/x-hvo-linear-frame";
            var stride = Width * (support ? 1 : 2);
            if (plane.Kind != ProcessingProductKind.PixelData || plane.Role != FrameArtifactRole.Combined ||
                plane.MediaType != media || plane.Layout is not { } layout || !layout.Validate().IsValid ||
                layout.Width != Width || layout.Height != Height || layout.StrideBytes != stride || layout.PixelFormat != format ||
                layout.ByteLength != plane.Payload.Length || plane.ChecksumSha256 != pin.PayloadSha256 ||
                ProcessingIdentity.ComputePayloadSha256(plane.Payload) != pin.PayloadSha256 ||
                plane.OutputIdentitySha256 != pin.OutputIdentitySha256 ||
                !plane.SourceArtifactIds.SequenceEqual(contributorIds) ||
                ProcessingIdentity.CreateOutputIdentity(plane.Role, plane.Variant, plane.Recipe.IdentitySha256,
                    plane.SourceArtifactIds) != plane.OutputIdentitySha256 ||
                ProcessingIdentity.CreateArtifactId(plane.OutputIdentitySha256) != pin.ArtifactId ||
                plane.TotalIntegration.Ticks != TotalIntegrationTicks)
            {
                throw new InvalidDataException("Registered plane identity or contents differ from publication.");
            }
        }
        if (planes.Count == 0)
        {
            return;
        }
        var members = PublishedSlotBits();
        var published = 0;
        var used = 0;
        var supportPlane = planes[^1].Payload.Span;
        for (var index = 0; index < supportPlane.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var bits = supportPlane[index];
            if ((bits & ~members) != 0)
            {
                throw new InvalidDataException("Coverage includes an excluded registration slot.");
            }
            if (ContributorsAt(bits) >= 2)
            {
                published++;
                used |= bits;
            }
            else
            {
                for (var channel = 0; channel < planes.Count - 1; channel++)
                {
                    if (planes[channel].Payload.Span.Slice(index * 2, 2).ContainsAnyExcept((byte)0))
                    {
                        throw new InvalidDataException("Invalid output pixels have nonzero linear codes.");
                    }
                }
            }
        }
        if (published != ValidPixelCount || used != members)
        {
            throw new InvalidDataException("Registered coverage differs from the declared membership.");
        }
    }

    private void ValidateMembership()
    {
        long total = 0;
        for (var index = 0; index < Sources.Count; index++)
        {
            var source = Sources[index];
            var slot = Plan.Slots[index];
            if (source is null || source.Offset != slot.Offset || source.CaptureId != slot.CaptureId ||
                !Enum.IsDefined(source.Reason) || source.Method is { } method && !Enum.IsDefined(method) ||
                (source.Reason == CenteredRegistrationReason.Contributed) != source.Method.HasValue ||
                (source.Method == CenteredRegistrationMethod.ReferenceIdentity) !=
                    (index == 2 && source.Reason == CenteredRegistrationReason.Contributed) ||
                (source.Method == CenteredRegistrationMethod.VerifiedCentroidSimilarity) != source.Similarity.HasValue ||
                source.Method == CenteredRegistrationMethod.VerifiedPlatePair && source.Plate is null ||
                source.Method.HasValue && slot.Exposure is null)
            {
                throw new InvalidDataException("Invalid registered membership.");
            }
            if (source.Method.HasValue)
            {
                total += slot.Exposure!.IntegrationTicks;
            }
        }
        var members = PublishedSlotBits();
        var registered = Disposition == CenteredStackDisposition.Registered;
        if (total != TotalIntegrationTicks ||
            !registered && (Outputs.Count != 0 || ValidPixelCount != 0) ||
            Disposition == CenteredStackDisposition.NoReference && members != 0 ||
            Disposition == CenteredStackDisposition.SingleExposure && members != 4 ||
            registered && ((members & 4) == 0 || ContributorsAt(members) < 2 ||
                Width < 1 || Height < 1 || (long)Width * Height > LinearRegisteredMean.MaximumPixels ||
                ValidPixelCount <= 0 || ValidPixelCount > Width * Height || !ValidChannels()))
        {
            throw new InvalidDataException("Invalid registered disposition or integration.");
        }
    }

    private byte PublishedSlotBits()
    {
        byte members = 0;
        for (var index = 0; index < Sources.Count; index++)
        {
            if (Sources[index].Reason == CenteredRegistrationReason.Contributed)
            {
                members |= (byte)(1 << index);
            }
        }
        return members;
    }

    private bool ValidChannels()
    {
        var names = Outputs.Select(output => output?.Channel).ToArray();
        return names.SequenceEqual(new[] { "mono", "support" }) ||
            names.SequenceEqual(new[] { "red", "green", "blue", "support" });
    }

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
