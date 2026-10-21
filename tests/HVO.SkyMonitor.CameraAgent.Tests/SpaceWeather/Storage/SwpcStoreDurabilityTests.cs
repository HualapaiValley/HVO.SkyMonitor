using System.Diagnostics;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcStoreDurabilityTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swpc-durable-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    public TestContext TestContext { get; set; } = null!;
    private string Outside => _root + "-outside";
    private SqliteSwpcStore Open() => new(SwpcStoreTests.Settings(_root), NullLogger<SqliteSwpcStore>.Instance);
    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(Outside)) Directory.Delete(Outside, true);
    }

    [TestMethod]
    [DataRow("space-weather.db")]
    [DataRow("space-weather.db-wal")]
    [DataRow("space-weather.db-shm")]
    [DataRow("space-weather.db-journal")]
    [DataRow("space-weather.lock")]
    public async Task DatabaseAndOwnershipSidecarsCannotFollowLinksOutsideTheRoot(string name)
    {
        Directory.CreateDirectory(_root);
        var outside = Outside;
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "sentinel");
        await File.WriteAllTextAsync(target, "untouched").ConfigureAwait(false);
        File.CreateSymbolicLink(Path.Combine(_root, name), target);
        using var store = Open();
        await Assert.ThrowsExactlyAsync<SwpcStorageException>(() => store.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.AreEqual("untouched", await File.ReadAllTextAsync(target).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RelativeRootIsRejectedAndDisposedOwnerCannotReacquireTheLock()
    {
        Assert.ThrowsExactly<ArgumentException>(() => { using var invalid = new SqliteSwpcStore(SwpcStoreTests.Settings("relative"), NullLogger<SqliteSwpcStore>.Instance); });
        using var store = Open();
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        store.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => store.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        using var replacement = Open();
        await replacement.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RepresentativeDurableWorkloadRetainsExactProvenanceAcrossRestart()
    {
        const int count = 64;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        using (var store = Open())
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            TestContext.WriteLine("InitializationMs=" + clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
            long revision = 0;
            for (var index = 0; index < count; index++)
            {
                var at = Now.AddSeconds(index);
                var snapshot = SwpcStoreTests.Alerts(SwpcStoreTests.Message(index + 1), at);
                var start = new SwpcAttemptStart(Guid.NewGuid(), revision, SwpcProduct.Alerts, "representative",
                    SwpcAttemptTrigger.Scheduled, at, new(null, null), SwpcStoreTests.Empty);
                var begun = await store.BeginAttemptAsync(start, CancellationToken.None).ConfigureAwait(false);
                var saved = await store.CompleteAttemptAsync(new(start.AttemptId, begun.View.Revision, at, at, at,
                    SwpcFetchOutcome.Updated, 200, "updated", snapshot.DecodedByteCount, SwpcValidatorOutcome.Replaced, snapshot),
                    _ => new(SwpcStoreTests.Empty, null, null, []), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(SwpcWriteStatus.Applied, saved.Status);
                revision = saved.View.Revision;
            }
        }
        clock.Stop();
        var writeAllocations = GC.GetTotalAllocatedBytes(true) - allocated;
        using var reopened = Open();
        var view = await reopened.ReadAsync(Now.AddSeconds(count), CancellationToken.None).ConfigureAwait(false);
        var health = await reopened.ReadHealthAsync(Now.AddSeconds(count), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(count, view.Notices.Versions.Length);
        Assert.AreEqual((long)count, health.ReasonCounts["updated"]);
        Assert.AreEqual((long)count, health.AttemptCount); Assert.AreEqual(0, health.IncompleteAttempts);
        Assert.AreEqual(1L, health.SnapshotCount);
        CollectionAssert.AreEqual(Enumerable.Range(1, count).Select(i => (long)i).ToArray(), view.Notices.Versions.Select(v => v.Version.IngestSequence).ToArray());
        Assert.AreEqual(view.Products[SwpcProduct.Alerts].LastKnownGood!.Sha256, view.Notices.Versions[^1].IntroducingSnapshotSha256);
        using var process = Process.GetCurrentProcess();
        TestContext.WriteLine(FormattableString.Invariant($"DurableAttempts={count}; ElapsedMs={clock.Elapsed.TotalMilliseconds:F3}; AllocatedBytes={writeAllocations}; PeakWorkingSetBytes={process.PeakWorkingSet64}; DatabaseBytes={new FileInfo(Path.Combine(_root, "space-weather.db")).Length}; RawBytes={health.RawBodyBytes}"));
    }
}
