using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// Retention retires only snapshots outside both the count and age policy, never the active, previous-active,
/// referenced or pinned snapshot, commits the tombstone before deleting files, keeps files another published
/// manifest still needs, and refuses entries that are not contained regular files.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsStoreRetentionTests : OrbitalStoreTestBase
{
    [TestMethod]
    public async Task SnapshotsOutsideThePolicyAreTombstonedThenDeletedAndTheActivePairIsProtected()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock, OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1));
        var snapshots = await PublishSeriesAsync(store, clock, 5).ConfigureAwait(false);

        var result = await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new OrbitalRetentionResult(Retired: 3, FilesDeleted: 9, Protected: 1, Refused: 0), result);
        AssertFilesExactly(root, snapshots[3], snapshots[4]);
        Assert.AreEqual("retired,retired,retired,published,published", OrbitalElementsStoreTestData.Scalar(root,
            "SELECT group_concat(state, ',') FROM (SELECT state FROM orbital_snapshots ORDER BY snapshot_sequence);"));
        Assert.AreEqual(0L, OrbitalElementsStoreTestData.Scalar(root, "SELECT COUNT(*) FROM orbital_snapshots WHERE files_pending = 1;"));
        Assert.AreEqual(snapshots[4].Manifest, (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).CurrentManifestSha256);

        var rerun = await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new OrbitalRetentionResult(0, 0, 1, 0), rerun, "Retention is idempotent.");
        AssertFilesExactly(root, snapshots[3], snapshots[4]);
    }

    [TestMethod]
    public async Task MinimumRetentionKeepsRecentSnapshotsBeyondTheCountLimit()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock,
            OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1, minimumRetention: TimeSpan.FromHours(5)));
        var snapshots = await PublishSeriesAsync(store, clock, 4).ConfigureAwait(false);

        // Ages are 8h, 6h, 4h and 2h: only the two older than five hours leave retention.
        var result = await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new OrbitalRetentionResult(2, 6, 0, 0), result);
        AssertFilesExactly(root, snapshots[2], snapshots[3]);
    }

    [TestMethod]
    public async Task ReferencedAndPinnedSnapshotsAreProtectedUntilReleased()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock, OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1));
        var snapshots = await PublishSeriesAsync(store, clock, 4).ConfigureAwait(false);
        const string Holder = "product:synthetic-1";

        Assert.IsTrue(await store.AddReferenceAsync(snapshots[0].Manifest, Holder, CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue(await store.AddReferenceAsync(snapshots[0].Manifest, Holder, CancellationToken.None).ConfigureAwait(false),
            "Adding the same reference again is idempotent.");
        Assert.IsFalse(await store.AddReferenceAsync(OrbitalElementsStoreTestData.Sha('A'), Holder, CancellationToken.None).ConfigureAwait(false),
            "An unknown manifest cannot be referenced.");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.AddReferenceAsync(
            new string('a', 64), Holder, CancellationToken.None).AsTask()).ConfigureAwait(false);
        var pin = await store.PinAsync(snapshots[1].Manifest, CancellationToken.None).ConfigureAwait(false);

        var held = await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new OrbitalRetentionResult(0, 0, 3, 0), held);
        AssertFilesExactly(root, snapshots);

        pin.Dispose();
        pin.Dispose();
        Assert.IsFalse(store.IsPinned(snapshots[1].Manifest));
        Assert.IsTrue(await store.RemoveReferenceAsync(snapshots[0].Manifest, Holder, CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(await store.RemoveReferenceAsync(snapshots[0].Manifest, Holder, CancellationToken.None).ConfigureAwait(false),
            "A holder that holds nothing releases nothing.");

        var released = await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new OrbitalRetentionResult(2, 6, 1, 0), released);
        AssertFilesExactly(root, snapshots[2], snapshots[3]);
        Assert.IsFalse(await store.AddReferenceAsync(snapshots[0].Manifest, Holder, CancellationToken.None).ConfigureAwait(false),
            "A retired manifest cannot be referenced again.");
    }

    [TestMethod]
    public async Task APayloadOrRawFileAnotherPublishedManifestNeedsSurvives()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock, OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1));
        var first = OrbitalElementsStoreTestData.Build("shared-a", "25544");
        var second = OrbitalElementsStoreTestData.Build("shared-b", "25544");
        var third = OrbitalElementsStoreTestData.Build("shared-c", "43013");
        foreach (var candidate in new[] { first, second, third })
        {
            await OrbitalElementsStoreTestData.PublishAsync(store, clock, candidate).ConfigureAwait(false);
        }

        var result = await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new OrbitalRetentionResult(1, 2, 1, 0), result);
        Assert.IsTrue(File.Exists(Path.Combine(root, "payloads", first.Payload + ".json")), "The second manifest still binds this payload.");
        Assert.IsFalse(File.Exists(Path.Combine(root, "manifests", first.Manifest + ".json")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "raw", first.RawSha256 + ".json")));
    }

    [TestMethod]
    public async Task AnAmbiguousManifestEntryRefusesRetirementWithoutATombstone()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock, OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1));
        var snapshots = await PublishSeriesAsync(store, clock, 3).ConfigureAwait(false);
        var manifestPath = Path.Combine(root, "manifests", snapshots[0].Manifest + ".json");
        File.Delete(manifestPath);
        Directory.CreateDirectory(manifestPath);

        var result = await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new OrbitalRetentionResult(0, 0, 1, 1), result);
        Assert.IsTrue(Directory.Exists(manifestPath));
        Assert.AreEqual("published", OrbitalElementsStoreTestData.Scalar(root,
            $"SELECT state FROM orbital_snapshots WHERE manifest_sha256 = '{snapshots[0].Manifest}';"));
    }

    [TestMethod]
    public async Task AnAmbiguousPayloadEntryKeepsTheTombstonePendingUntilItIsResolved()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock, OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1));
        var snapshots = await PublishSeriesAsync(store, clock, 3).ConfigureAwait(false);
        var payloadPath = Path.Combine(root, "payloads", snapshots[0].Payload + ".json");
        File.Delete(payloadPath);
        Directory.CreateDirectory(payloadPath);
        var pendingSql = $"SELECT state || ':' || files_pending FROM orbital_snapshots WHERE manifest_sha256 = '{snapshots[0].Manifest}';";

        var refused = await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new OrbitalRetentionResult(1, 2, 1, 1), refused);
        Assert.AreEqual("retired:1", OrbitalElementsStoreTestData.Scalar(root, pendingSql));
        Assert.IsTrue(Directory.Exists(payloadPath), "Retention never deletes an entry that is not a regular file.");

        Directory.Delete(payloadPath);
        var finished = await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new OrbitalRetentionResult(0, 0, 1, 0), finished);
        Assert.AreEqual("retired:0", OrbitalElementsStoreTestData.Scalar(root, pendingSql));
    }

    [TestMethod]
    public async Task ACrashAfterTheTombstoneCommitsIsFinishedByTheNextRun()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var settings = OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1);
        var faults = new OrbitalElementsStoreTestData.Faults();
        OrbitalElementsStoreTestData.Candidate[] snapshots;
        using (var store = OrbitalElementsStoreTestData.Open(root, clock, settings, faults))
        {
            snapshots = await PublishSeriesAsync(store, clock, 3).ConfigureAwait(false);
            faults.ArmedPoint = OrbitalStorageFaultPoint.AfterTombstone;
            faults.Throw = static () => new InvalidOperationException("simulated process crash");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => store.ApplyRetentionAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        }
        Assert.AreEqual("retired:1", OrbitalElementsStoreTestData.Scalar(root,
            $"SELECT state || ':' || files_pending FROM orbital_snapshots WHERE manifest_sha256 = '{snapshots[0].Manifest}';"));
        AssertFilesExactly(root, snapshots);

        using var reopened = OrbitalElementsStoreTestData.Open(root, clock, settings);
        var result = await reopened.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new OrbitalRetentionResult(0, 3, 1, 0), result);
        AssertFilesExactly(root, snapshots[1], snapshots[2]);
        Assert.AreEqual(0L, OrbitalElementsStoreTestData.Scalar(root, "SELECT COUNT(*) FROM orbital_snapshots WHERE files_pending = 1;"));
    }

    [TestMethod]
    public async Task RetirementIsFinalAndARetiredIdentityIsNeverRepublished()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock, OrbitalElementsStoreTestData.Settings(root, maximumRetained: 1));
        var snapshots = await PublishSeriesAsync(store, clock, 3).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ApplyRetentionAsync(CancellationToken.None).ConfigureAwait(false)).Retired);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);

        var failure = await Assert.ThrowsExactlyAsync<OrbitalStorageException>(() => store.PublishAndActivateAsync(
            attemptId, snapshots[0].Snapshot, snapshots[0].Raw, CancellationToken.None).AsTask()).ConfigureAwait(false);

        Assert.AreEqual("orbital.manifest-retired", failure.ReasonCode);
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(snapshots[2].Manifest, state.CurrentManifestSha256);
        Assert.AreEqual((OrbitalAttemptOutcome.StorageFailed, "orbital.manifest-retired"),
            (state.LastAttemptOutcome!.Value, state.LastAttemptReasonCode));
        AssertFilesExactly(root, snapshots[1], snapshots[2]);
        Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(() => OrbitalElementsStoreTestData.Execute(root,
            $"UPDATE orbital_snapshots SET state = 'published', retired_unix_ms = NULL, files_pending = 0 WHERE manifest_sha256 = '{snapshots[0].Manifest}';"));
    }

    /// <summary>Publishes <paramref name="count"/> snapshots with distinct payloads, two hours apart.</summary>
    internal static async Task<OrbitalElementsStoreTestData.Candidate[]> PublishSeriesAsync(
        SqliteOrbitalElementsStore store,
        OrbitalElementsStoreTestData.Clock clock,
        int count)
    {
        var snapshots = new OrbitalElementsStoreTestData.Candidate[count];
        for (var index = 0; index < count; index++)
        {
            var number = (10001 + index).ToString(CultureInfo.InvariantCulture);
            snapshots[index] = OrbitalElementsStoreTestData.Build("series-" + number, number);
            await OrbitalElementsStoreTestData.PublishAsync(store, clock, snapshots[index]).ConfigureAwait(false);
        }
        return snapshots;
    }

    private static void AssertFilesExactly(string root, params OrbitalElementsStoreTestData.Candidate[] snapshots)
    {
        CollectionAssert.AreEquivalent(snapshots.Select(snapshot => snapshot.Manifest + ".json").ToArray(),
            OrbitalElementsStoreTestData.Files(root, "manifests"));
        CollectionAssert.AreEquivalent(snapshots.Select(snapshot => snapshot.Payload + ".json").Distinct().ToArray(),
            OrbitalElementsStoreTestData.Files(root, "payloads"));
        CollectionAssert.AreEquivalent(snapshots.Select(snapshot => snapshot.RawSha256 + ".json").ToArray(),
            OrbitalElementsStoreTestData.Files(root, "raw"));
    }
}
