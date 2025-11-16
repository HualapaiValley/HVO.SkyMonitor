using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
public class ProtectedApiTests
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
    public async Task ProtectedStatus_WithBearerToken_Succeeds()
    {
        var scope = string.Join(' ', TestClients.WebUI.Scopes);
        var token = await HttpHelpers.GetPasswordTokenAsync(
            _client!,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            scope);

        var authedClient = HttpHelpers.WithBearerToken(_client!, token.AccessToken);
        var response = await authedClient.GetAsync("/api/v1.0/status/protected");

        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task ProtectedStatus_MissingToken_ReturnsUnauthorized()
    {
        var response = await _client!.GetAsync("/api/v1.0/status/protected");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task DetailedStatus_WithValidApiKey_Succeeds()
    {
        var authedClient = HttpHelpers.WithApiKey(_client!, TestApiKeys.InternalService.Key);
        var response = await authedClient.GetAsync("/api/v1.0/status/detailed");

        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task DetailedStatus_WithInvalidApiKey_ReturnsUnauthorized()
    {
        _client!.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, "invalid-key");
        var response = await _client.GetAsync("/api/v1.0/status/detailed");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

}
