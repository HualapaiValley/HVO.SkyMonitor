using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometryIngestValidationTests
{
    [TestMethod]
    [DataRow(false, "none")]
    [DataRow(true, "none")]
    [DataRow(false, "descriptor")]
    [DataRow(true, "descriptor")]
    [DataRow(false, "source")]
    [DataRow(true, "source")]
    [DataRow(false, "bytes")]
    [DataRow(true, "bytes")]
    public void IngestAcceptsNativeUnavailableEvidenceAndRejectsAlteredCaptureOrPayload(bool solution, string fault)
    {
        var (descriptor, bytes) = Create(solution);
        if (fault == "descriptor") descriptor = descriptor with
        {
            SourceCapture = descriptor.SourceCapture with
            { Timing = descriptor.SourceCapture.Timing with { DurableIngressUtc = descriptor.SourceCapture.Timing.DurableIngressUtc.AddTicks(1) } }
        };
        if (fault == "source") descriptor = descriptor with
        {
            Artifact = descriptor.Artifact with
            { SourceArtifactIds = descriptor.Artifact.SourceArtifactIds.Select((id, i) => i == 0 ? Guid.NewGuid() : id).ToArray() }
        };
        if (fault == "bytes") bytes = [.. bytes, (byte)' '];
        if (fault != "none")
        {
            Assert.ThrowsExactly<ArtifactIntegrityException>(() => ArtifactIngestService.ValidateAstrometryPayload(descriptor, bytes));
            return;
        }
        var accepted = ArtifactIngestService.ValidateAstrometryPayload(descriptor, bytes);
        Assert.AreEqual(descriptor.ProductSchemaVersion, accepted.Schema);
        Assert.AreEqual(descriptor.ContentIdentitySha256, accepted.Identity);
        Assert.AreEqual(descriptor.Artifact.SourceArtifactIds.Count, accepted.Sources.Count);
        Assert.AreEqual(FrameArtifactRole.Raw, accepted.Sources[0].Role);
        Assert.AreEqual(FrameArtifactRole.Metadata, accepted.Sources[1].Role);
    }

    private static (StructuredProcessingProductDescriptorV1 Descriptor, byte[] Bytes) Create(bool solution)
    {
        byte[] rawBytes = [0, 1, 0, 1];
        var raw = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 2, 1, 4, rawBytes).Descriptor;
        var compatibility = new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing");
        var source = new ProcessingArtifact(raw.Artifact.ArtifactId, FrameArtifactRole.Raw, raw.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(raw.Artifact.Recipe).IdentitySha256, raw.Artifact.MediaType, raw.Layout,
            rawBytes, raw.Artifact.CreatedUtc, raw.Controls.EffectiveExposure, compatibility);
        var measured = source with { ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Metadata };
        var scene = source with { ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Metadata };
        var projection = new ProjectionContext(ProjectionModel.Perspective, 1, .5, 1, 1, 2, 1, ProjectionAperture.Rectangular);
        var metadata = new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('A', 64), "test", "1");
        var package = new AstrometricCatalogProvenance("fixture", "1", "fixture", "unspecified");
        var context = new MeasuredAstrometryContextV1(MeasuredAstrometryContextV1.CurrentSchemaVersion,
            new(raw.Capture.CaptureId, raw.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(raw),
                raw.Artifact.ChecksumSha256, new string('B', 64), new(0, 0, 0), raw.Timing.ExposureStartedUtc,
                raw.Timing.ExposureEndedUtc, "fixture-detector", new string('C', 64)), projection, "fixture-calibration",
            MeasuredAstrometryJson.ComputeReadoutIdentity(raw.Layout), metadata, [], false, 7, AstrometricConventions.CoordinateModel,
            package, measured.ArtifactId, new string('D', 64), measured.RecipeIdentitySha256, AstrometricClockFacts.NotSupplied, null);
        var contextProduct = RetainedAstrometryProducts.CreateContextProduct(context, source, measured, scene, "astrometry-context");
        var product = contextProduct;
        if (solution)
        {
            var assessment = AstrometricSolver.Solve(context.Frame, new(projection, context.CalibrationVersion, context.ReadoutIdentitySha256),
                new(metadata, [], false, 7, provenance: package), []).Assessment;
            Assert.AreEqual(AstrometricAssessmentStatus.Unavailable, assessment.Status);
            var definition = BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.MeasuredAstrometry, out var found) ? found! : throw new InvalidOperationException();
            var identity = ProcessingIdentity.CreateRecipeIdentity(definition, JsonSerializer.SerializeToElement(new { }));
            var value = new MeasuredAstrometrySolutionV1(MeasuredAstrometrySolutionV1.CurrentSchemaVersion, identity.IdentitySha256,
                contextProduct.ContentIdentitySha256!, measured.ArtifactId, context.MeasuredIdentitySha256,
                measured.RecipeIdentitySha256, new string('E', 64), context.ReadoutIdentitySha256, package, assessment,
                new(0, [], [], [], null, null, null, null, "catalog-incomplete", []), null, "not-accepted");
            var payload = MeasuredAstrometryJson.Serialize(value);
            product = RetainedAstrometryProducts.Product(source with
            {
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Metadata,
                Variant = "astrometry",
                MediaType = MeasuredAstrometryJson.MediaType,
                Layout = null,
                Payload = payload,
                ProductKind = ProcessingProductKind.Metadata,
                SchemaVersion = value.SchemaVersion,
                ContentIdentitySha256 = value.SolutionIdentitySha256,
                RecipeDescriptor = identity.Descriptor,
                RecipeIdentitySha256 = identity.IdentitySha256,
                SourceArtifactIds = [source.ArtifactId, measured.ArtifactId, scene.ArtifactId, ProcessingIdentity.CreateArtifactId(contextProduct.OutputIdentitySha256)]
            });
        }
        var artifact = new ArtifactDescriptor(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role,
            "astrometry", product.Variant, raw.Artifact.CreatedUtc, product.SourceArtifactIds, product.Recipe.Descriptor,
            product.MediaType, product.ChecksumSha256);
        return (new(raw, artifact, product.OutputIdentitySha256, product.Algorithms, compatibility, product.TotalIntegration.Ticks,
            product.Payload.Length, product.Kind, product.SchemaVersion!, product.ContentIdentitySha256!), product.Payload.ToArray());
    }
}
