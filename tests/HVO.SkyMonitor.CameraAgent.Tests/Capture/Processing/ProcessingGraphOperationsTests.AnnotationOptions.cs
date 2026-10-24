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
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private const string AnnotationStyleSource = """{ "DrawImageCircle":true, "DrawCardinalDirections":true, "DrawMetadataCorners":true, "ExpectedPositionDiagnostics":true, "RequireProjectedSceneDependency":true, "RecipeVersion":"retained-annotation", "OutputVariant":"retained-output", "TopLeftTokens":["agent-identity","catalog"], "TopRightTokens":[], "BottomLeftTokens":[], "BottomRightTokens":[], "EnvironmentalKinds":[], "ConstellationIds":["TST"], "ConstellationLineRed":1, "ConstellationLineGreen":2, "ConstellationLineBlue":3, "future":[1e+02] }""";

    [TestMethod]
    public void AnnotationDescriptorMatchesStepDefaultsBoundsAndRegisteredPlanWithoutRewritingSource()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-annotation-defaults");
        try
        {
            using var provider = CreateProvider(root);
            var config = AnnotationStyleConfiguration();
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var plan = factory.CreateGraph(config);
            try
            {
                var node = plan.SharedPlan!.Nodes.Single(static node => node.Definition.StepAlias == "Annotation");
                Assert.AreEqual(AnnotationOptionDescriptors.Descriptor.StepVersion, node.Definition.StepVersion);
            }
            finally { plan.DisposeSteps(); }
            var descriptor = AnnotationOptionDescriptors.Descriptor;
            var runtimeDefaults = new AnnotationProcessingStepOptions();
            var runtime = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(runtimeDefaults));
            var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { }));
            foreach (var option in defaults.Options)
            {
                Assert.AreEqual(option.Value, runtime.Get(option.Descriptor.Key).Value, option.Descriptor.Key);
                if (option.Descriptor.Kind == ProcessingOptionKind.Toggle) continue;
                foreach (var member in option.Descriptor.Members)
                {
                    var property = typeof(AnnotationProcessingStepOptions).GetProperties().Single(property => string.Equals(property.Name, member, StringComparison.OrdinalIgnoreCase));
                    var range = property.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RangeAttribute), false)
                        .Cast<System.ComponentModel.DataAnnotations.RangeAttribute>().Single();
                    Assert.AreEqual(Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture),
                        option.Descriptor.Kind == ProcessingOptionKind.Color ? 0 : option.Descriptor.MinimumRealNumber ?? option.Descriptor.MinimumWholeNumber!.Value, property.Name);
                    Assert.AreEqual(Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture),
                        option.Descriptor.Kind == ProcessingOptionKind.Color ? 255 : option.Descriptor.MaximumRealNumber ?? option.Descriptor.MaximumWholeNumber!.Value, property.Name);
                }
            }
            var before = factory.PreviewPlan(config);
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, "source", "annotation", "2");
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            Assert.AreEqual(config.Pipeline.Steps[2].Options!.Value.GetRawText(), pipeline.Steps[2].Options!.Value.GetRawText());
            Assert.AreEqual(before.EffectiveSha256, factory.PreviewPlan(config with { Pipeline = pipeline }).EffectiveSha256);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("markRadius", "12")]
    [DataRow("markerValue", "32")]
    [DataRow("drawLabels", "False")]
    [DataRow("labelScale", "2")]
    [DataRow("maximumLabelMagnitude", "0")]
    [DataRow("drawConstellationLines", "False")]
    [DataRow("constellationLineValue", "32")]
    [DataRow("constellationLineColor", "#ef3210")]
    [DataRow("constellationLineThickness", "4")]
    [DataRow("constellationLineOpacity", "0.2")]
    [DataRow("drawImageCircle", "False")]
    [DataRow("imageCircleValue", "32")]
    [DataRow("drawCardinalDirections", "False")]
    [DataRow("cardinalValue", "32")]
    [DataRow("cardinalScale", "3")]
    [DataRow("drawMetadataCorners", "False")]
    [DataRow("metadataValue", "32")]
    [DataRow("metadataScale", "2")]
    [DataRow("metadataInset", "12")]
    [DataRow("metadataLineSpacing", "8")]
    public async Task AnnotationScalarActivationChangesPixelsAndRollbackReconstructsOldRevision(string key, string value)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-annotation-style");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = AnnotationStyleConfiguration();
            if (key == "constellationLineColor")
                config = config with { Rig = config.Rig with { Sensor = config.Rig.Sensor with { PixelFormat = CameraPixelFormat.Rgb24, ColorMode = SensorColorMode.Color } } };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var storedSource = (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!.Steps[2].Options!.Value.GetRawText();
            var original = await CaptureAnnotationStyleAsync(provider, config, 0).ConfigureAwait(false);
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "annotation", "2");
            model.Nodes[2].OptionsForm!.Set(key, value);
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            var draft = await operations.CreateRevisionAsync("annotation", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await CaptureAnnotationStyleAsync(provider, config, 1).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, changed.Detail.Execution.GraphRevisionId);
            Assert.AreNotEqual(Convert.ToHexString(SHA256.HashData(original.Pixels)), Convert.ToHexString(SHA256.HashData(changed.Pixels)), key);
            var retained = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(storedSource, retained!.Steps[2].Options!.Value.GetRawText());
            CollectionAssert.AreEqual(original.Pixels, await ReadAnnotationPixelsAsync(provider, original.Detail).ConfigureAwait(false));
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            var restored = await CaptureAnnotationStyleAsync(provider, config, 2).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, restored.Detail.Execution.GraphRevisionId);
            CollectionAssert.AreEqual(original.Pixels, restored.Pixels, key);
            if (key == "markRadius")
            {
                var replay = await ExecuteReplayAsync(provider, new ProcessingReplaySubmission(
                    original.CaptureId, initial.ActiveRevisionId, original.RawArtifactId), "old-annotation-replay").ConfigureAwait(false);
                CollectionAssert.AreEqual(original.Pixels, await ReadAnnotationPixelsAsync(provider, replay).ConfigureAwait(false));
                var before = original.Detail.Nodes.Single(static node => node.NodeId == "annotation").Outputs.Single();
                var reconstructed = replay.Nodes.Single(static node => node.NodeId == "annotation").Outputs.Single();
                Assert.AreEqual(before.ArtifactId, reconstructed.ArtifactId, "Replaying the same retained inputs and revision keeps the output identity.");
            }
            Console.WriteLine($"{key}: original/rollback SHA256={Convert.ToHexString(SHA256.HashData(original.Pixels))}; edited SHA256={Convert.ToHexString(SHA256.HashData(changed.Pixels))}; old revision={initial.ActiveRevisionId}; edited revision={draft.RevisionId}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AnnotationServerRejectsInvalidStylingWithoutCreatingOrActivatingADraft()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-annotation-invalid");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = AnnotationStyleConfiguration();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            foreach (var json in new[] { "{\"markRadius\":33}", "{\"markerValue\":256}", "{\"labelScale\":0}", "{\"metadataScale\":5}", "{\"constellationLineThickness\":9}" })
            {
                var options = JsonDocument.Parse(AnnotationStyleSource[..^1] + "," + json[1..]).RootElement.Clone();
                var invalid = config.Pipeline with { Steps = config.Pipeline.Steps.Select(step => step.Id == "annotation" ? step with { Options = options } : step).ToArray() };
                var expected = json.Contains("markerValue", StringComparison.Ordinal) ? typeof(JsonException) : typeof(System.ComponentModel.DataAnnotations.ValidationException);
                var previewError = Assert.Throws<Exception>(() => provider.GetRequiredService<ICaptureProcessingPipelineFactory>().PreviewPlan(config with { Pipeline = invalid }));
                Assert.IsInstanceOfType(previewError, expected);
                var saveError = await Assert.ThrowsAsync<Exception>(async () => await operations.CreateRevisionAsync("invalid", "2", invalid, "invalid", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.IsInstanceOfType(saveError, expected);
            }
            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, registry.ActiveRevisionId);
            Assert.HasCount(initial.Revisions.Count, registry.Revisions);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static CameraModuleConfig AnnotationStyleConfiguration()
    {
        var config = CreateProjectedSceneConfiguration();
        return config with
        {
            Rig = config.Rig with { Sensor = config.Rig.Sensor with { WidthPixels = 256, HeightPixels = 256 } },
            Pipeline = new CapturePipelineConfig([
                new("ProjectedScene", "projected-scene", 10, DependsOn: ["$raw"]),
                new("Preview", "preview", 20, JsonSerializer.SerializeToElement(new { outputEncoding = "Packed" }), ["$raw"]),
                new("Annotation", "annotation", 30, JsonDocument.Parse(AnnotationStyleSource).RootElement.Clone(), ["preview", "projected-scene"])
            ])
        };
    }

    private static async Task<(ProcessingGraphExecutionDetail Detail, byte[] Pixels, Guid CaptureId, Guid RawArtifactId)> CaptureAnnotationStyleAsync(ServiceProvider provider, CameraModuleConfig config, int sequence)
    {
        var utc = DateTimeOffset.UnixEpoch;
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var topology = new InMemoryConstellationTopology([new("TST", "1", "2")],
            new("fixture", "1", new Uri("https://example.test/topology"), new string('D', 64), "test", "v1"));
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new("center", "Center", siderealHours, 0, 1, HipparcosId: "1"),
            new("near", "Near", (siderealHours + 1) % 24, 10, 1, HipparcosId: "2")
        ]), topology).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new EquidistantProjectionContext(128, 128, 126, 126, WidthPixels: 256, HeightPixels: 256),
            new CatalogQuery(6, 10),
            new("test", "1", new Uri("https://example.test/catalog"), new string('0', 64), "test", "1"),
            projectionVersion: "projection-v1", algorithmVersion: "astronomy-v1", constellationIds: ["TST"])).ConfigureAwait(false);
        var stageKey = (sequence + 1).ToString(CultureInfo.InvariantCulture).PadLeft(64, '0');
        var sceneId = (sequence + 4).ToString(CultureInfo.InvariantCulture).PadLeft(64, '0');
        await provider.GetRequiredService<IProjectedSceneStagingStore>().StageAsync(stageKey, sceneId, visible, CancellationToken.None).ConfigureAwait(false);
        var format = config.Rig.Sensor.PixelFormat;
        var submission = WithSceneProvenance(CreateSubmission(sequence, format), sceneId, stageKey);
        var frame = submission.Result.Frame!;
        submission = submission with
        {
            Result = submission.Result with
            {
                Frame = frame with
                {
                    Width = 256,
                    Height = 256,
                    PixelData = new byte[256 * 256 * (format == CameraPixelFormat.Rgb24 ? 3 : 2)],
                    Metadata = frame.Metadata with { Scene = frame.Metadata.Scene! with { RigProfileHashSha256 = RigProjectionContextFactory.CreateProfileHashSha256(config.Rig) } }
                }
            }
        };
        var receipt = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        await CompleteLiveAsync(provider, config).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
        var detail = (await operations.ReadExecutionDetailAsync(id!.Value, CancellationToken.None).ConfigureAwait(false))!;
        return (detail, await ReadAnnotationPixelsAsync(provider, detail).ConfigureAwait(false), receipt.Manifest.Descriptor.Capture.CaptureId, receipt.Manifest.Descriptor.Artifact.ArtifactId);
    }

    private static async Task<byte[]> ReadAnnotationPixelsAsync(ServiceProvider provider, ProcessingGraphExecutionDetail detail)
    {
        var output = detail.Nodes.Single(static node => node.NodeId == "annotation").Outputs.Single();
        Assert.AreEqual("retained-output", output.Variant);
        var opened = await provider.GetRequiredService<ICameraAgentArtifactService>().OpenContentAsync(output.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, opened.Status);
        using var stream = opened.Content!;
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes).ConfigureAwait(false);
        var pixels = bytes.ToArray();
        Assert.AreEqual(stream.ByteLength, pixels.LongLength);
        Assert.AreEqual(stream.ChecksumSha256, Convert.ToHexString(SHA256.HashData(pixels)), ignoreCase: true);
        return pixels;
    }
}
