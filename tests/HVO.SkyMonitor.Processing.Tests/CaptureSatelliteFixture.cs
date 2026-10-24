using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.Tests;

internal sealed class CaptureSatelliteFixture
{
    internal CameraModuleConfig Configuration { get; }
    internal ReconstructionDescriptor Descriptor { get; }
    internal ProcessingArtifact Raw { get; }
    internal OrbitalSnapshot Snapshot { get; }
    internal OrbitalTimeData Time { get; }
    internal CaptureOrbitalPredictionInput Input { get; }
    internal CaptureSatelliteInputV1 Evidence { get; }

    internal CaptureSatelliteFixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nominal");
        var track = OrbitalPredictionJson.ParseTrack(File.ReadAllBytes(Path.Combine(root, "iss", "track-03.json")));
        Snapshot = OrbitalSnapshot.Parse(File.ReadAllBytes(Path.Combine(root, "snapshot", "manifest.json")),
            File.ReadAllBytes(Path.Combine(root, "snapshot", "records.json"))).Snapshot!;
        Time = OrbitalTimeData.FromFinals2000A(File.ReadAllBytes(Path.Combine(root, "predicted-2026.finals2000A")),
            File.ReadAllBytes(Path.Combine(root, "leap-seconds.list")));
        var rig = new CameraRigConfig(
            new("retained-satellite-fixture", 640, 640, 2, SensorColorMode.Color, CameraPixelFormat.BayerRggb16),
            new("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, 320, 320, 313.6, HorizontalFlip: true,
                CalibrationVersion: "satellite-fixture-v1"), new(90, 0, 0),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 1, 1));
        var observer = track.Observer;
        Configuration = new(new(observer.LatitudeDegrees, observer.LongitudeDegrees, observer.ElevationMeters, "UTC"),
            new("StandaloneFixture"), rig, CapturePipelineConfig.Empty, "satellite-fixture")
        {
            DeploymentLocation = DeploymentLocationSnapshot.Create("satellite-site", 1, "fixture", null,
                DateTimeOffset.UnixEpoch, null, observer.LatitudeDegrees, observer.LongitudeDegrees, observer.ElevationMeters, "UTC")
        };
        var payload = new byte[640 * 640 * 2];
        var layout = new FrameLayoutDescriptor(640, 640, 1280, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian,
            16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 0, 65535, payload.Length)
        { Readout = track.Geometry.Readout };
        var recipe = RecipeIdentityDescriptor.Create("raw-satellite-fixture", "1.0.0", "fixture-v1", JsonSerializer.SerializeToElement(new { }));
        ProfileIdentityDescriptor Profile(string sha) => new("fixture", "1", sha);
        var end = track.RequestedInterval.EndUtc;
        Descriptor = new(new("satellite-fixture", "satellite-fixture", 1, Guid.Parse("16410000-0000-0000-0000-000000000001")),
            new(track.RequestedInterval.StartUtc, track.RequestedInterval.StartUtc, end, end.AddMilliseconds(10), end.AddMilliseconds(20)),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 1, 1, null, null, null, null),
            new(Profile(CameraRigProfileIdentity.ComputeSha256(rig)), Profile(new('A', 64)), Profile(new('B', 64)),
                Profile(new('C', 64)), Profile(new('D', 64))), layout,
            new(Guid.Parse("16410000-0000-0000-0000-000000000002"), FrameArtifactRole.Raw, "StandaloneFixture", "raw",
                end.AddMilliseconds(20), [], recipe, "application/octet-stream", ProcessingIdentity.ComputePayloadSha256(payload)))
        { Location = Configuration.DeploymentLocation.ToProvenance() };
        var geometry = track.Geometry with
        {
            RigIdentitySha256 = Descriptor.Profiles.Rig.Sha256,
            CalibrationIdentitySha256 = CaptureContractJson.ComputeCanonicalJsonSha256(rig.Optics),
            Projection = RigProjectionContextFactory.Create(rig)
        };
        Input = new(Descriptor, observer, geometry, track.Timing, OrbitalPredictionModel.Create(Time, true),
            new(32, 120, 100_000, 32, 32, 8192, 10_000, 1, .1),
            new(CaptureOrbitalAssessmentMode.OriginalAvailability, Descriptor.Timing.DurableIngressUtc,
                Descriptor.Timing.DurableIngressUtc, track.RequestedInterval.StartUtc.AddDays(-1)),
            OrbitalSnapshotReadResult.Available(Snapshot), new([new("celestrak:gp:active", 0)]), [track.Lineage.ObjectIdentity]);
        Raw = new(Descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, "raw", ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256,
            Descriptor.Artifact.MediaType, layout, payload, Descriptor.Artifact.CreatedUtc, TimeSpan.FromSeconds(5),
            new(Descriptor.Profiles.Rig.Sha256, Descriptor.Profiles.Rig.Sha256, Descriptor.Profiles.Calibration.Sha256,
                Descriptor.Profiles.Mask.Sha256, Descriptor.Profiles.Sensor.Sha256, "fixture", Descriptor.Profiles.Processing.Sha256), 1,
            ObservationStartedUtc: Descriptor.Timing.ExposureStartedUtc, ObservationEndedUtc: Descriptor.Timing.ExposureEndedUtc)
        { CaptureId = Descriptor.Capture.CaptureId, DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(Descriptor) };
        Evidence = new(CaptureSatelliteProducts.InputSchema, Descriptor, rig, observer, Input.SerializePlan(),
            Snapshot.SerializeManifest(), Snapshot.SerializePayload(), Time.ExportEarthOrientation(), Time.ExportLeapSeconds(),
            "Override", Input.Plan.Availability.TimeDataAvailableUtc, null);
    }
}
