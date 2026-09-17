using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.LogicHost.Services.Elastic;

namespace HVO.SkyMonitor.LogicHost.Tests.Elastic;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ElasticProvisionableShortfallSelectorTests
{
    [TestMethod]
    public void ExhaustedObservatoryCannotConsumeAnotherObservatoryHeadroom()
    {
        var now = DateTimeOffset.UtcNow;
        var exhausted = Guid.NewGuid();
        var eligible = Guid.NewGuid();
        var blockedJob = Job(exhausted, now.AddMinutes(-2));
        var eligibleJob = Job(eligible, now.AddMinutes(-1));

        var result = ElasticProvisionableShortfallSelector.Select(
            [blockedJob, eligibleJob],
            new Dictionary<Guid, int> { [exhausted] = 0, [eligible] = 1 },
            now);

        CollectionAssert.AreEqual(new[] { eligibleJob.Id }, result.JobIds.ToArray());
        Assert.AreEqual(TimeSpan.FromMinutes(1), result.OldestAge);
    }

    [TestMethod]
    public void OldMatchedWorkCannotSetTheUnmatchedDeadlineAge()
    {
        var now = DateTimeOffset.UtcNow;
        var youngUnmatched = Job(Guid.NewGuid(), now.AddSeconds(-10));

        var result = ElasticProvisionableShortfallSelector.Select([youngUnmatched], null, now);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(10), result.OldestAge,
            "the selector sees only unmatched identities; an older matched job never enters the policy fact");
    }

    [TestMethod]
    public void PartialHeadroomRetainsOldestStableJobsPerObservatory()
    {
        var now = DateTimeOffset.UtcNow;
        var observatory = Guid.NewGuid();
        var old = Job(observatory, now.AddMinutes(-3));
        var middle = Job(observatory, now.AddMinutes(-2));
        var young = Job(observatory, now.AddMinutes(-1));

        var result = ElasticProvisionableShortfallSelector.Select(
            [young, old, middle],
            new Dictionary<Guid, int> { [observatory] = 2 },
            now);

        CollectionAssert.AreEqual(new[] { old.Id, middle.Id }, result.JobIds.ToArray());
        Assert.AreEqual(TimeSpan.FromMinutes(3), result.OldestAge);
    }

    private static ElasticProvisionableShortfallSelector.Job Job(Guid observatoryId, DateTimeOffset availableSince)
        => new(Guid.NewGuid(), observatoryId, availableSince);
}
