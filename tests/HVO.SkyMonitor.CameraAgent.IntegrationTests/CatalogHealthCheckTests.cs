using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CatalogHealthCheckTests
{
    [TestMethod]
    public async Task HealthCheckReportsExplicitFixtureCatalogAsync()
    {
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var catalog = payload.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "catalog");
        Assert.AreEqual("Degraded", catalog.GetProperty("status").GetString());
        StringAssert.Contains(
            catalog.GetProperty("description").GetString(),
            "Fixture celestial catalog snapshot",
            StringComparison.Ordinal);
        var identity = catalog.GetProperty("data");
        Assert.AreEqual("Fixture", identity.GetProperty("Kind").GetString());
        Assert.AreEqual("hyg-v42-fixture", identity.GetProperty("CatalogId").GetString());
        Assert.AreEqual("explicit-manifest-v2", identity.GetProperty("CatalogIdentitySource").GetString());
        Assert.AreEqual("4.2-fixture.1", identity.GetProperty("CatalogVersion").GetString());
        Assert.AreEqual(9, identity.GetProperty("RowCount").GetInt64());
    }
}
