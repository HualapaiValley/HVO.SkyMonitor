using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class ObservingDayCalendarProviderTests
{
    [TestMethod]
    public void Current_FallsBackToUtcUntilTheLocationInitializesThenFollowsIt()
    {
        var store = new Mock<IDeploymentLocationStore>();
        DeploymentLocationSnapshot? active = null;
        store.SetupGet(static location => location.Active).Returns(() => active);
        var provider = new DeploymentObservingDayCalendarProvider(store.Object);

        var before = provider.Current;
        Assert.IsTrue(before.TimeZoneFallback);
        Assert.AreSame(before, provider.Current);

        active = Snapshot("America/Phoenix");
        var after = provider.Current;
        Assert.IsFalse(after.TimeZoneFallback);
        Assert.AreEqual("America/Phoenix", after.TimeZoneId);
        Assert.AreSame(after, provider.Current);

        active = Snapshot("America/Denver");
        Assert.AreEqual("America/Denver", provider.Current.TimeZoneId);
    }

    [TestMethod]
    public void Current_CachesAnUnresolvableIdentifierInsteadOfRetryingEveryRead()
    {
        var store = new Mock<IDeploymentLocationStore>();
        store.SetupGet(static location => location.Active).Returns(Snapshot("Mars/Olympus_Mons"));
        var provider = new DeploymentObservingDayCalendarProvider(store.Object);

        var first = provider.Current;
        Assert.IsTrue(first.TimeZoneFallback);
        Assert.AreSame(first, provider.Current);
        Assert.AreSame(first, provider.Current);
    }

    [TestMethod]
    public void Current_WithoutAStoreIsAStableUtcFallback()
    {
        var provider = new DeploymentObservingDayCalendarProvider();
        var first = provider.Current;
        var second = provider.Current;
        Assert.IsTrue(first.TimeZoneFallback);
        Assert.AreSame(first, second);
    }

    private static DeploymentLocationSnapshot Snapshot(string timeZoneId) => new(
        "location-1", 1, new string('A', 64), "test", null,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null,
        33.5, -112.0, 300, timeZoneId);
}
