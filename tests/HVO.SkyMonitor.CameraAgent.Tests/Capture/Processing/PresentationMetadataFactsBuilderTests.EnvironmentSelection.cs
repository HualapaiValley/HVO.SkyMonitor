using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class PresentationMetadataFactsBuilderTests
{
    private static readonly JsonSerializerOptions EnvironmentSelectionJson = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions EnvironmentReceiptJson = new() { WriteIndented = true };
    private static readonly string[] MissingEnvironmentKinds = ["SkyQuality"];

    public Microsoft.VisualStudio.TestTools.UnitTesting.TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task EnvironmentSelectionDraftActivationAndRollbackReachRetainedProducerAndSharedReconstruction()
    {
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        var temperature = Fact(Guid.Parse("16630000-0000-0000-0000-000000000001"), EnvironmentalObservationKind.AirTemperature, 12);
        var humidity = Fact(Guid.Parse("16630000-0000-0000-0000-000000000002"), EnvironmentalObservationKind.RelativeHumidity, 45, staleAfter: Epoch.AddSeconds(1));
        await CommitAsync(store, temperature).ConfigureAwait(false);
        await CommitAsync(store, humidity).ConfigureAwait(false);
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        var sceneBytes = HVO.SkyMonitor.Astronomy.ProjectedSceneJson.Serialize(fixture.Scene);
        fixture.Context.BeginNode("scene", []);
        fixture.Context.RegisterProcessingProduct(fixture.Stack with
        {
            Role = FrameArtifactRole.Metadata,
            Variant = "scene",
            Payload = sceneBytes,
            OutputIdentitySha256 = new string('E', 64),
            Kind = ProcessingProductKind.Metadata,
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(sceneBytes),
            SchemaVersion = fixture.Scene.SchemaVersion,
            ContentIdentitySha256 = fixture.Scene.SceneIdentitySha256
        });
        fixture.Context.BeginNode("preview", []);
        fixture.Context.RegisterProcessingProduct(fixture.Stack with { Role = FrameArtifactRole.Preview });
        using var provider = ProcessingGraphOperationsTests.CreateProvider(_root!);
        await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var config = ProcessingGraphOperationsTests.CreateProjectedSceneConfiguration() with
        {
            Pipeline = new CapturePipelineConfig([
                new("Calibration", "calibration", 10, DependsOn: ["$raw"]),
                new("RollingCombination", "rolling", 20, DependsOn: ["calibration"]),
                new("CombinedPreview", "preview", 30, DependsOn: ["rolling"]),
                new("ProjectedScene", "scene", 40, DependsOn: ["$raw"]),
                new("EnvironmentPresentationLayer", "environment", 50,
                    JsonSerializer.Deserialize<JsonElement>("""{ "WidthPixels":1936,"HeightPixels":1216,"future":1e+02 }"""), ["scene", "preview"])
            ])
        };
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
        var originalSource = config.Pipeline.Steps[^1].Options!.Value.GetRawText();
        var original = await ExecuteActive("default").ConfigureAwait(false);
        Assert.HasCount(6, original.Facts.Environment);
        Assert.AreEqual("Stale", original.Facts.Environment.Single(static fact => fact.Kind == "RelativeHumidity").Status);
        var explicitDefault = JsonSerializer.SerializeToElement(new
        {
            widthPixels = 1936,
            heightPixels = 1216,
            environmentalKinds = new EnvironmentPresentationLayerProcessingStepOptions().EnvironmentalKinds.Select(static kind => kind.ToString()).ToArray()
        });
        var selectedDefault = await Execute(explicitDefault, "explicit-default").ConfigureAwait(false);
        Assert.AreEqual(original.Product.OutputIdentitySha256, selectedDefault.Product.OutputIdentitySha256);
        var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "environment", "2");
        var form = model.Nodes[^1].OptionsForm!;
        while (form.Collection("environmentalKinds").Items.Length > 1) form.RemoveCollectionItem("environmentalKinds", 1);
        Assert.IsTrue(model.TryBuild(out var edited, out _));
        var draft = await operations.CreateRevisionAsync("environment", "2", edited, "draft", "test", null, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
            await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
            await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion + 99, "stale", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
        var subset = await ExecuteActive("subset").ConfigureAwait(false);
        Assert.AreEqual("AirTemperature", subset.Facts.Environment.Single().Kind);
        Assert.AreEqual(temperature.ObservationId, subset.Facts.Environment.Single().ObservationId);
        Assert.AreNotEqual(original.Facts.FactsIdentitySha256, subset.Facts.FactsIdentitySha256);
        Assert.AreNotEqual(original.Facts.Environment[0].AssociationIdentitySha256, subset.Facts.Environment[0].AssociationIdentitySha256);
        Assert.IsFalse(subset.Inputs.Any(static input => input.Name!.Contains("RelativeHumidity", StringComparison.Ordinal)));
        Assert.IsTrue(subset.Inputs.Any(static input => input.Name == "environment-observation-AirTemperature"));
        Assert.AreEqual(subset.Facts.Environment[0].ObservationContentSha256,
            subset.Inputs.Single(static input => input.Name == "environment-observation-AirTemperature").IdentitySha256);
        Assert.AreEqual(subset.Facts.Environment[0].AssociationIdentitySha256,
            subset.Inputs.Single(static input => input.Name == "environment-association-AirTemperature").IdentitySha256);
        CollectionAssert.AreEqual(original.Product.SourceArtifactIds.ToArray(), subset.Product.SourceArtifactIds.ToArray());
        Assert.IsFalse(subset.Layer.TextBlocks.SelectMany(static block => block.Lines).Any(static line => line.Contains("HUMIDITY", StringComparison.Ordinal)));
        Assert.AreEqual(JsonSerializer.Serialize(original.Layer.TextBlocks.Where(static block => block.Anchor != PresentationTextAnchor.BottomLeft)),
            JsonSerializer.Serialize(subset.Layer.TextBlocks.Where(static block => block.Anchor != PresentationTextAnchor.BottomLeft)));
        form.RemoveCollectionItem("environmentalKinds", 0);
        Assert.IsTrue(model.TryBuild(out var emptyPipeline, out _));
        var emptyDraft = await operations.CreateRevisionAsync("environment", "3", emptyPipeline, "empty", "test", null, CancellationToken.None).ConfigureAwait(false);
        await operations.ValidateRevisionAsync(emptyDraft.RevisionId, "validate-empty", "test", null, CancellationToken.None).ConfigureAwait(false);
        var emptyActive = await operations.ActivateRevisionAsync(emptyDraft.RevisionId, active.StateVersion, "activate-empty", "test", null, CancellationToken.None).ConfigureAwait(false);
        var empty = await ExecuteActive("empty").ConfigureAwait(false);
        Assert.IsEmpty(empty.Facts.Environment);
        Assert.IsFalse(empty.Inputs.Any(static input => input.Name!.StartsWith("environment-", StringComparison.Ordinal)));
        Assert.IsTrue(empty.Layer.TextBlocks.SelectMany(static block => block.Lines).Contains("NOT RETAINED"));
        await operations.RollbackRevisionAsync(initial.ActiveRevisionId, emptyActive.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
        var restored = await ExecuteActive("rollback").ConfigureAwait(false);
        Assert.AreEqual(original.Product.OutputIdentitySha256, restored.Product.OutputIdentitySha256);
        CollectionAssert.AreEqual(original.Product.Payload.ToArray(), restored.Product.Payload.ToArray());
        Assert.AreEqual(originalSource, (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!.Steps[^1].Options!.Value.GetRawText());
        // A separate missing kind is retained as missing; selection cannot acquire an observation.
        var unavailableSource = JsonSerializer.SerializeToElement(new { widthPixels = 1936, heightPixels = 1216, environmentalKinds = MissingEnvironmentKinds });
        var unavailable = await Execute(unavailableSource, "unavailable").ConfigureAwait(false);
        Assert.AreEqual("Missing", unavailable.Facts.Environment.Single().Status);
        Assert.IsNull(unavailable.Facts.Environment.Single().ObservationId);
        var denseSource = JsonSerializer.SerializeToElement(new { widthPixels = 1936, heightPixels = 1216, environmentalKinds = Enum.GetNames<EnvironmentalObservationKind>() });
        var dense = await Execute(denseSource, "all-kinds").ConfigureAwait(false);
        Assert.HasCount(12, dense.Facts.Environment);
        Assert.HasCount(PresentationLayerPayloadV1.MaximumLinesPerBlock, dense.Facts.Corners.BottomLeft);
        Assert.IsTrue(dense.Facts.Corners.BottomLeft.All(static line => line.Length <= PresentationLayerPayloadV1.MaximumLineCharacters));

        async Task<(ProcessingProduct Product, PresentationMetadataFactsProductV1 Facts, PresentationLayerPayloadV1 Layer, IReadOnlyList<DurableProcessingNodeInput> Inputs)> ExecuteActive(string name)
        {
            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            var pipeline = await operations.ReadRevisionPipelineAsync(registry.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            return await Execute(pipeline!.Steps[^1].Options!.Value, name).ConfigureAwait(false);
        }

        async Task<(ProcessingProduct Product, PresentationMetadataFactsProductV1 Facts, PresentationLayerPayloadV1 Layer, IReadOnlyList<DurableProcessingNodeInput> Inputs)> Execute(JsonElement source, string name)
        {
            var options = source.Deserialize<EnvironmentPresentationLayerProcessingStepOptions>(EnvironmentSelectionJson)!;
            await fixture.Associations.AssociateAsync(fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
                fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
                fixture.Descriptor.Capture.RigId, options.EnvironmentalKinds, CancellationToken.None).ConfigureAwait(false);
            fixture.Context.BeginNode("environment", ["scene", "preview"]);
            await new EnvironmentPresentationLayerCaptureProcessingStep(new("environment", "EnvironmentPresentationLayer", 50), options, fixture.Builder)
                .ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);
            var outcome = fixture.Context.ProcessingOutcomes[^1];
            var product = outcome.Products.Single(product => product.Variant == options.OutputVariant);
            var facts = PresentationProcessingProducts.ParseMetadataFacts(outcome.Products.Single(product => product.Variant == options.FactsOutputVariant).Payload);
            var layer = PresentationLayerPayloadJson.Parse(product.Payload).Payload!;
            var reconstructed = PresentationLayerProducers.FromMetadataFacts(facts.Corners, options.WidthPixels, options.HeightPixels,
                scale: options.Scale, inset: options.Inset, lineSpacing: options.LineSpacing);
            CollectionAssert.AreEqual(product.Payload.ToArray(), PresentationLayerPayloadJson.Serialize(reconstructed));
            var path = Path.Combine(TestContext.TestResultsDirectory!, "environment-" + name + ".json");
            await File.WriteAllBytesAsync(path, product.Payload.ToArray()).ConfigureAwait(false);
            TestContext.AddResultFile(path);
            var factsPath = Path.Combine(TestContext.TestResultsDirectory!, "environment-" + name + "-facts.json");
            await File.WriteAllBytesAsync(factsPath, outcome.Products.Single(product => product.Variant == options.FactsOutputVariant).Payload.ToArray()).ConfigureAwait(false);
            TestContext.AddResultFile(factsPath);
            var inputsPath = Path.Combine(TestContext.TestResultsDirectory!, "environment-" + name + "-inputs.json");
            await File.WriteAllTextAsync(inputsPath, JsonSerializer.Serialize(fixture.Context.GetCurrentInputEvidence(), EnvironmentReceiptJson)).ConfigureAwait(false);
            TestContext.AddResultFile(inputsPath);
            WriteEnvironmentRaster(name, layer);
            return (product, facts, layer, fixture.Context.GetCurrentInputEvidence());
        }
    }

    private void WriteEnvironmentRaster(string name, PresentationLayerPayloadV1 layer)
    {
        var layout = new ImageLayout(1936, 1216, CameraPixelFormat.Mono8, 1936);
        var pixels = Enumerable.Repeat((byte)12, layout.RequiredByteLength).ToArray();
        var original = ProcessingIdentity.ComputePayloadSha256(pixels);
        var raster = PresentationLayerCompositor.CompositeDisplay(layout, pixels, [new(layer, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        Assert.AreEqual(original, ProcessingIdentity.ComputePayloadSha256(pixels));
        // Pin the unchanged renderer's bounded cost on the exact acceptance inputs, not acquisition or a whole pipeline.
        if (name != "rollback")
        {
            for (var i = 0; i < 3; i++) _ = PresentationLayerCompositor.CompositeDisplay(layout, pixels, [new(layer, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
            var times = new double[15];
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < times.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                var measured = PresentationLayerCompositor.CompositeDisplay(layout, pixels, [new(layer, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
                times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(raster.Pixels), ProcessingIdentity.ComputePayloadSha256(measured.Pixels));
            }
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            process.Refresh();
            var receipt = new
            {
                State = name,
                Width = layout.Width,
                Height = layout.Height,
                SourceSha256 = original,
                PayloadSha256 = ProcessingIdentity.ComputePayloadSha256(PresentationLayerPayloadJson.Serialize(layer)),
                OutputSha256 = ProcessingIdentity.ComputePayloadSha256(raster.Pixels),
                Warmup = 3,
                Iterations = times.Length,
                MedianMs = times.Order().ElementAt(times.Length / 2),
                P95Ms = times.Order().Last(),
                CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                AllocatedBytesPerOperation = allocatedBytes / times.Length,
                WorkingSetBytes = process.WorkingSet64,
                TimesMs = times
            };
            var receiptPath = Path.Combine(TestContext.TestResultsDirectory!, "environment-" + name + "-resources.json");
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt, EnvironmentReceiptJson));
            TestContext.AddResultFile(receiptPath);
        }
        using var bitmap = new SKBitmap(layout.Width, layout.Height);
        for (var y = 0; y < layout.Height; y++)
            for (var x = 0; x < layout.Width; x++)
            {
                var index = (y * layout.Width + x) * 3;
                bitmap.SetPixel(x, y, new SKColor(raster.Pixels[index], raster.Pixels[index + 1], raster.Pixels[index + 2]));
            }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(TestContext.TestResultsDirectory!, "environment-" + name + ".png");
        File.WriteAllBytes(path, encoded.ToArray());
        TestContext.AddResultFile(path);
    }
}
