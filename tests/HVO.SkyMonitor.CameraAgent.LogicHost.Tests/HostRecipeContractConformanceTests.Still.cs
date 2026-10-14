using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
#if COMBINED_TESTS
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
#endif
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

public sealed partial class HostRecipeContractConformanceTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono8)]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.Rgb24)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task FixedPreviewPreservesNativeInputsAndFrozenContractsAcrossHosts(CameraPixelFormat format)
    {
        var source = Source(format);
        var original = source.Payload.ToArray();
        var wide = format is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16;
        var result = await Execute(source, BuiltInProcessingRecipes.FixedPreview,
            new FixedDisplayTransferOptions(0, wide ? ushort.MaxValue : byte.MaxValue, 1));
        Assert.AreEqual(8, result.Layout!.Width);
        Assert.AreEqual(8, result.Layout.Height);
        Assert.AreEqual(format is CameraPixelFormat.Rgb24 or CameraPixelFormat.BayerRggb16
            ? CameraPixelFormat.Rgb24 : CameraPixelFormat.Mono8, result.Layout.PixelFormat);
        CollectionAssert.Contains(result.Algorithms.ToArray(),
            new ProcessingAlgorithmIdentity("fixed-display-transfer", FixedDisplayTransfer.AlgorithmVersion));
        if (!wide) CollectionAssert.AreEqual(original, result.Payload.ToArray());
        CollectionAssert.AreEqual(original, source.Payload.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WindowKeogramPreservesActualTimeOrderAndMissingCoverageAcrossHosts(bool planned)
    {
        var frames = StillFrames();
        var options = StillOptions(frames[0].ObservationStartedUtc!.Value, planned);
        var result = await StillWindow([frames[2], frames[0], frames[1]], BuiltInProcessingRecipes.Keogram,
            options, [KeogramGeometryJson.CreateAuxiliaryInput(StillGeometry(frames[0]))]);
        Assert.AreEqual(planned ? 9 : 6, result.Layout!.Width);
        Assert.AreEqual(9, result.Layout.Height);
        CollectionAssert.AreEqual(frames.Select(f => f.ArtifactId).ToArray(), result.SourceArtifactIds.ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(3), result.TotalIntegration);
        var pixels = result.Payload.ToArray();
        var offset = planned ? 1 : 0;
        Assert.AreEqual((byte)10, pixels[offset]);
        Assert.AreEqual((byte)20, pixels[offset + 1]);
        Assert.AreEqual((byte)30, pixels[offset + 5]);
        if (planned)
        {
            Assert.IsTrue(pixels[0] is 32 or 96, "Leading missing coverage remains patterned.");
            Assert.IsTrue(pixels[8] is 32 or 96, "Trailing missing coverage remains patterned.");
        }
        Assert.IsTrue(pixels[offset + 2] is 32 or 96, "Internal missing coverage remains patterned.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BoundSegmentAssemblyMatchesDirectHostExecutionWithFrozenAxes(bool planned)
    {
        var frames = StillFrames();
        var geometry = StillGeometry(frames[0]);
        var natural = StillOptions(frames[0].ObservationStartedUtc!.Value, false);
        var finalOptions = StillOptions(frames[0].ObservationStartedUtc!.Value, planned);
        var direct = await StillWindow(frames, BuiltInProcessingRecipes.Keogram, finalOptions,
            [KeogramGeometryJson.CreateAuxiliaryInput(geometry)]);
        var parts = new List<ProcessingArtifact>();
        var axes = new List<KeogramSegmentAxisV1>();
        foreach (var chunk in new[] { frames[..2], frames[2..] })
        {
            var product = await StillWindow(chunk, BuiltInProcessingRecipes.Keogram, natural,
                [KeogramGeometryJson.CreateAuxiliaryInput(geometry)]);
            var part = AsArtifact(product) with
            {
                CreatedUtc = chunk[0].ObservationStartedUtc!.Value,
                ObservationStartedUtc = chunk[0].ObservationStartedUtc,
                ObservationEndedUtc = chunk[^1].ObservationEndedUtc
            };
            parts.Add(part);
            axes.Add(KeogramSegmentAxesJson.CreateSegment(part.ArtifactId,
                chunk.Select(f => f.ObservationStartedUtc!.Value).ToArray(), natural));
        }
        var result = await StillWindow([parts[1], parts[0]], BuiltInProcessingRecipes.KeogramAssembly, finalOptions,
            [KeogramGeometryJson.CreateAuxiliaryInput(geometry), KeogramSegmentAxesJson.CreateAuxiliaryInput(
                new KeogramSegmentAxesV1(KeogramSegmentAxesV1.CurrentSchemaVersion, axes))]);
        Assert.AreEqual(direct.Layout, result.Layout);
        CollectionAssert.AreEqual(direct.Payload.ToArray(), result.Payload.ToArray());
        CollectionAssert.AreEqual(parts.Select(p => p.ArtifactId).ToArray(), result.SourceArtifactIds.ToArray());
        Assert.AreEqual(direct.TotalIntegration, result.TotalIntegration);
    }

    [TestMethod]
    [DataRow(false, CameraPixelFormat.Mono8)]
    [DataRow(true, CameraPixelFormat.Mono8)]
    [DataRow(false, CameraPixelFormat.Rgb24)]
    [DataRow(true, CameraPixelFormat.Rgb24)]
    public async Task EqualTimeSegmentAssemblyUsesOriginalSourceOrderAcrossHosts(bool planned, CameraPixelFormat format)
    {
        var seed = StillFrames(format)[0];
        var frames = Enumerable.Range(1, 4).Select(i => seed with
        {
            ArtifactId = Guid.Parse($"40000000-0000-0000-0000-{i:D12}"),
            Payload = Enumerable.Repeat((byte)(i * 10), seed.Payload.Length).ToArray()
        }).ToArray();
        var geometry = StillGeometry(seed);
        var natural = StillOptions(seed.ObservationStartedUtc!.Value, false);
        var final = StillOptions(seed.ObservationStartedUtc!.Value, planned);
        var direct = await StillWindow([frames[3], frames[1], frames[0], frames[2]], BuiltInProcessingRecipes.Keogram,
            final, [KeogramGeometryJson.CreateAuxiliaryInput(geometry)]);
        var parts = new List<ProcessingArtifact>();
        var axes = new List<KeogramSegmentAxisV1>();
        foreach (var chunk in new[] { new[] { frames[0], frames[2] }, new[] { frames[1], frames[3] } })
        {
            var result = await StillWindow(chunk, BuiltInProcessingRecipes.Keogram, natural,
                [KeogramGeometryJson.CreateAuxiliaryInput(geometry)]);
            var part = AsArtifact(result) with
            {
                ArtifactId = Guid.Parse($"50000000-0000-0000-0000-{9 - parts.Count:D12}"),
                ObservationStartedUtc = seed.ObservationStartedUtc,
                SourceArtifactIds = result.SourceArtifactIds
            };
            parts.Add(part);
            axes.Add(KeogramSegmentAxesJson.CreateSegment(part.ArtifactId,
                chunk.Select(f => f.ObservationStartedUtc!.Value).ToArray(), natural));
        }
        var assembled = await StillWindow([parts[1], parts[0]], BuiltInProcessingRecipes.KeogramAssembly, final,
            [KeogramGeometryJson.CreateAuxiliaryInput(geometry), KeogramSegmentAxesJson.CreateAuxiliaryInput(
                new KeogramSegmentAxesV1(KeogramSegmentAxesV1.CurrentSchemaVersion, axes))]);
        Assert.AreEqual(direct.Layout, assembled.Layout);
        CollectionAssert.AreEqual(direct.Payload.ToArray(), assembled.Payload.ToArray(),
            "Equal-time source identity order must survive interleaved segment membership and reversed segment identities.");
        Assert.AreEqual(direct.TotalIntegration, assembled.TotalIntegration);
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono8)]
    [DataRow(CameraPixelFormat.Rgb24)]
    public async Task WindowTrailLightensActualSourcesAndRetainsLineageAcrossHosts(CameraPixelFormat format)
    {
        var frames = StillFrames(format);
        var result = await StillWindow([frames[2], frames[0], frames[1]], BuiltInProcessingRecipes.StarTrail,
            new StarTrailRecipeOptions(), []);
        Assert.IsTrue(result.Payload.ToArray().All(value => value == 30));
        Assert.AreEqual(frames[0].Layout! with { BlackLevel = null }, result.Layout,
            "A derivative declares canonical packed display metadata rather than source sensor levels.");
        CollectionAssert.AreEqual(frames.Select(f => f.ArtifactId).ToArray(), result.SourceArtifactIds.ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(3), result.TotalIntegration);
    }

    private static async Task<ProcessingProduct> StillWindow<T>(IReadOnlyList<ProcessingArtifact> frames,
        string recipe, T options, IReadOnlyList<ProcessingAuxiliaryInput> auxiliary)
    {
        var recording = new RecordingExecutor();
        var first = frames[0];
        var outcome = await new LogicHostRecipeExecutionAdapter(recording).ExecuteAsync(
            frames.Select(f => new LogicHostProcessingInput(null, f.Payload, Artifact: f,
                BindingKind: ProcessingGraphInputBindingKind.PrimaryArtifact)).ToArray(), recipe,
            JsonSerializer.SerializeToElement(options),
            ProcessingInputSelector.RecipeResult(first.Role, first.Variant, first.RecipeIdentitySha256),
            "contract-window", auxiliaryInputs: auxiliary);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var result = Assert.ContainsSingle(outcome.Products);
        AssertContract(recording.Request!, result);
#if COMBINED_TESTS
        var edge = await new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor())
            .ExecuteAsync(recording.Request!, CancellationToken.None);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, edge.Status, edge.ReasonCode);
        var product = Assert.ContainsSingle(edge.Products);
        CollectionAssert.AreEqual(result.Payload.ToArray(), product.Payload.ToArray());
        Assert.AreEqual(result.OutputIdentitySha256, product.OutputIdentitySha256);
        CollectionAssert.AreEqual(result.SourceArtifactIds.ToArray(), product.SourceArtifactIds.ToArray());
#endif
        return result;
    }

    private static ProcessingArtifact[] StillFrames(CameraPixelFormat format = CameraPixelFormat.Mono8)
    {
        var source = Source(format) with { Role = FrameArtifactRole.Preview, Variant = "fixed-preview" };
        var origin = DateTimeOffset.UnixEpoch;
        return Enumerable.Range(0, 3).Select(i => source with
        {
            ArtifactId = Guid.NewGuid(),
            Payload = Enumerable.Repeat((byte)((i + 1) * 10), source.Payload.Length).ToArray(),
            CreatedUtc = origin.AddMinutes(i == 2 ? 5 : i),
            Integration = TimeSpan.FromSeconds(1),
            ObservationStartedUtc = origin.AddMinutes(i == 2 ? 5 : i),
            ObservationEndedUtc = origin.AddMinutes(i == 2 ? 5 : i).AddSeconds(1)
        }).ToArray();
    }

    private static KeogramRecipeOptions StillOptions(DateTimeOffset origin, bool planned) => new(
        MaximumGapSeconds: 90, PlannedAxis: planned
            ? new PlannedKeogramAxis(origin.AddMinutes(-1), origin.AddMinutes(8), TimeSpan.FromMinutes(1)) : null);

    private static KeogramGeometryV1 StillGeometry(ProcessingArtifact source) => new(
        KeogramGeometryV1.CurrentSchemaVersion, source.Compatibility.Rig,
        new ProjectionContext(ProjectionModel.EquidistantFisheye, 4, 4, 3.9 * 2 / Math.PI, 3.9 * 2 / Math.PI,
            8, 8, ProjectionAperture.Circular, 3.9), 9);
}
