using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.Tests.LogicHost.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class BasicCentralProcessingGraphSeedPinTests
{
    // Revision 2 (#526, measured stellar associations). Revision 1 (development/v1 b13f0d0e) is retained in
    // CanonicalCentralGraphSeedChain. Startup reseeding compares these bytes and identities with the existing row and
    // refuses to start on any difference, so a change here is a seeded-graph migration.
    private const string ExpectedDefinitionJsonSha256 = "325B6E5A604642C8F08E30B0008F4687BA679A0DF20FADF544F5B51838988523";
    private const string ExpectedDefinitionIdentitySha256 = "325B6E5A604642C8F08E30B0008F4687BA679A0DF20FADF544F5B51838988523";
    private const string ExpectedPortablePlanIdentitySha256 = "16692249A028631B94E13A0B025C8CC02D1B3CB4B805CD47079C3F4D9B1CDEC9";
    private const string ExpectedCentralPlanIdentitySha256 = "16692249A028631B94E13A0B025C8CC02D1B3CB4B805CD47079C3F4D9B1CDEC9";

    [TestMethod]
    public void SeededBasicCentralGraphIsByteIdenticalToItsPublishedRevision()
    {
        var definition = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        var portable = ProcessingGraphCompiler.Compile(definition).Plan!;
        var central = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.LogicHost, ImmutableArray<string>.Empty)).Plan!;

        Assert.AreEqual(ExpectedDefinitionJsonSha256,
            Convert.ToHexString(SHA256.HashData(ProcessingGraphJson.SerializeCanonical(definition))));
        Assert.AreEqual(ExpectedDefinitionIdentitySha256, portable.DefinitionIdentitySha256);
        Assert.AreEqual(ExpectedPortablePlanIdentitySha256, portable.PlanIdentitySha256);
        Assert.AreEqual(ExpectedCentralPlanIdentitySha256, central.PlanIdentitySha256);
    }
}
