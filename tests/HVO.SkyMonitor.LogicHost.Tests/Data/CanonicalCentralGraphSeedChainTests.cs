using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class CanonicalCentralGraphSeedChainTests
{
    [TestMethod]
    public void CurrentEntryMatchesTheDefinitionGeneratedFromCode()
    {
        var definition = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        var (_, definitionJsonSha256, portable, central) = CanonicalCentralGraphSeedChain.Compile(definition);
        var current = CanonicalCentralGraphSeedChain.Current;
        var actual = current with
        {
            DefinitionJsonSha256 = definitionJsonSha256,
            DefinitionIdentitySha256 = portable.Plan!.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.Plan.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.Plan!.PlanIdentitySha256
        };

        // A failure here means a seeded recipe definition, option default or graph shape changed. Do not edit the
        // recorded entry: append the next CanonicalCentralGraphSeedChain entry with these identities.
        Assert.AreEqual(current, actual, $"Generated identities: {actual}");
        Assert.AreEqual(CanonicalCentralGraphSeedChain.GraphName, definition.Name);
        Assert.AreEqual(current.Revision, definition.Revision);
    }

    [TestMethod]
    public void RecordedRevisionOneIsTheShippedB13f0d0eSeed()
    {
        Assert.AreEqual(
            new CanonicalCentralGraphSeedRevision(
                "1",
                Guid.Parse("8d8f8df2-fd82-4dbf-8679-4ce21f6d637e"),
                Guid.Parse("4ea2c2cb-6c92-4386-9ce2-2798c544b61f"),
                DateTimeOffset.UnixEpoch,
                "C1365A0FF6614B1AED2FED96D2CB5B858764676FEAAE3CFD19403F63B24B7785",
                "C1365A0FF6614B1AED2FED96D2CB5B858764676FEAAE3CFD19403F63B24B7785",
                "26D02C61ECD97F4DFC54CCC651267D670AD76781ED07E58CB3FDCF8EC9FDA64E",
                "26D02C61ECD97F4DFC54CCC651267D670AD76781ED07E58CB3FDCF8EC9FDA64E"),
            CanonicalCentralGraphSeedChain.Revisions[0]);
    }

    [TestMethod]
    public void RevisionOneFixtureIsTheExactShippedDefinitionJson()
    {
        // The fixture is the byte-exact DefinitionJson b13f0d0e stored; integration tests insert it to reproduce an
        // existing database, so it must never be regenerated from code.
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "canonical-central-graph-rev1-b13f0d0e.json"));

        Assert.AreEqual(
            CanonicalCentralGraphSeedChain.Revisions[0].DefinitionJsonSha256,
            CanonicalCentralGraphSeedChain.ComputeDefinitionJsonSha256(json));
    }

    [TestMethod]
    public void ChainIsOrderedWithDistinctIdentifiers()
    {
        CanonicalCentralGraphSeedChain.Validate(CanonicalCentralGraphSeedChain.Revisions);
        Assert.IsTrue(CanonicalCentralGraphSeedChain.Revisions.All(
            entry => CanonicalCentralGraphSeedChain.IsSeededRevision(entry.RevisionId) &&
                CanonicalCentralGraphSeedChain.IsSeededAssignment(entry.AssignmentId)));
        Assert.AreEqual(CanonicalCentralGraphSeedChain.Current.RevisionId, DatabaseSeeder.BasicCentralProcessingGraphRevisionId);
        Assert.AreEqual(CanonicalCentralGraphSeedChain.Current.AssignmentId, DatabaseSeeder.BasicCentralProcessingGraphAssignmentId);
    }

    [TestMethod]
    public void ValidateRejectsMalformedChains()
    {
        var first = CanonicalCentralGraphSeedChain.Revisions[0];
        var second = first with
        {
            Revision = "2",
            RevisionId = Guid.NewGuid(),
            AssignmentId = Guid.NewGuid(),
            SeededAtUtc = first.SeededAtUtc.AddTicks(1),
            DefinitionIdentitySha256 = new string('B', 64)
        };
        CanonicalCentralGraphSeedChain.Validate([first, second]);

        Assert.ThrowsExactly<InvalidOperationException>(() => CanonicalCentralGraphSeedChain.Validate([]));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => CanonicalCentralGraphSeedChain.Validate([first, second with { SeededAtUtc = first.SeededAtUtc }]));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => CanonicalCentralGraphSeedChain.Validate([first, second with { Revision = first.Revision }]));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => CanonicalCentralGraphSeedChain.Validate([first, second with { RevisionId = first.RevisionId }]));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => CanonicalCentralGraphSeedChain.Validate([first, second with { AssignmentId = first.RevisionId }]));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => CanonicalCentralGraphSeedChain.Validate(
                [first, second with { DefinitionIdentitySha256 = first.DefinitionIdentitySha256 }]));
    }
}
