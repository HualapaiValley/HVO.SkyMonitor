using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/artifacts")]
[Authorize(AuthenticationSchemes = "Bearer")]
internal sealed class ArtifactIngestController(IArtifactIngestService ingestService) : ControllerBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [HttpPost]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<ActionResult<DeviceUploadController.DeviceUploadResponse>> IngestAsync(IFormFile payload, [FromForm] string manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArtifactUploadManifest? parsedManifest;
        try
        {
            parsedManifest = JsonSerializer.Deserialize<ArtifactUploadManifest>(manifest, SerializerOptions);
            parsedManifest?.Validate();
        }
        catch (JsonException)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid artifact manifest" });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid artifact manifest" });
        }
        if (parsedManifest is null)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid artifact manifest" });
        }
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey)
            || !string.Equals(idempotencyKey.ToString(), parsedManifest.IdempotencyKey, StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails { Title = "Idempotency-Key does not match manifest" });
        }
        if (payload.Length != parsedManifest.ByteLength)
        {
            return BadRequest(new ProblemDetails { Title = "Payload length does not match manifest" });
        }

        await using var stream = payload.OpenReadStream();
        try
        {
            var result = await ingestService.IngestAsync(parsedManifest, stream, cancellationToken).ConfigureAwait(false);
            return Accepted(new DeviceUploadController.DeviceUploadResponse(result.RegistrationId, result.ObservatoryId, result.StorageReference, result.AcceptedAtUtc));
        }
        catch (ArtifactIntegrityException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Artifact integrity check failed", Detail = exception.Message });
        }
        catch (ArtifactIngestConflictException exception)
        {
            return Conflict(new ProblemDetails { Title = "Artifact idempotency conflict", Detail = exception.Message });
        }
    }
}
