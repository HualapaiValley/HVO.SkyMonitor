using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

internal sealed partial class ExecutionEvidenceController
{
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("/api/internal/observatories/{observatoryId:guid}/cameras/{logicalCameraId:guid}/installations/{installationId:guid}/execution-evidence/bindings/{bindingId:guid}/units/{unitId:long}/artifacts")]
    public Task<IActionResult> UnitArtifactsAsync(Guid observatoryId, Guid logicalCameraId, Guid installationId, Guid bindingId, long unitId,
        [FromQuery] string? cursor = null, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => GuardAsync(async () => Result(await service.ListUnitArtifactsAsync(observatoryId, logicalCameraId, installationId,
            bindingId, unitId, Actor, Scope, cursor, take, cancellationToken).ConfigureAwait(false)));

    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("/api/internal/observatories/{observatoryId:guid}/cameras/{logicalCameraId:guid}/installations/{installationId:guid}/execution-evidence/bindings/{bindingId:guid}/units/{unitId:long}/artifacts/{artifactReferenceId:long}/observations")]
    public Task<IActionResult> ArtifactObservationsAsync(Guid observatoryId, Guid logicalCameraId, Guid installationId, Guid bindingId, long unitId,
        long artifactReferenceId, [FromQuery] Guid captureId, [FromQuery] Guid artifactId, CancellationToken cancellationToken = default)
        => GuardAsync(async () => Result(await service.GetArtifactReconciliationAsync(observatoryId, logicalCameraId, installationId,
            bindingId, unitId, artifactReferenceId, captureId, artifactId, Actor, Scope, cancellationToken).ConfigureAwait(false)));

    // installationId is LogicalCameraInstallation.Id, never InstallationPublicId.
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("/api/internal/observatories/{observatoryId:guid}/cameras/{logicalCameraId:guid}/installations/{installationId:guid}/execution-evidence/bindings")]
    public Task<IActionResult> InstallationBindingsAsync(Guid observatoryId, Guid logicalCameraId, Guid installationId,
        CancellationToken cancellationToken)
        => GuardAsync(async () => Result(await service.ListInstallationBindingsAsync(
            observatoryId, logicalCameraId, installationId, Actor, Scope, cancellationToken).ConfigureAwait(false)));
}
