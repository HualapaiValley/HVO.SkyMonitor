using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// A typed centered view revision, written by the existing store, has exactly one published representation (#1965 4.4,
/// 6.1, 6.3): its documents, stream identity and revision ID come from the store's own functions, the revision ID is
/// recomputed rather than trusted, and its reopen set is derived from the typed preview and layers.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloTypedViewTests
{
    [TestMethod]
    public async Task ATypedViewRevisionYieldsOneNormalizedSectionUnderTheStoresOwnIdentities()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("silo-typed-view");
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });
            await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1).InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            using var store = new SqliteCaptureProcessingStore(options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var views = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
            var scope = new CenteredViewStream(new("agent-a", "camera-a"), Guid.NewGuid(), "centered-profile");
            var definition = ViewDefinition(scope);
            var first = await views.RegisterBaseAsync(definition, [], CancellationToken.None).ConfigureAwait(false);
            var second = await views.PublishLayerAsync(first.Base.BaseId, ReadyLayer(definition), CancellationToken.None).ConfigureAwait(false);

            var section = SiloCenteredViews.Section(second!, first.Revision.RevisionId);

            section.StreamId.Should().Be(SqliteCenteredViews.StreamIdentity(scope));
            section.ReferenceCaptureId.Should().Be(definition.Reference.Capture.CaptureId.ToString("D"));
            section.Preview.ArtifactId.Should().Be(definition.Preview.Product.ArtifactId.ToString("D"));
            section.Revision.GetProperty("revisionId").GetString().Should().Be(second!.Revision.RevisionId);
            section.Layers.Should().ContainSingle().Which.Readiness.Should().Be("Ready");
            var dependencies = SiloCenteredViews.ReopenDependencies(second, scienceDisplayed: true);
            var evidence = second.Revision.Layers.Single().Evidence!;
            dependencies.Should().Contain(dependency => dependency.ArtifactId == section.Preview.ArtifactId && dependency.Required);
            dependencies.Should().Contain(dependency => dependency.ArtifactId == evidence.ArtifactId.ToString("D") && dependency.Required);
            dependencies.Should().Contain(dependency => dependency.ArtifactId == definition.Science.ArtifactId.ToString("D") && dependency.Required);
            dependencies.Should().OnlyContain(static dependency => SiloHash.IsValid(dependency.Sha256));

            var forged = () => SiloCenteredViews.Section(second, previousRevisionId: null);
            forged.Should().Throw<ArgumentException>("a revision ID is recomputed from its base, predecessor and bindings, never trusted");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CenteredViewArtifact Pin() => new(Guid.NewGuid(), new string('A', 64), new string('D', 64));

    private static CenteredViewBaseDefinition ViewDefinition(CenteredViewStream scope)
    {
        var descriptor = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 16, 12, 16, new byte[16 * 12]).Descriptor;
        var capture = descriptor.Capture with { CaptureId = Guid.NewGuid(), CaptureSequence = 3 };
        descriptor = descriptor with { Capture = capture, Artifact = descriptor.Artifact with { ArtifactId = Guid.NewGuid() } };
        var input = new ExposureInputV1(new(scope.Camera, scope.RunId, capture.CaptureId), descriptor,
            new(descriptor.Layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));
        var linear = Pin();
        var exposure = new CenteredViewExposure(input, Guid.NewGuid(), linear);
        var previewPin = Pin();
        var preview = new CenteredViewPreview(new(previewPin.ArtifactId, previewPin.IdentitySha256, "image/jpeg",
            new(16, 12, new string('B', 64), new string('C', 64))), previewPin.PayloadSha256,
            RecipeIdentityDescriptor.Create("preview", "1.0.0", "preview-v1", JsonSerializer.SerializeToElement(new { })),
            "JPEG", "jpeg-v1", new(16, 12, 0, 0, 16, 12, 16, 12))
        {
            LinearDisposition = LinearExposureDisposition.QualifiedNormalizationOnly
        };
        return new(scope, exposure, 3, CenteredViewKind.Single, linear, preview,
            [new(exposure, "ReferenceIdentity", JsonSerializer.SerializeToElement(new { method = "identity" }))], null);
    }

    private static CenteredViewLayer ReadyLayer(CenteredViewBaseDefinition definition)
    {
        var pin = Pin();
        var drawable = LayeredPresentationJson.CreateLayer("markers",
            new(pin.ArtifactId, pin.IdentitySha256, "application/json", definition.Preview.Product.Compatibility),
            null, PresentationCoordinateSpace.ScenePixels, "renderer-v1", "style-v1",
            200, PresentationBlendMode.Normal, 1_000_000, enabledByDefault: true, default);
        return new(new("markers", CenteredViewLayerScope.Reference, definition.Reference.Capture.CaptureId),
            1, pin, definition.Reference.ExposureEndedUtc, CenteredViewReadiness.Ready, null, drawable);
    }
}
