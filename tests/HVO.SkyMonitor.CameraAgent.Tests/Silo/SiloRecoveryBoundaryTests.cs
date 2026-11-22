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
    public async Task InspectionScratchLeftByACrashIsRemovedAndUnrelatedFilesAreNot()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        silo.Close();
        var scratch = Path.Combine(silo.RootPath, SiloStateRoot.IndexDatabaseFileName + SiloCatalogFiles.InspectionInfix + "0123abcd");
        var stateScratch = Path.Combine(silo.RootPath, SiloStateRoot.PublicationDatabaseFileName + SiloCatalogFiles.InspectionInfix + "4567ef");
        var unrelated = Path.Combine(silo.RootPath, "operator-notes.inspect-0123abcd");
        foreach (var path in new[] { scratch, scratch + "-wal", stateScratch, unrelated })
        {
            File.WriteAllBytes(path, [1, 2, 3]);
        }

        await silo.OpenAsync().ConfigureAwait(false);

        File.Exists(scratch).Should().BeFalse();
        File.Exists(scratch + "-wal").Should().BeFalse();
        File.Exists(stateScratch).Should().BeFalse();
        File.Exists(unrelated).Should().BeTrue("only scratch attributable to a catalog file is removed");
        await silo.RebuildAsync().ConfigureAwait(false);
        Directory.EnumerateFiles(silo.RootPath).Select(Path.GetFileName)
            .Where(static name => name!.Contains(SiloCatalogFiles.InspectionInfix, StringComparison.Ordinal))
            .Should().Equal(["operator-notes.inspect-0123abcd"], "a completed inspection leaves nothing");
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
