using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// Journal authority at its edges (#1965 4.5, 7.2). Surviving writer State that proves an empty journal bounds every
/// walk exactly as a committed head does; a generation witness and a replica checkpoint are bound to exact bytes; and an
/// operation key names one exact set of records, so a retry that asks for something else is a conflict.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloJournalBoundaryTests
{
    private static string SegmentKey(long generation, long sequence) => SiloObjectNamespace.SegmentKey(new(generation, sequence));

    [TestMethod]
    public async Task AJournalTheWriterProvesEmptyAdoptsNoGenesisThatLandedButNeverCommitted()
    {
        using var silo = await SiloHarness.CreateAsync(install: false).ConfigureAwait(false);
        var floors = new Dictionary<string, long> { ["capture-sequence"] = 42 };
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentPut;
        var crash = () => silo.Journal.OpenFirstGenerationAsync(floors, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
        silo.Local.Keys(SiloObjectNamespace.JournalPrefix).Should().Equal([SegmentKey(1, 1)], "the genesis landed but was never read back");
        (await silo.WriterEvidenceAsync().ConfigureAwait(false)).Head.Should().BeNull("the writer committed nothing");

        await silo.RestartAsync().ConfigureAwait(false);
        await silo.CatchUpAsync().ConfigureAwait(false);
        var caughtUp = await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false);
        caughtUp.PrefixEnd.Should().BeNull("startup adopts nothing past a known-empty committed journal");
        caughtUp.UnknownInterval.Should().BeFalse();
        (await silo.Index.ReadIdentityFloorsAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        rebuilt.Authority.PrefixEnd.Should().BeNull("a rebuild with the same evidence adopts nothing either");
        rebuilt.Authority.UnknownInterval.Should().BeFalse();
        (await silo.Index.ReadIdentityFloorsAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();

        // The writer reconciles its pending genesis with the exact bytes, and only then is it authority.
        await silo.Journal.OpenFirstGenerationAsync(floors, CancellationToken.None).ConfigureAwait(false);
        await silo.CatchUpAsync().ConfigureAwait(false);
        (await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false)).PrefixEnd.Should().Be(new SiloJournalPosition(1, 1));
        (await silo.Index.ReadIdentityFloorsAsync(CancellationToken.None).ConfigureAwait(false))["capture-sequence"].Should().Be(42);
    }

    [TestMethod]
    public async Task AnOperationKeyRetriedWithOtherRecordsIsAConflictWhetherPendingOrCommitted()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var live = new SiloLiveIssueRecord("camera-1", "camera-1/run-1/generation-1", SiloDrafts.CaptureGroup(1), "r1", 7, SiloHarness.Start);
        var changed = live with { RevisionId = "r2" };
        var key = SegmentKey(1, 3);
        var timeouts = 1;
        silo.Local.FailAfterLanding = candidate => candidate == key && timeouts-- > 0;
        var first = () => silo.Journal.AppendAsync([live], "live:camera-1:7", CancellationToken.None).AsTask();
        await first.Should().ThrowAsync<SiloStoreUnavailableException>().ConfigureAwait(false);

        var whilePending = () => silo.Journal.AppendAsync([changed], "live:camera-1:7", CancellationToken.None).AsTask();
        (await whilePending.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.operation-conflict");
        await silo.Journal.AppendAsync([live], "live:camera-1:7", CancellationToken.None).ConfigureAwait(false);
        var afterCommit = () => silo.Journal.AppendAsync([changed], "live:camera-1:7", CancellationToken.None).AsTask();
        (await afterCommit.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.operation-conflict");
        silo.JournalRecords().OfType<SiloLiveIssueRecord>().Should().ContainSingle().Which.Should().Be(live);

        var head = await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false);
        var open = new SiloGenerationOpenRecord(2, "restore", 1, new Dictionary<string, long> { ["capture-sequence"] = 100 },
            head.Position, head.Sha256);
        await silo.Journal.OpenGenerationAsync(open, CancellationToken.None).ConfigureAwait(false);
        var otherFloors = () => silo.Journal.OpenGenerationAsync(
            open with { IdentityFloors = new Dictionary<string, long> { ["capture-sequence"] = 200 } }, CancellationToken.None).AsTask();
        (await otherFloors.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.operation-conflict");
        var otherGenesis = () => silo.Journal.OpenFirstGenerationAsync(new Dictionary<string, long> { ["capture-sequence"] = 9 },
            CancellationToken.None).AsTask();
        (await otherGenesis.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.operation-conflict");
        (await silo.Journal.OpenGenerationAsync(open, CancellationToken.None).ConfigureAwait(false)).Position
            .Should().Be(new SiloJournalPosition(2, 1), "the identical retry still returns the committed opening");
    }

    [TestMethod]
    public async Task AReplicaCheckpointIsReadOnlyThroughTheExactVersionItsReceiptNames()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var anchor = await new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock)
            .WriteAsync(CancellationToken.None).ConfigureAwait(false);
        var sealed1 = anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) };
        await silo.AppendAsync(sealed1).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        foreach (var key in silo.Local.Keys(SiloObjectNamespace.JournalPrefix))
        {
            silo.Archive.Plant(key, silo.Local.Read(key, silo.Local.Current(key)!));
        }
        var checkpoint = silo.Local.Read(anchor.Checkpoint.Key, anchor.Checkpoint.VersionId);
        var receipted = silo.Archive.Locator(anchor.Checkpoint.Key, silo.Archive.Plant(anchor.Checkpoint.Key, checkpoint));
        silo.Archive.Plant(anchor.Checkpoint.Key, "{\"format\":\"something newer at the same key\"}"u8.ToArray());
        silo.Authority.TrustAnchors = true;
        var writer = await silo.WriterEvidenceAsync().ConfigureAwait(false);

        var unreceipted = await silo.DeleteCatalogAndRebuildAsync(new(silo.Archive, Writer: writer)).ConfigureAwait(false);
        unreceipted.StartKind.Should().Be("genesis", "without a receipt nothing proves which version at the replica's key is the anchor's");

        silo.Authority.ReceiptedCheckpoints[(SiloTestAuthority.Seal(sealed1), silo.Archive.Descriptor.StoreId)] = receipted;
        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Archive, Writer: writer)).ConfigureAwait(false);

        rebuilt.StartKind.Should().Be("anchor", "the receipted version still verifies although a newer one holds the key");
        rebuilt.Anchor.Should().Be(anchor.Covers);
        rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.Tail);
        rebuilt.Authority.UnknownInterval.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("later-receipt", DisplayName = "the first anchor's receipted version is gone; a later anchor's receipt reads back")]
    [DataRow("wrong-receipt", DisplayName = "the later anchor's receipt names other bytes; no anchor qualifies")]
    public async Task EveryTrustedAnchorOfACheckpointIsTriedBeforeFallingBack(string later)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var anchor = await new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock)
            .WriteAsync(CancellationToken.None).ConfigureAwait(false);
        // The same checkpoint is anchored twice, after two seals whose receipts name different replica versions.
        var first = anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) };
        var second = anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-2" }) };
        await silo.AppendAsync(first).ConfigureAwait(false);
        await silo.AppendAsync(second).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        // The replica holds only the journal after the checkpoint: replay from generation 1 is impossible there.
        foreach (var key in silo.Local.Keys(SiloObjectNamespace.JournalPrefix))
        {
            SiloObjectNamespace.TryParseSegmentKey(key, out var position).Should().BeTrue();
            if (position > anchor.Covers)
            {
                silo.Archive.Plant(key, silo.Local.Read(key, silo.Local.Current(key)!));
            }
        }
        var bytes = silo.Local.Read(anchor.Checkpoint.Key, anchor.Checkpoint.VersionId);
        var gone = silo.Archive.Locator(anchor.Checkpoint.Key, silo.Archive.Plant(anchor.Checkpoint.Key, bytes));
        silo.Archive.Delete(gone);
        var readable = silo.Archive.Locator(anchor.Checkpoint.Key,
            silo.Archive.Plant(anchor.Checkpoint.Key, later == "later-receipt" ? bytes : "{\"format\":\"other bytes\"}"u8.ToArray()));
        silo.Authority.ReceiptedCheckpoints[(SiloTestAuthority.Seal(first), silo.Archive.Descriptor.StoreId)] = gone;
        silo.Authority.ReceiptedCheckpoints[(SiloTestAuthority.Seal(second), silo.Archive.Descriptor.StoreId)] = readable;
        silo.Authority.TrustAnchors = true;

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Archive, Writer: await silo.WriterEvidenceAsync().ConfigureAwait(false)))
            .ConfigureAwait(false);

        if (later == "later-receipt")
        {
            rebuilt.StartKind.Should().Be("anchor", "the second anchor of the same checkpoint qualifies when the first cannot");
            rebuilt.Anchor.Should().Be(anchor.Covers);
            rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.Tail);
            rebuilt.Authority.UnknownInterval.Should().BeFalse();
            (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(1), "r1", CancellationToken.None).ConfigureAwait(false))!
                .State.Should().Be("visible", "the checkpoint carries what the replica's journal no longer holds");
        }
        else
        {
            rebuilt.StartKind.Should().Be("genesis", "a receipt that names other bytes never qualifies its anchor");
            rebuilt.Authority.PrefixEnd.Should().BeNull();
            rebuilt.Authority.UnknownInterval.Should().BeTrue();
        }
    }

    /// <summary>The same opening segment with one field of its opening record replaced by a JSON value.</summary>
    internal static byte[] WithOpeningField(byte[] segment, string field, string valueJson)
    {
        var node = JsonNode.Parse(segment)!;
        node["records"]![0]![field] = JsonNode.Parse(valueJson);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }
}
