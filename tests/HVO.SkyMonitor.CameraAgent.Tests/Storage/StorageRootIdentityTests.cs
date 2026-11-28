using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;

namespace HVO.SkyMonitor.CameraAgent.Tests.Storage;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Storage")]
public sealed class StorageRootIdentityTests
{
    [TestMethod]
    public void CanonicalPathsPreserveDirectoryCaseWhileKeysUsePlatformComparison()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MiXeD-Root"));
        var alias = Path.Combine(root, ".") + Path.DirectorySeparatorChar;
        Assert.AreEqual(root, StorageRootIdentity.CanonicalPath(alias));
        Assert.AreEqual(OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root, StorageRootIdentity.Key(alias));
        var volumeRoot = Path.GetPathRoot(root)!;
        Assert.AreEqual(volumeRoot, StorageRootIdentity.CanonicalPath(volumeRoot));
    }

    [TestMethod]
    [DataRow("trailing")]
    [DataRow("dot")]
    [DataRow("parent")]
    [DataRow("relative")]
    [DataRow("repeated")]
    public void FenceEquivalentSpellingsShareEveryRootGateAndPressureEntry(string spelling)
    {
        var root = Path.Combine(Path.GetTempPath(), "root-identity", Guid.NewGuid().ToString("N"));
        var alias = Alias(root, spelling);
        Assert.AreSame(StorageLifecycleLock.ForRoot(root), StorageLifecycleLock.ForRoot(alias));
        Assert.AreSame(RawIngressLifecycleLock.ForRoot(root), RawIngressLifecycleLock.ForRoot(alias));
        var pressure = new StoragePressureState();
        var first = new StoragePressureSnapshot(alias, new(100, 20), false, 30, DateTimeOffset.UnixEpoch);
        pressure.Set(first);
        Assert.AreSame(first, pressure.Get(root));
        var latest = first with { StorageRoot = root, IsUnderPressure = true };
        pressure.Set(latest);
        Assert.AreSame(latest, pressure.Get(alias));
        Assert.HasCount(1, pressure.Snapshots);

        var outbox = new ArtifactOutboxState();
        outbox.ReportUnavailable(alias, "unavailable");
        outbox.Update(root, new(1, 1, DateTimeOffset.UnixEpoch, 1, 0, 0, 0, 0, 0));
        Assert.AreEqual(ArtifactOutboxAvailability.Healthy, outbox.Snapshot.Availability);
        outbox.Update(alias, new(0, 0, null, 0, 0, 0, 1, 0, 0));
        Assert.HasCount(1, outbox.Roots);
        Assert.AreEqual(0, outbox.Snapshot.PendingCount);
    }

    [TestMethod]
    public void DistinctRootsKeepSeparateGatesAndPressureEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "separate-roots", Guid.NewGuid().ToString("N"));
        var other = Path.Combine(root, "other");
        Assert.AreNotSame(StorageLifecycleLock.ForRoot(root), StorageLifecycleLock.ForRoot(other));
        Assert.AreNotSame(RawIngressLifecycleLock.ForRoot(root), RawIngressLifecycleLock.ForRoot(other));
        var state = new StoragePressureState();
        state.Set(new(root, new(100, 20), false, 30, DateTimeOffset.UnixEpoch));
        Assert.IsNull(state.Get(other));
        state.Set(new(other, new(100, 10), true, 1, DateTimeOffset.UnixEpoch));
        Assert.HasCount(2, state.Snapshots);
    }

    private static string Alias(string root, string spelling) => spelling switch
    {
        "trailing" => root + Path.DirectorySeparatorChar,
        "dot" => Path.Combine(root, ".") + Path.DirectorySeparatorChar,
        "parent" => Path.Combine(root, "child", ".."),
        "relative" => Path.GetRelativePath(Environment.CurrentDirectory, root),
        "repeated" => root + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar,
        _ => throw new ArgumentOutOfRangeException(nameof(spelling))
    };
}
