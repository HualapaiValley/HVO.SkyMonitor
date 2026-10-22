using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CameraJobHistoryItem(
    Guid CaptureId,
    DateTimeOffset CapturedAtUtc,
    Guid LogicalCameraInstallationId,
    Guid JobId,
    CentralDerivativeJobStatus Status,
    string? StateReasonCode,
    FrameArtifactRole TargetRole,
    string TargetVariant,
    string TargetRecipeVersion,
    string RecipeName,
    int AttemptCount,
    int MaxAttempts,
    Guid? GraphExecutionId,
    string? GraphNodeId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>
/// One central job and its immediate lineage. Lease owner, lease token, lease expiry, trace context, free-form error
/// text and the requesting actor are deliberately absent; related identities from another observatory are null.
/// </summary>
internal sealed record CameraJobDetail(
    Guid JobId,
    Guid CaptureId,
    Guid LogicalCameraInstallationId,
    Guid SourceArtifactId,
    FrameArtifactRole SourceArtifactRole,
    string SourceArtifactChecksumSha256,
    CentralDerivativeJobStatus Status,
    string? StateReasonCode,
    FrameArtifactRole TargetRole,
    string TargetVariant,
    string TargetRecipeVersion,
    string RecipeName,
    string RequestedRecipeIdentitySha256,
    string ExpectedRecipeIdentitySha256,
    string RequestIdentitySha256,
    string? InputSetIdentitySha256,
    CentralDerivativeWaitKind? WaitKind,
    CentralDerivativeWindowOutcome? MissingInputOutcome,
    int? MinimumInputCount,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? AvailableAtUtc,
    DateTimeOffset? ResolutionDeadlineUtc,
    DateTimeOffset? ResolutionStartedAtUtc,
    DateTimeOffset? ResolutionCompletedAtUtc,
    DateTimeOffset? LastFailedAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Guid? ResultArtifactId,
    Guid? RetainedResultArtifactId,
    Guid? PredecessorJobId,
    Guid? SupersededByJobId,
    Guid? GraphExecutionId,
    string? GraphNodeId,
    int? GraphNodeOrdinal,
    ProcessingGraphNodeFailurePolicy? GraphFailurePolicy,
    string? SharedNodePlanIdentitySha256);

/// <summary>
/// One frozen input requirement and, once resolved, the exact selected input. Artifact identity and checksum are
/// shown only for artifacts of the same observatory, matching the capture trace's lineage concealment.
/// </summary>
internal sealed record CameraJobInputView(
    int Ordinal,
    string BindingName,
    CentralDerivativeInputSourceKind SourceKind,
    bool IsRequired,
    CentralDerivativeInputResolutionState ResolutionState,
    string? ResolutionReasonCode,
    DateTimeOffset? ResolvedAtUtc,
    int? GraphInputOrdinal,
    ProcessingGraphInputBindingKind? GraphInputBindingKind,
    Guid? ProducerJobId,
    Guid? ArtifactId,
    Guid? ArtifactCaptureId,
    FrameArtifactRole? ArtifactRole,
    string? ArtifactChecksumSha256,
    long? InputByteLength,
    DateTimeOffset? SelectedAtUtc,
    Guid? EnvironmentalObservationId,
    string? CanonicalIdentitySha256);

internal sealed record CameraJobOutputView(
    int Ordinal,
    FrameArtifactRole Role,
    string Variant,
    ProcessingProductKind ProductKind,
    string ContractIdentitySha256,
    Guid? ArtifactId,
    string? OutputIdentitySha256,
    DateTimeOffset? BoundAtUtc);

/// <summary>The job plus the first bounded page of each child collection; each page continues independently.</summary>
internal sealed record CameraJobInspector(
    CameraJobDetail Job,
    CameraOperationsPage<OperationsDerivativeAttemptSummary> Attempts,
    CameraOperationsPage<CameraJobInputView> Inputs,
    CameraOperationsPage<CameraJobOutputView> Outputs);

internal sealed partial class CameraOperationsReadService
{
    /// <summary>
    /// Jobs whose source artifact belongs to one of the camera's captures, newest capture first and then newest job.
    /// No index orders jobs by camera, so each request walks at most <see cref="CaptureExaminationLimit"/> of the
    /// camera's own captures through existing capture, artifact and job indexes and never reads another camera's rows.
    /// </summary>
    public Task<CameraCaptureAnchoredPage<CameraJobHistoryItem>> ListJobsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, int take, string? cursor,
        CancellationToken cancellationToken = default)
        => CaptureAnchoredAsync(
            principal, observatoryId, cameraId, "jobs", take, cursor,
            (owner, position, limit) =>
            {
                var capture = position.Id;
                var time = position.ChildTime!.Value;
                var id = position.ChildId!.Value;
                return ProjectJobHistory(CameraJobs(owner, observatoryId, cameraId)
                        .Where(job => job.SourceArtifact!.CentralFrameId == capture &&
                            job.CreatedAtUtc <= time && (job.CreatedAtUtc < time || job.Id.CompareTo(id) < 0)))
                    .Take(limit).ToListAsync(cancellationToken);
            },
            (owner, captureIds, limit) => ProjectJobHistory(CameraJobs(owner, observatoryId, cameraId)
                    .Where(job => captureIds.Contains(job.SourceArtifact!.CentralFrameId)))
                .Take(limit).ToListAsync(cancellationToken),
            item => (item.CaptureId, item.CapturedAtUtc, item.CreatedAtUtc, item.JobId),
            cancellationToken);

    public async Task<CameraJobInspector?> GetJobAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid jobId, int take,
        CancellationToken cancellationToken = default)
    {
        ValidateTake(take);
        var owner = Owner(principal, observatoryId);
        if (owner is null) return null;
        var job = CameraJobs(owner, observatoryId, cameraId).Where(item => item.Id == jobId);
        var jobs = db.CentralDerivativeJobs.AsNoTracking();
        var detail = await job.Select(item => new CameraJobDetail(
                item.Id,
                item.SourceArtifact!.CentralFrameId,
                item.SourceArtifact.Frame!.LogicalCameraInstallationId!.Value,
                item.SourceArtifact.ArtifactId,
                item.SourceArtifact.Role,
                item.SourceArtifact.ChecksumSha256,
                item.Status,
                item.StateReasonCode,
                item.TargetRole,
                item.TargetVariant,
                item.TargetRecipeVersion,
                item.RecipeName,
                item.RequestedRecipeIdentitySha256,
                item.ExpectedRecipeIdentitySha256,
                item.RequestIdentitySha256,
                item.InputSetIdentitySha256,
                item.WaitKind,
                item.MissingInputOutcome,
                item.MinimumInputCount,
                item.AttemptCount,
                item.MaxAttempts,
                item.CreatedAtUtc,
                item.UpdatedAtUtc,
                item.AvailableAtUtc,
                item.ResolutionDeadlineUtc,
                item.ResolutionStartedAtUtc,
                item.ResolutionCompletedAtUtc,
                item.LastFailedAtUtc,
                item.CancellationRequestedAtUtc,
                item.CompletedAtUtc,
                item.ResultArtifact != null && item.ResultArtifact.Frame!.ObservatoryId == observatoryId
                    ? (Guid?)item.ResultArtifact.ArtifactId
                    : null,
                item.RetainedResultCentralArtifactId != null
                    ? db.CentralArtifacts
                        .Where(artifact => artifact.Id == item.RetainedResultCentralArtifactId &&
                            artifact.Frame!.ObservatoryId == observatoryId)
                        .Select(artifact => (Guid?)artifact.ArtifactId).FirstOrDefault()
                    : null,
                jobs.Where(other => other.Id == item.PredecessorJobId &&
                        other.SourceArtifact!.Frame!.ObservatoryId == observatoryId)
                    .Select(other => (Guid?)other.Id).FirstOrDefault(),
                jobs.Where(other => other.Id == item.SupersededByJobId &&
                        other.SourceArtifact!.Frame!.ObservatoryId == observatoryId)
                    .Select(other => (Guid?)other.Id).FirstOrDefault(),
                item.GraphExecutionId,
                item.GraphNodeId,
                item.GraphNodeOrdinal,
                item.GraphFailurePolicy,
                item.SharedNodePlanIdentitySha256))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (detail is null) return null;
        var attempts = await Attempts(jobId, null, take).ToListAsync(cancellationToken).ConfigureAwait(false);
        var inputs = await Inputs(jobId, observatoryId, null, take).ToListAsync(cancellationToken).ConfigureAwait(false);
        var outputs = await Outputs(jobId, observatoryId, null, take).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!await job.AnyAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new(
            detail,
            Page(attempts, take, owner, observatoryId, cameraId, "attempts", jobId, item => item.AttemptNumber),
            Page(inputs, take, owner, observatoryId, cameraId, "inputs", jobId, item => item.Ordinal),
            Page(outputs, take, owner, observatoryId, cameraId, "outputs", jobId, item => item.Ordinal));
    }

    public Task<CameraOperationsPage<OperationsDerivativeAttemptSummary>?> ListJobAttemptsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid jobId, int take, string? cursor,
        CancellationToken cancellationToken = default)
        => JobChildPageAsync(principal, observatoryId, cameraId, jobId, take, cursor, "attempts",
            after => Attempts(jobId, after, take), item => item.AttemptNumber, cancellationToken);

    public Task<CameraOperationsPage<CameraJobInputView>?> ListJobInputsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid jobId, int take, string? cursor,
        CancellationToken cancellationToken = default)
        => JobChildPageAsync(principal, observatoryId, cameraId, jobId, take, cursor, "inputs",
            after => Inputs(jobId, observatoryId, after, take), item => item.Ordinal, cancellationToken);

    public Task<CameraOperationsPage<CameraJobOutputView>?> ListJobOutputsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid jobId, int take, string? cursor,
        CancellationToken cancellationToken = default)
        => JobChildPageAsync(principal, observatoryId, cameraId, jobId, take, cursor, "outputs",
            after => Outputs(jobId, observatoryId, after, take), item => item.Ordinal, cancellationToken);

    private async Task<CameraOperationsPage<T>?> JobChildPageAsync<T>(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid jobId, int take, string? cursor,
        string purpose, Func<int?, IQueryable<T>> query, Func<T, int> ordinal, CancellationToken cancellationToken)
    {
        ValidateTake(take);
        var owner = Owner(principal, observatoryId);
        if (owner is null) return null;
        var job = CameraJobs(owner, observatoryId, cameraId).Where(item => item.Id == jobId);
        if (!await job.AnyAsync(cancellationToken).ConfigureAwait(false)) return null;
        var position = Decode(cursor, owner, observatoryId, cameraId, purpose, jobId);
        var rows = await query(position?.Ordinal).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!await job.AnyAsync(cancellationToken).ConfigureAwait(false)) return null;
        return Page(rows, take, owner, observatoryId, cameraId, purpose, jobId, ordinal);
    }

    private CameraOperationsPage<T> Page<T>(
        List<T> rows, int take, string owner, Guid observatoryId, Guid cameraId, string purpose, Guid subject,
        Func<T, int> ordinal)
    {
        var more = Trim(rows, take);
        return new(rows, more
            ? Encode(new(owner, observatoryId, cameraId, purpose, subject, "", default, Guid.Empty, ordinal(rows[^1])))
            : null);
    }

    /// <summary>Attempts by the unique (job, attempt number) index. Lease timestamps are history, not credentials.</summary>
    private IQueryable<OperationsDerivativeAttemptSummary> Attempts(Guid jobId, int? afterAttemptNumber, int take)
    {
        var attempts = db.CentralDerivativeJobAttempts.AsNoTracking().Where(item => item.CentralDerivativeJobId == jobId);
        if (afterAttemptNumber is { } after) attempts = attempts.Where(item => item.AttemptNumber > after);
        return attempts.OrderBy(item => item.AttemptNumber).Take(take + 1)
            .Select(item => new OperationsDerivativeAttemptSummary(
                item.AttemptNumber, item.WorkerId, item.Outcome, item.ReasonCode, item.LeaseAcquiredAtUtc,
                item.EndedAtUtc, item.InputBytes, item.OutputBytes));
    }

    /// <summary>Requirements by the unique (job, ordinal) index, each with its exact resolved input when present.</summary>
    private IQueryable<CameraJobInputView> Inputs(Guid jobId, Guid observatoryId, int? afterOrdinal, int take)
    {
        var requirements = db.CentralDerivativeJobInputRequirements.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == jobId);
        if (afterOrdinal is { } after) requirements = requirements.Where(item => item.Ordinal > after);
        return requirements.OrderBy(item => item.Ordinal).Take(take + 1)
            .Select(item => new CameraJobInputView(
                item.Ordinal,
                item.BindingName,
                item.SourceKind,
                item.IsRequired,
                item.ResolutionState,
                item.ResolutionReasonCode,
                item.ResolvedAtUtc,
                item.GraphInputOrdinal,
                item.GraphInputBindingKind,
                item.GraphDependency != null ? item.GraphDependency.ProducerJobId : null,
                item.Input != null && item.Input.Artifact!.Frame!.ObservatoryId == observatoryId
                    ? (Guid?)item.Input.Artifact.ArtifactId
                    : null,
                item.Input != null && item.Input.Artifact!.Frame!.ObservatoryId == observatoryId
                    ? (Guid?)item.Input.Artifact.CentralFrameId
                    : null,
                item.Input != null && item.Input.Artifact!.Frame!.ObservatoryId == observatoryId
                    ? (FrameArtifactRole?)item.Input.Artifact.Role
                    : null,
                item.Input != null && item.Input.Artifact!.Frame!.ObservatoryId == observatoryId
                    ? item.Input.Artifact.ChecksumSha256
                    : null,
                item.Input != null && item.Input.Artifact!.Frame!.ObservatoryId == observatoryId
                    ? (long?)item.Input.ByteLength
                    : null,
                item.Input != null && item.Input.Artifact!.Frame!.ObservatoryId == observatoryId
                    ? (DateTimeOffset?)item.Input.SelectedAtUtc
                    : null,
                item.CanonicalInput != null && item.CanonicalInput.EnvironmentalObservation != null &&
                    item.CanonicalInput.EnvironmentalObservation.SiteId == observatoryId
                    ? item.CanonicalInput.EnvironmentalObservationRecordId
                    : null,
                item.CanonicalInput != null && (item.CanonicalInput.EnvironmentalObservationRecordId == null ||
                    item.CanonicalInput.EnvironmentalObservation!.SiteId == observatoryId)
                    ? item.CanonicalInput.IdentitySha256
                    : null));
    }

    /// <summary>Graph output contracts by the (job, ordinal) alternate key; non-graph jobs have none.</summary>
    private IQueryable<CameraJobOutputView> Outputs(Guid jobId, Guid observatoryId, int? afterOrdinal, int take)
    {
        var outputs = db.CentralDerivativeJobOutputs.AsNoTracking().Where(item => item.CentralDerivativeJobId == jobId);
        if (afterOrdinal is { } after) outputs = outputs.Where(item => item.Ordinal > after);
        return outputs.OrderBy(item => item.Ordinal).Take(take + 1)
            .Select(item => new CameraJobOutputView(
                item.Ordinal,
                item.Role,
                item.Variant,
                item.ProductKind,
                item.ContractIdentitySha256,
                item.ResultArtifact != null && item.ResultArtifact.Frame!.ObservatoryId == observatoryId
                    ? (Guid?)item.ResultArtifact.ArtifactId
                    : null,
                item.ResultOutputIdentitySha256,
                item.BoundAtUtc));
    }

    private static IQueryable<CameraJobHistoryItem> ProjectJobHistory(IQueryable<CentralDerivativeJob> query)
        => query
            .OrderByDescending(job => job.SourceArtifact!.Frame!.CapturedAtUtc)
            .ThenByDescending(job => job.SourceArtifact!.CentralFrameId)
            .ThenByDescending(job => job.CreatedAtUtc)
            .ThenByDescending(job => job.Id)
            .Select(job => new CameraJobHistoryItem(
                job.SourceArtifact!.CentralFrameId,
                job.SourceArtifact.Frame!.CapturedAtUtc,
                job.SourceArtifact.Frame.LogicalCameraInstallationId!.Value,
                job.Id,
                job.Status,
                job.StateReasonCode,
                job.TargetRole,
                job.TargetVariant,
                job.TargetRecipeVersion,
                job.RecipeName,
                job.AttemptCount,
                job.MaxAttempts,
                job.GraphExecutionId,
                job.GraphNodeId,
                job.CreatedAtUtc,
                job.UpdatedAtUtc,
                job.CompletedAtUtc));

    /// <summary>
    /// A job belongs to the camera through its source capture's retained installation; graph node jobs carry the
    /// execution's anchor source, so the same relationship covers both graph and non-graph jobs.
    /// </summary>
    private IQueryable<CentralDerivativeJob> CameraJobs(string owner, Guid observatoryId, Guid cameraId)
    {
        var memberships = ObservatoryMembershipAccess.ForUser(db, owner);
        return db.CentralDerivativeJobs.AsNoTracking().Where(job =>
            job.SourceArtifact!.Frame!.ObservatoryId == observatoryId &&
            job.SourceArtifact.Frame.LogicalCameraInstallation!.LogicalCameraId == cameraId &&
            job.SourceArtifact.Frame.LogicalCameraInstallation.LogicalCamera!.ObservatoryId == observatoryId &&
            memberships.Any(membership => membership.ObservatoryId == observatoryId));
    }

    /// <summary>
    /// The camera's captures strictly after a capture position, newest first: one bounded TOP seek of
    /// (LogicalCameraInstallationId, CapturedAtUtc, Id) per retained installation, merged by UNION ALL.
    /// </summary>
    internal static IQueryable<CentralFrame>? CaptureWindow(
        ApplicationDbContext db, IReadOnlyList<Guid> installationIds, Guid observatoryId,
        DateTimeOffset? afterTime, Guid? afterId, int limit)
    {
        var frames = db.CentralFrames.AsNoTracking().Where(frame => frame.ObservatoryId == observatoryId);
        if (afterTime is { } time && afterId is { } id)
            frames = frames.Where(frame => frame.CapturedAtUtc <= time &&
                (frame.CapturedAtUtc < time || frame.Id.CompareTo(id) < 0));
        return PerInstallation(installationIds, installationId => frames
                .Where(frame => frame.LogicalCameraInstallationId == installationId)
                .OrderByDescending(frame => frame.CapturedAtUtc).ThenByDescending(frame => frame.Id)
                .Take(limit))
            ?.OrderByDescending(frame => frame.CapturedAtUtc).ThenByDescending(frame => frame.Id)
            .Take(limit);
    }

    /// <summary>
    /// Pages rows keyed by (capture time, capture, child time, child) over the camera's captures. A continuation
    /// either resumes inside a capture after a child, or (child absent) after a fully examined capture. Every row
    /// read belongs to this camera, so a short page or examined count discloses nothing about another scope.
    /// </summary>
    private async Task<CameraCaptureAnchoredPage<T>> CaptureAnchoredAsync<T>(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, string purpose, int take, string? cursor,
        Func<string, OperationsCursor, int, Task<List<T>>> remainderOfCapture,
        Func<string, List<Guid>, int, Task<List<T>>> windowRows,
        Func<T, (Guid CaptureId, DateTimeOffset CapturedAtUtc, DateTimeOffset? ChildTime, Guid ChildId)> key,
        CancellationToken cancellationToken)
    {
        ValidateTake(take);
        var owner = Owner(principal, observatoryId);
        if (owner is null || !await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null, 0);
        var position = Decode(cursor, owner, observatoryId, cameraId, purpose, Guid.Empty);
        var items = new List<T>(take + 1);
        var examined = 0;
        if (position?.ChildId is not null)
        {
            items.AddRange(await remainderOfCapture(owner, position, take + 1).ConfigureAwait(false));
            examined++;
        }
        var moreCaptures = false;
        List<CapturePosition> captures = [];
        if (items.Count <= take)
        {
            var installations = await InstallationKeysAsync(owner, observatoryId, cameraId, cancellationToken)
                .ConfigureAwait(false);
            var window = CaptureWindow(db, installations.Select(item => item.Id).ToArray(), observatoryId,
                position?.Time, position?.Id, CaptureExaminationLimit + 1);
            captures = window is null
                ? []
                : await window.Select(frame => new CapturePosition(frame.Id, frame.CapturedAtUtc))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            moreCaptures = Trim(captures, CaptureExaminationLimit);
            examined += captures.Count;
            if (captures.Count > 0)
            {
                items.AddRange(await windowRows(owner, captures.Select(capture => capture.Id).ToList(), take + 1 - items.Count)
                    .ConfigureAwait(false));
            }
        }
        if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null, 0);
        string? next = null;
        if (Trim(items, take))
        {
            var last = key(items[^1]);
            next = Encode(new(owner, observatoryId, cameraId, purpose, Guid.Empty, "", last.CapturedAtUtc,
                last.CaptureId, 0, last.ChildTime, last.ChildId));
        }
        else if (moreCaptures)
        {
            next = Encode(new(owner, observatoryId, cameraId, purpose, Guid.Empty, "", captures[^1].CapturedAtUtc,
                captures[^1].Id));
        }
        return new(items, next, examined);
    }

    private sealed record CapturePosition(Guid Id, DateTimeOffset CapturedAtUtc);
}
