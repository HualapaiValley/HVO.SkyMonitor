using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

internal sealed partial class ExecutionEvidenceController
{
    // installationId is LogicalCameraInstallation.Id, never InstallationPublicId.
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("/api/internal/observatories/{observatoryId:guid}/cameras/{logicalCameraId:guid}/installations/{installationId:guid}/execution-evidence/bindings")]
    public Task<IActionResult> InstallationBindingsAsync(Guid observatoryId, Guid logicalCameraId, Guid installationId,
        CancellationToken cancellationToken)
        => GuardAsync(async () => Result(await service.ListInstallationBindingsAsync(
            observatoryId, logicalCameraId, installationId, Actor, Scope, cancellationToken).ConfigureAwait(false)));
}
