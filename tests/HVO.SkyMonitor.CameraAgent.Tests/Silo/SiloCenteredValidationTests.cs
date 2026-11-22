using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// The embedded typed view is the durable view revision (#1965 4.4, 6.1, 6.3). At staging and at every manifest readback
/// its projection, preview and reopen entries must be exactly what its documents derive, so no Ready layer, preview or
/// stack closed window can be dropped while the embedded revision ID stays the same.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloCenteredValidationTests
{
    [TestMethod]
    public async Task AStoreWrittenStackRevisionPublishesRebuildsAndReopensExactlyAndNeedsItsClosedWindow()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("silo-centered-store");
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
            var stack = SiloTypedViews.Stack("store-stack");
            var stars = SiloTypedViews.Evidence("store-stars");
            var first = await views.RegisterBaseAsync(stack.Definition,
                [new(new("clouds", CenteredViewLayerScope.Stack, null), 0, null, null, CenteredViewReadiness.Waiting, null, null)],
                CancellationToken.None).ConfigureAwait(false);
            var second = (await views.PublishLayerAsync(first.Base.BaseId,
                new(new("stars", CenteredViewLayerScope.Stack, null), 3, stars.Artifact, SiloTypedViews.Registered, CenteredViewReadiness.Ready, null,
                    SiloTypedViews.Drawable("stars", stars.Artifact)), CancellationToken.None).ConfigureAwait(false))!;
            first.Base.BaseId.Should().Be(SqliteCenteredViews.BaseIdentity(stack.Definition), "the Silo derivation is the store's own");
            using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
            var (baseId, initial, enriched) = (first.Base.BaseId, first.Revision.RevisionId, second.Revision.RevisionId);
            await silo.PublishAsync(SiloTypedViews.Draft(baseId, initial, null, first,
                [.. stack.Products.Select(product => product.Member(silo.Payloads))], [], 1)).ConfigureAwait(false);
            await silo.PublishAsync(SiloTypedViews.Draft(baseId, enriched, initial, second, [stars.Member(silo.Payloads)], [], 2))
                .ConfigureAwait(false);
            var streamId = SqliteCenteredViews.StreamIdentity(stack.Definition.Stream);
            var reference = stack.Definition.Reference.Capture.CaptureId.ToString("D");
            Task<SiloReopenResult> ReopenAsync() => silo.Catalog.ReopenViewAsync(streamId, reference, baseId, enriched, CancellationToken.None).AsTask();

            var intact = await ReopenAsync().ConfigureAwait(false);
            intact.Reopenable.Should().BeTrue();
            intact.Items.Where(static item => item.Required && item.Role.Kind == SiloIdentityKind.Member).Select(static item => item.Role.Id)
                .Should().BeEquivalentTo([.. stack.Products.Select(static product => product.ArtifactId), stars.ArtifactId],
                    "the preview, displayed science, closed window and Ready evidence are the exact reopen set");
            var layers = await silo.Index.ReadLayersAsync(baseId, enriched, CancellationToken.None).ConfigureAwait(false);
            await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
            (await silo.Index.ReadPublicationAsync(baseId, enriched, CancellationToken.None).ConfigureAwait(false))!.State
                .Should().Be("visible", "the store's own documents conform when the manifest is read back");
            (await silo.Index.ReadLayersAsync(baseId, enriched, CancellationToken.None).ConfigureAwait(false)).Should().Equal(layers);
            (await ReopenAsync().ConfigureAwait(false)).Reopenable.Should().BeTrue();

            silo.Local.Delete(silo.MemberLocator(baseId, stack.ClosedWindow.ArtifactId, initial));
            await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None, includeAvailable: true).ConfigureAwait(false);
            var blocked = await ReopenAsync().ConfigureAwait(false);
            blocked.Reopenable.Should().BeFalse();
            blocked.Blocking.Should().ContainSingle().Which.Role.Id.Should().Be(stack.ClosedWindow.ArtifactId, "a stack's closed window is mandatory");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("none")]
    [DataRow("projected-ready-layer")]
    [DataRow("embedded-ready-layer")]
    [DataRow("embedded-presentation")]
    [DataRow("projected-preview")]
    [DataRow("closed-window-dependency")]
    [DataRow("closed-window-optional")]
    [DataRow("embedded-closed-window")]
    [DataRow("science-dependency")]
    public async Task AViewThatDisagreesWithItsTypedDocumentsIsRefusedAtStagingAndClosedOnReadback(string forgery)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var stack = SiloTypedViews.Stack("forged");
        var stars = SiloTypedViews.Evidence("forged-stars");
        var anchor = SiloHarness.Start.AddMinutes(10);
        await silo.PublishAsync(new SiloGroupDraft("product", "products-forged", "r1", anchor, anchor, anchor.AddSeconds(1),
            [.. stack.Products.Select(product => product.Member(silo.Payloads)), stars.Member(silo.Payloads)], [], new SiloManifestProvenance([]),
            CameraId: "camera-1")).ConfigureAwait(false);
        var view = SiloTypedViews.View(stack.Definition, null, 1,
        [
            new(new("clouds", CenteredViewLayerScope.Stack, null), 0, null, null, CenteredViewReadiness.Waiting, null, null),
            new(new("stars", CenteredViewLayerScope.Stack, null), 3, stars.Artifact, SiloHarness.Start.AddMinutes(11), CenteredViewReadiness.Ready,
                null, SiloTypedViews.Drawable("stars", stars.Artifact))
        ]);
        var (baseId, revisionId) = (view.Base.BaseId, view.Revision.RevisionId);
        var draft = Forge(SiloTypedViews.Draft(baseId, revisionId, null, view, [], [], 1), forgery, stack, stars);

        if (forgery != "none")
        {
            var stage = () => silo.Publisher.StageAsync(draft, CancellationToken.None).AsTask();
            await stage.Should().ThrowAsync<ArgumentException>("staging validates the manifest it will publish").ConfigureAwait(false);
        }
        // Readback: the same manifest, committed without staging, as a damaged writer or a forged object would leave it.
        var bytes = SiloJson.Serialize(SiloPublisher.BuildManifest(draft, silo.Binding, 1, []));
        var sha = SiloHash.Sha256(bytes);
        var key = SiloObjectNamespace.ManifestKey(draft.FromUtc, baseId, revisionId, sha);
        var version = silo.Local.Plant(key, bytes);
        await silo.AppendAsync(new SiloPublishRecord(SiloJson.CenteredViewGroupKind, baseId, revisionId,
            new(silo.Local.Locator(key, version), bytes.Length, sha, new(silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256, true)), []))
            .ConfigureAwait(false);

        await AssertReadbackAsync(silo, forgery, baseId, revisionId).ConfigureAwait(false);
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        await AssertReadbackAsync(silo, forgery, baseId, revisionId).ConfigureAwait(false);
    }

    private static async Task AssertReadbackAsync(SiloHarness silo, string forgery, string baseId, string revisionId)
    {
        var publication = (await silo.Index.ReadPublicationAsync(baseId, revisionId, CancellationToken.None).ConfigureAwait(false))!;
        var reopen = await silo.Catalog.ReopenViewAsync(SiloTypedViews.StreamId("forged"), SiloTypedViews.ReferenceCapture("forged").ToString("D"),
            baseId, revisionId, CancellationToken.None).ConfigureAwait(false);
        if (forgery == "none")
        {
            publication.State.Should().Be("visible", "the control manifest is committed the same way and conforms");
            reopen.Reopenable.Should().BeTrue();
            return;
        }
        publication.State.Should().Be("closed");
        publication.Outcome.Should().Be(SiloDisposition.Conflict);
        publication.OutcomeDetail.Should().Contain("is invalid", "the manifest itself fails validation, not its identity or members");
        reopen.Reopenable.Should().BeFalse();
    }

    private static SiloGroupDraft Forge(SiloGroupDraft draft, string forgery, SiloStackView stack, SiloViewProduct stars)
    {
        var view = draft.View!;
        IReadOnlyList<SiloManifestDependency> Without(SiloViewProduct product) =>
            [.. draft.Dependencies.Where(dependency => dependency.ArtifactId != product.ArtifactId)];
        var withoutStars = view.Layers.Where(static layer => layer.Kind != "stars").ToArray();
        switch (forgery)
        {
            case "none":
                return draft;
            case "projected-ready-layer":
                // The projection and the reopen set drop a Ready layer; the embedded revision still binds it.
                return draft with { View = view with { Layers = withoutStars }, Dependencies = Without(stars) };
            case "embedded-ready-layer":
                // The embedded revision drops it too, and keeps its revision ID.
                return draft with
                {
                    View = view with
                    {
                        Layers = withoutStars,
                        Revision = Edit(view.Revision, static node =>
                        {
                            var layers = node["layers"]!.AsArray();
                            layers.Remove(layers.Single(static layer => (string?)layer!["key"]!["kind"] == "stars"));
                        })
                    },
                    Dependencies = Without(stars)
                };
            case "embedded-presentation":
                // The presentation manifest is outside the revision ID, so it must be derived as well.
                return draft with { View = view with { Revision = Edit(view.Revision, static node => node["manifest"]!["layers"]!.AsArray().Clear()) } };
            case "projected-preview":
                var other = SiloTypedViews.Evidence("forged-other-preview");
                return draft with
                {
                    View = view with { Preview = new(other.ArtifactId, other.Artifact.IdentitySha256, other.Artifact.PayloadSha256) },
                    Dependencies = [.. Without(stack.Preview),
                        new(other.ArtifactId, other.Artifact.IdentitySha256, SiloHash.Sha256(other.Bytes), SiloDependencyKind.Reopen, true)]
                };
            case "closed-window-dependency":
                return draft with { Dependencies = Without(stack.ClosedWindow) };
            case "closed-window-optional":
                return draft with
                {
                    Dependencies = [.. draft.Dependencies.Select(dependency =>
                        dependency.ArtifactId == stack.ClosedWindow.ArtifactId ? dependency with { Required = false } : dependency)]
                };
            case "embedded-closed-window":
                // The embedded stack drops its mandatory closed window under the same base and revision IDs.
                return draft with
                {
                    View = view with { Base = Edit(view.Base, static node => node["definition"]!["closedWindow"] = null) },
                    Dependencies = Without(stack.ClosedWindow)
                };
            case "science-dependency":
                return draft with { Dependencies = Without(stack.Science) };
            default:
                throw new ArgumentOutOfRangeException(nameof(forgery));
        }
    }

    private static JsonElement Edit(JsonElement document, Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(document.GetRawText())!;
        edit(node);
        return JsonSerializer.SerializeToElement(node);
    }
}
