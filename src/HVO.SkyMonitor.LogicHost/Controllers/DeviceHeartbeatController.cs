using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/heartbeat")]
[AllowAnonymous]
internal sealed class DeviceHeartbeatController(
    IDeviceHeartbeatService heartbeatService,
    ILogger<DeviceHeartbeatController> logger) : ControllerBase
{
    private static readonly Action<ILogger, string, Guid?, Guid?, Exception?> HeartbeatRejected = LoggerMessage.Define<string, Guid?, Guid?>(
        LogLevel.Warning, new EventId(2404, nameof(HeartbeatRejected)),
        "Fleet heartbeat authentication rejected; reason={ReasonCode}, claimedRegistration={ClaimedRegistrationId}, credentialOwner={CredentialOwnerRegistrationId}");
    private static readonly Action<ILogger, Guid, long, Exception?> HeartbeatConflicted = LoggerMessage.Define<Guid, long>(
        LogLevel.Warning, new EventId(2405, nameof(HeartbeatConflicted)),
        "Fleet heartbeat identity or sequence conflict for agent {AgentInstanceId} sequence {Sequence}");
    private static readonly Action<ILogger, Exception?> MalformedHeartbeat = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2407, nameof(MalformedHeartbeat)),
        "Malformed fleet heartbeat JSON was rejected");

    [HttpPost]
    [RequestSizeLimit(FleetContractJson.MaximumPayloadBytes)]
    public async Task<IActionResult> RecordHeartbeatAsync(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        FleetHeartbeatEnvelope? request = null;
        try
        {
            request = FleetContractJson.DeserializeEnvelope(Encoding.UTF8.GetBytes(body.GetRawText()))
                ?? throw new JsonException("Fleet heartbeat envelope was empty.");
            var validation = FleetContractJson.Validate(request);
            if (!validation.IsValid)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid fleet heartbeat",
                    Detail = $"The fleet heartbeat is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                    Status = StatusCodes.Status400BadRequest
                });
            }
            var result = await heartbeatService.RecordHeartbeatAsync(
                request.DeviceId,
                request.DeviceKey,
                request.Report,
                cancellationToken).ConfigureAwait(false);
            return new ContentResult
            {
                Content = Encoding.UTF8.GetString(FleetContractJson.Serialize(result)),
                ContentType = "application/json",
                StatusCode = result.Disposition == FleetHeartbeatDisposition.Advanced
                    ? StatusCodes.Status202Accepted
                    : StatusCodes.Status200OK
            };
        }
        catch (JsonException exception)
        {
            MalformedHeartbeat(logger, exception);
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid fleet heartbeat",
                Detail = "The fleet heartbeat JSON is malformed or contains unsupported values.",
                Status = StatusCodes.Status400BadRequest
            });
        }
        catch (DeviceRegistrationException exception)
        {
            HeartbeatRejected(
                logger,
                exception.ReasonCode,
                exception.ClaimedRegistrationId,
                exception.CredentialOwnerRegistrationId,
                exception);
            return Unauthorized(new ProblemDetails
            {
                Title = "Device heartbeat rejected",
                Detail = "Device credentials are invalid or inactive.",
                Status = StatusCodes.Status401Unauthorized
            });
        }
        catch (FleetHeartbeatConflictException exception)
        {
            HeartbeatConflicted(
                logger,
                request!.Report.AgentInstanceId,
                request.Report.Sequence,
                exception);
            return Conflict(new ProblemDetails
            {
                Title = "Device heartbeat conflict",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }
}
