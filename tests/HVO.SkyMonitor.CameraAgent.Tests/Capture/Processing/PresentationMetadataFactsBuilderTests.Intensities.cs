using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class PresentationMetadataFactsBuilderTests
{
    [TestMethod]
    [DataRow("markerValue")]
    [DataRow("labelValue")]
    [DataRow("imageCircleValue")]
    [DataRow("cardinalValue")]
    public async Task SceneIntensityEditsReachRealProducerAndClearingReconstructsPalette(string key)
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        var position = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(80, 45), Epoch, 0, 0), Epoch);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("star:vega", "Vega", position.RightAscensionHours, position.DeclinationDegrees, 1)]))
            .BuildAsync(new VisibleSceneRequest(Epoch, new ObserverLocation(0, 0, 0),
                new ProjectionContext(ProjectionModel.EquidistantFisheye, 400, 300, 178, 178, 800, 600,
                    ProjectionAperture.Circular, ImageCircleRadiusPixels: 280, BoresightAltitudeDegrees: 90),
                new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                    new string('C', 64), "test", "v1"), projectionVersion: "equidistant-v1")).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(800, 600), fixture.Scene.Source, "calibration-v1", "equidistant-v1");
        var bytes = ProjectedSceneJson.Serialize(scene);
        fixture.Context.BeginNode("scene", []);
        fixture.Context.RegisterProcessingProduct(fixture.Stack with
        {
            Role = FrameArtifactRole.Metadata, Payload = bytes, SchemaVersion = scene.SchemaVersion,
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(bytes), ContentIdentitySha256 = scene.SceneIdentitySha256
        });
        async Task<ProcessingProduct[]> Execute(JsonElement json)
        {
            fixture.Context.BeginNode("scene-layer", ["scene"]);
            await new ScenePresentationLayerCaptureProcessingStep(new("scene-layer", "ScenePresentationLayer", 70),
                json.Deserialize<ScenePresentationLayerProcessingStepOptions>(ProductJson)!)
                .ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);
            return fixture.Context.ProcessingOutcomes[^1].Products.ToArray();
        }
        var source = JsonSerializer.Deserialize<JsonElement>("{ \"future\":[1e+02] }");
        var before = await Execute(source).ConfigureAwait(false);
        var index = key is "markerValue" or "labelValue" ? 0 : key == "cardinalValue" ? 1 : 2;
        foreach (var value in new[] { "0", "255" })
        {
            var form = new ProcessingNodeOptionsForm(ProcessingNodeOptionsDescriptors.ScenePresentationLayer, source);
            form.Set(key, value);
            Assert.IsTrue(form.TryBuild(out var edited, out _));
            var after = await Execute(edited!.Value).ConfigureAwait(false);
            if (value == "255" && key is "labelValue" or "cardinalValue")
                Assert.AreEqual(before[index].OutputIdentitySha256, after[index].OutputIdentitySha256, "The existing default text is already white.");
            else
            {
                Assert.AreNotEqual(before[index].ChecksumSha256, after[index].ChecksumSha256, key);
                Assert.AreNotEqual(before[index].OutputIdentitySha256, after[index].OutputIdentitySha256);
            }
            var payload = PresentationLayerPayloadJson.Parse(after[index].Payload).Payload!;
            var gray = byte.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            var color = new PresentationColor(gray, gray, gray);
            var colors = key switch
            {
                "markerValue" => payload.Markers.Select(static marker => marker.Color),
                "labelValue" or "cardinalValue" => payload.TextBlocks.Select(static block => block.Color),
                _ => payload.Ellipses.Select(static ellipse => ellipse.Color)
            };
            Assert.IsNotEmpty(colors);
            Assert.IsTrue(colors.All(actual => actual == color));
            CollectionAssert.AreEqual(before[index].SourceArtifactIds.ToArray(), after[index].SourceArtifactIds.ToArray());
            for (var i = 0; i < before.Length; i++)
                if (i != index) Assert.AreEqual(before[i].ChecksumSha256, after[i].ChecksumSha256);
            if (index == 0)
                CollectionAssert.AreEqual(PresentationLayerPayloadJson.Parse(before[0].Payload).Payload!.TextBlocks.SelectMany(static block => block.Lines).ToArray(),
                    payload.TextBlocks.SelectMany(static block => block.Lines).ToArray());
            var clear = new ProcessingNodeOptionsForm(form.Descriptor, edited);
            clear.Set(key, "");
            Assert.IsTrue(clear.TryBuild(out var cleared, out _));
            foreach (var reconstructed in new[] { await Execute(cleared!.Value).ConfigureAwait(false), await Execute(source).ConfigureAwait(false) })
                for (var i = 0; i < before.Length; i++)
                {
                    Assert.AreEqual(before[i].OutputIdentitySha256, reconstructed[i].OutputIdentitySha256);
                    CollectionAssert.AreEqual(before[i].Payload.ToArray(), reconstructed[i].Payload.ToArray());
                }
        }
    }
}
