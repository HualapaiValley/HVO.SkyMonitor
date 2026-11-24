using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>Declared synthetic raw pixels traverse the scene producer, CameraAgent measurement step and final annotation.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ResolvedSceneMeasuredLabelTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, false)]
    [DataRow(CameraPixelFormat.Mono16, true)]
    [DataRow(CameraPixelFormat.BayerRggb16, false)]
    [DataRow(CameraPixelFormat.BayerRggb16, true)]
    public async Task ProducedSceneMeasuresOutsideStarAndKeepsItsFinalLabel(
        CameraPixelFormat format, bool resolvedSun)
    {
        const int width = 512, height = 256;
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var utc = baseline.Timing.ExposureStartedUtc;
        var position = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(90, 0), utc, 0, 0), utc);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("star:vega", "Vega", position.RightAscensionHours, position.DeclinationDegrees, 1)
        ]), null, new AstronomyEnginePlanetEphemeris()).BuildAsync(new VisibleSceneRequest(utc,
            new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, width / 2d, height / 2d, 400, 400, width, height,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                new string('C', 64), "test", "v1"), projectionVersion: "perspective-v1",
            solarSystemBodies: resolvedSun ? [SolarSystemBody.Sun] : null)).ConfigureAwait(false);
        SolarDiskAppearance[] disks = resolvedSun
            ? [new(SolarSystemBody.Sun, utc, new AltAzPoint(80, 90), .25, 0, 1, 0, 149600000)]
            : [];
        if (resolvedSun) visible = visible.WithResolvedBodies(disks);
        var diskPixels = resolvedSun ? new SolarDiskRenderPlan(visible.Request.Projection, disks, 10_000) : null;
        if (diskPixels is not null) Assert.IsTrue(diskPixels.RetainedElectronRate > 0);
        var star = visible.Objects.Single(static item => item.Id == "star:vega");
        // Synthetic photons/noise are declared inputs, never a fabricated eligible association or detector truth.
        var bytes = new byte[width * height * 2];
        for (var index = 0; index < width * height; index++)
        {
            var dx = (index % width + .5 - star.Pixel.X) / 1.2;
            var dy = (index / width + .5 - star.Pixel.Y) / 1.2;
            var diskRate = diskPixels?.ElectronRate(index % width, index / width) ?? 0;
            var sample = (ushort)Math.Round(1000 + diskRate + 4000 * Math.Exp(-.5 * (dx * dx + dy * dy)) + FixtureNoise(index));
            bytes[index * 2] = (byte)sample;
            bytes[index * 2 + 1] = (byte)(sample >> 8);
        }
        var descriptor = baseline with
        {
            Layout = baseline.Layout with
            {
                Width = width,
                Height = height,
                StrideBytes = width * 2,
                ByteLength = bytes.Length,
                PixelFormat = format,
                CfaPattern = format == CameraPixelFormat.BayerRggb16
                    ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None
            },
            Artifact = baseline.Artifact with { ChecksumSha256 = PayloadChecksum.ComputeSha256(bytes) }
        };
        var descriptorIdentity = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(width, height),
            new ProjectedSceneSource(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, descriptorIdentity),
            "calibration-v1", visible.Request.ProjectionVersion);
        var schema = resolvedSun ? ProjectedSceneV1.ResolvedFootprintSchemaVersion : ProjectedSceneV1.CurrentSchemaVersion;
        Assert.AreEqual(schema, scene.SchemaVersion);
        if (resolvedSun)
        {
            var disk = Assert.ContainsSingle(scene.ResolvedFootprints!);
            Assert.IsTrue(star.Pixel.X < disk.Bounds.MinX || star.Pixel.X > disk.Bounds.MaxX ||
                star.Pixel.Y < disk.Bounds.MinY || star.Pixel.Y > disk.Bounds.MaxY, "the measurable star is outside the resolved disk");
        }
        Assert.IsTrue(FrameReconstructor.TryReconstruct(descriptor, bytes, out var frame).IsValid);
        var artifacts = new FrameArtifactSet(new FrameArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, frame!));
        var manifest = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, "fixture.bin");
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
            new StoredFrameReference("fixture.bin", "unused-fixture.bin", utc, FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));
        var context = new CaptureProcessingContext(ProcessingConformanceFixture.CameraConfig,
            new CaptureLoopSubmission(new CaptureRequest(utc, TimeSpan.FromSeconds(20), CaptureMode.Still),
                new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null),
                    TimeSpan.Zero, CaptureMode.Still, false, artifacts), utc, TimeSpan.FromSeconds(20), TimeSpan.Zero), receipt);
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, artifacts.Raw,
            descriptor.Artifact.Variant, reconstructionDescriptor: descriptor);
        var sceneBytes = ProjectedSceneJson.Serialize(scene);
        var sceneInput = new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: schema, IdentitySha256: scene.SceneIdentitySha256, Payload: sceneBytes)
        { ChecksumSha256 = PayloadChecksum.ComputeSha256(sceneBytes) };
        var edge = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var produced = await edge.ExecuteAsync(new ProcessingExecutionRequest(BuiltInProcessingRecipes.ProjectedScene,
            JsonSerializer.SerializeToElement(new { }), ProcessingInputSelector.Raw(source.Variant), [source], "scene",
            AuxiliaryInputs: [sceneInput]), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, produced.Status, produced.ReasonCode);
        var sceneProduct = Assert.ContainsSingle(produced.Products);
        Assert.AreEqual(schema, sceneProduct.SchemaVersion);
        CollectionAssert.AreEqual(sceneBytes, sceneProduct.Payload.ToArray());
        context.RestoreProduct("scene", ProcessingIdentity.CreateArtifactId(sceneProduct.OutputIdentitySha256), sceneProduct);
        context.BeginNode("measured", ["scene"]);
        var step = new MeasuredStellarAssociationCaptureProcessingStep(new("measured", "MeasuredStellarAssociations", 1),
            new MeasuredStellarAssociationProcessingStepOptions(), edge);
        CollectionAssert.AreEquivalent(ProjectedSceneV1.SupportedSchemaVersions.ToArray(),
            step.DependencyRequirements.Single().SchemaVersions!.ToArray());
        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        var edgeMeasured = Assert.ContainsSingle(context.ProcessingOutcomes);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, edgeMeasured.Status, edgeMeasured.ReasonCode);

        var product = Assert.ContainsSingle(edgeMeasured.Products);
        var associations = MeasuredStellarAssociationJson.Parse(product.Payload).Associations!;
        var association = Assert.ContainsSingle(associations.Associations);
        Assert.AreEqual("star:vega", association.CatalogId);
        Assert.IsTrue(association.LabelEligible, association.LabelRejectionReason);
        Assert.IsFalse(associations.Associations.Any(static item => item.CatalogId.StartsWith("solar-system:", StringComparison.Ordinal)));
        var typed = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false,
            includeImageCircle: false, includeCardinalDirections: false, associations: associations).StarAnnotations;
        Assert.IsTrue(typed.TextBlocks.Any(static item => item.Lines[0] == "Vega"));
        var absent = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false,
            includeImageCircle: false, includeCardinalDirections: false).StarAnnotations;
        Assert.IsFalse(absent.TextBlocks.Any(static item => item.Lines[0] == "Vega"));
        var mapped = ProjectedSceneAnnotation.CreateObjects(scene, 2.5);
        var annotation = new ProcessingAnnotationInput(mapped, [], new PreviewTransform(1, 1), null,
            scene.SceneIdentitySha256);
        var measuredArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, product);
        var auxiliary = new ProcessingAuxiliaryInput(BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName,
            ProcessingAuxiliaryInputKind.Artifact,
            ProcessingInputSelector.RecipeResult(product.Role, product.Variant, product.Recipe.IdentitySha256),
            ArtifactId: measuredArtifact.ArtifactId);
        byte[]? withoutMeasured = null;
        foreach (var bindMeasured in new[] { false, true })
        {
            var options = JsonSerializer.SerializeToElement(new AnnotationRecipeOptions(OutputEncoding: "Packed"));
            var edgeAnnotation = await edge.ExecuteAsync(new ProcessingExecutionRequest(BuiltInProcessingRecipes.Annotation,
                options, ProcessingInputSelector.Raw(source.Variant), bindMeasured ? [source, measuredArtifact] : [source],
                "annotated", annotation, bindMeasured ? [auxiliary] : [], source.ArtifactId), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, edgeAnnotation.Status, edgeAnnotation.ReasonCode);
            var rendered = Assert.ContainsSingle(edgeAnnotation.Products);
            if (!bindMeasured)
            {
                withoutMeasured = rendered.Payload.ToArray();
            }
            else
            {
                Assert.IsFalse(rendered.Payload.Span.SequenceEqual(withoutMeasured), "The eligible star produces a final raster label.");
                Assert.Contains(measuredArtifact.ArtifactId, rendered.SourceArtifactIds);
            }
        }
    }

    // Declared deterministic counter-hash noise, rather than a periodic ramp with constant adjacent differences.
    private static int FixtureNoise(int index)
    {
        var bits = (uint)index + 526u;
        bits ^= bits >> 16;
        bits = unchecked(bits * 0x7FEB352Du);
        bits ^= bits >> 15;
        bits = unchecked(bits * 0x846CA68Bu);
        bits ^= bits >> 16;
        return (int)(bits % 13) - 6;
    }
}
