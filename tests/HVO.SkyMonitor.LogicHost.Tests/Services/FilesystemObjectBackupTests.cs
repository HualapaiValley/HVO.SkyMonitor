using System.Text;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Tests.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class FilesystemObjectBackupTests
{
    private const string Artifacts = "skymonitor-artifacts";
    private const string Diagnostics = "skymonitor-diagnostics";
    private static readonly string[] Buckets = [Artifacts, Diagnostics];
    private static readonly CancellationToken None = CancellationToken.None;
    private string _temp = null!;
    private string _root = null!;
    private string _backup = null!;
    private FilesystemObjectStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Path.Combine(Path.GetTempPath(), "hvo-fsbk-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_temp, "root");
        _backup = Path.Combine(_temp, "backup");
        Directory.CreateDirectory(Path.Combine(_root, Artifacts));
        Directory.CreateDirectory(Path.Combine(_root, Diagnostics));
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(Path.Combine(_root, Artifacts), mode);
            File.SetUnixFileMode(Path.Combine(_root, Diagnostics), mode);
        }
        _store = OpenStore(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static FilesystemObjectStore OpenStore(string root)
    {
        var options = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        options.Filesystem.Root = root;
        var wrapped = Options.Create(options);
        return new FilesystemObjectStore(wrapped, new ObjectStoreTelemetry(wrapped), TimeProvider.System, NullLogger<FilesystemObjectStore>.Instance);
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private async Task WriteRestoreMarker(string phase, bool hadPrevious)
    {
        var marker = new FilesystemObjectRestoreMarker(
            "hvo-fs-object-restore-v1",
            Guid.NewGuid(),
            phase,
            Buckets.Select(bucket => new FilesystemObjectRestoreBucket(bucket, hadPrevious)).ToArray());
        await File.WriteAllTextAsync(
            Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName),
            System.Text.Json.JsonSerializer.Serialize(marker));
    }

    private async Task Put(string bucket, string key, string text, string contentType = "text/plain")
        => await _store.PutAsync(bucket, key, Bytes(text), Encoding.UTF8.GetByteCount(text), contentType, None);

    private static async Task<string> Read(FilesystemObjectStore store, string bucket, string key)
    {
        var sb = new StringBuilder();
        await store.ReadAsync(bucket, key, null, async (stream, ct) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            sb.Append(await reader.ReadToEndAsync(ct));
        }, None);
        return sb.ToString();
    }

    [TestMethod]
    public async Task BackupRecordsEveryLiveObjectExactlyAndNothingElse()
    {
        await Put(Artifacts, "sessions/a/frame.fits", "fits-bytes", "application/fits");
        await Put(Artifacts, "../../etc/passwd", "adversarial");
        await Put(Diagnostics, "diag/1", "d1");
        await Put(Artifacts, "sessions/a/frame.fits", "fits-bytes-v2", "application/fits"); // replace: retired generation exists
        await Put(Artifacts, "gone", "x");
        await _store.DeleteAsync(Artifacts, "gone", None);
        var g = (await _store.StatAsync(Artifacts, "sessions/a/frame.fits", None)).Generation;

        var inventory = await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);

        Assert.AreEqual(3, inventory.ObjectCount);
        Assert.AreEqual(FilesystemObjectBackup.InventorySchema, inventory.Schema);
        CollectionAssert.AreEqual(new[] { "../../etc/passwd", "sessions/a/frame.fits", "diag/1" }, inventory.Entries.Select(e => e.Key).ToArray(), "ordered by bucket then key, exact keys");
        var frame = inventory.Entries.Single(e => e.Key == "sessions/a/frame.fits");
        Assert.AreEqual("application/fits", frame.ContentType);
        Assert.AreEqual(13, frame.Length);
        Assert.AreEqual(g, frame.Generation, "the live generation, not the retired one");
        Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("fits-bytes-v2"))), frame.Sha256);

        var files = Directory.EnumerateFiles(_backup, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray();
        Assert.AreEqual(3, files.Count(f => f!.EndsWith(FilesystemObjectLayout.DataSuffix, StringComparison.Ordinal)), "one data file per live object: no retired generation, no deleted object");
        Assert.AreEqual(3, files.Count(f => f!.EndsWith(FilesystemObjectLayout.DescriptorSuffix, StringComparison.Ordinal)));
        Assert.IsTrue(File.Exists(Path.Combine(_backup, FilesystemObjectBackup.InventoryChecksumFileName)));
    }

    [TestMethod]
    public async Task BackupRefusesANonEmptyTargetAndAMissingBucket()
    {
        Directory.CreateDirectory(_backup);
        await File.WriteAllTextAsync(Path.Combine(_backup, "stray"), "x");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None));
        Directory.Delete(_backup, recursive: true);
        Directory.Delete(Path.Combine(_root, Diagnostics));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None));
    }

    [TestMethod]
    public async Task BackupRefusesACorruptStoreRatherThanRecordingIt()
    {
        await Put(Artifacts, "k", "payload");
        var g = (await _store.StatAsync(Artifacts, "k", None)).Generation;
        await File.WriteAllTextAsync(Path.Combine(_root, FilesystemObjectLayout.DataRelativePath(Artifacts, FilesystemObjectLayout.KeyHash("k"), g)), "PAYLOAD");
        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None));
        StringAssert.Contains(fault.Message, "corrupt");
    }

    [TestMethod]
    public async Task RestoreReproducesExactKeysMetadataLengthsGenerationsAndDigestsAndIsDestructive()
    {
        await Put(Artifacts, "keep/1", "one", "application/octet-stream");
        await Put(Artifacts, "keep/2", "two");
        await Put(Diagnostics, "d", "dd");
        var before = new Dictionary<string, ObjectStoreObjectMetadata>(StringComparer.Ordinal);
        foreach (var (b, k) in new[] { (Artifacts, "keep/1"), (Artifacts, "keep/2"), (Diagnostics, "d") })
        {
            before[b + "/" + k] = await _store.StatAsync(b, k, None);
        }
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);

        // Mutate after the backup: replace one, add one, delete one. Restore must undo all three.
        await Put(Artifacts, "keep/1", "one-changed");
        await Put(Artifacts, "added-later", "z");
        await _store.DeleteAsync(Artifacts, "keep/2", None);
        _store.Dispose();

        var inventory = await FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None);
        Assert.AreEqual(3, inventory.ObjectCount);

        using var restored = OpenStore(_root);
        Assert.AreEqual("one", await Read(restored, Artifacts, "keep/1"));
        Assert.AreEqual("two", await Read(restored, Artifacts, "keep/2"));
        Assert.AreEqual("dd", await Read(restored, Diagnostics, "d"));
        var gone = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => restored.StatAsync(Artifacts, "added-later", None));
        Assert.AreEqual(ObjectStoreFailureKind.MissingObject, gone.Kind, "destructive: an object not in the backup does not survive");
        foreach (var (path, expected) in before)
        {
            var parts = path.Split('/', 2);
            var actual = await restored.StatAsync(parts[0], parts[1], None);
            Assert.AreEqual(expected.Key, actual.Key);
            Assert.AreEqual(expected.ContentType, actual.ContentType);
            Assert.AreEqual(expected.ContentLength, actual.ContentLength);
            Assert.AreEqual(expected.Generation, actual.Generation, "generations are retained by this backup contract");
            Assert.AreEqual(expected.LastModifiedUtc, actual.LastModifiedUtc);
        }
        Assert.AreEqual(0, await FilesystemObjectBackup.VerifyAsync(_backup, _root, None));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, Artifacts + ".restoring")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, Artifacts + ".replaced")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
                File.GetUnixFileMode(Path.Combine(_root, Artifacts)));
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
                File.GetUnixFileMode(Path.Combine(_root, Diagnostics)));
        }
    }

    [TestMethod]
    public async Task RestoreIntoAnEmptyRootWorksAndIsWhatDisasterRecoveryLooksLike()
    {
        await Put(Artifacts, "k", "v");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        var fresh = Path.Combine(_temp, "fresh-root");
        await FilesystemObjectBackup.RestoreAsync(_backup, fresh, Buckets, None);
        using var restored = OpenStore(fresh);
        Assert.AreEqual("v", await Read(restored, Artifacts, "k"));
        Assert.IsTrue(Directory.Exists(Path.Combine(fresh, Diagnostics)), "an empty bucket in the backup is restored as an empty bucket");
    }

    [TestMethod]
    public async Task DamagedBackupIsRefusedBeforeAnyBucketIsTouched()
    {
        await Put(Artifacts, "k", "original");
        await Put(Diagnostics, "d", "dd");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        await Put(Artifacts, "k", "current");
        var currentGeneration = (await _store.StatAsync(Artifacts, "k", None)).Generation;

        // Damage a data file inside the backup (in the second bucket's staging order it does
        // not matter: staging completes for all buckets before any swap).
        var damaged = Directory.EnumerateFiles(Path.Combine(_backup, Diagnostics), "*" + FilesystemObjectLayout.DataSuffix, SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(damaged, "XX");
        _store.Dispose();
        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None));
        StringAssert.Contains(fault.Message, "nothing has been restored");
        using var reopened = OpenStore(_root);
        Assert.AreEqual(currentGeneration, (await reopened.StatAsync(Artifacts, "k", None)).Generation, "the live bucket is untouched");
        Assert.AreEqual("current", await Read(reopened, Artifacts, "k"));

        // A tampered inventory is refused by its checksum before anything is read.
        var inventoryPath = Path.Combine(_backup, FilesystemObjectBackup.InventoryFileName);
        await File.WriteAllTextAsync(inventoryPath, (await File.ReadAllTextAsync(inventoryPath)).Replace("\"k\"", "\"q\"", StringComparison.Ordinal));
        var tampered = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None));
        StringAssert.Contains(tampered.Message, "checksum");
    }

    [TestMethod]
    public async Task RestoreRefusesABackupMissingAConfiguredBucket()
    {
        await Put(Artifacts, "k", "v");
        await FilesystemObjectBackup.BackupAsync(_root, [Artifacts], _backup, TimeProvider.System, None);
        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None));
        StringAssert.Contains(fault.Message, Diagnostics);
    }

    [TestMethod]
    public async Task InterruptedSwapIsHealedByPuttingThePreviousBucketBack()
    {
        // The narrowest failure window: the previous bucket moved to .replaced, the process
        // died before the staged bucket moved in. The store has no bucket at all. The next
        // restore must first make the store whole (previous bucket back), then proceed.
        await Put(Artifacts, "k", "previous");
        await Put(Diagnostics, "d", "dd");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        Directory.Move(Path.Combine(_root, Artifacts), Path.Combine(_root, Artifacts + ".replaced"));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, Artifacts)));
        _store.Dispose();

        await FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None);
        using var restored = OpenStore(_root);
        Assert.AreEqual("previous", await Read(restored, Artifacts, "k"));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, Artifacts + ".replaced")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, Artifacts + ".restoring")));
    }

    [TestMethod]
    public async Task ReplacedBesideALiveBucketIsRefusedAndNeitherIsTouched()
    {
        await Put(Artifacts, "k", "live");
        await Put(Diagnostics, "d", "dd");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        Directory.CreateDirectory(Path.Combine(_root, Artifacts + ".replaced"));
        await File.WriteAllTextAsync(Path.Combine(_root, Artifacts + ".replaced", "marker"), "x");
        _store.Dispose();
        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None));
        StringAssert.Contains(fault.Message, ".replaced");
        using var reopened = OpenStore(_root);
        Assert.AreEqual("live", await Read(reopened, Artifacts, "k"));
        Assert.IsTrue(File.Exists(Path.Combine(_root, Artifacts + ".replaced", "marker")));
    }

    [TestMethod]
    public async Task RuntimeOwnershipRejectsASecondReplicaAndOfflineRestore()
    {
        await Put(Artifacts, "k", "live");
        await Put(Diagnostics, "d", "dd");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);

        Assert.ThrowsExactly<InvalidOperationException>(() => OpenStore(_root));
        var restore = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None));
        StringAssert.Contains(restore.Message, "LogicHost is running");
        Assert.IsFalse(File.Exists(Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName)));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, Artifacts + ".restoring")));
        Assert.AreEqual("live", await Read(_store, Artifacts, "k"));
    }

    [TestMethod]
    public async Task PreparedRestoreMarkerRollsEveryBucketBackBeforeRetry()
    {
        await Put(Artifacts, "a", "old-a");
        await Put(Diagnostics, "d", "old-d");
        var previousArtifacts = Path.Combine(_root, Artifacts + ".replaced");
        Directory.Move(Path.Combine(_root, Artifacts), previousArtifacts);
        Directory.CreateDirectory(Path.Combine(_root, Artifacts));
        await File.WriteAllTextAsync(Path.Combine(_root, Artifacts, "new-marker"), "new");
        var previousDiagnostics = Path.Combine(_root, Diagnostics + ".replaced");
        Directory.Move(Path.Combine(_root, Diagnostics), previousDiagnostics);
        var stagingDiagnostics = Path.Combine(_root, Diagnostics + ".restoring");
        Directory.CreateDirectory(stagingDiagnostics);
        await File.WriteAllTextAsync(Path.Combine(stagingDiagnostics, "staged-marker"), "new");
        await WriteRestoreMarker("prepared", hadPrevious: true);
        _store.Dispose();

        Assert.ThrowsExactly<InvalidOperationException>(() => OpenStore(_root));
        FilesystemObjectBackup.RecoverInterruptedRestore(HVO.SkyMonitor.Storage.FileSystem.PhysicalRoot.Open(_root), Buckets);

        using var recovered = OpenStore(_root);
        Assert.AreEqual("old-a", await Read(recovered, Artifacts, "a"));
        Assert.AreEqual("old-d", await Read(recovered, Diagnostics, "d"));
        Assert.IsFalse(Directory.Exists(previousArtifacts));
        Assert.IsFalse(Directory.Exists(previousDiagnostics));
        Assert.IsFalse(Directory.Exists(stagingDiagnostics));
        Assert.IsFalse(File.Exists(Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName)));
    }

    [TestMethod]
    public async Task CommittedRestoreMarkerKeepsRestoredBucketsAndFinishesCleanup()
    {
        await Put(Artifacts, "a", "new-a");
        await Put(Diagnostics, "d", "new-d");
        foreach (var bucket in Buckets)
        {
            var replaced = Path.Combine(_root, bucket + ".replaced");
            Directory.CreateDirectory(replaced);
            await File.WriteAllTextAsync(Path.Combine(replaced, "old-marker"), "old");
        }
        await WriteRestoreMarker("committed", hadPrevious: true);
        _store.Dispose();

        Assert.ThrowsExactly<InvalidOperationException>(() => OpenStore(_root));
        FilesystemObjectBackup.RecoverInterruptedRestore(HVO.SkyMonitor.Storage.FileSystem.PhysicalRoot.Open(_root), Buckets);

        using var recovered = OpenStore(_root);
        Assert.AreEqual("new-a", await Read(recovered, Artifacts, "a"));
        Assert.AreEqual("new-d", await Read(recovered, Diagnostics, "d"));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, Artifacts + ".replaced")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, Diagnostics + ".replaced")));
        Assert.IsFalse(File.Exists(Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName)));
    }

    [TestMethod]
    public async Task MalformedOrImpossibleRestoreMarkerStaysFenced()
    {
        var markerPath = Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName);
        await File.WriteAllTextAsync(markerPath, "{not-json");
        _store.Dispose();
        Assert.ThrowsExactly<InvalidOperationException>(() => OpenStore(_root));
        Assert.ThrowsExactly<InvalidOperationException>(() => FilesystemObjectBackup.RecoverInterruptedRestore(HVO.SkyMonitor.Storage.FileSystem.PhysicalRoot.Open(_root), Buckets));
        Assert.IsTrue(File.Exists(markerPath));

        await WriteRestoreMarker("committed", hadPrevious: false);
        Directory.Delete(Path.Combine(_root, Diagnostics), recursive: true);
        Assert.ThrowsExactly<InvalidOperationException>(() => FilesystemObjectBackup.RecoverInterruptedRestore(HVO.SkyMonitor.Storage.FileSystem.PhysicalRoot.Open(_root), Buckets));
        Assert.IsTrue(File.Exists(markerPath));
    }

    [TestMethod]
    public void ReservedMarkerDirectoryFailsClosedAtRuntimeStartup()
    {
        _store.Dispose();
        Directory.CreateDirectory(Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName));
        Assert.ThrowsExactly<InvalidOperationException>(() => OpenStore(_root));
        Assert.ThrowsExactly<InvalidOperationException>(() => FilesystemObjectBackup.RecoverInterruptedRestore(
            HVO.SkyMonitor.Storage.FileSystem.PhysicalRoot.Open(_root), Buckets));
    }

    [TestMethod]
    public void DanglingRestoreMarkerLinkFailsClosedAtRuntimeStartup()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux qualification case.");
        }
        _store.Dispose();
        File.CreateSymbolicLink(
            Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName),
            Path.Combine(_root, "absent-marker-target"));
        var fault = Assert.ThrowsExactly<HVO.SkyMonitor.Storage.FileSystem.FileSystemFaultException>(() => OpenStore(_root));
        Assert.AreEqual(HVO.SkyMonitor.Storage.FileSystem.FileSystemFaultKind.Containment, fault.Kind);
    }

    [TestMethod]
    public void FifoRestoreMarkerFailsClosedWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux qualification case.");
        }
        _store.Dispose();
        var path = Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName);
        Assert.AreEqual(0, NativeMkFifo(path, Convert.ToUInt32("600", 8)));
        Assert.ThrowsExactly<InvalidOperationException>(() => OpenStore(_root));
    }

    [TestMethod]
    public async Task ContradictoryCommittedStateKeepsMarkerAndRollbackData()
    {
        await Put(Artifacts, "a", "old-a");
        await Put(Diagnostics, "d", "new-d");
        var replaced = Path.Combine(_root, Artifacts + ".replaced");
        Directory.Move(Path.Combine(_root, Artifacts), replaced);
        var outside = Path.Combine(_temp, "outside-live");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, Artifacts), outside);
        await WriteRestoreMarker("committed", hadPrevious: true);
        _store.Dispose();

        var fault = Assert.ThrowsExactly<HVO.SkyMonitor.Storage.FileSystem.FileSystemFaultException>(
            () => FilesystemObjectBackup.RecoverInterruptedRestore(HVO.SkyMonitor.Storage.FileSystem.PhysicalRoot.Open(_root), Buckets));
        Assert.AreEqual(HVO.SkyMonitor.Storage.FileSystem.FileSystemFaultKind.Containment, fault.Kind);
        Assert.IsTrue(File.Exists(Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName)));
        Assert.IsTrue(Directory.Exists(replaced));
        Assert.AreEqual("old-a", await File.ReadAllTextAsync(Directory.EnumerateFiles(replaced, "*" + FilesystemObjectLayout.DataSuffix, SearchOption.AllDirectories).Single()));
    }

    [TestMethod]
    public async Task LaterPreparedContradictionDoesNotMutateEarlierBuckets()
    {
        await Put(Artifacts, "a", "old-a");
        await Put(Diagnostics, "d", "old-d");
        var replacedArtifacts = Path.Combine(_root, Artifacts + ".replaced");
        Directory.Move(Path.Combine(_root, Artifacts), replacedArtifacts);
        Directory.CreateDirectory(Path.Combine(_root, Artifacts));
        await File.WriteAllTextAsync(Path.Combine(_root, Artifacts, "restored-marker"), "new");
        // Diagnostics says it had a previous bucket, but has neither live nor rollback state.
        Directory.Delete(Path.Combine(_root, Diagnostics), recursive: true);
        await WriteRestoreMarker("prepared", hadPrevious: true);
        _store.Dispose();

        Assert.ThrowsExactly<InvalidOperationException>(() => FilesystemObjectBackup.RecoverInterruptedRestore(
            HVO.SkyMonitor.Storage.FileSystem.PhysicalRoot.Open(_root), Buckets));
        Assert.IsTrue(File.Exists(Path.Combine(_root, Artifacts, "restored-marker")), "the earlier bucket was not rolled back before validating the later one");
        Assert.IsTrue(Directory.Exists(replacedArtifacts));
        Assert.IsTrue(File.Exists(Path.Combine(_root, FilesystemObjectBackup.RestoreMarkerFileName)));
    }

    [TestMethod]
    public async Task LeftoverStagingThatIsALinkIsRefusedNotDeletedThrough()
    {
        await Put(Artifacts, "k", "v");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        var victim = Path.Combine(_temp, "victim-dir");
        Directory.CreateDirectory(victim);
        await File.WriteAllTextAsync(Path.Combine(victim, "keep"), "x");
        Directory.CreateSymbolicLink(Path.Combine(_root, Artifacts + ".restoring"), victim);
        await Assert.ThrowsAsync<Exception>(() => FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None));
        Assert.IsTrue(File.Exists(Path.Combine(victim, "keep")), "the link target was not deleted through");
    }

    [TestMethod]
    public async Task VerifyCountsEveryKindOfDrift()
    {
        await Put(Artifacts, "a", "aa");
        await Put(Artifacts, "b", "bb");
        await Put(Artifacts, "c", "cc");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        Assert.AreEqual(0, await FilesystemObjectBackup.VerifyAsync(_backup, _root, None));
        await Put(Artifacts, "a", "changed");           // generation differs
        await _store.DeleteAsync(Artifacts, "b", None);  // descriptor gone
        var gc = (await _store.StatAsync(Artifacts, "c", None)).Generation;
        await File.WriteAllTextAsync(Path.Combine(_root, FilesystemObjectLayout.DataRelativePath(Artifacts, FilesystemObjectLayout.KeyHash("c"), gc)), "CC"); // bytes differ
        Assert.AreEqual(3, await FilesystemObjectBackup.VerifyAsync(_backup, _root, None));

        await Put(Artifacts, "unexpected", "extra");
        Assert.AreEqual(4, await FilesystemObjectBackup.VerifyAsync(_backup, _root, None), "extra live keys are inventory drift too");
    }

    [TestMethod]
    public async Task MetadataOnlyDriftFailsVerificationAndRestoreBeforeSwap()
    {
        await Put(Artifacts, "k", "original");
        await Put(Diagnostics, "diagnostic", "unchanged");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        var relative = FilesystemObjectLayout.DescriptorRelativePath(Artifacts, FilesystemObjectLayout.KeyHash("k"));
        var path = Path.Combine(_backup, relative);
        var original = System.Text.Json.JsonSerializer.Deserialize<FilesystemObjectDescriptor>(
            await File.ReadAllTextAsync(path), FilesystemObjectLayout.DescriptorJson)!;
        await Put(Artifacts, "k", "current");

        foreach (var changed in new[]
                 {
                     original with { ContentType = "application/changed" },
                     original with { ModifiedUtc = original.ModifiedUtc.AddSeconds(1) }
                 })
        {
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(changed, FilesystemObjectLayout.DescriptorJson));
            Assert.AreEqual(1, await FilesystemObjectBackup.VerifyAsync(_backup, _backup, None));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None));
            Assert.AreEqual("current", await Read(_store, Artifacts, "k"), "metadata mismatch must be rejected before replacing live data");
        }
    }

    [TestMethod]
    public async Task EmptyBucketsArePresentAndVerifiableInBackupAndRestore()
    {
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        foreach (var bucket in Buckets)
        {
            Assert.IsTrue(Directory.Exists(Path.Combine(_backup, bucket)));
        }
        Assert.AreEqual(0, await FilesystemObjectBackup.VerifyAsync(_backup, _backup, None));
        var restored = Path.Combine(_temp, "empty-restored");
        await FilesystemObjectBackup.RestoreAsync(_backup, restored, Buckets, None);
        Assert.AreEqual(0, await FilesystemObjectBackup.VerifyAsync(_backup, restored, None));
        Directory.Delete(Path.Combine(restored, Diagnostics));
        Assert.AreEqual(1, await FilesystemObjectBackup.VerifyAsync(_backup, restored, None));
    }

    [TestMethod]
    public async Task BackupRejectsInvalidDescriptorMetadataBeforePublishingInventory()
    {
        await Put(Artifacts, "k", "payload");
        var path = Path.Combine(_root, FilesystemObjectLayout.DescriptorRelativePath(Artifacts, FilesystemObjectLayout.KeyHash("k")));
        var descriptor = System.Text.Json.JsonSerializer.Deserialize<FilesystemObjectDescriptor>(
            await File.ReadAllTextAsync(path), FilesystemObjectLayout.DescriptorJson)!;
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(
            descriptor with { ContentType = "" }, FilesystemObjectLayout.DescriptorJson));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None));
        Assert.IsFalse(File.Exists(Path.Combine(_backup, FilesystemObjectBackup.InventoryChecksumFileName)));
    }

    [TestMethod]
    public async Task InvalidInventoriesAreRejectedBeforeCreatingRestoreState()
    {
        await Put(Artifacts, "k", "original");
        var inventory = await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        var entry = inventory.Entries.Single();
        var invalid = new[]
        {
            inventory with { Buckets = null! },
            inventory with { Entries = null! },
            inventory with { Buckets = [Artifacts, Artifacts] },
            inventory with { Buckets = ["../escape"] },
            inventory with { Entries = [null!] },
            inventory with { Entries = [entry, entry], ObjectCount = 2, TotalBytes = entry.Length * 2 },
            inventory with { Entries = [entry with { Bucket = "unlisted" }] },
            inventory with { Entries = [entry with { Key = null! }] },
            inventory with { Entries = [entry with { Length = -1 }] },
            inventory with { Entries = [entry with { Generation = "../escape" }] },
            inventory with { Entries = [entry with { Sha256 = new string('z', 64) }] },
            inventory with { Entries = [entry with { ContentType = "" }] },
            inventory with { TotalBytes = inventory.TotalBytes + 1 },
            inventory with { Buckets = [Artifacts, Artifacts + ".restoring"] }
        };
        var target = Path.Combine(_temp, "must-not-be-created");
        foreach (var candidate in invalid)
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(candidate);
            await File.WriteAllBytesAsync(Path.Combine(_backup, FilesystemObjectBackup.InventoryFileName), bytes);
            await File.WriteAllTextAsync(Path.Combine(_backup, FilesystemObjectBackup.InventoryChecksumFileName),
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) + "  inventory.json\n");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, target, Buckets, None));
            Assert.IsFalse(Directory.Exists(target), "invalid inventory must fail before creating restore state");
            Assert.AreEqual("original", await Read(_store, Artifacts, "k"));
        }
    }

    [TestMethod]
    public async Task OverlappingRootsAreRejectedBeforeMutation()
    {
        await Put(Artifacts, "k", "original");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        foreach (var target in new[] { _backup, Path.Combine(_backup, "child"), _temp })
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, target, Buckets, None));
        }
        var nestedBackup = Path.Combine(_root, "nested-backup");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.BackupAsync(_root, Buckets, nestedBackup, TimeProvider.System, None));
        Assert.IsFalse(Directory.Exists(nestedBackup));
        Assert.IsFalse(Directory.Exists(Path.Combine(_backup, "child")));
        if (OperatingSystem.IsLinux())
        {
            var alias = Path.Combine(_temp, "backup-alias");
            Directory.CreateSymbolicLink(alias, _backup);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, Path.Combine(alias, "child"), Buckets, None));
            Assert.IsFalse(Directory.Exists(Path.Combine(_backup, "child")), "ancestor aliases must not bypass overlap rejection");
        }
        Assert.AreEqual("original", await Read(_store, Artifacts, "k"));
        Assert.AreEqual(0, await FilesystemObjectBackup.VerifyAsync(_backup, _backup, None));
    }

    [TestMethod]
    public async Task BackupNeverFollowsALinkOutOfTheRoot()
    {
        await Put(Artifacts, "k", "v");
        var g = (await _store.StatAsync(Artifacts, "k", None)).Generation;
        var outside = Path.Combine(_temp, "outside-secret");
        await File.WriteAllTextAsync(outside, "secret");
        var dataPath = Path.Combine(_root, FilesystemObjectLayout.DataRelativePath(Artifacts, FilesystemObjectLayout.KeyHash("k"), g));
        File.Delete(dataPath);
        File.CreateSymbolicLink(dataPath, outside);
        // The #592 root refuses to resolve through the link before the copy's own check runs.
        var fault = await Assert.ThrowsExactlyAsync<HVO.SkyMonitor.Storage.FileSystem.FileSystemFaultException>(() => FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None));
        Assert.AreEqual(HVO.SkyMonitor.Storage.FileSystem.FileSystemFaultKind.Containment, fault.Kind);
        Assert.IsFalse(Directory.EnumerateFiles(_backup, "*", SearchOption.AllDirectories).Any(f => f.EndsWith(FilesystemObjectLayout.DataSuffix, StringComparison.Ordinal)), "no bytes from outside the root were copied");
    }

#pragma warning disable SYSLIB1054 // Test-only creation of an actual FIFO for the Linux qualification case.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "mkfifo", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMkFifo(string path, uint mode);
#pragma warning restore SYSLIB1054
}
