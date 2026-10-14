using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricClockFactsMapperTests
{
    private static readonly DateTimeOffset Midpoint = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);

    [TestMethod]
    public void SynchronizedKernel_ReportsItsOwnErrorBoundsInSeconds()
    {
        var facts = AstrometricClockFactsMapper.FromSnapshot(Snapshot(Midpoint.AddMinutes(-1),
            new(KernelClockStatus.Synchronized, TimeSpan.FromMilliseconds(16), TimeSpan.FromMicroseconds(250))), Midpoint, MaximumAge);
        Assert.AreEqual(new AstrometricClockFacts("kernel-adjtimex", AstrometricClockSynchronization.Synchronized, .016, .00025), facts);
        facts.Validate();
    }

    [TestMethod]
    public void UnsynchronizedUnknownMissingAndStaleStates_NeverCarryABound()
    {
        Assert.AreEqual(AstrometricClockSynchronization.Unsynchronized, AstrometricClockFactsMapper.FromKernel(new(KernelClockStatus.Unsynchronized, TimeSpan.FromSeconds(16), null)).Synchronization);
        Assert.IsNull(AstrometricClockFactsMapper.FromKernel(new(KernelClockStatus.Unsynchronized, TimeSpan.FromSeconds(16), null)).MaximumErrorSeconds);
        Assert.AreEqual(AstrometricClockSynchronization.Unknown, AstrometricClockFactsMapper.FromKernel(KernelClockState.Unknown).Synchronization);
        Assert.AreEqual(AstrometricClockFacts.NotSupplied, AstrometricClockFactsMapper.FromSnapshot(null, Midpoint, MaximumAge));
        var synchronized = new KernelClockState(KernelClockStatus.Synchronized, TimeSpan.FromMilliseconds(16), null);
        Assert.AreEqual(AstrometricClockSynchronization.Unknown, AstrometricClockFactsMapper.FromSnapshot(Snapshot(Midpoint.AddMinutes(-6), synchronized), Midpoint, MaximumAge).Synchronization);
        Assert.AreEqual(AstrometricClockSynchronization.Unknown, AstrometricClockFactsMapper.FromSnapshot(Snapshot(Midpoint.AddSeconds(1), synchronized), Midpoint, MaximumAge).Synchronization);
        Assert.Throws<ArgumentOutOfRangeException>(() => AstrometricClockFactsMapper.FromSnapshot(null, Midpoint, TimeSpan.Zero));
    }

    [TestMethod]
    public void SntpOffset_IsNeverUsedAsAClockBound()
    {
        // A small selected server offset exists, but an unsynchronized kernel still yields no bound.
        var answer = new TimeServerResult("a.example", null, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(10), 1);
        var snapshot = Snapshot(Midpoint.AddMinutes(-1), new(KernelClockStatus.Unsynchronized, null, null)) with { Selected = answer, Servers = [answer] };
        var facts = AstrometricClockFactsMapper.FromSnapshot(snapshot, Midpoint, MaximumAge);
        Assert.AreEqual(AstrometricClockSynchronization.Unsynchronized, facts.Synchronization);
        Assert.IsNull(facts.MaximumErrorSeconds); Assert.IsNull(facts.EstimatedErrorSeconds);
        // A synchronized kernel reports its own bound, whatever the server offset.
        var bounded = AstrometricClockFactsMapper.FromSnapshot(snapshot with { Kernel = new(KernelClockStatus.Synchronized, TimeSpan.FromMilliseconds(40), null) }, Midpoint, MaximumAge);
        Assert.AreEqual(.04, bounded.MaximumErrorSeconds!.Value, 1e-15);
    }

    private static ClockSyncSnapshot Snapshot(DateTimeOffset measured, KernelClockState kernel) => new(measured, true, null, [], 0, kernel);
}
