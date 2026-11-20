using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Reads exact retained producer contracts for one frozen window, independently of the stack artifact bytes.</summary>
public interface ICenteredStackInputReader
{
    ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(ExposureKey key, Guid linearArtifactId, CancellationToken cancellationToken);
}

/// <summary>Explicit opt-in registration for a closed centered window.</summary>
public sealed class CenteredRegisteredStackRecipe(ICenteredStackInputReader reader) : IProcessingRecipe
{
    public const string RecipeName = "centered-registered-stack";
    public const string RecipeVersion = "centered-direct-registration-v1";
    public const string InvalidPlan = "registration.invalid-centered-plan";
    public const string ReferenceUnavailable = "registration.reference-unavailable";
    public const string ResourceLimit = "registration.resource-limit";
    public const string EvidenceVariant = "centered-stack-evidence";
    private const long MaximumInputBytes = 128L * 1024 * 1024;
    private readonly ICenteredStackInputReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public ProcessingRecipeDefinition Definition { get; } = new(RecipeName, "1.0.0", RecipeVersion, ProcessingOperationKind.Window);

    public static ProcessingExecutionRequest CreateRequest(CenteredStackPlanV1 plan,
        string linearVariant, string outputVariant, IReadOnlyList<ProcessingArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(RecipeName, default, ProcessingInputSelector.Calibrated(linearVariant), artifacts, outputVariant,
            AuxiliaryInputs: [plan.ToAuxiliaryInput()]);
    }

    public JsonElement NormalizeOptions(JsonElement options)
    {
        if (options.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
            (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
        {
            throw new ArgumentException("Centered registration v1 uses a fixed numerical policy.", nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(new
        {
            order = "ordered-five-slot-center-v1",
            coordinates = "delivered-pixel-edges-v1",
            directCentroidFit = DirectCentroidRegistration.FitVersion,
            plateCheck = DirectCentroidRegistration.PlateCheckVersion,
            combiner = LinearRegisteredMean.AlgorithmVersion,
            color = LinearBayerReconstruction.AlgorithmVersion,
            units = CenteredRegisteredStackEvidence.LinearUnits
        });
    }

    public async ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.InputArtifactId is not null || request.Input.Kind != ProcessingInputKind.Calibrated ||
            request.Input.Role != FrameArtifactRole.Calibrated || string.IsNullOrWhiteSpace(request.Input.Variant) ||
            request.Annotation is not null || request.AuxiliaryInputs is not [{ } auxiliary])
        {
            return ProcessingOutcome.TerminalFailure(InvalidPlan);
        }
        if (request.Inputs.Count > 40 || request.Inputs.Sum(item => (long)item.Payload.Length) > MaximumInputBytes)
        {
            return ProcessingOutcome.TerminalFailure(ResourceLimit);
        }

        CenteredStackPlanV1 plan;
        try
        {
            plan = CenteredStackPlanV1.Read(auxiliary);
        }
        catch (ArgumentException)
        {
            return ProcessingOutcome.TerminalFailure(InvalidPlan, nameof(request.AuxiliaryInputs));
        }
        catch (JsonException)
        {
            return ProcessingOutcome.TerminalFailure(InvalidPlan, nameof(request.AuxiliaryInputs));
        }

        var exposures = plan.Slots.Select(slot => slot.Exposure).OfType<CenteredStackExposurePin>().ToArray();
        var known = exposures.SelectMany(CenteredStackPlanV1.Pins).Select(pin => pin.ArtifactId).ToHashSet();
        var artifacts = new Dictionary<Guid, ProcessingArtifact>();
        foreach (var artifact in request.Inputs)
        {
            if (!known.Contains(artifact.ArtifactId) || !artifacts.TryAdd(artifact.ArtifactId, artifact))
            {
                return ProcessingOutcome.TerminalFailure(InvalidPlan, nameof(request.Inputs));
            }
        }
        var selected = ProcessingRecipeSupport.ResolveMany(request, out _);
        if (!selected.Select(artifact => artifact.ArtifactId).SequenceEqual(
            exposures.Select(exposure => exposure.Linear.ArtifactId).Where(artifacts.ContainsKey)))
        {
            return ProcessingOutcome.TerminalFailure(InvalidPlan, nameof(request.Inputs));
        }

        var decisions = plan.Slots.Select(InitialDecision).ToArray();
        var resolved = new ResolvedExposure?[LinearRegisteredMean.SlotCount];
        for (var slot = 0; slot < resolved.Length; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Slots[slot].Exposure is not { } exposure)
            {
                continue;
            }
            var loaded = await ResolveExposureAsync(exposure, artifacts, cancellationToken).ConfigureAwait(false);
            resolved[slot] = loaded.Exposure;
            decisions[slot] = decisions[slot] with
            {
                Reason = loaded.Reason, Plate = PlateEvidence(loaded.Exposure?.Plate),
                MeasurementStatus = loaded.Exposure?.MeasurementStatus, MeasurementReason = loaded.Exposure?.MeasurementReason
            };
        }
        var reference = resolved[2];
        if (reference is null)
        {
            for (var slot = 0; slot < decisions.Length; slot++)
            {
                if (decisions[slot].Reason == CenteredRegistrationReason.Contributed)
                {
                    decisions[slot] = decisions[slot] with { Reason = CenteredRegistrationReason.ReferenceUnavailable };
                }
            }
            return Publish(plan, auxiliary, identity, request.OutputVariant, artifacts, decisions, null, null,
                CenteredStackDisposition.NoReference, cancellationToken);
        }

        var layout = reference.Linear.Layout!;
        var isColor = layout.PixelFormat == CameraPixelFormat.BayerRggb16;
        var accumulator = new LinearRegisteredMean(layout.Width, layout.Height, isColor ? 3 : 1);
        var choices = new MapChoice?[resolved.Length];
        for (var slot = 0; slot < resolved.Length; slot++)
        {
            if (resolved[slot] is not { } source)
            {
                continue;
            }
            choices[slot] = slot == 2
                ? new(CenteredRegistrationReason.Contributed, CenteredRegistrationMethod.ReferenceIdentity)
                : ChooseMap(source, reference, layout, cancellationToken);
        }
        // The summation order is fixed by slot order even though the reference is resolved first.
        for (var slot = 0; slot < resolved.Length; slot++)
        {
            if (resolved[slot] is not { } source || choices[slot] is not { } choice)
            {
                continue;
            }
            var supported = 0;
            var reason = choice.Reason;
            if (reason == CenteredRegistrationReason.Contributed)
            {
                try
                {
                    var image = CenteredStackLinearInput.Decode(source.Raw, source.Linear, source.Mask, cancellationToken);
                    supported = accumulator.Add(slot, image, choice.Map, cancellationToken);
                    reason = supported == 0 ? CenteredRegistrationReason.NoValidSamples : reason;
                }
                catch (ArgumentException)
                {
                    // Decode validates the mask before mutating the accumulator.
                    reason = CenteredRegistrationReason.RetainedInputMismatch;
                }
            }
            decisions[slot] = decisions[slot] with
            {
                Reason = reason,
                Method = supported == 0 ? null : choice.Method,
                Similarity = supported > 0 && choice.Map is ImageSimilarity similarity ? similarity : null,
                SupportedPixels = supported,
                PairCheck = choice.Check
            };
        }

        var samples = accumulator.Finish(cancellationToken);
        var displayedSlots = DisplayedSlots(samples, cancellationToken);
        var hasReference = decisions[2].Reason == CenteredRegistrationReason.Contributed;
        var hasStack = hasReference && (displayedSlots & (1 << 2)) != 0;
        for (var slot = 0; slot < resolved.Length; slot++)
        {
            if (slot != 2 && decisions[slot].Reason == CenteredRegistrationReason.Contributed &&
                (!hasStack || (displayedSlots & (1 << slot)) == 0))
            {
                decisions[slot] = decisions[slot] with
                {
                    Reason = CenteredRegistrationReason.NoSharedSupport, Method = null, Similarity = null, SupportedPixels = 0
                };
            }
        }
        if (hasStack)
        {
            // Remove isolated support for a source that supplies no sample to any display-valid output pixel.
            var support = samples.Support.ToArray();
            for (var index = 0; index < support.Length; index++)
            {
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                support[index] &= displayedSlots;
            }
            samples = samples with { Support = support };
        }
        return Publish(plan, auxiliary, identity, request.OutputVariant, artifacts, decisions, reference,
            hasStack ? samples : null,
            !hasReference ? CenteredStackDisposition.NoReference : hasStack ? CenteredStackDisposition.Registered : CenteredStackDisposition.SingleExposure,
            cancellationToken);
    }

    private async ValueTask<(ResolvedExposure? Exposure, CenteredRegistrationReason Reason)> ResolveExposureAsync(
        CenteredStackExposurePin pin, Dictionary<Guid, ProcessingArtifact> artifacts, CancellationToken cancellationToken)
    {
        if (!TryGet(pin.Raw, out var raw) || !TryGet(pin.Linear, out var linear) || !TryGet(pin.ValidityMask, out var mask))
        {
            return (null, CenteredRegistrationReason.RetainedInputMissing);
        }
        var retained = await _reader.ReadLinearAsync(pin.Key, pin.Linear.ArtifactId, cancellationToken).ConfigureAwait(false);
        if (retained is null)
        {
            return (null, CenteredRegistrationReason.RetainedInputMissing);
        }
        var qualified = retained.Linear;
        if (!pin.Raw.Matches(raw) || !pin.Linear.Matches(linear) || !pin.ValidityMask.Matches(mask) ||
            !CenteredStackLinearInput.MatchesExposure(pin, raw, linear, mask, qualified) ||
            pin.MeasurementGeometrySha256 != retained.MeasurementGeometry.Identity())
        {
            return (null, CenteredRegistrationReason.RetainedInputMismatch);
        }

        IReadOnlyList<StellarDetection>? detections = null;
        var invalidMeasurement = false;
        ExposureMeasurementStatus? measurementStatus = null;
        string? measurementReason = null;
        if (pin.Measurement is { } measurementPin && TryGet(measurementPin, out var measurement))
        {
            try
            {
                if (!measurementPin.Matches(measurement) || measurement.CaptureId != pin.Key.CaptureId ||
                    measurement.SchemaVersion != ExposureMeasurementProducts.SchemaVersion)
                {
                    throw new ArgumentException("Measurement artifact differs from the fixed exposure product.");
                }
                var expected = ExpectedMeasurement(retained, raw, linear);
                detections = ExposureMeasurementConsumers.RegistrationDetections(measurement, expected);
                if (detections.Count == 0)
                {
                    var sources = ExposureMeasurementProducts.Read(measurement, expected).Sources;
                    measurementStatus = sources.Status;
                    measurementReason = sources.Reason;
                }
                else
                {
                    measurementStatus = ExposureMeasurementStatus.Usable;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or JsonException)
            {
                invalidMeasurement = true;
            }
        }

        CenteredStackPlate? plate = null;
        var invalidPlate = false;
        if (pin.Plate is { } platePins)
        {
            try
            {
                plate = CenteredStackPlate.Verify(raw, GetPlate(platePins.Associations), GetPlate(platePins.Scene),
                    GetPlate(platePins.Context), GetPlate(platePins.Solution), cancellationToken);
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                invalidPlate = true;
            }
        }
        return (new(pin, raw, linear, mask, detections, invalidMeasurement, measurementStatus, measurementReason, plate, invalidPlate),
            CenteredRegistrationReason.Contributed);

        bool TryGet(CenteredStackArtifactPin artifactPin, out ProcessingArtifact artifact) =>
            artifacts.TryGetValue(artifactPin.ArtifactId, out artifact!);

        ProcessingArtifact GetPlate(CenteredStackArtifactPin artifactPin) =>
            TryGet(artifactPin, out var artifact) && artifact.CaptureId == pin.Key.CaptureId && artifactPin.Matches(artifact)
                ? artifact : throw new ArgumentException("A fixed retained plate input is missing or differs.");
    }

    private static ExposureMeasurementInputV1 ExpectedMeasurement(CenteredStackQualifiedExposure retained,
        ProcessingArtifact raw, ProcessingArtifact linear)
    {
        var qualified = retained.Linear;
        var source = qualified.Provenance;
        var disposition = source.Disposition == LinearExposureDisposition.Calibrated
            ? ExposureCalibrationDisposition.ReferenceCalibrated : ExposureCalibrationDisposition.NormalizationOnly;
        return new(ExposureMeasurementSourceV1.FromDescriptor(qualified.Exposure.Raw),
            new(qualified.Exposure.Key.CaptureId, source.LinearArtifactId, source.LinearDescriptorSha256,
                source.LinearPayloadSha256, source.Recipe.IdentitySha256, source.OutputIdentitySha256, linear.Layout!),
            disposition, null, raw.Compatibility.Orientation, retained.MeasurementGeometry.NativePixelPitchMicrometers,
            qualified.Exposure.Geometry.Orientation, retained.MeasurementGeometry.ApertureProjection);
    }

    private static MapChoice ChooseMap(ResolvedExposure source, ResolvedExposure reference, FrameLayoutDescriptor layout,
        CancellationToken cancellationToken)
    {
        if (source.Pin.GeometrySha256 != reference.Pin.GeometrySha256 || source.Linear.Layout != layout ||
            source.Linear.Compatibility != reference.Linear.Compatibility || source.Raw.Compatibility != reference.Raw.Compatibility ||
            source.Linear.RecipeIdentitySha256 != reference.Linear.RecipeIdentitySha256 ||
            source.Pin.IntegrationTicks != reference.Pin.IntegrationTicks ||
            source.Raw.Conditions?.Gain != reference.Raw.Conditions?.Gain || source.Raw.Conditions?.Offset != reference.Raw.Conditions?.Offset)
        {
            return new(CenteredRegistrationReason.IncompatibleExposure);
        }
        if (source.InvalidPlate || reference.InvalidPlate)
        {
            return new(CenteredRegistrationReason.InvalidRetainedPlate);
        }
        if (source.InvalidMeasurement || reference.InvalidMeasurement)
        {
            return new(CenteredRegistrationReason.InvalidMeasurement);
        }
        if (source.Detections is not { } sourcePoints || reference.Detections is not { } referencePoints)
        {
            return new(CenteredRegistrationReason.MeasurementUnavailable);
        }
        if (sourcePoints.Count == 0)
        {
            return NoCentroids(source.MeasurementStatus);
        }
        if (referencePoints.Count == 0)
        {
            return NoCentroids(reference.MeasurementStatus);
        }
        if (source.Plate is { Mapping: { } sourceMap } && reference.Plate is { Mapping: { } referenceMap })
        {
            if (!source.Plate.IsCompatibleWith(reference.Plate))
            {
                return new(CenteredRegistrationReason.IncompatiblePlatePair);
            }
            var map = new CenteredStackPlate.Pair(sourceMap, referenceMap);
            var check = DirectCentroidRegistration.VerifyPlatePair(map, sourcePoints, referencePoints,
                layout.Width, layout.Height, cancellationToken);
            return check.Status == DirectRegistrationStatus.Verified
                ? new(CenteredRegistrationReason.Contributed, CenteredRegistrationMethod.VerifiedPlatePair, map, check)
                : new(CenteredRegistrationReason.RegistrationRejected, Check: check);
        }
        var result = DirectCentroidRegistration.Fit(sourcePoints, referencePoints, layout.Width, layout.Height, cancellationToken);
        return result.Map is { } similarity
            ? new(CenteredRegistrationReason.Contributed, CenteredRegistrationMethod.VerifiedCentroidSimilarity, similarity, result.Check)
            : new(CenteredRegistrationReason.RegistrationRejected, Check: result.Check);
    }

    private static MapChoice NoCentroids(ExposureMeasurementStatus? status) => new(status switch
    {
        ExposureMeasurementStatus.Empty => CenteredRegistrationReason.MeasurementEmpty,
        ExposureMeasurementStatus.Unavailable => CenteredRegistrationReason.MeasurementUnavailable,
        _ => CenteredRegistrationReason.MeasurementUnsuitable
    });

    private static ProcessingOutcome Publish(CenteredStackPlanV1 plan, ProcessingAuxiliaryInput auxiliary,
        ProcessingRecipeIdentity identity, string variant, Dictionary<Guid, ProcessingArtifact> artifacts,
        CenteredStackSourceEvidence[] decisions, ResolvedExposure? reference, RegisteredLinearSamples? samples,
        CenteredStackDisposition disposition, CancellationToken cancellationToken)
    {
        var lineage = plan.Slots.Select(slot => slot.Exposure).OfType<CenteredStackExposurePin>()
            .SelectMany(CenteredStackPlanV1.Pins)
            .Where(pin => artifacts.TryGetValue(pin.ArtifactId, out var artifact) && pin.Matches(artifact))
            .Select(pin => artifacts[pin.ArtifactId]).ToArray();
        var compatibility = reference?.Linear.Compatibility ?? lineage.FirstOrDefault()?.Compatibility;
        if (compatibility is null || lineage.Length == 0)
        {
            return ProcessingOutcome.Skipped(ReferenceUnavailable, nameof(plan.ReferenceCaptureId));
        }
        var contributors = plan.Slots.Where((_, index) => decisions[index].Reason == CenteredRegistrationReason.Contributed)
            .Select(slot => slot.Exposure!).ToArray();
        var integration = TimeSpan.FromTicks(contributors.Sum(exposure => exposure.IntegrationTicks));
        var layout = reference?.Linear.Layout;
        var isColor = layout?.PixelFormat == CameraPixelFormat.BayerRggb16;
        var algorithms = new List<ProcessingAlgorithmIdentity>
        {
            new(RecipeName, RecipeVersion),
            new("bilinear-registered-mean", LinearRegisteredMean.AlgorithmVersion),
            new("centroid-similarity", DirectCentroidRegistration.FitVersion),
            new("plate-pair-check", DirectCentroidRegistration.PlateCheckVersion)
        };
        if (isColor)
        {
            algorithms.Add(new("linear-bayer", LinearBayerReconstruction.AlgorithmVersion));
        }

        var products = new List<ProcessingProduct>();
        var outputs = new List<CenteredStackOutputPin>();
        if (samples is not null && layout is not null)
        {
            string[] channels = isColor ? ["red", "green", "blue"] : ["mono"];
            var pixelLayout = new FrameLayoutDescriptor(layout.Width, layout.Height, layout.Width * 2,
                CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None, 0, ushort.MaxValue, (long)layout.Width * layout.Height * 2);
            for (var channel = 0; channel < channels.Length; channel++)
            {
                var payload = EncodePlane(samples.Codes.Span, channels.Length, channel, cancellationToken);
                Add(channels[channel], variant + "-" + channels[channel], "application/x-hvo-linear-frame", pixelLayout, payload);
            }
            var supportLayout = new FrameLayoutDescriptor(layout.Width, layout.Height, layout.Width,
                CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None, 0, 31, (long)layout.Width * layout.Height);
            Add("support", variant + "-support", CenteredRegisteredStackEvidence.SupportMediaType, supportLayout, samples.Support);
        }

        var center = plan.Slots[2].Exposure;
        var evidence = new CenteredRegisteredStackEvidence(CenteredRegisteredStackEvidence.CurrentSchemaVersion, auxiliary.IdentitySha256!,
            plan, disposition,
            center?.ExposureStartedUtc.AddTicks((center.ExposureEndedUtc - center.ExposureStartedUtc).Ticks / 2),
            contributors.Length == 0 ? null : contributors[0].ExposureStartedUtc,
            contributors.Length == 0 ? null : contributors[^1].ExposureEndedUtc,
            integration.Ticks, layout?.Width ?? 0, layout?.Height ?? 0, samples?.ValidPixelCount ?? 0,
            CenteredRegisteredStackEvidence.LinearUnits, decisions, outputs);
        var bytes = CenteredStackPlanV1.Serialize(evidence);
        products.Add(ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, variant + "-" + EvidenceVariant,
            CenteredRegisteredStackEvidence.MediaType, null, bytes, identity, algorithms, lineage, integration, compatibility,
            ProcessingProductKind.Metadata, CenteredRegisteredStackEvidence.CurrentSchemaVersion,
            ProcessingIdentity.ComputePayloadSha256(bytes)));
        cancellationToken.ThrowIfCancellationRequested();
        return ProcessingOutcome.Produced(products.ToArray());

        void Add(string channel, string outputVariant, string mediaType, FrameLayoutDescriptor outputLayout, ReadOnlyMemory<byte> payload)
        {
            var product = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Combined, outputVariant, mediaType, outputLayout,
                payload, identity, algorithms, lineage, integration, compatibility);
            products.Add(product);
            outputs.Add(new(channel, ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
                product.OutputIdentitySha256, product.ChecksumSha256));
        }
    }

    private static byte[] EncodePlane(ReadOnlySpan<ushort> codes, int channels, int channel, CancellationToken cancellationToken)
    {
        var bytes = new byte[codes.Length / channels * 2];
        for (var index = 0; index < bytes.Length / 2; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2, 2), codes[index * channels + channel]);
        }
        return bytes;
    }

    private static byte DisplayedSlots(RegisteredLinearSamples samples, CancellationToken cancellationToken)
    {
        byte used = 0;
        for (var index = 0; index < samples.Support.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var support = samples.Support.Span[index];
            if (CenteredRegisteredStackEvidence.ContributorsAt(support) >= 2)
            {
                used |= support;
            }
        }
        return used;
    }

    private static CenteredStackSourceEvidence InitialDecision(CenteredStackSlot slot) => new(slot.Offset, slot.CaptureId,
        slot.State switch
        {
            CenteredSlotState.Missing => CenteredRegistrationReason.MissingSlot,
            CenteredSlotState.NotReadyAtClosure => CenteredRegistrationReason.NotReadyAtClosure,
            CenteredSlotState.Ineligible => CenteredRegistrationReason.IneligibleAtClosure,
            _ => CenteredRegistrationReason.RetainedInputMissing
        });

    private static CenteredStackPlateEvidence? PlateEvidence(CenteredStackPlate? plate) => plate is null ? null : new(
        plate.Solution.SolutionIdentitySha256, plate.Solution.Assessment.IdentitySha256,
        plate.Solution.Assessment.ReasonCode, plate.Solution.UncertaintyReasonCode);

    private sealed record ResolvedExposure(CenteredStackExposurePin Pin, ProcessingArtifact Raw, ProcessingArtifact Linear,
        ProcessingArtifact Mask, IReadOnlyList<StellarDetection>? Detections, bool InvalidMeasurement,
        ExposureMeasurementStatus? MeasurementStatus, string? MeasurementReason,
        CenteredStackPlate? Plate, bool InvalidPlate);

    private sealed record MapChoice(CenteredRegistrationReason Reason, CenteredRegistrationMethod? Method = null,
        IImageRegistrationMap? Map = null, DirectRegistrationCheck? Check = null);
}
