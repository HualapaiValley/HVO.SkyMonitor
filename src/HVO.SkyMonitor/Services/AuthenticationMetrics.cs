using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.Services;

/// <summary>
/// Provides custom metrics for authentication and authorization events.
/// Phase 6: Metrics & Observability
/// </summary>
public sealed class AuthenticationMetrics
{
    private readonly Counter<long> _tokenRequestsCounter;
    private readonly Counter<long> _apiKeyAuthCounter;
    private readonly Counter<long> _loginAttemptsCounter;
    private readonly Histogram<double> _tokenRequestDuration;
    private readonly Histogram<double> _authenticationDuration;

    public AuthenticationMetrics(Meter meter)
    {
        _tokenRequestsCounter = meter.CreateCounter<long>(
            "auth.token_requests",
            description: "Total number of OAuth2 token requests by client and grant type");

        _apiKeyAuthCounter = meter.CreateCounter<long>(
            "auth.apikey_authentication",
            description: "Total number of API key authentication attempts by result");

        _loginAttemptsCounter = meter.CreateCounter<long>(
            "auth.login_attempts",
            description: "Total number of login attempts by result");

        _tokenRequestDuration = meter.CreateHistogram<double>(
            "auth.token_request_duration",
            unit: "ms",
            description: "Duration of OAuth2 token requests in milliseconds");

        _authenticationDuration = meter.CreateHistogram<double>(
            "auth.authentication_duration",
            unit: "ms",
            description: "Duration of authentication operations in milliseconds");
    }

    /// <summary>
    /// Records an OAuth2 token request.
    /// </summary>
    public void RecordTokenRequest(string clientId, string grantType, bool success, double durationMs)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("client_id", clientId),
            new("grant_type", grantType),
            new("result", success ? "success" : "failure")
        };

        _tokenRequestsCounter.Add(1, tags);
        _tokenRequestDuration.Record(durationMs, tags);
    }

    /// <summary>
    /// Records an API key authentication attempt.
    /// </summary>
    public void RecordApiKeyAuthentication(string? keyId, bool success, string? accessLevel = null)
    {
        var tagsList = new List<KeyValuePair<string, object?>>
        {
            new("result", success ? "success" : "failure")
        };

        if (success && !string.IsNullOrEmpty(accessLevel))
        {
            tagsList.Add(new("access_level", accessLevel));
        }

        _apiKeyAuthCounter.Add(1, tagsList.ToArray());
    }

    /// <summary>
    /// Records a user login attempt.
    /// </summary>
    public void RecordLoginAttempt(string? username, bool success, string? reason = null)
    {
        var tagsList = new List<KeyValuePair<string, object?>>
        {
            new("result", success ? "success" : "failure")
        };

        if (!success && !string.IsNullOrEmpty(reason))
        {
            tagsList.Add(new("reason", reason));
        }

        _loginAttemptsCounter.Add(1, tagsList.ToArray());
    }

    /// <summary>
    /// Records the duration of an authentication operation.
    /// </summary>
    public void RecordAuthenticationDuration(string operationType, double durationMs, bool success)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("operation", operationType),
            new("result", success ? "success" : "failure")
        };

        _authenticationDuration.Record(durationMs, tags);
    }
}
