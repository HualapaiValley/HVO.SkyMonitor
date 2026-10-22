using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// The reader returns only snapshots whose stored bytes prove their committed identity, never substitutes the
/// current snapshot for an exact request, serves cache hits without I/O until retention moves, and reports every
/// failure as a typed unavailability rather than an exception, except cancellation of the caller's own token.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalSnapshotReaderTests : OrbitalStoreTestBase
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

    private static readonly string[] FirstCatalog = ["25544"];

    private static readonly string[] SecondCatalog = ["25544", "43013"];

    [TestMethod]
    public void TheConstructorRejectsMissingDependencies()
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));

        Assert.ThrowsExactly<ArgumentNullException>(() => new OrbitalSnapshotReader(null!, true, NullLogger<OrbitalSnapshotReader>.Instance));
        Assert.ThrowsExactly<ArgumentNullException>(() => new OrbitalSnapshotReader(store, true, null!));
    }

    [TestMethod]
    public async Task AMalformedIdentityIsRefusedBeforeAnyIoEvenWhenCancelledOrUnconfigured()
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        var configured = Reader(store);
        var unconfigured = Reader(store, configured: false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        foreach (var identity in MalformedIdentities)
        {
            AssertUnavailable(OrbitalSnapshotUnavailableReason.InvalidIdentity,
                await configured.ReadExactAsync(identity!, CancellationToken.None).ConfigureAwait(false));
            AssertUnavailable(OrbitalSnapshotUnavailableReason.InvalidIdentity,
                await configured.ReadExactAsync(identity!, cancellation.Token).ConfigureAwait(false));
            AssertUnavailable(OrbitalSnapshotUnavailableReason.InvalidIdentity,
                await unconfigured.ReadExactAsync(identity!, CancellationToken.None).ConfigureAwait(false));
        }

        Assert.IsFalse(Directory.Exists(root), "No storage is touched for a malformed identity.");
        Assert.IsFalse(store.IsInitialized);
    }

    [TestMethod]
    public async Task AnUnconfiguredReaderReportsNotConfiguredWithoutTouchingStorage()
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        var reader = Reader(store, configured: false);

        AssertUnavailable(OrbitalSnapshotUnavailableReason.NotConfigured,
            await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false));
        AssertUnavailable(OrbitalSnapshotUnavailableReason.NotConfigured,
            await reader.ReadExactAsync(OrbitalElementsStoreTestData.Sha('C'), CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task AnEmptyStoreHasNoActiveSnapshotAndAnUnknownIdentityIsNotSubstituted()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var reader = Reader(store);

        AssertUnavailable(OrbitalSnapshotUnavailableReason.NoActiveSnapshot,
            await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false));
        AssertUnavailable(OrbitalSnapshotUnavailableReason.UnknownIdentity,
            await reader.ReadExactAsync(OrbitalElementsStoreTestData.Sha('C'), CancellationToken.None).ConfigureAwait(false));

        await OrbitalElementsStoreTestData.PublishAsync(store, clock, OrbitalElementsStoreTestData.Build("only", "25544")).ConfigureAwait(false);
        AssertUnavailable(OrbitalSnapshotUnavailableReason.UnknownIdentity,
            await reader.ReadExactAsync(OrbitalElementsStoreTestData.Sha('C'), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentFollowsActivationAndExactReturnsOnlyTheRequestedIdentity()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var reader = Reader(store);
        var first = OrbitalElementsStoreTestData.Build("first", "25544");
        var second = OrbitalElementsStoreTestData.Build("second", "25544", "43013");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, first).ConfigureAwait(false);

        AssertAvailable(first, await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false));

        await OrbitalElementsStoreTestData.PublishAsync(store, clock, second).ConfigureAwait(false);
        var current = AssertAvailable(second, await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false));
        var exactFirst = AssertAvailable(first,
            await reader.ReadExactAsync(first.Manifest, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(2, current.Manifest.RecordCount);
        Assert.AreEqual(1, exactFirst.Manifest.RecordCount);
        CollectionAssert.AreEqual(FirstCatalog, exactFirst.ObjectIdentities.Select(identity => identity.CatalogNumber).ToArray());
        CollectionAssert.AreEqual(SecondCatalog, current.ObjectIdentities.Select(identity => identity.CatalogNumber).ToArray());
    }

    [TestMethod]
    public async Task ACacheHitPerformsNoIoAndConcurrentMissesLoadOnce()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var reader = Reader(store);
        var candidate = OrbitalElementsStoreTestData.Build("cached", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);

        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(
            _ => reader.ReadExactAsync(candidate.Manifest, CancellationToken.None).AsTask())).ConfigureAwait(false);

        Assert.AreEqual(1, reader.LoadCount, "Concurrent cache misses share one materialization.");
        var loaded = AssertAvailable(candidate, reads[0]);
        Assert.IsTrue(reads.All(read => ReferenceEquals(read.Snapshot, loaded)));

        File.Delete(Path.Combine(root, "payloads", candidate.Payload + ".json"));
        File.Delete(Path.Combine(root, "manifests", candidate.Manifest + ".json"));

        Assert.AreSame(loaded, (await reader.ReadExactAsync(candidate.Manifest, CancellationToken.None).ConfigureAwait(false)).Snapshot);
        Assert.AreSame(loaded, (await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false)).Snapshot);
        Assert.AreEqual(1, reader.LoadCount, "A cache hit reads no bytes.");
        CollectionAssert.AreEqual(new[] { candidate.Manifest }, reader.CachedIdentities().ToArray());
    }

    [TestMethod]
    public async Task TheCacheHoldsTheTwoMostRecentlyUsedSnapshots()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var reader = Reader(store);
        var snapshots = await OrbitalElementsStoreRetentionTests.PublishSeriesAsync(store, clock, 3).ConfigureAwait(false);

        await ReadExactAvailableAsync(reader, snapshots[0]).ConfigureAwait(false);
        await ReadExactAvailableAsync(reader, snapshots[1]).ConfigureAwait(false);
        await ReadExactAvailableAsync(reader, snapshots[0]).ConfigureAwait(false);
        Assert.AreEqual(2, reader.LoadCount);
        CollectionAssert.AreEqual(new[] { snapshots[0].Manifest, snapshots[1].Manifest }, reader.CachedIdentities().ToArray());

        await ReadExactAvailableAsync(reader, snapshots[2]).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { snapshots[2].Manifest, snapshots[0].Manifest }, reader.CachedIdentities().ToArray(),
            "The least recently used identity is evicted.");
        await ReadExactAvailableAsync(reader, snapshots[0]).ConfigureAwait(false);
        Assert.AreEqual(3, reader.LoadCount);
        await ReadExactAvailableAsync(reader, snapshots[1]).ConfigureAwait(false);
        Assert.AreEqual(4, reader.LoadCount);
    }

    [TestMethod]
    [DataRow("payload-missing")]
    [DataRow("payload-truncated")]
    [DataRow("payload-extended")]
    [DataRow("payload-altered")]
    [DataRow("payload-directory")]
    [DataRow("payload-symlink")]
    [DataRow("manifest-missing")]
    [DataRow("manifest-altered")]
    [DataRow("manifest-directory")]
    public async Task StoredBytesThatDoNotProveTheirIdentityAreAnIntegrityFailure(string damage)
    {
        ArgumentNullException.ThrowIfNull(damage);
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var reader = Reader(store);
        var candidate = OrbitalElementsStoreTestData.Build("damaged", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        var file = damage.StartsWith("payload", StringComparison.Ordinal)
            ? Path.Combine(root, "payloads", candidate.Payload + ".json")
            : Path.Combine(root, "manifests", candidate.Manifest + ".json");
        var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        File.Delete(file);
        switch (damage[(damage.IndexOf('-', StringComparison.Ordinal) + 1)..])
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
                // Even byte-identical content is refused when it is reached through a link.
                var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside.json");
                await File.WriteAllBytesAsync(outside, bytes).ConfigureAwait(false);
                File.CreateSymbolicLink(file, outside);
                break;
            default:
                Assert.Fail("Unknown damage " + damage);
                break;
        }

        AssertUnavailable(OrbitalSnapshotUnavailableReason.IntegrityFailure,
            await reader.ReadExactAsync(candidate.Manifest, CancellationToken.None).ConfigureAwait(false));
        AssertUnavailable(OrbitalSnapshotUnavailableReason.IntegrityFailure,
            await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.IsEmpty(reader.CachedIdentities(), "A failed load is never cached.");
    }

    [TestMethod]
    public async Task RetirementRevalidatesTheCacheAndEvictsTheRetiredIdentity()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock, OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1));
        var reader = Reader(store);
        var snapshots = await OrbitalElementsStoreRetentionTests.PublishSeriesAsync(store, clock, 3).ConfigureAwait(false);
        await ReadExactAvailableAsync(reader, snapshots[0]).ConfigureAwait(false);
        await ReadExactAvailableAsync(reader, snapshots[2]).ConfigureAwait(false);
        Assert.AreEqual(2, reader.LoadCount);

        Assert.AreEqual(1, (await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false)).Retired);

        AssertUnavailable(OrbitalSnapshotUnavailableReason.Retired,
            await reader.ReadExactAsync(snapshots[0].Manifest, CancellationToken.None).ConfigureAwait(false));
        CollectionAssert.AreEqual(new[] { snapshots[2].Manifest }, reader.CachedIdentities().ToArray());
        AssertAvailable(snapshots[2], await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(2, reader.LoadCount, "A still-published cached snapshot is revalidated, not reloaded.");
        AssertUnavailable(OrbitalSnapshotUnavailableReason.Retired,
            await Reader(store).ReadExactAsync(snapshots[0].Manifest, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ARootOwnedByAnotherStoreIsStorageUnavailable()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var owner = OrbitalElementsStoreTestData.Open(root, clock);
        var candidate = OrbitalElementsStoreTestData.Build("owned", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(owner, clock, candidate).ConfigureAwait(false);
        using var intruder = OrbitalElementsStoreTestData.Open(root, clock);
        var reader = Reader(intruder);

        AssertUnavailable(OrbitalSnapshotUnavailableReason.StorageUnavailable,
            await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false));
        AssertUnavailable(OrbitalSnapshotUnavailableReason.StorageUnavailable,
            await reader.ReadExactAsync(candidate.Manifest, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ADisposedStoreIsStorageUnavailable()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var store = OrbitalElementsStoreTestData.Open(root, clock);
        var candidate = OrbitalElementsStoreTestData.Build("disposed", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        var reader = Reader(store);
        store.Dispose();

        AssertUnavailable(OrbitalSnapshotUnavailableReason.StorageUnavailable,
            await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false));
        AssertUnavailable(OrbitalSnapshotUnavailableReason.StorageUnavailable,
            await reader.ReadExactAsync(candidate.Manifest, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CancellationOfTheCallersTokenPropagates()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var candidate = OrbitalElementsStoreTestData.Build("cancelled", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Reader(store).ReadCurrentAsync(cancellation.Token).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Reader(store).ReadExactAsync(candidate.Manifest, cancellation.Token).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Reader(store, configured: false).ReadCurrentAsync(cancellation.Token).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AHaltedRefreshKeepsRetainedSnapshotsReadable()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var candidate = OrbitalElementsStoreTestData.Build("before-halt", "25544");
        await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        await store.RecordHaltAsync(attemptId, 403, "synthetic provider refusal", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull((await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Halt);
        var reader = Reader(store);

        AssertAvailable(candidate, await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false));
        AssertAvailable(candidate, await reader.ReadExactAsync(candidate.Manifest, CancellationToken.None).ConfigureAwait(false));
    }

    private static OrbitalSnapshotReader Reader(SqliteOrbitalElementsStore store, bool configured = true)
        => new(store, configured, NullLogger<OrbitalSnapshotReader>.Instance);

    private static async Task ReadExactAvailableAsync(OrbitalSnapshotReader reader, OrbitalElementsStoreTestData.Candidate candidate)
        => AssertAvailable(candidate, await reader.ReadExactAsync(candidate.Manifest, CancellationToken.None).ConfigureAwait(false));

    private static OrbitalSnapshot AssertAvailable(OrbitalElementsStoreTestData.Candidate expected, OrbitalSnapshotReadResult result)
    {
        Assert.IsNull(result.UnavailableReason);
        Assert.IsNotNull(result.Snapshot);
        Assert.AreEqual(expected.Manifest, result.Snapshot.Manifest.ManifestSha256);
        Assert.AreEqual(expected.Payload, result.Snapshot.Manifest.NormalizedPayloadSha256);
        return result.Snapshot;
    }

    private static void AssertUnavailable(OrbitalSnapshotUnavailableReason expected, OrbitalSnapshotReadResult result)
    {
        Assert.AreEqual(expected, result.UnavailableReason);
        Assert.IsNull(result.Snapshot);
    }
}
