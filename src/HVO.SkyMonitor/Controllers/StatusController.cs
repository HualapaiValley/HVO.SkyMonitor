using Asp.Versioning;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.Controllers;

/// <summary>
/// Status and health information for the SkyMonitor application.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class StatusController : ControllerBase
{
    private readonly ILogger<StatusController> _logger;
    private readonly TimeProvider _timeProvider;

    public StatusController(ILogger<StatusController> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Get the current status of the SkyMonitor system.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            Service = "HVO.SkyMonitor",
            Status = "Running",
            Timestamp = _timeProvider.GetUtcNow(),
            Version = "1.0.0"
        });
    }

    /// <summary>
    /// Get detailed system information (requires authentication).
    /// </summary>
    [HttpGet("detailed")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    public IActionResult GetDetailedStatus()
    {
        return Ok(new
        {
            Service = "HVO.SkyMonitor",
            Status = "Running",
            Timestamp = _timeProvider.GetUtcNow(),
            Version = "1.0.0",
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown",
            MachineName = Environment.MachineName,
            User = User.Identity?.Name ?? "Anonymous"
        });
    }
}
