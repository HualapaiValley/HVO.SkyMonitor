using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// Publication boundaries (#1965 5.2, 5.3; #1967 acceptance 1 and 3): payloads before the manifest, the manifest before
/// the publish record, the publish record before browsing. Crashes, uncertain writes and conflicting bytes are injected
/// at each boundary against real SQLite and a generated versioned bucket.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloPublicationTests
{
    private static readonly DateTimeOffset Day = SiloHarness.Start;

    [TestMethod]
    public async Task APublishedCaptureNamesOnlyQualifiedLocatorsAndBecomesVisibleOnce()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);

        var outcome = await silo.PublishAsync(draft).ConfigureAwait(false);

        outcome.State.Should().Be(SiloStagedState.Visible);
        outcome.CatalogState.Should().Be("visible");
        var publish = silo.PublishRecord(draft.GroupId);
        publish.Capture.Should().Be(new SiloCaptureSequence("agent-0001", 1));
        foreach (var member in publish.Members)
        {
            member.Qualification.ProfileQualified.Should().BeTrue();
            member.Qualification.Method.Should().Be(SiloFormats.ExactGetSha256);
            SiloHash.Sha256(silo.Local.Read(member.Locator.Key, member.Locator.VersionId)).Should().Be(member.Sha256);
        }
        var manifestBytes = silo.Local.Read(publish.Manifest.Locator.Key, publish.Manifest.Locator.VersionId);
        SiloHash.Sha256(manifestBytes).Should().Be(publish.Manifest.Sha256);
        var manifest = SiloJson.ReadManifest(manifestBytes, "manifest");
        manifest.Members.Select(static member => member.Locator).Should().Equal(publish.Members.Select(static member => member.Locator));
        manifest.StorageBindingId.Should().Be(silo.Binding.BindingId);
        silo.Payloads.Released.Keys.Should().BeEquivalentTo(draft.Members.Select(static member => member.SourceToken));
        silo.StateRoot.Spool.Names().Should().BeEmpty();
        var browse = await silo.Index.QueryVisibleAsync(Day, Day.AddHours(1), "camera-1", 10, CancellationToken.None).ConfigureAwait(false);
        browse.Should().ContainSingle().Which.Capture.Should().Be(new SiloCaptureSequence("agent-0001", 1));
    }

    [TestMethod]
    [DataRow(nameof(SiloFaultPoint.AfterPayloadPut), false, false, false)]
    [DataRow(nameof(SiloFaultPoint.AfterPayloadVerified), false, false, false)]
    [DataRow(nameof(SiloFaultPoint.AfterManifestSpooled), false, false, false)]
    [DataRow(nameof(SiloFaultPoint.AfterManifestPut), false, false, false)]
    [DataRow(nameof(SiloFaultPoint.AfterManifestQualified), false, false, false)]
    [DataRow(nameof(SiloFaultPoint.AfterSegmentSpooled), false, false, false)]
    [DataRow(nameof(SiloFaultPoint.AfterSegmentPut), true, false, false)]
    [DataRow(nameof(SiloFaultPoint.AfterSegmentCommitted), true, true, false)]
    [DataRow(nameof(SiloFaultPoint.AfterIndexApplied), true, true, true)]
    public async Task ACrashAtAnyPublicationBoundaryResumesToExactlyOneVisibleRevision(string point, bool landed, bool committed, bool indexed)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = Enum.Parse<SiloFaultPoint>(point);

        var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);

        // A landed segment that the writer has not read back and committed is not yet authority.
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().HaveCount(landed ? 1 : 0);
        silo.Payloads.Released.Should().BeEmpty("spooled bytes are released only after the group is visible");
        var browse = await silo.Index.QueryVisibleAsync(Day, Day.AddHours(1), null, 10, CancellationToken.None).ConfigureAwait(false);
        browse.Should().HaveCount(indexed ? 1 : 0);
        if (point == nameof(SiloFaultPoint.AfterManifestPut))
        {
            silo.Local.Keys("m/").Should().ContainSingle("the manifest object exists although no publish record names it");
        }

        await silo.RestartAsync().ConfigureAwait(false);
        if (!committed)
        {
            // Before P3 everything is staged work: a rebuild from durable records never shows it.
            var rebuilt = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
            rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.Tail);
            (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))
                .Should().BeNull();
        }
        var outcomes = await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);

        outcomes.Should().ContainSingle().Which.State.Should().Be(SiloStagedState.Visible);
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().ContainSingle();
        var revisions = await silo.Index.ReadRevisionsAsync(draft.GroupId, CancellationToken.None).ConfigureAwait(false);
        revisions.Should().ContainSingle().Which.State.Should().Be("visible");
        revisions[0].Position.Should().Be(silo.PublishPosition(draft.GroupId), "P4 indexes the original P3 sequence");
        foreach (var member in draft.Members)
        {
            silo.Local.VersionsOf(SiloObjectNamespace.PayloadKey(member.RetentionClass, member.Sha256)).Should().ContainSingle();
            silo.Payloads.Released[member.SourceToken].Should().Be(1);
        }
        silo.Local.Keys("m/").Should().ContainSingle();
        silo.Local.VersionsOf(silo.Local.Keys("m/")[0]).Should().ContainSingle();
        silo.StateRoot.Spool.Names().Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUncertainPayloadWriteThatLandedIsResolvedByComparingDigestsNotByAnotherVersion()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 2);
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, draft.Members[0].Sha256);
        var timeouts = 1;
        silo.Local.FailAfterLanding = key => key == rawKey && timeouts-- > 0;

        var first = await silo.PublishAsync(draft).ConfigureAwait(false);

        first.State.Should().Be(SiloStagedState.Staged);
        first.BlockedCode.Should().Be("silo.store-unavailable");
        silo.Local.VersionsOf(rawKey).Should().ContainSingle("the uncertain write landed");
        var landed = silo.Local.Current(rawKey);

        var second = await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false);

        second.State.Should().Be(SiloStagedState.Visible);
        silo.Local.VersionsOf(rawKey).Should().Equal(landed);
        silo.MemberLocator(draft.GroupId, SiloDrafts.Raw(2)).VersionId.Should().Be(landed);
    }

    [TestMethod]
    public async Task AnAcknowledgedVersionLostBeforeReadbackIsReplacedAndNeverNamed()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 3);
        var previewKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Display, draft.Members[1].Sha256);
        var losses = 1;
        silo.Local.LoseAfterLanding = key => key == previewKey && losses-- > 0;

        var outcome = await silo.PublishAsync(draft).ConfigureAwait(false);

        outcome.State.Should().Be(SiloStagedState.Visible);
        var named = silo.MemberLocator(draft.GroupId, SiloDrafts.Preview(3));
        silo.Local.VersionsOf(previewKey).Should().Equal(named.VersionId);
        silo.Local.Puts.Should().BeGreaterThan(draft.Members.Count + 2, "the lost version was replayed from the same spooled bytes");
    }

    [TestMethod]
    public async Task AnUncertainJournalWriteReplaysItsExactBytesAfterRestartAndCommitsOnce()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 4);
        var segmentKey = SiloObjectNamespace.SegmentKey(new(1, 2));
        var timeouts = 1;
        silo.Local.FailAfterLanding = key => key == segmentKey && timeouts-- > 0;

        var first = await silo.PublishAsync(draft).ConfigureAwait(false);

        first.State.Should().Be(SiloStagedState.ManifestQualified);
        first.BlockedCode.Should().Be("silo.store-unavailable");
        var landed = silo.Local.Current(segmentKey);
        landed.Should().NotBeNull();
        (await silo.State.ReadPendingAsync(CancellationToken.None).ConfigureAwait(false)).Should().NotBeNull();

        await silo.RestartAsync().ConfigureAwait(false);
        var outcomes = await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);

        outcomes.Should().ContainSingle().Which.State.Should().Be(SiloStagedState.Visible);
        silo.Local.VersionsOf(segmentKey).Should().Equal(landed);
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().ContainSingle();
        (await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false)).VersionId.Should().Be(landed);
    }

    [TestMethod]
    public async Task DifferentBytesAtTheNextJournalSequenceStopTheWriterAndAreNeverReplaced()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var segmentKey = SiloObjectNamespace.SegmentKey(new(1, 2));
        silo.Local.Plant(segmentKey, "{\"format\":\"another writer\"}"u8.ToArray());
        var draft = SiloDrafts.Capture(silo.Payloads, 5);

        var outcome = await silo.PublishAsync(draft).ConfigureAwait(false);

        outcome.State.Should().Be(SiloStagedState.ManifestQualified);
        outcome.BlockedCode.Should().Be("silo.create-only-conflict");
        silo.Local.VersionsOf(segmentKey).Should().ContainSingle();
        silo.Local.Read(segmentKey, silo.Local.Current(segmentKey)!).Should().Equal("{\"format\":\"another writer\"}"u8.ToArray());
        var pin = new SiloPinRecord("pin-1", "group", draft.GroupId, "r1", true, "operator");
        var append = () => silo.Journal.AppendAsync([pin], "pin-1", CancellationToken.None).AsTask();
        await append.Should().ThrowAsync<SiloConflictException>("the writer stays fenced on its pending sequence").ConfigureAwait(false);
        (await silo.Index.ReadPublicationAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false)).Should().BeNull();
    }

    [TestMethod]
    public async Task DifferentBytesAtAContentAddressStopThePublicationAndAreNeverReplaced()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 6);
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, draft.Members[0].Sha256);
        var foreign = SiloDrafts.Bytes("not the raw", 64);
        silo.Local.Plant(rawKey, foreign);

        var outcome = await silo.PublishAsync(draft).ConfigureAwait(false);

        outcome.State.Should().Be(SiloStagedState.Staged);
        outcome.BlockedCode.Should().Be("silo.create-only-conflict");
        silo.Local.Read(rawKey, silo.Local.Current(rawKey)!).Should().Equal(foreign);
        silo.Local.VersionsOf(rawKey).Should().ContainSingle();
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task RepeatedStagingIsIdempotentAndAConflictingRevisionOrDigestIsRefused()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 7);

        (await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
        (await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();

        var changed = draft with { ToUtc = draft.ToUtc.AddSeconds(1) };
        var restage = () => silo.Publisher.StageAsync(changed, CancellationToken.None).AsTask();
        (await restage.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.revision-conflict");

        var other = SiloDrafts.Capture(silo.Payloads, 8);
        var rawClaim = other with
        {
            Members = [other.Members[0] with { ArtifactId = SiloDrafts.Raw(7) }, other.Members[1]]
        };
        var claim = () => silo.Publisher.StageAsync(rawClaim, CancellationToken.None).AsTask();
        (await claim.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.artifact-revision-conflict");

        (await silo.Publisher.PublishAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false)).State
            .Should().Be(SiloStagedState.Visible);
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        var published = () => silo.Publisher.StageAsync(rawClaim with { GroupId = "capture-0099" }, CancellationToken.None).AsTask();
        (await published.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code
            .Should().Be("silo.artifact-revision-conflict");
    }

    [TestMethod]
    public async Task AStoreWithoutAPersistenceQualifiedProfileIsNeverNamedByACommittedRecord()
    {
        using var silo = await SiloHarness.CreateAsync(install: false).ConfigureAwait(false);
        await silo.ReplaceLocalAsync(new SiloTestStore("local-silo", "instance-bucket", qualified: false)).ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 9);

        var outcome = await silo.PublishAsync(draft).ConfigureAwait(false);

        outcome.State.Should().Be(SiloStagedState.Staged);
        outcome.BlockedCode.Should().Be("silo.store-not-qualified");
        silo.Local.Puts.Should().Be(0);
    }

    [TestMethod]
    public async Task SpooledBytesThatNoLongerMatchTheirDigestAreRefusedByTheSuppliedChecksum()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 10);
        silo.Payloads.Corrupt(draft.Members[0].SourceToken);

        var outcome = await silo.PublishAsync(draft).ConfigureAwait(false);

        outcome.State.Should().Be(SiloStagedState.Staged);
        outcome.BlockedCode.Should().Be("silo.payload-checksum-rejected");
        silo.Local.Keys("p/full/").Should().BeEmpty();
    }

    [TestMethod]
    public async Task AbandonedStagedWorkBecomesOrphanCandidatesAndCommittedWorkCannotBeAbandoned()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var staged = SiloDrafts.Capture(silo.Payloads, 11);
        await silo.Publisher.StageAsync(staged, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = SiloFaultPoint.AfterManifestQualified;
        var crash = () => silo.Publisher.PublishAsync(staged.GroupId, "r1", CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        await silo.RestartAsync().ConfigureAwait(false);

        await silo.Publisher.AbandonAsync(staged.GroupId, "r1", "operator abandoned", CancellationToken.None).ConfigureAwait(false);

        var orphans = await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false);
        orphans.Select(static orphan => orphan.Locator.Key[..2]).Should().BeEquivalentTo(["p/", "p/", "m/"]);
        silo.Payloads.Released.Keys.Should().BeEquivalentTo(staged.Members.Select(static member => member.SourceToken));
        (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().BeEmpty();

        var committed = SiloDrafts.Capture(silo.Payloads, 12);
        await silo.PublishAsync(committed).ConfigureAwait(false);
        var abandon = () => silo.Publisher.AbandonAsync(committed.GroupId, "r1", "too late", CancellationToken.None).AsTask();
        await abandon.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
    }
}
