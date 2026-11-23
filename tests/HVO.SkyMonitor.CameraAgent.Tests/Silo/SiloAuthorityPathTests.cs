using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// Authority paths that must be proven positively (#1965 4.5, 5.3, 5.4, 6.1, 7.3). First visibility proves every
/// required reopen dependency's exact bytes through an authenticated publication; only the writer's own records,
/// its head, a trusted anchor or a witness certify a journal segment, never bytes that merely link backward; and a
/// copy is evicted only on proof under its own intent at or after its latest entry into the authority set.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloAuthorityPathTests
{
    private static string SegmentKey(long generation, long sequence) => SiloObjectNamespace.SegmentKey(new(generation, sequence));

    private static void AlterInPlace(SiloHarness silo, string key)
    {
        // Still well-formed and correctly linked to its predecessor; only its own bytes differ from what was committed.
        var node = JsonNode.Parse(silo.Local.Read(key, silo.Local.Current(key)!))!;
        node["writtenUtc"] = "2030-01-01T00:00:00+00:00";
        silo.Local.Delete(key, silo.Local.Current(key)!);
        silo.Local.Plant(key, Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    private static SiloGroupDraft ReadyStarsView(SiloHarness silo) =>
        SiloDrafts.View(silo.Payloads, "base-a", null, 1, [SiloDrafts.Layer("stars", "Ready", "stars", 4)], [], publishPreview: true);

    // ----- Required reopen dependencies at first visibility -----------------------------------------------------

    [TestMethod]
    public async Task AFirstP4NeverShowsAReadyViewUntilItsRequiredEvidenceIsPublished()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var view = ReadyStarsView(silo);

        var first = await silo.PublishAsync(view).ConfigureAwait(false);

        first.State.Should().Be(SiloStagedState.Recorded, "a required reopen dependency without an authenticated publication blocks first P4");
        first.CatalogOutcome.Should().Be(SiloDisposition.Unverified, "not concluded: the dependency may still be published");
        silo.Payloads.Released.Should().BeEmpty();

        await silo.PublishAsync(SiloDrafts.Evidence(silo.Payloads, "stars")).ConfigureAwait(false);
        (await silo.Index.ReadPublicationAsync(view.GroupId, view.RevisionId, CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible", "the dependency's publication requeues the waiting revision, so ordinary catch-up proves it");
        var retried = await silo.Publisher.PublishAsync(view.GroupId, view.RevisionId, CancellationToken.None).ConfigureAwait(false);

        retried.State.Should().Be(SiloStagedState.Visible, "the retry succeeds once the exact dependency is published and verifies");
        (await silo.Catalog.ReopenAsync(view.GroupId, view.RevisionId, CancellationToken.None).ConfigureAwait(false)).Reopenable.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("first P4", "deleted", "missing", DisplayName = "first P4: the dependency's only copy is gone, so the view is missing")]
    [DataRow("first P4", "damaged", "missing", DisplayName = "first P4: the dependency's only copy is damaged, so the view is missing")]
    [DataRow("first P4", "timeout", "temporarily-unavailable", DisplayName = "first P4: the dependency cannot be read, so the view is retried")]
    [DataRow("replayed first P4", "deleted", "missing", DisplayName = "a first P4 replayed after a rebuild reads the dependency too")]
    public async Task AFirstP4ReadsEveryRequiredDependencysExactBytes(string when, string fault, string expected)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var evidence = SiloDrafts.Evidence(silo.Payloads, "stars");
        await silo.PublishAsync(evidence).ConfigureAwait(false);
        var evidenceLocator = silo.PublishRecord(evidence.GroupId).Members.Single().Locator;
        var view = ReadyStarsView(silo);
        void Break()
        {
            switch (fault)
            {
                case "deleted":
                    silo.Local.Delete(evidenceLocator);
                    break;
                case "damaged":
                    silo.Local.Damage(evidenceLocator);
                    break;
                default:
                    silo.Local.ReadUnavailable = key => key == evidenceLocator.Key;
                    break;
            }
        }
        SiloPublicationOutcome? outcome = null;
        if (when == "first P4")
        {
            Break();
            outcome = await silo.PublishAsync(view).ConfigureAwait(false);
        }
        else
        {
            await silo.Publisher.StageAsync(view, CancellationToken.None).ConfigureAwait(false);
            silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
            var crash = () => silo.Publisher.PublishAsync(view.GroupId, view.RevisionId, CancellationToken.None).AsTask();
            await crash.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
            Break();
            await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        }

        var publication = (await silo.Index.ReadPublicationAsync(view.GroupId, view.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        publication.State.Should().Be("closed", "first visibility needs the required dependency's exact bytes now");
        publication.Outcome.Should().Be(expected);
        if (outcome is not null)
        {
            outcome.State.Should().Be(SiloStagedState.Recorded);
        }
        silo.Payloads.Released.Keys.Should().NotContain(view.Members.Select(static member => member.SourceToken));
    }

    [TestMethod]
    [DataRow("present", DisplayName = "a published, verified required dependency allows first P4")]
    [DataRow("optional", DisplayName = "an unpublished optional dependency never blocks first P4")]
    public async Task PresentOrOptionalDependenciesLeaveFirstVisibilityUnchanged(string dependency)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        SiloGroupDraft view;
        if (dependency == "present")
        {
            await silo.PublishAsync(SiloDrafts.Evidence(silo.Payloads, "stars")).ConfigureAwait(false);
            view = ReadyStarsView(silo);
        }
        else
        {
            view = SiloDrafts.View(silo.Payloads, "base-a", null, 1, [SiloDrafts.Layer("clouds", "Waiting", "clouds", 2)], [], publishPreview: true);
        }

        (await silo.PublishAsync(view).ConfigureAwait(false)).State.Should().Be(SiloStagedState.Visible);
    }

    [TestMethod]
    [DataRow("writer-proven", DisplayName = "history the writer proves reached first P4 rebuilds as settled, its bytes read lazily")]
    [DataRow("unproven", DisplayName = "a commit without the writer's first-P4 proof is verified like a first P4")]
    public async Task OnlyAWritersFirstP4ProofMakesRebuiltHistorySettled(string proof)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        if (proof == "writer-proven")
        {
            await silo.PublishAsync(draft).ConfigureAwait(false);
        }
        else
        {
            await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
            silo.Faults.Armed = SiloFaultPoint.AfterManifestQualified;
            var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
            await crash.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
            // The commit reaches the journal without the writer ever recording that its first P4 happened.
            var group = (await silo.State.ReadGroupAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
            var qualification = new SiloQualification(silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256, true);
            var manifest = silo.Local.Locator(
                SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, group.ManifestSha256!), group.ManifestVersionId!);
            var members = group.Members.Select(member => new SiloPublishedMember(member.Draft.ArtifactId, member.Draft.ArtifactRevision,
                member.Draft.Length, member.Draft.Sha256, member.Draft.Facts().Sha256(), member.Draft.Required,
                silo.Local.Locator(SiloObjectNamespace.PayloadKey(member.Draft.RetentionClass, member.Draft.Sha256), member.VersionId!),
                qualification)).ToArray();
            await silo.Journal.AppendAsync([new SiloPublishRecord(draft.GroupKind, draft.GroupId, draft.RevisionId,
                new(manifest, group.ManifestLength!.Value, group.ManifestSha256!, qualification), members, Capture: draft.Capture)],
                "commit-without-first-p4-proof", CancellationToken.None).ConfigureAwait(false);
        }
        var raw = new SiloRoleKey(SiloIdentityKind.Member, SiloDrafts.Raw(1), "1");
        silo.Local.Delete(silo.MemberLocator(draft.GroupId, raw.Id));

        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);

        var publication = (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        if (proof == "writer-proven")
        {
            publication.State.Should().Be("visible", "settled history rebuilds from its manifest; member bytes are read lazily");
            (await silo.Index.ReadRoleAsync(raw, CancellationToken.None).ConfigureAwait(false))!.Disposition.Should().Be(SiloDisposition.Unverified);
        }
        else
        {
            publication.State.Should().Be("closed", "absence of the writer's proof is not evidence that first P4 happened");
            publication.Outcome.Should().Be(SiloDisposition.Missing, "it is verified exactly as a first P4 would be");
        }
    }

    // ----- Positively proven journal prefix ----------------------------------------------------------------------

    [TestMethod]
    [DataRow("valid head", DisplayName = "the writer's head certifies the whole chain")]
    [DataRow("head lost, prior intact", DisplayName = "the head is lost; the writer's own record certifies the intact prior segment")]
    [DataRow("head lost, prior altered", DisplayName = "the head is lost; an altered prior segment that links back is never adopted")]
    public async Task AfterLosingItsHeadTheJournalKeepsOnlySegmentsTheWriterCertifies(string state)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        if (state != "valid head")
        {
            silo.Local.Delete(SegmentKey(1, 3), silo.Local.Current(SegmentKey(1, 3))!);
        }
        if (state == "head lost, prior altered")
        {
            AlterInPlace(silo, SegmentKey(1, 2));
        }

        var authority = (await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false)).Authority;

        var capture1 = await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(1), "r1", CancellationToken.None).ConfigureAwait(false);
        switch (state)
        {
            case "valid head":
                authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 3));
                authority.UnknownInterval.Should().BeFalse();
                break;
            case "head lost, prior intact":
                authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 2));
                authority.UnknownInterval.Should().BeTrue();
                capture1.Should().NotBeNull();
                break;
            default:
                authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 1), "bytes that only link backward certify nothing");
                authority.UnknownInterval.Should().BeTrue();
                capture1.Should().BeNull();
                break;
        }
    }

    [TestMethod]
    [DataRow("trusted anchor", DisplayName = "total loss: nothing past the trusted anchor's certified position is adopted")]
    [DataRow("altered tail", DisplayName = "total loss: a newest segment that only links backward is never adopted")]
    public async Task AfterTotalLossOnlyCertifiedSegmentsAreAuthority(string certifier)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        SiloAnchorRecord? anchor = null;
        if (certifier == "trusted anchor")
        {
            anchor = await new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock)
                .WriteAsync(CancellationToken.None).ConfigureAwait(false);
            await silo.AppendAsync(anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) }).ConfigureAwait(false);
            silo.Authority.TrustAnchors = true;
        }
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var head = (await silo.WriterEvidenceAsync().ConfigureAwait(false)).Head!.Value;
        if (certifier == "altered tail")
        {
            AlterInPlace(silo, SegmentKey(head.Generation, head.Sequence));
        }

        // Total loss: no writer State survives, so nothing local certifies any segment.
        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local)).ConfigureAwait(false);

        rebuilt.Authority.UnknownInterval.Should().BeTrue("nothing certifies the journal's newest segment");
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(2), "r1", CancellationToken.None).ConfigureAwait(false))
            .Should().BeNull("a publication no certifier reaches is authority-unproven, never adopted");
        rebuilt.Authority.UnprovenPublications.Select(static publication => publication.GroupId).Should().Contain(SiloDrafts.CaptureGroup(2));
        if (anchor is not null)
        {
            rebuilt.StartKind.Should().Be("anchor");
            rebuilt.Authority.PrefixEnd.Should().Be(anchor.Covers, "the trusted anchor certifies its covered position and nothing after it");
        }
    }

    [TestMethod]
    [DataRow(true, DisplayName = "total loss: history the trusted anchor records as reaching P4 stays visible while a member read times out")]
    [DataRow(false, DisplayName = "total loss: a P3 the trusted anchor does not record as reaching P4 stays fenced while a member read times out")]
    public async Task ATrustedAnchorKeepsTheHistoryItRecordsAsReachingP4BrowsableAfterTotalLoss(bool reachedP4)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        var raw = new SiloRoleKey(SiloIdentityKind.Member, SiloDrafts.Raw(1), "1");
        string RawKey() => silo.MemberLocator(draft.GroupId, raw.Id).Key;
        if (reachedP4)
        {
            await silo.PublishAsync(draft).ConfigureAwait(false);
        }
        else
        {
            // P3 commits; the raw read times out before anything can verify it, so the revision never reaches P4.
            await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
            silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
            var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
            await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
            silo.Local.ReadUnavailable = key => key == RawKey();
            await silo.CatchUpAsync().ConfigureAwait(false);
        }
        (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be(reachedP4 ? "visible" : "closed");
        var anchor = await new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock)
            .WriteAsync(CancellationToken.None).ConfigureAwait(false);
        await silo.AppendAsync(anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) }).ConfigureAwait(false);
        silo.Authority.TrustAnchors = true;
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        // Total loss: no writer State survives. The raw read times out; the manifest and the preview still read.
        silo.Local.ReadUnavailable = key => key == RawKey();

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local)).ConfigureAwait(false);

        rebuilt.StartKind.Should().Be("anchor");
        rebuilt.Authority.PrefixEnd.Should().Be(anchor.Covers);
        async Task<IReadOnlyList<SiloIndexedPublication>> BrowseAsync() =>
            await silo.Index.QueryVisibleAsync(SiloHarness.Start.AddDays(-1), SiloHarness.Start.AddDays(2), null, 10, CancellationToken.None)
                .ConfigureAwait(false);
        var publication = (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        if (reachedP4)
        {
            publication.State.Should().Be("visible", "history rebuilds from its verified manifest; member bytes wait for the lazy phase");
            (await BrowseAsync().ConfigureAwait(false)).Select(static item => item.GroupId).Should().Contain(draft.GroupId);
            (await silo.Index.ReadRoleAsync(raw, CancellationToken.None).ConfigureAwait(false))!.Disposition
                .Should().Be(SiloDisposition.Unverified, "every history role starts unverified");

            await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);

            (await silo.Index.ReadRoleAsync(raw, CancellationToken.None).ConfigureAwait(false))!.Disposition
                .Should().Be(SiloDisposition.TemporarilyUnavailable, "the lazy phase reports the timeout on the role");
            (await BrowseAsync().ConfigureAwait(false)).Select(static item => item.GroupId)
                .Should().Contain(draft.GroupId, "a role the lazy phase cannot reach never hides published history");
        }
        else
        {
            publication.State.Should().Be("closed", "a P3 that never reached P4 is verified as its first P4 would be");
            publication.Outcome.Should().Be(SiloDisposition.TemporarilyUnavailable);
            (await BrowseAsync().ConfigureAwait(false)).Select(static item => item.GroupId).Should().NotContain(draft.GroupId);
        }
    }

    [TestMethod]
    [DataRow(true, DisplayName = "total loss: a P4 the writer proved stays in the checkpoint through a manifest outage and keeps the revision visible")]
    [DataRow(false, DisplayName = "total loss: a P3 awaiting its first P4 through the same manifest outage stays fenced")]
    public async Task AManifestOutageNeverErasesProofOfP4FromTheCheckpoint(bool reachedP4)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        var raw = new SiloRoleKey(SiloIdentityKind.Member, SiloDrafts.Raw(1), "1");
        if (reachedP4)
        {
            await silo.PublishAsync(draft).ConfigureAwait(false);
        }
        else
        {
            await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
            silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
            var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
            await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
        }
        var manifestKey = silo.ManifestLocator(draft.GroupId).Key;
        var rawKey = silo.MemberLocator(draft.GroupId, raw.Id).Key;
        // The catalog is rebuilt with the writer's surviving State while the manifest read times out.
        silo.Local.ReadUnavailable = key => key == manifestKey;
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        var during = (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        during.State.Should().Be("closed");
        during.Outcome.Should().Be(SiloDisposition.TemporarilyUnavailable);
        var anchor = await new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock)
            .WriteAsync(CancellationToken.None).ConfigureAwait(false);
        await silo.AppendAsync(anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) }).ConfigureAwait(false);
        silo.Authority.TrustAnchors = true;
        // Total loss: no writer State survives. The manifest reads again; the raw read now times out.
        silo.Local.ReadUnavailable = key => key == rawKey;

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local)).ConfigureAwait(false);

        rebuilt.StartKind.Should().Be("anchor");
        var publication = (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        var browse = await silo.Index.QueryVisibleAsync(SiloHarness.Start.AddDays(-1), SiloHarness.Start.AddDays(2), null, 10,
            CancellationToken.None).ConfigureAwait(false);
        if (reachedP4)
        {
            publication.State.Should().Be("visible", "proof of P4 does not depend on the revision being visible when the checkpoint was written");
            browse.Select(static item => item.GroupId).Should().Contain(draft.GroupId);
            (await silo.Index.ReadRoleAsync(raw, CancellationToken.None).ConfigureAwait(false))!.Disposition.Should().Be(SiloDisposition.Unverified);
        }
        else
        {
            publication.State.Should().Be("closed", "nothing proves this revision ever reached P4");
            publication.Outcome.Should().Be(SiloDisposition.TemporarilyUnavailable);
            browse.Select(static item => item.GroupId).Should().NotContain(draft.GroupId);
        }
    }

    [TestMethod]
    [DataRow("this cursor's records", DisplayName = "a checkpoint retry at an unchanged cursor anchors the checkpoint that landed, with its earlier P4 proof")]
    [DataRow("response lost", DisplayName = "a checkpoint whose write landed but lost its response is anchored by the retry after a P4 at the same cursor")]
    [DataRow("response lost at the bound", DisplayName = "a landed checkpoint at exactly the size bound is anchored although P4 growth puts the retry over it")]
    [DataRow("landed over the bound", DisplayName = "a landed checkpoint over the size bound is never anchored; the retry is refused as too large")]
    [DataRow("other records", DisplayName = "a key holding a checkpoint of other records at this cursor is still a conflict")]
    [DataRow("other records at the bound", DisplayName = "a checkpoint of other records within the bound stays a conflict when the retry's snapshot is over the bound")]
    public async Task ACheckpointRetryAtAnUnchangedCursorAnchorsTheCheckpointThatLanded(string landed)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        string RawKey() => silo.MemberLocator(draft.GroupId, SiloDrafts.Raw(1)).Key;
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
        var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
        silo.Local.ReadUnavailable = key => key == RawKey();
        await silo.CatchUpAsync().ConfigureAwait(false);
        long? bound = null;
        SiloCheckpointWriter Writer() => bound is { } maximum
            ? new(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock, maximum)
            : new(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock);
        async Task<int> SnapshotLengthAsync()
        {
            var (covers, coversSha, entries, reachedP4) = await silo.Index.ExportRecordsAsync(CancellationToken.None).ConfigureAwait(false);
            return SiloJson.Serialize(new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, silo.Binding.InstanceId, silo.Binding.BindingId,
                covers, coversSha, entries, reachedP4)).Length;
        }
        var key = SiloObjectNamespace.CheckpointKey(new(1, 2));
        // The staged revision keeps its spooled bytes until the writer's own first P4; checkpoint attempts add none.
        var spooled = silo.StateRoot.Spool.Names();
        SiloAnchorRecord? first = null;
        string? landedVersion = null;
        if (landed is "this cursor's records" or "landed over the bound")
        {
            // Written while the revision is closed on a raw timeout; the process stops before its anchor is recorded.
            first = await Writer().WriteAsync(CancellationToken.None).ConfigureAwait(false);
            landedVersion = first.Checkpoint.VersionId;
        }
        else if (landed is "response lost" or "response lost at the bound")
        {
            if (landed == "response lost at the bound")
            {
                // The first attempt is exactly as large as the readers accept.
                bound = await SnapshotLengthAsync().ConfigureAwait(false);
            }
            // The PUT lands, but the writer sees a timeout and never learns the version.
            silo.Local.FailAfterLanding = candidate => candidate == key;
            var uncertain = () => Writer().WriteAsync(CancellationToken.None).AsTask();
            (await uncertain.Should().ThrowAsync<SiloStoreUnavailableException>().ConfigureAwait(false)).Which.WriteMayHaveLanded.Should().BeTrue();
            silo.Local.FailAfterLanding = null;
            landedVersion = silo.Local.Current(key);
        }
        else
        {
            var (covers, coversSha, entries, _) = await silo.Index.ExportRecordsAsync(CancellationToken.None).ConfigureAwait(false);
            var node = JsonNode.Parse(SiloJson.Serialize(new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, silo.Binding.InstanceId,
                silo.Binding.BindingId, covers, coversSha, entries, [])))!;
            node["entries"]![0]!["record"]!["identityFloors"]!["capture-sequence"] = 7;
            var other = Encoding.UTF8.GetBytes(node.ToJsonString());
            silo.Local.Plant(key, other);
            if (landed == "other records at the bound")
            {
                // The object at the key fits the bound; only the regenerated snapshot exceeds it.
                bound = other.Length;
            }
        }
        silo.Local.ReadUnavailable = null;
        await silo.Catalog.RetryClosedAsync(CancellationToken.None).ConfigureAwait(false);
        (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible", "the retry brings the revision to P4 at the same journal cursor");
        if (bound is { } limit)
        {
            (await SnapshotLengthAsync().ConfigureAwait(false)).Should().BeGreaterThan((int)limit, "the P4 now on the list grows the snapshot past the bound");
        }

        if (landed == "landed over the bound")
        {
            // Readers would refuse the checkpoint that landed, so the bound applies to it as to a new write.
            bound = first!.CheckpointLength - 1;
        }
        var retry = () => Writer().WriteAsync(CancellationToken.None).AsTask();
        if (landed == "landed over the bound")
        {
            await retry.Should().ThrowAsync<SiloCheckpointTooLargeException>().ConfigureAwait(false);
            return;
        }

        if (landedVersion is null)
        {
            (await retry.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.create-only-conflict");
            return;
        }
        var anchor = await retry().ConfigureAwait(false);
        anchor.Checkpoint.VersionId.Should().Be(landedVersion, "the checkpoint that landed is this cursor's checkpoint; it is anchored, never replaced");
        var bytes = silo.Local.Read(key, landedVersion);
        anchor.CheckpointLength.Should().Be(bytes.Length);
        anchor.CheckpointSha256.Should().Be(SiloHash.Sha256(bytes));
        SiloJson.ReadCheckpoint(bytes, "landed checkpoint").ReachedP4.Should().BeEmpty("it was written before the revision reached P4");
        if (first is not null)
        {
            anchor.Should().Be(first);
        }
        silo.Local.VersionsOf(key).Should().ContainSingle();
        silo.StateRoot.Spool.Names().Should().BeEquivalentTo(spooled, "no checkpoint attempt leaves its spooled bytes behind");
        await silo.AppendAsync(anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) }).ConfigureAwait(false);
        silo.Authority.TrustAnchors = true;
        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local)).ConfigureAwait(false);
        rebuilt.StartKind.Should().Be("anchor");
        (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible", "without P4 proof in the landed checkpoint it is verified as a first P4 would be, and its bytes read");
    }

    private static async Task<SiloAnchorRecord> AnchorAsync(SiloHarness silo, string seal)
    {
        var anchor = await new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock)
            .WriteAsync(CancellationToken.None).ConfigureAwait(false);
        await silo.AppendAsync(anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = seal }) }).ConfigureAwait(false);
        silo.Authority.TrustAnchors = true;
        return anchor;
    }

    [TestMethod]
    public async Task ProofOfP4OutlivesAManifestOutageAcrossSuccessiveAnchors()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        var raw = new SiloRoleKey(SiloIdentityKind.Member, SiloDrafts.Raw(1), "1");
        await silo.PublishAsync(draft).ConfigureAwait(false);
        var manifestKey = silo.ManifestLocator(draft.GroupId).Key;
        var rawKey = silo.MemberLocator(draft.GroupId, raw.Id).Key;
        var first = await AnchorAsync(silo, "sealed-1").ConfigureAwait(false);
        // The catalog is rebuilt from the first anchor while the manifest read times out, which closes the revision.
        silo.Local.ReadUnavailable = key => key == manifestKey;
        (await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false)).Anchor.Should().Be(first.Covers);
        (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!.Outcome
            .Should().Be(SiloDisposition.TemporarilyUnavailable);
        var second = await AnchorAsync(silo, "sealed-2").ConfigureAwait(false);
        second.Covers.Should().NotBe(first.Covers);
        // Total loss: no writer State survives. The manifest reads again; the raw read times out.
        silo.Local.ReadUnavailable = key => key == rawKey;

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local)).ConfigureAwait(false);

        rebuilt.Anchor.Should().Be(second.Covers, "the newest trusted anchor starts the rebuild");
        (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible", "the second anchor carries the proof of P4 through the outage that closed the revision when it was written");
        (await silo.Index.QueryVisibleAsync(SiloHarness.Start.AddDays(-1), SiloHarness.Start.AddDays(2), null, 10, CancellationToken.None)
            .ConfigureAwait(false)).Select(static item => item.GroupId).Should().Contain(draft.GroupId);
        (await silo.Index.ReadRoleAsync(raw, CancellationToken.None).ConfigureAwait(false))!.Disposition.Should().Be(SiloDisposition.Unverified);
    }

    [TestMethod]
    [DataRow("no group identity", DisplayName = "a newest checkpoint whose publish entry has no group identity is passed over for the older trusted anchor")]
    [DataRow("null member", DisplayName = "a newest checkpoint whose publish entry holds a null member is passed over for the older trusted anchor")]
    [DataRow("null entry", DisplayName = "a newest checkpoint holding a null entry is passed over for the older trusted anchor")]
    [DataRow("short entries", DisplayName = "a newest checkpoint whose records stop before its covered position is passed over")]
    [DataRow("well-formed", DisplayName = "a well-formed newest checkpoint starts the rebuild")]
    public async Task AMalformedNewestCheckpointFallsBackToTheOlderTrustedAnchor(string fault)
    {
        var malformed = fault != "well-formed";
        var shortEntries = fault == "short entries";
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var first = await AnchorAsync(silo, "sealed-1").ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        SiloAnchorRecord second;
        if (malformed)
        {
            // Its bytes match the sealed length and digest; only its content is malformed.
            var (covers, coversSha, entries, reachedP4) = await silo.Index.ExportRecordsAsync(CancellationToken.None).ConfigureAwait(false);
            var node = JsonNode.Parse(SiloJson.Serialize(new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, silo.Binding.InstanceId,
                silo.Binding.BindingId, covers, coversSha, entries, reachedP4)))!;
            var array = node["entries"]!.AsArray();
            var publish = array.Last(static entry => (string?)entry!["record"]!["type"] == "publish")!["record"]!;
            switch (fault)
            {
                case "no group identity":
                    publish.AsObject().Remove("groupId");
                    break;
                case "null member":
                    publish["members"]![0] = null;
                    break;
                case "short entries":
                    while ((long)array[^1]!["sequence"]! == covers.Sequence)
                    {
                        array.RemoveAt(array.Count - 1);
                    }
                    node["reachedP4"]!.AsArray().RemoveAt(reachedP4.Count - 1);
                    break;
                default:
                    array[array.Count - 1] = null;
                    break;
            }
            var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
            var key = SiloObjectNamespace.CheckpointKey(covers);
            second = new(silo.Local.Locator(key, silo.Local.Plant(key, bytes)), bytes.Length, SiloHash.Sha256(bytes), covers, coversSha);
            await silo.AppendAsync(second with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-2" }) }).ConfigureAwait(false);
        }
        else
        {
            second = await AnchorAsync(silo, "sealed-2").ConfigureAwait(false);
        }

        var writer = shortEntries ? await silo.WriterEvidenceAsync().ConfigureAwait(false) : null;
        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local, Writer: writer)).ConfigureAwait(false);

        rebuilt.StartKind.Should().Be("anchor");
        rebuilt.Anchor.Should().Be(malformed ? first.Covers : second.Covers);
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(1), "r1", CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible");
        if (shortEntries)
        {
            (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(2), "r1", CancellationToken.None).ConfigureAwait(false))!.State
                .Should().Be("visible", "the journal replays the publication omitted by the rejected checkpoint");
        }
    }

    [TestMethod]
    [DataRow("unproven", DisplayName = "a landed checkpoint claiming a P4 the catalog cannot prove is a conflict, never anchored")]
    [DataRow("unproven at the bound", DisplayName = "a landed checkpoint within the bound claiming an unproven P4 stays a conflict when the retry's snapshot is over the bound")]
    [DataRow("proven", DisplayName = "a landed checkpoint whose shorter P4 list the catalog proves is anchored by the retry")]
    public async Task ACheckpointRetryAnchorsOnlyP4ClaimsTheCatalogProves(string landed)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var proven = landed == "proven";
        if (landed != "unproven")
        {
            await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        }
        if (landed == "unproven at the bound")
        {
            // A third capture also reaches P4, so the catalog's own list is longer than the landed one.
            await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 3)).ConfigureAwait(false);
        }
        // B commits P3, then its raw read times out, so nothing proves it reached P4.
        var draft = SiloDrafts.Capture(silo.Payloads, 2);
        string RawKey() => silo.MemberLocator(draft.GroupId, SiloDrafts.Raw(2)).Key;
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
        var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
        silo.Local.ReadUnavailable = key => key == RawKey();
        await silo.CatchUpAsync().ConfigureAwait(false);
        var (covers, coversSha, entries, reachedP4) = await silo.Index.ExportRecordsAsync(CancellationToken.None).ConfigureAwait(false);
        // The checkpoint already at the key holds exactly these records. Proven: an earlier attempt's list, with only A.
        // Unproven: a list that claims B reached P4.
        IReadOnlyList<SiloCheckpointPublication> claims = proven ? reachedP4 : [new(draft.GroupId, draft.RevisionId)];
        if (proven)
        {
            claims.Should().Equal(new SiloCheckpointPublication(SiloDrafts.CaptureGroup(1), "r1"));
        }
        var bytes = SiloJson.Serialize(new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, silo.Binding.InstanceId, silo.Binding.BindingId,
            covers, coversSha, entries, claims));
        var key = SiloObjectNamespace.CheckpointKey(covers);
        var version = silo.Local.Plant(key, bytes);
        if (proven)
        {
            silo.Local.ReadUnavailable = null;
            await silo.Catalog.RetryClosedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        long bound = SiloJson.MaximumMetadataObjectBytes;
        if (landed == "unproven at the bound")
        {
            bound = bytes.Length;
            SiloJson.Serialize(new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, silo.Binding.InstanceId, silo.Binding.BindingId, covers,
                coversSha, entries, reachedP4)).Length.Should().BeGreaterThan(bytes.Length, "the retry's own snapshot is over the bound");
        }

        var retry = () => new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock, bound)
            .WriteAsync(CancellationToken.None).AsTask();

        if (!proven)
        {
            (await retry.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code
                .Should().Be("silo.create-only-conflict", "a retry never anchors a claim of P4 the catalog cannot prove");
            (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!.State
                .Should().Be("closed");
            return;
        }
        (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible", "B reached P4 at the same cursor after the earlier attempt landed");
        var anchor = await retry().ConfigureAwait(false);
        anchor.Checkpoint.VersionId.Should().Be(version, "every claim the landed checkpoint makes is proven; it only settles less");
        anchor.CheckpointSha256.Should().Be(SiloHash.Sha256(bytes));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "a checkpoint on a store that is not persistence-qualified is refused before it is written")]
    [DataRow(true, DisplayName = "a landed checkpoint on a store that is not persistence-qualified is never anchored, even past the size bound")]
    public async Task ACheckpointIsNeverAnchoredOnAStoreThatIsNotPersistenceQualified(bool atTheBound)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        string RawKey() => silo.MemberLocator(draft.GroupId, SiloDrafts.Raw(1)).Key;
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
        var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
        silo.Local.ReadUnavailable = key => key == RawKey();
        await silo.CatchUpAsync().ConfigureAwait(false);
        // A matching checkpoint landed, its response lost, on a store whose profile is not persistence-qualified.
        var (covers, coversSha, entries, reachedP4) = await silo.Index.ExportRecordsAsync(CancellationToken.None).ConfigureAwait(false);
        var bytes = SiloJson.Serialize(new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, silo.Binding.InstanceId, silo.Binding.BindingId,
            covers, coversSha, entries, reachedP4));
        var key = SiloObjectNamespace.CheckpointKey(covers);
        var unqualified = new SiloTestStore(silo.Local.Descriptor.StoreId, silo.Local.Descriptor.Bucket, qualified: false);
        unqualified.Plant(key, bytes);
        // P4 then grows the snapshot at the same cursor.
        silo.Local.ReadUnavailable = null;
        await silo.Catalog.RetryClosedAsync(CancellationToken.None).ConfigureAwait(false);
        long bound = atTheBound ? bytes.Length : SiloJson.MaximumMetadataObjectBytes;

        var write = () => new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, unqualified, silo.Binding, silo.Clock, bound)
            .WriteAsync(CancellationToken.None).AsTask();

        await write.Should().ThrowAsync<SiloStoreNotQualifiedException>("a store whose versions a power cut can lose never holds authority")
            .ConfigureAwait(false);
        unqualified.VersionsOf(key).Should().ContainSingle();
    }

    [TestMethod]
    [DataRow(false, DisplayName = "an oversized occupant is bounded when the snapshot also exceeds the limit")]
    [DataRow(true, DisplayName = "an oversized occupant is bounded when the snapshot fits the limit")]
    public async Task AnOversizedObjectAtTheCheckpointKeyIsReadOnlyToItsBound(bool snapshotFits)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var (covers, coversSha, entries, reachedP4) = await silo.Index.ExportRecordsAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = SiloJson.Serialize(new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, silo.Binding.InstanceId, silo.Binding.BindingId,
            covers, coversSha, entries, reachedP4)).Length;
        var key = SiloObjectNamespace.CheckpointKey(covers);
        var oversized = new byte[1024 * 1024];
        Array.Fill(oversized, (byte)' ');
        silo.Local.Plant(key, oversized);
        long bound = snapshotFits ? snapshot : snapshot - 1;

        var write = () => new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock, bound)
            .WriteAsync(CancellationToken.None).AsTask();

        if (snapshotFits)
        {
            (await write.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.create-only-conflict");
        }
        else
        {
            await write.Should().ThrowAsync<SiloCheckpointTooLargeException>("nothing within the bound is at the key").ConfigureAwait(false);
        }
        silo.Local.BytesServed(key).Should().Be(bound + 1, "the entire attempt stops at the first byte past the bound");
    }

    [TestMethod]
    [DataRow(long.MaxValue)]
    [DataRow(0L)]
    public async Task CheckpointWriteLimitsAreValidatedBeforeAccessingStorage(long bound)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        silo.Local.Unreachable = true;
        var write = () => new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock, bound)
            .WriteAsync(CancellationToken.None).AsTask();
        (await write.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(false)).Which.ParamName.Should().Be("maximumBytes");
    }

    [TestMethod]
    [DataRow(long.MaxValue)]
    [DataRow(0L)]
    public async Task CheckpointRebuildLimitsAreValidatedBeforeAccessingStorage(long bound)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        silo.Local.Unreachable = true;
        var rebuild = () => silo.RebuildAsync(new(silo.Local, MaximumCheckpointBytes: bound));

        (await rebuild.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(false)).Which.ParamName.Should().Be("MaximumCheckpointBytes");
    }

    [TestMethod]
    [DataRow("null record", DisplayName = "a segment holding a null record ends the authenticated prefix as invalid instead of stopping the rebuild")]
    [DataRow("scalar record", DisplayName = "a segment holding a record that is not an object ends the authenticated prefix as invalid")]
    [DataRow("null publish member", DisplayName = "a segment whose publish record holds a null member ends the authenticated prefix as invalid")]
    public async Task AMalformedJournalRecordEndsTheAuthenticatedPrefixAsInvalid(string fault)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var anchor = await AnchorAsync(silo, "sealed-1").ConfigureAwait(false);
        var head = await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false);
        var next = new SiloJournalSegment(SiloFormats.JournalSegment, silo.Binding.InstanceId, silo.Binding.BindingId, 1, head.Sequence + 1,
            SiloHarness.Start, [silo.PublishRecord(SiloDrafts.CaptureGroup(1))], head.Position, head.Sha256);
        var node = JsonNode.Parse(SiloJson.Serialize(next))!;
        switch (fault)
        {
            case "null record":
                node["records"]![0] = null;
                break;
            case "scalar record":
                node["records"]![0] = 5;
                break;
            default:
                node["records"]![0]!["members"]![0] = null;
                break;
        }
        silo.Local.Plant(SegmentKey(1, head.Sequence + 1), Encoding.UTF8.GetBytes(node.ToJsonString()));

        // Total loss: the rebuild starts at the trusted anchor and reads on until the malformed segment.
        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local)).ConfigureAwait(false);

        rebuilt.Anchor.Should().Be(anchor.Covers);
        rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.Invalid, "malformed data ends the prefix; only an unknown record type is refused");
        rebuilt.Authority.PrefixEnd.Should().Be(anchor.Covers);
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(1), "r1", CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible");
    }

    // ----- Fresh, same-intent proof for a copy eviction -----------------------------------------------------------

    [TestMethod]
    [DataRow("stale after restoration", false, DisplayName = "an eviction resting on absence proven before the copy's restoration is refused")]
    [DataRow("no intent named", true, DisplayName = "an eviction naming no intent is refused even after its intent's fresh proof evicted the copy")]
    [DataRow("presence after stale absence", false, DisplayName = "after restoration, a same-intent presence outcome stands and a stale eviction is refused")]
    [DataRow("other intent", true, DisplayName = "an eviction under an intent that proved nothing is refused though another's fresh proof evicted the copy")]
    [DataRow("fresh same intent", true, DisplayName = "an eviction on fresh absence proven under the same intent stands")]
    public async Task ACopyEvictionNeedsFreshProofUnderItsOwnIntent(string after, bool evicted)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(1));
        var copy = silo.ReplicateToArchive(raw, silo.MemberLocator(capture.GroupId, raw.Id));
        await silo.AppendAsync(copy).ConfigureAwait(false);
        var now = silo.Clock.GetUtcNow();
        // X evicts the archive copy; the copy is later restored to the authority set.
        await silo.AppendAsync(new SiloExpiryIntentRecord("evict-x", SiloExpiryScope.Copy, raw, "operator eviction", [new(copy.Locator, "archive")],
            "headroom", LastCopy: false)).ConfigureAwait(false);
        await silo.AppendAsync(
            new SiloExpiryDispositionRecord("evict-x", copy.Locator, SiloExpiryOutcome.Deleted, now, "NoSuchVersion"),
            new SiloAvailabilityRecord(SiloAvailabilityScope.Copy, raw, SiloAvailabilityState.Evicted, now, "proven absence", copy.Locator, "evict-x"))
            .ConfigureAwait(false);
        await silo.AppendAsync(new SiloAvailabilityRecord(SiloAvailabilityScope.Copy, raw, SiloAvailabilityState.Restored, now, "found again",
            copy.Locator)).ConfigureAwait(false);
        SiloAvailabilityRecord Evict(string? intent) =>
            new(SiloAvailabilityScope.Copy, raw, SiloAvailabilityState.Evicted, now, "evicted", copy.Locator, intent);
        switch (after)
        {
            case "stale after restoration":
                await silo.AppendAsync(Evict("evict-x")).ConfigureAwait(false);
                break;
            case "no intent named":
                // X's fresh proof evicts the copy itself; an eviction record naming no intent is still refused.
                await silo.AppendAsync(new SiloExpiryDispositionRecord("evict-x", copy.Locator, SiloExpiryOutcome.AlreadyAbsent, now, "NoSuchVersion"))
                    .ConfigureAwait(false);
                await silo.AppendAsync(Evict(null)).ConfigureAwait(false);
                break;
            case "presence after stale absence":
                await silo.AppendAsync(new SiloExpiryDispositionRecord("evict-x", copy.Locator, SiloExpiryOutcome.NotDeleted, now, "present"))
                    .ConfigureAwait(false);
                await silo.AppendAsync(Evict("evict-x")).ConfigureAwait(false);
                break;
            case "other intent":
                // X's fresh proof evicts the copy itself; Y names the copy but proved nothing, so its eviction is refused.
                await silo.AppendAsync(new SiloExpiryIntentRecord("evict-y", SiloExpiryScope.Copy, raw, "operator eviction",
                    [new(copy.Locator, "archive")], "headroom", LastCopy: false)).ConfigureAwait(false);
                await silo.AppendAsync(new SiloExpiryDispositionRecord("evict-x", copy.Locator, SiloExpiryOutcome.AlreadyAbsent, now, "NoSuchVersion"))
                    .ConfigureAwait(false);
                await silo.AppendAsync(Evict("evict-y")).ConfigureAwait(false);
                break;
            default:
                await silo.AppendAsync(new SiloExpiryDispositionRecord("evict-x", copy.Locator, SiloExpiryOutcome.AlreadyAbsent, now, "NoSuchVersion"))
                    .ConfigureAwait(false);
                await silo.AppendAsync(Evict("evict-x")).ConfigureAwait(false);
                break;
        }
        var role = new SiloRoleKey(SiloIdentityKind.Member, raw.Id, raw.Revision);

        foreach (var pass in new[] { "replayed", "rebuilt" })
        {
            if (pass == "rebuilt")
            {
                await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
            }
            var state = (await silo.Index.ReadRoleAsync(role, CancellationToken.None).ConfigureAwait(false))!.Locators
                .Single(locator => locator.Locator == copy.Locator).CopyState;
            state.Should().Be(evicted ? "evicted" : "active", $"{pass}: only fresh absence proven under the copy's own intent evicts it");
            var described = await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false);
            described.Should().NotContain("contradicts-proven-absence",
                $"{pass}: absence proven before the copy's restoration is stale, so a later presence outcome is recorded, not rejected");
            var refusal = after switch
            {
                "no intent named" => "eviction-without-intent",
                "fresh same intent" => null,
                _ => "eviction-without-fresh-proven-absence",
            };
            foreach (var reason in new[] { "eviction-without-intent", "eviction-without-fresh-proven-absence" })
            {
                if (reason == refusal)
                {
                    described.Should().Contain(reason, $"{pass}: the eviction is refused for its own reason");
                }
                else
                {
                    described.Should().NotContain(reason, $"{pass}: the eviction is not refused for another reason");
                }
            }
        }
    }
}
