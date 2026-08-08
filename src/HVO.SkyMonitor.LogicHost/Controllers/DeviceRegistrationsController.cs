using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/internal/devices")]
[Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
internal sealed class DeviceRegistrationsController(
    IDeviceRegistrationService registrationService,
    IDeviceRegistrationEnvelopeService envelopeService,
    ApplicationDbContext? dbContext = null,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private const string PortalConfirmationMethod = "PortalSelfAttested";
    private const string PortalRevocationMethod = "PortalSelfServiceRevocation";

    [HttpPost("verify")]
    public async Task<ActionResult<DeviceRegistrationResponse>> VerifyDeviceAsync(
        DeviceRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var ownerUserId = GetUserIdentifier();
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return Unauthorized();
        }
        if (!CentralArtifactCredentialAccess.IsObservatoryAllowed(User, request.ObservatoryId)) return Forbid();

        DeviceRegistration registration;
        try
        {
            registration = await registrationService.CreatePendingAsync(new DeviceRegistrationCreateRequest(
                request.DeviceId,
                request.VerificationCode,
                request.ObservatoryId,
                request.FriendlyName,
                ownerUserId,
                GetUserDisplayName() ?? ownerUserId,
                GetUserEmail(),
                PortalConfirmationMethod,
                null,
                TimeSpan.FromMinutes(request.PendingLifetimeMinutes ?? 15)), cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceRegistrationException ex) when (ex.ReasonCode == DeviceRegistrationException.NotFoundReasonCode)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return ValidationProblem(ModelState);
        }

        var response = new DeviceRegistrationResponse(
            registration.Id,
            registration.DeviceId,
            registration.Status,
            registration.IssuedAtUtc,
            registration.ExpiresAtUtc,
            registration.FriendlyName,
            registration.ObservatoryId,
            registration.ObservatoryName,
            registration.ObservatoryLatitudeDegrees,
            registration.ObservatoryLongitudeDegrees,
            registration.ObservatoryElevationMeters,
            registration.ObservatoryTimeZoneId,
            registration.OwnerUserId,
            registration.OwnerDisplayName,
            registration.OwnerEmail,
            registration.OwnerConfirmedAtUtc,
            registration.OwnerConfirmationMethod);

        return Ok(response);
    }

    [HttpPost("envelope")]
    public async Task<ActionResult<DeviceRegistrationEnvelopeDto>> CreateEnvelopeAsync(
        DeviceRegistrationEnvelopeDtoRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        TimeSpan? lifetime = request.EnvelopeLifetimeMinutes is int minutes
            ? TimeSpan.FromMinutes(minutes)
            : null;
        var ownerUserId = GetUserIdentifier();
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return Unauthorized();
        }
        if (!CentralArtifactCredentialAccess.IsObservatoryAllowed(User, request.ObservatoryId)) return Forbid();

        DeviceRegistrationEnvelopeResponse envelope;
        try
        {
            envelope = await envelopeService.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
                request.RegistrationId,
                request.DeviceId,
                request.ObservatoryId,
                ownerUserId,
                lifetime), cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceRegistrationException ex) when (ex.ReasonCode == DeviceRegistrationException.NotFoundReasonCode)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return ValidationProblem(ModelState);
        }

        var response = new DeviceRegistrationEnvelopeDto(
            envelope.RegistrationId,
            envelope.DevicePublicId,
            envelope.IssuedAtUtc,
            envelope.ExpiresAtUtc,
            envelope.Envelope,
            envelope.EnvelopeVersion);

        return Ok(response);
    }

    internal sealed record DeviceRegistrationRequest(
        [Required, StringLength(128)] string DeviceId,
        [Required, StringLength(32, MinimumLength = 4)] string VerificationCode,
        Guid ObservatoryId,
        [Required, StringLength(200)] string FriendlyName,
        int? PendingLifetimeMinutes = null);

    internal sealed record DeviceRegistrationResponse(
        Guid RegistrationId,
        string DeviceId,
        DeviceRegistrationStatus Status,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        string FriendlyName,
        Guid ObservatoryId,
        string ObservatoryName,
        double ObservatoryLatitudeDegrees,
        double ObservatoryLongitudeDegrees,
        double ObservatoryElevationMeters,
        string ObservatoryTimeZoneId,
        string OwnerUserId,
        string OwnerDisplayName,
        string? OwnerEmail,
        DateTimeOffset? OwnerConfirmedAtUtc,
        string OwnerConfirmationMethod);

    internal sealed record DeviceRegistrationEnvelopeDtoRequest(
        [Required] Guid RegistrationId,
        [Required, StringLength(128)] string DeviceId,
        Guid ObservatoryId,
        [Range(1, 30)] int? EnvelopeLifetimeMinutes = null);

    internal sealed record DeviceRegistrationEnvelopeDto(
        Guid RegistrationId,
        Guid DevicePublicId,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        string Envelope,
        string EnvelopeVersion);

    [HttpGet("continuity/{deviceId}")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    public async Task<ActionResult<DeviceContinuityResponse>> GetContinuityAsync(
        [StringLength(128, MinimumLength = 1)] string deviceId,
        CancellationToken cancellationToken,
        [FromQuery] long? fromCaptureSequence = null,
        [FromQuery] long? toCaptureSequence = null)
    {
        if (dbContext is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if ((fromCaptureSequence is null) != (toCaptureSequence is null) ||
            fromCaptureSequence is < 1 || toCaptureSequence < fromCaptureSequence ||
            toCaptureSequence - fromCaptureSequence >= 256)
        {
            return BadRequest();
        }

        var ownerUserId = GetUserIdentifier();
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return Unauthorized();
        }

        var query = dbContext.DeviceRegistrations
            .AsNoTracking()
            .Where(registration => registration.DeviceId == deviceId && registration.OwnerUserId == ownerUserId);
        if (CentralArtifactCredentialAccess.GetObservatoryScope(User) is { } observatoryScope)
        {
            query = query.Where(registration => registration.ObservatoryId == observatoryScope);
        }

        var registrations = await query
            .Select(item => new
            {
                item.Id,
                item.DeviceId,
                item.DevicePublicId,
                item.ObservatoryId,
                item.Status,
                item.ActivatedAtUtc,
                item.CurrentRigProfileVersion,
                item.CurrentRigProfileHash,
                item.IssuedAtUtc,
                item.ExpiresAtUtc
            })
            .OrderByDescending(item => item.IssuedAtUtc)
            .ThenByDescending(item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (registrations.Count == 0)
        {
            return NotFound();
        }
        var active = registrations.Where(item => item.Status == DeviceRegistrationStatus.Active).ToArray();
        if (active.Length > 1)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Device registration history has more than one active registration."
            });
        }
        var registration = active.SingleOrDefault()
            ?? registrations.FirstOrDefault(item => item.Status == DeviceRegistrationStatus.Pending)
            ?? registrations[0];

        var maximumCaptureSequence = await dbContext.CentralFrames
            .Where(frame => frame.RegistrationId == registration.Id)
            .Select(frame => frame.CaptureSequence)
            .MaxAsync(cancellationToken).ConfigureAwait(false);
        var fleet = await dbContext.DeviceFleetStates
            .AsNoTracking()
            .Where(state => state.RegistrationId == registration.Id)
            .Select(state => new { state.AgentInstanceId, state.Sequence, state.ReceivedAtUtc })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var artifactQuery = dbContext.CentralArtifacts
            .AsNoTracking()
            .Where(artifact => artifact.Frame != null && artifact.Frame.RegistrationId == registration.Id);
        var centralFrameCount = await dbContext.CentralFrames
            .LongCountAsync(frame => frame.RegistrationId == registration.Id, cancellationToken).ConfigureAwait(false);
        var centralArtifactCount = await artifactQuery.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var lineageSourceCount = await dbContext.CentralArtifactSources
            .LongCountAsync(source => source.Artifact != null && source.Artifact.Frame != null &&
                source.Artifact.Frame.RegistrationId == registration.Id, cancellationToken).ConfigureAwait(false);
        var completedDerivativeCount = await dbContext.CentralDerivativeJobs
            .LongCountAsync(job => job.Status == CentralDerivativeJobStatus.Completed && job.SourceArtifact != null &&
                job.SourceArtifact.Frame != null && job.SourceArtifact.Frame.RegistrationId == registration.Id,
                cancellationToken).ConfigureAwait(false);
        var latestArtifactRows = await artifactQuery
            .OrderByDescending(artifact => artifact.ReceivedAtUtc)
            .ThenBy(artifact => artifact.Id)
            .Take(16)
            .Select(artifact => new
            {
                artifact.Id,
                artifact.ArtifactId,
                artifact.Role,
                artifact.RecipeVersion,
                artifact.ByteLength,
                artifact.ChecksumSha256,
                artifact.ObjectState,
                artifact.ObjectVerifiedAtUtc,
                CaptureSequence = artifact.Frame!.CaptureSequence,
                RecipeName = artifact.Recipe == null ? null : artifact.Recipe.Name,
                RecipeSemanticVersion = artifact.Recipe == null ? null : artifact.Recipe.SemanticVersion,
                RecipeImplementationVersion = artifact.Recipe == null ? null : artifact.Recipe.ImplementationVersion,
                Width = artifact.Layout == null ? (int?)null : artifact.Layout.Width,
                Height = artifact.Layout == null ? (int?)null : artifact.Layout.Height,
                PixelFormat = artifact.Layout == null ? null : artifact.Layout.PixelFormat,
                Sources = artifact.Sources.OrderBy(source => source.Ordinal).Select(source => new
                {
                    source.SourceArtifactId,
                    ChecksumSha256 = source.ResolvedArtifact == null ? null : source.ResolvedArtifact.ChecksumSha256
                }).ToArray()
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var latestArtifactIds = latestArtifactRows.Select(artifact => artifact.Id).ToArray();
        var completedBySource = await dbContext.CentralDerivativeJobs
            .AsNoTracking()
            .Where(job => job.Status == CentralDerivativeJobStatus.Completed && latestArtifactIds.Contains(job.SourceCentralArtifactId))
            .GroupBy(job => job.SourceCentralArtifactId)
            .Select(group => new { ArtifactId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.ArtifactId, item => item.Count, cancellationToken).ConfigureAwait(false);
        var latestArtifacts = latestArtifactRows
            .Select(artifact => new DeviceArtifactContinuityResponse(
                artifact.ArtifactId,
                artifact.Role.ToString(),
                artifact.RecipeVersion,
                artifact.ByteLength,
                artifact.ChecksumSha256,
                artifact.ObjectState.ToString(),
                artifact.ObjectVerifiedAtUtc,
                artifact.CaptureSequence,
                artifact.RecipeName,
                artifact.RecipeSemanticVersion,
                artifact.RecipeImplementationVersion,
                artifact.Width,
                artifact.Height,
                artifact.PixelFormat,
                artifact.Sources.Select(source => new DeviceArtifactSourceContinuityResponse(
                    source.SourceArtifactId, source.ChecksumSha256)).ToArray(),
                completedBySource.GetValueOrDefault(artifact.Id)))
            .ToArray();
        var captureWindow = fromCaptureSequence is null
            ? []
            : await dbContext.CentralFrames
                .AsNoTracking()
                .Where(frame => frame.RegistrationId == registration.Id &&
                    frame.CaptureSequence >= fromCaptureSequence && frame.CaptureSequence <= toCaptureSequence)
                .OrderBy(frame => frame.CaptureSequence)
                .Select(frame => new DeviceCaptureContinuityResponse(
                    frame.CaptureSequence!.Value,
                    frame.FrameId,
                    frame.Artifacts.OrderBy(artifact => artifact.Role).ThenBy(artifact => artifact.ArtifactId)
                        .Select(artifact => new DeviceCaptureArtifactContinuityResponse(
                            artifact.ArtifactId,
                            artifact.Role.ToString(),
                            artifact.ChecksumSha256,
                            artifact.ByteLength,
                            artifact.ObjectState.ToString(),
                            artifact.ObjectVerifiedAtUtc,
                            artifact.Sources.OrderBy(source => source.Ordinal)
                                .Select(source => new DeviceArtifactSourceContinuityResponse(
                                    source.SourceArtifactId,
                                    source.ResolvedArtifact == null ? null : source.ResolvedArtifact.ChecksumSha256))
                                .ToArray(),
                            dbContext.CentralDerivativeJobs.LongCount(job =>
                                job.SourceCentralArtifactId == artifact.Id && job.Status == CentralDerivativeJobStatus.Completed)))
                        .ToArray()))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new DeviceContinuityResponse(
            registration.Id,
            registration.DeviceId,
            registration.DevicePublicId,
            registration.ObservatoryId,
            registration.Status.ToString(),
            registration.IssuedAtUtc,
            registration.ExpiresAtUtc,
            registration.ActivatedAtUtc,
            registration.CurrentRigProfileVersion,
            registration.CurrentRigProfileHash,
            maximumCaptureSequence,
            fleet?.AgentInstanceId,
            fleet?.Sequence,
            fleet?.ReceivedAtUtc,
            centralFrameCount,
            centralArtifactCount,
            lineageSourceCount,
            completedDerivativeCount,
            latestArtifacts,
            captureWindow,
            registrations.Select(item => new DeviceRegistrationHistoryResponse(
                item.Id,
                item.Status.ToString(),
                item.DevicePublicId,
                item.ObservatoryId,
                item.IssuedAtUtc,
                item.ExpiresAtUtc,
                item.ActivatedAtUtc,
                item.Id == registration.Id)).ToArray()));
    }

    internal sealed record DeviceContinuityResponse(
        Guid RegistrationId,
        string DeviceId,
        Guid? DevicePublicId,
        Guid ObservatoryId,
        string Status,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        DateTimeOffset? ActivatedAtUtc,
        int? CurrentRigProfileVersion,
        string? CurrentRigProfileHash,
        long? MaximumCaptureSequence,
        Guid? FleetAgentInstanceId,
        long? MaximumHeartbeatSequence,
        DateTimeOffset? LastHeartbeatReceivedAtUtc,
        long CentralFrameCount,
        long CentralArtifactCount,
        long LineageSourceCount,
        long CompletedDerivativeCount,
        IReadOnlyList<DeviceArtifactContinuityResponse> LatestArtifacts,
        IReadOnlyList<DeviceCaptureContinuityResponse> CaptureWindow,
        IReadOnlyList<DeviceRegistrationHistoryResponse> Registrations);

    internal sealed record DeviceCaptureContinuityResponse(
        long CaptureSequence,
        Guid CaptureId,
        IReadOnlyList<DeviceCaptureArtifactContinuityResponse> Artifacts);

    internal sealed record DeviceCaptureArtifactContinuityResponse(
        Guid ArtifactId,
        string Role,
        string ChecksumSha256,
        long ByteLength,
        string ObjectState,
        DateTimeOffset? ObjectVerifiedAtUtc,
        IReadOnlyList<DeviceArtifactSourceContinuityResponse> Sources,
        long CompletedDerivativeCount);

    internal sealed record DeviceArtifactContinuityResponse(
        Guid ArtifactId,
        string Role,
        string RecipeVersion,
        long ByteLength,
        string ChecksumSha256,
        string ObjectState,
        DateTimeOffset? ObjectVerifiedAtUtc,
        long? CaptureSequence,
        string? RecipeName,
        string? RecipeSemanticVersion,
        string? RecipeImplementationVersion,
        int? Width,
        int? Height,
        string? PixelFormat,
        IReadOnlyList<DeviceArtifactSourceContinuityResponse> Sources,
        long CompletedDerivativeCount);

    internal sealed record DeviceArtifactSourceContinuityResponse(Guid ArtifactId, string? ChecksumSha256);

    internal sealed record DeviceRegistrationHistoryResponse(
        Guid RegistrationId,
        string Status,
        Guid? DevicePublicId,
        Guid ObservatoryId,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        DateTimeOffset? ActivatedAtUtc,
        bool IsAuthoritative);

    [HttpPost("recover")]
    public async Task<ActionResult<DeviceRegistrationResponse>> RecoverActiveRegistrationAsync(
        DeviceRegistrationRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (dbContext is null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        var ownerUserId = GetUserIdentifier();
        if (string.IsNullOrWhiteSpace(ownerUserId)) return Unauthorized();
        if (!CentralArtifactCredentialAccess.IsObservatoryAllowed(User, request.ObservatoryId)) return Forbid();

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        var registration = await dbContext.DeviceRegistrations
            .SingleOrDefaultAsync(item => item.Id == request.RegistrationId && item.DeviceId == request.DeviceId
                && item.ObservatoryId == request.ObservatoryId && item.OwnerUserId == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        if (registration is null) return NotFound();
        if (registration.Status != DeviceRegistrationStatus.Active)
        {
            return Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Only an active registration can be recovered." });
        }
        var hasFrames = await dbContext.CentralFrames.AnyAsync(frame => frame.RegistrationId == registration.Id, cancellationToken).ConfigureAwait(false);
        var hasFleet = await dbContext.DeviceFleetStates.AnyAsync(state => state.RegistrationId == registration.Id, cancellationToken).ConfigureAwait(false);
        if (hasFrames || hasFleet)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Active registration has central evidence and requires incident recovery."
            });
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        registration.Status = DeviceRegistrationStatus.Pending;
        registration.VerificationCodeHash = DeviceRegistrationService.ComputeSha256(request.VerificationCode);
        registration.FriendlyName = request.FriendlyName.Trim();
        registration.DevicePublicId = null;
        registration.DeviceKeyHash = null;
        registration.RegistrationTokenHash = null;
        registration.ActivatedAtUtc = null;
        registration.IssuedAtUtc = now;
        registration.ExpiresAtUtc = now.AddMinutes(15);
        registration.RevokedReason = null;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new DeviceRegistrationResponse(
            registration.Id, registration.DeviceId, registration.Status, registration.IssuedAtUtc,
            registration.ExpiresAtUtc, registration.FriendlyName, registration.ObservatoryId,
            registration.ObservatoryName, registration.ObservatoryLatitudeDegrees,
            registration.ObservatoryLongitudeDegrees, registration.ObservatoryElevationMeters,
            registration.ObservatoryTimeZoneId, registration.OwnerUserId, registration.OwnerDisplayName,
            registration.OwnerEmail, registration.OwnerConfirmedAtUtc, registration.OwnerConfirmationMethod));
    }

    internal sealed record DeviceRegistrationRecoveryRequest(
        [Required] Guid RegistrationId,
        [Required, StringLength(128)] string DeviceId,
        [Required, StringLength(32, MinimumLength = 4)] string VerificationCode,
        Guid ObservatoryId,
        [Required, StringLength(200)] string FriendlyName);

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteRegistrationAsync(
        DeviceRegistrationDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var ownerUserId = GetUserIdentifier();
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return Unauthorized();
        }
        if (CentralArtifactCredentialAccess.GetObservatoryScope(User) is { } observatoryScope
            && (dbContext is null || !await dbContext.DeviceRegistrations.AnyAsync(registration =>
                registration.Id == request.RegistrationId
                && registration.ObservatoryId == observatoryScope, cancellationToken).ConfigureAwait(false)))
        {
            return Forbid();
        }

        try
        {
            await registrationService.RevokeAsync(new DeviceRegistrationRevokeRequest(
                request.RegistrationId,
                request.DeviceId,
                ownerUserId,
                GetUserDisplayName() ?? ownerUserId,
                PortalRevocationMethod,
                request.Reason), cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceRegistrationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return ValidationProblem(ModelState);
        }

        return NoContent();
    }

    internal sealed record DeviceRegistrationDeleteRequest(
        [Required] Guid RegistrationId,
        [Required, StringLength(128)] string DeviceId,
        [StringLength(256)] string? Reason);

    private string? GetUserIdentifier()
    {
        return User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User?.FindFirstValue("sub")
            ?? User?.Identity?.Name;
    }

    private string? GetUserDisplayName()
    {
        return User?.FindFirstValue("name")
            ?? User?.Identity?.Name
            ?? GetUserEmail();
    }

    private string? GetUserEmail()
    {
        return User?.FindFirstValue(ClaimTypes.Email)
            ?? User?.FindFirstValue("preferred_username");
    }
}
