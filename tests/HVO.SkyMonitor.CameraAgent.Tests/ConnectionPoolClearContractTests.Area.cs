using HVO.SkyMonitor.CameraAgent.Tests;

namespace HVO.SkyMonitor.TestSettings;

[TestProperty("Area", CameraAgentTestAreas.TestInfrastructure)]
public sealed partial class ConnectionPoolClearContractTests
{
    static partial void AssertAssemblySpecificContracts() =>
        CameraAgentTestAreas.AssertEveryTestClassHasValidArea(typeof(ConnectionPoolClearContractTests).Assembly);
}
