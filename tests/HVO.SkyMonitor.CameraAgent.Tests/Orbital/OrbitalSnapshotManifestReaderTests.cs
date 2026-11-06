using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// The manifest reader returns only a manifest whose stored bytes prove the requested identity, reads the small
/// manifest file and never the payload, holds a retention pin for the whole read, returns null for malformed, unknown
/// or retired identities, and reports damage and store unavailability as a coded exception that never names a path.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalSnapshotManifestReaderTests : OrbitalStoreTestBase
{
    private static readonly string?[] MalformedIdentities =
    [
        null,
        "",
        new string('a', 64),
        new string('A', 63),
        new string('A', 65),
        new string('G', 64),
        " " + new string('A', 63)
    ];

    [TestMethod]
    public async Task AMalformedIdentityIsNullBeforeAnyIoEvenWhenCancelled()
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        foreach (var identity in MalformedIdentities)
        {
            Assert.IsNull(await store.ReadManifestAsync(identity!, CancellationToken.None).ConfigureAwait(false));
            Assert.IsNull(await store.ReadManifestAsync(identity!, cancellation.Token).ConfigureAwait(false));
        }

        Assert.IsFalse(Directory.Exists(root), "No storage is touched for a malformed identity.");
        Assert.IsFalse(store.IsInitialized);
    }

    [TestMethod]
    public async Task APublishedManifestIsReturnedWithoutReadingThePayload()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var candidate = OrbitalElementsStoreTestData.Build("manifest", "25544", "43013");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        // The payload is gone, so success proves the read never needed it.
        File.Delete(Path.Combine(root, "payloads", candidate.Payload + ".json"));

        var manifest = await ((IOrbitalSnapshotManifestReader)store)
            .ReadManifestAsync(candidate.Manifest, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(manifest);
        CollectionAssert.AreEqual(OrbitalContract.SerializeCanonical(candidate.Snapshot.Manifest),
            OrbitalContract.SerializeCanonical(manifest));
        Assert.AreEqual(candidate.Manifest, manifest.ManifestSha256);
        Assert.AreEqual(2, manifest.RecordCount);
        Assert.AreEqual(OrbitalPopulationCompleteness.CompleteForRequestedPopulation, manifest.Coverage.Completeness);
        Assert.IsFalse(store.IsPinned(candidate.Manifest), "The pin is released after the read.");
    }

    [TestMethod]
    public async Task AnUnknownIdentityIsNullAndNothingIsSubstituted()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);

        Assert.IsNull(await store.ReadManifestAsync(OrbitalElementsStoreTestData.Sha('C'), CancellationToken.None).ConfigureAwait(false));

        await OrbitalElementsStoreTestData.PublishAsync(store, clock, OrbitalElementsStoreTestData.Build("only", "25544")).ConfigureAwait(false);
        Assert.IsNull(await store.ReadManifestAsync(OrbitalElementsStoreTestData.Sha('C'), CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(store.IsPinned(OrbitalElementsStoreTestData.Sha('C')));
    }

    [TestMethod]
    public async Task ARetiredIdentityIsNull()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock, OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1));
        var snapshots = await OrbitalElementsStoreRetentionTests.PublishSeriesAsync(store, clock, 3).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false)).Retired);

        Assert.IsNull(await store.ReadManifestAsync(snapshots[0].Manifest, CancellationToken.None).ConfigureAwait(false));
        Assert.IsNotNull(await store.ReadManifestAsync(snapshots[2].Manifest, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TheSnapshotIsPinnedForTheWholeReadSoRetentionCannotRetireIt()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var faults = new OrbitalElementsStoreTestData.Faults();
        using var store = OrbitalElementsStoreTestData.Open(root, clock, OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1), faults);
        var snapshots = await OrbitalElementsStoreRetentionTests.PublishSeriesAsync(store, clock, 3).ConfigureAwait(false);
        var manifestFile = Path.Combine(root, "manifests", snapshots[0].Manifest + ".json");
        OrbitalRetentionResult? during = null;
        var pinnedDuringRead = false;
        faults.ArmedPoint = OrbitalStorageFaultPoint.BeforeManifestRead;
        faults.OnPoint = () =>
        {
            pinnedDuringRead = store.IsPinned(snapshots[0].Manifest);
            during = store.ApplyRetentionAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        };

        var manifest = await store.ReadManifestAsync(snapshots[0].Manifest, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(pinnedDuringRead);
        Assert.IsNotNull(during);
        Assert.AreEqual(0, during.Retired, "A snapshot pinned by a manifest read is protected, not retired.");
        Assert.IsGreaterThanOrEqualTo(1, during.Protected);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(snapshots[0].Manifest, manifest.ManifestSha256);
        Assert.IsFalse(store.IsPinned(snapshots[0].Manifest));

        Assert.AreEqual(1, (await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false)).Retired,
            "Once the read releases its pin, ordinary retention retires the snapshot.");
        Assert.IsFalse(File.Exists(manifestFile));
        Assert.IsNull(await store.ReadManifestAsync(snapshots[0].Manifest, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("truncated")]
    [DataRow("extended")]
    [DataRow("altered")]
    [DataRow("directory")]
    [DataRow("symlink")]
    public async Task AManifestThatDoesNotProveItsIdentityIsAnIntegrityFailureWithoutAPath(string damage)
    {
        ArgumentNullException.ThrowIfNull(damage);
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var candidate = OrbitalElementsStoreTestData.Build("damaged", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        var file = Path.Combine(root, "manifests", candidate.Manifest + ".json");
        var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        File.Delete(file);
        switch (damage)
        {
            case "missing":
                break;
            case "truncated":
                await File.WriteAllBytesAsync(file, bytes[..^1]).ConfigureAwait(false);
                break;
            case "extended":
                await File.WriteAllBytesAsync(file, [.. bytes, (byte)'\n']).ConfigureAwait(false);
                break;
            case "altered":
                bytes[^2] ^= 0x01;
                await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
                break;
            case "directory":
                Directory.CreateDirectory(file);
                break;
            case "symlink":
                var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside.json");
                await File.WriteAllBytesAsync(outside, bytes).ConfigureAwait(false);
                File.CreateSymbolicLink(file, outside);
                break;
            default:
                Assert.Fail("Unknown damage " + damage);
                break;
        }

        var failure = await Assert.ThrowsExactlyAsync<OrbitalStorageException>(
            () => store.ReadManifestAsync(candidate.Manifest, CancellationToken.None).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsAvailability.IntegrityFailureReasonCode, failure.ReasonCode);
        Assert.DoesNotContain(root, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(candidate.Manifest, failure.Message, StringComparison.Ordinal);
        Assert.IsFalse(store.IsPinned(candidate.Manifest));
    }

    [TestMethod]
    public async Task ARootOwnedByAnotherStoreIsACodedStorageFailure()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var owner = OrbitalElementsStoreTestData.Open(root, clock);
        var candidate = OrbitalElementsStoreTestData.Build("owned", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(owner, clock, candidate).ConfigureAwait(false);
        using var intruder = OrbitalElementsStoreTestData.Open(root, clock);

        var failure = await Assert.ThrowsExactlyAsync<OrbitalStorageException>(
            () => intruder.ReadManifestAsync(candidate.Manifest, CancellationToken.None).AsTask()).ConfigureAwait(false);

        Assert.AreEqual("orbital.storage-locked", failure.ReasonCode);
        Assert.IsNotNull(await owner.ReadManifestAsync(candidate.Manifest, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ADisposedStoreIsUnavailable()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var store = OrbitalElementsStoreTestData.Open(root, clock);
        var candidate = OrbitalElementsStoreTestData.Build("disposed", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        store.Dispose();

        var failure = await Assert.ThrowsExactlyAsync<OrbitalStorageException>(
            () => store.ReadManifestAsync(candidate.Manifest, CancellationToken.None).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsAvailability.StoreUnavailableReasonCode, failure.ReasonCode);
    }

    [TestMethod]
    public async Task AnInjectedIoFaultKeepsItsStableStorageCode()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var faults = new OrbitalElementsStoreTestData.Faults();
        using var store = OrbitalElementsStoreTestData.Open(root, clock, faults: faults);
        var candidate = OrbitalElementsStoreTestData.Build("io", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        faults.ArmedPoint = OrbitalStorageFaultPoint.BeforeManifestRead;
        faults.Throw = () => new IOException("synthetic device failure at " + root);

        var failure = await Assert.ThrowsExactlyAsync<OrbitalStorageException>(
            () => store.ReadManifestAsync(candidate.Manifest, CancellationToken.None).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(OrbitalStorageException.DefaultReasonCode, failure.ReasonCode);
        Assert.DoesNotContain(root, failure.Message, StringComparison.Ordinal);
        Assert.IsFalse(store.IsPinned(candidate.Manifest));
    }

    [TestMethod]
    public async Task CancellationOfTheCallersTokenPropagates()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var faults = new OrbitalElementsStoreTestData.Faults();
        using var store = OrbitalElementsStoreTestData.Open(root, clock, faults: faults);
        var candidate = OrbitalElementsStoreTestData.Build("cancelled", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadManifestAsync(candidate.Manifest, cancelled.Token).AsTask()).ConfigureAwait(false);

        using var midRead = new CancellationTokenSource();
        faults.ArmedPoint = OrbitalStorageFaultPoint.BeforeManifestRead;
        faults.OnPoint = midRead.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadManifestAsync(candidate.Manifest, midRead.Token).AsTask()).ConfigureAwait(false);
        Assert.IsFalse(store.IsPinned(candidate.Manifest));
    }

    [TestMethod]
    public async Task AHaltedRefreshKeepsTheActiveManifestReadable()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var candidate = OrbitalElementsStoreTestData.Build("before-halt", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        await store.RecordHaltAsync(attemptId, 403, "synthetic provider refusal", CancellationToken.None).ConfigureAwait(false);

        var manifest = await store.ReadManifestAsync(candidate.Manifest, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(manifest);
        Assert.AreEqual(candidate.Manifest, manifest.ManifestSha256);
    }
}
