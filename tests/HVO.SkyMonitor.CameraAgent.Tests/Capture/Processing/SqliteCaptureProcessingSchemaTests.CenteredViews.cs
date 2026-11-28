using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    private static readonly bool[] ExpectedCenteredChangedLive = [false, true, false, true, true];

    [TestMethod]
    public async Task CenteredViewSerializesConcurrentWritersAndRejectsStaleAndIncompatibleEvidence()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        using var processing = new SqliteCaptureProcessingStore(fixture.Options);
        using var secondProcessing = new SqliteCaptureProcessingStore(fixture.Options);
        var first = new SqliteCenteredViews(processing, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var second = new SqliteCenteredViews(secondProcessing, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var scope = ViewScope();
        var definition = ViewDefinition(scope, 103);
        var starts = await Task.WhenAll(Task.Run(() => first.RegisterBaseAsync(definition, [], token).AsTask(), token),
            Task.Run(() => second.RegisterBaseAsync(definition, [], token).AsTask(), token)).ConfigureAwait(false);
        Assert.AreEqual(starts[0].Base.BaseId, starts[1].Base.BaseId);
        Assert.AreEqual(starts[0].Revision.Order, starts[1].Revision.Order);
        var baseId = starts[0].Base.BaseId;
        await first.SelectLiveAsync(scope, baseId, token).ConfigureAwait(false);
        var latestReference = ViewDefinition(scope, 104);
        await first.RegisterBaseAsync(latestReference, [], token).ConfigureAwait(false);
        Assert.AreEqual(latestReference.Reference.Capture.CaptureId, (await first.ReadLatestSingleAsync(scope, token)
            .ConfigureAwait(false))?.Base.Definition.Reference.Capture.CaptureId);

        var one = ExampleLayer(definition, "markers", 4);
        var two = ExampleLayer(definition, "weather", 9);
        await Task.WhenAll(Task.Run(() => first.PublishLayerAsync(baseId, one, token).AsTask(), token),
            Task.Run(() => second.PublishLayerAsync(baseId, two, token).AsTask(), token)).ConfigureAwait(false);
        var current = await first.ReadCurrentAsync(scope, token).ConfigureAwait(false);
        Assert.IsNotNull(current);
        Assert.HasCount(2, current.View.Revision.Layers);
        Assert.AreEqual(current.View.Revision.Order, current.Interval.StartedOrder);
        Assert.AreEqual(current.View.Revision.RevisionId, (await second.PublishLayerAsync(baseId, one, token)
            .ConfigureAwait(false))?.Revision.RevisionId);
        Assert.AreEqual(current.View.Revision.RevisionId, (await second.PublishLayerAsync(baseId, one with { EvidenceOrder = 3 }, token)
            .ConfigureAwait(false))?.Revision.RevisionId);
        var oneSource = definition.Reference;
        var slot = new ExposureProductSlot(oneSource.ExecutionId, "measurement", ExposureMeasurementProducts.SchemaVersion, "measurements");
        var expired = new ExposureProductLookup(oneSource.Input.Key, slot, ExposureProductAvailability.Expired,
            one.EvidenceOrder, null, "retention-expired");
        Assert.AreEqual(current.View.Revision.RevisionId, (await second.PublishUnavailableProductLayerAsync(baseId, one.Key,
            expired, oneSource.ExposureEndedUtc, token).ConfigureAwait(false))?.Revision.RevisionId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.PublishLayerAsync(baseId, one with { Reason = "conflicting" }, token)
            .AsTask()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => first.PublishLayerAsync(baseId,
            one with { Key = one.Key with { Scope = CenteredViewLayerScope.Contributor, SourceCaptureId = Guid.NewGuid() } }, token)
            .AsTask()).ConfigureAwait(false);
        Assert.IsNull(await first.ReadCurrentAsync(scope with { RunId = Guid.NewGuid() }, token).ConfigureAwait(false));
        var changes = await first.ReadChangesAsync(scope, 0, 10, token).ConfigureAwait(false);
        Assert.HasCount(5, changes);
        CollectionAssert.AreEqual(ExpectedCenteredChangedLive, changes.Select(change => change.SelectedLive).ToArray());
        Assert.HasCount(1, await first.ReadChangesAsync(scope, changes[^2].Order, 10, token).ConfigureAwait(false));
        var missing = ExampleLayer(definition, "restored-product", 10);
        var unavailable = await first.PublishUnavailableProductLayerAsync(baseId, missing.Key,
            expired with { Availability = ExposureProductAvailability.MissingEvidence, Revision = missing.EvidenceOrder },
            oneSource.ExposureEndedUtc, token).ConfigureAwait(false);
        Assert.IsNotNull(unavailable);
        Assert.AreEqual(CenteredViewReadiness.Incompatible, unavailable.Revision.Layers.Single(layer => layer.Key == missing.Key).Readiness);
        var restored = await first.PublishLayerAsync(baseId, missing, token).ConfigureAwait(false);
        Assert.IsNotNull(restored);
        Assert.AreNotEqual(unavailable.Revision.RevisionId, restored.Revision.RevisionId);
        Assert.AreEqual(CenteredViewReadiness.Ready, restored.Revision.Layers.Single(layer => layer.Key == missing.Key).Readiness);
    }

    [TestMethod]
    public async Task CenteredArchiveDistinguishesFirstLatestAndLastLiveRevisions()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        var index = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var token = TestContext.CancellationToken;
        var scope = ViewScope();
        var input = ViewDefinition(scope, 103);
        var initial = await index.RegisterBaseAsync(input, [], token).ConfigureAwait(false);
        var enriched = await index.PublishLayerAsync(initial.Base.BaseId, ExampleLayer(input, "markers", 1), token).ConfigureAwait(false);
        Assert.IsNotNull(enriched);
        var neverLive = (await index.ReadArchiveAsync(scope, input.Reference.Capture.CaptureId, token).ConfigureAwait(false)).Single();
        Assert.AreEqual(initial.Revision.RevisionId, neverLive.InitialRevisionId);
        Assert.AreEqual(enriched.Revision.RevisionId, neverLive.LatestRevisionId);
        Assert.IsNull(neverLive.LastLiveRevisionId);
        Assert.AreEqual(initial.Revision.RevisionId, neverLive.DefaultRevisionId);
        await index.SelectLiveAsync(scope, initial.Base.BaseId, token).ConfigureAwait(false);
        var replacement = await index.RegisterBaseAsync(ViewDefinition(scope, 104), [], token).ConfigureAwait(false);
        await index.SelectLiveAsync(scope, replacement.Base.BaseId, token).ConfigureAwait(false);
        var afterLive = await index.PublishLayerAsync(initial.Base.BaseId, ExampleLayer(input, "markers", 2), token).ConfigureAwait(false);
        Assert.IsNotNull(afterLive);
        var archived = (await index.ReadArchiveAsync(scope, input.Reference.Capture.CaptureId, token).ConfigureAwait(false)).Single();
        Assert.AreEqual(initial.Revision.RevisionId, archived.InitialRevisionId);
        Assert.AreEqual(enriched.Revision.RevisionId, archived.LastLiveRevisionId);
        Assert.AreEqual(enriched.Revision.RevisionId, archived.DefaultRevisionId);
        Assert.AreEqual(afterLive.Revision.RevisionId, archived.LatestRevisionId);
    }

    private static CenteredViewStream ViewScope() => new(new("agent-a", "camera-a"), Guid.NewGuid(), "centered-profile");

    private static CenteredViewBaseDefinition ViewDefinition(CenteredViewStream scope, long sequence)
    {
        var descriptor = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 16, 12, 16, new byte[16 * 12]).Descriptor;
        var capture = descriptor.Capture with { CaptureId = Guid.NewGuid(), CaptureSequence = sequence };
        descriptor = descriptor with { Capture = capture, Artifact = descriptor.Artifact with { ArtifactId = Guid.NewGuid() } };
        var input = new ExposureInputV1(new(scope.Camera, scope.RunId, capture.CaptureId), descriptor,
            new(descriptor.Layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));
        var linear = ExamplePin();
        var exposure = new CenteredViewExposure(input, Guid.NewGuid(), linear);
        var previewPin = ExamplePin();
        var preview = new CenteredViewPreview(new(previewPin.ArtifactId, previewPin.IdentitySha256, "image/jpeg",
            new(16, 12, new string('B', 64), new string('C', 64))), previewPin.PayloadSha256,
            RecipeIdentityDescriptor.Create("preview", "1.0.0", "preview-v1", JsonSerializer.SerializeToElement(new { })),
            "JPEG", "jpeg-v1", new(16, 12, 0, 0, 16, 12, 16, 12))
        {
            LinearDisposition = LinearExposureDisposition.QualifiedNormalizationOnly
        };
        return new(scope, exposure, sequence, CenteredViewKind.Single, linear, preview,
            [new(exposure, "ReferenceIdentity", JsonSerializer.SerializeToElement(new { method = "identity" }))], null);
    }

    private static CenteredViewArtifact ExamplePin() => new(Guid.NewGuid(), new string('A', 64), new string('D', 64));

    private static CenteredViewLayer ExampleLayer(CenteredViewBaseDefinition definition, string kind, long order)
    {
        var pin = ExamplePin();
        var drawable = LayeredPresentationJson.CreateLayer(kind,
            new(pin.ArtifactId, pin.IdentitySha256, "application/json", definition.Preview.Product.Compatibility),
            null, PresentationCoordinateSpace.ScenePixels, "renderer-v1", "style-v1",
            200, PresentationBlendMode.Normal, 1_000_000, enabledByDefault: true, default);
        return new(new(kind, CenteredViewLayerScope.Reference, definition.Reference.Capture.CaptureId),
            order, pin, definition.Reference.ExposureEndedUtc, CenteredViewReadiness.Ready, null, drawable);
    }
}
