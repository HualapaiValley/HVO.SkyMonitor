using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Tests.Configuration;

[TestClass]
public sealed class CentralProcessingEntitlementOptionsTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void DefaultsAreDisabledUnlimitedAndValid()
    {
        var options = new CentralProcessingEntitlementOptions();
        Assert.IsTrue(options.Validate(out var error), error);
        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(0, options.ResolveActiveJobs(Guid.NewGuid()));
        Assert.AreEqual(1.0, options.ResolveWeight(Guid.NewGuid()));
        Assert.IsNull(options.ResolvePool(Guid.NewGuid()));
        Assert.AreEqual("[]", options.CreateObservatoryEntitlementsJson());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RecipesMapToLayeredResourceClassesWithOverrides()
    {
        var options = new CentralProcessingEntitlementOptions
        {
            RecipeResourceClasses = { [BuiltInProcessingRecipes.RollingMean] = CentralProcessingEntitlementOptions.ImageClass }
        };
        Assert.AreEqual(CentralProcessingEntitlementOptions.EncodingClass, options.ResolveResourceClass(BuiltInProcessingRecipes.EncodedPreview));
        Assert.AreEqual(CentralProcessingEntitlementOptions.PresentationClass, options.ResolveResourceClass(BuiltInProcessingRecipes.Annotation));
        Assert.AreEqual(CentralProcessingEntitlementOptions.StructuredAnalysisClass, options.ResolveResourceClass(BuiltInProcessingRecipes.ImageQuality));
        Assert.AreEqual(CentralProcessingEntitlementOptions.StructuredAnalysisClass, options.ResolveResourceClass("central-transient-validation"));
        Assert.AreEqual(CentralProcessingEntitlementOptions.ImageClass, options.ResolveResourceClass(BuiltInProcessingRecipes.RollingMean));
        Assert.AreEqual(CentralProcessingEntitlementOptions.ImageClass, options.ResolveResourceClass("unknown-recipe"));
        using var classes = JsonDocument.Parse(options.CreateRecipeClassesJson());
        var preview = classes.RootElement.EnumerateArray().Single(item => item.GetProperty("r").GetString() == BuiltInProcessingRecipes.EncodedPreview);
        Assert.AreEqual(CentralProcessingEntitlementOptions.EncodingClass, preview.GetProperty("cls").GetString());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ObservatoryEntitlementsResolveAndSerializeForTheClaimQuery()
    {
        var observatory = Guid.NewGuid();
        var options = new CentralProcessingEntitlementOptions
        {
            Enabled = true,
            DefaultActiveJobs = 4,
            Observatories =
            {
                [observatory.ToString("D")] = new ObservatoryEntitlementOptions
                {
                    ActiveJobs = 2, Weight = 3, Priority = -1, Pool = "blue",
                    ResourceClassActiveJobs = { [CentralProcessingEntitlementOptions.EncodingClass] = 1 }
                }
            },
            ResourceClasses = { [CentralProcessingEntitlementOptions.EncodingClass] = new ResourceClassBudgetOptions { ActiveJobs = 8, ActiveInputBytes = 1024 } }
        };
        Assert.IsTrue(options.Validate(out var error), error);
        Assert.AreEqual(2, options.ResolveActiveJobs(observatory));
        Assert.AreEqual(4, options.ResolveActiveJobs(Guid.NewGuid()));
        Assert.AreEqual(3, options.ResolveWeight(observatory));
        Assert.AreEqual("blue", options.ResolvePool(observatory));
        using var entitlements = JsonDocument.Parse(options.CreateObservatoryEntitlementsJson());
        var row = entitlements.RootElement.EnumerateArray().Single();
        Assert.AreEqual(observatory, row.GetProperty("o").GetGuid());
        Assert.AreEqual(2, row.GetProperty("a").GetInt32());
        Assert.AreEqual("blue", row.GetProperty("pool").GetString());
        using var limits = JsonDocument.Parse(options.CreateObservatoryClassLimitsJson());
        Assert.AreEqual(1, limits.RootElement.EnumerateArray().Single().GetProperty("a").GetInt32());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ValidationRejectsBadWeightsPoolsClassesAndLimits()
    {
        Assert.IsFalse(new CentralProcessingEntitlementOptions { DefaultWeight = 0 }.Validate(out _));
        Assert.IsFalse(new CentralProcessingEntitlementOptions { StarvationAge = TimeSpan.Zero }.Validate(out _));
        Assert.IsFalse(new CentralProcessingEntitlementOptions
        {
            Observatories = { [Guid.NewGuid().ToString("D")] = new ObservatoryEntitlementOptions { Pool = "Shared Pool" } }
        }.Validate(out _));
        Assert.IsFalse(new CentralProcessingEntitlementOptions
        {
            Observatories = { [Guid.NewGuid().ToString("D")] = new ObservatoryEntitlementOptions { Pool = CentralProcessingEntitlementOptions.SharedPool } }
        }.Validate(out _));
        Assert.IsFalse(new CentralProcessingEntitlementOptions
        {
            ResourceClasses = { ["Bad Class"] = new ResourceClassBudgetOptions() }
        }.Validate(out _));
        Assert.IsFalse(new CentralProcessingEntitlementOptions
        {
            Observatories = { ["not-a-guid"] = new ObservatoryEntitlementOptions() }
        }.Validate(out _));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RunnerPoolLabelsResolveToClaimScopes()
    {
        Assert.AreEqual((null, CentralDerivativeClaimPoolMode.Shared), CentralProcessingRunnerJobService.ResolvePool([]));
        Assert.AreEqual(("blue", CentralDerivativeClaimPoolMode.Dedicated), CentralProcessingRunnerJobService.ResolvePool(["site:lab", "pool:blue"]));
        Assert.AreEqual(("blue", CentralDerivativeClaimPoolMode.Reserved), CentralProcessingRunnerJobService.ResolvePool(["pool:blue", "pool-mode:reserved"]));
        Assert.AreEqual((null, CentralDerivativeClaimPoolMode.Shared), CentralProcessingRunnerJobService.ResolvePool(["pool:shared"]));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CandidateSqlOnlyCarriesFairnessClausesWhenEnabled()
    {
        var plain = CentralDerivativeJobService.CreateCandidateSql(false);
        var fair = CentralDerivativeJobService.CreateCandidateSql(true);
        Assert.IsFalse(plain.Contains("OPENJSON(@entitlements)", StringComparison.Ordinal));
        Assert.IsFalse(plain.Contains("@starvationBefore", StringComparison.Ordinal));
        Assert.IsTrue(fair.Contains("OPENJSON(@entitlements)", StringComparison.Ordinal));
        Assert.IsTrue(fair.Contains("@starvationBefore", StringComparison.Ordinal));
        Assert.IsTrue(fair.Contains("(fair.[ObservatoryActive] + fair.[ObservatoryServed] + 1.0) / COALESCE(ent.[w], @defaultWeight)", StringComparison.Ordinal));
        Assert.IsTrue(fair.Contains("[RecordedAtUtc] > @servedSince", StringComparison.Ordinal), "recent service within the fair-share window counts toward share");
        Assert.IsFalse(plain.Contains("@servedSince", StringComparison.Ordinal));
        Assert.IsTrue(fair.Contains("@poolMode = 2 AND ent.[pool] = @pool", StringComparison.Ordinal));
        Assert.IsTrue(fair.TrimStart().StartsWith("WITH active AS", StringComparison.Ordinal), "fairness aggregates are computed once per query");
        Assert.IsFalse(plain.Contains("WITH active AS", StringComparison.Ordinal));
        var batch = CentralDerivativeJobService.CreateCandidateSql(true, idOnly: true);
        Assert.IsTrue(batch.Contains($"SELECT TOP({CentralDerivativeJobService.FairCandidateBatchSize}) ranked.[Value]", StringComparison.Ordinal));
        Assert.IsTrue(batch.Contains("ROW_NUMBER() OVER (PARTITION BY sourceFrame.[ObservatoryId]", StringComparison.Ordinal), "batches are breadth-first across observatories");
        Assert.IsTrue(batch.Contains("ORDER BY ranked.[k0], ranked.[k1], ranked.[k2], ranked.[k3], ranked.[rn], ranked.[a0]", StringComparison.Ordinal));
        Assert.IsTrue(fair.Contains("SELECT TOP(1) job.*", StringComparison.Ordinal));
        foreach (var scoped in new[] { "@excludedCameras", "@excludedClasses", "@excludedObservatoryClasses" })
        {
            Assert.IsTrue(fair.Contains(scoped, StringComparison.Ordinal), $"rejections exclude only their dimension ({scoped})");
        }
        Assert.IsTrue(fair.Contains("OR ((COALESCE(ent.[a], @defaultActive) = 0", StringComparison.Ordinal),
            "exhausted expired leases are exempt from the fairness predicates");
        foreach (var sql in new[] { plain, fair })
        {
            Assert.IsTrue(sql.Contains("STRING_SPLIT(@includeRecipes, ',')", StringComparison.Ordinal));
            Assert.IsTrue(sql.Contains("job.[AttemptCount] >= job.[MaxAttempts]", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ObservatoryKeysResolveByParsedGuidInEverySpelling()
    {
        var id = Guid.NewGuid();
        foreach (var spelling in new[] { id.ToString("N"), id.ToString("B").ToUpperInvariant(), id.ToString("D") })
        {
            var options = new CentralProcessingEntitlementOptions
            {
                Enabled = true,
                Observatories = { [spelling] = new ObservatoryEntitlementOptions { ActiveJobs = 1, Weight = 2 } }
            };
            Assert.IsTrue(options.Validate(out _), spelling);
            Assert.AreEqual(1, options.ResolveActiveJobs(id), spelling);
            Assert.AreEqual(2.0, options.ResolveWeight(id), spelling);
            Assert.IsTrue(options.CreateObservatoryEntitlementsJson().Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase), spelling);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void StarvationAgeIsBoundedAndTheThresholdNeverUnderflows()
    {
        var options = new CentralProcessingEntitlementOptions { Enabled = true, StarvationAge = TimeSpan.MaxValue };
        Assert.IsFalse(options.Validate(out var error));
        Assert.IsNotNull(error);
        Assert.IsFalse(new CentralProcessingEntitlementOptions { Enabled = true, FairShareWindow = TimeSpan.Zero }.Validate(out _));
        Assert.IsTrue(new CentralProcessingEntitlementOptions { Enabled = true, StarvationAge = CentralProcessingEntitlementOptions.MaximumStarvationAge }.Validate(out _));
        var now = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(now.AddMinutes(-10), CentralProcessingEntitlementOptions.StarvationThreshold(now, TimeSpan.FromMinutes(10)));
        Assert.AreEqual(DateTimeOffset.MinValue, CentralProcessingEntitlementOptions.StarvationThreshold(now, TimeSpan.MaxValue));
        Assert.AreEqual(now, CentralProcessingEntitlementOptions.StarvationThreshold(now, TimeSpan.Zero));
    }
}
