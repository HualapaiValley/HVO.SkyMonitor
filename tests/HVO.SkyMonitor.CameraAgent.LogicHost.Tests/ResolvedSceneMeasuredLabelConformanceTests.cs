using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

/// <summary>Declared synthetic raw pixels traverse the real scene producer, edge step and central frozen binding.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ResolvedSceneMeasuredLabelConformanceTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, false)]
    [DataRow(CameraPixelFormat.Mono16, true)]
    [DataRow(CameraPixelFormat.BayerRggb16, false)]
    [DataRow(CameraPixelFormat.BayerRggb16, true)]
    public async Task ProducedSceneMeasuresOutsideStarAndKeepsItsFinalLabelOnBothHosts(
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
            var sample = (ushort)Math.Round(1000 + diskRate + 4000 * Math.Exp(-.5 * (dx * dx + dy * dy)) + index * 7919 % 13 - 6);
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
        var central = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var produced = await edge.ExecuteAsync(new ProcessingExecutionRequest(BuiltInProcessingRecipes.ProjectedScene,
            JsonSerializer.SerializeToElement(new { }), ProcessingInputSelector.Raw(source.Variant), [source], "scene",
            AuxiliaryInputs: [sceneInput]), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, produced.Status, produced.ReasonCode);
        var sceneProduct = Assert.ContainsSingle(produced.Products);
        Assert.AreEqual(schema, sceneProduct.SchemaVersion);
        CollectionAssert.AreEqual(sceneBytes, sceneProduct.Payload.ToArray());
        var centralProduced = await central.ExecuteAsync(descriptor, bytes, BuiltInProcessingRecipes.ProjectedScene,
            JsonSerializer.SerializeToElement(new { }), ProcessingInputSelector.Raw(source.Variant), "scene",
            auxiliaryInputs: [sceneInput]).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, centralProduced.Status, centralProduced.ReasonCode);
        Assert.AreEqual(sceneProduct.OutputIdentitySha256, Assert.ContainsSingle(centralProduced.Products).OutputIdentitySha256);
        context.RestoreProduct("scene", ProcessingIdentity.CreateArtifactId(sceneProduct.OutputIdentitySha256), sceneProduct);
        context.BeginNode("measured", ["scene"]);
        var step = new MeasuredStellarAssociationCaptureProcessingStep(new("measured", "MeasuredStellarAssociations", 1),
            new MeasuredStellarAssociationProcessingStepOptions(), edge);
        CollectionAssert.AreEquivalent(ProjectedSceneV1.SupportedSchemaVersions.ToArray(),
            step.DependencyRequirements.Single().SchemaVersions!.ToArray());
        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        var edgeMeasured = Assert.ContainsSingle(context.ProcessingOutcomes);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, edgeMeasured.Status, edgeMeasured.ReasonCode);

        var reference = new CentralProjectedSceneReference(Guid.NewGuid(),
            ProcessingIdentity.CreateArtifactId(sceneProduct.OutputIdentitySha256), descriptor.Capture.CaptureId,
            sceneProduct.ChecksumSha256, sceneProduct.ContentIdentitySha256!, scene.Source, "scene",
            CentralProjectedSceneResolver.CreateAnnotation(scene, "scene").ProvenanceSha256, schema);
        var annotation = CentralProjectedSceneResolver.CreateAnnotation(scene, "scene");
        var selected = new CentralProjectedSceneSelection(new CentralArtifact
        { Id = reference.CentralArtifactId, StructuredProduct = new CentralStructuredProcessingProduct { ProductSchemaVersion = schema } },
            reference, annotation);
        Assert.IsTrue(CentralProcessingGraphScheduler.IsSupportedAssociationScene(selected));
        Assert.IsFalse(CentralProcessingGraphScheduler.IsSupportedAssociationScene(selected with
        { Reference = reference with { SchemaVersion = "projected-scene-v99" } }));
        var verified = CentralMeasuredAssociationSceneReader.Verify(reference, sceneBytes);
        Assert.IsNull(verified.FailureReasonCode, verified.FailureReasonCode);
        Assert.AreEqual(schema, verified.Input!.SchemaVersion);
        Assert.AreEqual(CentralMeasuredAssociationSceneReader.CreateIdentityInput(reference),
            verified.Input with { Payload = default, ChecksumSha256 = null });
        var centralMeasured = await central.ExecuteAsync(descriptor, bytes, BuiltInProcessingRecipes.MeasuredStellarAssociations,
            JsonSerializer.SerializeToElement(new MeasuredStellarAssociationRecipeOptions()), ProcessingInputSelector.Raw(source.Variant),
            step.OutputVariant, auxiliaryInputs: [verified.Input]).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, centralMeasured.Status, centralMeasured.ReasonCode);
        var product = Assert.ContainsSingle(edgeMeasured.Products);
        var centralProduct = Assert.ContainsSingle(centralMeasured.Products);
        CollectionAssert.AreEqual(product.Payload.ToArray(), centralProduct.Payload.ToArray());
        Assert.AreEqual(product.Recipe.IdentitySha256, centralProduct.Recipe.IdentitySha256);
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
        var raster = AnnotationRenderer.AnnotateMono8(new byte[width * height], width, height,
            StellarLabelPolicy.Apply(mapped, associations), new PreviewTransform(1, 1));
        var unmeasured = AnnotationRenderer.AnnotateMono8(new byte[width * height], width, height,
            StellarLabelPolicy.Apply(mapped, null), new PreviewTransform(1, 1));
        Assert.IsFalse(raster.Pixels.Span.SequenceEqual(unmeasured.Pixels.Span), "the eligible star produces a final raster label");
        var measuredArtifact = new ProcessingArtifact(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            product.Role, product.Variant, product.Recipe.IdentitySha256, product.MediaType, product.Layout,
            product.Payload, utc, product.TotalIntegration, product.Compatibility, SourceArtifactIds: product.SourceArtifactIds)
        { ProductKind = product.Kind, SchemaVersion = product.SchemaVersion, ContentIdentitySha256 = product.ContentIdentitySha256 };
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
            LogicHostProcessingInput[] centralInputs = bindMeasured
                ? [new(descriptor, bytes), new(null, measuredArtifact.Payload, auxiliary.Name,
                    measuredArtifact, ProcessingGraphInputBindingKind.AuxiliaryArtifact)]
                : [new(descriptor, bytes)];
            var centralAnnotation = await central.ExecuteAsync(centralInputs, BuiltInProcessingRecipes.Annotation,
                options, ProcessingInputSelector.Raw(source.Variant), "annotated", annotation).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, edgeAnnotation.Status, edgeAnnotation.ReasonCode);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, centralAnnotation.Status, centralAnnotation.ReasonCode);
            var rendered = Assert.ContainsSingle(edgeAnnotation.Products);
            var centralRendered = Assert.ContainsSingle(centralAnnotation.Products);
            CollectionAssert.AreEqual(rendered.Payload.ToArray(), centralRendered.Payload.ToArray());
            Assert.AreEqual(rendered.OutputIdentitySha256, centralRendered.OutputIdentitySha256);
            if (!bindMeasured) withoutMeasured = rendered.Payload.ToArray();
            else Assert.IsFalse(rendered.Payload.Span.SequenceEqual(withoutMeasured), "both final host recipes retain the measured label");
        }
    }
}
