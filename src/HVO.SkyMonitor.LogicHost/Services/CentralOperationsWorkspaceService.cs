using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralOperationsSnapshot(ObservatoryMembershipRole Role, bool PlatformEditor,
    CameraOperationsPage<CentralProcessingGraphExecutionSummary>? Executions = null,
    CameraProcessingGraphExecutionInspector? Execution = null,
    CameraCaptureAnchoredPage<CameraJobHistoryItem>? Jobs = null,
    CameraJobInspector? Job = null,
    CameraOperationsPage<CameraProcessingGraphAssignmentSummary>? Assignments = null,
    CentralProcessingGraphAssignmentView? Effective = null,
    CentralProcessingGraphRevisionPage? Revisions = null,
    CentralProcessingGraphRevisionView? Revision = null,
    CameraOperationsPage<CameraProcessingGraphProposalSummary>? Proposals = null,
    ProcessingGraphDeliveryProposalView? Proposal = null,
    CameraOperationsPage<ProcessingGraphDeliveryFactView>? Facts = null,
    CameraCaptureAnchoredPage<CameraTransientEventAssociation>? Events = null,
    CentralTransientEventDetail? Event = null,
    bool CanReviewEvent = false,
    CentralProcessingPolicySummary? Policy = null,
    OperationsArtifactPage? Artifacts = null);

internal interface ICentralOperationsWorkspaceService
{
    Task<CentralOperationsSnapshot?> ReadAsync(ClaimsPrincipal principal, Guid observatory, Guid camera,
        CentralOperationsQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Ephemeral UI composition. Each collection retains one bounded page; durable owners remain unchanged.</summary>
internal sealed class CentralOperationsWorkspaceService(ApplicationDbContext db, ICameraOperationsReadService operations,
    IProcessingGraphCatalogService catalog, ICentralProcessingPolicyService policy, INetworkOperationsReadService network,
    ICentralTransientEventReadService events, ICentralTransientReviewService reviews, TimeProvider clock, IDataProtectionProvider protection)
    : ICentralOperationsWorkspaceService
{
    internal const int PageSize = 25;
    private readonly IDataProtector artifactPages = protection.CreateProtector("central-workspace-artifact-pages-v1");

    public async Task<CentralOperationsSnapshot?> ReadAsync(ClaimsPrincipal principal, Guid observatory, Guid camera,
        CentralOperationsQuery query, CancellationToken cancellationToken = default)
    {
        var user = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (user is null || !CentralArtifactCredentialAccess.HasOwnerCredential(principal) ||
            !CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, observatory)) return null;
        var authority = await AuthorityAsync(user, observatory, camera, cancellationToken).ConfigureAwait(false);
        if (authority is null) return null;
        var result = new CentralOperationsSnapshot(authority.Value, principal.IsInRole(AuthorizationRoleNames.PlatformEditor));
        switch (query.View)
        {
            case "executions":
                result = result with
                {
                    Executions = await operations.ListExecutionsAsync(principal, observatory, camera,
                    PageSize, query.Page, cancellationToken).ConfigureAwait(false)
                };
                if (query.Execution is { } execution)
                {
                    var detail = await operations.GetExecutionAsync(principal, observatory, camera, execution,
                        PageSize, query.NodesPage, cancellationToken).ConfigureAwait(false);
                    if (detail is null) return null;
                    result = result with { Execution = detail };
                }
                break;
            case "jobs":
                result = result with
                {
                    Jobs = await operations.ListJobsAsync(principal, observatory, camera,
                    PageSize, query.Page, cancellationToken).ConfigureAwait(false)
                };
                if (query.Job is { } job)
                {
                    var detail = await operations.GetJobAsync(principal, observatory, camera, job, PageSize, cancellationToken).ConfigureAwait(false);
                    if (detail is null) return null;
                    if (query.AttemptsPage is not null)
                    {
                        var attempts = await operations.ListJobAttemptsAsync(principal, observatory, camera, job, PageSize, query.AttemptsPage, cancellationToken).ConfigureAwait(false);
                        if (attempts is null) return null;
                        detail = detail with { Attempts = attempts };
                    }
                    if (query.InputsPage is not null)
                    {
                        var inputs = await operations.ListJobInputsAsync(principal, observatory, camera, job, PageSize, query.InputsPage, cancellationToken).ConfigureAwait(false);
                        if (inputs is null) return null;
                        detail = detail with { Inputs = inputs };
                    }
                    if (query.OutputsPage is not null)
                    {
                        var outputs = await operations.ListJobOutputsAsync(principal, observatory, camera, job, PageSize, query.OutputsPage, cancellationToken).ConfigureAwait(false);
                        if (outputs is null) return null;
                        detail = detail with { Outputs = outputs };
                    }
                    result = result with { Job = detail };
                }
                break;
            case "graphs":
                if (!Enum.TryParse<CentralProcessingGraphTargetHost>(query.Target, out var target) ||
                    !Enum.IsDefined(target) || target.ToString() != query.Target) throw new ArgumentException("Invalid target.", nameof(query));
                result = result with
                {
                    Assignments = await operations.ListAssignmentsAsync(principal, observatory, camera, target,
                        PageSize, query.Page, cancellationToken).ConfigureAwait(false),
                    Effective = await catalog.ResolveForUserAsync(target, observatory, camera, clock.GetUtcNow(), user,
                        false, CentralArtifactCredentialAccess.GetObservatoryScope(principal), cancellationToken).ConfigureAwait(false)
                };
                break;
            case "catalog":
                result = result with { Revisions = await catalog.ListRevisionSummariesAsync(PageSize, query.Page, cancellationToken).ConfigureAwait(false) };
                break;
            case "delivery":
                result = result with
                {
                    Proposals = await operations.ListProposalsAsync(principal, observatory, camera,
                    PageSize, query.Page, cancellationToken).ConfigureAwait(false)
                };
                if (query.Proposal is { } proposal)
                {
                    // Exact retained proposal lookup; its installation identity must belong to this camera.
                    var header = await db.CentralProcessingGraphDeliveryProposals.AsNoTracking()
                        .Where(p => p.Id == proposal && p.LogicalCameraInstallation!.LogicalCameraId == camera &&
                            p.LogicalCameraInstallation.LogicalCamera!.ObservatoryId == observatory)
                        .Select(p => new ProcessingGraphDeliveryProposalView(p.Id, p.AssignmentId, p.RevisionId, p.RegistrationId,
                            p.LogicalCameraInstallationId, p.InstallationPublicId, p.ExpectedActiveLocalRevisionId,
                            p.CapabilitySnapshotSha256, p.IssuedAtUtc, p.ExpiresAtUtc))
                        .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                    var facts = await operations.ListProposalFactsAsync(principal, observatory, camera, proposal,
                        PageSize, query.FactsPage, cancellationToken).ConfigureAwait(false);
                    if (header is null || facts is null) return null;
                    result = result with { Proposal = header, Facts = facts };
                }
                break;
            case "events":
                var associations = await operations.ListEventAssociationsAsync(principal, observatory, camera,
                    PageSize, query.Page, cancellationToken).ConfigureAwait(false);
                result = result with { Events = associations };
                if (query.Event is { } eventId)
                {
                    // Keep the association page in the URL. Never turn global event access into camera association.
                    if (!associations.Items.Any(a => a.CentralTransientEventId == eventId)) return null;
                    var detail = await events.GetAsync(principal, eventId, cancellationToken).ConfigureAwait(false);
                    if (detail is null) return null;
                    var reviewable = await reviews.ListReviewableEventIdsAsync(principal, [eventId], cancellationToken).ConfigureAwait(false);
                    result = result with { Event = detail, CanReviewEvent = reviewable.Contains(eventId) };
                }
                break;
            case "policy":
                result = result with { Policy = await policy.GetAsync(observatory, user, cancellationToken).ConfigureAwait(false) };
                break;
            case "artifacts":
                if (query.Capture is not { } capture || !await CaptureAllowedAsync(observatory, camera, capture, cancellationToken).ConfigureAwait(false)) return null;
                var rawPage = DecodeArtifactPage(query.Page, user, observatory, camera, capture);
                var artifacts = await network.ListCaptureArtifactsAsync(user, capture, PageSize, rawPage, cancellationToken).ConfigureAwait(false);
                result = result with
                {
                    Artifacts = artifacts with
                    {
                        NextCursor = artifacts.NextCursor is { } next
                    ? artifactPages.Protect(JsonSerializer.Serialize(new ArtifactPage(user, observatory, camera, capture, next))) : null
                    }
                };
                break;
            default: throw new ArgumentException("Invalid workspace section.", nameof(query));
        }
        if (query.Revision is { } revision)
            result = result with { Revision = await catalog.GetRevisionAsync(revision, cancellationToken).ConfigureAwait(false) };
        // Role loss during a multi-command read must clear both evidence and stale mutation affordances.
        return await AuthorityAsync(user, observatory, camera, cancellationToken).ConfigureAwait(false) == authority ? result : null;
    }

    private sealed record ArtifactPage(string User, Guid Observatory, Guid Camera, Guid Capture, string Cursor);
    private string? DecodeArtifactPage(string? encoded, string user, Guid observatory, Guid camera, Guid capture)
    {
        if (encoded is null) return null;
        try
        {
            if (encoded.Length is 0 or > 8192) throw new FormatException();
            var page = JsonSerializer.Deserialize<ArtifactPage>(artifactPages.Unprotect(encoded));
            if (page is null || page.User != user || page.Observatory != observatory || page.Camera != camera || page.Capture != capture)
                throw new FormatException();
            return page.Cursor;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        { throw new ArgumentException("The artifact continuation is invalid for this scope.", nameof(encoded)); }
    }

    internal Task<bool> CaptureAllowedAsync(Guid observatory, Guid camera, Guid capture, CancellationToken token)
        => db.CentralFrames.AsNoTracking().AnyAsync(f => f.Id == capture && f.ObservatoryId == observatory &&
            f.LogicalCameraInstallation!.LogicalCameraId == camera, token);

    private Task<ObservatoryMembershipRole?> AuthorityAsync(string user, Guid observatory, Guid camera, CancellationToken token)
        => db.LogicalCameras.AsNoTracking().Where(c => c.Id == camera && c.ObservatoryId == observatory)
            .Join(ObservatoryMembershipAccess.ForUser(db, user), c => c.ObservatoryId, m => m.ObservatoryId,
                (c, m) => (ObservatoryMembershipRole?)m.Role).SingleOrDefaultAsync(token);
}
