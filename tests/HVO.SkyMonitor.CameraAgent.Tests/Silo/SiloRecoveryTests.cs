using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// P3 recovery and empty-index reconstruction (#1965 5.3, 6.1, 6.2, 7.5; #1967 acceptance 2 and 4): stable identities,
/// original revisions, surviving previews, and expired versus unavailable versus unexpectedly missing roles, all proven by
/// exact-version reads rather than by catalog state.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
public sealed class SiloRecoveryTests
{
    private static readonly int[] RemappedCaptures = [6, 7, 8];

    private static SiloRoleKey MemberRole(string artifactId) => new(SiloIdentityKind.Member, artifactId, "1");

    private static async Task<SiloRoleStatus> RoleAsync(SiloHarness silo, string artifactId) =>
        (await silo.Index.ReadRoleAsync(MemberRole(artifactId), CancellationToken.None).ConfigureAwait(false))!;

    private static async Task RebuildAndVerifyAsync(SiloHarness silo, SiloRebuildOptions? options = null)
    {
        await silo.DeleteCatalogAndRebuildAsync(options).ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(10_000, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ContradictoryAuthenticatedEvidenceClosesTheRevisionAsAConflictAndRebuildsTheSame()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var first = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var second = silo.PublishRecord(SiloDrafts.CaptureGroup(2));
        var raw = first.Members.Single(member => member.ArtifactId == SiloDrafts.Raw(1));
        var other = SiloDrafts.Bytes("a different raw", 64);
        var otherKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, SiloHash.Sha256(other));
        var otherVersion = silo.Local.Plant(otherKey, other);
        // Staging refuses both contradictions, so they can only arrive as authenticated records from a damaged or foreign writer.
        var claim = first with
        {
            GroupId = "capture-0050",
            Members = [raw with { Length = other.Length, Sha256 = SiloHash.Sha256(other), Locator = silo.Local.Locator(otherKey, otherVersion) }],
            Capture = null
        };
        var republished = second with { Manifest = second.Manifest with { Sha256 = SiloHash.Sha256("another manifest"u8) } };
        var preview = SiloHarness.Identity(second, SiloDrafts.Preview(2));
        await silo.AppendAsync(claim, republished, new SiloAvailabilityRecord(SiloAvailabilityScope.Role, preview,
            SiloAvailabilityState.Conflict, silo.Clock.GetUtcNow(), "replicas disagree about the preview")).ConfigureAwait(false);

        async Task AssertConflictsAsync()
        {
            (await silo.Index.ReadPublicationAsync("capture-0050", "r1", CancellationToken.None).ConfigureAwait(false))!
                .Outcome.Should().Be(SiloDisposition.Conflict, "an artifact revision is bound to one digest");
            (await silo.Index.ReadPublicationAsync(first.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))!
                .State.Should().Be("visible");
            (await RoleAsync(silo, raw.ArtifactId).ConfigureAwait(false)).Sha256.Should().Be(raw.Sha256, "a verified payload is never replaced");
            (await silo.Index.ReadPublicationAsync(second.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))!
                .Outcome.Should().Be(SiloDisposition.Conflict, "the same revision was published with different manifest bytes");
            var reopened = await silo.Catalog.ReopenAsync(second.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
            reopened.Reopenable.Should().BeFalse();
            reopened.ClosedOutcome.Should().Be(SiloDisposition.Conflict);
            (await RoleAsync(silo, SiloDrafts.Preview(2)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.Conflict);
            (await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false)).FirstIncompletePublication!
                .GroupId.Should().Be(second.GroupId, "the complete-publication frontier never advances past a contradiction");
        }

        await AssertConflictsAsync().ConfigureAwait(false);
        await RebuildAndVerifyAsync(silo).ConfigureAwait(false);
        await AssertConflictsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ATimedOutReadDuringP3RecoveryKeepsTheRevisionClosedUntilTheSameRevisionReachesP4()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
        var crash = () => silo.Publisher.PublishAsync(draft.GroupId, "r1", CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, draft.Members[0].Sha256);
        silo.Local.ReadUnavailable = key => key == rawKey;

        await silo.RestartAsync().ConfigureAwait(false);
        var blocked = await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);

        blocked.Should().ContainSingle().Which.Should().Match<SiloPublicationOutcome>(outcome =>
            outcome.State == SiloStagedState.Recorded && outcome.CatalogState == "closed" &&
            outcome.CatalogOutcome == SiloDisposition.TemporarilyUnavailable);
        var closed = await silo.Index.ReadPublicationAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        closed!.ManifestStatus.Should().Be(SiloDisposition.Verified);
        (await RoleAsync(silo, SiloDrafts.Raw(1)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.TemporarilyUnavailable);
        (await silo.Index.QueryVisibleAsync(SiloHarness.Start, SiloHarness.Start.AddHours(1), null, 10, CancellationToken.None)
            .ConfigureAwait(false)).Should().BeEmpty("readers of the group stay closed");
        silo.Payloads.Released.Should().BeEmpty();

        silo.Local.ReadUnavailable = null;
        var resumed = await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);

        resumed.Should().ContainSingle().Which.State.Should().Be(SiloStagedState.Visible);
        var visible = await silo.Index.ReadPublicationAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        visible!.Position.Should().Be(silo.PublishPosition(draft.GroupId));
        (await RoleAsync(silo, SiloDrafts.Raw(1)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.Available);
        silo.JournalRecords().OfType<SiloAvailabilityRecord>().Should().BeEmpty("a timeout records no loss and no conflict");
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().ContainSingle();
    }

    [TestMethod]
    public async Task P3RecoveryReadsASharedMemberAgainEvenWhenAnEarlierGroupVerifiedIt()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var first = SiloDrafts.Capture(silo.Payloads, 1);
        await silo.PublishAsync(first).ConfigureAwait(false);
        // A later group shares the first capture's raw by identity and bytes, so it names the same content-addressed version.
        var second = SiloDrafts.Capture(silo.Payloads, 2);
        var shared = second with { Members = [first.Members[0], second.Members[1]] };
        await silo.Publisher.StageAsync(shared, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
        var crash = () => silo.Publisher.PublishAsync(shared.GroupId, "r1", CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        var raw = silo.MemberLocator(first.GroupId, SiloDrafts.Raw(1));
        silo.MemberLocator(shared.GroupId, SiloDrafts.Raw(1)).Should().Be(raw);
        silo.Local.Delete(raw);

        await silo.RestartAsync().ConfigureAwait(false);
        var outcomes = await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);

        outcomes.Should().ContainSingle().Which.Should().Match<SiloPublicationOutcome>(outcome =>
            outcome.State == SiloStagedState.Recorded && outcome.CatalogState == "closed" && outcome.CatalogOutcome == SiloDisposition.Missing,
            "check 3 reads exact bytes after the cursor; the earlier group's verification is not evidence the version survives");
        (await RoleAsync(silo, SiloDrafts.Raw(1)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.Missing);
    }

    [TestMethod]
    public async Task ARebuiltCatalogAnswersEveryQueryAsTheIncrementalCatalogDidAcrossRestarts()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        for (var number = 1; number <= 3; number++)
        {
            await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, number)).ConfigureAwait(false);
        }
        await silo.RestartAsync().ConfigureAwait(false);
        await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Evidence(silo.Payloads, "stars")).ConfigureAwait(false);
        var a1 = SiloDrafts.View(silo.Payloads, "base-a", null, 1,
            [SiloDrafts.Layer("stars", "Ready", "stars", 4), SiloDrafts.Layer("clouds", "Waiting", null, 0)],
            [], publishPreview: true);
        await silo.PublishAsync(a1).ConfigureAwait(false);
        await silo.AppendAsync(new SiloLiveIssueRecord("camera-1", "camera-1/run-1/generation-1", a1.GroupId, a1.RevisionId, 7,
            SiloHarness.Start.AddMinutes(31))).ConfigureAwait(false);
        // A late layer revision on the base is archive-only: published, never live-issued.
        var a2 = SiloDrafts.View(silo.Payloads, "base-a", a1.RevisionId, 2,
            [SiloDrafts.Layer("stars", "Ready", "stars", 4), SiloDrafts.Layer("clouds", "Incompatible", null, 5)],
            [], publishPreview: false);
        await silo.PublishAsync(a2).ConfigureAwait(false);
        var capture1 = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        await silo.AppendAsync(
            new SiloPinRecord("pin-a", "group", a1.GroupId, a1.RevisionId, true, "operator keeps the first revision"),
            new SiloPolicyRecord("display", SiloJson.SerializeToElement(new { keepDays = 30 })),
            silo.ReplicateToArchive(SiloHarness.Identity(capture1, SiloDrafts.Raw(1)), silo.MemberLocator(capture1.GroupId, SiloDrafts.Raw(1))))
            .ConfigureAwait(false);
        await silo.RestartAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 4)).ConfigureAwait(false);
        var before = await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false);
        var layersBefore = await silo.Index.ReadLayersAsync(a2.GroupId, a2.RevisionId, CancellationToken.None).ConfigureAwait(false);

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(10_000, CancellationToken.None).ConfigureAwait(false);

        rebuilt.StartKind.Should().Be("genesis");
        rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.Tail);
        rebuilt.Authority.UnknownInterval.Should().BeFalse();
        rebuilt.Authority.CompleteThroughPrefixEnd.Should().BeTrue();
        (await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false)).Should().Be(before);
        (await silo.Index.ReadLayersAsync(a2.GroupId, a2.RevisionId, CancellationToken.None).ConfigureAwait(false)).Should().Equal(layersBefore);
        var pointers = await silo.Index.ReadPointersAsync(a1.GroupId, CancellationToken.None).ConfigureAwait(false);
        pointers.Should().Be(new SiloRevisionPointers(a1.GroupId, a1.RevisionId, a2.RevisionId, a1.RevisionId));
        pointers.DefaultRevisionId.Should().Be(a1.RevisionId);
        (await silo.Catalog.ReopenAsync(a1.GroupId, a1.RevisionId, CancellationToken.None).ConfigureAwait(false)).Reopenable.Should().BeTrue();
        (await silo.Catalog.ReopenAsync(a2.GroupId, a2.RevisionId, CancellationToken.None).ConfigureAwait(false)).Reopenable.Should().BeTrue();
        (await silo.Index.ReadCurrentLiveAsync("camera-1", CancellationToken.None).ConfigureAwait(false))!.RevisionId.Should().Be(a1.RevisionId);
        (await silo.Index.ReadIdentityFloorsAsync(CancellationToken.None).ConfigureAwait(false))
            .Should().ContainKey("capture-sequence");
    }

    [TestMethod]
    public async Task ALastCopyRoleExpiryRebuildsAsPolicyExpiredWhileAnUnauthorizedLossIsMissing()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var capture1 = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var raw1 = SiloHarness.Identity(capture1, SiloDrafts.Raw(1));
        // The view's typed reopen set is its own base's preview, closed window and displayed science, all published here as
        // members; the expired raw is only a compute input, so the view still reopens.
        var b1 = SiloDrafts.View(silo.Payloads, "base-b", null, 1, [SiloDrafts.Layer("clouds", "Waiting", null, 0)],
            [new(raw1.Id, raw1.Revision, raw1.Sha256, SiloDependencyKind.Compute, true)], publishPreview: true);
        await silo.PublishAsync(b1).ConfigureAwait(false);
        var local = silo.MemberLocator(capture1.GroupId, raw1.Id);
        await silo.AppendAsync(new SiloExpiryIntentRecord("expire-raw-1", SiloExpiryScope.Role, raw1, "last-copy-policy:operator",
            [new(local, "local", "allocation-raw-1", raw1.Length)], "full-fidelity retention elapsed", LastCopy: true)).ConfigureAwait(false);
        silo.Local.Delete(local);
        await silo.AppendAsync(
            new SiloExpiryDispositionRecord("expire-raw-1", local, SiloExpiryOutcome.Deleted, silo.Clock.GetUtcNow(), "NoSuchVersion after delete"),
            new SiloAvailabilityRecord(SiloAvailabilityScope.Role, raw1, SiloAvailabilityState.PolicyExpired, silo.Clock.GetUtcNow(),
                "every authoritative copy absent under the role intent", IntentId: "expire-raw-1")).ConfigureAwait(false);
        silo.Local.Delete(silo.MemberLocator(SiloDrafts.CaptureGroup(2), SiloDrafts.Raw(2)));
        (await RoleAsync(silo, SiloDrafts.Raw(1)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.PolicyExpired);

        await RebuildAndVerifyAsync(silo).ConfigureAwait(false);

        var expired = await RoleAsync(silo, SiloDrafts.Raw(1)).ConfigureAwait(false);
        expired.Disposition.Should().Be(SiloDisposition.PolicyExpired);
        expired.Locators.Single(locator => locator.Source == "named").CopyState.Should().Be("evicted");
        (await RoleAsync(silo, SiloDrafts.Raw(2)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.Missing);
        (await RoleAsync(silo, SiloDrafts.Preview(1)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.Available);
        (await silo.Catalog.ReopenAsync(b1.GroupId, b1.RevisionId, CancellationToken.None).ConfigureAwait(false)).Reopenable.Should().BeTrue();
        var capture = await silo.Catalog.ReopenAsync(capture1.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        capture.Reopenable.Should().BeFalse();
        capture.Blocking.Should().ContainSingle().Which.Disposition.Should().Be(SiloDisposition.PolicyExpired);
        var lost = await silo.Catalog.ReopenAsync(SiloDrafts.CaptureGroup(2), "r1", CancellationToken.None).ConfigureAwait(false);
        lost.Blocking.Should().ContainSingle().Which.Disposition.Should().Be(SiloDisposition.Missing);
    }

    [TestMethod]
    public async Task ARoleExpiryStaysPendingWhileACopyOutsideTheIntentOrAnUnsettledTargetRemains()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(1));
        var local = silo.MemberLocator(capture.GroupId, raw.Id);
        var copy = silo.ReplicateToArchive(raw, local);
        await silo.AppendAsync(copy).ConfigureAwait(false);
        await silo.AppendAsync(new SiloExpiryIntentRecord("local-only", SiloExpiryScope.Role, raw, "last-copy-policy:operator",
            [new(local, "local", "allocation-raw-1", raw.Length)], "retention", LastCopy: true)).ConfigureAwait(false);
        silo.Local.Delete(local);
        await silo.AppendAsync(
            new SiloExpiryDispositionRecord("local-only", local, SiloExpiryOutcome.Deleted, silo.Clock.GetUtcNow(), "NoSuchVersion"),
            new SiloAvailabilityRecord(SiloAvailabilityScope.Role, raw, SiloAvailabilityState.PolicyExpired, silo.Clock.GetUtcNow(),
                "claimed", IntentId: "local-only"),
            new SiloExpiryIntentRecord("archive-too", SiloExpiryScope.Role, raw, "last-copy-policy:operator",
                [new(copy.Locator, "archive")], "retention", LastCopy: true),
            new SiloAvailabilityRecord(SiloAvailabilityScope.Role, raw, SiloAvailabilityState.PolicyExpired, silo.Clock.GetUtcNow(),
                "claimed before settlement", IntentId: "archive-too")).ConfigureAwait(false);

        await RebuildAndVerifyAsync(silo).ConfigureAwait(false);

        var role = await RoleAsync(silo, raw.Id).ConfigureAwait(false);
        role.Journaled.Should().BeNull();
        role.Disposition.Should().Be(SiloDisposition.Available);
        role.AvailableAt.Should().Be(copy.Locator);
        var described = await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false);
        described.Should().Contain("role-expiry-incomplete-copy-set").And.Contain("role-expiry-unsettled");
    }

    [TestMethod]
    public async Task ACopyScopedEvictionReopensFromTheArchiveAndThenReportsItsOutageOrLoss()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(1));
        var local = silo.MemberLocator(capture.GroupId, raw.Id);
        var rawCopy = silo.ReplicateToArchive(raw, local);
        await silo.AppendAsync(
            silo.ReplicateToArchive(SiloHarness.ManifestIdentity(capture), capture.Manifest.Locator),
            rawCopy,
            silo.ReplicateToArchive(SiloHarness.Identity(capture, SiloDrafts.Preview(1)), silo.MemberLocator(capture.GroupId, SiloDrafts.Preview(1))))
            .ConfigureAwait(false);
        await silo.AppendAsync(
            new SiloReserveSurvivorRecord("hold-raw-1", raw, rawCopy.Locator, silo.Clock.GetUtcNow(), "evict-raw-1"),
            new SiloExpiryIntentRecord("evict-raw-1", SiloExpiryScope.Copy, raw, "survivor-hold:hold-raw-1",
                [new(local, "local", "allocation-raw-1", raw.Length)], "local headroom", LastCopy: false)).ConfigureAwait(false);
        silo.Local.Delete(local);
        await silo.AppendAsync(new SiloExpiryDispositionRecord("evict-raw-1", local, SiloExpiryOutcome.Deleted, silo.Clock.GetUtcNow(),
            "NoSuchVersion"), new SiloReleaseSurvivorRecord("hold-raw-1", "eviction settled")).ConfigureAwait(false);

        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        var reopened = await silo.Catalog.ReopenAsync(capture.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);

        reopened.Reopenable.Should().BeTrue();
        reopened.Items.Single(item => item.Role.Id == raw.Id).ServedFrom.Should().Be(rawCopy.Locator);
        var role = await RoleAsync(silo, raw.Id).ConfigureAwait(false);
        role.Locators.Single(locator => locator.Source == "named").CopyState.Should().Be("evicted");
        role.Disposition.Should().Be(SiloDisposition.Available);

        silo.Archive.Unreachable = true;
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        var outage = await silo.Catalog.ReopenAsync(capture.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        outage.Reopenable.Should().BeFalse();
        outage.Blocking.Should().ContainSingle().Which.Disposition.Should().Be(SiloDisposition.TemporarilyUnavailable);

        silo.Archive.Unreachable = false;
        silo.Archive.Delete(rawCopy.Locator);
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        var lost = await silo.Catalog.ReopenAsync(capture.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        lost.Blocking.Should().ContainSingle().Which.Disposition.Should().Be(SiloDisposition.Missing,
            "a copy-scoped eviction never counts as policy expiry");
    }

    [TestMethod]
    public async Task ALateReplicaIsEligibleButACopyBeforeItsPublicationOrBoundToAnotherIdentityIsNot()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 5);
        var rawBytes = SiloDrafts.Bytes("raw-5", 3005);
        var raw = new SiloObjectIdentity(SiloIdentityKind.Member, SiloDrafts.Raw(5), "1", SiloHash.Sha256(rawBytes), rawBytes.Length);
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, raw.Sha256);
        var early = silo.Archive.Plant(rawKey, rawBytes);
        await silo.AppendAsync(new SiloCopyRecord(raw, silo.Archive.Locator(rawKey, early), SiloStoreClass.Archive, SiloRetrieval.Online,
            "replica", silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256)).ConfigureAwait(false);
        await silo.PublishAsync(draft).ConfigureAwait(false);
        var capture = silo.PublishRecord(draft.GroupId);
        var otherIdentity = raw with { Id = "raw-9999" };
        var preview = silo.ReplicateToArchive(SiloHarness.Identity(capture, SiloDrafts.Preview(5)), silo.MemberLocator(draft.GroupId, SiloDrafts.Preview(5)));
        await silo.AppendAsync(
            new SiloCopyRecord(otherIdentity, silo.Archive.Locator(rawKey, early), SiloStoreClass.Archive, SiloRetrieval.Online, "replica",
                silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256),
            preview).ConfigureAwait(false);
        silo.Local.Delete(silo.MemberLocator(draft.GroupId, raw.Id));
        silo.Local.Delete(silo.MemberLocator(draft.GroupId, SiloDrafts.Preview(5)));

        await RebuildAndVerifyAsync(silo).ConfigureAwait(false);

        var rawRole = await RoleAsync(silo, raw.Id).ConfigureAwait(false);
        rawRole.Disposition.Should().Be(SiloDisposition.Missing, "matching bytes alone never make a locator eligible");
        rawRole.Locators.Single(locator => locator.Source == "copy").IneligibleReason.Should().Be("no-prior-publication");
        (await silo.Index.ReadRoleAsync(otherIdentity.Role, CancellationToken.None).ConfigureAwait(false)).Should().BeNull();
        var previewRole = await RoleAsync(silo, SiloDrafts.Preview(5)).ConfigureAwait(false);
        previewRole.Disposition.Should().Be(SiloDisposition.Available);
        previewRole.AvailableAt.Should().Be(preview.Locator);
    }

    [TestMethod]
    public async Task AVerifiedQualifiedRemapRestoresTheSameIdentityButAnUnqualifiedOrWrongByteRemapDoesNot()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var remaps = new List<SiloJournalRecord>();
        foreach (var (number, qualified, wrongBytes) in new[] { (6, true, false), (7, false, false), (8, true, true) })
        {
            await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, number)).ConfigureAwait(false);
            var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(number));
            var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(number));
            var lost = silo.MemberLocator(capture.GroupId, raw.Id);
            silo.Local.Delete(lost);
            var restored = silo.Local.Plant(lost.Key, wrongBytes ? SiloDrafts.Bytes("not raw", (int)raw.Length) : SiloDrafts.Bytes("raw-" + number, (int)raw.Length));
            remaps.Add(new SiloRemapRecord(raw, lost, silo.Local.Locator(lost.Key, restored), silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256,
                qualified));
        }
        var manifestsBefore = RemappedCaptures.Select(number => silo.PublishRecord(SiloDrafts.CaptureGroup(number)).Manifest).ToArray();
        await silo.AppendAsync([.. remaps]).ConfigureAwait(false);

        await RebuildAndVerifyAsync(silo).ConfigureAwait(false);

        var restoredRole = await RoleAsync(silo, SiloDrafts.Raw(6)).ConfigureAwait(false);
        restoredRole.Disposition.Should().Be(SiloDisposition.Available);
        restoredRole.AvailableAt.Should().Be(((SiloRemapRecord)remaps[0]).To);
        (await RoleAsync(silo, SiloDrafts.Raw(7)).ConfigureAwait(false)).Locators.Single(locator => locator.Source == "remap").IneligibleReason
            .Should().Be("not-persistence-qualified");
        (await RoleAsync(silo, SiloDrafts.Raw(7)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.Missing);
        var wrong = await RoleAsync(silo, SiloDrafts.Raw(8)).ConfigureAwait(false);
        wrong.Disposition.Should().Be(SiloDisposition.Missing);
        wrong.Locators.Single(locator => locator.Source == "remap").ProbeStatus.Should().Be("damaged");
        RemappedCaptures.Select(number => silo.PublishRecord(SiloDrafts.CaptureGroup(number)).Manifest).Should().Equal(manifestsBefore,
            "a remap never rewrites a manifest or publish record");
    }

    [TestMethod]
    public async Task AnAuditFindsAVersionDeletedBehindAStructurallyValidCatalog()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var raw = silo.MemberLocator(SiloDrafts.CaptureGroup(1), SiloDrafts.Raw(1));
        silo.Local.Delete(raw);
        (await RoleAsync(silo, SiloDrafts.Raw(1)).ConfigureAwait(false)).Disposition.Should().Be(SiloDisposition.Available, "the catalog alone proves nothing");

        await silo.Catalog.VerifyRolesAsync(10_000, CancellationToken.None, includeAvailable: true).ConfigureAwait(false);

        var role = await RoleAsync(silo, SiloDrafts.Raw(1)).ConfigureAwait(false);
        role.Disposition.Should().Be(SiloDisposition.Missing);
        role.Locators.Single().ProbeStatus.Should().Be("absent");
        (await silo.Catalog.ReopenAsync(SiloDrafts.CaptureGroup(1), "r1", CancellationToken.None).ConfigureAwait(false))
            .Blocking.Should().ContainSingle().Which.Role.Id.Should().Be(SiloDrafts.Raw(1));
    }

    [TestMethod]
    public async Task DamagedBytesBehindIntactMetadataAreMissingEvenThoughHeadStillLocatesTheVersion()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await silo.PublishAsync(draft).ConfigureAwait(false);
        var raw = silo.MemberLocator(draft.GroupId, SiloDrafts.Raw(1));
        silo.Local.Damage(raw);

        (await silo.Local.LocateCurrentVersionAsync(raw.Key, CancellationToken.None).ConfigureAwait(false)).Should().Be(raw.VersionId);
        silo.Local.Metadata(raw.Key, raw.VersionId)[SiloObjectHeaders.Sha256].Should().Be(draft.Members[0].Sha256);
        await silo.Catalog.VerifyRolesAsync(10_000, CancellationToken.None, includeAvailable: true).ConfigureAwait(false);

        var role = await RoleAsync(silo, SiloDrafts.Raw(1)).ConfigureAwait(false);
        role.Disposition.Should().Be(SiloDisposition.Missing);
        role.Locators.Single().Should().Match<SiloLocatorStatus>(locator => locator.ProbeStatus == "damaged" && locator.ProbeDetail == "sha256-mismatch");
    }
}
