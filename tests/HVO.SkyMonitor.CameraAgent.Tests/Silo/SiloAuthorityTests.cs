using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// The authenticated journal prefix, the complete-publication frontier and the authority-unknown interval are separate
/// claims (#1965 4.5, 5.3, 5.4; #1967 acceptance 2 and 4). These tests break the journal in the ways the fault matrix
/// names and check that each claim stops exactly where its evidence stops.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloAuthorityTests
{
    private static readonly int[] RemappedCaptures = [1, 2];
    private static readonly string[] WitnessSet = ["backup", "archive"];

    private static async Task<(SiloPublishRecord A, SiloPublishRecord B)> PublishACopyBAsync(SiloHarness silo)
    {
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var a = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var b = silo.PublishRecord(SiloDrafts.CaptureGroup(2));
        await silo.AppendAsync(
            silo.ReplicateToArchive(SiloHarness.ManifestIdentity(a), a.Manifest.Locator),
            silo.ReplicateToArchive(SiloHarness.Identity(a, SiloDrafts.Raw(1)), silo.MemberLocator(a.GroupId, SiloDrafts.Raw(1))),
            silo.ReplicateToArchive(SiloHarness.Identity(a, SiloDrafts.Preview(1)), silo.MemberLocator(a.GroupId, SiloDrafts.Preview(1))))
            .ConfigureAwait(false);
        return (a, b);
    }

    private static void LoseLocal(SiloHarness silo, SiloPublishRecord publish)
    {
        silo.Local.Delete(publish.Manifest.Locator);
        foreach (var member in publish.Members)
        {
            silo.Local.Delete(member.Locator);
        }
    }

    private static string SegmentKey(long generation, long sequence) => SiloObjectNamespace.SegmentKey(new(generation, sequence));

    [TestMethod]
    public async Task TheFrontierStopsBeforeAnUnreplicatedManifestWhileAnArchivedRevisionReopens()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var (a, b) = await PublishACopyBAsync(silo).ConfigureAwait(false);
        LoseLocal(silo, a);
        silo.Local.Delete(b.Manifest.Locator);
        var bRaw = silo.MemberLocator(b.GroupId, SiloDrafts.Raw(2));
        silo.Local.ReadUnavailable = key => key == bRaw.Key;

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);

        rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.Tail);
        rebuilt.Authority.UnknownInterval.Should().BeFalse();
        rebuilt.Authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 4), "the prefix reaches copy A");
        rebuilt.Authority.FirstIncompletePublication.Should().Be(new SiloPublicationRef(b.GroupId, "r1", new(1, 3), 0));
        var reopened = await silo.Catalog.ReopenAsync(a.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        reopened.Reopenable.Should().BeTrue();
        reopened.Items.Should().OnlyContain(item => item.ServedFrom!.StoreId == silo.Archive.Descriptor.StoreId);
        var closed = await silo.Index.ReadPublicationAsync(b.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        closed!.State.Should().Be("closed");
        closed.ManifestStatus.Should().Be(SiloDisposition.Missing);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);
        (await silo.Index.ReadRoleAsync(new(SiloIdentityKind.Member, SiloDrafts.Raw(2), "1"), CancellationToken.None).ConfigureAwait(false))!
            .Disposition.Should().Be(SiloDisposition.TemporarilyUnavailable, "B's raw keeps its own evidence");
    }

    [TestMethod]
    [DataRow("deleted")]
    [DataRow("damaged")]
    [DataRow("altered")]
    public async Task AMissingOrAlteredSegmentEndsAuthorityAndLeavesLaterCopiesIneligible(string fault)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var (a, b) = await PublishACopyBAsync(silo).ConfigureAwait(false);
        LoseLocal(silo, a);
        var key = SegmentKey(1, 3);
        switch (fault)
        {
            case "deleted":
                silo.Local.Delete(key, silo.Local.Current(key)!);
                break;
            case "damaged":
                silo.Local.Damage(key, silo.Local.Current(key)!);
                break;
            default:
                // Still a well-formed, correctly chained segment; only its successor's hash exposes the change.
                var altered = JsonNode.Parse(silo.Local.Read(key, silo.Local.Current(key)!))!;
                altered["writtenUtc"] = "2030-01-01T00:00:00+00:00";
                silo.Local.Delete(key, silo.Local.Current(key)!);
                silo.Local.Plant(key, System.Text.Encoding.UTF8.GetBytes(altered.ToJsonString()));
                break;
        }

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);

        rebuilt.Authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 2));
        rebuilt.Authority.UnknownInterval.Should().BeTrue();
        rebuilt.Authority.EndReason.Should().BeOneOf(SiloPrefixEnd.Gap, SiloPrefixEnd.ChainMismatch, SiloPrefixEnd.Invalid);
        var closed = await silo.Index.ReadPublicationAsync(a.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);
        closed!.ManifestStatus.Should().Be(SiloDisposition.Unverified, "absence cannot be concluded while a later copy may exist");
        var raw = await silo.Index.ReadRoleAsync(new(SiloIdentityKind.Member, SiloDrafts.Raw(1), "1"), CancellationToken.None).ConfigureAwait(false);
        raw!.Disposition.Should().Be(SiloDisposition.Unverified);
        raw.Locators.Should().OnlyContain(locator => locator.Source == "named", "copy A past the prefix is not eligible, whatever its bytes");
        (await silo.Index.ReadPublicationAsync(b.GroupId, "r1", CancellationToken.None).ConfigureAwait(false)).Should().BeNull();
    }

    [TestMethod]
    public async Task APublicationPastTheGapIsAuthorityUnprovenAndClaimsNothing()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 9)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        silo.Local.Delete(SegmentKey(1, 2), silo.Local.Current(SegmentKey(1, 2))!);

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);

        rebuilt.Authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 1));
        rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.Gap);
        rebuilt.Authority.UnknownInterval.Should().BeTrue();
        rebuilt.Authority.UnprovenPublications.Select(static publication => publication.GroupId)
            .Should().Equal(SiloDrafts.CaptureGroup(1), SiloDrafts.CaptureGroup(2));
        (await silo.Catalog.ReopenAsync(SiloDrafts.CaptureGroup(1), "r1", CancellationToken.None).ConfigureAwait(false)).Found.Should().BeFalse();
        (await silo.Index.QueryVisibleAsync(SiloHarness.Start, SiloHarness.Start.AddHours(1), null, 10, CancellationToken.None)
            .ConfigureAwait(false)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task ARemapAfterAnUnwitnessedGenerationIsIneligibleAndAWitnessedOneReopensOnlyWithMatchingBytes()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var remaps = new List<SiloJournalRecord>();
        foreach (var number in RemappedCaptures)
        {
            var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(number));
            var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(number));
            var lost = silo.MemberLocator(capture.GroupId, raw.Id);
            silo.Local.Delete(lost);
            var restored = silo.Local.Plant(lost.Key, number == 1 ? SiloDrafts.Bytes("raw-1", (int)raw.Length) : SiloDrafts.Bytes("other", (int)raw.Length));
            remaps.Add(new SiloRemapRecord(raw, lost, silo.Local.Locator(lost.Key, restored), silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256, true));
        }
        var head = await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false);
        var floor = 2L << 40;
        await silo.Journal.OpenGenerationAsync(new SiloGenerationOpenRecord(2, "restore", 1,
            new Dictionary<string, long> { ["capture-sequence"] = floor }, head.Position, head.Sha256,
            Witnesses: SiloJson.SerializeToElement(new { set = WitnessSet })), CancellationToken.None).ConfigureAwait(false);
        await silo.Journal.AppendAsync(remaps, "remaps-after-restore", CancellationToken.None).ConfigureAwait(false);

        var unwitnessed = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);

        unwitnessed.Authority.EndReason.Should().Be(SiloPrefixEnd.UnprovenGeneration);
        unwitnessed.Authority.PrefixEnd.Should().Be(head.Position);
        unwitnessed.Authority.UnknownInterval.Should().BeTrue();
        var raw1 = new SiloRoleKey(SiloIdentityKind.Member, SiloDrafts.Raw(1), "1");
        (await silo.Index.ReadRoleAsync(raw1, CancellationToken.None).ConfigureAwait(false))!.Disposition.Should().Be(SiloDisposition.Unverified);

        silo.Authority.WitnessedOpenings.Add((2, head.Sha256!));
        var witnessed = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);

        witnessed.Authority.EndReason.Should().Be(SiloPrefixEnd.Tail);
        witnessed.Authority.PrefixEnd.Should().Be(new SiloJournalPosition(2, 2));
        (await silo.Index.ReadRoleAsync(raw1, CancellationToken.None).ConfigureAwait(false))!.AvailableAt
            .Should().Be(((SiloRemapRecord)remaps[0]).To);
        (await silo.Index.ReadRoleAsync(new(SiloIdentityKind.Member, SiloDrafts.Raw(2), "1"), CancellationToken.None).ConfigureAwait(false))!
            .Disposition.Should().Be(SiloDisposition.Missing, "a witnessed remap reopens only when its exact bytes match");
        (await silo.Index.ReadIdentityFloorsAsync(CancellationToken.None).ConfigureAwait(false))["capture-sequence"].Should().Be(floor);
    }

    [TestMethod]
    public async Task ReplayStartsAtTheNewestTrustedAnchorAndFallsBackWhenItsCheckpointIsDamaged()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var checkpoints = new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var first = await checkpoints.WriteAsync(CancellationToken.None).ConfigureAwait(false);
        await silo.AppendAsync(first with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-1" }) }).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 3)).ConfigureAwait(false);
        checkpoints = new SiloCheckpointWriter(silo.Index, silo.StateRoot.Spool, silo.Local, silo.Binding, silo.Clock);
        var second = await checkpoints.WriteAsync(CancellationToken.None).ConfigureAwait(false);
        await silo.AppendAsync(second with { SealEvidence = SiloJson.SerializeToElement(new { backup = "sealed-2" }) }).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 4)).ConfigureAwait(false);
        var before = await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false);
        silo.Authority.TrustAnchors = true;

        async Task<SiloRebuildResult> RebuildAsync()
        {
            var result = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
            await silo.Catalog.VerifyRolesAsync(10_000, CancellationToken.None).ConfigureAwait(false);
            return result;
        }

        var newest = await RebuildAsync().ConfigureAwait(false);
        newest.StartKind.Should().Be("anchor");
        newest.Anchor.Should().Be(second.Covers);
        (await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false)).Should().Be(before);

        var removable = SiloJournalRetention.RemovableThrough([first.Covers, second.Covers]);
        removable.Should().Be(first.Covers);
        foreach (var key in silo.Local.Keys(SiloObjectNamespace.JournalPrefix))
        {
            SiloObjectNamespace.TryParseSegmentKey(key, out var position).Should().BeTrue();
            if (position <= removable!.Value)
            {
                silo.Local.Delete(key, silo.Local.Current(key)!);
            }
        }
        var compacted = await RebuildAsync().ConfigureAwait(false);
        compacted.Anchor.Should().Be(second.Covers);
        (await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false)).Should().Be(before);

        silo.Local.Damage(second.Checkpoint);
        var fallback = await RebuildAsync().ConfigureAwait(false);
        fallback.Anchor.Should().Be(first.Covers, "a damaged newest checkpoint falls back to the previous trusted anchor");
        (await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false)).Should().Be(before);

        silo.Authority.TrustAnchors = false;
        var untrusted = await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        untrusted.StartKind.Should().Be("genesis");
        untrusted.Authority.PrefixEnd.Should().BeNull("an untrusted checkpoint authenticates nothing and generation 1 was compacted");
        untrusted.Authority.UnknownInterval.Should().BeTrue();
        (await silo.Index.QueryVisibleAsync(SiloHarness.Start, SiloHarness.Start.AddHours(1), null, 10, CancellationToken.None)
            .ConfigureAwait(false)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task ACatchUpThatReadsTheCommittedHeadAgainClearsAnEarlierGapAndItsUnprovenList()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 3)).ConfigureAwait(false);
        var key = SegmentKey(1, 3);
        var bytes = silo.Local.Read(key, silo.Local.Current(key)!);
        silo.Local.Delete(key, silo.Local.Current(key)!);

        var gap = (await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false)).Authority;

        gap.EndReason.Should().Be(SiloPrefixEnd.Gap);
        gap.UnknownInterval.Should().BeTrue();
        gap.UnprovenPublications.Should().ContainSingle().Which.GroupId.Should().Be(SiloDrafts.CaptureGroup(3));

        silo.Local.Plant(key, bytes);
        await silo.CatchUpAsync().ConfigureAwait(false);
        var tail = await silo.Index.ReadAuthorityAsync(CancellationToken.None).ConfigureAwait(false);

        tail.EndReason.Should().Be(SiloPrefixEnd.Tail);
        tail.UnknownInterval.Should().BeFalse();
        tail.UnprovenPublications.Should().BeEmpty("a list from an earlier interruption never outlives it");
    }

    [TestMethod]
    public void OnlySegmentsBehindThePreviousTrustedAnchorMayBeRemoved()
    {
        SiloJournalRetention.RemovableThrough([]).Should().BeNull();
        SiloJournalRetention.RemovableThrough([new(1, 5)]).Should().BeNull("a damaged only anchor must still fall back to generation 1");
        SiloJournalRetention.RemovableThrough([new(1, 9), new(1, 5)]).Should().Be(new SiloJournalPosition(1, 5));
        SiloJournalRetention.RemovableThrough([new(1, 9), new(2, 3), new(1, 5)]).Should().Be(new SiloJournalPosition(1, 9));
    }

    [TestMethod]
    public async Task AJournalReplicaThatMayLagReportsAnUnknownIntervalInsteadOfLoss()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        foreach (var key in new[] { SegmentKey(1, 1), SegmentKey(1, 2) })
        {
            silo.Archive.Plant(key, silo.Local.Read(key, silo.Local.Current(key)!));
        }
        silo.Local.Delete(silo.MemberLocator(SiloDrafts.CaptureGroup(1), SiloDrafts.Raw(1)));
        var head = await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false);
        var raw1 = new SiloRoleKey(SiloIdentityKind.Member, SiloDrafts.Raw(1), "1");

        var lagging = await silo.DeleteCatalogAndRebuildAsync(new(silo.Archive, SourceMayBeIncomplete: true)).ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);

        lagging.Authority.EndReason.Should().Be(SiloPrefixEnd.PossiblyIncompleteTail);
        lagging.Authority.UnknownInterval.Should().BeTrue();
        lagging.Authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 2));
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(1), "r1", CancellationToken.None).ConfigureAwait(false))!
            .State.Should().Be("visible");
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(2), "r1", CancellationToken.None).ConfigureAwait(false)).Should().BeNull();
        (await silo.Index.ReadRoleAsync(raw1, CancellationToken.None).ConfigureAwait(false))!.Disposition.Should().Be(SiloDisposition.Unverified);

        var writer = await silo.WriterEvidenceAsync().ConfigureAwait(false);
        var behindHead = await silo.DeleteCatalogAndRebuildAsync(new(silo.Archive, Writer: writer))
            .ConfigureAwait(false);
        behindHead.Authority.UnknownInterval.Should().BeTrue("the surviving writer head proves the replica lost its tail");

        var authoritative = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local, Writer: writer))
            .ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);
        authoritative.Authority.EndReason.Should().Be(SiloPrefixEnd.Tail);
        authoritative.Authority.UnknownInterval.Should().BeFalse();
        (await silo.Index.ReadRoleAsync(raw1, CancellationToken.None).ConfigureAwait(false))!.Disposition.Should().Be(SiloDisposition.Missing);
    }

    [TestMethod]
    public async Task AnAlteredTailThatStillParsesIsRejectedAgainstTheWritersKnownHead()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var head = await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false);
        var tailKey = SegmentKey(1, 3);
        var original = JsonNode.Parse(silo.Local.Read(tailKey, silo.Local.Current(tailKey)!))!;
        original["writtenUtc"] = "2030-01-01T00:00:00+00:00";
        silo.Local.Delete(tailKey, silo.Local.Current(tailKey)!);
        silo.Local.Plant(tailKey, System.Text.Encoding.UTF8.GetBytes(original.ToJsonString()));

        var rebuilt = await silo.DeleteCatalogAndRebuildAsync(new(silo.Local, Writer: await silo.WriterEvidenceAsync().ConfigureAwait(false)))
            .ConfigureAwait(false);

        rebuilt.Authority.PrefixEnd.Should().Be(new SiloJournalPosition(1, 2));
        rebuilt.Authority.EndReason.Should().Be(SiloPrefixEnd.ChainMismatch);
        rebuilt.Authority.UnknownInterval.Should().BeTrue();
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(2), "r1", CancellationToken.None).ConfigureAwait(false)).Should().BeNull();
    }
}
