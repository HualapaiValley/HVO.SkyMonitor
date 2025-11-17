using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using HVO.SkyMonitor.CameraAgent.Models.Sample;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestClass]
public sealed class SampleEndpointTests
{
    private HttpClient? _cameraAgentClient;
    private HttpClient? _hostClient;

    [TestInitialize]
    public void SetUp()
    {
        _cameraAgentClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        _hostClient = AssemblyHooks.Fixture.CreateHostClient();
    }

    [TestCleanup]
    public void TearDown()
    {
        _cameraAgentClient?.Dispose();
        _hostClient?.Dispose();
    }

    [TestMethod]
    public async Task StatusEndpointAllowsAnonymousRequestsAsync()
    {
        var statusUri = new Uri("/api/v1/sample/status", UriKind.Relative);
        using var response = await _cameraAgentClient!.GetAsync(statusUri).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SampleStatusResponse>(HttpHelpers.DefaultJsonOptions)
            .ConfigureAwait(false);

        Assert.IsNotNull(payload, "Sample status payload should be returned.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(payload.Message), "Sample status message should be populated.");
    }

    [TestMethod]
    public async Task AuthOnlyEndpointRejectsAnonymousRequestsAsync()
    {
        var authOnlyUri = new Uri("/api/v1/sample/authonly", UriKind.Relative);
        using var response = await _cameraAgentClient!.GetAsync(authOnlyUri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "Anonymous requests should be rejected.");
    }

    [TestMethod]
    public async Task AuthOnlyEndpointAllowsSystemClientBearerTokenAsync()
    {
        var scope = string.Join(' ', TestClients.SystemCameraAgent.Scopes);
        var token = await HttpHelpers.GetClientCredentialsTokenAsync(
            _hostClient!,
            "/connect/token",
            TestClients.SystemCameraAgent.ClientId,
            TestClients.SystemCameraAgent.ClientSecret,
            scope).ConfigureAwait(false);

        HttpHelpers.WithBearerToken(_cameraAgentClient!, token.AccessToken);

        var authOnlyUri = new Uri("/api/v1/sample/authonly", UriKind.Relative);
        using var response = await _cameraAgentClient!.GetAsync(authOnlyUri).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var challenges = string.Join(", ", response.Headers.WwwAuthenticate);
            Assert.Fail($"Status: {response.StatusCode}, Challenges: {challenges}, Body: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<SampleAuthenticatedResponse>(HttpHelpers.DefaultJsonOptions)
            .ConfigureAwait(false);

        Assert.IsNotNull(payload, "Authenticated payload should be returned.");
        Assert.AreEqual("Authenticated request.", payload!.Message, "Sample authenticated message should match.");
    }
}
