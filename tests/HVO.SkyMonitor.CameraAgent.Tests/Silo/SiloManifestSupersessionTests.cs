using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// A payload version lost before P3 is recovered before the commit (#1965 4.3, 5.3). The member is recreated from its
/// spooled bytes, and a manifest already built from the lost locator is superseded by a new one. Each exact manifest
/// lives under its own digest key beneath the same logical revision, so the superseded version is never overwritten or
/// deleted and is handed to retention. The logical revision, and the one manifest its single P3 names, never change for
/// a staged locator, and an uncertain P3 is always reconciled with its exact bytes rather than replaced.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloManifestSupersessionTests
{
    private static async Task CrashAsync(SiloHarness silo, SiloGroupDraft draft, SiloFaultPoint point)
    {
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        silo.Faults.Armed = point;
        var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
    }

    private static string RawKey(SiloGroupDraft draft) => SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, draft.Members[0].Sha256);

    private static string Prefix(SiloGroupDraft draft) => SiloObjectNamespace.ManifestRevisionPrefix(draft.FromUtc, draft.GroupId, draft.RevisionId);

    /// <summary>Lose the raw payload's named version and return it, with the manifest version and bytes it superseded.</summary>
    private static async Task<(string LostPayload, SiloExactLocator Manifest, byte[] ManifestBytes)> LosePayloadAfterManifestAsync(
        SiloHarness silo, SiloGroupDraft draft)
    {
        var group = (await silo.State.ReadGroupAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        group.ManifestVersionId.Should().NotBeNull("the manifest was qualified before the payload was lost");
        var manifest = silo.Local.Locator(
            SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, group.ManifestSha256!), group.ManifestVersionId!);
        var bytes = silo.Local.Read(manifest.Key, manifest.VersionId);
        silo.Local.Delete(RawKey(draft), group.Members[0].VersionId!);
        return (group.Members[0].VersionId!, manifest, bytes);
    }

    /// <summary>The published revision is the original logical one, named once, under the digest key of its exact manifest.</summary>
    private static SiloPublishRecord AssertSinglePublication(SiloHarness silo, SiloGroupDraft draft)
    {
        var publish = silo.JournalRecords().OfType<SiloPublishRecord>()
            .Should().ContainSingle(record => record.GroupId == draft.GroupId, "a staged locator never produces a second P3").Which;
        publish.RevisionId.Should().Be(draft.RevisionId, "a replaced staged locator never changes the logical revision");
        var bytes = silo.Local.Read(publish.Manifest.Locator.Key, publish.Manifest.Locator.VersionId);
        SiloHash.Sha256(bytes).Should().Be(publish.Manifest.Sha256);
        publish.Manifest.Locator.Key.Should().Be(
            SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, publish.Manifest.Sha256));
        var manifest = SiloJson.ReadManifest(bytes, "manifest");
        manifest.RevisionId.Should().Be(draft.RevisionId);
        manifest.Members.Select(static member => member.Locator).Should().Equal(publish.Members.Select(static member => member.Locator));
        foreach (var member in publish.Members)
        {
            SiloHash.Sha256(silo.Local.Read(member.Locator.Key, member.Locator.VersionId)).Should().Be(member.Sha256);
        }
        return publish;
    }

    [TestMethod]
    public async Task APayloadLostAfterItsManifestWasWrittenIsRecoveredBeforeTheCommitUnderANewDigestKey()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 3);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterManifestQualified).ConfigureAwait(false);
        var (lostPayload, superseded, supersededBytes) = await LosePayloadAfterManifestAsync(silo, draft).ConfigureAwait(false);

        await silo.RestartAsync().ConfigureAwait(false);
        var outcome = (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single();

        outcome.State.Should().Be(SiloStagedState.Visible);
        outcome.BlockedCode.Should().BeNull("recovery before P3 never waits for retention");
        var publish = AssertSinglePublication(silo, draft);
        publish.Manifest.Locator.Key.Should().NotBe(superseded.Key, "the new manifest has its own digest key");
        publish.Members[0].Locator.VersionId.Should().NotBe(lostPayload, "a lost version is never named by the commit");
        silo.Local.Keys(Prefix(draft)).Should().BeEquivalentTo([superseded.Key, publish.Manifest.Locator.Key]);
        silo.Local.VersionsOf(superseded.Key).Should().Equal([superseded.VersionId], "the superseded version is never overwritten or deleted");
        silo.Local.Read(superseded.Key, superseded.VersionId).Should().Equal(supersededBytes);
        (await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false)).Should().ContainSingle()
            .Which.Locator.Should().Be(superseded, "the never-committed exact locator is handed to retention");
        silo.Payloads.Released.Keys.Should().BeEquivalentTo(draft.Members.Select(static member => member.SourceToken));

        // The original logical revision rebuilds and reopens from the committed manifest; the orphan plays no part.
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!
            .State.Should().Be("visible");
        var reopened = await silo.Catalog.ReopenAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
        reopened.Reopenable.Should().BeTrue();
        reopened.Items.Single(item => item.Source == "manifest").ServedFrom.Should().Be(publish.Manifest.Locator);
        reopened.Items.Single(item => item.Role.Id == SiloDrafts.Raw(3)).ServedFrom.Should().Be(publish.Members[0].Locator);
    }

    [TestMethod]
    [DataRow(nameof(SiloFaultPoint.AfterPayloadPut), DisplayName = "replacement payload landed, not yet named")]
    [DataRow(nameof(SiloFaultPoint.AfterPayloadReplaced), DisplayName = "member replaced and manifest superseded, no new manifest yet")]
    [DataRow(nameof(SiloFaultPoint.AfterManifestSpooled), DisplayName = "new manifest spooled, not written")]
    [DataRow(nameof(SiloFaultPoint.AfterManifestPut), DisplayName = "new manifest landed, not yet named")]
    [DataRow(nameof(SiloFaultPoint.AfterManifestQualified), DisplayName = "new manifest qualified, not committed")]
    [DataRow(nameof(SiloFaultPoint.AfterSegmentPut), DisplayName = "publish segment landed, not committed")]
    [DataRow(nameof(SiloFaultPoint.AfterSegmentCommitted), DisplayName = "committed, not yet visible")]
    public async Task ACrashAnywhereAroundTheReplacementResumesToOneCommitOfTheOriginalRevision(string point)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 3);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterManifestQualified).ConfigureAwait(false);
        var (_, superseded, supersededBytes) = await LosePayloadAfterManifestAsync(silo, draft).ConfigureAwait(false);
        await silo.RestartAsync().ConfigureAwait(false);
        silo.Faults.Armed = Enum.Parse<SiloFaultPoint>(point);
        var crash = () => silo.Publisher.ResumeAsync(10, CancellationToken.None).AsTask();
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);

        await silo.RestartAsync().ConfigureAwait(false);
        var outcome = (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single();

        outcome.State.Should().Be(SiloStagedState.Visible);
        var publish = AssertSinglePublication(silo, draft);
        silo.Local.VersionsOf(RawKey(draft)).Should().ContainSingle("the replacement payload is written once and found again on retry")
            .Which.Should().Be(publish.Members[0].Locator.VersionId);
        silo.Local.Keys(Prefix(draft)).Should().BeEquivalentTo([superseded.Key, publish.Manifest.Locator.Key]);
        silo.Local.VersionsOf(publish.Manifest.Locator.Key).Should().ContainSingle("a retried manifest write reuses its key and bytes");
        silo.Local.VersionsOf(superseded.Key).Should().Equal([superseded.VersionId]);
        silo.Local.Read(superseded.Key, superseded.VersionId).Should().Equal(supersededBytes);
        (await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false)).Should().ContainSingle()
            .Which.Locator.Should().Be(superseded, "the orphan is recorded once, however often the replacement is retried");
        (await silo.State.ListStagedLocatorsAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(true, DisplayName = "superseded manifest write landed")]
    [DataRow(false, DisplayName = "superseded manifest write never landed")]
    public async Task AnUncertainSupersededManifestWriteIsResolvedThroughItsDigestKey(bool landed)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 4);
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        if (landed)
        {
            silo.Faults.Armed = SiloFaultPoint.AfterManifestPut;
            var crash = () => silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).AsTask();
            await crash.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        }
        else
        {
            silo.Local.FailBeforeLanding = static key => key.StartsWith("m/", StringComparison.Ordinal);
            (await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false)).BlockedCode
                .Should().Be("silo.store-unavailable");
            silo.Local.FailBeforeLanding = null;
        }
        var group = (await silo.State.ReadGroupAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        group.ManifestPutStarted.Should().BeTrue();
        group.ManifestVersionId.Should().BeNull("the write was uncertain");
        var oldKey = SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, group.ManifestSha256!);
        var landedVersion = silo.Local.Current(oldKey);
        (landedVersion is not null).Should().Be(landed);
        silo.Local.Delete(RawKey(draft), group.Members[0].VersionId!);

        await silo.RestartAsync().ConfigureAwait(false);
        (await silo.Publisher.ResumeAsync(10, CancellationToken.None).ConfigureAwait(false)).Single().State.Should().Be(SiloStagedState.Visible);

        var publish = AssertSinglePublication(silo, draft);
        publish.Manifest.Locator.Key.Should().NotBe(oldKey);
        var orphan = (await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false)).Should().ContainSingle().Which;
        orphan.Locator.Key.Should().Be(oldKey);
        orphan.Locator.VersionId.Should().Be(landed ? landedVersion : SiloPublicationState.UnknownVersion,
            "a landed write is resolved to its exact version; otherwise the never-committed key itself is handed over");
        orphan.Reason.StartsWith("uncertain-write", StringComparison.Ordinal).Should().Be(!landed);
    }

    [TestMethod]
    [DataRow(true, true, DisplayName = "publish segment landed, reconciled at startup")]
    [DataRow(false, true, DisplayName = "publish segment never landed, reconciled at startup")]
    [DataRow(true, false, DisplayName = "publish segment landed, reconciled by the caller's retry")]
    [DataRow(false, false, DisplayName = "publish segment never landed, reconciled by the caller's retry")]
    public async Task AnUncertainCommitIsReconciledWithItsExactBytesAndNeverReplacedByASecond(bool landed, bool startup)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 5);
        await silo.Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        static bool Segment(string key) => key.StartsWith(SiloObjectNamespace.JournalPrefix, StringComparison.Ordinal);
        if (landed)
        {
            silo.Local.FailAfterLanding = Segment;
        }
        else
        {
            silo.Local.FailBeforeLanding = Segment;
        }
        (await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false)).BlockedCode
            .Should().Be("silo.store-unavailable");
        (silo.Local.FailAfterLanding, silo.Local.FailBeforeLanding) = (null, null);
        var group = (await silo.State.ReadGroupAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        var manifest = silo.Local.Locator(
            SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, group.ManifestSha256!), group.ManifestVersionId!);
        var named = group.Members[0].VersionId!;
        silo.Local.Delete(RawKey(draft), named);

        // While that commit is uncertain, nothing may re-name a locator of the revision it commits.
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
            // The same writer retries the publication itself: P3 reconciles the pending commit before reading anything back.
            outcome = await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
        }

        var publish = silo.JournalRecords().OfType<SiloPublishRecord>().Should().ContainSingle(record => record.GroupId == draft.GroupId).Which;
        publish.Manifest.Locator.Should().Be(manifest, "the uncertain P3 commits with its exact bytes, naming its original manifest");
        publish.Members[0].Locator.VersionId.Should().Be(named, "no locator is substituted after P3");
        silo.Local.Keys(Prefix(draft)).Should().Equal([manifest.Key]);
        (await silo.State.ListOrphansAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
        outcome.State.Should().Be(SiloStagedState.Recorded);
        outcome.CatalogOutcome.Should().Be(SiloDisposition.Missing, "P3 recovery closes the revision for the lost member");
        silo.Payloads.Released.Should().BeEmpty("the spool is kept until the revision reaches P4");
    }

    [TestMethod]
    public async Task ACommitThatNamesItsManifestOutsideItsDigestKeyIsClosedAsAConflict()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 6);
        await CrashAsync(silo, draft, SiloFaultPoint.AfterManifestQualified).ConfigureAwait(false);
        var group = (await silo.State.ReadGroupAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        var bytes = silo.Local.Read(
            SiloObjectNamespace.ManifestKey(draft.FromUtc, draft.GroupId, draft.RevisionId, group.ManifestSha256!), group.ManifestVersionId!);
        var elsewhere = SiloObjectNamespace.ManifestRevisionPrefix(draft.FromUtc, draft.GroupId, draft.RevisionId) + "manifest.json";
        var planted = silo.Local.Locator(elsewhere, silo.Local.Plant(elsewhere, bytes));
        var qualification = new SiloQualification(silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256, true);
        var members = group.Members.Select(member => new SiloPublishedMember(member.Draft.ArtifactId, member.Draft.ArtifactRevision,
            member.Draft.Length, member.Draft.Sha256, member.Draft.Facts().Sha256(), member.Draft.Required,
            silo.Local.Locator(SiloObjectNamespace.PayloadKey(member.Draft.RetentionClass, member.Draft.Sha256), member.VersionId!),
            qualification)).ToArray();

        await silo.AppendAsync(new SiloPublishRecord(draft.GroupKind, draft.GroupId, draft.RevisionId,
            new(planted, bytes.Length, group.ManifestSha256!, qualification), members, Capture: draft.Capture)).ConfigureAwait(false);

        var publication = (await silo.Index.ReadPublicationAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
        publication.State.Should().Be("closed");
        publication.Outcome.Should().Be(SiloDisposition.Conflict, "the exact bytes are right, but not at the key they fix");
    }
}
