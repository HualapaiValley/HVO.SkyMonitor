using System.Security.Claims;
using HVO;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.ZWO.Models.Sample;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.ZWO.Services;

/// <summary>
/// Default implementation of <see cref="ISampleStatusService"/> that composes status responses using
/// the application's logging and time infrastructure.
/// </summary>
public sealed class SampleStatusService(TimeProvider timeProvider, ILogger<SampleStatusService> logger) : ISampleStatusService
{
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<SampleStatusService> _logger = logger;

    public Result<SampleStatusResponse> GetStatus()
    {
        var timestamp = _timeProvider.GetUtcNow();
        var response = new SampleStatusResponse("Sample status OK", timestamp);

        _logger.LogDebug("Generated sample status response at {TimestampUtc}.", timestamp);
        return response;
    }

    public Result<SampleAuthenticatedResponse> GetAuthenticatedStatus(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            _logger.LogWarning("Attempted to build authenticated sample response without an authenticated identity.");
            return Result<SampleAuthenticatedResponse>.Failure(new InvalidOperationException("No authenticated identity is available."));
        }

        var userName = principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userName))
        {
            userName = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        }

        var authenticationType = principal.FindFirstValue(ApiKeyClaims.AuthenticationType) ?? IdentityConstants.ApplicationScheme;
        var accessLevel = principal.FindFirstValue(ApiKeyClaims.AccessLevel) ?? "InteractiveUser";

        var response = new SampleAuthenticatedResponse(
            "Authenticated request.",
            userName,
            authenticationType,
            accessLevel);

        _logger.LogInformation(
            "Sample authenticated response prepared for user {UserName} using scheme {AuthenticationScheme} with access {AccessLevel}.",
            userName,
            authenticationType,
            accessLevel);

        return response;
    }
}
