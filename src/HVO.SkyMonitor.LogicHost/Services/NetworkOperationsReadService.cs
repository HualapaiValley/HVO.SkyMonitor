using System.Text.Json;
using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record OperationsObservatorySummary(
    Guid ObservatoryId,
    string Name,
    ObservatoryMembershipRole Role,
    int LogicalCameraCount,
    int ActiveInstallationCount,
    int PendingDeploymentCount,
    int ActiveJobCount);

internal sealed record OperationsObservatoryPage(
    IReadOnlyList<OperationsObservatorySummary> Items,
    string? NextCursor);

internal sealed record OperationsCameraFleetSummary(
    Guid LogicalCameraId,
    string Name,
    string Description,
    Guid? InstallationPublicId,
    string? InstallationName,
    FleetHealth? ReportedHealth,
    DateTimeOffset? ObservedAtUtc,
    bool HasStoragePressure,
    bool HasRequiredLaneFailure,
    bool HasQuarantine,
    string? SoftwareVersion,
    bool IsPubliclyReleased);

internal sealed record OperationsCameraFleetPage(
    IReadOnlyList<OperationsCameraFleetSummary> Items,
    string? NextCursor);

internal sealed record OperationsEnvironmentalSummary(
    EnvironmentalObservationKind Kind,
    EnvironmentalObservationUnit Unit,
    double? NumericValue,
    bool? BooleanValue,
    EnvironmentalObservationQuality Quality,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset StaleAfterUtc);

internal sealed record OperationsCameraInstallationSummary(
    Guid InstallationPublicId,
    string InstallationName,
    DeviceRegistrationStatus RegistrationStatus,
    DateTimeOffset AssignedAtUtc,
    DateTimeOffset? RetiredAtUtc,
    Guid? ReplacesInstallationId,
    string AssignmentReasonCode,
    string? RetirementReasonCode);

internal sealed record OperationsCameraDetail(
    Guid LogicalCameraId,
    Guid ObservatoryId,
    string Name,
    string Description,
    IReadOnlyList<OperationsCameraInstallationSummary> Installations,
    string? InstallationsNextCursor);

internal sealed record OperationsCameraInstallationPage(
    IReadOnlyList<OperationsCameraInstallationSummary> Items,
    string? NextCursor);

internal sealed record OperationsRegistrationSummary(
    Guid RegistrationId,
    string FriendlyName,
    DeviceRegistrationStatus Status,
    Guid? AssignedLogicalCameraId);

internal sealed record OperationsRegistrationPage(
    IReadOnlyList<OperationsRegistrationSummary> Items,
    string? NextCursor);

internal sealed record OperationsObservatoryDetail(
    OperationsObservatorySummary Observatory,
    IReadOnlyList<OperationsCameraFleetSummary> Cameras,
    string? CamerasNextCursor,
    IReadOnlyList<OperationsEnvironmentalSummary> Environment,
    IReadOnlyList<OperationsRegistrationSummary> Registrations,
    string? RegistrationsNextCursor);

internal sealed record OperationsCaptureSummary(
    Guid CaptureId,
    Guid ObservatoryId,
    Guid? LogicalCameraId,
    Guid? LogicalCameraInstallationId,
    Guid? InstallationPublicId,
    string ObservatoryName,
    string? LogicalCameraName,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset FirstReceivedAtUtc,
    string? RigId,
    int? RigProfileVersion,
    Guid? DeviceRigProfileId,
    long? CaptureSequence,
    int ArtifactCount,
    bool HasRaw,
    bool HasPreview,
    bool HasAnnotation);

internal sealed record OperationsArtifactSummary(
    Guid ArtifactId,
    Guid PublicationSubjectId,
    FrameArtifactRole Role,
    string? Variant,
    string RecipeVersion,
    string MediaType,
    long ByteLength,
    string ChecksumSha256,
    CentralArtifactObjectState ObjectState,
    CentralReconstructionState ReconstructionState,
    string? StateReasonCode,
    DateTimeOffset ReceivedAtUtc,
    string ContentPath,
    string OriginLabel,
    int SourceCount,
    string? RecipeName,
    string? RecipeSemanticVersion,
    string? RecipeOptionsSha256,
    string ManifestSchemaVersion,
    bool IsPubliclyEligible,
    bool IsPubliclyReleased);

internal sealed record OperationsCaptureProfileSummary(
    CentralProfileKind Kind,
    string Name,
    string Version,
    string Sha256);

internal sealed record OperationsCaptureProvenanceSummary(
    string AgentId,
    Guid FrameId,
    CentralCaptureLocationEvidenceState LocationEvidenceState,
    string? LocationId,
    long? LocationVersion,
    DateTimeOffset? RequestedStartUtc,
    DateTimeOffset? ExposureStartedUtc,
    DateTimeOffset? ExposureEndedUtc,
    DateTimeOffset? ReadoutCompletedUtc,
    DateTimeOffset? DurableIngressUtc,
    long? EffectiveExposureTicks,
    double? EffectiveGain,
    double? EffectiveOffset,
    double? EffectiveTemperatureC,
    IReadOnlyList<OperationsCaptureProfileSummary> Profiles);

internal sealed record OperationsArtifactLineageSummary(
    Guid ArtifactId,
    int Ordinal,
    Guid? SourceArtifactId,
    Guid? ResolvedArtifactId,
    FrameArtifactRole? ResolvedRole,
    string? ResolvedVariant,
    string? ResolvedChecksumSha256);

internal sealed record OperationsDerivativeAttemptSummary(
    int AttemptNumber,
    string WorkerId,
    CentralDerivativeAttemptOutcome Outcome,
    string? ReasonCode,
    DateTimeOffset LeaseAcquiredAtUtc,
    DateTimeOffset? EndedAtUtc,
    long InputBytes,
    long OutputBytes);

internal sealed record OperationsDerivativeInputSummary(
    int Ordinal,
    string BindingName,
    CentralDerivativeInputSourceKind SourceKind,
    bool IsRequired,
    CentralDerivativeInputResolutionState ResolutionState,
    string? ResolutionReasonCode,
    Guid? ArtifactId,
    Guid? EnvironmentalObservationId,
    string? CanonicalIdentitySha256);

internal sealed record OperationsCaptureJobTrace(
    Guid JobId,
    FrameArtifactRole TargetRole,
    string TargetVariant,
    string RecipeName,
    string TargetRecipeVersion,
    CentralDerivativeJobStatus Status,
    CentralDerivativeWindowOutcome? MissingInputOutcome,
    string? StateReasonCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<OperationsDerivativeInputSummary> Inputs,
    IReadOnlyList<OperationsDerivativeAttemptSummary> Attempts);

internal sealed record OperationsEnvironmentalEvidenceSummary(
    Guid ObservationId,
    EnvironmentalObservationKind Kind,
    EnvironmentalObservationUnit Unit,
    double? NumericValue,
    bool? BooleanValue,
    EnvironmentalObservationQuality Quality,
    DateTimeOffset ObservedAtUtc,
    string Provider,
    string MethodName,
    string MethodVersion,
    string SourceIdentitySha256,
    int LineageSourceCount);

internal sealed record OperationsTransientEventTrace(
    Guid EventId,
    int? Version,
    TransientEventState? State,
    TransientClassification? EffectiveClassification,
    int? EffectiveConfidenceMillionths,
    CentralTransientReviewState? ReviewState,
    TransientReviewDisposition? LatestReviewDisposition,
    DateTimeOffset? UpdatedUtc,
    int ObservationCount,
    int AssessmentCount,
    int ReviewCount);

internal sealed record OperationsCaptureDetail(
    OperationsCaptureSummary Capture,
    IReadOnlyList<OperationsArtifactSummary> Artifacts,
    string? ArtifactsNextCursor,
    ObservatoryMembershipRole EffectiveRole,
    OperationsCaptureProvenanceSummary Provenance);

internal sealed record OperationsCaptureTrace(
    IReadOnlyList<OperationsArtifactLineageSummary> ArtifactLineage,
    IReadOnlyList<OperationsCaptureJobTrace> Jobs,
    IReadOnlyList<OperationsEnvironmentalEvidenceSummary> EnvironmentalEvidence,
    IReadOnlyList<OperationsTransientEventTrace> Events,
    bool TraceIsTruncated);

internal sealed record OperationsArtifactPage(
    IReadOnlyList<OperationsArtifactSummary> Items,
    string? NextCursor);

internal sealed record OperationsCapturePage(
    IReadOnlyList<OperationsCaptureSummary> Items,
    string? NextCursor);

internal sealed record OperationsJobSummary(
    Guid JobId,
    Guid CaptureId,
    FrameArtifactRole TargetRole,
    string TargetRecipeVersion,
    string TargetVariant,
    CentralDerivativeJobStatus Status,
    string? StateReasonCode,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ObservatoryMembershipRole EffectiveRole);

internal sealed record OperationsJobPage(
    IReadOnlyList<OperationsJobSummary> Items,
    string? NextCursor);

internal interface INetworkOperationsReadService
{
    Task<OperationsObservatoryPage> ListObservatoriesAsync(
        string userId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<OperationsObservatorySummary?> GetObservatoryAsync(
        string userId,
        Guid observatoryId,
        CancellationToken cancellationToken = default);

    Task<OperationsCameraFleetPage> ListObservatoryCamerasAsync(
        string userId,
        Guid observatoryId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<OperationsRegistrationPage> ListObservatoryRegistrationsAsync(
        string userId,
        Guid observatoryId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<OperationsObservatoryDetail?> GetObservatoryDetailAsync(
        string userId,
        Guid observatoryId,
        CancellationToken cancellationToken = default);

    Task<OperationsObservatoryDetail?> GetPublicationAuthorityAsync(
        string userId,
        Guid observatoryId,
        CancellationToken cancellationToken = default);

    Task<OperationsCameraDetail?> GetCameraAsync(
        string userId,
        Guid logicalCameraId,
        CancellationToken cancellationToken = default);

    Task<OperationsCameraInstallationPage> ListCameraInstallationsAsync(
        string userId,
        Guid logicalCameraId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<OperationsCapturePage> ListCapturesAsync(
        string userId,
        Guid? observatoryId,
        Guid? logicalCameraId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<OperationsCaptureDetail?> GetCaptureAsync(
        string userId,
        Guid captureId,
        CancellationToken cancellationToken = default);

    Task<OperationsCaptureTrace?> GetCaptureTraceAsync(
        string userId,
        Guid captureId,
        CancellationToken cancellationToken = default);

    Task<OperationsArtifactPage> ListCaptureArtifactsAsync(
        string userId,
        Guid captureId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<OperationsJobPage> ListJobsAsync(
        string userId,
        Guid? observatoryId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);
}

internal sealed class NetworkOperationsReadService(
    ApplicationDbContext dbContext,
    OperatorUiTelemetry? telemetry = null)
    : INetworkOperationsReadService
{
    public async Task<OperationsObservatoryPage> ListObservatoriesAsync(
        string userId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("operations-observatories");
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ValidateTake(take);
        if (!TryDecode(cursor, out ObservatoryCursor? cursorValue))
        {
            throw new ArgumentException("The observatory cursor is invalid.", nameof(cursor));
        }
        var query = ObservatoryMembershipAccess.ForUser(dbContext, userId)
            .Where(membership => membership.Observatory!.IsActive);
        if (cursorValue is not null)
        {
#pragma warning disable CA1309 // SQL Server performs this comparison using the indexed database collation.
            query = query.Where(membership => string.Compare(membership.Observatory!.Name, cursorValue.Name) > 0
                || membership.Observatory!.Name == cursorValue.Name
                && membership.ObservatoryId.CompareTo(cursorValue.ObservatoryId) > 0);
#pragma warning restore CA1309
        }
        var orderedQuery = query
            .OrderBy(membership => membership.Observatory!.Name)
            .ThenBy(membership => membership.ObservatoryId);
        var rows = await ProjectObservatories(orderedQuery)
            .Take(take + 1)
            .Select(membership => membership.Summary)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var nextCursor = hasMore && rows.Count > 0
            ? Encode(new ObservatoryCursor(rows[^1].Name, rows[^1].ObservatoryId))
            : null;
        telemetry?.RecordRead("operations-observatories", "member", "success", rows.Count, 0, Stopwatch.GetElapsedTime(started));
        return new(rows, nextCursor);
    }

    public Task<OperationsObservatorySummary?> GetObservatoryAsync(
        string userId,
        Guid observatoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return ProjectObservatories(ObservatoryMembershipAccess.ForUser(dbContext, userId)
                .Where(membership => membership.ObservatoryId == observatoryId && membership.Observatory!.IsActive))
            .Select(item => item.Summary)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<OperationsCameraFleetPage> ListObservatoryCamerasAsync(
        string userId,
        Guid observatoryId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ValidateTake(take);
        if (!TryDecode(cursor, out CameraCursor? cursorValue))
        {
            throw new ArgumentException("The observatory camera cursor is invalid.", nameof(cursor));
        }
        IQueryable<LogicalCamera> query = dbContext.LogicalCameras.AsNoTracking()
            .Where(camera => camera.ObservatoryId == observatoryId
                && camera.DeactivatedAtUtc == null
                && ObservatoryMembershipAccess.ForUser(dbContext, userId).Any(membership =>
                    membership.ObservatoryId == observatoryId));
        if (cursorValue is not null)
        {
#pragma warning disable CA1309 // SQL Server performs this comparison using the indexed database collation.
            query = query.Where(camera => string.Compare(camera.Name, cursorValue.Name) > 0
                || camera.Name == cursorValue.Name
                && camera.Id.CompareTo(cursorValue.LogicalCameraId) > 0);
#pragma warning restore CA1309
        }
        var rows = await ProjectCameraFleet(query.OrderBy(camera => camera.Name).ThenBy(camera => camera.Id))
            .Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore && rows.Count > 0
            ? Encode(new CameraCursor(rows[^1].Name, rows[^1].LogicalCameraId))
            : null);
    }

    public async Task<OperationsRegistrationPage> ListObservatoryRegistrationsAsync(
        string userId,
        Guid observatoryId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ValidateTake(take);
        if (!TryDecode(cursor, out RegistrationCursor? cursorValue))
        {
            throw new ArgumentException("The observatory registration cursor is invalid.", nameof(cursor));
        }
        IQueryable<DeviceRegistration> query = dbContext.DeviceRegistrations.AsNoTracking()
            .Where(item => item.ObservatoryId == observatoryId
                && item.Status == DeviceRegistrationStatus.Active
                && ObservatoryMembershipAccess.ForUser(dbContext, userId).Any(membership =>
                    membership.ObservatoryId == observatoryId));
        if (cursorValue is not null)
        {
#pragma warning disable CA1309 // SQL Server performs this comparison using the indexed database collation.
            query = query.Where(item => string.Compare(item.FriendlyName, cursorValue.FriendlyName) > 0
                || item.FriendlyName == cursorValue.FriendlyName
                && item.Id.CompareTo(cursorValue.RegistrationId) > 0);
#pragma warning restore CA1309
        }
        var rows = await ProjectRegistrations(query.OrderBy(item => item.FriendlyName).ThenBy(item => item.Id))
            .Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore && rows.Count > 0
            ? Encode(new RegistrationCursor(rows[^1].FriendlyName, rows[^1].RegistrationId))
            : null);
    }

    public async Task<OperationsObservatoryDetail?> GetObservatoryDetailAsync(
        string userId,
        Guid observatoryId,
        CancellationToken cancellationToken = default)
        => await GetObservatoryDetailAsync(userId, observatoryId, includeEnvironment: true, cancellationToken)
            .ConfigureAwait(false);

    public async Task<OperationsObservatoryDetail?> GetPublicationAuthorityAsync(
        string userId,
        Guid observatoryId,
        CancellationToken cancellationToken = default)
        => await GetObservatoryDetailAsync(userId, observatoryId, includeEnvironment: false, cancellationToken)
            .ConfigureAwait(false);

    private async Task<OperationsObservatoryDetail?> GetObservatoryDetailAsync(
        string userId,
        Guid observatoryId,
        bool includeEnvironment,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("operations-observatory-detail");
        var observatory = await GetObservatoryAsync(userId, observatoryId, cancellationToken).ConfigureAwait(false);
        if (observatory is null)
        {
            telemetry?.RecordRead("operations-observatory-detail", "member", "not-found", 0, 0, Stopwatch.GetElapsedTime(started));
            return null;
        }
        var cameraPage = await ListObservatoryCamerasAsync(userId, observatoryId, 50, null, cancellationToken)
            .ConfigureAwait(false);
        var environmentalRows = includeEnvironment
            ? await dbContext.EnvironmentalObservations.AsNoTracking()
                .Where(item => item.SiteId == observatoryId)
                .Where(item => !dbContext.EnvironmentalObservations.Any(successor =>
                    successor.SiteId == item.SiteId
                    && successor.Kind == item.Kind
                    && (successor.ObservedAtUtc > item.ObservedAtUtc
                        || successor.ObservedAtUtc == item.ObservedAtUtc
                        && successor.Id.CompareTo(item.Id) > 0)))
                .OrderByDescending(item => item.ObservedAtUtc).ThenByDescending(item => item.Id)
                .Take(100)
                .Select(item => new OperationsEnvironmentalSummary(
                    item.Kind,
                    item.Unit,
                    item.NumericValue,
                    item.BooleanValue,
                    item.Quality,
                    item.ObservedAtUtc,
                    item.StaleAfterUtc))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var environment = environmentalRows.GroupBy(item => item.Kind).Select(group => group.First())
            .OrderBy(item => item.Kind).ToArray();
        var registrationPage = await ListObservatoryRegistrationsAsync(
            userId, observatoryId, 50, null, cancellationToken).ConfigureAwait(false);
        telemetry?.RecordRead("operations-observatory-detail", "member", "success",
            1 + cameraPage.Items.Count + environment.Length + registrationPage.Items.Count,
            0,
            Stopwatch.GetElapsedTime(started));
        return new(
            observatory,
            cameraPage.Items,
            cameraPage.NextCursor,
            environment,
            registrationPage.Items,
            registrationPage.NextCursor);
    }

    public async Task<OperationsCameraDetail?> GetCameraAsync(
        string userId,
        Guid logicalCameraId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var camera = await dbContext.LogicalCameras.AsNoTracking()
            .Where(item => item.Id == logicalCameraId
                && ObservatoryMembershipAccess.ForUser(dbContext, userId).Any(membership =>
                    membership.ObservatoryId == item.ObservatoryId))
            .Select(item => new { item.Id, item.ObservatoryId, item.Name, item.Description })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (camera is null) return null;
        var page = await ListCameraInstallationsAsync(userId, logicalCameraId, 50, null, cancellationToken)
            .ConfigureAwait(false);
        return new(
            camera.Id,
            camera.ObservatoryId,
            camera.Name,
            camera.Description,
            page.Items,
            page.NextCursor);
    }

    public async Task<OperationsCameraInstallationPage> ListCameraInstallationsAsync(
        string userId,
        Guid logicalCameraId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ValidateTake(take);
        if (!TryDecode(cursor, out InstallationCursor? cursorValue))
        {
            throw new ArgumentException("The camera installation cursor is invalid.", nameof(cursor));
        }
        IQueryable<LogicalCameraInstallation> query = dbContext.LogicalCameraInstallations.AsNoTracking()
            .Where(item => item.LogicalCameraId == logicalCameraId
                && ObservatoryMembershipAccess.ForUser(dbContext, userId).Any(membership =>
                    membership.ObservatoryId == item.LogicalCamera!.ObservatoryId));
        if (cursorValue is not null)
        {
            query = query.Where(item => item.AssignedAtUtc < cursorValue.AssignedAtUtc
                || item.AssignedAtUtc == cursorValue.AssignedAtUtc
                && item.InstallationPublicId.CompareTo(cursorValue.InstallationPublicId) < 0);
        }
        var rows = await query
            .OrderByDescending(item => item.AssignedAtUtc)
            .ThenByDescending(item => item.InstallationPublicId)
            .Select(item => new OperationsCameraInstallationSummary(
                item.InstallationPublicId,
                item.Registration!.FriendlyName,
                item.Registration.Status,
                item.AssignedAtUtc,
                item.RetiredAtUtc,
                item.ReplacesInstallationId,
                item.AssignmentReasonCode,
                item.RetirementReasonCode))
            .Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore && rows.Count > 0
            ? Encode(new InstallationCursor(rows[^1].AssignedAtUtc, rows[^1].InstallationPublicId))
            : null);
    }

    public async Task<OperationsCapturePage> ListCapturesAsync(
        string userId,
        Guid? observatoryId,
        Guid? logicalCameraId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("operations-captures");
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ValidateTake(take);
        if (!TryDecode(cursor, out CaptureCursor? cursorValue))
        {
            throw new ArgumentException("The capture cursor is invalid.", nameof(cursor));
        }
        var accessibleObservatories = ObservatoryMembershipAccess.ForUser(dbContext, userId)
            .Select(membership => membership.ObservatoryId);
        IQueryable<CentralFrame> query = logicalCameraId.HasValue
            ? dbContext.CentralFrames.FromSqlInterpolated($"""
                SELECT frame.*
                FROM [CentralFrames] AS frame
                    WITH (INDEX([IX_CentralFrames_LogicalCameraInstallationId_CapturedAtUtc_Id]))
                INNER JOIN [LogicalCameraInstallations] AS installation
                    ON installation.[Id] = frame.[LogicalCameraInstallationId]
                WHERE installation.[LogicalCameraId] = {logicalCameraId.Value}
                """).AsNoTracking()
            : dbContext.CentralFrames.AsNoTracking();
        query = query
            .Where(frame => accessibleObservatories.Contains(frame.ObservatoryId));
        if (observatoryId.HasValue)
        {
            query = query.Where(frame => frame.ObservatoryId == observatoryId.Value);
        }
        if (cursorValue is not null)
        {
            query = query.Where(frame => frame.CapturedAtUtc < cursorValue.CapturedAtUtc
                || frame.CapturedAtUtc == cursorValue.CapturedAtUtc
                && frame.Id.CompareTo(cursorValue.CaptureId) < 0);
        }
        var frameIds = await query.OrderByDescending(frame => frame.CapturedAtUtc)
            .ThenByDescending(frame => frame.Id)
            .Take(take + 1)
            .Select(frame => frame.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = frameIds.Count > take;
        if (hasMore)
        {
            frameIds.RemoveAt(frameIds.Count - 1);
        }
        var projectedRows = await dbContext.CentralFrames.AsNoTracking()
            .Where(frame => frameIds.Contains(frame.Id))
            .Select(frame => new OperationsCaptureSummary(
                frame.Id,
                frame.ObservatoryId,
                dbContext.LogicalCameraInstallations.Where(installation =>
                        installation.Id == frame.LogicalCameraInstallationId)
                    .Select(installation => (Guid?)installation.LogicalCameraId).SingleOrDefault(),
                frame.LogicalCameraInstallationId,
                dbContext.LogicalCameraInstallations.Where(installation =>
                        installation.Id == frame.LogicalCameraInstallationId)
                    .Select(installation => (Guid?)installation.InstallationPublicId).SingleOrDefault(),
                dbContext.Observatories.Where(observatory => observatory.Id == frame.ObservatoryId)
                    .Select(observatory => observatory.Name).Single(),
                dbContext.LogicalCameraInstallations.Where(installation =>
                        installation.Id == frame.LogicalCameraInstallationId)
                    .Select(installation => installation.LogicalCamera!.Name).SingleOrDefault(),
                frame.CapturedAtUtc,
                frame.FirstReceivedAtUtc,
                frame.RigId,
                frame.RigProfileVersion,
                frame.DeviceRigProfileId,
                frame.CaptureSequence,
                frame.Artifacts.Count,
                frame.Artifacts.Any(artifact => artifact.Role == FrameArtifactRole.Raw),
                frame.Artifacts.Any(artifact => artifact.Role == FrameArtifactRole.Preview),
                frame.Artifacts.Any(artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview)))
            .ToDictionaryAsync(frame => frame.CaptureId, cancellationToken).ConfigureAwait(false);
        var rows = frameIds.Select(frameId => projectedRows[frameId]).ToList();
        var nextCursor = hasMore && rows.Count > 0
            ? Encode(new CaptureCursor(rows[^1].CapturedAtUtc, rows[^1].CaptureId))
            : null;
        telemetry?.RecordRead("operations-captures", "member", "success", rows.Count, 0, Stopwatch.GetElapsedTime(started));
        return new(rows, nextCursor);
    }

    public async Task<OperationsCaptureDetail?> GetCaptureAsync(
        string userId,
        Guid captureId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("operations-capture-detail");
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var accessibleObservatories = ObservatoryMembershipAccess.ForUser(dbContext, userId)
            .Select(membership => membership.ObservatoryId);
        var captureRow = await dbContext.CentralFrames.AsNoTracking()
            .Where(frame => frame.Id == captureId && accessibleObservatories.Contains(frame.ObservatoryId))
            .Select(frame => new
            {
                Capture = new OperationsCaptureSummary(
                    frame.Id,
                    frame.ObservatoryId,
                    dbContext.LogicalCameraInstallations.Where(installation =>
                            installation.Id == frame.LogicalCameraInstallationId)
                        .Select(installation => (Guid?)installation.LogicalCameraId).SingleOrDefault(),
                    frame.LogicalCameraInstallationId,
                    dbContext.LogicalCameraInstallations.Where(installation =>
                            installation.Id == frame.LogicalCameraInstallationId)
                        .Select(installation => (Guid?)installation.InstallationPublicId).SingleOrDefault(),
                    dbContext.Observatories.Where(observatory => observatory.Id == frame.ObservatoryId)
                        .Select(observatory => observatory.Name).Single(),
                    dbContext.LogicalCameraInstallations.Where(installation =>
                            installation.Id == frame.LogicalCameraInstallationId)
                        .Select(installation => installation.LogicalCamera!.Name).SingleOrDefault(),
                    frame.CapturedAtUtc,
                    frame.FirstReceivedAtUtc,
                    frame.RigId,
                    frame.RigProfileVersion,
                    frame.DeviceRigProfileId,
                    frame.CaptureSequence,
                    frame.Artifacts.Count,
                    frame.Artifacts.Any(artifact => artifact.Role == FrameArtifactRole.Raw),
                    frame.Artifacts.Any(artifact => artifact.Role == FrameArtifactRole.Preview),
                    frame.Artifacts.Any(artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview)),
                EffectiveRole = dbContext.ObservatoryMemberships.Where(membership =>
                        membership.UserId == userId && membership.ObservatoryId == frame.ObservatoryId)
                    .Select(membership => membership.Role).Single(),
                frame.AgentId,
                frame.FrameId,
                frame.LocationEvidenceState,
                LocationId = frame.Location != null ? frame.Location.LocationId : null,
                LocationVersion = frame.Location != null ? (long?)frame.Location.Version : null,
                RequestedStartUtc = frame.Timing != null ? (DateTimeOffset?)frame.Timing.RequestedStartUtc : null,
                ExposureStartedUtc = frame.Timing != null ? (DateTimeOffset?)frame.Timing.ExposureStartedUtc : null,
                ExposureEndedUtc = frame.Timing != null ? (DateTimeOffset?)frame.Timing.ExposureEndedUtc : null,
                ReadoutCompletedUtc = frame.Timing != null ? (DateTimeOffset?)frame.Timing.ReadoutCompletedUtc : null,
                DurableIngressUtc = frame.Timing != null ? (DateTimeOffset?)frame.Timing.DurableIngressUtc : null,
                EffectiveExposureTicks = frame.Control != null ? (long?)frame.Control.EffectiveExposureTicks : null,
                EffectiveGain = frame.Control != null ? (double?)frame.Control.EffectiveGain : null,
                EffectiveOffset = frame.Control != null ? frame.Control.EffectiveOffset : null,
                EffectiveTemperatureC = frame.Control != null ? frame.Control.EffectiveTemperatureC : null,
                Profiles = frame.Profiles
                    .OrderBy(profile => profile.Kind)
                    .ThenBy(profile => profile.Name)
                    .Take(51)
                    .Select(profile => new OperationsCaptureProfileSummary(
                        profile.Kind, profile.Name, profile.Version, profile.Sha256))
                    .ToArray()
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (captureRow is null)
        {
            telemetry?.RecordRead("operations-capture-detail", "member", "not-found", 0, 0, Stopwatch.GetElapsedTime(started));
            return null;
        }
        var capture = captureRow.Capture;
        var artifactPage = await ListCaptureArtifactsAsync(userId, captureId, 50, null, cancellationToken)
            .ConfigureAwait(false);
        var profiles = captureRow.Profiles.Take(50).ToArray();
        var provenance = new OperationsCaptureProvenanceSummary(
            captureRow.AgentId,
            captureRow.FrameId,
            captureRow.LocationEvidenceState,
            captureRow.LocationId,
            captureRow.LocationVersion,
            captureRow.RequestedStartUtc,
            captureRow.ExposureStartedUtc,
            captureRow.ExposureEndedUtc,
            captureRow.ReadoutCompletedUtc,
            captureRow.DurableIngressUtc,
            captureRow.EffectiveExposureTicks,
            captureRow.EffectiveGain,
            captureRow.EffectiveOffset,
            captureRow.EffectiveTemperatureC,
            profiles);
        telemetry?.RecordRead("operations-capture-detail", "member", "success",
            1 + artifactPage.Items.Count, 0, Stopwatch.GetElapsedTime(started));
        return new(capture, artifactPage.Items, artifactPage.NextCursor, captureRow.EffectiveRole, provenance);
    }

    public async Task<OperationsCaptureTrace?> GetCaptureTraceAsync(
        string userId,
        Guid captureId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("operations-capture-trace");
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var observatoryId = await dbContext.CentralFrames.AsNoTracking()
            .Where(frame => frame.Id == captureId
                && ObservatoryMembershipAccess.ForUser(dbContext, userId).Any(membership =>
                    membership.ObservatoryId == frame.ObservatoryId))
            .Select(frame => (Guid?)frame.ObservatoryId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (observatoryId is null)
        {
            telemetry?.RecordRead("operations-capture-trace", "member", "not-found", 0, 0,
                Stopwatch.GetElapsedTime(started));
            return null;
        }

        var artifactLineageRows = await dbContext.CentralArtifactSources.AsNoTracking()
            .Where(source => source.Artifact!.CentralFrameId == captureId)
            .OrderBy(source => source.Artifact!.ReceivedAtUtc)
            .ThenBy(source => source.Artifact!.ArtifactId)
            .ThenBy(source => source.Ordinal)
            .Take(201)
            .Select(source => new OperationsArtifactLineageSummary(
                source.Artifact!.ArtifactId,
                source.Ordinal,
                source.ResolvedArtifact != null
                    && source.ResolvedArtifact.Frame!.ObservatoryId == observatoryId.Value
                        ? (Guid?)source.SourceArtifactId
                        : null,
                source.ResolvedArtifact != null
                    && source.ResolvedArtifact.Frame!.ObservatoryId == observatoryId.Value
                        ? (Guid?)source.ResolvedArtifact.ArtifactId
                        : null,
                source.ResolvedArtifact != null
                    && source.ResolvedArtifact.Frame!.ObservatoryId == observatoryId.Value
                        ? (FrameArtifactRole?)source.ResolvedArtifact.Role
                        : null,
                source.ResolvedArtifact != null
                    && source.ResolvedArtifact.Frame!.ObservatoryId == observatoryId.Value
                        ? source.ResolvedArtifact.Variant
                        : null,
                source.ResolvedArtifact != null
                    && source.ResolvedArtifact.Frame!.ObservatoryId == observatoryId.Value
                        ? source.ResolvedArtifact.ChecksumSha256
                        : null))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var artifactLineage = artifactLineageRows.Take(200).ToArray();

        var allJobRows = await dbContext.CentralDerivativeJobs.AsNoTracking().AsSplitQuery()
            .Where(job => job.SourceArtifact!.Frame!.ObservatoryId == observatoryId.Value
                && (job.SourceArtifact.CentralFrameId == captureId
                    || job.ResultArtifact != null && job.ResultArtifact.CentralFrameId == captureId
                    || job.Inputs.Any(input => input.Artifact!.CentralFrameId == captureId)))
            .OrderBy(job => job.CreatedAtUtc).ThenBy(job => job.Id)
            .Take(51)
            .Select(job => new OperationsCaptureJobTrace(
                job.Id,
                job.TargetRole,
                job.TargetVariant,
                job.RecipeName,
                job.TargetRecipeVersion,
                job.Status,
                job.MissingInputOutcome,
                job.StateReasonCode,
                job.CreatedAtUtc,
                job.CompletedAtUtc,
                job.InputRequirements
                    .OrderBy(input => input.Ordinal).ThenBy(input => input.Id)
                    .Take(51)
                    .Select(input => new OperationsDerivativeInputSummary(
                        input.Ordinal,
                        input.BindingName,
                        input.SourceKind,
                        input.IsRequired,
                        input.ResolutionState,
                        input.ResolutionReasonCode,
                        input.Input != null && input.Input.Artifact!.Frame!.ObservatoryId == observatoryId.Value
                            ? (Guid?)input.Input.Artifact.ArtifactId
                            : null,
                        input.CanonicalInput != null
                            && input.CanonicalInput.EnvironmentalObservation != null
                            && input.CanonicalInput.EnvironmentalObservation.SiteId == observatoryId.Value
                                ? input.CanonicalInput.EnvironmentalObservationRecordId
                                : null,
                        input.CanonicalInput != null
                            && (input.CanonicalInput.EnvironmentalObservationRecordId == null
                                || input.CanonicalInput.EnvironmentalObservation!.SiteId == observatoryId.Value)
                                ? input.CanonicalInput.IdentitySha256
                                : null))
                    .ToArray(),
                job.Attempts
                    .OrderBy(attempt => attempt.AttemptNumber).ThenBy(attempt => attempt.Id)
                    .Take(51)
                    .Select(attempt => new OperationsDerivativeAttemptSummary(
                        attempt.AttemptNumber,
                        attempt.WorkerId,
                        attempt.Outcome,
                        attempt.ReasonCode,
                        attempt.LeaseAcquiredAtUtc,
                        attempt.EndedAtUtc,
                        attempt.InputBytes,
                        attempt.OutputBytes))
                    .ToArray()))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var jobs = allJobRows.Take(50)
            .Select(job => job with
            {
                Inputs = job.Inputs.Take(50).ToArray(),
                Attempts = job.Attempts.Take(50).ToArray()
            })
            .ToArray();
        var jobIds = jobs.Select(job => job.JobId).ToArray();

        var allEnvironmentalEvidence = await dbContext.EnvironmentalObservations.AsNoTracking()
            .Where(observation => observation.SiteId == observatoryId.Value
                && dbContext.CentralDerivativeJobCanonicalInputs.Any(input =>
                    jobIds.Contains(input.CentralDerivativeJobId)
                    && input.EnvironmentalObservationRecordId == observation.Id))
            .OrderBy(observation => observation.ObservedAtUtc).ThenBy(observation => observation.Id)
            .Take(51)
            .Select(observation => new OperationsEnvironmentalEvidenceSummary(
                observation.ObservationId,
                observation.Kind,
                observation.Unit,
                observation.NumericValue,
                observation.BooleanValue,
                observation.Quality,
                observation.ObservedAtUtc,
                observation.Source!.Provider,
                observation.Source.MethodName,
                observation.Source.MethodVersion,
                observation.SourceIdentitySha256,
                observation.Lineage.Count))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var environmentalEvidence = allEnvironmentalEvidence.Take(50).ToArray();

        var accessibleObservatories = ObservatoryMembershipAccess.ForUser(dbContext, userId)
            .Select(membership => membership.ObservatoryId);
        var allEvents = await dbContext.CentralTransientEvents.AsNoTracking()
            .Where(item => item.Observations.Any(observation =>
                observation.Source!.Artifact!.CentralFrameId == captureId
                || observation.Backgrounds.Any(background => background.Artifact!.CentralFrameId == captureId))
                && dbContext.CentralTransientObservationSources.Any(source =>
                    source.Observation!.CentralTransientEventId == item.Id)
                && !dbContext.CentralTransientObservationSources.Any(source =>
                    source.Observation!.CentralTransientEventId == item.Id
                    && !accessibleObservatories.Contains(source.Artifact!.Frame!.ObservatoryId))
                && !dbContext.CentralTransientObservationBackgrounds.Any(background =>
                    background.Observation!.CentralTransientEventId == item.Id
                    && !accessibleObservatories.Contains(background.Artifact!.Frame!.ObservatoryId)))
            .OrderBy(item => item.EventCreatedUtc).ThenBy(item => item.Id)
            .Take(51)
            .Select(item => new OperationsTransientEventTrace(
                item.EventId,
                item.Current != null ? (int?)item.Current.LatestVersion : null,
                item.Current != null ? (TransientEventState?)item.Current.LatestEventVersion!.State : null,
                item.Current != null ? (TransientClassification?)item.Current.EffectiveClassification : null,
                item.Current != null ? (int?)item.Current.EffectiveConfidenceMillionths : null,
                item.Current != null ? (CentralTransientReviewState?)item.Current.ReviewState : null,
                item.Current != null && item.Current.LatestReview != null
                    ? (TransientReviewDisposition?)item.Current.LatestReview.Disposition
                    : null,
                item.Current != null ? (DateTimeOffset?)item.Current.UpdatedUtc : null,
                item.Observations.Count,
                item.Assessments.Count,
                item.Reviews.Count))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var events = allEvents.Take(50).ToArray();
        var traceIsTruncated = artifactLineageRows.Length > 200
            || allJobRows.Length > 50
            || allJobRows.Any(job => job.Inputs.Count > 50 || job.Attempts.Count > 50)
            || allEnvironmentalEvidence.Length > 50
            || allEvents.Length > 50;
        telemetry?.RecordRead("operations-capture-trace", "member", "success",
            artifactLineage.Length + jobs.Length
                + environmentalEvidence.Length + events.Length,
            0,
            Stopwatch.GetElapsedTime(started));
        return new(
            artifactLineage,
            jobs,
            environmentalEvidence,
            events,
            traceIsTruncated);
    }

    public async Task<OperationsArtifactPage> ListCaptureArtifactsAsync(
        string userId,
        Guid captureId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ValidateTake(take);
        if (!TryDecode(cursor, out ArtifactCursor? cursorValue))
        {
            throw new ArgumentException("The capture artifact cursor is invalid.", nameof(cursor));
        }
        IQueryable<CentralArtifact> query = dbContext.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.CentralFrameId == captureId
                && ObservatoryMembershipAccess.ForUser(dbContext, userId).Any(membership =>
                    membership.ObservatoryId == artifact.Frame!.ObservatoryId));
        if (cursorValue is not null)
        {
            query = query.Where(artifact => artifact.ReceivedAtUtc < cursorValue.ReceivedAtUtc
                || artifact.ReceivedAtUtc == cursorValue.ReceivedAtUtc
                && artifact.ArtifactId.CompareTo(cursorValue.ArtifactId) < 0);
        }
        var rows = await ProjectArtifacts(query
                .OrderByDescending(artifact => artifact.ReceivedAtUtc)
                .ThenByDescending(artifact => artifact.ArtifactId))
            .Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore && rows.Count > 0
            ? Encode(new ArtifactCursor(rows[^1].ReceivedAtUtc, rows[^1].ArtifactId))
            : null);
    }

    public async Task<OperationsJobPage> ListJobsAsync(
        string userId,
        Guid? observatoryId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("operations-jobs");
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ValidateTake(take);
        if (!TryDecode(cursor, out JobCursor? cursorValue))
        {
            throw new ArgumentException("The job cursor is invalid.", nameof(cursor));
        }
        var accessibleObservatories = ObservatoryMembershipAccess.ForUser(dbContext, userId)
            .Select(membership => membership.ObservatoryId);
        var query = dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => accessibleObservatories.Contains(job.SourceArtifact!.Frame!.ObservatoryId));
        if (observatoryId.HasValue)
        {
            query = query.Where(job => job.SourceArtifact!.Frame!.ObservatoryId == observatoryId.Value);
        }
        if (cursorValue is not null)
        {
            query = query.Where(job => job.CreatedAtUtc < cursorValue.CreatedAtUtc
                || job.CreatedAtUtc == cursorValue.CreatedAtUtc && job.Id.CompareTo(cursorValue.JobId) < 0);
        }
        var rows = await query.OrderByDescending(job => job.CreatedAtUtc).ThenByDescending(job => job.Id)
            .Take(take + 1)
            .Select(job => new OperationsJobSummary(
                job.Id,
                job.SourceArtifact!.CentralFrameId,
                job.TargetRole,
                job.TargetRecipeVersion,
                job.TargetVariant,
                job.Status,
                job.StateReasonCode,
                job.AttemptCount,
                job.MaxAttempts,
                job.CreatedAtUtc,
                job.UpdatedAtUtc,
                job.CompletedAtUtc,
                dbContext.ObservatoryMemberships.Where(membership =>
                        membership.UserId == userId
                        && membership.ObservatoryId == job.SourceArtifact!.Frame!.ObservatoryId)
                    .Select(membership => membership.Role).Single()))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore && rows.Count > 0
            ? Encode(new JobCursor(rows[^1].CreatedAtUtc, rows[^1].JobId))
            : null;
        telemetry?.RecordRead("operations-jobs", "member", "success", rows.Count, 0, Stopwatch.GetElapsedTime(started));
        return new(rows, nextCursor);
    }

    private static void ValidateTake(int take)
    {
        if (take is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }
    }

    private static string Encode<T>(T value)
        => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value));

    private static bool TryDecode<T>(string? cursor, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }
        try
        {
            value = JsonSerializer.Deserialize<T>(WebEncoders.Base64UrlDecode(cursor));
            return value is not null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record CaptureCursor(DateTimeOffset CapturedAtUtc, Guid CaptureId);
    private sealed record CameraCursor(string Name, Guid LogicalCameraId);
    private sealed record RegistrationCursor(string FriendlyName, Guid RegistrationId);
    private sealed record InstallationCursor(DateTimeOffset AssignedAtUtc, Guid InstallationPublicId);
    private sealed record ArtifactCursor(DateTimeOffset ReceivedAtUtc, Guid ArtifactId);
    private sealed record JobCursor(DateTimeOffset CreatedAtUtc, Guid JobId);
    private sealed record ObservatoryCursor(string Name, Guid ObservatoryId);

    private IQueryable<OperationsCameraFleetSummary> ProjectCameraFleet(IQueryable<LogicalCamera> query)
        => query.Select(camera => new OperationsCameraFleetSummary(
            camera.Id,
            camera.Name,
            camera.Description,
            camera.Installations.Where(installation => installation.RetiredAtUtc == null)
                .Select(installation => (Guid?)installation.InstallationPublicId).SingleOrDefault(),
            camera.Installations.Where(installation => installation.RetiredAtUtc == null)
                .Select(installation => installation.Registration!.FriendlyName).SingleOrDefault(),
            dbContext.DeviceFleetStates.Where(state => camera.Installations.Any(installation =>
                    installation.RetiredAtUtc == null && installation.RegistrationId == state.RegistrationId))
                .Select(state => (FleetHealth?)state.ReportedHealth).SingleOrDefault(),
            dbContext.DeviceFleetStates.Where(state => camera.Installations.Any(installation =>
                    installation.RetiredAtUtc == null && installation.RegistrationId == state.RegistrationId))
                .Select(state => (DateTimeOffset?)state.ObservedAtUtc).SingleOrDefault(),
            dbContext.DeviceFleetStates.Any(state => state.HasStoragePressure && camera.Installations.Any(
                installation => installation.RetiredAtUtc == null && installation.RegistrationId == state.RegistrationId)),
            dbContext.DeviceFleetStates.Any(state => state.HasRequiredLaneFailure && camera.Installations.Any(
                installation => installation.RetiredAtUtc == null && installation.RegistrationId == state.RegistrationId)),
            dbContext.DeviceFleetStates.Any(state => state.HasQuarantine && camera.Installations.Any(
                installation => installation.RetiredAtUtc == null && installation.RegistrationId == state.RegistrationId)),
            dbContext.DeviceFleetStates.Where(state => camera.Installations.Any(installation =>
                    installation.RetiredAtUtc == null && installation.RegistrationId == state.RegistrationId))
                .Select(state => state.SoftwareVersion).SingleOrDefault(),
            dbContext.PublicRecordPublicationDecisions.Any(decision =>
                decision.LogicalCameraId == camera.Id
                && decision.State == PublicationDecisionState.Released
                && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                    successor.SupersedesDecisionId == decision.Id))));

    private IQueryable<OperationsRegistrationSummary> ProjectRegistrations(IQueryable<DeviceRegistration> query)
        => query.Select(item => new OperationsRegistrationSummary(
            item.Id,
            item.FriendlyName,
            item.Status,
            dbContext.LogicalCameraInstallations.Where(installation =>
                    installation.RegistrationId == item.Id && installation.RetiredAtUtc == null)
                .Select(installation => (Guid?)installation.LogicalCameraId).SingleOrDefault()));

    private IQueryable<OperationsArtifactSummary> ProjectArtifacts(IQueryable<CentralArtifact> query)
        => query.Select(artifact => new OperationsArtifactSummary(
            artifact.ArtifactId,
            artifact.Id,
            artifact.Role,
            artifact.Variant,
            artifact.RecipeVersion,
            artifact.MediaType,
            artifact.ByteLength,
            artifact.ChecksumSha256,
            artifact.ObjectState,
            artifact.ReconstructionState,
            artifact.StateReasonCode,
            artifact.ReceivedAtUtc,
            $"/api/v1.0/devices/{artifact.Frame!.DevicePublicId:D}/artifacts/{artifact.ArtifactId:D}/content",
            dbContext.CentralDerivativeJobs.Any(job => job.ResultCentralArtifactId == artifact.Id)
                ? "Central derivative"
                : artifact.Role == FrameArtifactRole.AnnotatedPreview
                    ? "Edge annotation"
                    : artifact.Role == FrameArtifactRole.Preview
                        ? "Edge preview"
                        : "Edge evidence",
            artifact.Sources.Count,
            artifact.Recipe != null ? artifact.Recipe.Name : null,
            artifact.Recipe != null ? artifact.Recipe.SemanticVersion : null,
            artifact.Recipe != null ? artifact.Recipe.OptionsSha256 : null,
            artifact.ManifestSchemaVersion,
            artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.ReconstructionState == CentralReconstructionState.Complete
                && (artifact.Role == FrameArtifactRole.Preview || artifact.Role == FrameArtifactRole.AnnotatedPreview)
                && (artifact.MediaType == "image/jpeg" || artifact.MediaType == "image/png"
                    || artifact.MediaType == "image/webp"),
            dbContext.PublicRecordPublicationDecisions.Any(decision =>
                decision.AuthorityObservatoryId == artifact.Frame!.ObservatoryId
                && decision.SubjectKind == PublicRecordSubjectKind.Artifact
                && decision.CentralArtifactId == artifact.Id
                && decision.State == PublicationDecisionState.Released
                && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                    successor.SupersedesDecisionId == decision.Id))));

    private IQueryable<ObservatoryProjection> ProjectObservatories(IQueryable<ObservatoryMembership> query)
        => query.Select(membership => new ObservatoryProjection(
            membership.Observatory!,
            membership.ObservatoryId,
            new OperationsObservatorySummary(
                membership.ObservatoryId,
                membership.Observatory!.Name,
                membership.Role,
                dbContext.LogicalCameras.Count(camera =>
                    camera.ObservatoryId == membership.ObservatoryId && camera.DeactivatedAtUtc == null),
                dbContext.LogicalCameraInstallations.Count(installation =>
                    installation.LogicalCamera!.ObservatoryId == membership.ObservatoryId
                    && installation.RetiredAtUtc == null),
                dbContext.DeviceDeploymentLocationVersions.Count(location =>
                    location.ObservatoryId == membership.ObservatoryId
                    && location.Status == DeploymentLocationResolutionStatus.Pending),
                dbContext.CentralDerivativeJobs.Count(job =>
                    job.SourceArtifact!.Frame!.ObservatoryId == membership.ObservatoryId
                    && (job.Status == CentralDerivativeJobStatus.Waiting
                        || job.Status == CentralDerivativeJobStatus.Pending
                        || job.Status == CentralDerivativeJobStatus.Leased
                        || job.Status == CentralDerivativeJobStatus.RetryableFailure
                        || job.Status == CentralDerivativeJobStatus.CancelRequested)))));

    private sealed record ObservatoryProjection(
        Observatory Observatory,
        Guid ObservatoryId,
        OperationsObservatorySummary Summary);
}
