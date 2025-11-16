using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.TestSupport;

/// <summary>
/// HTTP helper utilities for test scenarios.
/// </summary>
public static class HttpHelpers
{
    /// <summary>
    /// Default JSON serializer options for tests.
    /// </summary>
    public static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Obtains an OAuth2 bearer token using client credentials grant.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the token request.</param>
    /// <param name="tokenEndpoint">Token endpoint URL (e.g., "/connect/token").</param>
    /// <param name="clientId">OAuth client ID.</param>
    /// <param name="clientSecret">OAuth client secret.</param>
    /// <param name="scope">Requested scopes (space-separated).</param>
    /// <returns>Bearer token response.</returns>
    public static async Task<TokenResponse> GetClientCredentialsTokenAsync(
        HttpClient httpClient,
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        string? scope = null)
    {
        var request = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        };

        if (!string.IsNullOrEmpty(scope))
        {
            request["scope"] = scope;
        }

        using var content = new FormUrlEncodedContent(request);
        using var response = await httpClient.PostAsync(tokenEndpoint, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(DefaultJsonOptions)
            .ConfigureAwait(false);
        return tokenResponse ?? throw new InvalidOperationException("Failed to deserialize token response");
    }

    /// <summary>
    /// Obtains an OAuth2 bearer token using resource owner password credentials grant.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the token request.</param>
    /// <param name="tokenEndpoint">Token endpoint URL (e.g., "/connect/token").</param>
    /// <param name="username">User's username or email.</param>
    /// <param name="password">User's password.</param>
    /// <param name="clientId">OAuth client ID.</param>
    /// <param name="scope">Requested scopes (space-separated).</param>
    /// <returns>Bearer token response.</returns>
    public static async Task<TokenResponse> GetPasswordTokenAsync(
        HttpClient httpClient,
        string tokenEndpoint,
        string username,
        string password,
        string clientId,
        string? scope = null)
    {
        var request = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = clientId
        };

        if (!string.IsNullOrEmpty(scope))
        {
            request["scope"] = scope;
        }

        using var content = new FormUrlEncodedContent(request);
        using var response = await httpClient.PostAsync(tokenEndpoint, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(DefaultJsonOptions)
            .ConfigureAwait(false);
        return tokenResponse ?? throw new InvalidOperationException("Failed to deserialize token response");
    }

    /// <summary>
    /// Creates an HttpClient with bearer token authentication.
    /// </summary>
    /// <param name="baseClient">Base HTTP client (usually from WebApplicationFactory).</param>
    /// <param name="accessToken">Bearer access token.</param>
    /// <returns>New HttpClient with authorization header set.</returns>
    public static HttpClient WithBearerToken(HttpClient baseClient, string accessToken)
    {
        baseClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return baseClient;
    }

    /// <summary>
    /// Creates an HttpClient with API key authentication.
    /// </summary>
    /// <param name="baseClient">Base HTTP client.</param>
    /// <param name="apiKey">API key value.</param>
    /// <param name="headerName">API key header name (default: "X-API-Key").</param>
    /// <returns>New HttpClient with API key header set.</returns>
    public static HttpClient WithApiKey(HttpClient baseClient, string apiKey, string headerName = "X-API-Key")
    {
        baseClient.DefaultRequestHeaders.Add(headerName, apiKey);
        return baseClient;
    }

    /// <summary>
    /// Represents an OAuth2 token response.
    /// </summary>
    public sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }
}
