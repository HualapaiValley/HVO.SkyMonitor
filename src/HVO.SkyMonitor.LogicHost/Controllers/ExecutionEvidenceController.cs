using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/internal/execution-evidence/bindings")]
internal sealed partial class ExecutionEvidenceController(ExecutionEvidenceService service) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
    [RequestSizeLimit(4096)]
    public Task<IActionResult> CreateAsync(ExecutionEvidenceBindingRequest request, CancellationToken cancellationToken)
        => GuardAsync(async () =>
        {
            if (!CentralArtifactCredentialAccess.HasOwnerWriteCredential(User)) { return NotFound(); }
            return Result(await service.CreateBindingAsync(request, Actor, Scope, cancellationToken).ConfigureAwait(false));
        });

    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("{bindingId:guid}")]
    public Task<IActionResult> GetAsync(Guid bindingId, CancellationToken cancellationToken)
        => GuardAsync(async () => Result(await service.GetBindingAsync(bindingId, Actor, Scope, cancellationToken).ConfigureAwait(false)));

    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("{bindingId:guid}/status")]
    public Task<IActionResult> StatusAsync(Guid bindingId, CancellationToken cancellationToken)
        => GuardAsync(async () => Result(await service.GetStatusAsync(bindingId, Actor, Scope, cancellationToken).ConfigureAwait(false)));

    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("{bindingId:guid}/origins")]
    public Task<IActionResult> OriginsAsync(Guid bindingId, [FromQuery] long afterId = 0, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => GuardAsync(async () => Result(await service.ListOriginsAsync(bindingId, Actor, Scope, afterId, take, cancellationToken).ConfigureAwait(false)));

    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("{bindingId:guid}/units")]
    public Task<IActionResult> UnitsAsync(Guid bindingId, [FromQuery] long afterId = 0, [FromQuery] int take = 50,
        [FromQuery] string? originIdentitySha256 = null, [FromQuery] string? kind = null, CancellationToken cancellationToken = default)
        => GuardAsync(async () => Result(await service.ListUnitsAsync(bindingId, Actor, Scope, afterId, take, originIdentitySha256, kind, cancellationToken).ConfigureAwait(false)));

    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("{bindingId:guid}/units/{unitId:long}")]
    public Task<IActionResult> UnitAsync(Guid bindingId, long unitId, CancellationToken cancellationToken)
        => GuardAsync(async () =>
        {
            var bytes = await service.GetUnitAsync(bindingId, unitId, Actor, Scope, cancellationToken).ConfigureAwait(false);
            return bytes is null ? NotFound() : File(bytes, "application/json");
        });

    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("{bindingId:guid}/origins/{originIdentitySha256}/feedback")]
    public Task<IActionResult> FeedbackAsync(Guid bindingId, string originIdentitySha256, [FromQuery] long afterSequence = 0,
        [FromQuery] int take = 64, CancellationToken cancellationToken = default)
        => GuardAsync(async () =>
        {
            var response = await service.GetFeedbackAsync(bindingId, originIdentitySha256, Actor, Scope, afterSequence, take, cancellationToken).ConfigureAwait(false);
            return response is null ? NotFound() : File(GraphExecutionEvidenceJson.Serialize(response), "application/json");
        });

    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    [HttpGet("{bindingId:guid}/observations")]
    public Task<IActionResult> ObservationsAsync(Guid bindingId, [FromQuery] long afterId = 0, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => GuardAsync(async () => Result(await service.ListObservationsAsync(bindingId, Actor, Scope, afterId, take, cancellationToken).ConfigureAwait(false)));

    private string Actor => CentralArtifactCredentialAccess.GetOwnerId(User) ?? string.Empty;
    private Guid? Scope => CentralArtifactCredentialAccess.GetObservatoryScope(User);
    private IActionResult Result(object? value) => value is null ? NotFound() : Ok(value);

    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        if (Actor.Length == 0 || !(CentralArtifactCredentialAccess.HasOwnerCredential(User) || CentralArtifactCredentialAccess.HasOwnerWriteCredential(User))) { return NotFound(); }
        try { return await action().ConfigureAwait(false); }
        catch (ExecutionEvidenceInvalidException) { return BadRequest(new ProblemDetails { Title = "Invalid execution evidence query." }); }
        catch (Exception exception) when (exception is ExecutionEvidencePressureException or SqlException or DbUpdateException)
        {
            Response.Headers.RetryAfter = "5";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "Execution evidence is temporarily unavailable." });
        }
    }
}
