using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

/// <summary>Opt-in local package for an explicitly synthetic named orbital pass.</summary>
public sealed record VirtualSkyNamedPassOptions(string PackagePath, string PackageSha256);

/// <summary>Validates and converts frozen orbital evidence before acquisition. The renderer receives only anonymous signal.</summary>
internal sealed class VirtualSkyNamedPass
{
    private const string Schema = "virtual-sky-named-pass-v1";
    private const string PredictionLineageProperty = "namedPassPredictionV1";
    private const int DocumentLimit = 256 * 1024;
    private const int MaximumTracksPerObject = 12;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 20
    };

    private VirtualSkyNamedPass(VirtualTransientScenarioDefinition definition, ObserverLocation observer,
        NamedPassPredictionLineage prediction)
    {
        Definition = definition;
        Observer = observer;
        var parameters = JsonNode.Parse(CaptureContractJson.SerializeToElement(definition).GetRawText())!.AsObject();
        parameters[PredictionLineageProperty] = JsonSerializer.SerializeToNode(prediction, Json);
        Parameters = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(parameters));
        ParametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(Parameters);
    }

    public VirtualTransientScenarioDefinition Definition { get; }
    public ObserverLocation Observer { get; }
    public JsonElement Parameters { get; }
    public string ParametersSha256 { get; }

    public static VirtualSkyNamedPass Prepare(
        VirtualSkyNamedPassOptions reference,
        CameraModuleConfig configuration,
        VirtualSkyCameraModuleOptions options,
        ResolvedSensorReadout? readout,
        CancellationToken cancellationToken)
    {
        var rig = configuration.Rig;
        var sensor = rig.Sensor;
        if (!Path.IsPathFullyQualified(reference.PackagePath) || options.FixedSequenceStartUtc is not null ||
            (readout?.Layout.PixelFormat ?? sensor.PixelFormat) is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new InvalidDataException("Named orbital passes require an absolute local package path, shutter UTC and supported 16-bit readout.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var packagePath = Path.GetFullPath(reference.PackagePath);
        var directory = PhysicalRoot.Open(Path.GetDirectoryName(packagePath)!);
        directory.Verify(packagePath, "read-named-orbital-package");
        var package = Deserialize<NamedPassPackage>(ReadPinned(packagePath, reference.PackageSha256, DocumentLimit));
        if (package.SchemaVersion != Schema || package.EpochUtc == default || package.EpochUtc.Offset != TimeSpan.Zero ||
            package.Objects.Count is < 1 or > 4)
        {
            throw new InvalidDataException("Invalid named orbital pass package schema, epoch or object count.");
        }

        var snapshot = OrbitalSnapshot.Parse(ReadReference(directory, package.SnapshotManifest),
            ReadReference(directory, package.SnapshotRecords)).Snapshot ??
            throw new InvalidDataException("Invalid named orbital pass snapshot or normalized records.");
        var time = OrbitalTimeData.FromFinals2000A(
            ReadReference(directory, package.EarthOrientation, CameraAgentPinnedOrbitalTimeSource.MaximumEarthOrientationBytes),
            ReadReference(directory, package.LeapSeconds, CameraAgentPinnedOrbitalTimeSource.MaximumLeapSecondsBytes));
        var model = OrbitalPredictionModel.Create(time, assumeEllipsoidalHeight: true);
        var selection = CaptureSatelliteTracksOptions.CreateSelectionPolicy(package.AllowStaleDiagnostics);
        var site = configuration.ResolveObservatory(package.EpochUtc);
        var observer = ToObserver(site);
        var expectedReadout = readout?.Geometry ?? new FrameReadoutDescriptor(sensor.WidthPixels, sensor.HeightPixels,
            0, 0, sensor.WidthPixels, sensor.HeightPixels, 1, 1, FrameBinningAlgorithm.IdentityV1,
            sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? 0 : null,
            sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? 0 : null);
        var projection = RigProjectionContextFactory.Create(rig);
        var expectedTransform = ProjectedSceneImageTransformV1.Identity(projection.WidthPixels, projection.HeightPixels);
        var rigHash = RigProjectionContextFactory.CreateProfileHashSha256(rig);
        var readoutHash = CaptureContractJson.ComputeCanonicalJsonSha256(expectedReadout);
        var calibrationHash = new AstrometricCalibration(projection, rig.Optics.CalibrationVersion, readoutHash).IdentitySha256;
        var opticsHash = CaptureContractJson.ComputeCanonicalJsonSha256(rig.Optics);
        var projector = ProjectorFactory.Create(projection);
        var objects = new HashSet<OrbitalObjectIdentity>();
        var primitives = new List<VirtualTransientSkyTrack>();
        var sampleCount = 0;
        int? seed = null;

        foreach (var group in package.Objects)
        {
            if (group is null)
                throw new InvalidDataException("Named orbital pass package contains an empty object.");
            var identity = new OrbitalObjectIdentity("norad", group.CatalogNumber);
            if (!OrbitalContract.IsObjectIdentity(identity) || !objects.Add(identity) ||
                group.Tracks.Count is < 1 or > MaximumTracksPerObject)
            {
                throw new InvalidDataException("Named orbital pass objects must be distinct with a bounded track list.");
            }

            var record = snapshot.RecordsFor(identity).SingleOrDefault(r => r.RecordSha256 == group.RecordSha256)
                ?? throw new InvalidDataException("Named orbital pass record does not match the pinned object and snapshot.");
            var optical = Deserialize<SyntheticOptics>(ReadReference(directory, group.Optics));
            optical.Validate();
            seed ??= optical.Seed;
            if (seed != optical.Seed)
                throw new InvalidDataException("Named orbital pass optical profiles must share one renderer seed.");

            var sampleOffsets = new HashSet<double>();
            var previousEnd = package.EpochUtc;
            foreach (var entry in group.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is null)
                    throw new InvalidDataException("Named orbital pass package contains an empty track entry.");
                var track = OrbitalPredictionJson.ParseTrack(ReadReference(directory, entry.Track));
                var interval = track.RequestedInterval;
                if (track.Status != OrbitalExecutionStatus.Complete || track.CoveredInterval != interval ||
                    entry.ExposureStartUtc.Offset != TimeSpan.Zero || interval.StartUtc != entry.ExposureStartUtc ||
                    interval.StartUtc < previousEnd ||
                    interval.EndUtc - interval.StartUtc != TimeSpan.FromSeconds(optical.ExposureSeconds) ||
                    interval.EndUtc - package.EpochUtc > TimeSpan.FromHours(1) ||
                    ToObserver(configuration.ResolveObservatory(interval.StartUtc)) != observer ||
                    ToObserver(configuration.ResolveObservatory(interval.EndUtc)) != observer ||
                    track.Timing.ExposureBasis != OrbitalTimingBasis.Estimated ||
                    track.Timing.ReadoutCompletedUtc is not null || track.Timing.ReadoutBasis != OrbitalTimingBasis.Unknown ||
                    entry.Transmission is < 0 or > 1 || !double.IsFinite(entry.Transmission))
                {
                    throw new InvalidDataException("Named orbital track time, shutter, site or optical transmission does not match its package.");
                }
                previousEnd = interval.EndUtc;

                var lineage = track.Lineage;
                if (lineage.ObjectIdentity != identity || lineage.RecordSha256 != record.RecordSha256 ||
                    lineage.SourceDataset != record.SourceDataset || lineage.EpochUtc != record.EpochUtc ||
                    lineage.SnapshotManifestSha256 != snapshot.Manifest.ManifestSha256 ||
                    lineage.NormalizedPayloadSha256 != snapshot.Manifest.NormalizedPayloadSha256 ||
                    lineage.SelectionPolicySha256 != selection.IdentitySha256 || track.Model != model)
                {
                    throw new InvalidDataException("Named orbital track identity, snapshot or time model does not match its package.");
                }
                if (OrbitalRecordSelector.Select(snapshot, identity, interval, selection).SelectedRecord?.RecordSha256 != record.RecordSha256)
                    throw new InvalidDataException("Named orbital track record does not match the capture prediction selection policy.");
                time.ValidateModel(model, interval, OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig);
                time.ValidatePropagationInterval(record.EpochUtc, interval);

                var geometry = track.Geometry;
                if (track.Observer != observer || geometry.Basis != OrbitalGeometryBasis.ConfiguredRig ||
                    geometry.HorizonPolicy != HorizonPolicy.GeometricHorizon ||
                    geometry.ProjectionCoordinates != OrbitalProjectionCoordinates.ReadoutView ||
                    geometry.RigIdentitySha256 != rigHash || geometry.Readout != expectedReadout ||
                    geometry.ReadoutIdentitySha256 != readoutHash || geometry.ImageTransform != expectedTransform ||
                    geometry.Projection != projection ||
                    geometry.CalibrationIdentitySha256 != calibrationHash && geometry.CalibrationIdentitySha256 != opticsHash)
                {
                    throw new InvalidDataException("Named orbital track does not match the configured observer, rig, projection or readout.");
                }

                sampleCount = checked(sampleCount + track.Samples.Count);
                if (sampleCount > VirtualTransientScenarioDefinition.MaximumKeyframeCount)
                    throw new InvalidDataException("Named orbital pass sample budget exceeded.");
                foreach (var sample in track.Samples)
                {
                    sampleOffsets.Add((sample.Utc - package.EpochUtc).TotalSeconds);
                    if (sample.Pixel is { } pixel &&
                        (projector.Project(sample.Horizontal) is not { } projected ||
                            double.Hypot(pixel.X - projected.X, pixel.Y - projected.Y) > 1e-8))
                    {
                        throw new InvalidDataException("Named orbital sample pixels do not reproduce under the declared projection.");
                    }
                }

                // A dark stimulus supplies prediction geometry but no electrons, regardless of orbital illumination.
                if (entry.Transmission > 0)
                    AddSegments(primitives, track, optical, package.EpochUtc, entry.Transmission);
            }
            if (optical.LightCurve.Any(point => !sampleOffsets.Contains(point.OffsetSeconds)))
                throw new InvalidDataException("Synthetic optical light-curve knots must coincide with orbital sample times.");
        }

        var unrelated = options.TransientScenario;
        if (unrelated is not null && (unrelated.EpochUtc != package.EpochUtc || unrelated.Recurrence is not null))
            throw new InvalidDataException("Additional transient controls must use the named package epoch and no recurrence.");

        var definition = new VirtualTransientScenarioDefinition
        {
            EpochUtc = package.EpochUtc,
            Seed = seed!.Value,
            TemporalSampleCount = 64,
            SourcePackageSha256 = reference.PackageSha256,
            SkyTracks = [.. primitives, .. unrelated?.SkyTracks ?? []],
            SensorTracks = unrelated?.SensorTracks ?? []
        };
        definition = definition with { ScenarioId = definition.ComputeCanonicalScenarioId() };
        definition.ValidateSensorBounds(sensor.WidthPixels, sensor.HeightPixels);
        var pins = new CaptureSatellitePinnedInputs(snapshot.Manifest.ManifestSha256,
            time.EarthOrientationSha256!, time.LeapSecondsSha256, package.Objects.ToDictionary(
                static group => group.CatalogNumber, static group => group.RecordSha256, StringComparer.Ordinal));
        return new(definition, observer, new(selection.IdentitySha256, pins));
    }

    internal static bool TryReadPredictionLineage(CameraModuleConfig configuration, SceneProvenance? scene,
        out NamedPassPredictionLineage? lineage)
    {
        lineage = null;
        var namedOption = string.Equals(configuration.Module?.Type, "VirtualSky", StringComparison.OrdinalIgnoreCase) &&
            configuration.ModuleOptions is { ValueKind: JsonValueKind.Object } options
            ? options.EnumerateObject().LastOrDefault(static entry => entry.Name.Equals("namedOrbitalPass", StringComparison.OrdinalIgnoreCase)).Value
            : default;
        var configured = namedOption.ValueKind == JsonValueKind.Object;
        var requiresLineage = configured || scene?.VirtualExposure?.TimeMapping is
            VirtualExposureTimeMapping.ShutterUtc or VirtualExposureTimeMapping.FixedCelestialAndShutterUtc ||
            (scene?.TransientScenario?.Parameters is { ValueKind: JsonValueKind.Object } declared &&
                declared.TryGetProperty(PredictionLineageProperty, out _));
        if (!requiresLineage)
            return true;
        if (scene?.TransientScenario?.Parameters is not { ValueKind: JsonValueKind.Object } parameters ||
            !parameters.TryGetProperty("sourcePackageSha256", out var package))
            return false;
        if (package.ValueKind != JsonValueKind.String || !OrbitalContract.IsSha256(package.GetString()) ||
            !parameters.TryGetProperty(PredictionLineageProperty, out var value))
            return false;

        if (configured)
        {
            var configuredHash = namedOption.EnumerateObject().LastOrDefault(static entry =>
                entry.Name.Equals("packageSha256", StringComparison.OrdinalIgnoreCase)).Value;
            if (configuredHash.ValueKind != JsonValueKind.String || configuredHash.GetString() != package.GetString())
                return false;
        }

        try
        {
            lineage = value.Deserialize<NamedPassPredictionLineage>(Json);
            return lineage is { Inputs: { } inputs } && OrbitalContract.IsSha256(lineage.SelectionPolicySha256) &&
                OrbitalContract.IsSha256(inputs.SnapshotManifestSha256) &&
                OrbitalContract.IsSha256(inputs.EarthOrientationSha256) &&
                OrbitalContract.IsSha256(inputs.LeapSecondsSha256) &&
                inputs.ExpectedRecords is { Count: > 0 and <= 4 } records &&
                records.All(static entry => OrbitalContract.IsObjectIdentity(new("norad", entry.Key)) &&
                    OrbitalContract.IsSha256(entry.Value));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static VirtualExposureProvenance? ReadObservedShutter(SceneProvenance? scene, ReconstructionDescriptor descriptor)
        => scene is { VirtualExposure: { } shutter, TransientScenario: { } scenario } &&
            shutter.MatchesObservedShutter(descriptor) &&
            scene.SceneUtc == shutter.CelestialMidpointUtc &&
            scenario.IntegrationStartUtc == shutter.ScenarioStartUtc && scenario.IntegrationEndUtc == shutter.ScenarioEndUtc &&
            (scene.CloudScenario is not { } cloud ||
                (cloud.IntegrationStartUtc == shutter.ScenarioStartUtc && cloud.IntegrationEndUtc == shutter.ScenarioEndUtc))
            ? shutter : null;

    internal sealed record NamedPassPredictionLineage(string SelectionPolicySha256, CaptureSatellitePinnedInputs Inputs);

    public static ObserverLocation ToObserver(ObservatoryLocation site)
        => new(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters);

    private static void AddSegments(List<VirtualTransientSkyTrack> target, OrbitalExposureTrackV1 track,
        SyntheticOptics optical, DateTimeOffset epoch, double transmission)
    {
        var attenuationMagnitude = -2.5 * Math.Log10(transmission);
        foreach (var segment in track.Segments)
        {
            if (segment.SampleCount < 2)
                throw new InvalidDataException("Named orbital segments need two samples for exposure integration.");
            for (var start = 0; start < segment.SampleCount - 1; start += 127)
            {
                var keyframes = new VirtualTransientSkyKeyframe[Math.Min(128, segment.SampleCount - start)];
                for (var index = 0; index < keyframes.Length; index++)
                {
                    var sample = track.Samples[segment.StartSampleIndex + start + index];
                    var offset = (sample.Utc - epoch).TotalSeconds;
                    var magnitude = optical.MagnitudeAt(offset) + attenuationMagnitude;
                    if (magnitude > 30)
                        throw new InvalidDataException("Named orbital optical transmission exceeds the supported magnitude range.");
                    keyframes[index] = new()
                    {
                        OffsetSeconds = offset,
                        AltitudeDegrees = sample.Horizontal.AltitudeDegrees,
                        AzimuthDegrees = sample.Horizontal.AzimuthDegrees,
                        Magnitude = magnitude,
                        AngularWidthDegrees = optical.AngularWidthDegrees,
                        RedWeight = optical.RedWeight,
                        GreenWeight = optical.GreenWeight,
                        BlueWeight = optical.BlueWeight
                    };
                }
                // Adjacent chunks share only an endpoint. The renderer integrates each segment's temporal support.
                target.Add(new()
                {
                    PrimitiveId = "orbital-signal-" + (target.Count + 1).ToString(CultureInfo.InvariantCulture),
                    Keyframes = keyframes
                });
            }
        }
    }

    private static byte[] ReadReference(PhysicalRoot directory, FileReference reference, int maximumBytes = DocumentLimit)
        => ReadPinned(directory.Resolve(reference.Path), reference.Sha256, maximumBytes);

    private static byte[] ReadPinned(string path, string hash, int maximumBytes)
    {
        if (!OrbitalContract.IsSha256(hash))
            throw new InvalidDataException("Named orbital package checksums must be uppercase SHA-256.");
        DurableSync.RequireRegularFile(path);
        using var stream = File.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Named orbital package file length is outside its bound.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || Convert.ToHexString(SHA256.HashData(bytes)) != hash)
            throw new InvalidDataException("Named orbital package file checksum mismatch.");
        return bytes;
    }

    private static T Deserialize<T>(byte[] bytes) where T : class
        => JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty named orbital package document.");

    private sealed record FileReference(string Path, string Sha256);
    private sealed record PackageTrack(FileReference Track, DateTimeOffset ExposureStartUtc, double Transmission);
    private sealed record PackageObject(string CatalogNumber, string RecordSha256, FileReference Optics, IReadOnlyList<PackageTrack> Tracks);
    private sealed record NamedPassPackage(string SchemaVersion, DateTimeOffset EpochUtc, FileReference SnapshotManifest,
        FileReference SnapshotRecords, FileReference EarthOrientation, FileReference LeapSeconds, IReadOnlyList<PackageObject> Objects,
        bool AllowStaleDiagnostics = false);
    private sealed record LightCurvePoint(double OffsetSeconds, double MagnitudeDelta);
    private sealed record SyntheticOptics(string SchemaVersion, bool IsSynthetic, double Magnitude, double AngularWidthDegrees,
        double RedWeight, double GreenWeight, double BlueWeight, double ExposureSeconds, int Seed, IReadOnlyList<LightCurvePoint> LightCurve)
    {
        public void Validate()
        {
            if (SchemaVersion != "satellite-synthetic-optical-v1" || !IsSynthetic ||
                !double.IsFinite(Magnitude) || Magnitude is < -30 or > 30 ||
                !double.IsFinite(AngularWidthDegrees) || AngularWidthDegrees is <= 0 or > 2 ||
                !double.IsFinite(ExposureSeconds) || ExposureSeconds is <= 0 or > 20 ||
                !IsWeight(RedWeight) || !IsWeight(GreenWeight) || !IsWeight(BlueWeight) ||
                RedWeight + GreenWeight + BlueWeight <= 0 || LightCurve.Count > 128)
                throw new InvalidDataException("Invalid explicit synthetic optical profile.");

            var previous = double.NegativeInfinity;
            foreach (var point in LightCurve)
            {
                if (point is null)
                    throw new InvalidDataException("Synthetic optical light curve contains an empty point.");
                var magnitude = Magnitude + point.MagnitudeDelta;
                if (!double.IsFinite(point.OffsetSeconds) || point.OffsetSeconds <= previous ||
                    point.OffsetSeconds is < 0 or > 3600 || !double.IsFinite(magnitude) || magnitude is < -30 or > 30)
                    throw new InvalidDataException("Invalid synthetic optical light curve.");
                previous = point.OffsetSeconds;
            }
        }

        public double MagnitudeAt(double offset)
        {
            if (LightCurve.Count == 0) return Magnitude;
            if (offset <= LightCurve[0].OffsetSeconds) return Magnitude + LightCurve[0].MagnitudeDelta;

            for (var index = 1; index < LightCurve.Count; index++)
            {
                var right = LightCurve[index];
                if (offset > right.OffsetSeconds) continue;
                var left = LightCurve[index - 1];
                var fraction = (offset - left.OffsetSeconds) / (right.OffsetSeconds - left.OffsetSeconds);
                return Magnitude + left.MagnitudeDelta + fraction * (right.MagnitudeDelta - left.MagnitudeDelta);
            }
            return Magnitude + LightCurve[^1].MagnitudeDelta;
        }

        private static bool IsWeight(double weight) => double.IsFinite(weight) && weight is >= 0 and <= 10;
    }
}
