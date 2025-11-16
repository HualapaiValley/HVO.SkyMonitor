using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
public sealed class ProtectedApiTests
{
    private HttpClient? _client;

    [TestInitialize]
    public void SetUp()
    {
        _client = AssemblyHooks.Fixture.Factory.CreateClient();
    }

    [TestCleanup]
    public void TearDown()
    {
        _client?.Dispose();
    }

    [TestMethod]
    public async Task ProtectedStatusWithBearerTokenSucceedsAsync()
    {
        var scope = string.Join(' ', TestClients.WebUI.Scopes);
        var token = await HttpHelpers.GetPasswordTokenAsync(
            _client!,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            scope).ConfigureAwait(false);

        var authedClient = HttpHelpers.WithBearerToken(_client!, token.AccessToken);
        var response = await authedClient.GetAsync(new Uri("/api/v1.0/status/protected", UriKind.Relative)).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task ProtectedStatusMissingTokenReturnsUnauthorizedAsync()
    {
        var response = await _client!.GetAsync(new Uri("/api/v1.0/status/protected", UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task DetailedStatusWithValidApiKeySucceedsAsync()
    {
        var authedClient = HttpHelpers.WithApiKey(_client!, TestApiKeys.InternalService.Key);
        var response = await authedClient.GetAsync(new Uri("/api/v1.0/status/detailed", UriKind.Relative)).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task DetailedStatusWithInvalidApiKeyReturnsUnauthorizedAsync()
    {
        _client!.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, "invalid-key");
        var response = await _client.GetAsync(new Uri("/api/v1.0/status/detailed", UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

}
