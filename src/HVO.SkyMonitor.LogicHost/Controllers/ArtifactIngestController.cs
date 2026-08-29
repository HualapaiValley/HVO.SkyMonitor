using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/artifacts")]
[Authorize(Policy = "ArtifactIngest")]
internal sealed class ArtifactIngestController(
    IArtifactIngestService ingestService,
    CentralIngestTelemetry telemetry) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<ActionResult<ArtifactUploadAcknowledgement>> IngestAsync(IFormFile payload, [FromForm] string manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var parseResult = CaptureContractJson.ParseManifest(manifestBytes);
        var productParseResult = !parseResult.IsValid
            ? StructuredProcessingProductManifestJson.Parse(manifestBytes)
            : null;
        if ((!parseResult.IsValid || parseResult.Document is null) &&
            (productParseResult is null || !productParseResult.IsValid || productParseResult.Manifest is null))
        {
            telemetry.RecordValidation("unknown", "rejected");
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid artifact manifest",
                Detail = $"{parseResult.Validation.ReasonCode} at {parseResult.Validation.FieldPath}"
            });
        }
        if ((productParseResult?.Manifest?.Descriptor.SourceCapture ?? parseResult.Document?.Manifest.Descriptor)?.Location is null)
        {
            return BadRequest(new ProblemDetails { Title = "Capture location provenance is required" });
        }
        var delivery = productParseResult?.Manifest is { } productManifest
            ? ArtifactIngestManifest.Create(productManifest)
            : ArtifactIngestManifest.Create(parseResult.Document!);
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey)
            || idempotencyKey.Count != 1
            || !string.Equals(idempotencyKey[0], delivery.IdempotencyKey, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new ProblemDetails { Title = "Idempotency-Key does not match manifest" });
        }
        if (payload.Length != delivery.ByteLength)
        {
            return BadRequest(new ProblemDetails { Title = "Payload length does not match manifest" });
        }

        await using var stream = payload.OpenReadStream();
        try
        {
            var result = productParseResult?.Manifest is { } structuredProduct
                ? await ingestService.IngestAsync(structuredProduct, stream, cancellationToken).ConfigureAwait(false)
                : await ingestService.IngestAsync(parseResult.Document!, stream, cancellationToken).ConfigureAwait(false);
            if (!result.ReadyForAcknowledgement)
            {
                Response.Headers.RetryAfter = "5";
                return StatusCode(425, new ProblemDetails
                {
                    Title = "Artifact is awaiting a required reconstruction reference",
                    Detail = "The payload is durable and will be reconciled after its exact historical profile and source lineage are available."
                });
            }
            return Accepted(new ArtifactUploadAcknowledgement(
                ArtifactUploadAcknowledgement.CurrentSchemaVersion,
                delivery.IdempotencyKey,
                delivery.ArtifactId,
                delivery.ChecksumSha256.ToUpperInvariant(),
                delivery.ByteLength,
                result.Receipt.AcceptedAtUtc,
                delivery.SchemaVersion));
        }
        catch (ArtifactIntegrityException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Artifact integrity check failed", Detail = exception.Message });
        }
        catch (ArtifactIngestConflictException exception)
        {
            return Conflict(new ProblemDetails { Title = "Artifact idempotency conflict", Detail = exception.Message });
        }
        catch (DeviceRegistrationException exception)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Agent is not registered for artifact ingestion",
                Detail = exception.Message
            });
        }
    }

    [HttpPost("status")]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<ActionResult<ArtifactUploadAcknowledgement>> StatusAsync(
        [FromBody] JsonElement manifest,
        CancellationToken cancellationToken)
    {
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.GetRawText());
        var parseResult = CaptureContractJson.ParseManifest(manifestBytes);
        var productParseResult = !parseResult.IsValid
            ? StructuredProcessingProductManifestJson.Parse(manifestBytes)
            : null;
        if ((!parseResult.IsValid || parseResult.Document is null) &&
            (productParseResult is null || !productParseResult.IsValid || productParseResult.Manifest is null))
        {
            telemetry.RecordValidation("unknown", "rejected");
            return BadRequest(new ProblemDetails { Title = "Invalid artifact manifest" });
        }
        if ((productParseResult?.Manifest?.Descriptor.SourceCapture ?? parseResult.Document?.Manifest.Descriptor)?.Location is null)
        {
            return BadRequest(new ProblemDetails { Title = "Capture location provenance is required" });
        }
        var delivery = productParseResult?.Manifest is { } productManifest
            ? ArtifactIngestManifest.Create(productManifest)
            : ArtifactIngestManifest.Create(parseResult.Document!);
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey)
            || idempotencyKey.Count != 1
            || !string.Equals(idempotencyKey[0], delivery.IdempotencyKey, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new ProblemDetails { Title = "Idempotency-Key does not match manifest" });
        }

        try
        {
            var result = productParseResult?.Manifest is { } structuredProduct
                ? await ingestService.CheckStatusAsync(structuredProduct, cancellationToken).ConfigureAwait(false)
                : await ingestService.CheckStatusAsync(parseResult.Document!, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return NotFound(new ProblemDetails { Title = "Artifact payload must be uploaded" });
            }
            if (!result.ReadyForAcknowledgement)
            {
                Response.Headers.RetryAfter = "5";
                return StatusCode(425, new ProblemDetails
                {
                    Title = "Artifact is awaiting a required reconstruction reference"
                });
            }
            return Accepted(new ArtifactUploadAcknowledgement(
                ArtifactUploadAcknowledgement.CurrentSchemaVersion,
                delivery.IdempotencyKey,
                delivery.ArtifactId,
                delivery.ChecksumSha256.ToUpperInvariant(),
                delivery.ByteLength,
                result.Receipt.AcceptedAtUtc,
                delivery.SchemaVersion));
        }
        catch (ArtifactIntegrityException)
        {
            return NotFound(new ProblemDetails { Title = "Artifact payload must be uploaded again" });
        }
        catch (ArtifactIngestConflictException exception)
        {
            return Conflict(new ProblemDetails { Title = "Artifact idempotency conflict", Detail = exception.Message });
        }
        catch (DeviceRegistrationException exception)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Agent is not registered for artifact ingestion",
                Detail = exception.Message
            });
        }
    }
}
