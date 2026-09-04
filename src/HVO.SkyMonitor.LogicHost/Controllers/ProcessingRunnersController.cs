using System.Net;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace HVO.SkyMonitor.LogicHost.Controllers;

/// <summary>
/// The <c>processing-runner-v1</c> endpoint. Bodies are read and written with the protocol serializer (camel case,
/// string enums, unmapped members rejected) rather than the host's default JSON options so both sides share one
/// contract, and every failure carries a <see cref="ProcessingRunnerProblem"/> with a stable reason code.
/// </summary>
[ApiController]
[Route("api/v1.0/processing-runners/{runnerId}")]
[Authorize(Policy = "ProcessingRunner")]
internal sealed class ProcessingRunnersController(
    ICentralProcessingRunnerRegistry registry,
    ICentralProcessingRunnerJobService jobs) : ControllerBase
{
    private const long MaximumCompletionBodyBytes = ProcessingRunnerProtocol.MaximumTransferBytes
        + ProcessingRunnerProtocol.MaximumMetadataBytes + 64 * 1024;

    [HttpPut]
    public async Task<IActionResult> RegisterAsync(string runnerId, CancellationToken cancellationToken)
    {
        var subject = Subject();
        if (subject is null)
        {
            return Forbid();
        }
        try
        {
            var request = await ReadBodyAsync<ProcessingRunnerRegistrationRequest>(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(request.RunnerId, runnerId, StringComparison.Ordinal))
            {
                return Problem(HttpStatusCode.BadRequest, ProcessingRunnerReasonCodes.InvalidRunnerId,
                    "The route runner id must match the registration runner id.");
            }
            var response = await registry.RegisterAsync(subject, request, cancellationToken).ConfigureAwait(false);
            return Json(response);
        }
        catch (ProcessingRunnerProtocolException exception)
        {
            return Problem(HttpStatusCode.BadRequest, exception.ReasonCode, exception.Message);
        }
        catch (CentralProcessingRunnerRejectedException exception)
        {
            return Problem(exception);
        }
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> HeartbeatAsync(string runnerId, CancellationToken cancellationToken)
    {
        var subject = Subject();
        if (subject is null)
        {
            return Forbid();
        }
        try
        {
            var request = await ReadBodyAsync<ProcessingRunnerHeartbeatRequest>(cancellationToken).ConfigureAwait(false);
            return Json(await registry.HeartbeatAsync(subject, runnerId, request, cancellationToken).ConfigureAwait(false));
        }
        catch (ProcessingRunnerProtocolException exception)
        {
            return Problem(HttpStatusCode.BadRequest, exception.ReasonCode, exception.Message);
        }
        catch (CentralProcessingRunnerRejectedException exception)
        {
            return Problem(exception);
        }
    }

    [HttpPost("claims")]
    public async Task<IActionResult> ClaimAsync(string runnerId, CancellationToken cancellationToken)
    {
        var subject = Subject();
        if (subject is null)
        {
            return Forbid();
        }
        try
        {
            var request = await ReadBodyAsync<ProcessingRunnerClaimRequest>(cancellationToken).ConfigureAwait(false);
            var runner = await registry.ResolveOwnedAsync(subject, runnerId, cancellationToken).ConfigureAwait(false);
            var claim = await jobs.ClaimAsync(runner, request, cancellationToken).ConfigureAwait(false);
            return claim is null ? NoContent() : Json(claim);
        }
        catch (ProcessingRunnerProtocolException exception)
        {
            return Problem(HttpStatusCode.BadRequest, exception.ReasonCode, exception.Message);
        }
        catch (CentralProcessingRunnerRejectedException exception)
        {
            return Problem(exception);
        }
    }

    [HttpPost("jobs/{jobId:guid}/lease")]
    public async Task<IActionResult> RenewAsync(string runnerId, Guid jobId, CancellationToken cancellationToken)
    {
        var subject = Subject();
        if (subject is null)
        {
            return Forbid();
        }
        try
        {
            var request = await ReadBodyAsync<ProcessingRunnerLeaseRenewalRequest>(cancellationToken).ConfigureAwait(false);
            var runner = await registry.ResolveOwnedAsync(subject, runnerId, cancellationToken).ConfigureAwait(false);
            return Json(await jobs.RenewAsync(runner, jobId, request.LeaseToken, cancellationToken).ConfigureAwait(false));
        }
        catch (ProcessingRunnerProtocolException exception)
        {
            return Problem(HttpStatusCode.BadRequest, exception.ReasonCode, exception.Message);
        }
        catch (CentralProcessingRunnerRejectedException exception)
        {
            return Problem(exception);
        }
        catch (CentralDerivativeLeaseCanceledException)
        {
            return Problem(HttpStatusCode.Gone, ProcessingRunnerReasonCodes.LeaseCanceled, "The job was canceled.");
        }
        catch (CentralDerivativeJobStateException exception)
        {
            return Problem(HttpStatusCode.Conflict, ProcessingRunnerReasonCodes.LeaseStale, exception.Message);
        }
    }

    [HttpPost("jobs/{jobId:guid}/completion")]
    [RequestSizeLimit(MaximumCompletionBodyBytes)]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> CompleteAsync(string runnerId, Guid jobId, CancellationToken cancellationToken)
    {
        var subject = Subject();
        if (subject is null)
        {
            return Forbid();
        }
        try
        {
            var (request, payloads) = await ReadCompletionAsync(cancellationToken).ConfigureAwait(false);
            var runner = await registry.ResolveOwnedAsync(subject, runnerId, cancellationToken).ConfigureAwait(false);
            return Json(await jobs.CompleteAsync(runner, jobId, request, payloads, cancellationToken).ConfigureAwait(false));
        }
        catch (ProcessingRunnerProtocolException exception)
        {
            return Problem(HttpStatusCode.BadRequest, exception.ReasonCode, exception.Message);
        }
        catch (CentralProcessingRunnerRejectedException exception)
        {
            return Problem(exception);
        }
        catch (CentralDerivativeJobStateException exception)
        {
            return Problem(HttpStatusCode.Conflict, ProcessingRunnerReasonCodes.LeaseStale, exception.Message);
        }
    }

    [HttpPost("jobs/{jobId:guid}/failure")]
    public async Task<IActionResult> FailAsync(string runnerId, Guid jobId, CancellationToken cancellationToken)
    {
        var subject = Subject();
        if (subject is null)
        {
            return Forbid();
        }
        try
        {
            var request = await ReadBodyAsync<ProcessingRunnerFailureRequest>(cancellationToken).ConfigureAwait(false);
            var runner = await registry.ResolveOwnedAsync(subject, runnerId, cancellationToken).ConfigureAwait(false);
            await jobs.FailAsync(runner, jobId, request, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (ProcessingRunnerProtocolException exception)
        {
            return Problem(HttpStatusCode.BadRequest, exception.ReasonCode, exception.Message);
        }
        catch (CentralProcessingRunnerRejectedException exception)
        {
            return Problem(exception);
        }
        catch (CentralDerivativeJobStateException exception)
        {
            return Problem(HttpStatusCode.Conflict, ProcessingRunnerReasonCodes.LeaseStale, exception.Message);
        }
    }

    [HttpDelete]
    public async Task<IActionResult> RetireAsync(string runnerId, CancellationToken cancellationToken)
    {
        var subject = Subject();
        if (subject is null)
        {
            return Forbid();
        }
        try
        {
            await registry.RetireAsync(subject, runnerId, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (CentralProcessingRunnerRejectedException exception)
        {
            return Problem(exception);
        }
    }

    private string? Subject()
    {
        var subject = CentralArtifactCredentialAccess.GetSubject(User);
        return string.IsNullOrWhiteSpace(subject) || subject.Length > 256 ? null : subject;
    }

    private async Task<T> ReadBodyAsync<T>(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(Request.Body, buffer, ProcessingRunnerProtocol.MaximumMetadataBytes, cancellationToken)
            .ConfigureAwait(false);
        return ProcessingRunnerProjection.DeserializeMetadata<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private async Task<(ProcessingRunnerCompletionRequest Request, IReadOnlyList<ReadOnlyMemory<byte>> Payloads)>
        ReadCompletionAsync(CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(contentType.Boundary.Value))
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCompletion, "A completion must be multipart/form-data.");
        }
        var reader = new MultipartReader(HeaderUtilities.RemoveQuotes(contentType.Boundary).Value!, Request.Body)
        {
            BodyLengthLimit = MaximumCompletionBodyBytes
        };
        ProcessingRunnerCompletionRequest? request = null;
        var payloads = new SortedDictionary<int, ReadOnlyMemory<byte>>();
        long total = 0;
        while (await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProcessingRunnerProtocolException(
                    ProcessingRunnerReasonCodes.InvalidCompletion, "A completion part is not form-data.");
            }
            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty;
            if (string.Equals(name, ProcessingRunnerProtocol.OutcomePartName, StringComparison.Ordinal))
            {
                if (request is not null)
                {
                    throw new ProcessingRunnerProtocolException(
                        ProcessingRunnerReasonCodes.InvalidCompletion, "A completion carries exactly one outcome part.");
                }
                using var buffer = new MemoryStream();
                await CopyBoundedAsync(section.Body, buffer, ProcessingRunnerProtocol.MaximumMetadataBytes, cancellationToken)
                    .ConfigureAwait(false);
                request = ProcessingRunnerProjection.DeserializeMetadata<ProcessingRunnerCompletionRequest>(
                    buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                continue;
            }
            if (!name.StartsWith(ProcessingRunnerProtocol.PayloadPartPrefix, StringComparison.Ordinal)
                || !int.TryParse(name.AsSpan(ProcessingRunnerProtocol.PayloadPartPrefix.Length), out var ordinal)
                || ordinal < 0 || ordinal >= ProcessingRunnerProtocol.MaximumProductCount
                || payloads.ContainsKey(ordinal))
            {
                throw new ProcessingRunnerProtocolException(
                    ProcessingRunnerReasonCodes.InvalidCompletion, $"Completion part '{name}' is not a valid payload part.");
            }
            var payload = new MemoryStream();
            await using (payload.ConfigureAwait(false))
            {
                await CopyBoundedAsync(
                    section.Body, payload, ProcessingRunnerProtocol.MaximumTransferBytes - total, cancellationToken)
                    .ConfigureAwait(false);
                total += payload.Length;
                payloads[ordinal] = payload.ToArray();
            }
        }
        if (request is null)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCompletion, "A completion requires an outcome part.");
        }
        var ordered = new ReadOnlyMemory<byte>[payloads.Count];
        var expected = 0;
        foreach (var pair in payloads)
        {
            if (pair.Key != expected++)
            {
                throw new ProcessingRunnerProtocolException(
                    ProcessingRunnerReasonCodes.InvalidCompletion, "Completion payload parts must be contiguous.");
            }
            ordered[pair.Key] = pair.Value;
        }
        return (request, ordered);
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        MemoryStream destination,
        long limit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
            if (destination.Length + read > limit)
            {
                throw new ProcessingRunnerProtocolException(
                    ProcessingRunnerReasonCodes.TransferTooLarge, "The request body exceeds the protocol limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private ContentResult Json<T>(T value)
        => new()
        {
            StatusCode = StatusCodes.Status200OK,
            ContentType = "application/json",
            Content = System.Text.Encoding.UTF8.GetString(ProcessingRunnerProjection.SerializeMetadata(value))
        };

    private ContentResult Problem(HttpStatusCode statusCode, string reasonCode, string message)
        => new()
        {
            StatusCode = (int)statusCode,
            ContentType = "application/json",
            Content = System.Text.Encoding.UTF8.GetString(
                ProcessingRunnerProjection.SerializeMetadata(new ProcessingRunnerProblem(reasonCode, message)))
        };

    private ContentResult Problem(CentralProcessingRunnerRejectedException exception)
        => Problem(exception.ReasonCode switch
        {
            ProcessingRunnerReasonCodes.RunnersDisabled or ProcessingRunnerReasonCodes.Unavailable
                => HttpStatusCode.ServiceUnavailable,
            ProcessingRunnerReasonCodes.RegistrationRequired => HttpStatusCode.NotFound,
            ProcessingRunnerReasonCodes.RegistrationNotOwned or ProcessingRunnerReasonCodes.JobClassNotClaimable
                or ProcessingRunnerReasonCodes.CapabilityMismatch => HttpStatusCode.Forbidden,
            ProcessingRunnerReasonCodes.RegistrationRetired => HttpStatusCode.Gone,
            ProcessingRunnerReasonCodes.LeaseStale => HttpStatusCode.Conflict,
            ProcessingRunnerReasonCodes.LeaseCanceled => HttpStatusCode.Gone,
            ProcessingRunnerReasonCodes.TransferTooLarge => HttpStatusCode.RequestEntityTooLarge,
            _ => HttpStatusCode.BadRequest
        }, exception.ReasonCode, exception.Message);
}
