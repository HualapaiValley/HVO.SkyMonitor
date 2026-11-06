using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcSnapshotComparisonTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void NoLastKnownGoodIsUpdated()
        => Assert.AreEqual(SwpcFetchOutcome.Updated, SwpcSnapshotComparison.Classify(null, Snapshot("new", null)));

    [TestMethod]
    [DataRow(true, -1, 0, SwpcFetchOutcome.Unchanged)]
    [DataRow(true, 1, 0, SwpcFetchOutcome.Unchanged)]
    [DataRow(true, 0, 0, SwpcFetchOutcome.Unchanged)]
    [DataRow(true, null, null, SwpcFetchOutcome.Unchanged)]
    [DataRow(false, -1, 0, SwpcFetchOutcome.StaleSnapshot)]
    [DataRow(false, 1, 0, SwpcFetchOutcome.Updated)]
    [DataRow(false, 0, 0, SwpcFetchOutcome.Updated)]
    [DataRow(false, null, 0, SwpcFetchOutcome.Updated)]
    [DataRow(false, 0, null, SwpcFetchOutcome.Updated)]
    [DataRow(false, null, null, SwpcFetchOutcome.Updated)]
    public void HashEqualityPrecedesStrictlyOlderNonNullStamps(
        bool sameHash, int? candidateHours, int? lkgHours, SwpcFetchOutcome expected)
    {
        var lkg = Snapshot("old", lkgHours is { } oldHours ? Stamp.AddHours(oldHours) : null);
        var candidate = Snapshot(sameHash ? "old" : "new",
            candidateHours is { } newHours ? Stamp.AddHours(newHours) : null);

        Assert.AreEqual(expected, SwpcSnapshotComparison.Classify(lkg, candidate));
    }

    [TestMethod]
    public void EqualInstantsWithDifferentOffsetsAreNotStale()
        => Assert.AreEqual(SwpcFetchOutcome.Updated, SwpcSnapshotComparison.Classify(
            Snapshot("old", Stamp), Snapshot("new", Stamp.ToOffset(TimeSpan.FromHours(2)))));

    [TestMethod]
    public void NullCandidateIsRejected()
        => Assert.ThrowsExactly<ArgumentNullException>(() => SwpcSnapshotComparison.Classify(null, null!));

    private static SwpcSnapshot Snapshot(string hash, DateTimeOffset? stamp)
        => new(SwpcProduct.Alerts, "/products/alerts.json", Stamp, Stamp, 200,
            "application/json", 0, hash, stamp, new SwpcValidators(null, null), [],
            new SwpcAlertsBody(new SwpcBodyResult<SwpcMessageRecord>([], [], null, hash, stamp, 0)));
}
