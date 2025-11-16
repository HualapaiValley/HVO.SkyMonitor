using System;

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
    public async Task HealthCheckReturnsHealthyAsync()
    {
        // Arrange
        var request = new Uri("/health", UriKind.Relative);

        // Act
        var response = await _client!.GetAsync(request).ConfigureAwait(false);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
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
