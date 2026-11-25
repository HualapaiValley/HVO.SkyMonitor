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

    private string? RecipeSha256 { get; init; }
    private string? BaseVariant { get; init; }

    /// <summary>Reads an exact retained evidence product and validates its canonical plan and product identity.</summary>
    public static CenteredRegisteredStackEvidence Read(ProcessingProduct retained)
    {
        ArgumentNullException.ThrowIfNull(retained);
        try
        {
            return ReadCore(retained);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Invalid retained registration evidence.", exception);
        }
    }

    private static CenteredRegisteredStackEvidence ReadCore(ProcessingProduct retained)
    {
        if (retained.Kind != ProcessingProductKind.Metadata || retained.Role != FrameArtifactRole.Metadata ||
            retained.SchemaVersion != CurrentSchemaVersion || retained.MediaType != MediaType || retained.Layout is not null ||
            retained.Payload.Length is < 1 or > 128 * 1024 || retained.ContentIdentitySha256 != retained.ChecksumSha256 ||
            retained.ChecksumSha256 != ProcessingIdentity.ComputePayloadSha256(retained.Payload) ||
            retained.Recipe is not { Descriptor: { } descriptor } || descriptor.Name != CenteredRegisteredStackRecipe.RecipeName ||
            descriptor.ImplementationVersion is not
                (CenteredRegisteredStackRecipe.LegacyRecipeVersion or CenteredRegisteredStackRecipe.RecipeVersion) ||
            !HasRecipe(retained.Recipe, retained.Recipe.IdentitySha256) ||
            retained.OutputIdentitySha256 != ProcessingIdentity.CreateOutputIdentity(retained.Role, retained.Variant,
                retained.Recipe.IdentitySha256, retained.SourceArtifactIds))
        {
            throw new InvalidDataException("Invalid retained registration evidence envelope.");
        }

        var evidence = JsonSerializer.Deserialize<CenteredRegisteredStackEvidence>(retained.Payload.Span, CenteredStackPlanV1.Json);
        if (evidence is null || evidence.SchemaVersion != CurrentSchemaVersion || evidence.Plan is null ||
            evidence.PlanSha256 != evidence.Plan.ToAuxiliaryInput().IdentitySha256 ||
            ReadBoundPlanIdentity(descriptor.Options) != evidence.PlanSha256 ||
            evidence.Units != LinearUnits || !Enum.IsDefined(evidence.Disposition) ||
            evidence.Sources is not { Count: 5 } || evidence.Outputs is null ||
            !CenteredStackPlanV1.Serialize(evidence).AsSpan().SequenceEqual(retained.Payload.Span))
        {
            throw new InvalidDataException("Invalid retained registration evidence.");
        }
        var expectedLineage = evidence.Plan.Slots.Select(slot => slot.Exposure).OfType<CenteredStackExposurePin>()
            .SelectMany(CenteredStackPlanV1.Pins).Select(pin => pin.ArtifactId);
        if (!retained.SourceArtifactIds.SequenceEqual(expectedLineage) || retained.TotalIntegration.Ticks != evidence.TotalIntegrationTicks)
        {
            throw new InvalidDataException("Retained registration lineage or integration differs from its plan.");
        }
        var suffix = "-" + CenteredRegisteredStackRecipe.EvidenceVariant;
        if (!retained.Variant.EndsWith(suffix, StringComparison.Ordinal) || retained.Variant.Length == suffix.Length)
        {
            throw new InvalidDataException("Invalid retained registration variant.");
        }
        evidence = evidence with { RecipeSha256 = retained.Recipe.IdentitySha256, BaseVariant = retained.Variant[..^suffix.Length] };
        evidence.ValidateMembership();
        evidence.ValidateOutputPins();
        return evidence;
    }

    private static string? ReadBoundPlanIdentity(JsonElement options) =>
        options.TryGetProperty("auxiliaryInputs", out var auxiliary) &&
        auxiliary.ValueKind == JsonValueKind.Array && auxiliary.GetArrayLength() > 0 &&
        auxiliary[0] is { ValueKind: JsonValueKind.Object } binding &&
        binding.TryGetProperty("identitySha256", out var identity) && identity.ValueKind == JsonValueKind.String
            ? identity.GetString() : null;

    /// <summary>Checks the retained display and support planes against the same published evidence.</summary>
    public void VerifyPlanes(IReadOnlyList<ProcessingProduct> planes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planes);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMembership();
        ValidateOutputPins();
        if (planes.Count != Outputs.Count)
        {
            throw new InvalidDataException("Incomplete registered plane group.");
        }
        var contributorIds = ContributorIds();
        for (var index = 0; index < planes.Count; index++)
        {
            var pin = Outputs[index];
            var plane = planes[index];
            var support = pin.Channel == "support";
            var media = support ? SupportMediaType : "application/x-hvo-linear-frame";
            var layout = PlaneLayout(Width, Height, support);
            if (plane.Kind != ProcessingProductKind.PixelData || plane.Role != FrameArtifactRole.Combined ||
                plane.MediaType != media || plane.Layout != layout || plane.Variant != PlaneVariant(pin.Channel) ||
                !HasRecipe(plane.Recipe, RecipeSha256) ||
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
        Span<int> supportCounts = stackalloc int[LinearRegisteredMean.SlotCount];
        supportCounts.Clear();
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
            for (var remaining = (uint)bits; remaining != 0; remaining &= remaining - 1)
            {
                supportCounts[BitOperations.TrailingZeroCount(remaining)]++;
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
        for (var slot = 0; slot < supportCounts.Length; slot++)
        {
            if (supportCounts[slot] != Sources[slot].SupportedPixels)
            {
                throw new InvalidDataException("Registered support differs from the declared per-source pixel count.");
            }
        }
    }

    internal static FrameLayoutDescriptor PlaneLayout(int width, int height, bool support)
    {
        var bytesPerPixel = support ? 1 : 2;
        return new(width, height, width * bytesPerPixel,
            support ? CameraPixelFormat.Mono8 : CameraPixelFormat.Mono16,
            support ? FrameByteOrder.NotApplicable : FrameByteOrder.LittleEndian,
            bytesPerPixel * 8, bytesPerPixel * 8, FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None, 0, support ? 31 : ushort.MaxValue, (long)width * height * bytesPerPixel);
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
                source.Method is null && source.Similarity.HasValue ||
                source.Method.HasValue && (slot.Exposure is null || source.SupportedPixels <= 0) ||
                source.Method is null && source.SupportedPixels != 0 ||
                source.SupportedPixels > (long)Width * Height)
            {
                throw new InvalidDataException("Invalid registered membership.");
            }
            if (source.Method.HasValue)
            {
                RequireVerifiedMap(source);
                total += slot.Exposure!.IntegrationTicks;
            }
        }
        var members = PublishedSlotBits();
        var registered = Disposition == CenteredStackDisposition.Registered;
        var contributors = Plan.Slots.Where((_, index) => (members & (1 << index)) != 0).Select(slot => slot.Exposure!).ToArray();
        var center = Plan.Slots[2].Exposure;
        var midpoint = center?.ExposureStartedUtc.AddTicks((center.ExposureEndedUtc - center.ExposureStartedUtc).Ticks / 2);
        if (total != TotalIntegrationTicks || ReferenceMidpointUtc != midpoint ||
            Width != (center?.LinearLayout.Width ?? 0) || Height != (center?.LinearLayout.Height ?? 0) ||
            FirstExposureUtc != contributors.FirstOrDefault()?.ExposureStartedUtc ||
            LastExposureUtc != contributors.LastOrDefault()?.ExposureEndedUtc ||
            !registered && (Outputs.Count != 0 || ValidPixelCount != 0) ||
            Disposition == CenteredStackDisposition.NoReference && members != 0 ||
            Disposition == CenteredStackDisposition.SingleExposure &&
                (members is not (0 or 4) || members == 0 && Sources[2].Reason != CenteredRegistrationReason.NoValidSamples) ||
            registered && ((members & 4) == 0 || ContributorsAt(members) < 2 ||
                Width < 1 || Height < 1 || (long)Width * Height > LinearRegisteredMean.MaximumPixels ||
                ValidPixelCount <= 0 || ValidPixelCount > Width * Height || !ValidChannels(center?.LinearLayout.PixelFormat)))
        {
            throw new InvalidDataException("Invalid registered disposition or integration.");
        }
    }

    internal static void RequireVerifiedMap(CenteredStackSourceEvidence source)
    {
        var valid = source.Method switch
        {
            CenteredRegistrationMethod.ReferenceIdentity => source.Similarity is null && source.PairCheck is null,
            CenteredRegistrationMethod.VerifiedCentroidSimilarity => source.Similarity is { } map &&
                DirectCentroidRegistration.IsVerifiedSimilarity(map, source.PairCheck),
            CenteredRegistrationMethod.VerifiedPlatePair => source.Similarity is null && source.Plate is not null &&
                DirectCentroidRegistration.IsVerifiedPlateCheck(source.PairCheck),
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException("Registered contributor does not have a verified map.");
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

    private bool ValidChannels(CameraPixelFormat? sourceFormat)
    {
        var names = Outputs.Select(output => output?.Channel).ToArray();
        return sourceFormat switch
        {
            CameraPixelFormat.Mono16 => names.SequenceEqual(new[] { "mono", "support" }),
            CameraPixelFormat.BayerRggb16 => names.SequenceEqual(new[] { "red", "green", "blue", "support" }),
            _ => false
        };
    }

    internal static bool HasRecipe(ProcessingRecipeIdentity recipe, string? expected) =>
        recipe.IdentitySha256 == expected && recipe.Descriptor is { Options.ValueKind: JsonValueKind.Object } descriptor &&
        descriptor.OptionsSha256 == CaptureContractJson.ComputeCanonicalJsonSha256(descriptor.Options) &&
        ProcessingIdentity.CreateRecipeIdentity(descriptor).IdentitySha256 == expected;

    private Guid[] ContributorIds() => Plan.Slots.Where((_, index) => Sources[index].Method is not null)
        .Select(slot => slot.Exposure!.Linear.ArtifactId).ToArray();

    private string PlaneVariant(string channel) => BaseVariant + "-" + channel;

    private void ValidateOutputPins()
    {
        if (RecipeSha256 is null || BaseVariant is null)
        {
            throw new InvalidDataException("Read the retained evidence envelope before validating its output planes.");
        }
        var contributors = ContributorIds();
        foreach (var pin in Outputs)
        {
            var identity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Combined,
                PlaneVariant(pin.Channel), RecipeSha256, contributors);
            if (pin.OutputIdentitySha256 != identity || pin.ArtifactId != ProcessingIdentity.CreateArtifactId(identity) ||
                pin.PayloadSha256 is not { Length: 64 } || !pin.PayloadSha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("A registered plane pin does not match its declared channel and publication.");
            }
        }
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
