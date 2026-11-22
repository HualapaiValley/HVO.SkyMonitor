using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// Recovery boundaries that couple two durable facts (#1965 4.5, 4.6, 5.4, 7.3). An interrupted replacement is
/// finished only for this instance's own catalog, and only attributable scratch is removed. Authority, its unproven
/// names and the role dispositions it implies change together, and known gaps keep their names while the journal cannot
/// be read. A role expiry settles only after every copy that was authoritative at any point under its intent is proven
/// absent under that intent.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloRecoveryBoundaryTests
{
    private static string SegmentKey(long generation, long sequence) => SiloObjectNamespace.SegmentKey(new(generation, sequence));

    private static Dictionary<string, string> CatalogFiles(SiloHarness silo) => Directory.EnumerateFiles(silo.RootPath)
        .Where(static path => Path.GetFileName(path).StartsWith(SiloStateRoot.IndexDatabaseFileName, StringComparison.Ordinal))
        .ToDictionary(static path => Path.GetFileName(path), static path => SiloHash.Sha256(File.ReadAllBytes(path)), StringComparer.Ordinal);

    [TestMethod]
    public async Task AnInterruptedReplacementIsNeverFinishedOrDiscardedForAnotherBinding()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var interrupted = () => silo.RebuildAsync(faults: new SiloTestFaults { Armed = SiloFaultPoint.AfterReplacementReady });
        await interrupted.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
        File.WriteAllBytes(Path.Combine(silo.RootPath, SiloStateRoot.IndexDatabaseFileName + SiloCatalogFiles.BuildingSuffix), [1, 2, 3]);
        var before = CatalogFiles(silo);
        before.Keys.Should().Contain(SiloStateRoot.IndexDatabaseFileName + SiloCatalogFiles.ReplacementSuffix);
        var other = new SiloStorageBinding("instance-0002", silo.Binding.StoreId, silo.Binding.Bucket);

        using (var foreign = new SiloCatalogIndex(silo.StateRoot.IndexDatabasePath, other, silo.Clock))
        {
            var open = () => foreign.InitializeAsync(CancellationToken.None).AsTask();
            (await open.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.binding-mismatch");
        }
        CatalogFiles(silo).Should().Equal(before, "another binding neither installs, discards nor cleans this instance's catalog files");
        var rebuild = () => SiloCatalog.RebuildAsync(silo.StateRoot, other, silo.Stores, silo.Authority, silo.Authority, silo.Clock,
            new(silo.Local), CancellationToken.None).AsTask();
        (await rebuild.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.binding-mismatch");
        CatalogFiles(silo).Should().Equal(before);

        await silo.RestartAsync().ConfigureAwait(false);
        CatalogFiles(silo).Keys.Where(static name => !name.EndsWith("-wal", StringComparison.Ordinal) && !name.EndsWith("-shm", StringComparison.Ordinal))
            .Should().Equal([SiloStateRoot.IndexDatabaseFileName], "the owner finishes its own replacement");
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(1), "r1", CancellationToken.None).ConfigureAwait(false))!
            .State.Should().Be("visible");
    }

    [TestMethod]
    public async Task OnlyTheExactInspectionScratchThisCodeEmitsIsRemoved()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        silo.Close();
        var token = new string('0', 24) + "0123abcd";
        string Root(string name) => Path.Combine(silo.RootPath, name);
        var index = SiloStateRoot.IndexDatabaseFileName;
        var state = SiloStateRoot.PublicationDatabaseFileName;
        // The shapes an interrupted inspection leaves: an inspected file's name, the infix, a 32-hex token, a sidecar.
        string[] owned =
        [
            Root(index + SiloCatalogFiles.InspectionInfix + token), Root(index + SiloCatalogFiles.InspectionInfix + token + "-wal"),
            Root(index + SiloCatalogFiles.ReplacementSuffix + SiloCatalogFiles.InspectionInfix + token),
            Root(state + SiloCatalogFiles.InspectionInfix + token + "-shm")
        ];
        // Names an operator could create beside them, under the same prefixes.
        string[] unrelated =
        [
            Root(index + ".backup.inspect-notes"), Root(index + ".backup" + SiloCatalogFiles.InspectionInfix + token),
            Root(index + SiloCatalogFiles.InspectionInfix + "notes"), Root(index + SiloCatalogFiles.InspectionInfix + token + ".txt"),
            Root(state + ".copy" + SiloCatalogFiles.InspectionInfix + token), Root("operator-notes.inspect-0123abcd")
        ];
        foreach (var path in owned.Concat(unrelated))
        {
            File.WriteAllBytes(path, [1, 2, 3]);
        }

        await silo.OpenAsync().ConfigureAwait(false);

        owned.Where(File.Exists).Should().BeEmpty("the scratch this code emits is removed once each database is proven its own");
        unrelated.Where(path => !File.Exists(path)).Should().BeEmpty("no operator file under a shared prefix is ever removed");
        await silo.RebuildAsync().ConfigureAwait(false);
        Directory.EnumerateFiles(silo.RootPath).Where(path => Path.GetFileName(path).Contains(".inspect-", StringComparison.Ordinal))
            .Should().BeEquivalentTo(unrelated, "a completed inspection leaves nothing of its own");
    }

    [TestMethod]
    [DataRow("cursor", DisplayName = "the cursor segment cannot be read")]
    [DataRow("past-the-gap", DisplayName = "the segment past the gap cannot be read")]
    public async Task AKnownGapKeepsItsUnprovenNamesAndIntervalWhileTheJournalTimesOut(string timedOut)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 3)).ConfigureAwait(false);
        silo.Local.Delete(SegmentKey(1, 3), silo.Local.Current(SegmentKey(1, 3))!);
        var gap = (await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false)).Authority;
        gap.UnprovenPublications.Should().ContainSingle().Which.GroupId.Should().Be(SiloDrafts.CaptureGroup(3));

        var unreadable = timedOut == "cursor" ? SegmentKey(1, 2) : SegmentKey(1, 4);
        silo.Local.ReadUnavailable = key => key == unreadable;
        await silo.CatchUpAsync().ConfigureAwait(false);

        var authority = await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false);
        authority.UnknownInterval.Should().BeTrue();
        authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 2));
        authority.UnprovenPublications.Should().ContainSingle("a read that failed proves nothing about the gap")
            .Which.GroupId.Should().Be(SiloDrafts.CaptureGroup(3));
    }

    [TestMethod]
    public async Task ThePrefixAndItsUnprovenNamesStayDisjointWhenRecoveryReachesPartWayThroughAGap()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        for (var number = 1; number <= 5; number++)
        {
            await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, number)).ConfigureAwait(false);
        }
        var third = SegmentKey(1, 3);
        var bytes = silo.Local.Read(third, silo.Local.Current(third)!);
        silo.Local.Delete(third, silo.Local.Current(third)!);
        var gap = (await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false)).Authority;
        gap.UnprovenPublications.Select(static publication => publication.GroupId)
            .Should().Equal([SiloDrafts.CaptureGroup(3), SiloDrafts.CaptureGroup(4), SiloDrafts.CaptureGroup(5)]);

        silo.Local.Plant(third, bytes);
        silo.Local.ReadUnavailable = key => key == SegmentKey(1, 5);
        await silo.CatchUpAsync().ConfigureAwait(false);

        var authority = await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false);
        authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 4), "recovery reached through segment 4 while 5 timed out");
        authority.UnknownInterval.Should().BeTrue();
        authority.UnprovenPublications.Should().OnlyContain(publication => publication.Position > authority.PrefixEnd!.Value,
            "nothing inside the authenticated prefix is also reported unproven");
        authority.UnprovenPublications.Select(static publication => publication.GroupId)
            .Should().Equal([SiloDrafts.CaptureGroup(4), SiloDrafts.CaptureGroup(5)], "known names beyond the prefix survive the timeout");
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(3), "r1", CancellationToken.None).ConfigureAwait(false))
            .Should().NotBeNull("the publication at segment 4 is now inside the prefix");
    }

    [TestMethod]
    public async Task AuthorityAndTheRoleDispositionsItImpliesChangeTogetherAcrossACrash()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var raw = new SiloRoleKey(SiloIdentityKind.Member, SiloDrafts.Raw(1), "1");
        silo.Local.Delete(silo.MemberLocator(SiloDrafts.CaptureGroup(1), raw.Id));
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None, includeAvailable: true).ConfigureAwait(false);
        (await silo.Index.ReadRoleAsync(raw, CancellationToken.None).ConfigureAwait(false))!.Disposition.Should().Be(SiloDisposition.Missing);
        // The writer commits a segment the catalog has not applied, and that tail is then lost: authority becomes unknown.
        await silo.Journal.AppendAsync([new SiloPinRecord("pin-1", "group", SiloDrafts.CaptureGroup(1), "r1", true, "operator")],
            "pin-1", CancellationToken.None).ConfigureAwait(false);
        silo.Local.Delete(SegmentKey(1, 3), silo.Local.Current(SegmentKey(1, 3))!);
        silo.Faults.Armed = SiloFaultPoint.AfterAuthorityRecorded;
        var crash = () => silo.CatchUpAsync();
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);

        await silo.RestartAsync().ConfigureAwait(false);
        await silo.CatchUpAsync().ConfigureAwait(false);

        (await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false)).UnknownInterval.Should().BeTrue();
        (await silo.Index.ReadRoleAsync(raw, CancellationToken.None).ConfigureAwait(false))!.Disposition
            .Should().Be(SiloDisposition.Unverified, "a later copy may exist in the unknown interval, whatever crashed after it opened");
    }

    [TestMethod]
    [DataRow("restored", DisplayName = "an evicted copy is restored after the intent, then evicted by another")]
    [DataRow("promoted", DisplayName = "an ineligible copy is promoted after the intent, then evicted by another")]
    public async Task ACopyReenteringTheAuthoritySetAfterARoleIntentMustSettleUnderThatIntent(string entry)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var rawBytes = SiloDrafts.Bytes("raw-1", 3001);
        var raw = new SiloObjectIdentity(SiloIdentityKind.Member, SiloDrafts.Raw(1), "1", SiloHash.Sha256(rawBytes), rawBytes.Length);
        var rawKey = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, raw.Sha256);
        var b = new SiloCopyRecord(raw, silo.Archive.Locator(rawKey, silo.Archive.Plant(rawKey, rawBytes)), SiloStoreClass.Archive,
            SiloRetrieval.Online, "replica", silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256);
        if (entry == "promoted")
        {
            await silo.AppendAsync(b).ConfigureAwait(false);
        }
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var local = silo.MemberLocator(SiloDrafts.CaptureGroup(1), raw.Id);
        if (entry == "restored")
        {
            // B is already known, and evicted under its own intent before the role intent.
            await silo.AppendAsync(b).ConfigureAwait(false);
            await EvictAsync(silo, raw, b.Locator, "evict-b-before").ConfigureAwait(false);
        }
        await silo.AppendAsync(new SiloExpiryIntentRecord("expire-raw", SiloExpiryScope.Role, raw, "last-copy-policy:operator",
            [new(local, "local", "allocation-raw", raw.Length)], "retention", LastCopy: true)).ConfigureAwait(false);
        // B enters the authority set after the intent: restored after its eviction, or promoted by a valid receipt.
        await silo.AppendAsync(entry == "restored"
            ? new SiloAvailabilityRecord(SiloAvailabilityScope.Copy, raw, SiloAvailabilityState.Restored, silo.Clock.GetUtcNow(), "found again",
                b.Locator)
            : b).ConfigureAwait(false);
        await EvictAsync(silo, raw, b.Locator, "evict-b-after").ConfigureAwait(false);
        silo.Local.Delete(local);
        await silo.AppendAsync(
            new SiloExpiryDispositionRecord("expire-raw", local, SiloExpiryOutcome.Deleted, silo.Clock.GetUtcNow(), "NoSuchVersion"),
            new SiloAvailabilityRecord(SiloAvailabilityScope.Role, raw, SiloAvailabilityState.PolicyExpired, silo.Clock.GetUtcNow(),
                "claimed", IntentId: "expire-raw")).ConfigureAwait(false);
        var role = new SiloRoleKey(SiloIdentityKind.Member, raw.Id, raw.Revision);

        foreach (var pass in new[] { "replayed", "rebuilt" })
        {
            if (pass == "rebuilt")
            {
                await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
                await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);
            }
            var status = (await silo.Index.ReadRoleAsync(role, CancellationToken.None).ConfigureAwait(false))!;
            status.Journaled.Should().BeNull($"{pass}: B re-entered the authority set under the intent and was never settled under it");
            status.Disposition.Should().NotBe(SiloDisposition.PolicyExpired);
        }
    }

    private static async Task EvictAsync(SiloHarness silo, SiloObjectIdentity role, SiloExactLocator copy, string intent)
    {
        await silo.AppendAsync(new SiloExpiryIntentRecord(intent, SiloExpiryScope.Copy, role, "operator eviction", [new(copy, "archive")],
            "headroom", LastCopy: false)).ConfigureAwait(false);
        await silo.AppendAsync(
            new SiloExpiryDispositionRecord(intent, copy, SiloExpiryOutcome.Deleted, silo.Clock.GetUtcNow(), "NoSuchVersion"),
            new SiloAvailabilityRecord(SiloAvailabilityScope.Copy, role, SiloAvailabilityState.Evicted, silo.Clock.GetUtcNow(), "proven absence",
                copy, intent)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ACopyArrivingAfterARoleIntentBlocksItsSettlementEvenWhenAnotherIntentRemovedIt()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(1));
        var local = silo.MemberLocator(capture.GroupId, raw.Id);
        await silo.AppendAsync(new SiloExpiryIntentRecord("expire-raw", SiloExpiryScope.Role, raw, "last-copy-policy:operator",
            [new(local, "local", "allocation-raw", raw.Length)], "retention", LastCopy: true)).ConfigureAwait(false);
        var arrival = silo.ReplicateToArchive(raw, local);
        await silo.AppendAsync(arrival).ConfigureAwait(false);
        await silo.AppendAsync(new SiloExpiryIntentRecord("evict-archive", SiloExpiryScope.Copy, raw, "operator eviction",
            [new(arrival.Locator, "archive")], "headroom", LastCopy: false)).ConfigureAwait(false);
        silo.Archive.Delete(arrival.Locator);
        await silo.AppendAsync(
            new SiloExpiryDispositionRecord("evict-archive", arrival.Locator, SiloExpiryOutcome.Deleted, silo.Clock.GetUtcNow(), "NoSuchVersion"),
            new SiloAvailabilityRecord(SiloAvailabilityScope.Copy, raw, SiloAvailabilityState.Evicted, silo.Clock.GetUtcNow(), "proven absence",
                arrival.Locator, "evict-archive")).ConfigureAwait(false);
        silo.Local.Delete(local);
        await silo.AppendAsync(
            new SiloExpiryDispositionRecord("expire-raw", local, SiloExpiryOutcome.Deleted, silo.Clock.GetUtcNow(), "NoSuchVersion"),
            new SiloAvailabilityRecord(SiloAvailabilityScope.Role, raw, SiloAvailabilityState.PolicyExpired, silo.Clock.GetUtcNow(),
                "claimed", IntentId: "expire-raw")).ConfigureAwait(false);
        var role = new SiloRoleKey(SiloIdentityKind.Member, raw.Id, raw.Revision);

        foreach (var pass in new[] { "incremental", "rebuilt" })
        {
            if (pass == "rebuilt")
            {
                await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
                await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);
            }
            var status = (await silo.Index.ReadRoleAsync(role, CancellationToken.None).ConfigureAwait(false))!;
            status.Journaled.Should().BeNull($"{pass}: the archive copy arrived under the intent and was never settled under it");
            status.Disposition.Should().NotBe(SiloDisposition.PolicyExpired);
        }
    }
}
