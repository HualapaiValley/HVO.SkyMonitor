using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Explicit post-admission producer. Never runs during acquisition or consults providers.</summary>
internal sealed class CaptureSatelliteTracksProcessingStep(
    CaptureProcessingStepMetadata metadata, CaptureSatelliteTracksOptions options,
    CaptureSatelliteWorkStore workStore, IOrbitalSnapshotReader snapshots, ICameraAgentOrbitalTimeSource timeSource,
    IDeploymentLocationStore? locationStore = null)
    : ConfigurableCaptureProcessingStep<CaptureSatelliteTracksOptions>(metadata, options),
      ICaptureProcessingGraphStep, IMultiOutputCaptureProcessingGraphStep, IFrozenAuxiliaryInputCaptureProcessingStep
{
    internal const string Alias = "CaptureSatelliteTracks";
    public bool Enabled => true;
    public string RecipeName => CaptureOrbitalPredictionRecipe.Name;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
    public string? OutputSchemaVersion => CaptureOrbitalPredictionJson.SchemaVersion;
    public string? OutputMediaType => CaptureOrbitalPredictionJson.MediaType;
    public string? SharedStepVersion => CaptureSatelliteProducts.Version;
    public ProcessingOperationKind? SharedOperationKind => ProcessingOperationKind.Analyzer;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
    public IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
    [
        new(OutputRole, OutputVariant, RecipeName, OutputSchemaVersion,
            new(RecipeName, "1.0.0", CaptureOrbitalPredictionRecipe.AlgorithmVersion, ProcessingOperationKind.Analyzer), MediaType: OutputMediaType),
        new(OutputRole, OutputVariant + "-inputs", "capture-satellite-evidence", CaptureSatelliteProducts.InputSchema,
            MetadataRecipe("capture-satellite-evidence"), MediaType: CaptureSatelliteProducts.InputMediaType),
        new(OutputRole, OutputVariant + "-status", "capture-satellite-status", CaptureSatelliteProducts.StatusSchema,
            MetadataRecipe("capture-satellite-status"), MediaType: CaptureSatelliteProducts.StatusMediaType)
    ];

    private static ProcessingRecipeDefinition MetadataRecipe(string name) =>
        new(name, "1.0.0", CaptureSatelliteProducts.Version, ProcessingOperationKind.Analyzer);

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        var descriptor = context.ReconstructionDescriptor ?? throw new InvalidDataException("Satellite prediction requires durable raw admission.");
        if (context.Config.Pipeline.SchemaVersion != CapturePipelineSchemaVersions.ExplicitV2)
            throw new InvalidOperationException("Capture satellite prediction requires an explicit v2 graph.");
        var raw = context.Artifacts?.Raw ?? throw new InvalidDataException("Satellite prediction requires the exact raw capture.");
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, descriptor.Artifact.Variant,
            context.AcquisitionTiming, descriptor) with
        {
            MediaType = descriptor.Artifact.MediaType,
            CreatedUtc = descriptor.Artifact.CreatedUtc,
            ObservationStartedUtc = descriptor.Timing.ExposureStartedUtc,
            ObservationEndedUtc = descriptor.Timing.ExposureEndedUtc
        };
        CaptureSatelliteInputV1 input;
        ReadOnlyMemory<byte>? result;
        OrbitalSnapshotReadResult? exactSnapshot = null;
        if (context.IsReplayExecution)
        {
            if (context.FrozenAuxiliaryInputFailure is not null)
                throw new InvalidDataException("The frozen satellite products are missing or altered.");
            var frozen = context.GetFrozenAuxiliaryInputs();
            var evidence = frozen.SingleOrDefault(static p => p.SchemaVersion == CaptureSatelliteProducts.InputSchema)
                ?? throw new InvalidDataException("Replay requires the original retained satellite inputs.");
            input = CaptureSatelliteProducts.ParseInput(evidence.Payload.Span);
            result = frozen.SingleOrDefault(static p => p.SchemaVersion == CaptureOrbitalPredictionJson.SchemaVersion)?.Payload;
        }
        else
        {
            var prepared = await workStore.PrepareAsync(context, Name, Options.OutputVariant,
                () => ResolveAsync(context, descriptor, cancellationToken), cancellationToken).ConfigureAwait(false);
            prepared = await workStore.PinAsync(prepared, cancellationToken).ConfigureAwait(false);
            input = prepared.Input;
            exactSnapshot = await ReadExactAsync(input, cancellationToken).ConfigureAwait(false);
            result = await workStore.ReadOrPredictAsync(prepared, source, exactSnapshot, cancellationToken).ConfigureAwait(false);
        }
        exactSnapshot ??= await ReadExactAsync(input, cancellationToken).ConfigureAwait(false);
        if (CaptureContractJson.ComputeDescriptorSha256(input.Descriptor) != CaptureContractJson.ComputeDescriptorSha256(descriptor))
            throw new InvalidDataException("Retained satellite inputs belong to another raw descriptor.");
        context.RecordCanonicalInput("capture-satellite-input", CaptureSatelliteProducts.InputSchema,
            ProcessingIdentity.ComputePayloadSha256(CaptureSatelliteProducts.SerializeInput(input)));
        if (input.Plan is not null)
            context.RecordExecutionRequest(new(CaptureOrbitalPredictionRecipe.Name, JsonSerializer.SerializeToElement(new { }),
                ProcessingInputSelector.Raw(source.Variant), [source], OutputVariant, InputArtifactId: source.ArtifactId));
        context.RecordExecutionRoute(ProcessingNodeExecutionRoute.InProcess);
        LayeredPresentationCaptureProcessing.Add(context, CaptureSatelliteProducts.Create(input, source, OutputVariant, result, exactSnapshot)
            .Where(static p => p.SchemaVersion is CaptureSatelliteProducts.InputSchema or CaptureSatelliteProducts.StatusSchema or
                CaptureOrbitalPredictionJson.SchemaVersion).ToArray());
    }

    private async ValueTask<OrbitalSnapshotReadResult?> ReadExactAsync(CaptureSatelliteInputV1 input, CancellationToken cancellationToken)
    {
        if (input.Plan is null || CaptureSatelliteProducts.ReadSnapshotManifest(input) is not { } manifest) return null;
        var exact = await snapshots.ReadExactAsync(manifest.ManifestSha256, cancellationToken).ConfigureAwait(false);
        if (exact.Snapshot is null) throw new IOException($"The retained orbital snapshot is unavailable ({exact.UnavailableReason}).");
        return exact;
    }

    private async ValueTask<CaptureSatelliteInputV1> ResolveAsync(CaptureProcessingContext context,
        ReconstructionDescriptor descriptor, CancellationToken cancellationToken)
    {
        DeploymentLocationSnapshot? location = null;
        if (descriptor.Location is { } provenance)
        {
            if (context.Config.DeploymentLocation is { } configured && configured.ToProvenance() == provenance &&
                configured.IsEffectiveAt(descriptor.Timing.ExposureStartedUtc)) location = configured;
            else
            {
                try { location = locationStore?.Resolve(provenance, descriptor.Timing.ExposureStartedUtc); }
                catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
                {
                    // Retain the missing capture-time identity; a current location is never a substitute.
                }
            }
        }
        ObserverLocation? observer = location is null ? null :
            new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters);
        var exposure = new OrbitalUtcInterval(descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc);
        // Both seams are already warmed by startup/refresh. Any cold materialization happens on this durable lane.
        var snapshot = await snapshots.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        var time = await timeSource.ResolveAsync(exposure, cancellationToken).ConfigureAwait(false);
        var available = time.Source == CameraAgentOrbitalTimeDataSource.Override ? Options.OverrideTimeDataAvailableUtc :
            new[] { time.EarthOrientationProvenance?.RetrievedUtc, time.LeapSecondsProvenance?.RetrievedUtc }.Max();
        var reason = time.Data is null ? "TimeInputsUnavailable:" + time.Problem :
            available is null ? "TimeAvailabilityUnknown" : available > descriptor.Timing.DurableIngressUtc ? "TimeNotOriginallyAvailable" :
            descriptor.Layout.Readout is null ? "ReadoutGeometryUnavailable" : observer is null ? "CaptureLocationUnavailable" : null;
        if (time.Data is { } data && (data.EarthOrientationCoverage is not { } coverage ||
            coverage.StartUtc > exposure.StartUtc || coverage.EndUtc < exposure.EndUtc ||
            data.LeapTableCoverage.StartUtc > exposure.StartUtc || data.LeapTableCoverage.EndUtc < exposure.EndUtc))
            reason ??= "TimeCoverageUnavailable";
        if ((exposure.EndUtc - exposure.StartUtc).TotalSeconds > Options.Bounds.MaximumWindowSeconds)
            reason ??= "ExposureWindowBudget";
        if (descriptor.Layout.Readout is { } actual)
        {
            var sensor = context.Config.Rig.Sensor;
            var expected = context.Config.Rig.Readout is { } configuredReadout
                ? SensorReadoutResolver.Resolve(sensor, configuredReadout).Geometry
                : new FrameReadoutDescriptor(sensor.WidthPixels, sensor.HeightPixels, 0, 0, sensor.WidthPixels,
                    sensor.HeightPixels, 1, 1, FrameBinningAlgorithm.IdentityV1,
                    descriptor.Layout.CfaPattern == ColorFilterArrayPattern.Rggb ? 0 : null,
                    descriptor.Layout.CfaPattern == ColorFilterArrayPattern.Rggb ? 0 : null);
            if (actual != expected) reason ??= "CaptureReadoutProfileMismatch";
        }
        byte[]? manifest = null;
        if (snapshot.Snapshot is { } resolved)
        {
            manifest = resolved.SerializeManifest();
            if (resolved.Manifest.Sources.Any(s => s.RetrievedUtc > descriptor.Timing.DurableIngressUtc ||
                s.ProviderGeneratedUtc > descriptor.Timing.DurableIngressUtc)) reason ??= "SnapshotNotOriginallyAvailable";
            if (Options.CatalogNumbers.Sum(n => (long)resolved.RecordsFor(new("norad", n)).Count) >
                CaptureOrbitalPredictionInput.MaximumSelectionCandidates) reason ??= "SelectionCandidateBudget";
        }
        var eop = time.Data?.ExportEarthOrientation();
        var leaps = time.Data?.ExportLeapSeconds();
        CaptureOrbitalPredictionInput? plan = null;
        if (reason is null)
        {
            var readout = descriptor.Layout.Readout!;
            var projection = RigProjectionContextFactory.Create(context.Config.Rig);
            var geometry = new OrbitalGeometryFacts(OrbitalGeometryBasis.ConfiguredRig, descriptor.Profiles.Rig.Sha256,
                CaptureContractJson.ComputeCanonicalJsonSha256(readout), readout,
                CaptureContractJson.ComputeCanonicalJsonSha256(context.Config.Rig.Optics), null, null,
                OrbitalProjectionCoordinates.ReadoutView, projection,
                ProjectedSceneImageTransformV1.Identity(descriptor.Layout.Width, descriptor.Layout.Height), HorizonPolicy.GeometricHorizon);
            var timing = new OrbitalTimingFacts(exposure, OrbitalTimingBasis.Reported, descriptor.Timing.ReadoutCompletedUtc,
                OrbitalTimingBasis.Reported, "capture-descriptor-command-status-observations-v1", AstrometricClockFacts.NotSupplied);
            var model = OrbitalPredictionModel.Create(time.Data!, assumeEllipsoidalHeight: true);
            plan = new(descriptor, observer!.Value, geometry, timing, model, Options.Bounds,
                new(CaptureOrbitalAssessmentMode.OriginalAvailability, descriptor.Timing.DurableIngressUtc,
                    descriptor.Timing.DurableIngressUtc, available!.Value), snapshot,
                new([new("celestrak:gp:active", 0)], Options.AllowStaleDiagnostics),
                Options.CatalogNumbers.Select(static n => new OrbitalObjectIdentity("norad", n)));
        }
        return new(CaptureSatelliteProducts.InputSchema, descriptor, context.Config.Rig, observer, OptionalBytes(plan?.SerializePlan()),
            OptionalBytes(manifest), null, OptionalBytes(eop), OptionalBytes(leaps), time.Source.ToString(), available, reason)
        {
            RequestOptions = JsonSerializer.SerializeToElement(Options),
            TimeProvenance = JsonSerializer.SerializeToElement(new
            {
                time.EarthOrientationProvenance,
                time.LeapSecondsProvenance,
                time.ActiveManagedPair,
                OverrideAvailableUtc = Options.OverrideTimeDataAvailableUtc
            })
        };
    }

    private static ReadOnlyMemory<byte>? OptionalBytes(byte[]? bytes)
    {
        if (bytes is null) return default;
        return new ReadOnlyMemory<byte>(bytes);
    }
}

public sealed class CaptureSatelliteTracksOptions : IValidatableObject
{
    [Required, MaxLength(96), MinLength(1)]
    public string OutputVariant { get; init; } = "capture-satellites-v1";
    public IReadOnlyList<string> CatalogNumbers { get; init; } = [];
    public bool AllowStaleDiagnostics { get; init; }
    public DateTimeOffset? OverrideTimeDataAvailableUtc { get; init; }
    public OrbitalQueryBounds Bounds { get; init; } = new(32, 120, 100_000, 32, 32, 8192, 200, 1, .1);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        Bounds.Validate();
        if (CatalogNumbers.Count == 0 || CatalogNumbers.Count > Math.Min(Bounds.MaximumObjects, 256) ||
            CatalogNumbers.Distinct(StringComparer.Ordinal).Count() != CatalogNumbers.Count ||
            CatalogNumbers.Any(static n => !OrbitalContract.IsObjectIdentity(new OrbitalObjectIdentity("norad", n))))
            yield return new ValidationResult("Select an explicit bounded set of unique NORAD catalog numbers.");
        if (Bounds.MaximumWindowSeconds > 120 || Bounds.MaximumEvaluations > 100_000 || Bounds.MaximumTracks > 32 ||
            Bounds.MaximumSamples > 8192 || Bounds.MaximumElapsedMilliseconds > 10_000 ||
            OverrideTimeDataAvailableUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            yield return new ValidationResult("Satellite bounds or time availability exceed the capture contract.");
    }
}
