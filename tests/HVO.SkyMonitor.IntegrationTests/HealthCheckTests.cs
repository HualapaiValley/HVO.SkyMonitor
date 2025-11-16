namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Basic health check integration tests to verify the test infrastructure works.
/// </summary>
[TestClass]
public class HealthCheckTests
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
    public async Task HealthCheck_ReturnsHealthy()
    {
        // Arrange
        var request = "/health";

        // Act
        var response = await _client!.GetAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AliveCheck_ReturnsHealthy()
    {
        // Arrange
        var request = "/alive";

        // Act
        var response = await _client!.GetAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
