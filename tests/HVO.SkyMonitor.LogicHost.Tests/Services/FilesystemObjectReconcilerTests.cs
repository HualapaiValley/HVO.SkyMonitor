using System.Text;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Tests.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class FilesystemObjectReconcilerTests
{
    private const string Bucket = "skymonitor-artifacts";
    private string _temp = null!;
    private FixedTimeProvider _clock = null!;
    private FilesystemObjectStore _store = null!;
    private FilesystemObjectReconciler _reconciler = null!;
    private static readonly CancellationToken None = CancellationToken.None;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Path.Combine(Path.GetTempPath(), "hvo-fsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_temp, Bucket));
        Directory.CreateDirectory(Path.Combine(_temp, "skymonitor-diagnostics"));
        _clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-19T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var options = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        options.Filesystem.Root = _temp;
        var wrapped = Options.Create(options);
        _store = new FilesystemObjectStore(wrapped, new ObjectStoreTelemetry(wrapped), _clock, NullLogger<FilesystemObjectStore>.Instance);
        _reconciler = new FilesystemObjectReconciler(_store, _clock, NullLogger<FilesystemObjectReconciler>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private string KeyDir(string key) => Path.Combine(_temp, FilesystemObjectLayout.KeyDirectoryRelativePath(Bucket, FilesystemObjectLayout.KeyHash(key)));
    private string DescriptorPath(string key) => Path.Combine(_temp, FilesystemObjectLayout.DescriptorRelativePath(Bucket, FilesystemObjectLayout.KeyHash(key)));
    private string DataPath(string key, string generation) => Path.Combine(_temp, FilesystemObjectLayout.DataRelativePath(Bucket, FilesystemObjectLayout.KeyHash(key), generation));
    private string QuarantineDir => Path.Combine(_temp, Bucket, FilesystemObjectReconciler.QuarantineDirectoryName);
    private FilesystemReconciliationReport Run(FilesystemReconciliationOptions? options = null)
        => _reconciler.Reconcile(Bucket, options ?? new FilesystemReconciliationOptions(), None);

    /// <summary>Set a file's mtime relative to the fake clock so grace ages are deterministic.</summary>
    private void Age(string path, TimeSpan age) => File.SetLastWriteTimeUtc(path, (_clock.GetUtcNow() - age).UtcDateTime);

    [TestMethod]
    public async Task CleanBucketReportsLiveObjectsAndNothingElse()
    {
        await _store.PutAsync(Bucket, "a", Bytes("x"), 1, "text/plain", None);
        await _store.PutAsync(Bucket, "b", Bytes("y"), 1, "text/plain", None);
        var report = Run(new FilesystemReconciliationOptions { VerifyDigests = true });
        Assert.AreEqual(2, report.LiveObjects);
        Assert.AreEqual(2, report.DigestsVerified);
        Assert.AreEqual(0, report.Quarantined);
        Assert.AreEqual(0, report.RetiredCount);
        Assert.AreEqual(0, report.TemporariesRemoved);
        Assert.IsFalse(report.NeedsAttention);
        Assert.IsFalse(report.Truncated);
        Assert.IsFalse(Directory.Exists(QuarantineDir), "a clean pass creates no quarantine directory");
    }

    [TestMethod]
    public async Task RetiredGenerationIsKeptWithinGraceThenReclaimed()
    {
        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        var g1 = (await _store.StatAsync(Bucket, "k", None)).Generation;
        await _store.PutAsync(Bucket, "k", Bytes("v2"), 2, "text/plain", None);
        Assert.IsTrue(File.Exists(DataPath("k", g1)));

        var young = Run();
        Assert.AreEqual(1, young.RetiredCount);
        Assert.AreEqual(1, young.RetiredWithinGrace);
        Assert.AreEqual(2, young.RetiredBytes);
        Assert.AreEqual(0, young.ReclaimedCount);
        Assert.IsTrue(File.Exists(DataPath("k", g1)), "within grace, an open reader may still hold it");

        Age(DataPath("k", g1), TimeSpan.FromMinutes(16));
        var old = Run();
        Assert.AreEqual(1, old.ReclaimedCount);
        Assert.AreEqual(2, old.ReclaimedBytes);
        Assert.AreEqual(0, old.RetiredCount);
        Assert.IsFalse(File.Exists(DataPath("k", g1)));
        Assert.AreEqual("v2", await ReadAll("k"), "the live generation is untouched");
    }

    [TestMethod]
    public async Task DeletedObjectDataIsReclaimedAfterGraceAndNothingIsInvented()
    {
        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        var g = (await _store.StatAsync(Bucket, "k", None)).Generation;
        await _store.DeleteAsync(Bucket, "k", None);
        Age(DataPath("k", g), TimeSpan.FromHours(1));
        var report = Run();
        Assert.AreEqual(1, report.ReclaimedCount);
        Assert.AreEqual(0, report.LiveObjects);
        Assert.IsFalse(File.Exists(DescriptorPath("k")), "reconciliation never writes a descriptor");
        var gone = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.StatAsync(Bucket, "k", None));
        Assert.AreEqual(ObjectStoreFailureKind.MissingObject, gone.Kind);
    }

    [TestMethod]
    public async Task OpenReaderIsNotBrokenByReclamationOnPosix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("POSIX unlink semantics; Windows defers instead (tested separately by the deferred counter).");
        }
        var payload = new string('r', 200_000);
        await _store.PutAsync(Bucket, "k", Bytes(payload), payload.Length, "text/plain", None);
        var g1 = (await _store.StatAsync(Bucket, "k", None)).Generation;
        var readBack = new StringBuilder();
        await _store.ReadAsync(Bucket, "k", null, async (stream, ct) =>
        {
            var buffer = new byte[64 * 1024];
            var first = await stream.ReadAsync(buffer, ct);
            readBack.Append(Encoding.UTF8.GetString(buffer, 0, first));
            // Replace, age the retired file past grace, reclaim: all while the reader holds it.
            await _store.PutAsync(Bucket, "k", Bytes("new"), 3, "text/plain", ct);
            Age(DataPath("k", g1), TimeSpan.FromHours(1));
            var report = Run();
            Assert.AreEqual(1, report.ReclaimedCount, "unlinked while open");
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                readBack.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }, None);
        Assert.AreEqual(payload, readBack.ToString(), "the reader finishes the generation it opened");
    }

    [TestMethod]
    public async Task CrashedTemporariesAreRemovedAfterGraceOnly()
    {
        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        var fresh = Path.Combine(KeyDir("k"), "crash-fresh." + Guid.NewGuid().ToString("N") + ".tmp");
        var stale = Path.Combine(KeyDir("k"), "crash-stale." + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllBytesAsync(fresh, new byte[10]);
        await File.WriteAllBytesAsync(stale, new byte[10]);
        Age(stale, TimeSpan.FromHours(1));
        var report = Run();
        Assert.AreEqual(1, report.TemporariesRemoved);
        Assert.AreEqual(1, report.TemporariesWithinGrace);
        Assert.IsTrue(File.Exists(fresh), "an in-flight write's temporary is left alone");
        Assert.IsFalse(File.Exists(stale));
    }

    [TestMethod]
    public async Task DescriptorWithoutDataIsQuarantinedAndTheKeyBecomesMissing()
    {
        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        var g = (await _store.StatAsync(Bucket, "k", None)).Generation;
        File.Delete(DataPath("k", g));
        // Stat answers from the descriptor alone and still succeeds; the contradiction surfaces
        // on read, where the bytes are needed. That is the state reconciliation must resolve.
        var before = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.ReadAsync(Bucket, "k", null, (_, _) => Task.CompletedTask, None));
        Assert.AreEqual(ObjectStoreFailureKind.CorruptState, before.Kind, "before reconciliation the store refuses loudly");

        var report = Run();
        Assert.AreEqual(1, report.Quarantined);
        Assert.AreEqual(1, report.QuarantineReasons["descriptor-without-data"]);
        Assert.IsTrue(report.NeedsAttention);
        Assert.IsFalse(File.Exists(DescriptorPath("k")));
        Assert.HasCount(1, Directory.GetFiles(QuarantineDir));

        var after = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.StatAsync(Bucket, "k", None));
        Assert.AreEqual(ObjectStoreFailureKind.MissingObject, after.Kind, "after quarantine the key is simply absent; nothing was invented");
    }

    [TestMethod]
    public async Task DigestMismatchQuarantinesThePairTogether()
    {
        await _store.PutAsync(Bucket, "k", Bytes("payload"), 7, "text/plain", None);
        var g = (await _store.StatAsync(Bucket, "k", None)).Generation;
        await File.WriteAllTextAsync(DataPath("k", g), "PAYLOAD");
        var report = Run(new FilesystemReconciliationOptions { VerifyDigests = true });
        Assert.AreEqual(2, report.Quarantined);
        Assert.AreEqual(2, report.QuarantineReasons["data-digest-mismatch"]);
        Assert.AreEqual(0, report.LiveObjects);
        Assert.IsFalse(File.Exists(DescriptorPath("k")));
        Assert.IsFalse(File.Exists(DataPath("k", g)));
        Assert.HasCount(2, Directory.GetFiles(QuarantineDir));
    }

    [TestMethod]
    public async Task MalformedAndMisnamedDescriptorsAreQuarantined()
    {
        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        await File.WriteAllTextAsync(DescriptorPath("k"), "{not json");
        // A descriptor whose file name is not the hash of the key it names: tampering or a
        // misplaced file. Its key would resolve to a different physical name.
        var misnamed = Path.Combine(KeyDir("k"), new string('0', 64) + FilesystemObjectLayout.DescriptorSuffix);
        await File.WriteAllTextAsync(misnamed, "{\"schema\":\"hvo-fs-object-v1\",\"key\":\"other\",\"contentType\":\"t\",\"length\":0,\"sha256\":\"" + new string('a', 64) + "\",\"generation\":\"" + new string('b', 32) + "\",\"modifiedUtc\":\"2026-01-01T00:00:00Z\"}");
        var report = Run();
        Assert.AreEqual(2, report.QuarantineReasons["malformed-descriptor"]);
    }

    [TestMethod]
    public async Task QuarantinedFilesAreInvisibleToListingAndToTheNextPass()
    {
        await _store.PutAsync(Bucket, "good", Bytes("x"), 1, "text/plain", None);
        await _store.PutAsync(Bucket, "bad", Bytes("x"), 1, "text/plain", None);
        await File.WriteAllTextAsync(DescriptorPath("bad"), "{not json");
        Run();
        var listed = new List<string>();
        await foreach (var item in _store.ListAsync(Bucket, "", None))
        {
            listed.Add(item.Key);
        }
        CollectionAssert.AreEqual(new[] { "good" }, listed);
        var second = Run();
        Assert.AreEqual(0, second.Quarantined, "quarantine is not re-examined");
        Assert.AreEqual(1, second.LiveObjects);
    }

    [TestMethod]
    public async Task PassIsBoundedAndReportsTruncation()
    {
        for (var i = 0; i < 20; i++)
        {
            await _store.PutAsync(Bucket, $"k{i:D2}", Bytes("x"), 1, "text/plain", None);
        }
        var report = Run(new FilesystemReconciliationOptions { MaximumEntriesPerPass = 5 });
        Assert.IsTrue(report.Truncated);
        Assert.IsTrue(report.LiveObjects <= 5);
    }

    [TestMethod]
    public async Task CopyWhoseSourceDataWasReclaimedIsPreconditionWhenTheDescriptorMovedOn()
    {
        // Deferred finding F1 from #917, made deterministic: the copy reads a descriptor naming
        // g1, whose data has been reclaimed, while the current descriptor names g2. That is
        // exactly the state a copy sees when it loses the race to replace-then-reclaim. The
        // store must classify it as Precondition (the source moved), not CorruptState.
        await _store.PutAsync(Bucket, "src", Bytes("v1"), 2, "text/plain", None);
        var g1 = (await _store.StatAsync(Bucket, "src", None)).Generation;
        await _store.PutAsync(Bucket, "src", Bytes("v2"), 2, "text/plain", None);
        Age(DataPath("src", g1), TimeSpan.FromHours(1));
        Run();
        Assert.IsFalse(File.Exists(DataPath("src", g1)), "g1 reclaimed");

        // Present the stale view to the copy by handing it a descriptor that names g1, then
        // let its re-read see the real (g2) descriptor.
        var stale = await File.ReadAllTextAsync(DescriptorPath("src"));
        var g2 = (await _store.StatAsync(Bucket, "src", None)).Generation;
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => _store.CopyFromDescriptorForTestAsync(Bucket, "src", "dst",
                System.Text.Json.JsonSerializer.Deserialize<FilesystemObjectDescriptor>(stale.Replace(g2, g1, StringComparison.Ordinal), FilesystemObjectLayout.DescriptorJson)!, None));
        Assert.AreEqual(ObjectStoreFailureKind.Precondition, fault.Kind);
        Assert.IsFalse(File.Exists(DescriptorPath("dst")), "a failed copy commits nothing");

        // And when the descriptor still names the missing generation, it is the store contradicting itself.
        await File.WriteAllTextAsync(DescriptorPath("src"), stale.Replace(g2, g1, StringComparison.Ordinal));
        var corrupt = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.CopyAsync(Bucket, "src", "dst", None));
        Assert.AreEqual(ObjectStoreFailureKind.CorruptState, corrupt.Kind);
    }

    [TestMethod]
    public void MissingBucketIsMissingBucket()
    {
        var fault = Assert.ThrowsExactly<ObjectStoreException>(() => _reconciler.Reconcile("absent", new FilesystemReconciliationOptions(), None));
        Assert.AreEqual(ObjectStoreFailureKind.MissingBucket, fault.Kind);
    }

    [TestMethod]
    public async Task ReconciliationNeverFollowsASymbolicLink()
    {
        var outside = Path.Combine(Path.GetTempPath(), "hvo-fsrc-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "victim." + new string('c', 32) + FilesystemObjectLayout.DataSuffix);
        await File.WriteAllBytesAsync(victim, new byte[3]);
        try
        {
            await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
            // A link inside the bucket pointing at an outside file that looks like retired data.
            var link = Path.Combine(KeyDir("k"), new string('d', 64) + "." + new string('c', 32) + FilesystemObjectLayout.DataSuffix);
            File.CreateSymbolicLink(link, victim);
            Age(victim, TimeSpan.FromHours(1));
            var report = Run();
            Assert.IsTrue(File.Exists(victim), "nothing outside the root is touched");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    public async Task HealthCheckReportsReconciliationFactsAndDegradesOnQuarantine()
    {
        var options = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        options.Filesystem.Root = _temp;
        var wrapped = Options.Create(options);
        using var worker = new FilesystemObjectReconciliationWorker(_store, wrapped, _clock, NullLoggerFactory.Instance);
        var check = new ObjectStoreHealthCheck(_store, wrapped, NullLogger<ObjectStoreHealthCheck>.Instance, worker);

        var beforeAnyPass = await check.CheckHealthAsync(new HealthCheckContext(), None);
        Assert.AreEqual(HealthStatus.Healthy, beforeAnyPass.Status);
        Assert.AreEqual("both-required-buckets-authenticated", beforeAnyPass.Data["Reason"]);

        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        worker.RunOnce(_reconciler, None);
        var clean = await check.CheckHealthAsync(new HealthCheckContext(), None);
        Assert.AreEqual(HealthStatus.Healthy, clean.Status);
        Assert.AreEqual("both-required-buckets-reconciled", clean.Data["Reason"]);
        Assert.AreEqual(0, clean.Data["QuarantinedCount"]);
        Assert.IsTrue(clean.Data.ContainsKey("ReconciledUtc"));

        var g = (await _store.StatAsync(Bucket, "k", None)).Generation;
        File.Delete(DataPath("k", g));
        worker.RunOnce(_reconciler, None);
        var degraded = await check.CheckHealthAsync(new HealthCheckContext(), None);
        Assert.AreEqual(HealthStatus.Degraded, degraded.Status);
        Assert.AreEqual("reconciliation-attention", degraded.Data["Reason"]);
        Assert.AreEqual(1, degraded.Data["QuarantinedCount"]);
    }

    private async Task<string> ReadAll(string key)
    {
        var sb = new StringBuilder();
        await _store.ReadAsync(Bucket, key, null, async (stream, ct) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            sb.Append(await reader.ReadToEndAsync(ct));
        }, None);
        return sb.ToString();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
