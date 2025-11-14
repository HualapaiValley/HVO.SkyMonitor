using System.Security.Claims;
using Asp.Versioning;
using HVO;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.CameraAgent.Simulator.Models.Sample;
using HVO.SkyMonitor.CameraAgent.Simulator.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Controllers.v1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sample")]
public sealed class SampleController : ControllerBase
{
    private readonly ISampleStatusService _sampleStatusService;
    private readonly ILogger<SampleController> _logger;

    public SampleController(ISampleStatusService sampleStatusService, ILogger<SampleController> logger)
    {
        _sampleStatusService = sampleStatusService;
        _logger = logger;
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<SampleStatusResponse> GetStatus()
    {
        var result = _sampleStatusService.GetStatus();

        if (result.IsSuccessful)
        {
            var response = result.Value;
            _logger.LogInformation("Returning sample status generated at {TimestampUtc}.", response.RetrievedAtUtc);
            return Ok(response);
        }

        var exception = result.Error ?? new InvalidOperationException("Unknown error while retrieving sample status.");
        _logger.LogError(exception, "Failed to produce sample status response.");
        return Problem(
            title: "Unable to retrieve sample status.",
            detail: exception.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }

    [HttpGet("authonly")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyOrCookie)]
    public ActionResult<SampleAuthenticatedResponse> AuthOnly()
    {
        var result = _sampleStatusService.GetAuthenticatedStatus(User);

        if (result.IsSuccessful)
        {
            var response = result.Value;
            _logger.LogInformation(
                "Returning authenticated sample payload for {UserName} with scheme {Scheme}.",
                response.UserName,
                response.AuthenticationType);
            return Ok(response);
        }

        var exception = result.Error ?? new InvalidOperationException("Unknown error while building authenticated response.");
        _logger.LogError(exception, "Failed to produce authenticated sample response.");
        return Problem(
            title: "Unable to complete authenticated request.",
            detail: exception.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
}
