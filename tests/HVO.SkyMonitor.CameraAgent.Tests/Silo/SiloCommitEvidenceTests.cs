using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// Exact physical evidence at the commit, at first P4, at reopen and at settlement (#1965 5.3, 6.1, 7.3). Every
/// decision that names, promotes, releases or serves a version rests on exact bytes read in that same operation.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloCommitEvidenceTests
{
    private static async Task CrashAsync(SiloHarness silo, SiloGroupDraft draft, SiloFaultPoint point)
    {
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = point;
        var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
    }

    private static async Task<SiloRoleStatus> RoleAsync(SiloHarness silo, string artifactId) =>
        (await silo.Index.ReadRoleAsync(new(SiloIdentityKind.Member, artifactId, "1"), CancellationToken.None).ConfigureAwait(false))!;

    private static void AssertNamedLocatorsVerify(SiloHarness silo, SiloPublishRecord publish)
    {
        SiloHash.Sha256(silo.Local.Read(publish.Manifest.Locator.Key, publish.Manifest.Locator.VersionId)).Should().Be(publish.Manifest.Sha256);
        foreach (var member in publish.Members)
        {
            SiloHash.Sha256(silo.Local.Read(member.Locator.Key, member.Locator.VersionId)).Should().Be(member.Sha256);
        }
    }

    [TestMethod]
    public async Task AManifestLostBeforeTheCommitIsRecreatedFromItsExactSpooledBytes()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterManifestQualified).ConfigureAwait(false);
        var manifestKey = silo.Local.Keys("m/").Single();
        var lost = silo.Local.Current(manifestKey)!;
        silo.Local.Delete(manifestKey, lost);

        await silo.RestartAsync().ConfigureAwait(false);
        var outcome = (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single();

        outcome.State.Should().Be(SiloStagedState.Visible);
        var publish = silo.PublishRecord(draft.GroupId);
        publish.Manifest.Locator.VersionId.Should().NotBe(lost, "a lost version is never named by the commit");
        AssertNamedLocatorsVerify(silo, publish);
    }

    [TestMethod]
    public async Task APayloadLostBeforeItsManifestIsWrittenIsRecreatedFromTheSpoolBeforeTheManifestNamesIt()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 2);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterPayloadVerified).ConfigureAwait(false);
        var recorded = (await silo.State.ReadGroupAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))!.Members[0];
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, recorded.Draft.Sha256);
        silo.Local.Delete(rawKey, recorded.VersionId!);

        await silo.RestartAsync().ConfigureAwait(false);
        var outcome = (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single();

        outcome.State.Should().Be(SiloStagedState.Visible);
        var publish = silo.PublishRecord(draft.GroupId);
        publish.Members[0].Locator.VersionId.Should().NotBe(recorded.VersionId);
        AssertNamedLocatorsVerify(silo, publish);
        SiloJson.ReadManifest(silo.Local.Read(publish.Manifest.Locator.Key, publish.Manifest.Locator.VersionId), "manifest")
            .Members[0].Locator.Should().Be(publish.Members[0].Locator);
    }

    [TestMethod]
    public async Task ReopenDuringP3RecoveryNeverPromotesTheRevisionOrLetsItsSpoolGo()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterSegmentCommitted).ConfigureAwait(false);
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, draft.Members[0].Sha256);
        silo.Local.ReadUnavailable = key => key == rawKey;
        await silo.RestartAsync().ConfigureAwait(false);
        await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);

        var reopened = await silo.Catalog.ReopenAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);

        reopened.Reopenable.Should().BeFalse();
        (await silo.Index.ReadPublicationAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))!
            .State.Should().Be("closed", "a read never promotes a revision to P4");
        (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single().State
            .Should().Be(SiloStagedState.Recorded);
        silo.Payloads.Released.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ARebuildBetweenTheCommitAndTheFirstP4KeepsTheSpoolUntilEveryRequiredMemberVerifies()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterSegmentCommitted).ConfigureAwait(false);
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, draft.Members[0].Sha256);
        silo.Local.ReadUnavailable = key => key == rawKey;

        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);

        var rebuilt = await silo.Index.ReadPublicationAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        rebuilt!.State.Should().Be("closed", "a P3 that never reached P4 is recovered, not treated as settled history");
        rebuilt.Outcome.Should().Be(SiloDisposition.TemporarilyUnavailable);
        (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single().State
            .Should().Be(SiloStagedState.Recorded);
        silo.Payloads.Released.Should().BeEmpty();

        silo.Local.ReadUnavailable = null;
        (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single().State
            .Should().Be(SiloStagedState.Visible);
        silo.Payloads.Released.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ExactReopenReadsEachRequiredRoleNowAndFallsBackToAnEligibleReplica()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(1));
        var local = silo.MemberLocator(capture.GroupId, raw.Id);
        var copy = silo.ReplicateToArchive(raw, local);
        await silo.AppendAsync(copy).ConfigureAwait(false);
        (await silo.Catalog.ReopenAsync(capture.GroupId, "r1", CancellationToken.None).ConfigureAwait(false)).Reopenable.Should().BeTrue();

        silo.Local.Delete(local);
        var fallback = await silo.Catalog.ReopenAsync(capture.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);

        fallback.Reopenable.Should().BeTrue();
        fallback.Items.Single(item => item.Role.Id == raw.Id).ServedFrom.Should().Be(copy.Locator, "the dead local locator is never returned");

        silo.Archive.Delete(copy.Locator);
        var lost = await silo.Catalog.ReopenAsync(capture.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        lost.Reopenable.Should().BeFalse();
        lost.Blocking.Should().ContainSingle().Which.Disposition.Should().Be(SiloDisposition.Missing);
    }

    [TestMethod]
    public async Task ALateValidCopyOrQualifiedRemapPromotesAnEarlierIneligibleRecordOfTheSameVersion()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 5);
        var rawBytes = SiloDrafts.Bytes("raw-5", 3005);
        var raw = new SiloObjectIdentity(SiloIdentityKind.Member, SiloDrafts.Raw(5), "1", SiloHash.Sha256(rawBytes), rawBytes.Length);
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, raw.Sha256);
        var archived = silo.Archive.Locator(rawKey, silo.Archive.Plant(rawKey, rawBytes));
        var copy = new SiloCopyRecord(raw, archived, SiloStoreClass.Archive, SiloRetrieval.Online, "replica", silo.Clock.GetUtcNow(),
            SiloFormats.ExactGetSha256);
        await silo.AppendAsync(copy).ConfigureAwait(false);
        await silo.PublishAsync(draft).ConfigureAwait(false);
        await silo.AppendAsync(copy).ConfigureAwait(false);
        var preview = SiloHarness.Identity(silo.PublishRecord(draft.GroupId), SiloDrafts.Preview(5));
        var lostPreview = silo.MemberLocator(draft.GroupId, preview.Id);
        var restored = silo.Local.Locator(lostPreview.Key, "restored-later");
        silo.Local.Delete(lostPreview);
        var restoredVersion = silo.Local.Plant(lostPreview.Key, SiloDrafts.Bytes("preview-5", 405));
        restored = restored with { VersionId = restoredVersion };
        await silo.AppendAsync(new SiloRemapRecord(preview, lostPreview, restored, silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256, false))
            .ConfigureAwait(false);
        await silo.AppendAsync(new SiloRemapRecord(preview, lostPreview, restored, silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256, true))
            .ConfigureAwait(false);
        silo.Local.Delete(silo.MemberLocator(draft.GroupId, raw.Id));

        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None, includeAvailable: true).ConfigureAwait(false);
        var incremental = (await RoleAsync(silo, raw.Id).ConfigureAwait(false), await RoleAsync(silo, preview.Id).ConfigureAwait(false));
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);
        var rebuilt = (await RoleAsync(silo, raw.Id).ConfigureAwait(false), await RoleAsync(silo, preview.Id).ConfigureAwait(false));

        foreach (var (rawRole, previewRole) in new[] { incremental, rebuilt })
        {
            rawRole.AvailableAt.Should().Be(archived);
            previewRole.AvailableAt.Should().Be(restored);
        }
    }

    [TestMethod]
    public async Task ALateCopyRequeuesARevisionThatP3RecoveryClosedAsMissing()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterSegmentCommitted).ConfigureAwait(false);
        var publish = silo.PublishRecord(draft.GroupId);
        var raw = SiloHarness.Identity(publish, SiloDrafts.Raw(1));
        var local = silo.MemberLocator(draft.GroupId, raw.Id);
        var bytes = silo.Local.Read(local.Key, local.VersionId);
        silo.Local.Delete(local);
        await silo.RestartAsync().ConfigureAwait(false);
        (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single().CatalogOutcome
            .Should().Be(SiloDisposition.Missing);

        var archived = silo.Archive.Locator(local.Key, silo.Archive.Plant(local.Key, bytes));
        await silo.Journal.AppendAsync([new SiloCopyRecord(raw, archived, SiloStoreClass.Archive, SiloRetrieval.Online, "replica",
            silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256)], "late-copy-raw-1", CancellationToken.None).ConfigureAwait(false);
        var resumed = (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single();

        resumed.State.Should().Be(SiloStagedState.Visible, "incremental recovery reaches what a rebuild would serve");
        var incremental = await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false);
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        (await silo.Index.ReadPublicationAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))!.State.Should().Be("visible");
        incremental.Should().Contain("visible");
    }

    [TestMethod]
    [DataRow("without-last-copy-authority")]
    [DataRow("missing-copy-outside-intent")]
    public async Task RoleExpiryNeedsLastCopyAuthorityAndEveryAuthoritativeCopyProvedAbsent(string fault)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(1));
        var local = silo.MemberLocator(capture.GroupId, raw.Id);
        var lastCopy = fault != "without-last-copy-authority";
        if (fault == "missing-copy-outside-intent")
        {
            var copy = silo.ReplicateToArchive(raw, local);
            await silo.AppendAsync(copy).ConfigureAwait(false);
            silo.Archive.Delete(copy.Locator);
            await silo.AppendAsync(new SiloAvailabilityRecord(SiloAvailabilityScope.Copy, raw, SiloAvailabilityState.Missing,
                silo.Clock.GetUtcNow(), "unexpectedly absent at the archive", copy.Locator)).ConfigureAwait(false);
        }
        await silo.AppendAsync(new SiloExpiryIntentRecord("expire-raw", SiloExpiryScope.Role, raw, "operator authorization",
            [new(local, "local", "allocation-raw", raw.Length)], "retention", LastCopy: lastCopy)).ConfigureAwait(false);
        silo.Local.Delete(local);
        await silo.AppendAsync(
            new SiloExpiryDispositionRecord("expire-raw", local, SiloExpiryOutcome.Deleted, silo.Clock.GetUtcNow(), "NoSuchVersion"),
            new SiloAvailabilityRecord(SiloAvailabilityScope.Role, raw, SiloAvailabilityState.PolicyExpired, silo.Clock.GetUtcNow(),
                "claimed", IntentId: "expire-raw")).ConfigureAwait(false);

        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);

        var role = await RoleAsync(silo, raw.Id).ConfigureAwait(false);
        role.Journaled.Should().BeNull();
        role.Disposition.Should().Be(SiloDisposition.Missing, "unauthorized or uncovered loss is unexpected loss, never policy expiry");
    }

    [TestMethod]
    public async Task AbandoningAfterAnUncertainPutStillHandsTheLandedVersionToRetention()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterPayloadPut).ConfigureAwait(false);
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, draft.Members[0].Sha256);
        silo.Local.Current(rawKey).Should().NotBeNull("the PUT landed before the crash");

        await silo.RestartAsync().ConfigureAwait(false);
        await silo.Publisher.AbandonAsync(draft.GroupId, "r1", "operator abandoned", CancellationToken.None).ConfigureAwait(false);

        (await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false))
            .Should().Contain(orphan => orphan.Locator.Key == rawKey);
    }

    [TestMethod]
    public async Task BoundedVerificationMakesProgressPastATimeoutAndPastHealthyRows()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var raw1 = silo.MemberLocator(SiloDrafts.CaptureGroup(1), SiloDrafts.Raw(1));
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        silo.Local.ReadUnavailable = key => key == raw1.Key;

        for (var pass = 0; pass < 4; pass++)
        {
            await silo.Catalog.VerifyRolesAsync(1, CancellationToken.None).ConfigureAwait(false);
        }

        (await RoleAsync(silo, SiloDrafts.Raw(1)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.TemporarilyUnavailable);
        foreach (var other in new[] { SiloDrafts.Preview(1), SiloDrafts.Raw(2), SiloDrafts.Preview(2) })
        {
            (await RoleAsync(silo, other).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.Available, "a timeout never starves later roles");
        }

        silo.Local.ReadUnavailable = null;
        silo.Local.Delete(silo.MemberLocator(SiloDrafts.CaptureGroup(2), SiloDrafts.Raw(2)));
        for (var pass = 0; pass < 8; pass++)
        {
            await silo.Catalog.VerifyRolesAsync(1, CancellationToken.None, includeAvailable: true).ConfigureAwait(false);
        }
        (await RoleAsync(silo, SiloDrafts.Raw(2)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.Missing, "a healthy early row never hides a later loss");
    }

    [TestMethod]
    public async Task AnInterruptedReleaseIsFinishedOnResume()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterVisibleCommitted).ConfigureAwait(false);
        (await silo.State.ReadGroupAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))!.State.Should().Be(SiloStagedState.Visible);
        silo.Payloads.Released.Should().BeEmpty();

        await silo.RestartAsync().ConfigureAwait(false);
        await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);

        silo.Payloads.Released.Keys.Should().BeEquivalentTo(draft.Members.Select(static member => member.SourceToken));
        (await silo.State.ListUnreleasedAsync(10, CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task ALateCopyReopensAHistoricalRevisionThatARebuildClosedAsMissing()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var publish = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var manifest = publish.Manifest.Locator;
        var bytes = silo.Local.Read(manifest.Key, manifest.VersionId);
        silo.Local.Delete(manifest);
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        (await silo.Index.ReadPublicationAsync(publish.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))!.Outcome
            .Should().Be(SiloDisposition.Missing);

        var archived = silo.Archive.Locator(manifest.Key, silo.Archive.Plant(manifest.Key, bytes));
        await silo.AppendAsync(new SiloCopyRecord(SiloHarness.ManifestIdentity(publish), archived, SiloStoreClass.Archive, SiloRetrieval.Online,
            "replica", silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256)).ConfigureAwait(false);

        (await silo.Index.ReadPublicationAsync(publish.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))!.State
            .Should().Be("visible", "ordinary catch-up reaches what a rebuild with the same journal serves");
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        (await silo.Index.ReadPublicationAsync(publish.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))!.State.Should().Be("visible");
    }
}
