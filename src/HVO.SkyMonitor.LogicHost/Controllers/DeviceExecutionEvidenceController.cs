using System.Diagnostics;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/execution-evidence")]
[AllowAnonymous]
internal sealed class DeviceExecutionEvidenceController(ExecutionEvidenceService service, ExecutionEvidenceCapacity capacity,
    ExecutionEvidenceTelemetry telemetry, IOptions<ExecutionEvidenceOptions> options) : ControllerBase
{
    [HttpPost("negotiate")]
    [RequestSizeLimit(GraphExecutionEvidenceLimits.MaximumNegotiationBytes)]
    public Task<IActionResult> NegotiateAsync(CancellationToken cancellationToken)
        => RunAsync(async token =>
        {
            var credentials = Credentials();
            if (!MediaType("application/json")) { return StatusCode(StatusCodes.Status415UnsupportedMediaType); }
            var bytes = await ExecutionEvidenceFraming.ReadAsync(Request.Body, Request.ContentLength,
                GraphExecutionEvidenceLimits.MaximumNegotiationBytes, token).ConfigureAwait(false);
            var parsed = GraphExecutionEvidenceJson.ParseNegotiationRequest(bytes);
            if (parsed.Value is not { } request || !bytes.AsSpan().SequenceEqual(GraphExecutionEvidenceJson.Serialize(request)))
            { throw new ExecutionEvidenceInvalidException(parsed.Validation.ReasonCode ?? GraphExecutionEvidenceReasonCodes.InvalidJson); }
            return File(GraphExecutionEvidenceJson.Serialize(await service.NegotiateAsync(credentials.Id, credentials.Key, request, token)
                .ConfigureAwait(false)), "application/json");
        }, cancellationToken);

    [HttpPost]
    [RequestSizeLimit(ExecutionEvidenceOptions.MaximumRequestBytes)]
    public Task<IActionResult> ReceiveAsync(CancellationToken cancellationToken)
        => RunAsync(async token =>
        {
            var credentials = Credentials();
            if (!MediaType(ExecutionEvidenceFraming.MediaType)) { return StatusCode(StatusCodes.Status415UnsupportedMediaType); }
            if (!Request.Headers.TryGetValue("X-HVO-Evidence-Origin", out var origin) || origin.Count != 1 ||
                !ExecutionEvidencePlanValidator.IsHash(origin[0])) { throw new ExecutionEvidenceDeniedException(); }
            var bytes = await ExecutionEvidenceFraming.ReadAsync(Request.Body, Request.ContentLength,
                ExecutionEvidenceOptions.MaximumRequestBytes, token).ConfigureAwait(false);
            var units = ExecutionEvidenceFraming.Decode(bytes);
            var feedback = await service.ReceiveAsync(credentials.Id, credentials.Key, origin[0]!, units, token).ConfigureAwait(false);
            return File(GraphExecutionEvidenceJson.Serialize(feedback), "application/json");
        }, cancellationToken);

    private (string Id, string Key) Credentials()
    {
        if (!Request.Headers.TryGetValue("X-HVO-Device-Id", out var ids) || ids.Count != 1 || string.IsNullOrWhiteSpace(ids[0]) || ids[0]!.Length > 128 ||
            !Request.Headers.TryGetValue("X-HVO-Device-Key", out var keys) || keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 512)
        { throw new DeviceRegistrationException("Invalid device credentials."); }
        return (ids[0]!, keys[0]!);
    }

    private bool MediaType(string expected)
        => Request.ContentType?.Split(';', 2)[0].Trim().Equals(expected, StringComparison.OrdinalIgnoreCase) == true;

    private async Task<IActionResult> RunAsync(Func<CancellationToken, Task<IActionResult>> action, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "unavailable";
        if (!capacity.TryEnter())
        {
            telemetry.Request("capacity", Stopwatch.GetElapsedTime(started));
            return Unavailable();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds));
        try
        {
            var result = await action(timeout.Token).ConfigureAwait(false);
            outcome = "completed";
            return result;
        }
        catch (DeviceRegistrationException) { outcome = "authentication"; return StatusCode(401); }
        catch (ExecutionEvidenceDeniedException) { outcome = "authorization"; return StatusCode(403); }
        catch (ExecutionEvidenceInvalidException exception)
        {
            outcome = "invalid";
            return StatusCode(exception.ReasonCode == GraphExecutionEvidenceReasonCodes.PayloadTooLarge ? 413 : 400,
                new ProblemDetails { Title = "Invalid execution evidence.", Detail = exception.ReasonCode });
        }
        catch (ExecutionEvidencePressureException) { outcome = "pressure"; return Unavailable(); }
        catch (SqlException) { return Unavailable(); }
        catch (DbUpdateException) { return Unavailable(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { outcome = "timeout"; return Unavailable(); }
        finally
        {
            capacity.Exit();
            telemetry.Request(outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    private ObjectResult Unavailable()
    {
        Response.Headers.RetryAfter = "5";
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "Execution evidence is temporarily unavailable." });
    }
}
