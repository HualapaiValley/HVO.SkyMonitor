using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentStateInventoryTests
{
    [TestMethod]
    public void Stores_UsesTheSmallDatabaseInventoryForTheSiloBaseline()
    {
        string[] expected =
        [
            "identity/cameraagent_identity.db",
            "raw/journal/raw-ingress.db",
            "raw/.environment/environmental-observations.db",
            "raw/.time-lapses/time-lapses.db",
            "raw/.nightly-products/nightly-products.db",
            "raw/.automation/local-automations.db",
            "raw/.location/retained-integrity.v1.db"
        ];
        CollectionAssert.AreEqual(expected, CameraAgentStateInventory.Stores.ToArray());
    }
}
