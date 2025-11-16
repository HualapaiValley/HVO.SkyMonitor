using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Authentication;

/// <summary>
/// Implementation of central authentication service for camera agents.
/// Handles OAuth2 client credentials flow and API key authentication.
/// </summary>
public class CentralAuthenticationService(
    IOptions<CentralIdentityOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<CentralAuthenticationService> logger,
    TimeProvider timeProvider) : ICentralAuthenticationService
{
    private readonly CentralIdentityOptions _options = options.Value;
    private TokenCacheEntry? _cachedToken;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Mode == AuthenticationMode.ApiKey)
        {
            logger.LogDebug("Using API key mode, no access token needed");
            return null;
        }

        // Check if we have a valid cached token
        if (_cachedToken is not null && !IsTokenExpiringSoon(_cachedToken))
        {
            logger.LogDebug("Using cached access token");
            return _cachedToken.AccessToken;
        }

        // Acquire lock to prevent multiple concurrent token acquisitions
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_cachedToken is not null && !IsTokenExpiringSoon(_cachedToken))
            {
                logger.LogDebug("Using cached access token (after lock)");
                return _cachedToken.AccessToken;
            }

            logger.LogInformation("Acquiring new access token from {ServiceUrl}", _options.ServiceUrl);
            var token = await AcquireTokenAsync(cancellationToken);

            _cachedToken = new TokenCacheEntry
            {
                AccessToken = token.AccessToken,
                ExpiresAt = timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn),
                AcquiredAt = timeProvider.GetUtcNow()
            };

            logger.LogInformation("Access token acquired successfully, expires in {ExpiresIn} seconds", token.ExpiresIn);
            return _cachedToken.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public string? GetApiKey()
    {
        return _options.Mode == AuthenticationMode.ApiKey
            ? _options.ApiKey?.Key
            : null;
    }

    public async Task ConfigureHttpClientAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        if (_options.Mode == AuthenticationMode.ClientCredentials)
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            if (token is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                logger.LogDebug("Configured HttpClient with Bearer token");
            }
        }
        else if (_options.Mode == AuthenticationMode.ApiKey)
        {
            var apiKey = GetApiKey();
            if (apiKey is not null)
            {
                client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
                logger.LogDebug("Configured HttpClient with API key");
            }
        }
    }

    private async Task<TokenResponse> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        if (_options.ClientCredentials is null)
        {
            throw new InvalidOperationException(
                "ClientCredentials configuration is required when using ClientCredentials authentication mode");
        }

        var client = httpClientFactory.CreateClient();
        var tokenEndpoint = new Uri(_options.ServiceUrl, "/connect/token");

        using var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.ClientCredentials.ClientId,
            ["client_secret"] = _options.ClientCredentials.ClientSecret,
            ["scope"] = string.Join(" ", _options.ClientCredentials.Scopes)
        });

        logger.LogDebug("Requesting token from {TokenEndpoint} with client_id {ClientId}",
            tokenEndpoint, _options.ClientCredentials.ClientId);

        using var response = await client.PostAsync(tokenEndpoint, requestContent, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            logger.LogError("Token acquisition failed with status {StatusCode}: {Error}",
                response.StatusCode, error);
            throw new HttpRequestException(
                $"Failed to acquire access token. Status: {response.StatusCode}, Error: {error}");
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Token response was null");

        return tokenResponse;
    }

    private bool IsTokenExpiringSoon(TokenCacheEntry entry)
    {
        var now = timeProvider.GetUtcNow();
        var refreshWindow = TimeSpan.FromSeconds(_options.TokenRefreshWindowSeconds);
        return entry.ExpiresAt - now <= refreshWindow;
    }

    private sealed class TokenCacheEntry
    {
        public required string AccessToken { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required DateTimeOffset AcquiredAt { get; init; }
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Type is materialized by System.Text.Json deserialization.")]
    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }
}
