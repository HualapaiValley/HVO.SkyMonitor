using System.Text;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/environmental-observations")]
[AllowAnonymous]
internal sealed class DeviceEnvironmentalObservationController(
    IDeviceCredentialValidator credentialValidator,
    ApplicationDbContext dbContext,
    IEnvironmentalObservationIngestService ingestService,
    EnvironmentalObservationTelemetry telemetry,
    ILogger<DeviceEnvironmentalObservationController> logger) : ControllerBase
{
    private static readonly Action<ILogger, string, Exception?> Rejected = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2515, nameof(Rejected)),
        "Environmental observation delivery was rejected with reason {ReasonCode}");

    [HttpPost]
    [RequestSizeLimit(EnvironmentalObservationDeliveryJson.MaximumEnvelopeBytes)]
    public async Task<IActionResult> IngestAsync(CancellationToken cancellationToken)
    {
        var body = await ReadBoundedAsync(
            Request.Body,
            Request.ContentLength,
            EnvironmentalObservationDeliveryJson.MaximumEnvelopeBytes,
            cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            telemetry.RecordValidation("delivery-size");
            return ProblemResponse(
                StatusCodes.Status413PayloadTooLarge,
                "Environmental observation payload is too large.",
                EnvironmentalObservationReasonCodes.PayloadTooLarge);
        }
        var parsed = EnvironmentalObservationDeliveryJson.ParseEnvelope(body);
        if (parsed.Value is not { } envelope)
        {
            telemetry.RecordValidation("delivery-contract");
            var status = parsed.Validation.ReasonCode == EnvironmentalObservationReasonCodes.PayloadTooLarge
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status400BadRequest;
            return ProblemResponse(
                status,
                "Environmental observation delivery JSON is invalid.",
                parsed.Validation.ReasonCode ?? EnvironmentalObservationReasonCodes.InvalidJson);
        }

        try
        {
            var registration = await credentialValidator.ValidateAsync(
                envelope.DeviceId,
                envelope.DeviceKey,
                cancellationToken).ConfigureAwait(false);
            if (registration.DevicePublicId is null ||
                envelope.Observation.Target.AgentId is not { } targetAgentId ||
                !await IsAuthenticatedDeviceTargetAsync(
                    registration,
                    envelope.Observation.Target.SiteId,
                    targetAgentId,
                    cancellationToken).ConfigureAwait(false))
            {
                telemetry.RecordConflict("delivery-agent");
                Rejected(logger, "environment.target-agent-binding", null);
                return ProblemResponse(
                    StatusCodes.Status409Conflict,
                    "Environmental observation target does not match the authenticated device.",
                    "environment.target-agent-binding");
            }

            var result = await ingestService.IngestAsync(envelope.Observation, cancellationToken).ConfigureAwait(false);
            var acknowledgement = new EnvironmentalObservationAcknowledgement(
                EnvironmentalObservationAcknowledgement.CurrentSchemaVersion,
                envelope.Observation.ObservationId,
                EnvironmentalObservationJson.ComputeSourceIdentitySha256(envelope.Observation),
                result.ContentSha256,
                result.ReceivedAtUtc,
                result.Disposition == EnvironmentalObservationIngestDisposition.Accepted
                    ? EnvironmentalObservationDeliveryDisposition.Accepted
                    : EnvironmentalObservationDeliveryDisposition.Duplicate);
            return new ContentResult
            {
                Content = Encoding.UTF8.GetString(EnvironmentalObservationDeliveryJson.Serialize(acknowledgement)),
                ContentType = "application/json",
                StatusCode = result.Disposition == EnvironmentalObservationIngestDisposition.Accepted
                    ? StatusCodes.Status202Accepted
                    : StatusCodes.Status200OK
            };
        }
        catch (DeviceRegistrationException)
        {
            Rejected(logger, "environment.authentication-rejected", null);
            return ProblemResponse(
                StatusCodes.Status401Unauthorized,
                "Device credentials are invalid or inactive.",
                "environment.authentication-rejected");
        }
        catch (EnvironmentalObservationConflictException)
        {
            Rejected(logger, "environment.content-conflict", null);
            return ProblemResponse(
                StatusCodes.Status409Conflict,
                "Environmental observation identity or historical binding conflicts with durable state.",
                "environment.content-conflict");
        }
        catch (ArgumentException)
        {
            Rejected(logger, "environment.invalid-contract", null);
            return ProblemResponse(
                StatusCodes.Status400BadRequest,
                "Environmental observation contract is invalid.",
                "environment.invalid-contract");
        }
    }

    private async Task<bool> IsAuthenticatedDeviceTargetAsync(
        DeviceRegistration registration,
        Guid targetSiteId,
        Guid targetAgentId,
        CancellationToken cancellationToken)
    {
        if (registration.DevicePublicId == targetAgentId && registration.ObservatoryId == targetSiteId)
        {
            return true;
        }
        var historicalRegistrationIds = dbContext.DeviceRegistrations
            .Where(candidate => candidate.DeviceId == registration.DeviceId)
            .Select(candidate => candidate.Id);
        return await dbContext.DeviceRegistrations
            .AsNoTracking()
            .AnyAsync(
                candidate => candidate.DeviceId == registration.DeviceId &&
                    candidate.DevicePublicId == targetAgentId && candidate.ObservatoryId == targetSiteId,
                cancellationToken).ConfigureAwait(false) || await dbContext.CentralFrames
            .AsNoTracking()
            .AnyAsync(
                frame => frame.DevicePublicId == targetAgentId && frame.ObservatoryId == targetSiteId &&
                    historicalRegistrationIds.Contains(frame.RegistrationId),
                cancellationToken).ConfigureAwait(false) || await dbContext.DeviceRigProfiles
            .AsNoTracking()
            .AnyAsync(
                profile => profile.DevicePublicId == targetAgentId && profile.ObservatoryId == targetSiteId &&
                    historicalRegistrationIds.Contains(profile.RegistrationId),
                cancellationToken).ConfigureAwait(false);
    }

    private ObjectResult ProblemResponse(int status, string detail, string reasonCode)
    {
        var problem = new ProblemDetails
        {
            Title = "Environmental observation delivery rejected",
            Detail = detail,
            Status = status
        };
        problem.Extensions["reasonCode"] = reasonCode;
        return StatusCode(status, problem);
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        Stream source,
        long? contentLength,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (contentLength > maximumBytes)
        {
            return null;
        }
        using var destination = new MemoryStream(Math.Min(maximumBytes, 4096));
        var buffer = new byte[4096];
        while (destination.Length <= maximumBytes)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return destination.ToArray();
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return null;
    }
}
