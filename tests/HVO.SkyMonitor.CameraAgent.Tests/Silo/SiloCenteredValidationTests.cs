using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// The embedded typed view is the durable view revision (#1965 4.4, 6.1, 6.3). At staging and at every manifest readback
/// its outer group and revision IDs must be the store-issued base and revision IDs, its layers must be in the order the
/// store writes, its projection and preview must be what its documents derive, and its reopen rows must be exactly their
/// reopen set, each artifact revision once. No Ready layer, preview or stack closed window can be dropped, aliased or
/// contradicted while the embedded revision ID stays the same.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloCenteredValidationTests
{
    [TestMethod]
    public async Task AStoreWrittenStackRevisionPublishesRebuildsAndReopensExactlyByItsStoreIssuedIdsOnly()
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
            CenteredViewLayer[] reversed = [Waiting("satellites"), Waiting("clouds")];
            var first = await views.RegisterBaseAsync(stack.Definition, reversed, CancellationToken.None).ConfigureAwait(false);
            var second = (await views.PublishLayerAsync(first.Base.BaseId,
                new(new("stars", CenteredViewLayerScope.Stack, null), 3, stars.Artifact, SiloTypedViews.Registered, CenteredViewReadiness.Ready, null,
                    SiloTypedViews.Drawable("stars", stars.Artifact)), CancellationToken.None).ConfigureAwait(false))!;
            first.Base.BaseId.Should().Be(SqliteCenteredViews.BaseIdentity(stack.Definition), "the Silo derivation is the store's own");
            first.Revision.Layers.Select(static layer => layer.Key.Kind).Should().Equal(["clouds", "satellites"], "the store writes one layer order");
            first.Revision.RevisionId.Should().Be(SiloTypedViews.View(stack.Definition, null, 1, reversed).Revision.RevisionId)
                .And.NotBe(SqliteCenteredViews.RevisionIdentity(first.Base.BaseId, null, reversed), "the store never issues an unsorted identity");
            using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
            var (baseId, initial, enriched) = (first.Base.BaseId, first.Revision.RevisionId, second.Revision.RevisionId);
            var initialDraft = SiloTypedViews.Draft(first, null, [.. stack.Products.Select(product => product.Member(silo.Payloads))], [], 1);
            var enrichedDraft = SiloTypedViews.Draft(second, initial, [stars.Member(silo.Payloads)], [], 2);
            (initialDraft.GroupId, initialDraft.RevisionId, enrichedDraft.RevisionId).Should().Be((baseId, initial, enriched));
            foreach (var alias in new[] { enrichedDraft with { GroupId = "base-a" }, enrichedDraft with { RevisionId = "rev-2" } })
            {
                var stage = () => silo.Publisher.StageAsync(alias, CancellationToken.None).AsTask();
                await stage.Should().ThrowAsync<ArgumentException>("a view is published only under its store-issued IDs").ConfigureAwait(false);
            }
            await silo.PublishAsync(initialDraft).ConfigureAwait(false);
            await silo.PublishAsync(enrichedDraft).ConfigureAwait(false);
            var streamId = SqliteCenteredViews.StreamIdentity(stack.Definition.Stream);
            var reference = stack.Definition.Reference.Capture.CaptureId.ToString("D");
            Task<SiloReopenResult> ReopenAsync(string group, string revision) =>
                silo.Catalog.ReopenViewAsync(streamId, reference, group, revision, CancellationToken.None).AsTask();

            var intact = await ReopenAsync(baseId, enriched).ConfigureAwait(false);
            intact.Reopenable.Should().BeTrue();
            intact.Items.Where(static item => item.Required && item.Role.Kind == SiloIdentityKind.Member).Select(static item => item.Role.Id)
                .Should().BeEquivalentTo([.. stack.Products.Select(static product => product.ArtifactId), stars.ArtifactId],
                    "the preview, displayed science, closed window and Ready evidence are the exact reopen set");
            foreach (var (group, revision) in new[] { ("base-a", enriched), (baseId, "rev-2") })
            {
                (await ReopenAsync(group, revision).ConfigureAwait(false)).Found.Should().BeFalse("an alias names no published view");
            }
            var layers = await silo.Index.ReadLayersAsync(baseId, enriched, CancellationToken.None).ConfigureAwait(false);
            await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
            (await silo.Index.ReadPublicationAsync(baseId, enriched, CancellationToken.None).ConfigureAwait(false))!.State
                .Should().Be("visible", "the store's own documents conform when the manifest is read back");
            (await silo.Index.ReadLayersAsync(baseId, enriched, CancellationToken.None).ConfigureAwait(false)).Should().Equal(layers);
            (await ReopenAsync(baseId, enriched).ConfigureAwait(false)).Reopenable.Should().BeTrue();

            silo.Local.Delete(silo.MemberLocator(baseId, stack.ClosedWindow.ArtifactId, initial));
            await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None, includeAvailable: true).ConfigureAwait(false);
            var blocked = await ReopenAsync(baseId, enriched).ConfigureAwait(false);
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
    [DataRow("alias-group")]
    [DataRow("alias-revision")]
    [DataRow("unsorted-layers")]
    [DataRow("member-dependency-alias")]
    [DataRow("member-dependency-duplicate")]
    [DataRow("extra-reopen-dependency")]
    [DataRow("extra-required-member")]
    [DataRow("extra-optional-member")]
    public async Task AViewThatDisagreesWithItsTypedDocumentsIsRefusedAtStagingAndClosedOnReadback(string forgery)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var stack = SiloTypedViews.Stack("forged");
        var stars = SiloTypedViews.Evidence("forged-stars");
        var anchor = SiloHarness.Start.AddMinutes(10);
        await silo.PublishAsync(new SiloGroupDraft("product", "products-forged", "r1", anchor, anchor, anchor.AddSeconds(1),
            [stack.Science.Member(silo.Payloads), stack.ClosedWindow.Member(silo.Payloads), stars.Member(silo.Payloads)], [],
            new SiloManifestProvenance([]), CameraId: "camera-1")).ConfigureAwait(false);
        var view = SiloTypedViews.View(stack.Definition, null, 1,
        [
            Waiting("clouds"),
            new(new("stars", CenteredViewLayerScope.Stack, null), 3, stars.Artifact, SiloHarness.Start.AddMinutes(11), CenteredViewReadiness.Ready,
                null, SiloTypedViews.Drawable("stars", stars.Artifact))
        ]);
        // The preview is published in the view's own group; everything else it reopens from is a dependency.
        var draft = Forge(SiloTypedViews.Draft(view, null, [stack.Preview.Member(silo.Payloads)], [], 1), forgery, view, stack, stars,
            silo.Payloads);

        if (forgery == "extra-optional-member")
        {
            (await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false)).Should()
                .BeTrue("optional data outside the typed proof is allowed");
        }
        else if (!Accepted(forgery))
        {
            var stage = () => silo.Publisher.StageAsync(draft, CancellationToken.None).AsTask();
            await stage.Should().ThrowAsync<ArgumentException>("staging validates the manifest it will publish").ConfigureAwait(false);
        }
        // Readback: the same manifest, committed without staging, as a damaged writer or a forged object would leave it.
        await PlantAsync(silo, draft).ConfigureAwait(false);

        await AssertReadbackAsync(silo, forgery, draft).ConfigureAwait(false);
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        await AssertReadbackAsync(silo, forgery, draft).ConfigureAwait(false);
        if (forgery == "extra-optional-member")
        {
            // Losing optional data the typed view does not reference is reported, and never blocks exact reopen.
            var extra = SiloTypedViews.Evidence("forged-extra").ArtifactId;
            silo.Local.Delete(silo.MemberLocator(draft.GroupId, extra, draft.RevisionId));
            await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None, includeAvailable: true).ConfigureAwait(false);
            var reopen = await silo.Catalog.ReopenViewAsync(SiloTypedViews.StreamId("forged"), SiloTypedViews.ReferenceCapture("forged").ToString("D"),
                draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
            reopen.Reopenable.Should().BeTrue();
            reopen.Items.Single(item => item.Role.Id == extra).Should()
                .Match<SiloReopenItem>(static item => !item.Required && item.Disposition == SiloDisposition.Missing);
        }
    }

    /// <summary>Rows whose manifest is the canonical projection, or adds only optional data outside it.</summary>
    private static bool Accepted(string forgery) => forgery is "none" or "extra-optional-member";

    private static CenteredViewLayer Waiting(string kind) =>
        new(new(kind, CenteredViewLayerScope.Stack, null), 0, null, null, CenteredViewReadiness.Waiting, null, null);

    /// <summary>Commit a draft's payloads, manifest and publish record directly, bypassing staging.</summary>
    private static async Task PlantAsync(SiloHarness silo, SiloGroupDraft draft)
    {
        var qualification = new SiloQualification(silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256, true);
        var locators = new List<SiloExactLocator>();
        var published = new List<SiloPublishedMember>();
        foreach (var member in draft.Members)
        {
            var stream = await silo.Payloads.OpenAsync(member.SourceToken, CancellationToken.None).ConfigureAwait(false);
            byte[] payload;
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer).ConfigureAwait(false);
                payload = buffer.ToArray();
            }
            var key = SiloObjectNamespace.PayloadKey(member.RetentionClass, member.Sha256);
            var locator = silo.Local.Locator(key, silo.Local.Plant(key, payload));
            locators.Add(locator);
            published.Add(new(member.ArtifactId, member.ArtifactRevision, member.Length, member.Sha256, member.Facts().Sha256(), member.Required,
                locator, qualification));
        }
        var bytes = SiloJson.Serialize(SiloPublisher.BuildManifest(draft, silo.Binding, 1, locators));
        var sha = SiloHash.Sha256(bytes);
        var manifestKey = SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, sha);
        var version = silo.Local.Plant(manifestKey, bytes);
        await silo.AppendAsync(new SiloPublishRecord(SiloJson.CenteredViewGroupKind, draft.GroupId, draft.RevisionId,
            new(silo.Local.Locator(manifestKey, version), bytes.Length, sha, qualification), published, draft.PreviousRevisionId)).ConfigureAwait(false);
    }

    private static async Task AssertReadbackAsync(SiloHarness silo, string forgery, SiloGroupDraft draft)
    {
        var publication = (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        var reopen = await silo.Catalog.ReopenViewAsync(SiloTypedViews.StreamId("forged"), SiloTypedViews.ReferenceCapture("forged").ToString("D"),
            draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
        if (Accepted(forgery))
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

    private static SiloGroupDraft Forge(
        SiloGroupDraft draft, string forgery, CenteredView typed, SiloStackView stack, SiloViewProduct stars, SiloTestPayloads payloads)
    {
        var view = draft.View!;
        IReadOnlyList<SiloManifestDependency> Without(SiloViewProduct product) =>
            [.. draft.Dependencies.Where(dependency => dependency.ArtifactId != product.ArtifactId)];
        var withoutStars = view.Layers.Where(static layer => layer.Kind != "stars").ToArray();
        var other = SiloTypedViews.Evidence("forged-other");
        SiloManifestDependency Reopen(SiloViewProduct product, byte[] bytes, bool required) =>
            new(product.ArtifactId, product.Artifact.IdentitySha256, SiloHash.Sha256(bytes), SiloDependencyKind.Reopen, required);
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
                return draft with { View = view with { Preview = other.Binding } };
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
            case "alias-group":
                // Valid typed documents under an outer ID the store never issued for them.
                return draft with { GroupId = "base-forged" };
            case "alias-revision":
                return draft with { RevisionId = "rev-1" };
            case "unsorted-layers":
                {
                    // Valid Waiting layers in reverse order, hashed in that order, published under that matching ID. The store
                    // only ever writes and hashes its normalized order, so it never issued this identity.
                    CenteredViewLayer[] reversed = [Waiting("satellites"), Waiting("clouds")];
                    var members = new HashSet<(string, string)> { (stack.Preview.ArtifactId, stack.Preview.Artifact.IdentitySha256) };
                    var revision = typed.Revision with
                    {
                        RevisionId = SqliteCenteredViews.RevisionIdentity(typed.Base.BaseId, null, reversed),
                        Layers = reversed,
                        Manifest = LayeredPresentationJson.CreateManifest(stack.Definition.Preview.Product, sceneIdentitySha256: null, [])
                    };
                    return draft with
                    {
                        RevisionId = revision.RevisionId,
                        View = view with { Revision = SqliteCenteredViews.SerializeDocument(revision), Layers = SiloCenteredViews.Bindings(reversed) },
                        Dependencies = [.. SiloCenteredViews.ReopenDependencies(new CenteredView(typed.Base, revision), scienceDisplayed: true, members)]
                    };
                }
            case "member-dependency-alias":
                // The correct preview member, and a required reopen row for the same artifact revision with another digest.
                return draft with { Dependencies = [.. draft.Dependencies, Reopen(stack.Preview, other.Bytes, true)] };
            case "member-dependency-duplicate":
                // The same preview row twice: reopen would read two proofs for one artifact revision.
                return draft with { Dependencies = [.. draft.Dependencies, Reopen(stack.Preview, stack.Preview.Bytes, true)] };
            case "extra-reopen-dependency":
                // A reopen row the typed view does not imply.
                return draft with { Dependencies = [.. draft.Dependencies, Reopen(other, other.Bytes, false)] };
            case "extra-required-member":
            case "extra-optional-member":
                // A distinct member the typed view never references. Required, its loss would block exact reopen although
                // every typed proof still holds; optional, it is only data.
                return draft with
                {
                    Members = [.. draft.Members, SiloTypedViews.Evidence("forged-extra").Member(payloads, forgery == "extra-required-member")]
                };
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
