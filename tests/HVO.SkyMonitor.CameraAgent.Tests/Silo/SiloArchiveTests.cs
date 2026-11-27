using Amazon.S3;
using Amazon.S3.Model;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
public sealed class SiloArchiveTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("Endpoint", "http://127.0.0.1")]
    [DataRow("Endpoint", "https://127.0.0.1/path")]
    [DataRow("AccessKey", "reader/key")]
    [DataRow("SecretKey", "secret with spaces")]
    [DataRow("Bucket", "Archive_Bucket")]
    [DataRow("Bucket", "two..dots")]
    [DataRow("AuthorityPem", "invalid certificate")]
    public async Task StoreRefusesInvalidArchiveConfiguration(string field, string value)
    {
        var token = TestContext.CancellationToken;
        var fixture = await SiloArchiveFixture.StartAsync(new string('a', 64), token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        fixture.Settings[field] = value;
        Assert.ThrowsExactly<ArgumentException>(() => SiloArchiveStore.Create(fixture.Settings, TimeProvider.System).Dispose());
    }

    [TestMethod]
    [DataRow("untrusted")]
    [DataRow("expired")]
    [DataRow("wrong-host")]
    [DataRow("no-eku")]
    public async Task StoreClassifiesTlsFailuresInProfileExactProbeJournalAndRetention(string profile)
    {
        var token = TestContext.CancellationToken;
        var fixture = await SiloArchiveFixture.StartAsync(new string('a', 64), token, profile).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        using var store = SiloArchiveStore.Create(fixture.Settings, TimeProvider.System);
        var locator = new SiloExactLocator(store.Marker.StoreId, store.Marker.Bucket, "p/payload", "version-1");
        var identity = new SiloObjectIdentity(SiloIdentityKind.Member, "artifact", "1", new string('b', 64), 1);
        var commit = new SiloCommittedSegment(new(1, 1), new string('c', 64), "journal-version");
        var verify = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() => store.VerifyAsync(token).AsTask()).ConfigureAwait(false);
        var retain = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() =>
            store.ReadRetentionAsync(locator, token).AsTask()).ConfigureAwait(false);
        var probe = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() =>
            store.ProbeAsync(identity, locator, captureBytes: false, token).AsTask()).ConfigureAwait(false);
        var journal = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() => store.ReadSegmentAsync(commit, token).AsTask()).ConfigureAwait(false);
        foreach (var failure in new[] { verify, retain, probe, journal })
        {
            Assert.AreEqual("failed", failure.State);
            Assert.AreEqual("archive-tls-failed", failure.Reason);
            Assert.AreEqual(failure.Reason, failure.Message);
            Assert.IsNull(failure.InnerException);
        }
    }

    [TestMethod]
    public async Task RetentionRequiresADateWhenModeIsSetAndTreatsUnspecifiedDatesAsUtc()
    {
        var token = TestContext.CancellationToken;
        var marker = new SiloArchiveMarker(SiloArchiveStore.MarkerFormat, SiloArchiveFixture.StoreId, SiloArchiveFixture.Bucket,
            new string('b', 64), SiloArchiveStore.RetentionPolicy);
        var locator = new SiloExactLocator(marker.StoreId, marker.Bucket, "p/payload", "version-1");
        var unspecified = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        var client = new Mock<IAmazonS3>(MockBehavior.Strict);
        client.Setup(s3 => s3.Dispose());
        client.SetupSequence(s3 => s3.GetObjectMetadataAsync(It.Is<GetObjectMetadataRequest>(request =>
            request.BucketName == locator.Bucket && request.Key == locator.Key && request.VersionId == locator.VersionId),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { VersionId = locator.VersionId, ObjectLockMode = ObjectLockMode.Governance })
            .ReturnsAsync(new GetObjectMetadataResponse
            {
                VersionId = locator.VersionId,
                ObjectLockMode = ObjectLockMode.Governance,
                ObjectLockRetainUntilDate = unspecified,
                ObjectLockLegalHoldStatus = ObjectLockLegalHoldStatus.On
            });
        using var store = new SiloArchiveStore(client.Object, marker, TimeProvider.System, Mock.Of<IDisposable>());
        var refused = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() =>
            store.ReadRetentionAsync(locator, token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual("failed", refused.State);
        Assert.AreEqual("archive-retention-invalid", refused.Reason);
        var retained = await store.ReadRetentionAsync(locator, token).ConfigureAwait(false);
        Assert.IsTrue(retained.LegalHold);
        Assert.AreEqual("GOVERNANCE", retained.Mode);
        Assert.AreEqual(new DateTimeOffset(unspecified, TimeSpan.Zero), retained.RetainUntilUtc);
    }

    [TestMethod]
    public async Task ExpiryAcceptsMaintenanceClockSkewButRefusesATamperedVersion()
    {
        var token = TestContext.CancellationToken;
        var fixture = await SiloArchiveFixture.StartAsync(new string('a', 64), token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        var clock = new SiloTestClock(SiloHarness.Start);
        using var store = SiloArchiveStore.Create(fixture.Settings, clock);
        var locator = new SiloExactLocator(store.Marker.StoreId, store.Marker.Bucket, "p/full/payload", "version-1");
        Assert.IsFalse(await store.WasExpiredAsync(locator, token).ConfigureAwait(false));
        fixture.WriteExpiry(locator, clock.GetUtcNow().AddMilliseconds(1));
        Assert.IsTrue(await store.WasExpiredAsync(locator, token).ConfigureAwait(false));
        fixture.WriteExpiry(locator, clock.GetUtcNow(), tampered: true);
        var refused = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() =>
            store.WasExpiredAsync(locator, token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual("failed", refused.State);
        Assert.AreEqual("archive-expiry-disposition-invalid", refused.Reason);
    }

    [TestMethod]
    public async Task StoreVerificationDistinguishesMismatchUnreachableAndCancellation()
    {
        var token = TestContext.CancellationToken;
        var fixture = await SiloArchiveFixture.StartAsync(new string('a', 64), token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        using var store = SiloArchiveStore.Create(fixture.Settings, TimeProvider.System);
        await store.VerifyAsync(token).ConfigureAwait(false);

        fixture.Put(SiloArchiveStore.MarkerKey, SiloArchiveFixture.MarkerVersion,
            SiloJson.Serialize(fixture.Marker with { SourceBindingId = new string('b', 64) }));
        var mismatch = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() => store.VerifyAsync(token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual("archive-binding-mismatch", mismatch.Reason);
        Assert.AreEqual("blocked", mismatch.State);
        fixture.Put(SiloArchiveStore.MarkerKey, SiloArchiveFixture.MarkerVersion, "{"u8.ToArray());
        var badJson = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() => store.VerifyAsync(token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual("archive-marker-invalid", badJson.Reason);
        Assert.AreEqual("failed", badJson.State);
        fixture.UnavailableKey = SiloArchiveStore.MarkerKey;
        var outage = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() => store.VerifyAsync(token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual("archive-unreachable", outage.Reason);
        Assert.AreEqual("blocked", outage.State);

        fixture.StallKey = SiloArchiveStore.MarkerKey;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var verifying = store.VerifyAsync(cancel.Token).AsTask();
        await fixture.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        await cancel.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => verifying).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("manifest", "archive-version-digest-mismatch")]
    [DataRow("payload", "archive-version-digest-mismatch")]
    [DataRow("receipt", "archive-journal-digest-mismatch")]
    public async Task ReceiptRechecksManifestPayloadAndJournalBytes(string corrupt, string reason)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(silo.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1);
        var (reference, source, published) = await PublishAndReplicateAsync(silo, fixture, draft, token).ConfigureAwait(false);
        using var archive = SiloArchiveStore.Create(fixture.Settings, silo.Clock);
        var verification = new SiloArchiveVerification(archive, silo.Clock);
        var records = await verification.VerifyPublicationAsync(reference, source, silo.Index, prefix: 0, token).ConfigureAwait(false);
        var receipt = await silo.AppendAsync([.. records.Records]).ConfigureAwait(false);
        fixture.Replicate(silo.Local, receipt);
        var copies = await verification.ReadReceiptAsync(receipt, reference, verifyCopies: true, token).ConfigureAwait(false);
        Assert.HasCount(published.Members.Count + 1, copies.Copies);

        var locator = corrupt switch
        {
            "manifest" => published.Manifest.Locator,
            "receipt" => archive.SegmentLocator(receipt),
            _ => published.Members[0].Locator
        };
        var original = silo.Local.Read(locator.Key, locator.VersionId);
        original[0] ^= 0x80;
        fixture.Put(locator.Key, locator.VersionId, original);
        var invalid = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() =>
            verification.ReadReceiptAsync(receipt, reference, verifyCopies: true, token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual("failed", invalid.State);
        Assert.AreEqual(reason, invalid.Reason);
    }

    [TestMethod]
    public async Task MissingOptionalPublicationMemberDoesNotPreventAValidReceipt()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(silo.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        var (reference, source, publish) = await PublishAndReplicateAsync(silo, fixture, SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false),
            token, requiredOnly: true).ConfigureAwait(false);
        using var archive = SiloArchiveStore.Create(fixture.Settings, silo.Clock);
        var verification = new SiloArchiveVerification(archive, silo.Clock);

        var records = await verification.VerifyPublicationAsync(reference, source, silo.Index, prefix: 0, token).ConfigureAwait(false);
        var receipt = await silo.AppendAsync([.. records.Records]).ConfigureAwait(false);
        fixture.Replicate(silo.Local, receipt);
        var evidence = await verification.ReadReceiptAsync(receipt, reference, verifyCopies: true, token).ConfigureAwait(false);

        Assert.HasCount(2, evidence.Copies);
        Assert.IsTrue(evidence.Copies.Any(copy => copy.Identity.Id == publish.Members.Single(member => member.Required).ArtifactId));
        Assert.IsFalse(evidence.Copies.Any(copy => copy.Identity.Id == publish.Members.Single(member => !member.Required).ArtifactId));
    }

    [TestMethod]
    public async Task UnreachableComputeInputIsRecordedSeparatelyFromVerifiedReopen()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(silo.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        var (_, _, input) = await PublishAndReplicateAsync(silo, fixture, SiloDrafts.Capture(silo.Payloads, 1), token).ConfigureAwait(false);
        var member = input.Members[0];
        var draft = SiloDrafts.Capture(silo.Payloads, 2) with
        {
            Dependencies = [new(member.ArtifactId, member.ArtifactRevision, member.Sha256, SiloDependencyKind.Compute, Required: true)]
        };
        var (reference, source, _) = await PublishAndReplicateAsync(silo, fixture, draft, token).ConfigureAwait(false);
        using var archive = SiloArchiveStore.Create(fixture.Settings, silo.Clock);
        var verification = new SiloArchiveVerification(archive, silo.Clock);
        var initial = await verification.VerifyPublicationAsync(reference, source, silo.Index, prefix: 0, token).ConfigureAwait(false);
        var firstReceipt = await silo.AppendAsync([.. initial.Records]).ConfigureAwait(false);
        fixture.Replicate(silo.Local, firstReceipt);
        fixture.UnavailableKey = member.Locator.Key;

        var previous = await verification.ReadReceiptAsync(firstReceipt, reference, verifyCopies: true, token).ConfigureAwait(false);
        var role = new SiloRoleKey(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision);
        Assert.IsTrue(previous.Copies.Any(copy => copy.Identity.Role == role));
        var records = await verification.VerifyPublicationAsync(reference, source, silo.Index, prefix: 0, token).ConfigureAwait(false);
        var receipt = await silo.AppendAsync([.. records.Records]).ConfigureAwait(false);
        fixture.Replicate(silo.Local, receipt);
        var evidence = await verification.ReadReceiptAsync(receipt, reference, verifyCopies: true, token).ConfigureAwait(false);

        Assert.HasCount(draft.Members.Count + 1, evidence.Copies);
        Assert.AreEqual("blocked", evidence.Unverified[role]);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public async Task DeliberateDependencyExpiryPreservesSurvivingCopies(bool required, bool compute)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(silo.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        var (_, _, parent) = await PublishAndReplicateAsync(silo, fixture, SiloDrafts.Capture(silo.Payloads, 1), token).ConfigureAwait(false);
        var dependency = parent.Members[0];
        var draft = SiloDrafts.Capture(silo.Payloads, 2) with
        {
            Dependencies = [new(dependency.ArtifactId, dependency.ArtifactRevision, dependency.Sha256, compute ? SiloDependencyKind.Compute : SiloDependencyKind.Reopen, required)]
        };
        var (reference, source, _) = await PublishAndReplicateAsync(silo, fixture, draft, token).ConfigureAwait(false);
        using var archive = SiloArchiveStore.Create(fixture.Settings, silo.Clock);
        var verification = new SiloArchiveVerification(archive, silo.Clock);
        var records = await verification.VerifyPublicationAsync(reference, source, silo.Index, prefix: 0, token).ConfigureAwait(false);
        Assert.IsTrue(records.Records.OfType<SiloCopyRecord>().Any(copy => copy.Identity.Id == dependency.ArtifactId));
        var receipt = await silo.AppendAsync([.. records.Records]).ConfigureAwait(false);
        fixture.Replicate(silo.Local, receipt);
        fixture.WriteExpiry(dependency.Locator, silo.Clock.GetUtcNow());

        if (required && !compute)
        {
            var refused = await Assert.ThrowsExactlyAsync<SiloArchiveException>(() =>
                verification.ReadReceiptAsync(receipt, reference, verifyCopies: true, token).AsTask()).ConfigureAwait(false);
            Assert.AreEqual("archive-copy-policy-expired", refused.Reason);
            var current = await verification.VerifyPublicationAsync(reference, source, silo.Index, prefix: 0, token).ConfigureAwait(false);
            var receiptRecord = (SiloArchiveReceiptRecord)current.Records[0];
            Assert.AreEqual("policy-expired", receiptRecord.UnverifiedMembers?.Single().State);
            var partial = await silo.AppendAsync([.. current.Records]).ConfigureAwait(false);
            fixture.Replicate(silo.Local, partial);
            var evidence = await verification.ReadReceiptAsync(partial, reference, verifyCopies: true, token).ConfigureAwait(false);
            Assert.HasCount(draft.Members.Count + 1, evidence.Copies);
            Assert.AreEqual("policy-expired", evidence.Unverified.Single().Value);
        }
        else
        {
            var historic = await verification.ReadReceiptAsync(receipt, reference, verifyCopies: true, token).ConfigureAwait(false);
            Assert.IsTrue(historic.Copies.Any(copy => copy.Identity.Id == dependency.ArtifactId));
            var current = await verification.VerifyPublicationAsync(reference, source, silo.Index, prefix: 0, token).ConfigureAwait(false);
            Assert.IsFalse(current.Records.OfType<SiloCopyRecord>().Any(copy => copy.Identity.Id == dependency.ArtifactId));
            var laterReceipt = await silo.AppendAsync([.. current.Records]).ConfigureAwait(false);
            fixture.Replicate(silo.Local, laterReceipt);
            Assert.HasCount(draft.Members.Count + 1, (await verification.ReadReceiptAsync(laterReceipt, reference, verifyCopies: true, token)
                .ConfigureAwait(false)).Copies);
        }
    }

    internal static async Task<(SiloPublicationRef Reference, SiloCommittedSegment Commit, SiloPublishRecord Record)>
        PublishAndReplicateAsync(SiloHarness silo, SiloArchiveFixture fixture, SiloGroupDraft draft, CancellationToken token, bool requiredOnly = false)
    {
        var published = await silo.PublishAsync(draft).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, published.State);
        var position = silo.PublishPosition(draft.GroupId, draft.RevisionId);
        var commit = await silo.State.ReadCommittedSegmentAsync(position, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Fixture publication did not commit.");
        var publish = silo.PublishRecord(draft.GroupId, draft.RevisionId);
        fixture.Replicate(silo.Local, commit);
        fixture.Replicate(silo.Local, publish.Manifest.Locator);
        foreach (var member in publish.Members.Where(member => member.Required || !requiredOnly))
            fixture.Replicate(silo.Local, member.Locator);
        return (new(publish.GroupId, publish.RevisionId, position, 0), commit, publish);
    }
}
