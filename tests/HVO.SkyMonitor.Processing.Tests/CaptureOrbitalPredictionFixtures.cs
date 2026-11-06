using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.Tests;

internal sealed class CaptureOrbitalPredictionFixture
{
    internal OrbitalExposureTrackV1 Track { get; }
    internal OrbitalSnapshot Snapshot { get; }
    internal OrbitalTimeData Time { get; }
    internal ReconstructionDescriptor Descriptor { get; }
    internal ProcessingArtifact Raw { get; }
    internal OrbitalQueryBounds Bounds { get; } = new(32, 120, 100_000, 32, 32, 8192, 10_000, 1, .01);
    internal OrbitalSelectionPolicy Policy { get; } = new([new("celestrak:gp:active", 0)]);

    internal CaptureOrbitalPredictionFixture(string name = "iss", OrbitalGeometryFacts? geometry = null)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nominal");
        Track = OrbitalPredictionJson.ParseTrack(File.ReadAllBytes(Path.Combine(root, name, "track-03.json")));
        var snapshot = name == "iss" ? "snapshot" : "two-starlinks";
        Snapshot = OrbitalSnapshot.Parse(File.ReadAllBytes(Path.Combine(root, snapshot, "manifest.json")),
            File.ReadAllBytes(Path.Combine(root, snapshot, "records.json"))).Snapshot!;
        Time = OrbitalTimeData.FromFinals2000A(File.ReadAllBytes(Path.Combine(root, "predicted-2026.finals2000A")),
            File.ReadAllBytes(Path.Combine(root, "leap-seconds.list")));
        var g = geometry ?? Track.Geometry;
        var width = g.ImageTransform.OutputWidthPixels; var height = g.ImageTransform.OutputHeightPixels;
        var payload = new byte[width * height * 2];
        var layout = new FrameLayoutDescriptor(width, height, width * 2,
            g.Readout.CfaOriginX is null ? CameraPixelFormat.Mono16 : CameraPixelFormat.BayerRggb16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, g.Readout.CfaOriginX is null ? ColorFilterArrayPattern.None : ColorFilterArrayPattern.Rggb,
            0, 65535, payload.Length)
        { Readout = g.Readout };
        var end = Track.RequestedInterval.EndUtc;
        var recipe = RecipeIdentityDescriptor.Create("raw-capture-fixture", "1.0.0", "1640-fixture-v1", JsonSerializer.SerializeToElement(new { }));
        ProfileIdentityDescriptor Profile(string sha) => new("fixture", "1", sha);
        Descriptor = new(new("fixture-agent", "fixture-rig", 3, Guid.Parse("16400000-0000-0000-0000-000000000001")),
            new(Track.RequestedInterval.StartUtc, Track.RequestedInterval.StartUtc, end, end.AddMilliseconds(10), end.AddMilliseconds(20)),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 1, 1, null, null, null, null),
            new(Profile(g.RigIdentitySha256), Profile(g.CalibrationIdentitySha256), Profile(new('A', 64)), Profile(new('B', 64)), Profile(new('C', 64))),
            layout, new(Guid.Parse("16400000-0000-0000-0000-000000000002"), FrameArtifactRole.Raw,
                "fixture-agent", "raw", end.AddMilliseconds(20), [], recipe, "application/octet-stream", ProcessingIdentity.ComputePayloadSha256(payload)));
        Assert.IsTrue(Descriptor.Validate().IsValid, JsonSerializer.Serialize(Descriptor.Validate()));
        Raw = new(Descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, "raw", ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256,
            Descriptor.Artifact.MediaType, layout, payload, Descriptor.Artifact.CreatedUtc, TimeSpan.FromSeconds(5),
            new("fixture-rig", "north", "configured", "full", "fixture", "night", "fixture"), 3,
            ObservationStartedUtc: Track.RequestedInterval.StartUtc, ObservationEndedUtc: end)
        {
            CaptureId = Descriptor.Capture.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(Descriptor)
        };
    }

    internal CaptureOrbitalPredictionInput Input(OrbitalQueryBounds? bounds = null, OrbitalSnapshotReadResult? snapshot = null,
        OrbitalSelectionPolicy? policy = null, IEnumerable<OrbitalObjectIdentity>? objects = null,
        CaptureOrbitalAvailabilityV1? availability = null, ReconstructionDescriptor? descriptor = null,
        OrbitalGeometryFacts? geometry = null, OrbitalTimingFacts? timing = null, OrbitalPredictionModelFacts? model = null,
        ObserverLocation? observer = null) => new(descriptor ?? Descriptor, observer ?? Track.Observer, geometry ?? Track.Geometry,
            timing ?? Track.Timing, model ?? Track.Model, bounds ?? Bounds,
            availability ?? Availability, snapshot ?? OrbitalSnapshotReadResult.Available(Snapshot), policy ?? Policy,
            objects ?? [Track.Lineage.ObjectIdentity]);

    internal CaptureOrbitalAvailabilityV1 Availability => new(CaptureOrbitalAssessmentMode.OriginalAvailability,
        Descriptor.Timing.DurableIngressUtc, Descriptor.Timing.DurableIngressUtc, Descriptor.Timing.ExposureStartedUtc.AddDays(-1));

    internal ProcessingExecutionRequest Request(ProcessingArtifact? raw = null) => new(CaptureOrbitalPredictionRecipe.Name,
        JsonSerializer.SerializeToElement(new { }), ProcessingInputSelector.Raw("raw"), [raw ?? Raw], "predicted-satellites");

    internal async Task<(ProcessingProduct Product, CaptureOrbitalPredictionV1 Output)> Run(CaptureOrbitalPredictionInput? input = null,
        TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        input ??= Input();
        var outcome = await new ProcessingRecipeExecutor([new CaptureOrbitalPredictionRecipe(input, Time, clock)])
            .ExecuteAsync(Request(), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{outcome.ReasonCode}:{outcome.Field}");
        var product = outcome.Products.Single();
        return (product, CaptureOrbitalPredictionJson.Parse(product.Payload.Span, input));
    }
}
