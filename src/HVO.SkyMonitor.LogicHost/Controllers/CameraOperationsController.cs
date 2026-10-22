using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

/// <summary>
/// Read-only, camera-scoped central processing and delivery history. Collections return an empty page when the
/// caller cannot read the camera; exact resources return 404 for absent or inaccessible rows alike. A continuation
/// issued for another camera, collection, subject or filter is rejected with 400 rather than reinterpreted.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
[Route("api/v1.0/observatories/{observatoryId:guid}/cameras/{cameraId:guid}")]
internal sealed class CameraOperationsController(ICameraOperationsReadService readService) : ControllerBase
{
    private const int DefaultPageSize = 25;

    [HttpGet("processing-graph-assignments")]
    public Task<IActionResult> ListAssignmentsAsync(
        Guid observatoryId, Guid cameraId, [FromQuery] string? targetHost, [FromQuery] int take = DefaultPageSize,
        [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (targetHost is null ||
            !Enum.TryParse<CentralProcessingGraphTargetHost>(targetHost, ignoreCase: false, out var host) ||
            !Enum.IsDefined(host) || !string.Equals(targetHost, host.ToString(), StringComparison.Ordinal))
        {
            return Task.FromResult<IActionResult>(BadRequest(new ProblemDetails { Title = "targetHost must be Edge or Central." }));
        }
        return ReadAsync(() => readService.ListAssignmentsAsync(User, observatoryId, cameraId, host, take, cursor,
            cancellationToken));
    }

    [HttpGet("processing-graph-executions")]
    public Task<IActionResult> ListExecutionsAsync(
        Guid observatoryId, Guid cameraId, [FromQuery] int take = DefaultPageSize, [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.ListExecutionsAsync(User, observatoryId, cameraId, take, cursor, cancellationToken));

    [HttpGet("processing-graph-executions/{executionId:guid}")]
    public Task<IActionResult> GetExecutionAsync(
        Guid observatoryId, Guid cameraId, Guid executionId, [FromQuery] int take = DefaultPageSize,
        [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.GetExecutionAsync(User, observatoryId, cameraId, executionId, take, cursor,
            cancellationToken));

    [HttpGet("jobs")]
    public Task<IActionResult> ListJobsAsync(
        Guid observatoryId, Guid cameraId, [FromQuery] int take = DefaultPageSize, [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.ListJobsAsync(User, observatoryId, cameraId, take, cursor, cancellationToken));

    [HttpGet("jobs/{jobId:guid}")]
    public Task<IActionResult> GetJobAsync(
        Guid observatoryId, Guid cameraId, Guid jobId, [FromQuery] int take = DefaultPageSize,
        CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.GetJobAsync(User, observatoryId, cameraId, jobId, take, cancellationToken));

    [HttpGet("jobs/{jobId:guid}/attempts")]
    public Task<IActionResult> ListJobAttemptsAsync(
        Guid observatoryId, Guid cameraId, Guid jobId, [FromQuery] int take = DefaultPageSize,
        [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.ListJobAttemptsAsync(User, observatoryId, cameraId, jobId, take, cursor,
            cancellationToken));

    [HttpGet("jobs/{jobId:guid}/inputs")]
    public Task<IActionResult> ListJobInputsAsync(
        Guid observatoryId, Guid cameraId, Guid jobId, [FromQuery] int take = DefaultPageSize,
        [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.ListJobInputsAsync(User, observatoryId, cameraId, jobId, take, cursor,
            cancellationToken));

    [HttpGet("jobs/{jobId:guid}/outputs")]
    public Task<IActionResult> ListJobOutputsAsync(
        Guid observatoryId, Guid cameraId, Guid jobId, [FromQuery] int take = DefaultPageSize,
        [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.ListJobOutputsAsync(User, observatoryId, cameraId, jobId, take, cursor,
            cancellationToken));

    [HttpGet("processing-graph-proposals")]
    public Task<IActionResult> ListProposalsAsync(
        Guid observatoryId, Guid cameraId, [FromQuery] int take = DefaultPageSize, [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.ListProposalsAsync(User, observatoryId, cameraId, take, cursor, cancellationToken));

    [HttpGet("processing-graph-proposals/{proposalId:guid}/facts")]
    public Task<IActionResult> ListProposalFactsAsync(
        Guid observatoryId, Guid cameraId, Guid proposalId, [FromQuery] int take = DefaultPageSize,
        [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.ListProposalFactsAsync(User, observatoryId, cameraId, proposalId, take, cursor,
            cancellationToken));

    [HttpGet("transient-events")]
    public Task<IActionResult> ListEventAssociationsAsync(
        Guid observatoryId, Guid cameraId, [FromQuery] int take = DefaultPageSize, [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
        => ReadAsync(() => readService.ListEventAssociationsAsync(User, observatoryId, cameraId, take, cursor,
            cancellationToken));

    private async Task<IActionResult> ReadAsync<T>(Func<Task<T>> read)
        where T : class?
    {
        if (!CentralArtifactCredentialAccess.HasOwnerCredential(User) ||
            string.IsNullOrWhiteSpace(CentralArtifactCredentialAccess.GetOwnerId(User)))
        {
            return Forbid();
        }
        Response.Headers.CacheControl = "private, no-store";
        try
        {
            var result = await read().ConfigureAwait(false);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"take must be between 1 and {CameraOperationsReadService.MaximumPageSize}, and a continuation " +
                    "must come from the same request."
            });
        }
    }
}
