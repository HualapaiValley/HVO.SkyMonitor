using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
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

    Task<CentralArtifactLookup> FindForDeviceSubmissionAsync(
        Guid devicePublicId,
        string agentId,
        Guid artifactId,
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

    Task<CentralArtifactDownloadGrant?> IssueDownloadAuthorizationAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CentralArtifactByteRange? range,
        CancellationToken cancellationToken);

    Task<bool> ValidateDownloadAuthorizationAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CentralArtifactByteRange? range,
        Guid authorizationId,
        string token,
        CancellationToken cancellationToken);

    Task<bool> RequiresDownloadAuthorizationAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}

internal sealed record CentralArtifactDownloadGrant(Guid AuthorizationId, string Token, DateTimeOffset ExpiresAtUtc);

internal sealed partial class CentralArtifactRetrievalService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    CentralArtifactRetrievalTelemetry telemetry,
    ILogger<CentralArtifactRetrievalService> logger,
    OperatorUiTelemetry? operatorUiTelemetry = null,
    ICentralProcessingRunnerRegistry? runnerRegistry = null) : ICentralArtifactRetrievalService
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
            .Include(candidate => candidate.Layout)
            .Include(candidate => candidate.Recipe)
            .SingleOrDefaultAsync(candidate => candidate.DevicePublicId == devicePublicId
                && candidate.ArtifactId == artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact?.Frame is null)
        {
            return Denied("unknown", "not-found", devicePublicId, artifactId);
        }

        var callerKind = CentralArtifactCredentialAccess.IsSystem(principal) ? "worker" : "member";
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

    public async Task<CentralArtifactLookup> FindForDeviceSubmissionAsync(
        Guid devicePublicId,
        string agentId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var artifact = await dbContext.CentralArtifacts
            .Include(candidate => candidate.Layout)
            .Include(candidate => candidate.Recipe)
            .Include(candidate => candidate.Sources)
            .Include(candidate => candidate.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(candidate => candidate.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(candidate => candidate.Frame)!.ThenInclude(frame => frame!.Profiles)
            .Include(candidate => candidate.Frame)!.ThenInclude(frame => frame!.Location)
            .SingleOrDefaultAsync(candidate => candidate.DevicePublicId == devicePublicId
                && candidate.ArtifactId == artifactId
                && candidate.Frame!.AgentId == agentId, cancellationToken).ConfigureAwait(false);
        if (artifact?.Frame is null)
        {
            return Denied("device", "not-found", devicePublicId, artifactId);
        }
        var status = GetAvailability(artifact);
        telemetry.RecordAuthorization("device", status == CentralArtifactLookupStatus.Found ? "allowed" : "unavailable");
        Log.AccessDecision(logger, "device", devicePublicId, artifactId, status.ToString(), artifact.StateReasonCode);
        return new CentralArtifactLookup(status, artifact, "device");
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
                        .SetProperty(candidate => candidate.ReconciledAtUtc, (DateTimeOffset?)null)
                        .SetProperty(candidate => candidate.ObjectVerificationToken, (Guid?)null)
                        .SetProperty(candidate => candidate.ObjectVerificationRequestedAtUtc, (DateTimeOffset?)null)
                        .SetProperty(candidate => candidate.ObjectVerificationRetryCount, 0)
                        .SetProperty(candidate => candidate.ObjectVerificationRetryAtUtc, (DateTimeOffset?)null), cancellationToken)
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
                        .SetProperty(candidate => candidate.ReconciledAtUtc, (DateTimeOffset?)null)
                        .SetProperty(candidate => candidate.ReferenceRetryCount, 0)
                        .SetProperty(candidate => candidate.ReferenceRetryAtUtc, (DateTimeOffset?)null), cancellationToken)
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

    public async Task<CentralArtifactDownloadGrant?> IssueDownloadAuthorizationAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CentralArtifactByteRange? range,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRawDownload();
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(principal);
        if (CentralArtifactCredentialAccess.IsSystem(principal))
        {
            return null;
        }
        if (!CentralArtifactCredentialAccess.HasOwnerCredential(principal))
        {
            return null;
        }
        var actorUserId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actorUserId))
        {
            return null;
        }
        if (!CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, artifact.Frame!.ObservatoryId))
        {
            return null;
        }
        var authority = await dbContext.ObservatoryMemberships.AsNoTracking()
                .Where(membership => membership.ObservatoryId == artifact.Frame!.ObservatoryId
                    && membership.UserId == actorUserId
                    && membership.User!.AccountType == AccountType.User)
                .Select(membership => new { membership.ObservatoryId, membership.Role })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (authority is null)
        {
            return null;
        }

        var issuedAtUtc = timeProvider.GetUtcNow();
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var authorization = new CentralArtifactDownloadAuthorization
        {
            CentralArtifactId = artifact.Id,
            ObservatoryId = authority.ObservatoryId,
            ActorUserId = actorUserId,
            MembershipRole = authority.Role,
            RangeStart = range?.Start,
            RangeEnd = range?.End,
            IssuedAtUtc = issuedAtUtc,
            ExpiresAtUtc = issuedAtUtc.AddMinutes(1),
            TokenSha256 = HashToken(token)
        };
        dbContext.CentralArtifactDownloadAuthorizations.Add(authorization);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        OperatorUiAuditLog.RawDownload(
            logger,
            "issued",
            authority.Role.ToString(),
            range is null ? "full" : "range");
        operatorUiTelemetry?.RecordMutation(
            "raw-download",
            "issued",
            authority.Role.ToString().ToLowerInvariant(),
            Stopwatch.GetElapsedTime(started));
        return new(authorization.Id, token, authorization.ExpiresAtUtc);
    }

    public async Task<bool> ValidateDownloadAuthorizationAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CentralArtifactByteRange? range,
        Guid authorizationId,
        string token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(principal);
        if (CentralArtifactCredentialAccess.IsSystem(principal)) return true;
        var actorUserId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actorUserId) || string.IsNullOrWhiteSpace(token)) return false;
        var now = timeProvider.GetUtcNow();
        var tokenSha256 = HashToken(token);
        return await dbContext.CentralArtifactDownloadAuthorizations.AsNoTracking().AnyAsync(authorization =>
            authorization.Id == authorizationId
            && authorization.CentralArtifactId == artifact.Id
            && authorization.ActorUserId == actorUserId
            && authorization.TokenSha256 == tokenSha256
            && authorization.ExpiresAtUtc > now
            && authorization.RangeStart == (range == null ? null : range.Start)
            && authorization.RangeEnd == (range == null ? null : range.End)
            && (CentralArtifactCredentialAccess.GetObservatoryScope(principal) == null
                || authorization.ObservatoryId == CentralArtifactCredentialAccess.GetObservatoryScope(principal))
            && dbContext.ObservatoryMemberships.Any(membership =>
                membership.ObservatoryId == authorization.ObservatoryId
                && membership.UserId == actorUserId
                && membership.User!.AccountType == AccountType.User), cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RequiresDownloadAuthorizationAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!CentralArtifactCredentialAccess.IsSystem(principal));
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

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
        if (!CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, artifact.Frame!.ObservatoryId))
        {
            return false;
        }
        var observatories = string.IsNullOrWhiteSpace(ownerId)
            ? null
            : ObservatoryMembershipAccess.ForUser(dbContext, ownerId)
                .Select(membership => membership.ObservatoryId);
        return !string.IsNullOrWhiteSpace(ownerId)
            && observatories is not null
            && await observatories.ContainsAsync(artifact.Frame!.ObservatoryId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> AuthorizeWorkerAsync(
        CentralArtifact artifact,
        ClaimsPrincipal principal,
        CentralArtifactWorkerAccess? access,
        CancellationToken cancellationToken)
    {
        if (!CentralArtifactCredentialAccess.HasScope(principal, "api.artifacts.read") || access is null)
        {
            return false;
        }
        var subject = CentralArtifactCredentialAccess.GetSubject(principal);
        if (access.RunnerId is { } runnerId)
        {
            // A runner reads under its registered identity: the lease owner is the runner id and the credential
            // subject must own that registration, so one runner credential cannot read another runner's job inputs.
            if (runnerRegistry is null || string.IsNullOrWhiteSpace(subject)
                || !string.Equals(runnerId, access.WorkerId, StringComparison.Ordinal)
                || !await runnerRegistry.IsOwnedAsync(subject, runnerId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        else if (!string.Equals(subject, access.WorkerId, StringComparison.Ordinal))
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
        if (string.Equals(
                artifact.StateReasonCode,
                CentralDerivativeJobScheduler.LocationUnresolvedReason,
                StringComparison.Ordinal)
            || string.Equals(
                artifact.StateReasonCode,
                CentralDerivativeJobScheduler.LocationMismatchReason,
                StringComparison.Ordinal))
        {
            return CentralArtifactLookupStatus.Conflict;
        }
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

/// <summary>
/// Job-scoped worker access. <see cref="WorkerId"/> is the lease owner: the credential subject for the in-process
/// worker, or the registered runner id when <see cref="RunnerId"/> is supplied and owned by the credential subject.
/// </summary>
internal sealed record CentralArtifactWorkerAccess(Guid JobId, string WorkerId, Guid LeaseToken, string? RunnerId = null);

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
    internal const string BearerAuthenticationType = CanonicalCredentialClaims.BearerAuthenticationType;

    public static bool HasSingleCredentialIdentity(ClaimsPrincipal principal)
        => CanonicalCredentialClaims.HasSingleIdentity(principal);

    public static bool HasOwnerCredential(ClaimsPrincipal principal)
    {
        var identity = GetSingleCredentialIdentity(principal);
        if (identity is null || IsSystem(principal))
        {
            return false;
        }
        if (IsApiKey(identity))
        {
            var access = CanonicalCredentialClaims.GetApiKeyAccessLevel(principal);
            return access is nameof(ApiKeyAccessLevel.Read) or nameof(ApiKeyAccessLevel.ReadWrite);
        }
        return CanonicalCredentialClaims.IsCookie(identity)
            || HasScope(principal, "api.viewer")
            || HasScope(principal, "api.admin");
    }

    /// <summary>
    /// An owner credential that may mutate owner-scoped resources: everything <see cref="HasOwnerCredential"/>
    /// accepts plus a bearer carrying only the documented <c>api.owner.write</c> scope, which the
    /// <c>ApiKeyReadWrite</c> policy already authorizes for mutation endpoints. Read-only API keys are excluded.
    /// </summary>
    public static bool HasOwnerWriteCredential(ClaimsPrincipal principal)
    {
        var identity = GetSingleCredentialIdentity(principal);
        if (identity is null || IsSystem(principal))
        {
            return false;
        }
        if (IsApiKey(identity))
        {
            return CanonicalCredentialClaims.GetApiKeyAccessLevel(principal) == nameof(ApiKeyAccessLevel.ReadWrite);
        }
        return CanonicalCredentialClaims.IsCookie(identity)
            || CanonicalCredentialClaims.IsBearer(identity)
                && (HasScope(principal, "api.owner.write") || HasScope(principal, "api.admin"));
    }

    public static string? GetOwnerId(ClaimsPrincipal principal)
        => CanonicalCredentialClaims.GetOwnerId(principal);

    public static string? GetSubject(ClaimsPrincipal principal)
        => CanonicalCredentialClaims.GetSubject(principal);

    public static bool IsSystem(ClaimsPrincipal principal)
        => CanonicalCredentialClaims.IsSystem(principal);

    public static string? GetAccountType(ClaimsPrincipal principal)
        => CanonicalCredentialClaims.GetAccountType(principal);

    public static bool HasScope(ClaimsPrincipal principal, string scope)
        => CanonicalCredentialClaims.HasScope(principal, scope);

    public static Guid? GetObservatoryScope(ClaimsPrincipal principal)
        => CanonicalCredentialClaims.GetObservatoryScope(principal);

    public static bool IsObservatoryAllowed(ClaimsPrincipal principal, Guid observatoryId)
        => GetObservatoryScope(principal) is not { } scope || scope == observatoryId;

    public static ClaimsIdentity? GetSingleCredentialIdentity(ClaimsPrincipal principal)
        => CanonicalCredentialClaims.GetSingleIdentity(principal);

    public static bool IsApiKey(ClaimsIdentity identity)
        => CanonicalCredentialClaims.IsApiKey(identity);

    public static string? GetApiKeyAccessLevel(ClaimsPrincipal principal)
        => CanonicalCredentialClaims.GetApiKeyAccessLevel(principal);
}
