namespace HVO.SkyMonitor.CameraAgent.Authentication;

/// <summary>
/// Service for acquiring authentication tokens or credentials from the central HVO.SkyMonitor identity service.
/// </summary>
public interface ICentralAuthenticationService
{
    /// <summary>
    /// Gets a valid access token for calling HVO.SkyMonitor APIs.
    /// Automatically handles token caching, refresh, and acquisition using configured authentication mode.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A valid access token, or null if using API key mode.</returns>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the API key if configured, otherwise null.
    /// </summary>
    /// <returns>The API key or null.</returns>
    string? GetApiKey();

    /// <summary>
    /// Configures an HttpClient to include authentication headers for requests to HVO.SkyMonitor.
    /// Adds either Bearer token or X-API-Key header based on authentication mode.
    /// </summary>
    /// <param name="client">The HttpClient to configure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConfigureHttpClientAsync(HttpClient client, CancellationToken cancellationToken = default);
}
