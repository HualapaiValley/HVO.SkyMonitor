using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.Tests.LogicHost.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class BasicCentralProcessingGraphSeedPinTests
{
    // Captured from development/v1 b13f0d0e. Startup reseeding compares these bytes and identities with the
    // existing row and refuses to start on any difference, so a change here is a seeded-graph migration.
    private const string ExpectedDefinitionJsonSha256 = "C1365A0FF6614B1AED2FED96D2CB5B858764676FEAAE3CFD19403F63B24B7785";
    private const string ExpectedDefinitionIdentitySha256 = "C1365A0FF6614B1AED2FED96D2CB5B858764676FEAAE3CFD19403F63B24B7785";
    private const string ExpectedPortablePlanIdentitySha256 = "26D02C61ECD97F4DFC54CCC651267D670AD76781ED07E58CB3FDCF8EC9FDA64E";
    private const string ExpectedCentralPlanIdentitySha256 = "26D02C61ECD97F4DFC54CCC651267D670AD76781ED07E58CB3FDCF8EC9FDA64E";

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
