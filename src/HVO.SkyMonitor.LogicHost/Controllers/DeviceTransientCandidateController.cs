using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/transient-candidates")]
[Authorize(Policy = "ArtifactIngest")]
internal sealed class DeviceTransientCandidateController(
    IDeviceCredentialValidator credentialValidator,
    ICentralTransientSubmissionService submissionService) : ControllerBase
{
    internal const string DeviceIdHeader = "X-HVO-Device-Id";
    internal const string DeviceKeyHeader = "X-HVO-Device-Key";

    [HttpPost]
    [RequestSizeLimit(TransientCandidateDeliveryJson.MaximumSubmissionBytes)]
    public async Task<IActionResult> SubmitAsync(CancellationToken cancellationToken)
    {
        if (!TryReadSingleHeader(DeviceIdHeader, out var deviceId)
            || !TryReadSingleHeader(DeviceKeyHeader, out var deviceKey))
        {
            return ProblemResponse(StatusCodes.Status401Unauthorized,
                CentralTransientSubmissionReasonCodes.AuthenticationRejected);
        }
        DeviceRegistration registration;
        try
        {
            registration = await credentialValidator.ValidateAsync(deviceId, deviceKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DeviceRegistrationException)
        {
            return ProblemResponse(StatusCodes.Status401Unauthorized,
                CentralTransientSubmissionReasonCodes.AuthenticationRejected);
        }

        var body = await ReadBoundedAsync(Request.Body, Request.ContentLength,
            TransientCandidateDeliveryJson.MaximumSubmissionBytes, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return ProblemResponse(StatusCodes.Status413PayloadTooLarge,
                CentralTransientSubmissionReasonCodes.PayloadTooLarge);
        }
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(body));
        var parsed = TransientCandidateDeliveryJson.ParseSubmission(body);
        var envelope = parsed.Value;
        if (envelope is null || !body.AsSpan().SequenceEqual(TransientCandidateDeliveryJson.Serialize(envelope)))
        {
            await submissionService.RecordRejectedAsync(
                registration,
                payloadSha256,
                parsed.Validation.ReasonCode ?? CentralTransientSubmissionReasonCodes.InvalidContract,
                candidateId: null,
                eventId: null,
                claimedSubmissionIdentitySha256: null,
                cancellationToken).ConfigureAwait(false);
            return ProblemResponse(StatusCodes.Status400BadRequest,
                parsed.Validation.ReasonCode ?? CentralTransientSubmissionReasonCodes.InvalidContract);
        }
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey)
            || idempotencyKey.Count != 1
            || !string.Equals(idempotencyKey[0], envelope.SubmissionIdentitySha256, StringComparison.Ordinal))
        {
            await submissionService.RecordRejectedAsync(
                registration,
                payloadSha256,
                CentralTransientSubmissionReasonCodes.IdentityConflict,
                envelope.CandidateId,
                envelope.EventId,
                envelope.SubmissionIdentitySha256,
                cancellationToken).ConfigureAwait(false);
            return ProblemResponse(StatusCodes.Status409Conflict,
                CentralTransientSubmissionReasonCodes.IdentityConflict);
        }

        try
        {
            var result = await submissionService.SubmitAsync(
                registration, envelope, payloadSha256, cancellationToken).ConfigureAwait(false);
            return new ContentResult
            {
                Content = Encoding.UTF8.GetString(
                    TransientCandidateDeliveryJson.Serialize(result.Acknowledgement)),
                ContentType = "application/json",
                StatusCode = result.Acknowledgement.Disposition == TransientCandidateSubmissionDisposition.Accepted
                    ? StatusCodes.Status202Accepted
                    : StatusCodes.Status200OK
            };
        }
        catch (CentralTransientSubmissionRejectedException exception)
        {
            return ProblemResponse(StatusFor(exception.Kind), exception.ReasonCode);
        }
    }

    private bool TryReadSingleHeader(string name, out string value)
    {
        if (Request.Headers.TryGetValue(name, out var values) && values.Count == 1
            && !string.IsNullOrWhiteSpace(values[0]))
        {
            value = values[0]!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private ObjectResult ProblemResponse(int status, string reasonCode)
    {
        var problem = new ProblemDetails
        {
            Title = "Hybrid transient submission rejected",
            Detail = "The submission could not be accepted.",
            Status = status
        };
        problem.Extensions["reasonCode"] = reasonCode;
        return StatusCode(status, problem);
    }

    private static int StatusFor(CentralTransientSubmissionRejectionKind kind) => kind switch
    {
        CentralTransientSubmissionRejectionKind.Forbidden => StatusCodes.Status403Forbidden,
        CentralTransientSubmissionRejectionKind.NotFound => StatusCodes.Status404NotFound,
        CentralTransientSubmissionRejectionKind.Unavailable => 425,
        CentralTransientSubmissionRejectionKind.Integrity => StatusCodes.Status422UnprocessableEntity,
        CentralTransientSubmissionRejectionKind.Timeout => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status409Conflict
    };

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
