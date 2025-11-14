using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.ZWO.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class StatusController : ControllerBase
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StatusController> _logger;

    public StatusController(TimeProvider timeProvider, ILogger<StatusController> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets basic status information.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            Service = "Camera Agent ZWO",
            Version = "1.0",
            Timestamp = _timeProvider.GetUtcNow(),
            Status = "Running"
        });
    }

    /// <summary>
    /// Gets detailed status information (requires authentication).
    /// </summary>
    [HttpGet("detailed")]
    [Authorize]
    public IActionResult GetDetailedStatus()
    {
        return Ok(new
        {
            Service = "Camera Agent ZWO",
            Version = "1.0",
            Timestamp = _timeProvider.GetUtcNow(),
            Status = "Running",
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            MachineName = Environment.MachineName,
            ProcessorCount = Environment.ProcessorCount,
            User = User.Identity?.Name
        });
    }
}
