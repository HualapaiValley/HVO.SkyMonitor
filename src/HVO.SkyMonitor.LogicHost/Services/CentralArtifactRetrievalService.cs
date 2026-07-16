using System.Security.Claims;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralArtifactRetrievalService
{
    Task<CentralArtifactLookup> FindAsync(
        Guid devicePublicId,
        Guid artifactId,
        ClaimsPrincipal principal,
        CentralArtifactWorkerAccess? workerAccess,
        CancellationToken cancellationToken);

    Task MarkUnavailableAsync(
        CentralArtifact artifact,
        string reasonCode,
        bool quarantine,
        CancellationToken cancellationToken);

    Task<bool> ReauthorizeWorkerAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CentralArtifactWorkerAccess workerAccess,
        CancellationToken cancellationToken);
}

internal sealed partial class CentralArtifactRetrievalService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    CentralArtifactRetrievalTelemetry telemetry,
    ILogger<CentralArtifactRetrievalService> logger) : ICentralArtifactRetrievalService
{
    public async Task<CentralArtifactLookup> FindAsync(
        Guid devicePublicId,
        Guid artifactId,
        ClaimsPrincipal principal,
        CentralArtifactWorkerAccess? workerAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var artifact = await dbContext.CentralArtifacts
            .Include(candidate => candidate.Frame)
            .Include(candidate => candidate.Recipe)
            .SingleOrDefaultAsync(candidate => candidate.DevicePublicId == devicePublicId
                && candidate.ArtifactId == artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact?.Frame is null)
        {
            return Denied("unknown", "not-found", devicePublicId, artifactId);
        }

        var callerKind = CentralArtifactCredentialAccess.IsSystem(principal) ? "worker" : "owner";
        var authorized = callerKind == "worker"
            ? await AuthorizeWorkerAsync(artifact, principal, workerAccess, cancellationToken).ConfigureAwait(false)
            : await AuthorizeOwnerAsync(artifact, principal, cancellationToken).ConfigureAwait(false);
        if (!authorized)
        {
            return Denied(callerKind, "not-found", devicePublicId, artifactId);
        }

        var status = GetAvailability(artifact);
        telemetry.RecordAuthorization(callerKind, status == CentralArtifactLookupStatus.Found ? "allowed" : "unavailable");
        Log.AccessDecision(logger, callerKind, devicePublicId, artifactId, status.ToString(), artifact.StateReasonCode);
        return new CentralArtifactLookup(status, artifact, callerKind);
    }

    public async Task MarkUnavailableAsync(
        CentralArtifact artifact,
        string reasonCode,
        bool quarantine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (quarantine)
            {
                await dbContext.CentralArtifacts.Where(candidate => candidate.Id == artifact.Id
                        && candidate.ObjectState != CentralArtifactObjectState.Quarantined
                        && candidate.ObjectState != CentralArtifactObjectState.Expired)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(candidate => candidate.ObjectState, CentralArtifactObjectState.Quarantined)
                        .SetProperty(candidate => candidate.ReconstructionState, CentralReconstructionState.Quarantined)
                        .SetProperty(candidate => candidate.StateReasonCode, reasonCode)
                        .SetProperty(candidate => candidate.ReconciledAtUtc, (DateTimeOffset?)null), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await dbContext.CentralArtifacts.Where(candidate => candidate.Id == artifact.Id
                        && candidate.ObjectState != CentralArtifactObjectState.Quarantined
                        && candidate.ObjectState != CentralArtifactObjectState.Expired)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(candidate => candidate.ObjectState, CentralArtifactObjectState.Pending)
                        .SetProperty(candidate => candidate.ReconstructionState, CentralReconstructionState.PendingReference)
                        .SetProperty(candidate => candidate.StateReasonCode, reasonCode)
                        .SetProperty(candidate => candidate.ReconciledAtUtc, (DateTimeOffset?)null), cancellationToken)
                    .ConfigureAwait(false);
            }
            dbContext.ChangeTracker.Clear();
            var current = await dbContext.CentralArtifacts.SingleAsync(
                candidate => candidate.Id == artifact.Id, cancellationToken).ConfigureAwait(false);
            if (current.ObjectState == CentralArtifactObjectState.Expired)
            {
                telemetry.RecordVerification("expired");
                return;
            }
            try
            {
                await ArtifactIngestService.InvalidateDependentsAsync(dbContext, current, cancellationToken)
                    .ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                telemetry.RecordVerification(current.ObjectState == CentralArtifactObjectState.Quarantined
                    ? "quarantined"
                    : "missing");
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    public Task<bool> ReauthorizeWorkerAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CentralArtifactWorkerAccess workerAccess,
        CancellationToken cancellationToken)
        => ReauthorizeWorkerCoreAsync(artifact, principal, workerAccess, cancellationToken);

    private async Task<bool> ReauthorizeWorkerCoreAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CentralArtifactWorkerAccess workerAccess,
        CancellationToken cancellationToken)
    {
        var authorized = await AuthorizeWorkerAsync(artifact, principal, workerAccess, cancellationToken)
            .ConfigureAwait(false);
        if (!authorized)
        {
            telemetry.RecordAuthorization("worker", "denied");
            Log.AccessDecision(
                logger,
                "worker",
                artifact.Frame!.DevicePublicId,
                artifact.ArtifactId,
                "Denied",
                "lease-stale");
        }
        return authorized;
    }

    private async Task<bool> AuthorizeOwnerAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (!CentralArtifactCredentialAccess.HasOwnerCredential(principal))
        {
            return false;
        }
        var ownerId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        return !string.IsNullOrWhiteSpace(ownerId)
            && await dbContext.DeviceRegistrations.AnyAsync(registration =>
                registration.Id == artifact.Frame!.RegistrationId
                && registration.OwnerUserId == ownerId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> AuthorizeWorkerAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CentralArtifactWorkerAccess? access,
        CancellationToken cancellationToken)
    {
        if (!CentralArtifactCredentialAccess.HasScope(principal, "api.artifacts.read") || access is null
            || !string.Equals(CentralArtifactCredentialAccess.GetSubject(principal), access.WorkerId, StringComparison.Ordinal))
        {
            return false;
        }
        var now = timeProvider.GetUtcNow();
        return await dbContext.CentralDerivativeJobs.AnyAsync(job =>
            job.Id == access.JobId
            && (job.SourceCentralArtifactId == artifact.Id
                || job.Inputs.Any(input => input.CentralArtifactId == artifact.Id))
            && job.Status == CentralDerivativeJobStatus.Leased
            && job.LeaseOwner == access.WorkerId
            && job.LeaseToken == access.LeaseToken
            && job.LeaseExpiresAtUtc > now, cancellationToken).ConfigureAwait(false);
    }

    private CentralArtifactLookup Denied(string callerKind, string reason, Guid devicePublicId, Guid artifactId)
    {
        telemetry.RecordAuthorization(callerKind, "denied");
        Log.AccessDecision(logger, callerKind, devicePublicId, artifactId, "Denied", reason);
        return new CentralArtifactLookup(CentralArtifactLookupStatus.NotFound, null, callerKind);
    }

    private static CentralArtifactLookupStatus GetAvailability(CentralArtifact artifact)
    {
        if (artifact.ObjectState == CentralArtifactObjectState.Expired)
        {
            return CentralArtifactLookupStatus.Gone;
        }
        if (artifact.ObjectState == CentralArtifactObjectState.Pending
            || artifact.ReconstructionState == CentralReconstructionState.PendingReference)
        {
            return CentralArtifactLookupStatus.Pending;
        }
        return artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete
                ? CentralArtifactLookupStatus.Found
                : CentralArtifactLookupStatus.Conflict;
    }

    private static partial class Log
    {
        [LoggerMessage(2100, LogLevel.Information,
            "Central artifact access: CallerKind={CallerKind}, DevicePublicId={DevicePublicId}, ArtifactId={ArtifactId}, Decision={Decision}, Reason={Reason}")]
        public static partial void AccessDecision(
            ILogger logger,
            string callerKind,
            Guid devicePublicId,
            Guid artifactId,
            string decision,
            string? reason);
    }
}

internal sealed record CentralArtifactLookup(
    CentralArtifactLookupStatus Status,
    CentralArtifact? Artifact,
    string CallerKind);

internal sealed record CentralArtifactWorkerAccess(Guid JobId, string WorkerId, Guid LeaseToken);

internal enum CentralArtifactLookupStatus
{
    Found,
    NotFound,
    Pending,
    Conflict,
    Gone
}

internal static class CentralArtifactCredentialAccess
{
    public static bool HasOwnerCredential(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (IsSystem(principal))
        {
            return false;
        }
        if (principal.FindFirst(ApiKeyClaims.AuthenticationType) is not null)
        {
            var access = principal.FindFirstValue(ApiKeyClaims.AccessLevel);
            return access is nameof(ApiKeyAccessLevel.Read) or nameof(ApiKeyAccessLevel.ReadWrite);
        }
        return !principal.Claims.Any(claim => claim.Type == "scope")
            || HasScope(principal, "api.viewer")
            || HasScope(principal, "api.admin");
    }

    public static string? GetOwnerId(ClaimsPrincipal principal)
        => principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.Identity?.Name;

    public static string? GetSubject(ClaimsPrincipal principal)
        => principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

    public static bool IsSystem(ClaimsPrincipal principal)
        => string.Equals(principal.FindFirstValue("account_type"), "System", StringComparison.Ordinal);

    public static bool HasScope(ClaimsPrincipal principal, string scope)
        => principal.Claims.Where(claim => claim.Type == "scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
