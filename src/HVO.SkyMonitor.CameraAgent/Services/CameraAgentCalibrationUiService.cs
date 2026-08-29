using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentCalibrationUiService
{
    ValueTask<OperatorUiResult<CalibrationUiStatus>> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CalibrationUiBundlePage>> GetBundlesAsync(
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CalibrationUiBundleDetail>> GetBundleAsync(
        string bundleId,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CalibrationUiAcquisition>> AcquireAsync(
        CalibrationUiAcquisitionRequest request,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CalibrationUiAcquisition>> CancelAsync(
        string jobId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CalibrationUiStatus>> ActivateAsync(
        string bundleId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CalibrationUiStatus>> RollbackAsync(
        string bundleId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);
}

internal sealed record CalibrationUiStatus(
    long Version,
    CalibrationUiBundleSummary? ActiveBundle,
    CalibrationUiAcquisition? PendingAcquisition,
    long PublishedBundleCount,
    long QuarantineCount,
    string? LastSelectionReason,
    DateTimeOffset? LastSelectionUtc,
    string? LastReconciliationReason,
    DateTimeOffset? LastReconciliationUtc,
    CalibrationLibraryActivationSnapshot? LastActivation);

internal sealed record CalibrationUiBundlePage(
    IReadOnlyList<CalibrationUiBundleSummary> Items,
    string? NextCursor);

internal sealed record CalibrationUiBundleSummary(
    string BundleId,
    string BundleIdentitySha256,
    string Source,
    string PublicationState,
    string? FailureReason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string ProfileIdentitySha256,
    string AcquisitionModelIdentitySha256,
    CalibrationApplicabilityV1 Applicability,
    int SourceCount,
    int MasterCount);

internal sealed record CalibrationUiBundleDetail(
    CalibrationUiBundleSummary Summary,
    IReadOnlyList<CalibrationUiArtifact> Artifacts);

internal sealed record CalibrationUiArtifact(
    string Kind,
    string Role,
    Guid ArtifactId,
    string PayloadSha256,
    TimeSpan Exposure,
    double Gain,
    double? Offset,
    double? TemperatureC,
    int? SourceIndex,
    IReadOnlyList<Guid> OrderedSourceArtifactIds,
    HVO.SkyMonitor.AgentCore.RecipeIdentityDescriptor? MasterBuildRecipe);

internal sealed record CalibrationUiAcquisition(
    string JobId,
    string State,
    string Phase,
    int AttemptCount,
    string? BundleId,
    string? FailureReason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc,
    VirtualCalibrationAcquisitionPlanV1 Plan);

internal sealed record CalibrationUiAcquisitionRequest(
    double Gain,
    double Offset,
    double TemperatureC,
    TimeSpan BiasExposure,
    TimeSpan DarkExposure,
    TimeSpan FlatExposure,
    TimeSpan DefectExposure,
    TimeSpan ApplicableLightExposure,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    VirtualCalibrationSourceModelV1 SourceModel,
    string? Reason);

internal sealed class CameraAgentCalibrationUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    SqliteCalibrationLibraryStore store,
    VirtualCalibrationAcquisitionCoordinator acquisitionCoordinator,
    CalibrationLibraryOperationsCoordinator operationsCoordinator,
    CalibrationOperationsTokenService tokens,
    ILogger<CameraAgentCalibrationUiService> logger) : ICameraAgentCalibrationUiService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<CalibrationUiStatus>> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CalibrationUiStatus>();
        }
        try
        {
            return OperatorUiResult<CalibrationUiStatus>.Success(Project(
                await store.GetOperationsStatusAsync(cancellationToken).ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent calibration UI status read failed.");
            return Unavailable<CalibrationUiStatus>("Calibration status is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<CalibrationUiBundlePage>> GetBundlesAsync(
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CalibrationUiBundlePage>();
        }
        try
        {
            CalibrationLibraryBundleCursor? decoded = null;
            if (cursor is not null && !tokens.TryUnprotect(cursor, out decoded))
            {
                return Invalid<CalibrationUiBundlePage>("The calibration library page expired. Return to newest bundles.");
            }
            var page = await store.GetBundlePageAsync(pageSize, decoded, cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<CalibrationUiBundlePage>.Success(new CalibrationUiBundlePage(
                page.Items.Select(ProjectSummary).ToArray(),
                page.NextCursor is null ? null : tokens.Protect(page.NextCursor)));
        }
        catch (ArgumentException)
        {
            return Invalid<CalibrationUiBundlePage>("The calibration library page request is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent calibration UI library read failed.");
            return Unavailable<CalibrationUiBundlePage>("The calibration library is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<CalibrationUiBundleDetail>> GetBundleAsync(
        string bundleId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CalibrationUiBundleDetail>();
        }
        try
        {
            var bundle = await store.GetBundleAsync(bundleId, cancellationToken).ConfigureAwait(false);
            return bundle is null
                ? OperatorUiResult<CalibrationUiBundleDetail>.Failure(
                    OperatorUiResultKind.NotFound, "The calibration bundle was not found.")
                : OperatorUiResult<CalibrationUiBundleDetail>.Success(ProjectDetail(bundle));
        }
        catch (ArgumentException)
        {
            return Invalid<CalibrationUiBundleDetail>("The calibration bundle identifier is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent calibration UI bundle read failed.");
            return Unavailable<CalibrationUiBundleDetail>("Calibration bundle detail is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<CalibrationUiAcquisition>> AcquireAsync(
        CalibrationUiAcquisitionRequest request,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Denied<CalibrationUiAcquisition>();
        }
        var command = new VirtualCalibrationAcquisitionRequestV1(
            VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
            idempotencyKey,
            request.Gain,
            request.Offset,
            request.TemperatureC,
            request.BiasExposure,
            request.DarkExposure,
            request.FlatExposure,
            request.DefectExposure,
            request.ApplicableLightExposure,
            request.EffectiveFromUtc,
            request.EffectiveUntilUtc,
            request.SourceModel,
            actor,
            request.Reason,
            expectedVersion);
        try
        {
            var result = await acquisitionCoordinator.AcquireAsync(command, cancellationToken).ConfigureAwait(false);
            return ProjectAcquisitionResult(result, cancellationCommand: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var replay = await acquisitionCoordinator.AcquireAsync(command, CancellationToken.None)
                    .ConfigureAwait(false);
                return ProjectAcquisitionResult(replay, cancellationCommand: false);
            }
            catch (Exception exception)
            {
                return HandleMutationFailure<CalibrationUiAcquisition>(exception, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            return HandleMutationFailure<CalibrationUiAcquisition>(exception, cancellationToken);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<CalibrationUiAcquisition>> CancelAsync(
        string jobId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Denied<CalibrationUiAcquisition>();
        }
        try
        {
            var result = await acquisitionCoordinator.CancelAsync(
                jobId, idempotencyKey, expectedVersion, actor, reason, cancellationToken)
                .ConfigureAwait(false);
            return ProjectAcquisitionResult(result, cancellationCommand: true);
        }
        catch (Exception exception)
        {
            return HandleMutationFailure<CalibrationUiAcquisition>(exception, cancellationToken);
        }
    }

    public ValueTask<OperatorUiResult<CalibrationUiStatus>> ActivateAsync(
        string bundleId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ChangeActivationAsync(
            bundleId, expectedVersion, idempotencyKey, reason, rollback: false, cancellationToken);

    public ValueTask<OperatorUiResult<CalibrationUiStatus>> RollbackAsync(
        string bundleId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ChangeActivationAsync(
            bundleId, expectedVersion, idempotencyKey, reason, rollback: true, cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<CalibrationUiStatus>> ChangeActivationAsync(
        string bundleId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        bool rollback,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Denied<CalibrationUiStatus>();
        }
        try
        {
            _ = rollback
                ? await operationsCoordinator.RollbackAsync(
                    bundleId, idempotencyKey, expectedVersion, actor, reason, cancellationToken).ConfigureAwait(false)
                : await operationsCoordinator.ActivateAsync(
                    bundleId, idempotencyKey, expectedVersion, actor, reason, cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<CalibrationUiStatus>.Success(Project(
                await store.GetOperationsStatusAsync(cancellationToken).ConfigureAwait(false)));
        }
        catch (Exception exception)
        {
            return HandleMutationFailure<CalibrationUiStatus>(exception, cancellationToken);
        }
    }

    private OperatorUiResult<T> HandleMutationFailure<T>(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (exception is CalibrationLibraryStoreConflictException)
        {
            return OperatorUiResult<T>.Failure(
                OperatorUiResultKind.Conflict, "Calibration state changed. Refresh before retrying.");
        }
        if (exception is KeyNotFoundException)
        {
            return OperatorUiResult<T>.Failure(
                OperatorUiResultKind.NotFound, "The calibration bundle or acquisition was not found.");
        }
        if (exception is CalibrationLibraryAcquisitionException)
        {
            return Unavailable<T>("Calibration acquisition could not be completed.");
        }
        if (exception is ArgumentException or InvalidOperationException)
        {
            return Invalid<T>("The calibration command is invalid.");
        }
        logger.LogWarning(exception, "CameraAgent calibration UI mutation failed.");
        return Unavailable<T>("The calibration command could not be completed.");
    }

    private async Task<bool> IsAuthorizedAsync(string policy)
        => await GetAuthorizedPrincipalAsync(policy).ConfigureAwait(false) is not null;

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy).ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }

    private static CalibrationUiStatus Project(CalibrationLibraryOperationsStatus status)
        => new(
            status.State.Version,
            status.State.ActiveBundle is null ? null : ProjectSummary(status.State.ActiveBundle),
            status.PendingAcquisition is null ? null : Project(status.PendingAcquisition),
            status.PublishedBundleCount,
            status.QuarantineCount,
            status.State.LastSelectionReason,
            status.State.LastSelectionUtc,
            status.State.LastReconciliationReason,
            status.State.LastReconciliationUtc,
            status.LastActivation);

    private static CalibrationUiBundleSummary ProjectSummary(CalibrationLibraryBundleSnapshot snapshot)
        => new(
            snapshot.Bundle.BundleId,
            snapshot.BundleIdentitySha256,
            snapshot.Bundle.Source,
            snapshot.PublicationState,
            snapshot.FailureReason,
            snapshot.CreatedUtc,
            snapshot.UpdatedUtc,
            snapshot.Bundle.ProfileIdentitySha256,
            snapshot.Bundle.AcquisitionModelIdentitySha256,
            snapshot.Bundle.Applicability,
            snapshot.Bundle.Artifacts.Count(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Source),
            snapshot.Bundle.Artifacts.Count(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master));

    private static CalibrationUiBundleDetail ProjectDetail(CalibrationLibraryBundleSnapshot snapshot)
        => new(
            ProjectSummary(snapshot),
            snapshot.Bundle.Artifacts.Select(static artifact => new CalibrationUiArtifact(
                artifact.Kind,
                artifact.Role,
                artifact.ArtifactId,
                artifact.PayloadSha256,
                artifact.Exposure,
                artifact.Gain,
                artifact.Offset,
                artifact.TemperatureC,
                artifact.SourceIndex,
                artifact.OrderedSourceArtifactIds,
                artifact.MasterBuildRecipe)).ToArray());

    private static CalibrationUiAcquisition Project(CalibrationAcquisitionJobSnapshot job)
        => new(
            job.Plan.JobId,
            job.State,
            job.Phase,
            job.AttemptCount,
            job.BundleId,
            job.FailureReason,
            job.CreatedUtc,
            job.UpdatedUtc,
            job.CompletedUtc,
            job.Plan);

    internal static OperatorUiResult<CalibrationUiAcquisition> ProjectAcquisitionResult(
        CalibrationAcquisitionJobSnapshot job,
        bool cancellationCommand)
    {
        if (job.State == CalibrationAcquisitionStates.Failed)
        {
            return OperatorUiResult<CalibrationUiAcquisition>.Failure(
                OperatorUiResultKind.Invalid,
                $"Calibration acquisition failed ({CalibrationTelemetry.NormalizeReason(job.FailureReason)}).");
        }
        if (cancellationCommand && job.State != CalibrationAcquisitionStates.Cancelled)
        {
            return OperatorUiResult<CalibrationUiAcquisition>.Failure(
                OperatorUiResultKind.Conflict,
                "Calibration acquisition completed before cancellation committed.");
        }
        if (!cancellationCommand && job.State == CalibrationAcquisitionStates.Cancelled)
        {
            return OperatorUiResult<CalibrationUiAcquisition>.Failure(
                OperatorUiResultKind.Conflict,
                "Calibration acquisition was cancelled before publication.");
        }
        return OperatorUiResult<CalibrationUiAcquisition>.Success(Project(job));
    }

    private static OperatorUiResult<T> Denied<T>()
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");

    private static OperatorUiResult<T> Invalid<T>(string message)
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, message);

    private static OperatorUiResult<T> Unavailable<T>(string message)
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, message);
}
