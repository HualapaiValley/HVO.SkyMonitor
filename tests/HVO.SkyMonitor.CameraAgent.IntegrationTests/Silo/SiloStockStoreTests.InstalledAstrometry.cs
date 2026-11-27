using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    private static async Task AssertRetainedAstrometryAsync(IServiceProvider services, CameraModuleConfig config,
        CaptureLoopSubmission submission, RawCaptureReceipt receipt, CancellationToken token)
    {
        var descriptor = receipt.Manifest.Descriptor;
        var frame = submission.Result.Frame!;
        var rawFrame = new FrameArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, frame);
        var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(config, rawFrame, descriptor.Artifact.Variant,
            reconstructionDescriptor: descriptor);
        var geometry = new ProjectionContext(ProjectionModel.EquidistantFisheye,
            frame.Width / 2d, frame.Height / 2d, frame.Height / 3d, frame.Height / 3d, frame.Width, frame.Height,
            ProjectionAperture.Circular, frame.Height / 2d - 1, 90, 0, 0);
        var metadata = new CatalogMetadata("installed-astrometry-fixture", "1", new Uri("https://example.test/astrometry"),
            new string('A', 64), "test fixture", "1");
        CelestialCatalogObject[] stars = [new("fixture-star", "Fixture Star", 0, 0, 3)];
        var package = new AstrometricCatalogProvenance(metadata.Name, metadata.Version, "fixture", "unspecified");
        var catalog = new AstrometryCatalogSnapshotV1(metadata, stars, package, true, 7);
        var observer = new ObserverLocation(0, 0, 0);
        var timing = descriptor.Timing;
        var midpoint = timing.ExposureStartedUtc.AddTicks((timing.ExposureEndedUtc - timing.ExposureStartedUtc).Ticks / 2);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(stars)).BuildAsync(new VisibleSceneRequest(
            midpoint, observer, geometry, new CatalogQuery(7, 10), metadata, projectionVersion: "fixture-v1"),
            token).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(frame.Width, frame.Height),
            new(descriptor.Capture.CaptureId, raw.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor)),
            "fixture-calibration", "fixture-v1");
        var sceneBytes = ProjectedSceneJson.Serialize(scene);
        var sceneInput = new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: scene.SchemaVersion, IdentitySha256: scene.SceneIdentitySha256, Payload: sceneBytes)
        { ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(sceneBytes) };
        var executor = new ProcessingRecipeExecutor();
        var measured = await executor.ExecuteAsync(new ProcessingExecutionRequest(BuiltInProcessingRecipes.MeasuredStellarAssociations,
            JsonSerializer.SerializeToElement(new { }), ProcessingInputSelector.Raw(raw.Variant), [raw], "measured",
            AuxiliaryInputs: [sceneInput], InputArtifactId: raw.ArtifactId), token).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, measured.Status, measured.ReasonCode);

        var context = new CaptureProcessingContext(config, submission, receipt);
        context.BeginNode("test-silo-astrometry", []);
        var measuredArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, measured.Products.Single());
        var sceneArtifact = measuredArtifact with
        {
            ArtifactId = Guid.NewGuid(),
            Variant = "fixture-scene",
            MediaType = StructuredProcessingProductContracts.ProjectedSceneMediaType,
            SchemaVersion = scene.SchemaVersion,
            ContentIdentitySha256 = scene.SceneIdentitySha256,
            Payload = sceneBytes,
            SourceArtifactIds = [raw.ArtifactId]
        };
        var frozen = RetainedAstrometryProducts.CreateContext(raw, measuredArtifact, sceneArtifact, catalog, AstrometricClockFacts.NotSupplied);
        var contextProduct = RetainedAstrometryProducts.CreateContextProduct(frozen, raw, measuredArtifact, sceneArtifact, "astrometry-context");
        var frozenArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, contextProduct);
        var request = RetainedAstrometryProducts.Request(raw, measuredArtifact, sceneArtifact, frozenArtifact, new(), "astrometry");
        var persistence = services.GetRequiredService<CaptureProcessingPersistence>();
        var first = await persistence.ExecuteRetainedAstrometryAsync(context, request, contextProduct,
            () => executor.ExecuteAsync(request, token), token).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, first.Outcome.Status, first.Outcome.ReasonCode);
        Assert.IsFalse(first.Restored);

        var restored = await persistence.ExecuteRetainedAstrometryAsync(context, request, contextProduct,
            static () => throw new AssertFailedException("A settled astrometry solution must reopen without another fit."), token).ConfigureAwait(false);
        Assert.IsTrue(restored.Restored);
        var solution = restored.Outcome.Products.Single();
        var original = first.Outcome.Products.Single();
        CollectionAssert.AreEqual(original.Payload.ToArray(), solution.Payload.ToArray());
        var silo = services.GetRequiredService<SiloArtifactStorage>();
        Assert.IsTrue(await silo.HasMemberAsync(ProcessingIdentity.CreateArtifactId(solution.OutputIdentitySha256),
            solution.OutputIdentitySha256, token).ConfigureAwait(false));
        var root = services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot;
        var derived = Path.Combine(root, "derived");
        Assert.IsFalse(Directory.Exists(derived) &&
            Directory.EnumerateFiles(derived, $"*{ProcessingIdentity.CreateArtifactId(solution.OutputIdentitySha256):N}*",
                SearchOption.AllDirectories).Any());
    }
}
