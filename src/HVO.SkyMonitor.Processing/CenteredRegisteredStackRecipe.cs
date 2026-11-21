using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Reads exact retained producer contracts for one frozen window, independently of the stack artifact bytes.</summary>
public interface ICenteredStackInputReader
{
    /// <summary>Attach independently retained custody and geometry to the in-memory pinned pixel artifact.</summary>
    ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(ExposureKey key, ProcessingArtifact linear, CancellationToken cancellationToken);
}

/// <summary>Explicit opt-in registration for a closed centered window.</summary>
public sealed class CenteredRegisteredStackRecipe(ICenteredStackInputReader reader) : IProcessingRecipe
{
    public const string RecipeName = "centered-registered-stack";
    public const string RecipeVersion = "centered-direct-registration-v2";
    internal const string LegacyRecipeVersion = "centered-direct-registration-v1";
    private const string ReferenceFootprintVersion = "reference-aperture-footprint-v1";
    public const string InvalidPlan = "registration.invalid-centered-plan";
    public const string RetainedInputsUnavailable = "registration.retained-inputs-unavailable";
    public const string ReferenceUnavailable = "registration.reference-unavailable";
    public const string ResourceLimit = "registration.resource-limit";
    public const string EvidenceVariant = "centered-stack-evidence";
    // Five complete, individually bounded exposures, including both plate endpoints and the fixed measurement.
    public const long MaximumInputBytes = 224L * 1024 * 1024;
    private readonly ICenteredStackInputReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    private static readonly ProcessingRecipeDefinition RecipeDefinition = new(RecipeName, "1.0.0", RecipeVersion, ProcessingOperationKind.Window);

    public ProcessingRecipeDefinition Definition => RecipeDefinition;

    public static ProcessingRecipeIdentity FixedIdentity { get; } =
        ProcessingIdentity.CreateRecipeIdentity(RecipeDefinition, NormalizeFixedOptions(default));

    public static ProcessingExecutionRequest CreateRequest(CenteredStackPlanV1 plan,
        string linearVariant, string outputVariant, IReadOnlyList<ProcessingArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(RecipeName, default, ProcessingInputSelector.Calibrated(linearVariant), artifacts, outputVariant,
            AuxiliaryInputs: [plan.ToAuxiliaryInput()]);
    }

    /// <summary>
    /// Restores the exact drawable geometry for a published contributor. For a plate pair, the caller supplies
    /// both endpoints' pinned raw, association, scene, context and solution artifacts from the published plan.
    /// </summary>
    public static IImageRegistrationMap? ReconstructSourceMap(CenteredRegisteredStackEvidence evidence, int offset,
        IReadOnlyDictionary<Guid, ProcessingArtifact> plateInputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(plateInputs);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, -2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, 2);
        var slot = offset + 2;
        var member = evidence.Sources[slot];
        if (member.Reason != CenteredRegistrationReason.Contributed)
        {
            return null;
        }
        CenteredRegisteredStackEvidence.RequireVerifiedMap(member);
        if (member.Method == CenteredRegistrationMethod.ReferenceIdentity)
        {
            return new ImageSimilarity(1, 0, 0, 0);
        }
        if (member.Method == CenteredRegistrationMethod.VerifiedCentroidSimilarity)
        {
            return member.Similarity;
        }
        var source = Open(slot);
        var reference = Open(2);
        if (source.Mapping is not { } sourceMap || reference.Mapping is not { } referenceMap ||
            !source.IsCompatibleWith(reference))
        {
            throw new InvalidDataException("Published plate pair could not be reconstructed.");
        }
        return new CenteredStackPlate.Pair(sourceMap, referenceMap);

        CenteredStackPlate Open(int index)
        {
            var endpoint = evidence.Plan.Slots[index].Exposure ??
                throw new InvalidDataException("Published plate contributor has no pinned exposure.");
            var pins = endpoint.Plate ?? throw new InvalidDataException("Published contributor has no plate pins.");
            var association = Pinned(pins.Associations);
            if (!CenteredStackPlate.MatchesMeasurementPin(association, endpoint.Measurement))
            {
                throw new InvalidDataException("Published centered association has a different pinned exposure measurement.");
            }
            var plate = CenteredStackPlate.Verify(Pinned(endpoint.Raw), association, Pinned(pins.Scene),
                Pinned(pins.Context), Pinned(pins.Solution), cancellationToken);
            if (PlateEvidence(plate) != evidence.Sources[index].Plate)
            {
                throw new InvalidDataException("Published plate identity differs from retained solution.");
            }
            return plate;
        }

        ProcessingArtifact Pinned(CenteredStackArtifactPin pin) =>
            plateInputs.TryGetValue(pin.ArtifactId, out var artifact) && pin.Matches(artifact)
                ? artifact : throw new InvalidDataException("Published plate evidence is missing or changed.");
    }

    public JsonElement NormalizeOptions(JsonElement options) => NormalizeFixedOptions(options);

    private static JsonElement NormalizeFixedOptions(JsonElement options)
    {
        if (options.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
            (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
        {
            throw new ArgumentException("Centered registration uses a fixed numerical policy.", nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(new
        {
            order = "ordered-five-slot-center-v1",
            coordinates = "delivered-pixel-edges-v1",
            directCentroidFit = DirectCentroidRegistration.FitVersion,
            plateCheck = DirectCentroidRegistration.PlateCheckVersion,
            combiner = LinearRegisteredMean.AlgorithmVersion,
            referenceFootprint = ReferenceFootprintVersion,
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
        if (request.RecipeName != RecipeName || request.InputArtifactId is not null ||
            request.Input.Kind != ProcessingInputKind.Calibrated ||
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

        JsonElement normalized;
        try
        {
            normalized = NormalizeOptions(request.Options);
        }
        catch (ArgumentException)
        {
            return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidOptions, nameof(request.Options));
        }
        var effectiveOptions = ProcessingIdentity.BindExecutionInputs(normalized, request.Input, null, [auxiliary]);
        var expectedIdentity = ProcessingIdentity.CreateRecipeIdentity(Definition, effectiveOptions);
        if (identity.OperationKind != expectedIdentity.OperationKind ||
            !CenteredRegisteredStackEvidence.HasRecipe(identity, expectedIdentity.IdentitySha256))
        {
            return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidLineage, nameof(identity));
        }

        var exposures = plan.Slots.Select(slot => slot.Exposure).OfType<CenteredStackExposurePin>().ToArray();
        var pins = exposures.SelectMany(CenteredStackPlanV1.ArtifactBounds).ToDictionary(item => item.Pin.ArtifactId);
        var artifacts = new Dictionary<Guid, ProcessingArtifact>();
        foreach (var artifact in request.Inputs)
        {
            if (!pins.TryGetValue(artifact.ArtifactId, out var pin) || !artifacts.TryAdd(artifact.ArtifactId, artifact))
            {
                return ProcessingOutcome.TerminalFailure(InvalidPlan, nameof(request.Inputs));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact.Payload.Length > pin.MaximumBytes)
            {
                return ProcessingOutcome.TerminalFailure(ResourceLimit, nameof(request.Inputs));
            }
            if (!pin.Pin.Matches(artifact))
            {
                return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidLineage, nameof(request.Inputs));
            }
        }
        if (artifacts.Count != pins.Count)
        {
            return ProcessingOutcome.RetryableFailure(RetainedInputsUnavailable);
        }
        var selected = ProcessingRecipeSupport.ResolveMany(request, out _);
        if (!selected.Select(artifact => artifact.ArtifactId).SequenceEqual(
            exposures.Select(exposure => exposure.Linear.ArtifactId)))
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
            if (loaded.Reason == CenteredRegistrationReason.RetainedInputMissing)
            {
                return ProcessingOutcome.RetryableFailure(RetainedInputsUnavailable);
            }
            if (loaded.Reason == CenteredRegistrationReason.RetainedInputMismatch)
            {
                return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidLineage);
            }
            resolved[slot] = loaded.Exposure;
            decisions[slot] = decisions[slot] with
            {
                Reason = loaded.Reason,
                Plate = PlateEvidence(loaded.Exposure?.Plate),
                MeasurementStatus = loaded.Exposure?.MeasurementStatus,
                MeasurementReason = loaded.Exposure?.MeasurementReason
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
        var accumulator = new LinearRegisteredMean(layout.Width, layout.Height, isColor ? 3 : 1, reference.Aperture);
        var decoder = new CenteredStackLinearInput.Decoder(layout);
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
                var image = decoder.Decode(source.Raw, source.Linear, source.Mask, source.Aperture, cancellationToken);
                supported = accumulator.Add(slot, image, choice.Map, cancellationToken);
                reason = supported == 0 ? CenteredRegistrationReason.NoValidSamples : reason;
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
        var hasStack = (displayedSlots & (1 << 2)) != 0;
        for (var slot = 0; slot < resolved.Length; slot++)
        {
            if (slot != 2 && decisions[slot].Reason == CenteredRegistrationReason.Contributed &&
                (!hasStack || (displayedSlots & (1 << slot)) == 0))
            {
                decisions[slot] = decisions[slot] with
                {
                    Reason = CenteredRegistrationReason.NoSharedSupport,
                    Method = null,
                    Similarity = null,
                    SupportedPixels = 0
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
            hasStack ? CenteredStackDisposition.Registered : CenteredStackDisposition.SingleExposure,
            cancellationToken);
    }

    private async ValueTask<(ResolvedExposure? Exposure, CenteredRegistrationReason Reason)> ResolveExposureAsync(
        CenteredStackExposurePin pin, Dictionary<Guid, ProcessingArtifact> artifacts, CancellationToken cancellationToken)
    {
        if (!TryGet(pin.Raw, out var raw) || !TryGet(pin.Linear, out var linear) || !TryGet(pin.ValidityMask, out var mask))
        {
            return (null, CenteredRegistrationReason.RetainedInputMissing);
        }
        CenteredStackQualifiedExposure? retained;
        try
        {
            retained = await _reader.ReadLinearAsync(pin.Key, linear, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return (null, CenteredRegistrationReason.RetainedInputMismatch);
        }
        catch (IOException)
        {
            return (null, CenteredRegistrationReason.RetainedInputMissing);
        }
        if (retained is null)
        {
            return (null, CenteredRegistrationReason.RetainedInputMissing);
        }
        var qualified = retained.Linear;
        if (!CenteredStackLinearInput.MatchesExposure(pin, raw, linear, mask, qualified) ||
            pin.MeasurementBindingSha256 != retained.MeasurementBindingIdentity())
        {
            return (null, CenteredRegistrationReason.RetainedInputMismatch);
        }

        IReadOnlyList<StellarDetection>? detections = null;
        ProcessingArtifact? retainedMeasurement = null;
        var invalidMeasurement = false;
        ExposureMeasurementStatus? measurementStatus = null;
        string? measurementReason = null;
        if (pin.Measurement is { } measurementPin && TryGet(measurementPin, out var measurement))
        {
            try
            {
                if (measurement.CaptureId != pin.Key.CaptureId ||
                    measurement.SchemaVersion != ExposureMeasurementProducts.SchemaVersion)
                {
                    throw new ArgumentException("Measurement artifact differs from the fixed exposure product.");
                }
                var expected = retained.MeasurementInput;
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
                retainedMeasurement = measurement;
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
                var association = GetPlate(platePins.Associations);
                var scene = GetPlate(platePins.Scene);
                if (association.RecipeDescriptor?.Name == ExposureAssociationProducts.RecipeName)
                {
                    if (retainedMeasurement is null || !CenteredStackPlate.MatchesMeasurementPin(association, pin.Measurement))
                    {
                        throw new InvalidDataException("Centered association does not name the pinned measurement.");
                    }
                    var measured = ExposureMeasurementProducts.Read(retainedMeasurement, retained.MeasurementInput);
                    ExposureAssociationProducts.VerifyMeasurement(association, raw, retainedMeasurement, measured, scene);
                }
                plate = CenteredStackPlate.Verify(raw, association, scene,
                    GetPlate(platePins.Context), GetPlate(platePins.Solution), cancellationToken);
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
            {
                invalidPlate = true;
            }
        }
        return (new(pin, raw, linear, mask, retained.MeasurementInput.ApertureProjection, detections, invalidMeasurement, measurementStatus, measurementReason, plate, invalidPlate),
            CenteredRegistrationReason.Contributed);

        bool TryGet(CenteredStackArtifactPin artifactPin, out ProcessingArtifact artifact) =>
            artifacts.TryGetValue(artifactPin.ArtifactId, out artifact!);

        ProcessingArtifact GetPlate(CenteredStackArtifactPin artifactPin) =>
            TryGet(artifactPin, out var artifact) && artifact.CaptureId == pin.Key.CaptureId
                ? artifact : throw new ArgumentException("A fixed retained plate input is missing or differs.");
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
            .Select(pin => artifacts[pin.ArtifactId]).ToArray();
        var compatibility = reference?.Linear.Compatibility ?? lineage.FirstOrDefault()?.Compatibility;
        if (compatibility is null || lineage.Length == 0)
        {
            return ProcessingOutcome.Skipped(ReferenceUnavailable, nameof(plan.ReferenceCaptureId));
        }
        var contributors = plan.Slots.Where((_, index) => decisions[index].Reason == CenteredRegistrationReason.Contributed)
            .Select(slot => slot.Exposure!).ToArray();
        var integration = TimeSpan.FromTicks(contributors.Sum(exposure => exposure.IntegrationTicks));
        var pixelLineage = contributors.Select(exposure => artifacts[exposure.Linear.ArtifactId]).ToArray();
        var layout = reference?.Linear.Layout;
        var isColor = layout?.PixelFormat == CameraPixelFormat.BayerRggb16;
        var algorithms = new List<ProcessingAlgorithmIdentity>
        {
            new(RecipeName, RecipeVersion),
            new("bilinear-registered-mean", LinearRegisteredMean.AlgorithmVersion),
            new("reference-aperture-footprint", ReferenceFootprintVersion),
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
            var pixelLayout = CenteredRegisteredStackEvidence.PlaneLayout(layout.Width, layout.Height, support: false);
            for (var channel = 0; channel < channels.Length; channel++)
            {
                var payload = EncodePlane(samples.Codes.Span, channels.Length, channel, cancellationToken);
                Add(channels[channel], variant + "-" + channels[channel], "application/x-hvo-linear-frame", pixelLayout, payload);
            }
            var supportLayout = CenteredRegisteredStackEvidence.PlaneLayout(layout.Width, layout.Height, support: true);
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
                payload, identity, algorithms, pixelLineage, integration, compatibility);
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
        ProcessingArtifact Mask, ProjectionContext? Aperture, IReadOnlyList<StellarDetection>? Detections, bool InvalidMeasurement,
        ExposureMeasurementStatus? MeasurementStatus, string? MeasurementReason,
        CenteredStackPlate? Plate, bool InvalidPlate);

    private sealed record MapChoice(CenteredRegistrationReason Reason, CenteredRegistrationMethod? Method = null,
        IImageRegistrationMap? Map = null, DirectRegistrationCheck? Check = null);
}
