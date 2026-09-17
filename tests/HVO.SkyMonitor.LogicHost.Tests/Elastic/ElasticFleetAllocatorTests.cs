using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Elastic;

namespace HVO.SkyMonitor.LogicHost.Tests.Elastic;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ElasticFleetAllocatorTests
{
    [TestMethod]
    public void AugmentingPathPreservesMaximumCardinality()
    {
        var result = Allocate(
            [Job("a-1", "A", 3), Job("b", "B", 2), Job("a-2", "A", 1)],
            [Runner("a-only", ["A"]), Runner("flex", ["A", "B"]), Runner("b-only", ["B"])]);

        Assert.AreEqual(3, result.MatchedJobIds.Count);
        Assert.AreEqual(0, result.UnmatchedJobIds.Count);
    }

    [TestMethod]
    public void OccupiedSlotsCannotCoverQueuedWork()
    {
        var jobs = Enumerable.Range(0, 9).Select(index => Job($"b-{index}", "B", index)).ToArray();
        var result = Allocate(jobs, [Runner("busy", ["B"], concurrency: 10, occupied: 10)]);

        Assert.AreEqual(0, result.AvailableSlots);
        Assert.AreEqual(9, result.UnmatchedJobIds.Count);
    }

    [TestMethod]
    public void TransferLimitUsesReassignmentRatherThanStrandingLargeWork()
    {
        var small = Job("small", "A", 2, inputBytes: 8);
        var large = Job("large", "A", 1, inputBytes: 64);
        var result = Allocate(
            [small, large],
            [Runner("small-only", ["A"], transfer: 16), Runner("large-capable", ["A"], transfer: 128)]);

        Assert.AreEqual(2, result.MatchedJobIds.Count);
        Assert.AreEqual(0, result.UnmatchedJobIds.Count);
    }

    [TestMethod]
    public void PriorityAndUnmatchedIdentityAreRegistrationOrderInvariant()
    {
        var oldA = Job("old-a", "A", 3);
        var middleB = Job("middle-b", "B", 2);
        var youngA = Job("young-a", "A", 1);
        var jobs = new[] { oldA, middleB, youngA };
        var aOnly = Runner("a-only", ["A"]);
        var flex = Runner("flex", ["A", "B"]);

        var forward = Allocate(jobs, [aOnly, flex]);
        var reverse = Allocate(jobs, [flex, aOnly]);

        CollectionAssert.AreEqual(new[] { youngA.Id }, forward.UnmatchedJobIds.ToArray());
        CollectionAssert.AreEqual(forward.MatchedJobIds.ToArray(), reverse.MatchedJobIds.ToArray());
        CollectionAssert.AreEqual(forward.UnmatchedJobIds.ToArray(), reverse.UnmatchedJobIds.ToArray());
        Assert.AreEqual(forward.CompatibilityChecks, reverse.CompatibilityChecks);
    }

    [TestMethod]
    public void CandidateBoundsFailInsteadOfTruncatingShortfall()
    {
        var excessJobs = Enumerable.Range(0, ElasticFleetAllocator.MaximumJobs + 1)
            .Select(index => Job($"job-{index}", "A", index))
            .ToArray();
        var jobsFailure = Assert.ThrowsExactly<InvalidOperationException>(() => Allocate(excessJobs, []));
        StringAssert.Contains(jobsFailure.Message, "jobs exceeds");

        var slotsFailure = Assert.ThrowsExactly<InvalidOperationException>(() => Allocate(
            [], [Runner("too-many-slots", ["A"], concurrency: ElasticFleetAllocator.MaximumSlots + 1)]));
        StringAssert.Contains(slotsFailure.Message, "slots exceeds");
    }

    [TestMethod]
    public void ClaimableProjectionBoundsRowsBeforeMaterialization()
    {
        var sql = CentralDerivativeJobService.CreateClaimableSql();

        StringAssert.Contains(sql, "SELECT TOP (@candidateLimit)");
        StringAssert.Contains(sql, "ORDER BY job.[Id]");
    }

    private static ElasticFleetAllocator.Result Allocate(
        IReadOnlyList<ElasticFleetAllocator.Job> jobs,
        IReadOnlyList<ElasticFleetAllocator.Registration> registrations)
        => ElasticFleetAllocator.Allocate(jobs, registrations);

    private static ElasticFleetAllocator.Job Job(string id, string recipe, int ageMinutes, long inputBytes = 1)
        => new(StableGuid(id), recipe, inputBytes, DateTimeOffset.UnixEpoch.AddMinutes(-ageMinutes));

    private static ElasticFleetAllocator.Registration Runner(
        string id,
        IReadOnlyCollection<string> recipes,
        long transfer = 1024,
        int concurrency = 1,
        int occupied = 0)
        => new(id, recipes.ToHashSet(StringComparer.Ordinal), transfer, concurrency, occupied);

    private static Guid StableGuid(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
