using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
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
        var source = CreateSource(context.Config, raw, descriptor, context.AcquisitionTiming);
        var scene = context.RawCapture?.Manifest.Scene;
        var lineageValid = VirtualSkyNamedPass.TryReadPredictionLineage(context.Config, scene, out var named);
        var observedShutter = lineageValid && named is not null ? VirtualSkyNamedPass.ReadObservedShutter(scene, descriptor) : null;
        var namedFailure = !lineageValid ? "NamedPassPredictionLineageUnavailable" :
            named is not null && observedShutter is null ? "NamedPassPredictionTimingUnavailable" : null;
        // Whole-millisecond observations already match the descriptor. Keep their existing evidence format.
        var virtualShutter = observedShutter is { } shutter &&
            (shutter.ScenarioStartUtc != descriptor.Timing.ExposureStartedUtc || shutter.ScenarioEndUtc != descriptor.Timing.ExposureEndedUtc)
            ? shutter : null;
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
            input = BindPreparedShutter(CaptureSatelliteProducts.ParseInput(evidence.Payload.Span), virtualShutter, namedFailure);
            result = input.Plan is null ? null :
                frozen.SingleOrDefault(static p => p.SchemaVersion == CaptureOrbitalPredictionJson.SchemaVersion)?.Payload;
        }
        else
        {
            var prepared = await workStore.PrepareAsync(context, Name, Options.OutputVariant,
                () => ResolveAsync(context, descriptor, named, virtualShutter, namedFailure, cancellationToken), cancellationToken).ConfigureAwait(false);
            input = BindPreparedShutter(prepared.Input, virtualShutter, namedFailure);
            result = null;
            if (ReferenceEquals(input, prepared.Input))
            {
                prepared = await workStore.PinAsync(prepared, cancellationToken).ConfigureAwait(false);
                input = BindPreparedShutter(prepared.Input, virtualShutter, namedFailure);
                exactSnapshot = await ReadExactAsync(input, cancellationToken).ConfigureAwait(false);
                if (input.Plan is not null)
                    result = await workStore.ReadOrPredictAsync(prepared, source, exactSnapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        exactSnapshot ??= await ReadExactAsync(input, cancellationToken).ConfigureAwait(false);
        if (CaptureContractJson.ComputeDescriptorSha256(input.Descriptor) != CaptureContractJson.ComputeDescriptorSha256(descriptor))
            throw new InvalidDataException("Retained satellite inputs belong to another raw descriptor.");
        context.RecordCanonicalInput("capture-satellite-input", CaptureSatelliteProducts.InputSchema,
            ProcessingIdentity.ComputePayloadSha256(CaptureSatelliteProducts.SerializeInput(input)));
        if (input.Plan is not null)
            context.RecordExecutionRequest(CaptureSatelliteProducts.CreatePredictionRequest(source, OutputVariant));
        context.RecordExecutionRoute(ProcessingNodeExecutionRoute.InProcess);
        LayeredPresentationCaptureProcessing.Add(context, CaptureSatelliteProducts.Create(input, source, OutputVariant, result, exactSnapshot)
            .Where(static p => p.SchemaVersion is CaptureSatelliteProducts.InputSchema or CaptureSatelliteProducts.StatusSchema or
                CaptureOrbitalPredictionJson.SchemaVersion).ToArray());
    }

    internal static ProcessingArtifact CreateSource(CameraModuleConfig config, FrameArtifact raw,
        ReconstructionDescriptor descriptor, CaptureAcquisitionTiming? acquisitionTiming = null) =>
        CameraAgentRecipeExecutionAdapter.CreateArtifact(config, raw, descriptor.Artifact.Variant,
            acquisitionTiming, descriptor) with
        {
            MediaType = descriptor.Artifact.MediaType,
            CreatedUtc = descriptor.Artifact.CreatedUtc,
            ObservationStartedUtc = descriptor.Timing.ExposureStartedUtc,
            ObservationEndedUtc = descriptor.Timing.ExposureEndedUtc
        };

    private async ValueTask<OrbitalSnapshotReadResult?> ReadExactAsync(CaptureSatelliteInputV1 input, CancellationToken cancellationToken)
    {
        if (input.Plan is null || CaptureSatelliteProducts.ReadSnapshotManifest(input) is not { } manifest) return null;
        var exact = await snapshots.ReadExactAsync(manifest.ManifestSha256, cancellationToken).ConfigureAwait(false);
        if (exact.Snapshot is null) throw new IOException($"The retained orbital snapshot is unavailable ({exact.UnavailableReason}).");
        return exact;
    }

    private static CaptureSatelliteInputV1 BindPreparedShutter(CaptureSatelliteInputV1 input,
        VirtualExposureProvenance? shutter, string? namedFailure)
    {
        if (input.Plan is null)
            return input;
        // Previously prepared work can predate the exact shutter binding. Never publish its coarse plan as a named result.
        var reason = namedFailure ?? (input.VirtualShutter != shutter ? "NamedPassPredictionTimingMismatch" : null);
        return reason is null ? input : input with { Plan = null, UnavailableReason = reason, VirtualShutter = shutter };
    }

    private async ValueTask<CaptureSatelliteInputV1> ResolveAsync(CaptureProcessingContext context,
        ReconstructionDescriptor descriptor, VirtualSkyNamedPass.NamedPassPredictionLineage? named,
        VirtualExposureProvenance? virtualShutter, string? namedFailure, CancellationToken cancellationToken)
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
        var exposure = virtualShutter is { } shutter
            ? new OrbitalUtcInterval(shutter.ScenarioStartUtc, shutter.ScenarioEndUtc)
            : new OrbitalUtcInterval(descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc);
        // Both seams are already warmed by startup/refresh. Any cold materialization happens on this durable lane.
        var selectionPolicy = CaptureSatelliteTracksOptions.CreateSelectionPolicy(Options.AllowStaleDiagnostics);
        var pin = named?.Inputs ?? Options.PinnedInputs;
        var snapshot = namedFailure is not null ? OrbitalSnapshotReadResult.Unavailable(OrbitalSnapshotUnavailableReason.IntegrityFailure) :
            pin is null ? await snapshots.ReadCurrentAsync(cancellationToken).ConfigureAwait(false) :
                await snapshots.ReadExactAsync(pin.SnapshotManifestSha256, cancellationToken).ConfigureAwait(false);
        var time = await timeSource.ResolveAsync(exposure, cancellationToken).ConfigureAwait(false);
        var available = time.Source == CameraAgentOrbitalTimeDataSource.Override ? Options.OverrideTimeDataAvailableUtc :
            new[] { time.EarthOrientationProvenance?.RetrievedUtc, time.LeapSecondsProvenance?.RetrievedUtc }.Max();
        var reason = namedFailure ?? (named is not null && !MatchesNamedPass(named, selectionPolicy) ? "NamedPassPredictionInputsMismatch" :
            time.Data is null ? "TimeInputsUnavailable:" + time.Problem :
            available is null ? "TimeAvailabilityUnknown" : available > descriptor.Timing.DurableIngressUtc ? "TimeNotOriginallyAvailable" :
            descriptor.Layout.Readout is null ? "ReadoutGeometryUnavailable" : observer is null ? "CaptureLocationUnavailable" : null);
        if (pin is not null)
        {
            if (snapshot.Snapshot?.Manifest.ManifestSha256 != pin.SnapshotManifestSha256)
                reason ??= "PinnedSnapshotUnavailable";
            if (time.Data?.EarthOrientationSha256 != pin.EarthOrientationSha256 ||
                time.Data?.LeapSecondsSha256 != pin.LeapSecondsSha256)
                reason ??= "PinnedTimeInputsUnavailable";
        }
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
            // A millisecond readout report in the shutter-close bucket cannot establish its exact ordering.
            var readoutUtc = descriptor.Timing.ReadoutCompletedUtc >= exposure.EndUtc
                ? descriptor.Timing.ReadoutCompletedUtc : (DateTimeOffset?)null;
            var timing = new OrbitalTimingFacts(exposure, OrbitalTimingBasis.Reported, readoutUtc,
                readoutUtc is null ? OrbitalTimingBasis.Unknown : OrbitalTimingBasis.Reported,
                virtualShutter is null ? "capture-descriptor-command-status-observations-v1" : "virtual-sky-named-shutter-utc-ticks-v1",
                AstrometricClockFacts.NotSupplied);
            var model = OrbitalPredictionModel.Create(time.Data!, assumeEllipsoidalHeight: true);
            plan = new(descriptor, observer!.Value, geometry, timing, model, Options.Bounds,
                new(CaptureOrbitalAssessmentMode.OriginalAvailability, descriptor.Timing.DurableIngressUtc,
                    descriptor.Timing.DurableIngressUtc, available!.Value), snapshot,
                selectionPolicy,
                Options.CatalogNumbers.Select(static n => new OrbitalObjectIdentity("norad", n)),
                context.Config.Rig.HorizonObstruction is null ? null : context.Config.Rig, virtualShutter);
            if (pin?.ExpectedRecords is { } records && plan.Plan.Selections.Any(selection =>
                    selection.SelectedRecord?.RecordSha256 != records[selection.ObjectIdentity.CatalogNumber]))
            {
                plan = null;
                reason = "PinnedRecordSelectionMismatch";
            }
        }
        return new(CaptureSatelliteProducts.InputSchema, descriptor, context.Config.Rig, observer, OptionalBytes(plan?.SerializePlan()),
            OptionalBytes(manifest), null, OptionalBytes(eop), OptionalBytes(leaps), time.Source.ToString(), available, reason)
        {
            VirtualShutter = virtualShutter,
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

    private bool MatchesNamedPass(VirtualSkyNamedPass.NamedPassPredictionLineage named, OrbitalSelectionPolicy selection)
    {
        var expected = named.Inputs;
        var records = expected.ExpectedRecords!;
        if (named.SelectionPolicySha256 != selection.IdentitySha256 || Options.CatalogNumbers.Any(number => !records.ContainsKey(number)))
            return false;
        return Options.PinnedInputs is not { } configured ||
            configured.SnapshotManifestSha256 == expected.SnapshotManifestSha256 &&
            configured.EarthOrientationSha256 == expected.EarthOrientationSha256 &&
            configured.LeapSecondsSha256 == expected.LeapSecondsSha256 &&
            (configured.ExpectedRecords is null ||
             configured.ExpectedRecords.All(entry => records.TryGetValue(entry.Key, out var hash) && hash == entry.Value));
    }

    private static ReadOnlyMemory<byte>? OptionalBytes(byte[]? bytes)
    {
        if (bytes is null) return default;
        return new ReadOnlyMemory<byte>(bytes);
    }
}

public sealed class CaptureSatelliteTracksOptions : IValidatableObject
{
    internal static OrbitalSelectionPolicy CreateSelectionPolicy(bool allowStaleDiagnostics)
        => new([new(CelesTrakGpSource.DatasetKey, 0)], allowStaleDiagnostics);

    [Required, MaxLength(96), MinLength(1)]
    public string OutputVariant { get; init; } = "capture-satellites-v1";
    public IReadOnlyList<string> CatalogNumbers { get; init; } = [];
    public bool AllowStaleDiagnostics { get; init; }
    public DateTimeOffset? OverrideTimeDataAvailableUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureSatellitePinnedInputs? PinnedInputs { get; init; }
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
        if (PinnedInputs is { } pin && (!OrbitalContract.IsSha256(pin.SnapshotManifestSha256) ||
            !OrbitalContract.IsSha256(pin.EarthOrientationSha256) || !OrbitalContract.IsSha256(pin.LeapSecondsSha256) ||
            pin.ExpectedRecords is { } records && (records.Count != CatalogNumbers.Count ||
                CatalogNumbers.Any(number => !records.TryGetValue(number, out var hash) || !OrbitalContract.IsSha256(hash)))))
            yield return new ValidationResult("Pinned satellite inputs require valid uppercase SHA-256 identities and a matching optional record set.");
    }
}

/// <summary>Exact local inputs required when a capture profile uses a frozen orbital scenario.</summary>
public sealed record CaptureSatellitePinnedInputs(string SnapshotManifestSha256, string EarthOrientationSha256, string LeapSecondsSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? ExpectedRecords = null);
