using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests;

/// <summary>
/// Integration tests using Aspire distributed application testing.
/// These tests start the entire AppHost with all dependencies (Redis, PostgreSQL, MinIO).
/// Requires Docker/Podman to be running for container orchestration.
/// </summary>
[TestClass]
[TestCategory("AspireIntegration")]
[TestCategory("RequiresDocker")]
public class AspireIntegrationTests
{
    [TestMethod]
    public async Task SkyMonitorApp_StartsSuccessfully()
    {
        // Arrange
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.HVO_SkyMonitor_AppHost>();

        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        await using var app = await appHost.BuildAsync();

        // Act
        await app.StartAsync();

        // Assert - Get HTTP client for the SkyMonitor application
        var httpClient = app.CreateHttpClient("skymonitor");
        
        // Wait a bit for the application to be fully ready
        await Task.Delay(TimeSpan.FromSeconds(5));
        
        var response = await httpClient.GetAsync("/api/v1.0/Status");
        
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(content.TryGetProperty("Status", out var status));
        Assert.AreEqual("Running", status.GetString());

        await app.StopAsync();
    }

    [TestMethod]
    public async Task OAuth2_TokenEndpoint_RespondsCorrectly()
    {
        // Arrange
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.HVO_SkyMonitor_AppHost>();

        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        await using var app = await appHost.BuildAsync();
        await app.StartAsync();

        var httpClient = app.CreateHttpClient("skymonitor");
        
        // Wait for application to be ready
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Act - Request token with invalid credentials (proves endpoint exists)
        var tokenRequest = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "test-client",
            ["client_secret"] = "test-secret",
            ["scope"] = "api"
        };

        var response = await httpClient.PostAsync("/connect/token", new FormUrlEncodedContent(tokenRequest));

        // Assert - Expecting 400 Bad Request for invalid client (proves endpoint is functional)
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        await app.StopAsync();
    }

    [TestMethod]
    public async Task ProtectedEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        // Arrange
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.HVO_SkyMonitor_AppHost>();

        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        await using var app = await appHost.BuildAsync();
        await app.StartAsync();

        var httpClient = app.CreateHttpClient("skymonitor");
        
        // Wait for application to be ready
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Act - Try to access protected endpoint without authentication
        var response = await httpClient.GetAsync("/api/v1.0/Status/protected");

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        await app.StopAsync();
    }

    [TestMethod]
    public async Task DetailedStatus_WithoutApiKey_RequiresAuthentication()
    {
        // Arrange
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.HVO_SkyMonitor_AppHost>();

        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        await using var app = await appHost.BuildAsync();
        await app.StartAsync();

        var httpClient = app.CreateHttpClient("skymonitor");
        
        // Wait for application to be ready
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Act - Try to access detailed status without API key
        var response = await httpClient.GetAsync("/api/v1.0/Status/detailed");

        // Assert - Should require authentication
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        await app.StopAsync();
    }
}

