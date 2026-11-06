using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class PresentationMetadataFactsBuilderTests
{
    [TestMethod]
    [DataRow("scale", "4")]
    [DataRow("inset", "64")]
    [DataRow("lineSpacing", "16")]
    [DataRow("value", "0")]
    [DataRow("value", "255")]
    public async Task RetainedEnvironmentEditsReachRealProducerAndPreserveMissingStaleFacts(string key, string value)
    {
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        await CommitAsync(store, Fact(Guid.Parse("10000000-0000-0000-0000-000000000009"),
            EnvironmentalObservationKind.RelativeHumidity, 45, staleAfter: Epoch.AddSeconds(1))).ConfigureAwait(false);
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        await fixture.Associations.AssociateAsync(fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
            fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
            fixture.Descriptor.Capture.RigId, [EnvironmentalObservationKind.RelativeHumidity, EnvironmentalObservationKind.AtmosphericPressure],
            CancellationToken.None).ConfigureAwait(false);
        var sceneBytes = ProjectedSceneJson.Serialize(fixture.Scene);
        var scene = fixture.Stack with
        {
            Role = FrameArtifactRole.Metadata,
            Variant = "scene",
            OutputIdentitySha256 = new string('E', 64),
            Payload = sceneBytes,
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(sceneBytes),
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = fixture.Scene.SchemaVersion,
            ContentIdentitySha256 = fixture.Scene.SceneIdentitySha256
        };
        fixture.Context.BeginNode("scene", []);
        fixture.Context.RegisterProcessingProduct(scene);
        fixture.Context.BeginNode("preview", []);
        fixture.Context.RegisterProcessingProduct(fixture.Stack with { Role = FrameArtifactRole.Preview });
        // Use the runtime enum serializer rather than assuming its numeric wire values.
        var optionsSource = JsonSerializer.SerializeToElement(new EnvironmentPresentationLayerProcessingStepOptions
        {
            WidthPixels = 1936,
            HeightPixels = 512,
            FactsOutputVariant = "kept-facts",
            OutputVariant = "kept-layer",
            EnvironmentalKinds = [EnvironmentalObservationKind.RelativeHumidity, EnvironmentalObservationKind.AtmosphericPressure]
        });
        var form = new ProcessingNodeOptionsForm(RetainedLayerOptionDescriptors.Environment, optionsSource);
        form.Set(key, value);
        Assert.IsTrue(form.TryBuild(out var edited, out _));
        async Task<(ProcessingProduct Product, PresentationMetadataFactsProductV1 Facts)> Execute(JsonElement json)
        {
            fixture.Context.BeginNode("environment", ["scene", "preview"]);
            var options = json.Deserialize<EnvironmentPresentationLayerProcessingStepOptions>(ProductJson)!;
            await new EnvironmentPresentationLayerCaptureProcessingStep(new("environment", "EnvironmentPresentationLayer", 70), options, fixture.Builder)
                .ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);
            var products = fixture.Context.ProcessingOutcomes[^1].Products;
            return (products.Single(static product => product.Variant == "kept-layer"),
                PresentationProcessingProducts.ParseMetadataFacts(products.Single(static product => product.Variant == "kept-facts").Payload));
        }
        var before = await Execute(optionsSource).ConfigureAwait(false);
        var after = await Execute(edited!.Value).ConfigureAwait(false);
        if (key == "value" && value == "255") Assert.AreEqual(before.Product.ChecksumSha256, after.Product.ChecksumSha256, "The existing default text is already white.");
        else Assert.AreNotEqual(before.Product.ChecksumSha256, after.Product.ChecksumSha256, key);
        Assert.AreEqual(before.Facts.FactsIdentitySha256, after.Facts.FactsIdentitySha256);
        Assert.AreEqual("Stale", after.Facts.Environment.Single(static fact => fact.Kind == "RelativeHumidity").Status);
        Assert.AreEqual("Missing", after.Facts.Environment.Single(static fact => fact.Kind == "AtmosphericPressure").Status);
        CollectionAssert.AreEqual(before.Product.SourceArtifactIds.ToArray(), after.Product.SourceArtifactIds.ToArray());
        var payload = PresentationLayerPayloadJson.Parse(after.Product.Payload).Payload!;
        Assert.AreEqual(1936, payload.WidthPixels);
        Assert.AreEqual(512, payload.HeightPixels);
        Assert.IsNotEmpty(payload.TextBlocks);
        if (key == "value")
        {
            var gray = byte.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsTrue(payload.TextBlocks.All(block => block.Color == new PresentationColor(gray, gray, gray)));
            CollectionAssert.AreEqual(PresentationLayerPayloadJson.Parse(before.Product.Payload).Payload!.TextBlocks.SelectMany(static block => block.Lines).ToArray(),
                payload.TextBlocks.SelectMany(static block => block.Lines).ToArray());
        }
        var restoreForm = new ProcessingNodeOptionsForm(RetainedLayerOptionDescriptors.Environment, edited);
        restoreForm.Set(key, key == "value" ? "" : form.Descriptor.Find(key)!.DefaultValue.AsWholeNumber().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsTrue(restoreForm.TryBuild(out var cleared, out _));
        var restored = await Execute(cleared!.Value).ConfigureAwait(false);
        Assert.AreEqual(before.Product.OutputIdentitySha256, restored.Product.OutputIdentitySha256);
        CollectionAssert.AreEqual(before.Product.Payload.ToArray(), restored.Product.Payload.ToArray());
    }
    [TestMethod]
    public async Task RetainedCloudEditsReachRealProducerWithoutChangingAssessmentStatus()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        var assessment = CloudAssessment();
        var bytes = CloudAssessmentJson.Serialize(assessment);
        fixture.Context.BeginNode("cloud", []);
        fixture.Context.RegisterProcessingProduct(fixture.Stack with
        {
            Role = FrameArtifactRole.Metadata,
            Payload = bytes,
            SchemaVersion = CloudAssessmentV1.CurrentSchemaVersion,
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(bytes),
            ContentIdentitySha256 = assessment.AssessmentIdentitySha256
        });
        var source = JsonSerializer.SerializeToElement(new CloudPresentationLayerProcessingStepOptions
        {
            WidthPixels = 320,
            HeightPixels = 240,
            MaskOutputVariant = "kept-mask",
            LabelOutputVariant = "kept-label"
        });
        async Task<ProcessingProduct[]> Execute(JsonElement json)
        {
            fixture.Context.BeginNode("cloud-layer", ["cloud"]);
            await new CloudPresentationLayerCaptureProcessingStep(new("cloud-layer", "CloudPresentationLayer", 80),
                json.Deserialize<CloudPresentationLayerProcessingStepOptions>(ProductJson)!)
                .ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);
            return fixture.Context.ProcessingOutcomes[^1].Products.ToArray();
        }
        var before = await Execute(source).ConfigureAwait(false);
        foreach (var key in new[] { "drawLabels", "lineThickness" })
        {
            var form = new ProcessingNodeOptionsForm(RetainedLayerOptionDescriptors.Cloud, source);
            form.Set(key, key == "drawLabels" ? "False" : "4");
            Assert.IsTrue(form.TryBuild(out var edited, out _));
            var after = await Execute(edited!.Value).ConfigureAwait(false);
            var index = key == "drawLabels" ? 1 : 0;
            Assert.AreNotEqual(before[index].ChecksumSha256, after[index].ChecksumSha256);
            Assert.AreEqual(before[1 - index].ChecksumSha256, after[1 - index].ChecksumSha256);
            var payload = PresentationLayerPayloadJson.Parse(after[index].Payload).Payload!;
            Assert.AreEqual(assessment.AssessmentIdentitySha256, payload.SourceIdentitySha256);
            Assert.AreEqual(320, payload.WidthPixels);
            Assert.AreEqual(240, payload.HeightPixels);
            if (key == "drawLabels") Assert.IsEmpty(payload.TextBlocks);
            else Assert.AreEqual(4, payload.TileMask!.LineThickness);
            CollectionAssert.AreEqual(before[index].SourceArtifactIds.ToArray(), after[index].SourceArtifactIds.ToArray());
        }
        StringAssert.Contains(string.Join(' ', PresentationLayerPayloadJson.Parse(before[1].Payload).Payload!.TextBlocks.SelectMany(static block => block.Lines)), "Precipitation Missing", StringComparison.Ordinal);
        var restored = await Execute(source).ConfigureAwait(false);
        for (var i = 0; i < before.Length; i++)
        {
            Assert.AreEqual(before[i].OutputIdentitySha256, restored[i].OutputIdentitySha256);
            CollectionAssert.AreEqual(before[i].Payload.ToArray(), restored[i].Payload.ToArray());
        }
    }

    private static CloudAssessmentV1 CloudAssessment() => new(
        CloudAssessmentV1.CurrentSchemaVersion, CloudAssessmentStatus.Quantified, CloudAssessmentQuality.Degraded,
        [CloudAssessmentReasonCodes.EnvironmentMissing], 500_000, 750_000, new CloudAssessmentGridV1(2, 1, 750_000, 2, 1, 8, 4),
        [new(0, 0, 0, 0, 2, 2, 4, 4, 0, 1_000_000, false), new(1, 0, 2, 0, 2, 2, 4, 4, 0, 500_000, true)],
        new CloudAssessmentMaskV1(CloudAssessmentMaskV1.RowMajorLsbFirst, 2, 1, new byte[] { 0b0000_0010 }),
        new(Guid.Parse("10000000-0000-0000-0000-000000000001"), FrameArtifactRole.Calibrated, "linear-v1", new string('A', 64)),
        new(Guid.Parse("10000000-0000-0000-0000-000000000002"), FrameArtifactRole.Calibrated, "clear-v1", new string('B', 64)),
        new CloudAssessmentCalibrationV1(0, ushort.MaxValue, ushort.MaxValue, "calibration", "mask", "sensor", "processing"),
        new CloudAssessmentEnvironmentV1(CloudAssessmentEnvironmentV1.CurrentSchemaVersion, CaptureSolarRegime.Night,
            EnvironmentalObservationMatchStatus.Missing, null, null, false),
        new string('C', 64), [new ProcessingAlgorithmIdentity("cloud-transmission", "v1")]);
}
