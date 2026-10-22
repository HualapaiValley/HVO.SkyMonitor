using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task ConstellationEdit_ActivateCaptureAndRollbackPreserveImmutableRevisionOutput()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-constellation-options");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateProjectedSceneConfiguration();
            config = config with
            {
                Rig = config.Rig with { Sensor = config.Rig.Sensor with { WidthPixels = 64, HeightPixels = 64 } },
                Pipeline = new CapturePipelineConfig([
                    new CaptureProcessingStepConfig("ProjectedScene", "projected-scene", 10, DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig("ScenePresentationLayer", "overlay", 20,
                        JsonSerializer.SerializeToElement(new { future = new { keep = 17 } }), ["projected-scene"])
                ])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var original = await CaptureConstellationsAsync(provider, config, 0).ConfigureAwait(false);
            var originalPixels = RenderConstellations(original.Payload);
            Assert.AreEqual(initial.ActiveRevisionId, original.RevisionId);
            Assert.IsNotEmpty(original.Payload.Segments);
            Assert.IsTrue(original.Payload.Segments.All(static segment => segment.Color == new PresentationColor(96, 160, 255) && segment.Thickness == 2));

            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "sky", "2");
            var form = model.Nodes[1].OptionsForm!;
            form.Set("constellationLineColor", "#ef3210");
            form.Set("constellationLineThickness", "5");
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            var draft = await operations.CreateRevisionAsync("sky", "2", pipeline, "create", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "too-soon", "owner-test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var validated = await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Validated, validated.Lifecycle);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await CaptureConstellationsAsync(provider, config, 1).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, changed.RevisionId);
            Assert.IsTrue(changed.Payload.Segments.All(static segment => segment.Color == new PresentationColor(239, 50, 16) && segment.Thickness == 5));
            var changedPixels = RenderConstellations(changed.Payload);
            Assert.AreNotEqual(Convert.ToHexString(SHA256.HashData(originalPixels)), Convert.ToHexString(SHA256.HashData(changedPixels)));
            // Raster coverage antialiases channel values; the brightest pixel must retain the chosen RGB ratio.
            var brightest = Enumerable.Range(0, changedPixels.Length / 3).MaxBy(index => changedPixels[index * 3]);
            var coverage = changedPixels[brightest * 3] / 239.0;
            Assert.IsGreaterThan(0.5, coverage);
            Assert.IsLessThanOrEqualTo(2, Math.Abs(changedPixels[brightest * 3 + 1] - 50 * coverage));
            Assert.IsLessThanOrEqualTo(2, Math.Abs(changedPixels[brightest * 3 + 2] - 16 * coverage));
            Assert.IsGreaterThan(Enumerable.Range(0, originalPixels.Length / 3).Count(index => originalPixels[index * 3] > 0),
                Enumerable.Range(0, changedPixels.Length / 3).Count(index => changedPixels[index * 3] > 0));

            var retainedPipeline = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(config.Pipeline.Steps[1].Options!.Value.GetRawText(), retainedPipeline!.Steps[1].Options!.Value.GetRawText());
            var retained = await ReadConstellationPayloadAsync(provider, original.ArtifactId).ConfigureAwait(false);
            CollectionAssert.AreEqual(original.Bytes, retained.Bytes);
            var rolledBack = await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, rolledBack.ActiveRevisionId);
            var reproduced = await CaptureConstellationsAsync(provider, config, 2).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, reproduced.RevisionId);
            CollectionAssert.AreEqual(originalPixels, RenderConstellations(reproduced.Payload));
            Console.WriteLine($"Original revision={original.RevisionId}; new revision={changed.RevisionId}; rollback revision={reproduced.RevisionId}");
            Console.WriteLine($"Retained payload bytes={original.Bytes.Length}; SHA256={Convert.ToHexString(SHA256.HashData(original.Bytes))}");
            Console.WriteLine($"Original/rollback raster SHA256={Convert.ToHexString(SHA256.HashData(originalPixels))}; edited raster SHA256={Convert.ToHexString(SHA256.HashData(changedPixels))}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record ConstellationCapture(string RevisionId, Guid ArtifactId, PresentationLayerPayloadV1 Payload, byte[] Bytes);

    private static async Task<ConstellationCapture> CaptureConstellationsAsync(ServiceProvider provider, CameraModuleConfig config, int sequence)
    {
        var utc = DateTimeOffset.UnixEpoch;
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var topology = new InMemoryConstellationTopology([new ConstellationSegment("TST", "1", "2")],
            new ConstellationTopologyMetadata("fixture", "1", new Uri("https://example.test/topology"), new string('D', 64), "test", "v1"));
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new("center", "Center", siderealHours, 0, 1, HipparcosId: "1"),
            new("near", "Near", (siderealHours + 1) % 24, 10, 1, HipparcosId: "2")
        ]), topology).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new EquidistantProjectionContext(32, 32, 30, 30, WidthPixels: 64, HeightPixels: 64),
            new CatalogQuery(6, 10),
            new CatalogMetadata("test", "1", new Uri("https://example.test/catalog"), new string('0', 64), "test", "1"),
            projectionVersion: "projection-v1", algorithmVersion: "astronomy-v1", constellationIds: ["TST"])).ConfigureAwait(false);
        var stageKey = (sequence + 1).ToString(CultureInfo.InvariantCulture).PadLeft(64, '0');
        var sceneId = (sequence + 4).ToString(CultureInfo.InvariantCulture).PadLeft(64, '0');
        await provider.GetRequiredService<IProjectedSceneStagingStore>().StageAsync(stageKey, sceneId, visible, CancellationToken.None).ConfigureAwait(false);
        var submission = WithSceneProvenance(CreateSubmission(sequence, CameraPixelFormat.Mono16), sceneId, stageKey);
        var frame = submission.Result.Frame!;
        submission = submission with
        {
            Result = submission.Result with
            {
                Frame = frame with
                {
                    Width = 64,
                    Height = 64,
                    PixelData = new byte[64 * 64 * 2],
                    Metadata = frame.Metadata with { Scene = frame.Metadata.Scene! with { RigProfileHashSha256 = RigProjectionContextFactory.CreateProfileHashSha256(config.Rig) } }
                }
            }
        };
        var receipt = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        await CompleteLiveAsync(provider, config).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var executionId = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
        var detail = await operations.ReadExecutionDetailAsync(executionId!.Value, CancellationToken.None).ConfigureAwait(false);
        var output = detail!.Nodes.Single(static node => node.NodeId == "overlay").Outputs.Single(static output => output.Variant == "scene-constellation-layer-v1");
        var result = await ReadConstellationPayloadAsync(provider, output.ArtifactId).ConfigureAwait(false);
        return new(detail.Execution.GraphRevisionId, output.ArtifactId, result.Payload, result.Bytes);
    }

    private static async Task<(PresentationLayerPayloadV1 Payload, byte[] Bytes)> ReadConstellationPayloadAsync(ServiceProvider provider, Guid artifactId)
    {
        var opened = await provider.GetRequiredService<ICameraAgentArtifactService>().OpenContentAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, opened.Status);
        using var stream = opened.Content!;
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes).ConfigureAwait(false);
        var payload = bytes.ToArray();
        Assert.AreEqual(stream.ByteLength, payload.LongLength);
        Assert.AreEqual(stream.ChecksumSha256, Convert.ToHexString(SHA256.HashData(payload)), ignoreCase: true);
        var parsed = PresentationLayerPayloadJson.Parse(payload);
        Assert.IsNotNull(parsed.Payload, parsed.ErrorPath);
        return (parsed.Payload, payload);
    }

    private static byte[] RenderConstellations(PresentationLayerPayloadV1 payload)
        => PresentationLayerCompositor.Composite(new ImageLayout(64, 64, CameraPixelFormat.Rgb24, 64 * 3),
            new byte[64 * 64 * 3], [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
}
