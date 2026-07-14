using System;
using System.Text.Json;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Basic health check integration tests to verify the test infrastructure works.
/// </summary>
[TestClass]
public sealed class HealthCheckTests
{
    private HttpClient? _client;

    [TestInitialize]
    public void TestInitialize()
    {
        _client = AssemblyHooks.Fixture.Factory.CreateClient();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client?.Dispose();
    }

    [TestMethod]
    public async Task HealthCheckReportsExplicitFixtureCatalogAsync()
    {
        // Arrange
        var request = new Uri("/health", UriKind.Relative);

        // Act
        var response = await _client!.GetAsync(request).ConfigureAwait(false);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual("Degraded", payload.RootElement.GetProperty("status").GetString());
        var catalog = payload.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "catalog");
        Assert.AreEqual("Degraded", catalog.GetProperty("status").GetString());
        StringAssert.Contains(
            catalog.GetProperty("description").GetString(),
            "Fixture celestial catalog snapshot",
            StringComparison.Ordinal);
        var identity = catalog.GetProperty("data");
        Assert.AreEqual("Fixture", identity.GetProperty("Kind").GetString());
        Assert.AreEqual("4.2-fixture.1", identity.GetProperty("CatalogVersion").GetString());
        Assert.AreEqual(9, identity.GetProperty("RowCount").GetInt64());
    }

    [TestMethod]
    public async Task AliveCheckReturnsHealthyAsync()
    {
        // Arrange
        var request = new Uri("/alive", UriKind.Relative);

        // Act
        var response = await _client!.GetAsync(request).ConfigureAwait(false);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
