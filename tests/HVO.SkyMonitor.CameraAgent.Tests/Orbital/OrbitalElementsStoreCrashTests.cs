using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// Crash, disk-full, corruption and cancellation at every publication step. The last-known-good snapshot stays
/// active, accepted bytes are never rewritten, and every file named by the failed attempt's intent but bound to no
/// published snapshot is removed, immediately when the failure is recorded or at the next start after a crash.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsStoreCrashTests : OrbitalStoreTestBase
{
    private static readonly OrbitalElementsStoreTestData.Candidate Good = OrbitalElementsStoreTestData.Build("good", "25544");
    private static readonly string[] AmbiguousStaging = ["ambiguous-directory"];
    private static readonly OrbitalElementsStoreTestData.Candidate Next = OrbitalElementsStoreTestData.Build("next", "25544", "100001");

    [TestMethod]
    [DataRow(nameof(OrbitalStorageFaultPoint.AfterRaw))]
    [DataRow(nameof(OrbitalStorageFaultPoint.AfterPayload))]
    [DataRow(nameof(OrbitalStorageFaultPoint.AfterManifest))]
    [DataRow(nameof(OrbitalStorageFaultPoint.BeforeActivationCommit))]
    public async Task ACrashAtAnyPublicationStepKeepsTheLastKnownGoodAndRestartRemovesTheOrphans(string point)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var faults = new OrbitalElementsStoreTestData.Faults();
        using (var store = OrbitalElementsStoreTestData.Open(root, clock, faults: faults))
        {
            await OrbitalElementsStoreTestData.PublishAsync(store, clock, Good).ConfigureAwait(false);
            faults.ArmedPoint = Enum.Parse<OrbitalStorageFaultPoint>(point);
            faults.Throw = static () => new InvalidOperationException("simulated process crash");
            var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
            // As the refresh job does, the valid 200 is recorded before publication begins.
            await store.RecordRetrievalAsync(attemptId, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.PublishAndActivateAsync(
                attemptId, Next.Snapshot, Next.Raw, CancellationToken.None).AsTask()).ConfigureAwait(false);

            // A crash records nothing: the attempt is still in flight and its intent is still staged.
            Assert.AreEqual(OrbitalAttemptOutcome.InFlight,
                (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).LastAttemptOutcome);
            CollectionAssert.AreEqual(new[] { attemptId + ".intent" }, OrbitalElementsStoreTestData.Files(root, "staging"));
        }

        using var reopened = OrbitalElementsStoreTestData.Open(root, clock);
        var state = await reopened.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(Good.Manifest, state.CurrentManifestSha256);
        Assert.AreEqual(OrbitalAttemptOutcome.Interrupted, state.LastAttemptOutcome);
        Assert.AreEqual("orbital.attempt-interrupted", state.LastAttemptReasonCode,
            "The provider outcome was recorded before the crash, so it is a plain interruption.");
        Assert.IsNull(state.Halt);
        AssertOnlyGoodFiles(root);
        Assert.IsEmpty(OrbitalElementsStoreTestData.Files(root, "staging"));

        clock.Advance(OrbitalElementsStoreTestData.Interval);
        var retried = await OrbitalElementsStoreTestData.PublishAsync(reopened, clock, Next).ConfigureAwait(false);
        Assert.AreEqual(Next.Manifest, retried.ManifestSha256);
        Assert.IsFalse(retried.PayloadUnchanged);
    }

    [TestMethod]
    [DataRow(nameof(OrbitalStorageFaultPoint.AfterRaw))]
    [DataRow(nameof(OrbitalStorageFaultPoint.AfterPayload))]
    [DataRow(nameof(OrbitalStorageFaultPoint.AfterManifest))]
    [DataRow(nameof(OrbitalStorageFaultPoint.BeforeActivationCommit))]
    public async Task DiskFullAtAnyPublicationStepIsRecordedAndCleanedUpImmediately(string point)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var faults = new OrbitalElementsStoreTestData.Faults();
        using var store = OrbitalElementsStoreTestData.Open(root, clock, faults: faults);
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, Good).ConfigureAwait(false);
        faults.ArmedPoint = Enum.Parse<OrbitalStorageFaultPoint>(point);
        faults.Throw = static () => new IOException("No space left on device", 28);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);

        var failure = await Assert.ThrowsExactlyAsync<OrbitalStorageException>(() => store.PublishAndActivateAsync(
            attemptId, Next.Snapshot, Next.Raw, CancellationToken.None).AsTask()).ConfigureAwait(false);

        Assert.AreEqual("orbital.storage-no-space", failure.ReasonCode);
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(Good.Manifest, state.CurrentManifestSha256);
        Assert.AreEqual(OrbitalAttemptOutcome.StorageFailed, state.LastAttemptOutcome);
        Assert.AreEqual("orbital.storage-no-space", state.LastAttemptReasonCode);
        AssertOnlyGoodFiles(root);
        Assert.IsEmpty(OrbitalElementsStoreTestData.Files(root, "staging"));
        Assert.AreEqual(1L, OrbitalElementsStoreTestData.Scalar(root, "SELECT COUNT(*) FROM orbital_snapshots;"));

        var blocked = await store.TryBeginAttemptAsync("retry", OrbitalAttemptTrigger.Manual, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.FloorNotElapsed, blocked.Status, "A storage failure still consumed the attempt.");
        clock.Advance(OrbitalElementsStoreTestData.Interval);
        var retried = await OrbitalElementsStoreTestData.PublishAsync(store, clock, Next).ConfigureAwait(false);
        Assert.AreEqual(Next.Manifest, retried.ManifestSha256);
    }

    [TestMethod]
    public async Task ACorruptedAcceptedFileIsAnIntegrityFailureAndIsNeverRewritten()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, Good).ConfigureAwait(false);
        var payloadPath = Path.Combine(root, "payloads", Good.Payload + ".json");
        var corrupted = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);
        corrupted[^2] ^= 0x01;
        await File.WriteAllBytesAsync(payloadPath, corrupted).ConfigureAwait(false);
        var samePayload = OrbitalElementsStoreTestData.Build("same-payload", "25544");
        Assert.AreEqual(Good.Payload, samePayload.Payload);

        var failure = await Assert.ThrowsExactlyAsync<OrbitalStorageException>(async () => await store.PublishAndActivateAsync(
            await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false), samePayload.Snapshot, samePayload.Raw,
            CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual("orbital.storage-integrity", failure.ReasonCode);
        CollectionAssert.AreEqual(corrupted, await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false));
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(Good.Manifest, state.CurrentManifestSha256);
        Assert.AreEqual((OrbitalAttemptOutcome.StorageFailed, "orbital.storage-integrity"),
            (state.LastAttemptOutcome!.Value, state.LastAttemptReasonCode));
        AssertOnlyGoodFiles(root);
    }

    [TestMethod]
    [DataRow(nameof(OrbitalStorageFaultPoint.AfterRaw))]
    [DataRow(nameof(OrbitalStorageFaultPoint.BeforeActivationCommit))]
    public async Task CancellationDuringPublicationInterruptsTheAttemptAndRemovesItsOrphans(string point)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var faults = new OrbitalElementsStoreTestData.Faults();
        using var store = OrbitalElementsStoreTestData.Open(root, clock, faults: faults);
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, Good).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        faults.ArmedPoint = Enum.Parse<OrbitalStorageFaultPoint>(point);
        faults.OnPoint = cancellation.Cancel;
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.PublishAndActivateAsync(
            attemptId, Next.Snapshot, Next.Raw, cancellation.Token).AsTask()).ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(Good.Manifest, state.CurrentManifestSha256);
        Assert.AreEqual(OrbitalAttemptOutcome.Interrupted, state.LastAttemptOutcome);
        Assert.AreEqual("orbital.attempt-cancelled", state.LastAttemptReasonCode);
        AssertOnlyGoodFiles(root);
        Assert.IsEmpty(OrbitalElementsStoreTestData.Files(root, "staging"));
    }

    [TestMethod]
    public async Task AStagedIntentFromAnotherCrashIsSettledWithoutTouchingPublishedFiles()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            await OrbitalElementsStoreTestData.PublishAsync(store, clock, Good).ConfigureAwait(false);
        }
        var orphanRaw = Path.Combine(root, "raw", OrbitalElementsStoreTestData.Sha('C') + ".json");
        await File.WriteAllTextAsync(orphanRaw, "orphan").ConfigureAwait(false);
        var foreign = Path.Combine(root, "raw", OrbitalElementsStoreTestData.Sha('D') + ".json");
        await File.WriteAllTextAsync(foreign, "not named by any intent").ConfigureAwait(false);
        var staging = Path.Combine(root, "staging");
        // Names the published payload and manifest too: they are bound to a published snapshot, so they stay.
        await File.WriteAllTextAsync(Path.Combine(staging, new string('e', 32) + ".intent"),
            $"hvo-orbital-intent-v1 {new string('e', 32)}\nraw {OrbitalElementsStoreTestData.Sha('C')}\npayload {Good.Payload}\nmanifest {Good.Manifest}\n")
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(staging, new string('f', 32) + ".intent"), "malformed").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(staging, "stray.tmp"), "stray").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(root, "payloads", "x.json." + Guid.NewGuid().ToString("N") + ".tmp"), "temporary")
            .ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(staging, "ambiguous-directory"));

        using var reopened = OrbitalElementsStoreTestData.Open(root, clock);
        await reopened.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(File.Exists(orphanRaw), "A verified orphan is removed.");
        Assert.IsTrue(File.Exists(foreign), "A file no intent names is ambiguous and stays.");
        CollectionAssert.AreEqual(AmbiguousStaging, OrbitalElementsStoreTestData.Files(root, "staging"));
        CollectionAssert.AreEqual(new[] { Good.Payload + ".json" }, OrbitalElementsStoreTestData.Files(root, "payloads"));
        CollectionAssert.AreEqual(new[] { Good.Manifest + ".json" }, OrbitalElementsStoreTestData.Files(root, "manifests"));
        Assert.AreEqual(Good.Manifest, (await reopened.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).CurrentManifestSha256);
    }

    private static void AssertOnlyGoodFiles(string root)
    {
        CollectionAssert.AreEqual(new[] { Good.RawSha256 + ".json" }, OrbitalElementsStoreTestData.Files(root, "raw"));
        CollectionAssert.AreEqual(new[] { Good.Payload + ".json" }, OrbitalElementsStoreTestData.Files(root, "payloads"));
        CollectionAssert.AreEqual(new[] { Good.Manifest + ".json" }, OrbitalElementsStoreTestData.Files(root, "manifests"));
    }
}
