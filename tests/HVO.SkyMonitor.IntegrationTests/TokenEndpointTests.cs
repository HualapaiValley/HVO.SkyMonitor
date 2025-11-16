using System.Threading.Tasks;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
public sealed class TokenEndpointTests
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
    public async Task ClientCredentialsWithSystemClientReturnsTokenAsync()
    {
        var scope = string.Join(' ', TestClients.SystemCameraAgent.Scopes);
        var token = await HttpHelpers.GetClientCredentialsTokenAsync(
            _client!,
            "/connect/token",
            TestClients.SystemCameraAgent.ClientId,
            TestClients.SystemCameraAgent.ClientSecret,
            scope).ConfigureAwait(false);

        Assert.IsFalse(string.IsNullOrEmpty(token.AccessToken), "Access token should not be empty.");
    }

    [TestMethod]
    public async Task PasswordGrantWithAdminUserReturnsTokenAsync()
    {
        var scope = string.Join(' ', TestClients.WebUI.Scopes);
        var token = await HttpHelpers.GetPasswordTokenAsync(
            _client!,
            "/connect/token",
            TestUsers.Admin.Username,
            TestUsers.Admin.Password,
            TestClients.WebUI.ClientId,
            scope).ConfigureAwait(false);

        Assert.IsFalse(string.IsNullOrEmpty(token.AccessToken), "Access token should not be empty.");
    }
}
