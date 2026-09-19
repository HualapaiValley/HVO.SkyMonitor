using System.Text;
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
        _store = OpenStore(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
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

        var inventory = await FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None);
        Assert.AreEqual(3, inventory.ObjectCount);

        var restored = OpenStore(_root);
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
    }

    [TestMethod]
    public async Task RestoreIntoAnEmptyRootWorksAndIsWhatDisasterRecoveryLooksLike()
    {
        await Put(Artifacts, "k", "v");
        await FilesystemObjectBackup.BackupAsync(_root, Buckets, _backup, TimeProvider.System, None);
        var fresh = Path.Combine(_temp, "fresh-root");
        await FilesystemObjectBackup.RestoreAsync(_backup, fresh, Buckets, None);
        Assert.AreEqual("v", await Read(OpenStore(fresh), Artifacts, "k"));
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
        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None));
        StringAssert.Contains(fault.Message, "nothing has been restored");
        Assert.AreEqual(currentGeneration, (await _store.StatAsync(Artifacts, "k", None)).Generation, "the live bucket is untouched");
        Assert.AreEqual("current", await Read(_store, Artifacts, "k"));

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

        await FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None);
        Assert.AreEqual("previous", await Read(OpenStore(_root), Artifacts, "k"));
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
        var fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FilesystemObjectBackup.RestoreAsync(_backup, _root, Buckets, None));
        StringAssert.Contains(fault.Message, ".replaced");
        Assert.AreEqual("live", await Read(_store, Artifacts, "k"));
        Assert.IsTrue(File.Exists(Path.Combine(_root, Artifacts + ".replaced", "marker")));
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
}
