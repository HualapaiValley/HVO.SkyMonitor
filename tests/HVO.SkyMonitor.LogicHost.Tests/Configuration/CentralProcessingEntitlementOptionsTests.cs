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
        Assert.IsTrue(fair.Contains("/ COALESCE(ent.[w], @defaultWeight)", StringComparison.Ordinal));
        Assert.IsTrue(fair.Contains("@poolMode = 2 AND ent.[pool] = @pool", StringComparison.Ordinal));
        foreach (var sql in new[] { plain, fair })
        {
            Assert.IsTrue(sql.Contains("STRING_SPLIT(@includeRecipes, ',')", StringComparison.Ordinal));
            Assert.IsTrue(sql.Contains("job.[AttemptCount] >= job.[MaxAttempts]", StringComparison.Ordinal));
        }
    }
}
