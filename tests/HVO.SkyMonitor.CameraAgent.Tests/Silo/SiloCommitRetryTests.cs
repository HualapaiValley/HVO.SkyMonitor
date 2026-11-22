using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// What a retry may and may not change around the commit (#1965 4.5, 5.2, 5.3). A publish segment that may have been
/// written keeps its exact bytes and is reconciled before anything else, so a revision is committed once and nothing is
/// substituted after P3. One that provably never reached the store is withdrawn, and the group is requalified from its
/// staged inputs before its first actual commit. Proof never crosses a phase: P3 and the first P4 read every version
/// they rely on again. A version a write may have created is always handed over, exactly when it can be resolved.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloCommitRetryTests
{
    private static string RawKey(SiloGroupDraft draft) => SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, draft.Members[0].Sha256);

    private static bool IsSegment(string key) => key.StartsWith(SiloObjectNamespace.JournalPrefix, StringComparison.Ordinal);

    private static bool IsManifest(string key) => key.StartsWith("m/", StringComparison.Ordinal);

    private static async Task<SiloStagedGroup> GroupAsync(SiloHarness silo, SiloGroupDraft draft) =>
        (await silo.State.ReadGroupAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;

    private static SiloPublishRecord SinglePublication(SiloHarness silo, SiloGroupDraft draft) =>
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().ContainSingle(record => record.GroupId == draft.GroupId,
            "a revision is committed once").Which;

    [TestMethod]
    [DataRow("ambiguous-timeout", true, DisplayName = "write may have landed (timeout after landing), reconciled at startup")]
    [DataRow("ambiguous-timeout", false, DisplayName = "write may have landed (timeout after landing), reconciled by the caller's retry")]
    [DataRow("crashed-in-flight", true, DisplayName = "process died with the write in flight, nothing landed, reconciled at startup")]
    [DataRow("landed-then-refused", true, DisplayName = "an attempt landed and was lost, then a retry was refused, reconciled at startup")]
    [DataRow("definitely-not-applied", true, DisplayName = "store proved the write was not applied, requalified at startup")]
    [DataRow("definitely-not-applied", false, DisplayName = "store proved the write was not applied, requalified by the caller's retry")]
    [DataRow("never-sent", true, DisplayName = "segment pending but never sent, requalified at startup")]
    public async Task APublishSegmentIsReplayedWhileItMayExistAndRequalifiedOnlyWhenItProvablyNeverDid(string attempt, bool startup)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 5);
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        switch (attempt)
        {
            case "ambiguous-timeout":
                silo.Local.FailAfterLanding = IsSegment;
                break;
            case "definitely-not-applied":
                silo.Local.FailBeforeLanding = IsSegment;
                break;
            case "crashed-in-flight":
                silo.Local.CrashBeforeLanding = IsSegment;
                break;
            case "landed-then-refused":
                // The first attempt lands and its version is lost before readback; the retry within it is refused outright.
                var attempts = 0;
                silo.Local.LoseAfterLanding = IsSegment;
                silo.Local.FailBeforeLanding = key => IsSegment(key) && ++attempts > 1;
                break;
            default:
                silo.Faults.Armed = SiloFaultPoint.AfterSegmentPending;
                break;
        }
        var publish = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
        if (attempt is "ambiguous-timeout" or "definitely-not-applied" or "landed-then-refused")
        {
            (await publish().ConfigureAwait(false)).BlockedCode.Should().Be("silo.store-unavailable");
        }
        else
        {
            await publish.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
        }
        (silo.Local.FailAfterLanding, silo.Local.FailBeforeLanding, silo.Local.CrashBeforeLanding, silo.Local.LoseAfterLanding) =
            (null, null, null, null);
        var group = await GroupAsync(silo, draft).ConfigureAwait(false);
        var manifest = silo.Local.Locator(
            SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, group.ManifestSha256!), group.ManifestVersionId!);
        var named = group.Members[0].VersionId!;
        silo.Local.Delete(RawKey(draft), named);

        // While a commit of this revision is pending, nothing may re-name one of its locators.
        var replace = () => silo.State.ReplaceMemberLocatorAsync(draft.GroupId, draft.RevisionId, 0, named, "another-version",
            silo.Clock.GetUtcNow(), null, CancellationToken.None).AsTask();
        await replace.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reconciled*").ConfigureAwait(false);

        SiloPublicationOutcome outcome;
        if (startup)
        {
            await silo.RestartAsync().ConfigureAwait(false);
            outcome = (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single();
        }
        else
        {
            outcome = await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
        }

        var committed = SinglePublication(silo, draft);
        if (attempt is "ambiguous-timeout" or "crashed-in-flight" or "landed-then-refused")
        {
            committed.Manifest.Locator.Should().Be(manifest, "a segment that may exist commits with its exact bytes");
            committed.Members[0].Locator.VersionId.Should().Be(named, "nothing is substituted after P3");
            (await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
            outcome.State.Should().Be(SiloStagedState.Recorded);
            outcome.CatalogOutcome.Should().Be(SiloDisposition.Missing, "P3 recovery closes the revision for the lost member");
            silo.Payloads.Released.Should().BeEmpty("the spool is kept until the revision reaches P4");
        }
        else
        {
            outcome.State.Should().Be(SiloStagedState.Visible, "the first actual commit is built from staged inputs verified now");
            committed.Members[0].Locator.VersionId.Should().NotBe(named);
            committed.Manifest.Locator.Key.Should().NotBe(manifest.Key, "the replaced payload needs a new manifest under its own digest");
            SiloHash.Sha256(silo.Local.Read(committed.Members[0].Locator.Key, committed.Members[0].Locator.VersionId))
                .Should().Be(committed.Members[0].Sha256);
            (await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false)).Should().ContainSingle()
                .Which.Locator.Should().Be(manifest);
        }
        (await silo.State.ReadPendingAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeNull();
    }

    [TestMethod]
    [DataRow("resolved", DisplayName = "the replay finds the acknowledged bytes and commits them once")]
    [DataRow("fenced", DisplayName = "other bytes now hold the sequence, so the writer stays fenced")]
    public async Task AFailedReadNeverClearsAWriteTheCommitMayAlreadyHaveApplied(string outcome)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 11);
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        var segment = SiloObjectNamespace.SegmentKey(new(1, 2));
        // The PUT is acknowledged, its exact read misses, the retry conflicts with what is there, and the HEAD that would
        // name it times out. That HEAD is a read: it proves nothing about either write.
        var (misses, heads) = (1, 1);
        silo.Local.MissOnRead = key => key == segment && misses-- > 0;
        silo.Local.HeadUnavailable = key => key == segment && heads-- > 0;
        (await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false)).BlockedCode
            .Should().Be("silo.store-unavailable");
        (silo.Local.MissOnRead, silo.Local.HeadUnavailable) = (null, null);
        var pending = (await silo.State.ReadPendingAsync(CancellationToken.None).ConfigureAwait(false))!;
        pending.MayHaveLanded.Should().BeTrue("an acknowledged creation stays a possible application whatever a later read says");
        pending.ProvablyUnapplied.Should().BeFalse();
        silo.StateRoot.Spool.Names().Should().Contain(SiloObjectSpool.NameOf(pending.Sha256), "its exact bytes are kept for the replay");
        var group = await GroupAsync(silo, draft).ConfigureAwait(false);
        var manifest = silo.Local.Locator(
            SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, group.ManifestSha256!), group.ManifestVersionId!);
        var named = group.Members[0].VersionId!;
        silo.Local.Delete(RawKey(draft), named);
        if (outcome == "fenced")
        {
            silo.Local.Delete(segment, silo.Local.Current(segment)!);
            silo.Local.Plant(segment, "{\"not\":\"this writer's segment\"}"u8.ToArray());
        }

        await silo.RestartAsync().ConfigureAwait(false);

        if (outcome == "resolved")
        {
            var resumed = (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single();
            var committed = SinglePublication(silo, draft);
            committed.Manifest.Locator.Should().Be(manifest, "the commit is exactly the pending one, replayed");
            committed.Members[0].Locator.VersionId.Should().Be(named, "nothing is requalified once the commit may exist");
            silo.Local.VersionsOf(segment).Should().ContainSingle("the replay found the acknowledged write instead of writing again");
            resumed.State.Should().Be(SiloStagedState.Recorded);
            resumed.CatalogOutcome.Should().Be(SiloDisposition.Missing);
            silo.Payloads.Released.Should().BeEmpty();
        }
        else
        {
            var resume = () => silo.Publisher.ResumeAsync(10, CancellationToken.None).AsTask();
            (await resume.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.create-only-conflict");
            (await silo.State.ReadPendingAsync(CancellationToken.None).ConfigureAwait(false))!.Sha256
                .Should().Be(pending.Sha256, "the writer stays fenced on its exact pending bytes");
            (await silo.State.ReadWriterEvidenceAsync(CancellationToken.None).ConfigureAwait(false)).Head.Should().Be(new SiloJournalPosition(1, 1));
            (await GroupAsync(silo, draft).ConfigureAwait(false)).ManifestSha256.Should().Be(group.ManifestSha256, "nothing was requalified");
            (await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
        }
    }

    [TestMethod]
    public async Task APayloadLostWhileItsManifestIsWrittenIsFoundAgainBeforeTheCommit()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 6);
        string? lost = null;
        silo.Local.AfterLanding = key =>
        {
            if (lost is null && IsManifest(key))
            {
                lost = silo.Local.Current(RawKey(draft));
                silo.Local.Delete(RawKey(draft), lost!);
            }
        };

        var outcome = await silo.PublishAsync(draft).ConfigureAwait(false);

        lost.Should().NotBeNull();
        outcome.State.Should().Be(SiloStagedState.Visible);
        var committed = SinglePublication(silo, draft);
        committed.Members[0].Locator.VersionId.Should().NotBe(lost, "a P1 proof taken before the manifest was written proves nothing at P3");
        SiloHash.Sha256(silo.Local.Read(committed.Members[0].Locator.Key, committed.Members[0].Locator.VersionId))
            .Should().Be(committed.Members[0].Sha256);
    }

    [TestMethod]
    public async Task TheFirstP4ReadsEveryRequiredMemberAgainAfterTheCommit()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 7);
        string? lost = null;
        silo.Local.AfterLanding = key =>
        {
            if (lost is null && IsSegment(key) && silo.Local.Current(RawKey(draft)) is { } version)
            {
                lost = version;
                silo.Local.Delete(RawKey(draft), version);
            }
        };
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);

        var outcome = await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false);

        lost.Should().NotBeNull();
        SinglePublication(silo, draft).Members[0].Locator.VersionId.Should().Be(lost, "the commit named the version verified just before it");
        outcome.State.Should().Be(SiloStagedState.Recorded, "a proof from before the commit never satisfies the first P4");
        outcome.CatalogOutcome.Should().Be(SiloDisposition.Missing);
        silo.Payloads.Released.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("payload", DisplayName = "a recreated payload landed before its locator was stored")]
    [DataRow("manifest", DisplayName = "a recreated manifest landed before its version was stored")]
    public async Task AbandoningAfterARecreationLandedHandsTheLandedVersionToRetention(string recreated)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 8);
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = SiloFaultPoint.AfterManifestQualified;
        var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        var group = await GroupAsync(silo, draft).ConfigureAwait(false);
        var key = recreated == "payload"
            ? RawKey(draft)
            : SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, group.ManifestSha256!);
        var named = recreated == "payload" ? group.Members[0].VersionId! : group.ManifestVersionId!;
        silo.Local.Delete(key, named);
        await silo.RestartAsync().ConfigureAwait(false);
        silo.Faults.Armed = recreated == "payload" ? SiloFaultPoint.AfterPayloadPut : SiloFaultPoint.AfterManifestPut;
        var recreate = () => silo.Publisher.ResumeAsync(10, CancellationToken.None).AsTask();
        await recreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
        var landed = silo.Local.Current(key);
        landed.Should().NotBeNull().And.NotBe(named, "the recreation landed under a new version that SQLite does not name yet");

        await silo.RestartAsync().ConfigureAwait(false);
        await silo.Publisher.AbandonAsync(draft.GroupId, draft.RevisionId, "operator abandoned", CancellationToken.None).ConfigureAwait(false);

        (await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false))
            .Should().Contain(orphan => orphan.Locator == silo.Local.Locator(key, landed!), "the landed recreation is handed over exactly");
    }

    [TestMethod]
    public async Task TheLongestValidGroupAndRevisionIdentitiesPublish()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 9) with { GroupId = new string('g', 128), RevisionId = new string('r', 128) };

        var outcome = await silo.PublishAsync(draft).ConfigureAwait(false);

        outcome.State.Should().Be(SiloStagedState.Visible);
        SinglePublication(silo, draft).RevisionId.Should().Be(draft.RevisionId);
    }
}
