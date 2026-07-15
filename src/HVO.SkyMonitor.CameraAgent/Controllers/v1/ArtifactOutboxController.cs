using Asp.Versioning;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Controllers.v1;

[ApiController]
[ApiVersion("1.0")]
[Authorize]
[Route("api/v{version:apiVersion}/artifact-outbox")]
public sealed class ArtifactOutboxController(
    IArtifactOutbox outbox,
    ICameraAgentConfigurationAccessor configurationAccessor,
    IOptions<CameraAgentHostOptions> hostOptions,
    IOptions<LocalIdentityOptions> localIdentityOptions) : ControllerBase
{
    [HttpPost("{idempotencyKey}/replay")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ReplayAsync(
        string idempotencyKey,
        ArtifactOutboxResolutionRequest? request,
        CancellationToken cancellationToken)
        => ResolveAsync(idempotencyKey, request, abandon: false, cancellationToken);

    [HttpPost("{idempotencyKey}/abandon")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AbandonAsync(
        string idempotencyKey,
        ArtifactOutboxResolutionRequest? request,
        CancellationToken cancellationToken)
        => ResolveAsync(idempotencyKey, request, abandon: true, cancellationToken);

    private async Task<IActionResult> ResolveAsync(
        string idempotencyKey,
        ArtifactOutboxResolutionRequest? request,
        bool abandon,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(idempotencyKey)
            || string.IsNullOrWhiteSpace(request.StorageRoot)
            || string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new ProblemDetails { Title = "Storage root, idempotency key, and reason are required" });
        }
        if (!string.Equals(
            User.Identity?.Name,
            localIdentityOptions.Value.AdminEmail,
            StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        string requestedRoot;
        try
        {
            requestedRoot = Path.GetFullPath(request.StorageRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadRequest(new ProblemDetails { Title = "Storage root is invalid" });
        }

        var config = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var configuredRoot = ArtifactOutboxDrainService.ResolveStorageRoots(config, hostOptions.Value)
            .SingleOrDefault(root => PathsEqual(root, requestedRoot));
        if (configuredRoot is null
            || await outbox.ReadAsync(configuredRoot, idempotencyKey, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFound();
        }

        var actor = User.Identity?.Name ?? "authenticated-operator";
        try
        {
            if (abandon)
            {
                await outbox.AbandonAsync(
                    configuredRoot, idempotencyKey, actor, request.Reason, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await outbox.ReplayAsync(
                    configuredRoot, idempotencyKey, actor, request.Reason, cancellationToken).ConfigureAwait(false);
            }
            return NoContent();
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            return Conflict(new ProblemDetails { Title = "Outbox record cannot be resolved from its current state", Detail = exception.Message });
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record ArtifactOutboxResolutionRequest(string StorageRoot, string Reason);
