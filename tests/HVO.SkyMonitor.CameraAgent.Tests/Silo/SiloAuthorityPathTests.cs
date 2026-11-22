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
    [DataRow("other records", DisplayName = "a key holding a checkpoint of other records at this cursor is still a conflict")]
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
        SiloCheckpointWriter Writer() => new(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock);
        var key = SiloObjectNamespace.CheckpointKey(new(1, 2));
        SiloAnchorRecord? first = null;
        if (landed == "this cursor's records")
        {
            // Written while the revision is closed on a raw timeout; the process stops before its anchor is recorded.
            first = await Writer().WriteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            var (covers, coversSha, entries, _) = await silo.Index.ExportRecordsAsync(CancellationToken.None).ConfigureAwait(false);
            var node = JsonNode.Parse(SiloJson.Serialize(new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, silo.Binding.InstanceId,
                silo.Binding.BindingId, covers, coversSha, entries, [])))!;
            node["entries"]![0]!["record"]!["identityFloors"]!["capture-sequence"] = 7;
            silo.Local.Plant(key, Encoding.UTF8.GetBytes(node.ToJsonString()));
        }
        silo.Local.ReadUnavailable = null;
        await silo.Catalog.RetryClosedAsync(CancellationToken.None).ConfigureAwait(false);
        (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible", "the retry brings the revision to P4 at the same journal cursor");

        var retry = () => Writer().WriteAsync(CancellationToken.None).AsTask();

        if (first is null)
        {
            (await retry.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.create-only-conflict");
            return;
        }
        var anchor = await retry().ConfigureAwait(false);
        anchor.Should().Be(first, "the checkpoint that landed is this cursor's checkpoint; it is anchored, never replaced");
        silo.Local.VersionsOf(key).Should().ContainSingle();
        await silo.AppendAsync(anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) }).ConfigureAwait(false);
        silo.Authority.TrustAnchors = true;
        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local)).ConfigureAwait(false);
        rebuilt.StartKind.Should().Be("anchor");
        (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible", "without P4 proof in the landed checkpoint it is verified as a first P4 would be, and its bytes read");
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
