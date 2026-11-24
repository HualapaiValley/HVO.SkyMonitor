using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// The writer's committed head is the authority the catalog follows while instance State survives (#1965 4.5, 5.3).
/// Catch-up and rebuild never apply a segment the writer has not committed or whose bytes differ from what it
/// committed, an authority-unknown interval lasts until the committed head is read again, and writer transitions are
/// atomic and idempotent across crashes and retries.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
public sealed class SiloWriterAuthorityTests
{
    private static string SegmentKey(long generation, long sequence) => SiloObjectNamespace.SegmentKey(new(generation, sequence));

    private static void AlterInPlace(SiloHarness silo, string key)
    {
        // Still well-formed and correctly chained to its predecessor; only its own bytes differ from what was committed.
        var node = JsonNode.Parse(silo.Local.Read(key, silo.Local.Current(key)!))!;
        node["writtenUtc"] = "2030-01-01T00:00:00+00:00";
        silo.Local.Delete(key, silo.Local.Current(key)!);
        silo.Local.Plant(key, Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    [TestMethod]
    public async Task StartupNeverAppliesATailWhoseBytesDifferFromTheWritersCommittedHead()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
        var crash = () => silo.Publisher.PublishAsync(draft.GroupId, "r1", CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        AlterInPlace(silo, SegmentKey(1, 2));

        await silo.RestartAsync().ConfigureAwait(false);
        await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);

        (await silo.Index.ReadPublicationAsync(draft.GroupId, "r1", CancellationToken.None).ConfigureAwait(false))
            .Should().BeNull("the altered tail is not the segment the writer committed");
        var authority = await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false);
        authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 1));
        authority.EndReason.Should().Be(SiloPrefixEnd.ChainMismatch);
        authority.UnknownInterval.Should().BeTrue();
        silo.Payloads.Released.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUnknownIntervalLastsAcrossStartupUntilTheCommittedHeadIsReadAgain()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var tailKey = SegmentKey(1, 3);
        var tail = silo.Local.Read(tailKey, silo.Local.Current(tailKey)!);
        silo.Local.Delete(tailKey, silo.Local.Current(tailKey)!);

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        rebuilt.Authority.UnknownInterval.Should().BeTrue("the surviving writer head proves a lost tail");

        await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);
        var stillUnknown = await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false);
        stillUnknown.UnknownInterval.Should().BeTrue("an ordinary tail is not the committed head");
        stillUnknown.PrefixEnd.Should().Be(new SiloJournalPosition(1, 2));

        silo.Local.Plant(tailKey, tail);
        await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false);
        var healed = await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false);
        healed.UnknownInterval.Should().BeFalse("reading the committed head again heals the interval");
        healed.PrefixEnd.Should().Be(new SiloJournalPosition(1, 3));
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(2), "r1", CancellationToken.None).ConfigureAwait(false))!
            .State.Should().Be("visible");
    }

    [TestMethod]
    public async Task AnEarlierMissingObservationNeverOutranksATimeoutOrAnUnknownInterval()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(1));
        await silo.AppendAsync(new SiloAvailabilityRecord(SiloAvailabilityScope.Role, raw, SiloAvailabilityState.Missing,
            silo.Clock.GetUtcNow(), "an earlier audit found no copy")).ConfigureAwait(false);
        var local = silo.MemberLocator(capture.GroupId, raw.Id);
        silo.Local.ReadUnavailable = key => key == local.Key;
        var role = new SiloRoleKey(SiloIdentityKind.Member, raw.Id, raw.Revision);

        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);
        (await silo.Index.ReadRoleAsync(role, CancellationToken.None).ConfigureAwait(false))!.Disposition
            .Should().Be(SiloDisposition.TemporarilyUnavailable, "a source that cannot be checked proves nothing");

        silo.Local.ReadUnavailable = null;
        silo.Local.Delete(local);
        foreach (var key in silo.Local.Keys(SiloObjectNamespace.JournalPrefix))
        {
            silo.Archive.Plant(key, silo.Local.Read(key, silo.Local.Current(key)!));
        }
        await silo.DeleteCatalogAndRebuildAsync(new(silo.Archive, SourceMayBeIncomplete: true, Writer: await silo.WriterEvidenceAsync().ConfigureAwait(false)))
            .ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);
        (await silo.Index.ReadRoleAsync(role, CancellationToken.None).ConfigureAwait(false))!.Disposition
            .Should().Be(SiloDisposition.Unverified, "a later copy may exist in the authority-unknown interval");
    }

    [TestMethod]
    public async Task ACrashWhileOpeningAGenerationLeavesTheSameOpeningResumable()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var head = await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false);
        var open = new SiloGenerationOpenRecord(2, "restore", 1, new Dictionary<string, long> { ["capture-sequence"] = 2L << 40 },
            head.Position, head.Sha256);
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentSpooled;
        var crash = () => silo.Journal.OpenGenerationAsync(open, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);

        await silo.RestartAsync().ConfigureAwait(false);
        await silo.Journal.OpenGenerationAsync(open, CancellationToken.None).ConfigureAwait(false);

        var opened = await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false);
        opened.Position.Should().Be(new SiloJournalPosition(2, 1));
        silo.Local.Keys(SiloObjectNamespace.JournalPrefix).Should().Contain(SegmentKey(2, 1)).And.HaveCount(3);
    }

    [TestMethod]
    public async Task RetryingAnUncertainAppendCommitsTheOperationOnce()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var live = new SiloLiveIssueRecord("camera-1", "camera-1/run-1/generation-1", SiloDrafts.CaptureGroup(1), "r1", 7, SiloHarness.Start);
        var key = SegmentKey(1, 3);
        var timeouts = 1;
        silo.Local.FailAfterLanding = candidate => candidate == key && timeouts-- > 0;

        var first = () => silo.Journal.AppendAsync([live], "live:camera-1:7", CancellationToken.None).AsTask();
        await first.Should().ThrowAsync<SiloStoreUnavailableException>().ConfigureAwait(false);
        await silo.Journal.AppendAsync([live], "live:camera-1:7", CancellationToken.None).ConfigureAwait(false);
        await silo.CatchUpAsync().ConfigureAwait(false);

        silo.JournalRecords().OfType<SiloLiveIssueRecord>().Should().ContainSingle();
        (await silo.Index.ReadLiveHistoryAsync("camera-1", CancellationToken.None).ConfigureAwait(false)).Should().ContainSingle();

        // A retry after a commit whose acknowledgment the caller never saw returns that committed segment.
        var pin = new SiloPinRecord("pin-1", "group", SiloDrafts.CaptureGroup(1), "r1", true, "operator");
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
        var lost = () => silo.Journal.AppendAsync([pin], "pin:1", CancellationToken.None).AsTask();
        await lost.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        var again = await silo.Journal.AppendAsync([pin], "pin:1", CancellationToken.None).ConfigureAwait(false);

        again.Position.Should().Be(new SiloJournalPosition(1, 4));
        silo.JournalRecords().OfType<SiloPinRecord>().Should().ContainSingle();
    }

    [TestMethod]
    [DataRow("reason", "\"replacement\"", DisplayName = "another opening differs only in its reason")]
    [DataRow("highestPriorGeneration", "0", DisplayName = "another opening differs only in its prior-generation evidence")]
    public async Task AWitnessBindsToTheExactOpeningBytesNotToItsGenerationAndPrefix(string field, string valueJson)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var head = await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false);
        var open = new SiloGenerationOpenRecord(2, "restore", 1, new Dictionary<string, long>(), head.Position, head.Sha256);
        var opening = await silo.Journal.OpenGenerationAsync(open, CancellationToken.None).ConfigureAwait(false);
        await silo.Journal.AppendAsync([new SiloPinRecord("pin-1", "group", SiloDrafts.CaptureGroup(1), "r1", true, "operator")],
            "pin-1", CancellationToken.None).ConfigureAwait(false);
        var key = SegmentKey(2, 1);
        var bytes = silo.Local.Read(key, silo.Local.Current(key)!);
        // The same generation, continuing the same prefix end, but a different opening in exactly one field.
        var other = SiloJournalBoundaryTests.WithOpeningField(bytes, field, valueJson);
        SiloHash.Sha256(other).Should().NotBe(opening.Sha256);

        silo.Authority.WitnessedOpenings.Add((2, SiloHash.Sha256(other)));
        var unproven = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        unproven.Authority.EndReason.Should().Be(SiloPrefixEnd.UnprovenGeneration, "a witness of another opening proves nothing here");
        unproven.Authority.PrefixEnd.Should().Be(head.Position);

        silo.Authority.WitnessedOpenings.Add((2, opening.Sha256));
        var witnessed = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        witnessed.Authority.PrefixEnd.Should().Be(new SiloJournalPosition(2, 2));
    }

    [TestMethod]
    public async Task TheAnchorsExactCheckpointVersionIsUsedEvenWhenItsKeyHasANewerVersion()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var anchor = await new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock)
            .WriteAsync(CancellationToken.None).ConfigureAwait(false);
        await silo.AppendAsync(anchor with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) }).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        silo.Local.Plant(anchor.Checkpoint.Key, "{\"format\":\"something newer at the same key\"}"u8.ToArray());
        silo.Authority.TrustAnchors = true;

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);

        rebuilt.StartKind.Should().Be("anchor");
        rebuilt.Anchor.Should().Be(anchor.Covers);
        rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.Tail);
    }

    [TestMethod]
    public async Task AnOversizedCheckpointFallsBackToAnOlderAnchorAndTheWriterNeverProducesOne()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var first = await new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock)
            .WriteAsync(CancellationToken.None).ConfigureAwait(false);
        await silo.AppendAsync(first with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) }).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var second = await new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock)
            .WriteAsync(CancellationToken.None).ConfigureAwait(false);
        await silo.AppendAsync(second with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-2" }) }).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 3)).ConfigureAwait(false);
        silo.Authority.TrustAnchors = true;
        second.CheckpointLength.Should().BeGreaterThan(first.CheckpointLength);

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local, Writer: await silo.WriterEvidenceAsync().ConfigureAwait(false),
            MaximumCheckpointBytes: (first.CheckpointLength + second.CheckpointLength) / 2)).ConfigureAwait(false);

        rebuilt.Anchor.Should().Be(first.Covers, "a checkpoint beyond the reader's bound is skipped, never fatal");
        rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.Tail);

        var checkpoints = silo.Local.Keys(SiloObjectNamespace.CheckpointPrefix).Count;
        var bounded = new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock, maximumBytes: 64);
        var write = () => bounded.WriteAsync(CancellationToken.None).AsTask();
        await write.Should().ThrowAsync<SiloCheckpointTooLargeException>().ConfigureAwait(false);
        silo.Local.Keys(SiloObjectNamespace.CheckpointPrefix).Should().HaveCount(checkpoints, "nothing unreadable is written");
    }
}
