using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalSelectionTests
{
    [TestMethod]
    public void SelectionChecksBothEndpointsAndExactPolicyBoundaries()
    {
        var record = OrbitalContractExamples.Record(); var snapshot = OrbitalContractExamples.Snapshot(record);
        var id = record.ObjectIdentity;
        OrbitalRecordSelection Select(double start, double end, bool stale = false) => OrbitalRecordSelector.Select(snapshot, id,
            new(record.EpochUtc.AddHours(start), record.EpochUtc.AddHours(end)), OrbitalContractExamples.Policy(stale));
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital", "contract-v1", "selection-cases.json")));
        Assert.AreEqual(record.RecordSha256, fixture.RootElement.GetProperty("recordSha256").GetString());
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var selected = Select(item.GetProperty("startHours").GetDouble(), item.GetProperty("endHours").GetDouble(), item.GetProperty("allowStaleDiagnostics").GetBoolean());
            Assert.AreEqual(item.GetProperty("eligibility").GetString(), selected.Eligibility.ToString());
            Assert.AreEqual(item.GetProperty("freshness").GetString(), selected.Candidates[0].Freshness.ToString());
        }
        Assert.AreEqual(OrbitalSelectionEligibility.Eligible, Select(-24, 48).Eligibility);
        Assert.AreEqual(OrbitalElementFreshness.Fresh, Select(-24, 48).Candidates[0].Freshness);
        Assert.AreEqual(OrbitalSelectionEligibility.OutsidePolicyWindow, Select(-24.00001, 0).Eligibility);
        Assert.AreEqual(OrbitalElementFreshness.FutureBeyondPolicy, Select(-24.00001, 0).Candidates[0].Freshness);
        Assert.AreEqual(OrbitalSelectionEligibility.StaleWithheld, Select(0, 48.00001).Eligibility);
        Assert.AreEqual(OrbitalSelectionEligibility.Eligible, Select(0, 168, true).Eligibility);
        Assert.AreEqual(OrbitalElementFreshness.Stale, Select(0, 168, true).Candidates[0].Freshness);
        Assert.AreEqual(OrbitalSelectionEligibility.OutsidePolicyWindow, Select(0, 168.00001, true).Eligibility);
        Assert.AreEqual(OrbitalElementFreshness.Expired, Select(0, 168.00001, true).Candidates[0].Freshness);
        Assert.AreEqual(OrbitalSourceValidity.Unknown, Select(0, 1).Candidates[0].SourceValidity);
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalRecordSelector.Select(snapshot, id,
            new(record.EpochUtc.AddSeconds(1), record.EpochUtc), OrbitalContractExamples.Policy()));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalRecordSelector.Select(snapshot, id,
            new(record.EpochUtc.ToOffset(TimeSpan.FromHours(1)), record.EpochUtc), OrbitalContractExamples.Policy()));
    }

    [TestMethod]
    public void SourceRankThenMidpointNewestAndHashAreStableUnderPermutation()
    {
        var older = OrbitalContractExamples.Record(epochHours: -1);
        var newer = OrbitalContractExamples.Record(epochHours: 1);
        var alias = OrbitalContract.CreateRecord(newer with { ObjectName = "other alias" });
        var sourceB = OrbitalContractExamples.Record(dataset: "other:gp:active");
        var records = new[] { older, newer, alias, sourceB };
        var sources = new[] { OrbitalContractExamples.Source(), OrbitalContractExamples.Source("other:gp:active") };
        var interval = new OrbitalUtcInterval(OrbitalContractExamples.Epoch, OrbitalContractExamples.Epoch);
        var priorities = new[] { new OrbitalSourcePriority("other:gp:active", 1), new OrbitalSourcePriority("celestrak:gp:active", 0) };
        var policy = new OrbitalSelectionPolicy(priorities);
        priorities[0] = new("other:gp:active", 0);
        var expected = new[] { newer, alias }.OrderBy(static r => r.RecordSha256, StringComparer.Ordinal).First();
        foreach (var inputs in new[] { records, records.Reverse().ToArray(), new[] { alias, sourceB, older, newer } })
        {
            var snapshot = OrbitalSnapshot.Create(inputs, sources, OrbitalContractExamples.Coverage);
            var selected = OrbitalRecordSelector.Select(snapshot, newer.ObjectIdentity, interval, policy);
            Assert.AreEqual(expected.RecordSha256, selected.SelectedRecord!.RecordSha256);
            Assert.AreEqual(OrbitalRecordSelector.SelectionRule, selected.SelectionRule);
        }
        var higherRankB = new OrbitalSelectionPolicy([new("other:gp:active", 0), new("celestrak:gp:active", 1)]);
        var s = OrbitalSnapshot.Create(records, sources, OrbitalContractExamples.Coverage);
        Assert.AreEqual(sourceB.RecordSha256, OrbitalRecordSelector.Select(s, newer.ObjectIdentity, interval, higherRankB).SelectedRecord!.RecordSha256);
        var sameRank = new OrbitalSelectionPolicy([new("other:gp:active", 0), new("celestrak:gp:active", 0)]);
        Assert.AreEqual(sourceB.RecordSha256, OrbitalRecordSelector.Select(s, newer.ObjectIdentity, interval, sameRank).SelectedRecord!.RecordSha256);
        var atStart = OrbitalContractExamples.Record();
        var oneTickNewer = OrbitalContract.CreateRecord(atStart with { EpochUtc = atStart.EpochUtc.AddTicks(1) });
        var halfTickInterval = new OrbitalUtcInterval(atStart.EpochUtc, oneTickNewer.EpochUtc);
        var halfTickSelection = OrbitalRecordSelector.Select(OrbitalContractExamples.Snapshot(atStart, oneTickNewer),
            atStart.ObjectIdentity, halfTickInterval, OrbitalContractExamples.Policy());
        Assert.AreEqual(oneTickNewer.RecordSha256, halfTickSelection.SelectedRecord!.RecordSha256,
            "Equal distance from a half-tick midpoint must use the newest-epoch tie break.");
    }

    [TestMethod]
    public void MissingObjectUnapprovedSourceAndValidityRemainDistinct()
    {
        var r = OrbitalContractExamples.Record();
        var limited = OrbitalContract.CreateRecord(r with { ValidFromUtc = r.EpochUtc.AddHours(1), ValidToUtc = r.EpochUtc.AddHours(2) });
        var snapshot = OrbitalContractExamples.Snapshot(limited);
        OrbitalRecordSelection Select(OrbitalObjectIdentity id, double start, double end, OrbitalSelectionPolicy? policy = null)
            => OrbitalRecordSelector.Select(snapshot, id, new(r.EpochUtc.AddHours(start), r.EpochUtc.AddHours(end)), policy ?? OrbitalContractExamples.Policy());
        Assert.AreEqual(OrbitalSelectionEligibility.Eligible, Select(r.ObjectIdentity, 1, 2).Eligibility);
        Assert.AreEqual(OrbitalSourceValidity.Known, Select(r.ObjectIdentity, 1, 2).Candidates[0].SourceValidity);
        Assert.AreEqual(OrbitalSelectionEligibility.OutsideSourceValidity, Select(r.ObjectIdentity, .99, 2).Eligibility);
        Assert.AreEqual(OrbitalSelectionEligibility.OutsideSourceValidity, Select(r.ObjectIdentity, 1, 2.01).Eligibility);
        Assert.AreEqual(OrbitalSelectionEligibility.MissingObject, Select(new("norad", "123456789"), 1, 2).Eligibility);
        Assert.AreEqual(OrbitalSelectionEligibility.UnapprovedSource, Select(r.ObjectIdentity, 1, 2, new([new("unapproved:other", 0)])).Eligibility);
        Assert.AreEqual(OrbitalPopulationCompleteness.CompleteForRequestedPopulation, snapshot.Manifest.Coverage.Completeness);
        Assert.IsTrue(snapshot.Manifest.Coverage.KnownExclusions.Contains("inactive objects"));
    }
}
