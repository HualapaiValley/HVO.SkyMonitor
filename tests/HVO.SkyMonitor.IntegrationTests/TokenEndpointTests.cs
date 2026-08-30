using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class TokenEndpointTests
{
    private const string RedirectUri = "http://localhost:5000/signin-oidc";
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

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

    [TestMethod]
    public async Task AuthorizationCodeWithPkceReturnsTokensAsync()
    {
        using var authClient = AssemblyHooks.Fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var state = Guid.NewGuid().ToString("N");
        var codeChallenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(CodeVerifier)));
        var authorizeUri = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = TestClients.WebUI.ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile email api.viewer",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = Guid.NewGuid().ToString("N")
        });

        using var challenge = await authClient.GetAsync(new Uri(authorizeUri, UriKind.Relative)).ConfigureAwait(false);
        Assert.IsTrue(IsRedirect(challenge.StatusCode));
        var loginUri = challenge.Headers.Location ?? throw new AssertFailedException("Authorization did not redirect to login.");

        using var loginPage = await authClient.GetAsync(loginUri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, loginPage.StatusCode);
        var html = await loginPage.Content.ReadAsStringAsync().ConfigureAwait(false);
        var tokenMatch = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        Assert.IsTrue(tokenMatch.Success, "The login page omitted its antiforgery token.");

        using var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(tokenMatch.Groups[1].Value),
            ["Input.Email"] = TestUsers.Operator.Email,
            ["Input.Password"] = TestUsers.Operator.Password,
            ["Input.RememberMe"] = "false",
            ["_handler"] = "login"
        });
        using var login = await authClient.PostAsync(loginUri, loginForm).ConfigureAwait(false);
        Assert.IsTrue(IsRedirect(login.StatusCode));
        var resumedAuthorizeUri = login.Headers.Location ?? throw new AssertFailedException("Login did not resume authorization.");

        using var authorization = await authClient.GetAsync(resumedAuthorizeUri).ConfigureAwait(false);
        Assert.IsTrue(IsRedirect(authorization.StatusCode));
        var callbackUri = authorization.Headers.Location ?? throw new AssertFailedException("Authorization did not return a callback.");
        Assert.AreEqual(RedirectUri, callbackUri.GetLeftPart(UriPartial.Path));
        var callbackQuery = QueryHelpers.ParseQuery(callbackUri.Query);
        Assert.AreEqual(state, callbackQuery["state"].ToString());
        var hasError = callbackQuery.TryGetValue("error", out var error);
        var errorDescription = callbackQuery.TryGetValue("error_description", out var description)
            ? description.ToString()
            : error.ToString();
        Assert.IsFalse(hasError, errorDescription);
        var code = callbackQuery["code"].ToString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(code), "Authorization response omitted the code.");

        using var exchangeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = TestClients.WebUI.ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = CodeVerifier
        });
        using var exchange = await _client!.PostAsync(
            new Uri("/connect/token", UriKind.Relative), exchangeForm).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, exchange.StatusCode, await exchange.Content.ReadAsStringAsync().ConfigureAwait(false));
        using var tokens = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.IsFalse(string.IsNullOrWhiteSpace(tokens.RootElement.GetProperty("access_token").GetString()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(tokens.RootElement.GetProperty("id_token").GetString()));
        Assert.AreEqual("Bearer", tokens.RootElement.GetProperty("token_type").GetString(), ignoreCase: true);

        var invalidState = Guid.NewGuid().ToString("N");
        var invalidAuthorizeUri = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = TestClients.WebUI.ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile email api.viewer",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = invalidState,
            ["nonce"] = Guid.NewGuid().ToString("N")
        });
        using var invalidAuthorization = await authClient.GetAsync(
            new Uri(invalidAuthorizeUri, UriKind.Relative)).ConfigureAwait(false);
        Assert.IsTrue(IsRedirect(invalidAuthorization.StatusCode));
        var invalidCallback = invalidAuthorization.Headers.Location
            ?? throw new AssertFailedException("Authorization did not return the PKCE enforcement callback.");
        var invalidQuery = QueryHelpers.ParseQuery(invalidCallback.Query);
        Assert.AreEqual(invalidState, invalidQuery["state"].ToString());
        var invalidCode = invalidQuery["code"].ToString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(invalidCode));
        using var invalidExchangeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = TestClients.WebUI.ClientId,
            ["code"] = invalidCode,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = $"{CodeVerifier[..^1]}A"
        });
        using var invalidExchange = await _client.PostAsync(
            new Uri("/connect/token", UriKind.Relative), invalidExchangeForm).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidExchange.StatusCode);
        using var invalidError = JsonDocument.Parse(
            await invalidExchange.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual("invalid_grant", invalidError.RootElement.GetProperty("error").GetString());
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => (int)statusCode is >= 300 and < 400;
}
