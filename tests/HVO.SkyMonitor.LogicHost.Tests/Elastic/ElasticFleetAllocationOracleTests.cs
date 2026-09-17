using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.LogicHost.Tests.Elastic;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ElasticFleetAllocationOracleTests
{
    [TestMethod]
    public void ConstrainedRecipeIsNotStrandedBehindFlexibleCapacity()
    {
        var result = Allocate(
            [Job("a-1", "A", age: TimeSpan.FromMinutes(3)), Job("b", "B", age: TimeSpan.FromMinutes(2)), Job("a-2", "A", age: TimeSpan.FromMinutes(1))],
            [Runner("a-only", ["A"]), Runner("flex", ["A", "B"]), Runner("b-only", ["B"])]);

        CollectionAssert.AreEquivalent(new[] { "a-1", "a-2", "b" }, result.MatchedJobs.ToArray());
        Assert.AreEqual(0, result.UncoveredJobs.Count);
    }

    [TestMethod]
    public void OccupiedSlotsAreUnavailableToQueuedWork()
    {
        var result = Allocate(
            Enumerable.Range(0, 9).Select(index => Job($"b-{index}", "B")).ToArray(),
            [Runner("busy-b", ["B"], concurrency: 10, occupied: 10)]);

        Assert.AreEqual(0, result.MatchedJobs.Count);
        Assert.AreEqual(9, result.UncoveredJobs.Count);
        Assert.AreEqual(9, result.RequiredInstances);
    }

    [TestMethod]
    public void ClassSpecificShortfallUsesRemainingEntitlementHeadroom()
    {
        var jobs = Enumerable.Range(0, 9).Select(index => Job($"a-{index}", "A", scope: "observatory-a"))
            .Concat([Job("blocked-a", "B", scope: "observatory-a", age: TimeSpan.FromMinutes(2)),
                     Job("eligible-b", "B", scope: "observatory-b", age: TimeSpan.FromMinutes(1))])
            .ToArray();
        var result = Allocate(
            jobs,
            [Runner("a-capacity", ["A"], concurrency: 10)],
            entitlement: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["observatory-a"] = 0,
                ["observatory-b"] = 1
            });

        CollectionAssert.AreEqual(new[] { "eligible-b" }, result.ProvisionableJobs.ToArray());
        Assert.AreEqual(1, result.RequiredInstances);
    }

    [TestMethod]
    public void DeadlineAgeComesFromTheWorkThatNeedsProvisioning()
    {
        var result = Allocate(
            [Job("old-a", "A", age: TimeSpan.FromHours(1)), Job("young-b", "B", age: TimeSpan.FromSeconds(10))],
            [Runner("a-only", ["A"])]);

        CollectionAssert.AreEqual(new[] { "young-b" }, result.ProvisionableJobs.ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(10), result.OldestProvisionableAge);
    }

    [TestMethod]
    public void TransferLimitParticipatesInMatchingAndReassignment()
    {
        var result = Allocate(
            [Job("small", "A", input: 8, age: TimeSpan.FromMinutes(2)),
             Job("large", "A", input: 64, age: TimeSpan.FromMinutes(1))],
            [Runner("small-only", ["A"], transfer: 16), Runner("large-capable", ["A"], transfer: 128)]);

        CollectionAssert.AreEquivalent(new[] { "small", "large" }, result.MatchedJobs.ToArray());
        Assert.AreEqual(0, result.UncoveredJobs.Count);
    }

    [TestMethod]
    public void RepresentativeBoundRemainsPolynomialAndDeterministic()
    {
        var recipes = Enumerable.Range(0, 8).Select(index => $"recipe-{index}").ToArray();
        var jobs = Enumerable.Range(0, 64)
            .Select(index => Job($"job-{index:D2}", recipes[index % recipes.Length], input: 1 + index, age: TimeSpan.FromSeconds(index)))
            .ToArray();
        var runners = Enumerable.Range(0, 16)
            .Select(index => Runner(
                $"runner-{index:D2}",
                recipes.Where((_, recipeIndex) => recipeIndex % 4 == index % 4).ToArray(),
                transfer: 128,
                concurrency: 4,
                occupied: index % 2))
            .ToArray();

        var headroom = jobs.Select(job => job.EntitlementScope).Distinct(StringComparer.Ordinal)
            .ToDictionary(scope => scope, _ => 12, StringComparer.Ordinal);
        var first = ElasticFleetAllocationOracle.Allocate(jobs, runners, headroom, templateConcurrency: 4);
        var second = ElasticFleetAllocationOracle.Allocate(jobs, runners, headroom, templateConcurrency: 4);

        CollectionAssert.AreEqual(first.MatchedJobs.ToArray(), second.MatchedJobs.ToArray());
        CollectionAssert.AreEqual(first.UncoveredJobs.ToArray(), second.UncoveredJobs.ToArray());
        CollectionAssert.AreEqual(first.ProvisionableJobs.ToArray(), second.ProvisionableJobs.ToArray());
        Assert.AreEqual(first.OldestProvisionableAge, second.OldestProvisionableAge);
        Assert.AreEqual(first.RequiredInstances, second.RequiredInstances);
        Assert.AreEqual(first.CompatibilityChecks, second.CompatibilityChecks);
        Assert.IsTrue(first.CompatibilityChecks <= jobs.Length * 64 * 64,
            $"augmenting-path checks remain bounded for the representative fleet; observed {first.CompatibilityChecks}");
    }

    [TestMethod]
    public void AmbiguousOrInvalidModelInputsAreRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Allocate(
            [Job("duplicate", "A"), Job("duplicate", "B")],
            [Runner("runner", ["A", "B"])]));
        Assert.ThrowsExactly<ArgumentException>(() => Allocate(
            [Job("job", "A")],
            [Runner("duplicate", ["A"]), Runner("duplicate", ["A"])]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ElasticFleetAllocationOracle.Allocate(
            [Job("job", "A")],
            [Runner("runner", ["A"])],
            remainingEntitlementConcurrency: new Dictionary<string, int> { ["observatory"] = -1 },
            templateConcurrency: 1));
    }

    private static ElasticFleetAllocationOracle.Result Allocate(
        IReadOnlyList<ElasticFleetAllocationOracle.Job> jobs,
        IReadOnlyList<ElasticFleetAllocationOracle.Registration> runners,
        IReadOnlyDictionary<string, int>? entitlement = null)
        => ElasticFleetAllocationOracle.Allocate(jobs, runners, entitlement, templateConcurrency: 1);

    private static ElasticFleetAllocationOracle.Job Job(
        string id,
        string recipe,
        long input = 1,
        TimeSpan? age = null,
        string scope = "observatory")
        => new(id, scope, recipe, input, age ?? TimeSpan.FromMinutes(1));

    private static ElasticFleetAllocationOracle.Registration Runner(
        string id,
        IReadOnlyCollection<string> recipes,
        long transfer = 1024,
        int concurrency = 1,
        int occupied = 0)
        => new(id, recipes.ToHashSet(StringComparer.Ordinal), transfer, concurrency, occupied);
}
