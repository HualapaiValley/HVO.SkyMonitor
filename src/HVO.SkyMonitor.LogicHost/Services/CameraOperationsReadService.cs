using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>One bounded page. <see cref="NextCursor"/> is null only when no later row exists.</summary>
internal sealed record CameraOperationsPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// A page anchored on the camera's own captures. Each request examines at most
/// <see cref="CameraOperationsReadService.CaptureExaminationLimit"/> captures, so a page can be short or empty while
/// <see cref="NextCursor"/> still continues; <see cref="ExaminedCaptures"/> counts only this camera's captures.
/// </summary>
internal sealed record CameraCaptureAnchoredPage<T>(IReadOnlyList<T> Items, string? NextCursor, int ExaminedCaptures);

internal sealed record CameraProcessingGraphAssignmentSummary(
    Guid Id,
    Guid RevisionId,
    string RevisionName,
    string Revision,
    CentralProcessingGraphLifecycle RevisionLifecycle,
    string DefinitionIdentitySha256,
    CentralProcessingGraphTargetHost TargetHost,
    CentralProcessingGraphAssignmentScope Scope,
    Guid? ObservatoryId,
    Guid? LogicalCameraId,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    DateTimeOffset CreatedAtUtc,
    string ReasonCode);

internal sealed record CameraProcessingGraphNodeSummary(
    Guid JobId,
    int Ordinal,
    string NodeId,
    CentralDerivativeJobStatus Status,
    ProcessingGraphNodeFailurePolicy? FailurePolicy,
    string? StateReasonCode,
    string RecipeName,
    FrameArtifactRole TargetRole,
    string TargetVariant,
    string TargetRecipeVersion,
    string? SharedNodePlanIdentitySha256,
    int AttemptCount,
    int MaxAttempts,
    int InputCount,
    int RequiredInputCount,
    int ResolvedInputCount,
    int OutputCount,
    int BoundOutputCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>
/// One central execution with its immutable identities, every frozen source (the compiler bounds sources to
/// <see cref="ProcessingGraphCompiler.MaximumSources"/>), and one keyset page of nodes in graph ordinal order.
/// Frozen definition and plan JSON are not repeated here; the exact revision is read by identity.
/// </summary>
internal sealed record CameraProcessingGraphExecutionInspector(
    CentralProcessingGraphExecutionSummary Execution,
    Guid LogicalCameraInstallationId,
    Guid? AssignmentId,
    string DefinitionIdentitySha256,
    string CentralPlanIdentitySha256,
    string AnchorSourceChecksumSha256,
    int ExpectedSourceCount,
    int ExpectedNodeCount,
    int ExpectedDependencyCount,
    int ExpectedOutputCount,
    DateTimeOffset? ExpandedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc,
    Guid? PredecessorExecutionId,
    Guid? SuccessorExecutionId,
    IReadOnlyList<CentralProcessingGraphExecutionSourceView> Sources,
    CameraOperationsPage<CameraProcessingGraphNodeSummary> Nodes);

internal interface ICameraOperationsReadService
{
    Task<CameraOperationsPage<CameraProcessingGraphAssignmentSummary>> ListAssignmentsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, CentralProcessingGraphTargetHost targetHost,
        int take, string? cursor, CancellationToken cancellationToken = default);

    Task<CameraOperationsPage<CentralProcessingGraphExecutionSummary>> ListExecutionsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, int take, string? cursor,
        CancellationToken cancellationToken = default);

    Task<CameraProcessingGraphExecutionInspector?> GetExecutionAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid executionId, int take, string? cursor,
        CancellationToken cancellationToken = default);

    Task<CameraCaptureAnchoredPage<CameraJobHistoryItem>> ListJobsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, int take, string? cursor,
        CancellationToken cancellationToken = default);

    Task<CameraJobInspector?> GetJobAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid jobId, int take,
        CancellationToken cancellationToken = default);

    Task<CameraOperationsPage<OperationsDerivativeAttemptSummary>?> ListJobAttemptsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid jobId, int take, string? cursor,
        CancellationToken cancellationToken = default);

    Task<CameraOperationsPage<CameraJobInputView>?> ListJobInputsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid jobId, int take, string? cursor,
        CancellationToken cancellationToken = default);

    Task<CameraOperationsPage<CameraJobOutputView>?> ListJobOutputsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid jobId, int take, string? cursor,
        CancellationToken cancellationToken = default);

    Task<CameraOperationsPage<CameraProcessingGraphProposalSummary>> ListProposalsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, int take, string? cursor,
        CancellationToken cancellationToken = default);

    Task<CameraOperationsPage<ProcessingGraphDeliveryFactView>?> ListProposalFactsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid proposalId, int take, string? cursor,
        CancellationToken cancellationToken = default);

    Task<CameraCaptureAnchoredPage<CameraTransientEventAssociation>> ListEventAssociationsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, int take, string? cursor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorized, bounded reads of central processing and delivery evidence for one logical camera, across every
/// retained installation of that camera. Authority is the caller's current membership of the camera's observatory,
/// intersected with any credential observatory scope; a global reusable definition grants nothing here. Every
/// collection is keyset paged over an existing index with a scope-bound protected continuation; nothing is drained.
/// </summary>
internal sealed partial class CameraOperationsReadService(ApplicationDbContext db, IDataProtectionProvider protection)
    : ICameraOperationsReadService
{
    internal const int MaximumPageSize = 50;
    internal const int CaptureExaminationLimit = 50;
    private const int MaximumCursorLength = 4096;
    private readonly IDataProtector _cursorProtector = protection.CreateProtector("camera-operations-v1");

    public async Task<CameraOperationsPage<CameraProcessingGraphAssignmentSummary>> ListAssignmentsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, CentralProcessingGraphTargetHost targetHost,
        int take, string? cursor, CancellationToken cancellationToken = default)
    {
        ValidateTake(take);
        if (!Enum.IsDefined(targetHost)) throw new ArgumentException("The target host is invalid.", nameof(targetHost));
        var owner = Owner(principal, observatoryId);
        if (owner is null || !await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        var filter = targetHost.ToString();
        var position = Decode(cursor, owner, observatoryId, cameraId, "assignments", Guid.Empty, filter);
        var rows = await AssignmentHistory(db, observatoryId, cameraId, targetHost, position?.Time, position?.Id)
            .Take(take + 1)
            .Select(item => new AssignmentRow(
                item.Id, item.RevisionId, item.Revision!.Name, item.Revision.Revision, item.Revision.PublishedAtUtc,
                item.Revision.RetiredAtUtc, item.Revision.DefinitionIdentitySha256, item.TargetHost, item.Scope,
                item.ObservatoryId, item.LogicalCameraId, item.EffectiveFromUtc, item.EffectiveUntilUtc,
                item.CreatedAtUtc, item.ReasonCode))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        var more = Trim(rows, take);
        var items = rows.Select(row => new CameraProcessingGraphAssignmentSummary(
            row.Id, row.RevisionId, row.RevisionName, row.Revision,
            row.RetiredAtUtc is not null ? CentralProcessingGraphLifecycle.Retired
                : row.PublishedAtUtc is not null ? CentralProcessingGraphLifecycle.Published
                : CentralProcessingGraphLifecycle.Draft,
            row.DefinitionIdentitySha256, row.TargetHost, row.Scope, row.ObservatoryId, row.LogicalCameraId,
            row.EffectiveFromUtc, row.EffectiveUntilUtc, row.CreatedAtUtc, row.ReasonCode)).ToArray();
        return new(items, more
            ? Encode(new(owner, observatoryId, cameraId, "assignments", Guid.Empty, filter, rows[^1].EffectiveFromUtc, rows[^1].Id))
            : null);
    }

    public async Task<CameraOperationsPage<CentralProcessingGraphExecutionSummary>> ListExecutionsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, int take, string? cursor,
        CancellationToken cancellationToken = default)
    {
        ValidateTake(take);
        var owner = Owner(principal, observatoryId);
        if (owner is null || !await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        var position = Decode(cursor, owner, observatoryId, cameraId, "executions", Guid.Empty);
        var installations = await InstallationKeysAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false);
        var history = ExecutionHistory(db, installations.Select(item => item.Id).ToArray(), observatoryId, cameraId,
            position?.Time, position?.Id, take + 1);
        var rows = history is null
            ? []
            : await ProjectExecutions(history).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        var more = Trim(rows, take);
        return new(rows, more
            ? Encode(new(owner, observatoryId, cameraId, "executions", Guid.Empty, "", rows[^1].CreatedAtUtc, rows[^1].Id))
            : null);
    }

    public async Task<CameraProcessingGraphExecutionInspector?> GetExecutionAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid executionId, int take, string? cursor,
        CancellationToken cancellationToken = default)
    {
        ValidateTake(take);
        var owner = Owner(principal, observatoryId);
        if (owner is null) return null;
        var executions = CameraExecutions(owner, observatoryId, cameraId).Where(item => item.Id == executionId);
        var allExecutions = db.CentralProcessingGraphExecutions.AsNoTracking();
        var header = await executions
            .Select(item => new
            {
                item.LogicalCameraInstallationId,
                item.AssignmentId,
                item.DefinitionIdentitySha256,
                item.CentralPlanIdentitySha256,
                item.AnchorSourceChecksumSha256,
                item.ExpectedSourceCount,
                item.ExpectedNodeCount,
                item.ExpectedDependencyCount,
                item.ExpectedOutputCount,
                item.ExpandedAtUtc,
                item.StartedAtUtc,
                item.CancellationRequestedAtUtc,
                // Successor/predecessor identities stay within the same observatory; replay never crosses it.
                PredecessorExecutionId = allExecutions
                    .Where(other => other.Id == item.PredecessorExecutionId && other.ObservatoryId == observatoryId)
                    .Select(other => (Guid?)other.Id).FirstOrDefault(),
                SuccessorExecutionId = allExecutions
                    .Where(other => other.PredecessorExecutionId == item.Id && other.ObservatoryId == observatoryId)
                    .Select(other => (Guid?)other.Id).FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (header is null) return null;
        // Decode only after the execution is authorized, so a denied caller never learns cursor validity.
        var position = Decode(cursor, owner, observatoryId, cameraId, "nodes", executionId);
        var summary = await ProjectExecutions(executions).SingleAsync(cancellationToken).ConfigureAwait(false);
        var sources = await db.CentralProcessingGraphExecutionSources.AsNoTracking()
            .Where(source => source.ExecutionId == executionId)
            .OrderBy(source => source.Ordinal)
            .Take(ProcessingGraphCompiler.MaximumSources)
            .Select(source => new CentralProcessingGraphExecutionSourceView(
                source.Ordinal, source.SourceId, source.ArtifactId, source.ArtifactChecksumSha256,
                source.ArtifactByteLength, source.SelectedAtUtc))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var nodes = db.CentralDerivativeJobs.AsNoTracking().Where(job => job.GraphExecutionId == executionId);
        if (position is not null)
        {
            var ordinal = position.Ordinal;
            nodes = nodes.Where(job => job.GraphNodeOrdinal > ordinal);
        }
        var nodeRows = await nodes.OrderBy(job => job.GraphNodeOrdinal).Take(take + 1)
            .Select(job => new CameraProcessingGraphNodeSummary(
                job.Id, job.GraphNodeOrdinal!.Value, job.GraphNodeId!, job.Status, job.GraphFailurePolicy,
                job.StateReasonCode, job.RecipeName, job.TargetRole, job.TargetVariant, job.TargetRecipeVersion,
                job.SharedNodePlanIdentitySha256, job.AttemptCount, job.MaxAttempts,
                job.InputRequirements.Count(),
                job.InputRequirements.Count(requirement => requirement.IsRequired),
                job.InputRequirements.Count(requirement =>
                    requirement.ResolutionState == CentralDerivativeInputResolutionState.Resolved),
                job.Outputs.Count(),
                job.Outputs.Count(output => output.ResultCentralArtifactId != null),
                job.CreatedAtUtc, job.UpdatedAtUtc, job.CompletedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // A membership removal during the multi-command read conceals the whole inspector.
        if (!await executions.AnyAsync(cancellationToken).ConfigureAwait(false)) return null;
        var more = Trim(nodeRows, take);
        return new(
            summary,
            header.LogicalCameraInstallationId,
            header.AssignmentId,
            header.DefinitionIdentitySha256,
            header.CentralPlanIdentitySha256,
            header.AnchorSourceChecksumSha256,
            header.ExpectedSourceCount,
            header.ExpectedNodeCount,
            header.ExpectedDependencyCount,
            header.ExpectedOutputCount,
            header.ExpandedAtUtc,
            header.StartedAtUtc,
            header.CancellationRequestedAtUtc,
            header.PredecessorExecutionId,
            header.SuccessorExecutionId,
            sources,
            new(nodeRows, more
                ? Encode(new(owner, observatoryId, cameraId, "nodes", executionId, "", default, Guid.Empty, nodeRows[^1].Ordinal))
                : null));
    }

    /// <summary>
    /// Assignments that can govern this camera: its own camera-scoped rows, its observatory's rows and global
    /// defaults, newest effective first. Each branch seeks an existing (TargetHost, scope key, EffectiveFromUtc, Id)
    /// index; the keyset is anchored with <c>&lt;=</c> so equal timestamps cost only the tied rows.
    /// </summary>
    internal static IQueryable<CentralProcessingGraphAssignment> AssignmentHistory(
        ApplicationDbContext db, Guid observatoryId, Guid cameraId, CentralProcessingGraphTargetHost targetHost,
        DateTimeOffset? afterTime, Guid? afterId)
    {
        var assignments = db.CentralProcessingGraphAssignments.AsNoTracking().Where(item => item.TargetHost == targetHost);
        if (afterTime is { } time && afterId is { } id)
            assignments = assignments.Where(item => item.EffectiveFromUtc <= time &&
                (item.EffectiveFromUtc < time || item.Id.CompareTo(id) < 0));
        return assignments
                .Where(item => item.Scope == CentralProcessingGraphAssignmentScope.LogicalCamera &&
                    item.LogicalCameraId == cameraId && item.ObservatoryId == observatoryId)
            .Concat(assignments.Where(item => item.Scope == CentralProcessingGraphAssignmentScope.Observatory &&
                item.ObservatoryId == observatoryId))
            .Concat(assignments.Where(item => item.Scope == CentralProcessingGraphAssignmentScope.GlobalDefault))
            .OrderByDescending(item => item.EffectiveFromUtc).ThenByDescending(item => item.Id);
    }

    /// <summary>
    /// Executions of the given retained installations of the camera, newest first: one bounded TOP seek of
    /// (LogicalCameraInstallationId, CreatedAtUtc, Id) per installation, merged by UNION ALL. A correlated per-group
    /// Take is avoided deliberately; EF decorrelates it into ROW_NUMBER over the camera's complete history.
    /// </summary>
    internal static IQueryable<CentralProcessingGraphExecution>? ExecutionHistory(
        ApplicationDbContext db, IReadOnlyList<Guid> installationIds, Guid observatoryId, Guid cameraId,
        DateTimeOffset? afterTime, Guid? afterId, int limit)
    {
        var executions = db.CentralProcessingGraphExecutions.AsNoTracking()
            .Where(item => item.ObservatoryId == observatoryId && item.LogicalCameraId == cameraId);
        if (afterTime is { } time && afterId is { } id)
            executions = executions.Where(item => item.CreatedAtUtc <= time &&
                (item.CreatedAtUtc < time || item.Id.CompareTo(id) < 0));
        return PerInstallation(installationIds, installationId => executions
                .Where(item => item.LogicalCameraInstallationId == installationId)
                .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id)
                .Take(limit))
            ?.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id)
            .Take(limit);
    }

    /// <summary>One independently bounded branch per installation; null when the camera has no installation.</summary>
    private static IQueryable<T>? PerInstallation<TKey, T>(IReadOnlyList<TKey> installations, Func<TKey, IQueryable<T>> seek)
    {
        IQueryable<T>? union = null;
        foreach (var installation in installations)
        {
            var branch = seek(installation);
            union = union is null ? branch : union.Concat(branch);
        }
        return union;
    }

    /// <summary>The camera's retained installations, including retired and replaced ones, in assignment order.</summary>
    private Task<List<InstallationKey>> InstallationKeysAsync(
        string owner, Guid observatoryId, Guid cameraId, CancellationToken cancellationToken)
        => CameraInstallations(db, owner, observatoryId, cameraId)
            .OrderBy(installation => installation.AssignedAtUtc).ThenBy(installation => installation.Id)
            .Select(installation => new InstallationKey(
                installation.Id, installation.RegistrationId, installation.InstallationPublicId))
            .ToListAsync(cancellationToken);

    private static IQueryable<CentralProcessingGraphExecutionSummary> ProjectExecutions(
        IQueryable<CentralProcessingGraphExecution> query)
        => query.Select(execution => new CentralProcessingGraphExecutionSummary(
            execution.Id,
            execution.ExecutionClass.ToString(),
            execution.Status.ToString(),
            execution.RevisionId,
            execution.ObservatoryId,
            execution.LogicalCameraId,
            execution.InstallationPublicId,
            execution.AnchorSourceArtifactId,
            execution.Trigger.ToString(),
            execution.ActorId,
            execution.ReasonCode,
            execution.Sources.Count,
            execution.Jobs.Count,
            execution.CreatedAtUtc,
            execution.UpdatedAtUtc,
            execution.CompletedAtUtc));

    /// <summary>Every retained installation of the camera, including retired and replaced ones.</summary>
    internal static IQueryable<LogicalCameraInstallation> CameraInstallations(
        ApplicationDbContext db, string owner, Guid observatoryId, Guid cameraId)
    {
        var memberships = ObservatoryMembershipAccess.ForUser(db, owner);
        return db.LogicalCameraInstallations.AsNoTracking().Where(installation =>
            installation.LogicalCameraId == cameraId &&
            installation.LogicalCamera!.ObservatoryId == observatoryId &&
            memberships.Any(membership => membership.ObservatoryId == observatoryId));
    }

    private IQueryable<CentralProcessingGraphExecution> CameraExecutions(string owner, Guid observatoryId, Guid cameraId)
    {
        var memberships = ObservatoryMembershipAccess.ForUser(db, owner);
        return db.CentralProcessingGraphExecutions.AsNoTracking().Where(execution =>
            execution.ObservatoryId == observatoryId &&
            execution.LogicalCameraId == cameraId &&
            execution.LogicalCameraInstallation!.LogicalCameraId == cameraId &&
            execution.LogicalCameraInstallation.LogicalCamera!.ObservatoryId == observatoryId &&
            memberships.Any(membership => membership.ObservatoryId == observatoryId));
    }

    private static string? Owner(ClaimsPrincipal principal, Guid observatoryId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var owner = CentralArtifactCredentialAccess.GetOwnerId(principal);
        return CentralArtifactCredentialAccess.HasOwnerCredential(principal) && !string.IsNullOrWhiteSpace(owner) &&
            CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, observatoryId)
                ? owner
                : null;
    }

    private Task<bool> CanReadAsync(string owner, Guid observatoryId, Guid cameraId, CancellationToken cancellationToken)
    {
        var memberships = ObservatoryMembershipAccess.ForUser(db, owner);
        return db.LogicalCameras.AsNoTracking().AnyAsync(camera => camera.Id == cameraId &&
            camera.ObservatoryId == observatoryId &&
            memberships.Any(membership => membership.ObservatoryId == observatoryId), cancellationToken);
    }

    private static void ValidateTake(int take)
    {
        if (take is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(take));
    }

    private static bool Trim<T>(List<T> rows, int take)
    {
        if (rows.Count <= take) return false;
        rows.RemoveRange(take, rows.Count - take);
        return true;
    }

    private string Encode(OperationsCursor value)
        => _cursorProtector.Protect(JsonSerializer.Serialize(value));

    /// <summary>
    /// A continuation is valid only for the same caller, observatory, camera, collection, subject and filter that
    /// issued it; anything else is rejected rather than reinterpreted under another scope.
    /// </summary>
    private OperationsCursor? Decode(
        string? cursor, string owner, Guid observatoryId, Guid cameraId, string purpose, Guid subject, string filter = "")
    {
        if (cursor is null) return null;
        try
        {
            if (cursor.Length is 0 or > MaximumCursorLength) throw new FormatException();
            var value = JsonSerializer.Deserialize<OperationsCursor>(_cursorProtector.Unprotect(cursor));
            if (value is null || value.Owner != owner || value.Observatory != observatoryId || value.Camera != cameraId ||
                value.Purpose != purpose || value.Subject != subject || value.Filter != filter)
                throw new FormatException();
            return value;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw new ArgumentException("The continuation is invalid for this request.", nameof(cursor));
        }
    }

    private sealed record OperationsCursor(
        string Owner,
        Guid Observatory,
        Guid Camera,
        string Purpose,
        Guid Subject,
        string Filter,
        DateTimeOffset Time,
        Guid Id,
        int Ordinal = 0,
        DateTimeOffset? ChildTime = null,
        Guid? ChildId = null);

    internal sealed record InstallationKey(Guid Id, Guid RegistrationId, Guid InstallationPublicId);

    private sealed record AssignmentRow(
        Guid Id,
        Guid RevisionId,
        string RevisionName,
        string Revision,
        DateTimeOffset? PublishedAtUtc,
        DateTimeOffset? RetiredAtUtc,
        string DefinitionIdentitySha256,
        CentralProcessingGraphTargetHost TargetHost,
        CentralProcessingGraphAssignmentScope Scope,
        Guid? ObservatoryId,
        Guid? LogicalCameraId,
        DateTimeOffset EffectiveFromUtc,
        DateTimeOffset? EffectiveUntilUtc,
        DateTimeOffset CreatedAtUtc,
        string ReasonCode);
}
