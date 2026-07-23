using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal enum OperatorUiResultKind
{
    Success,
    Unauthorized,
    NotFound,
    Conflict,
    Invalid,
    Unavailable
}

internal sealed record OperatorUiResult<T>(
    OperatorUiResultKind Kind,
    T? Value = default,
    string? Message = null)
{
    internal bool IsSuccess => Kind == OperatorUiResultKind.Success;

    internal static OperatorUiResult<T> Success(T value) => new(OperatorUiResultKind.Success, value);

    internal static OperatorUiResult<T> Failure(OperatorUiResultKind kind, string message) => new(kind, default, message);
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instances are created and consumed through Razor component parameters.")]
public sealed record OperatorOutboxItem(
    string Kind,
    string Status,
    string? StorageAlias,
    string? Role,
    int AttemptCount,
    long? PayloadBytes,
    DateTimeOffset UpdatedUtc,
    string ReasonCode,
    string? ReplayToken,
    string? AbandonToken,
    DateTimeOffset? CreatedUtc = null,
    DateTimeOffset? NextAttemptUtc = null,
    string? MediaType = null,
    string? AuditSummary = null);

internal sealed record OperatorOutboxPage(
    string Kind,
    IReadOnlyList<string> StorageAliases,
    string? StorageAlias,
    IReadOnlyList<OperatorOutboxItem> Items,
    string? NextCursor);

internal sealed record CameraAgentOperationsView(
    CameraAgentOperationsSummary Summary,
    IReadOnlyList<OperatorOutboxItem> ArtifactQuarantine,
    IReadOnlyList<OperatorOutboxItem> EnvironmentalQuarantine);

internal sealed record OperatorCommandReceipt(
    string Action,
    string Disposition,
    string State,
    long? Version,
    DateTimeOffset CompletedUtc);

internal sealed record CameraAgentSystemStatus(
    string SnapshotLabel,
    string SnapshotIdentity,
    string ValidationStatus,
    string AgentId,
    string ModuleType,
    string CentralIntegration,
    CameraAgentSensorStatus Sensor,
    CameraAgentOpticsStatus Optics,
    CameraAgentCapturePolicyStatus Capture,
    IReadOnlyList<CameraAgentPipelineNodeStatus> Pipeline,
    CameraAgentRetentionStatus Retention,
    CameraAgentUploadStatus Upload,
    CameraAgentEnvironmentalPolicyStatus Environmental,
    CameraAgentTransientPolicyStatus Transient);

internal sealed record CameraAgentSensorStatus(
    string Name,
    int WidthPixels,
    int HeightPixels,
    double PixelSizeMicrons,
    string ColorMode,
    string PixelFormat,
    string ResponseMode,
    string ProfileVersion,
    string SensorRecipeVersion);

internal sealed record CameraAgentOpticsStatus(
    string LensKind,
    string ProjectionModel,
    double FocalLengthMillimeters,
    double HorizontalFieldOfViewDegrees,
    double? VerticalFieldOfViewDegrees,
    string CalibrationVersion,
    bool HorizontalFlip,
    string Layout);

internal sealed record CameraAgentCapturePolicyStatus(
    string CadenceMode,
    double CaptureIntervalSeconds,
    double DayExposureMilliseconds,
    double NightExposureMilliseconds,
    double DayGain,
    double NightGain,
    double? MinimumExposureMilliseconds,
    double? MaximumExposureMilliseconds,
    double? MinimumGain,
    double? MaximumGain,
    string ExposurePreference);

internal sealed record CameraAgentPipelineNodeStatus(
    string Id,
    string Type,
    bool Required,
    IReadOnlyList<string> Dependencies);

internal sealed record CameraAgentRetentionStatus(
    int SweepIntervalMinutes,
    double PressureThresholdPercent,
    double PressureRecoveryPercent,
    int PressureRetentionDays,
    long RawIngressReserveBytes);

internal sealed record CameraAgentUploadStatus(
    bool Enabled,
    int BatchSize,
    int PollIntervalSeconds,
    int RetryInitialDelaySeconds,
    int RetryMaximumDelaySeconds,
    int BandwidthLimitBytesPerSecond,
    int RequiredMaximumPendingCount,
    long RequiredMaximumPendingBytes,
    int OptionalMaximumPendingCount,
    long OptionalMaximumPendingBytes);

internal sealed record CameraAgentEnvironmentalPolicyStatus(
    bool Enabled,
    int BatchSize,
    int PollIntervalSeconds,
    int MaximumAttempts,
    int MaximumPendingCount,
    long MaximumPendingBytes);

internal sealed record CameraAgentTransientPolicyStatus(
    string Mode,
    bool Required,
    int CandidateTimeoutMinutes,
    int WorkerPollIntervalMilliseconds,
    int MaximumAttempts,
    int MaximumAdjacentStartIntervalSeconds,
    int StarMaximumResults);

internal interface ICameraAgentOperatorUiService
{
    ValueTask<OperatorUiResult<CameraAgentOperationsView>> GetOperationsAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentGalleryPage>> GetGalleryPageAsync(
        CameraAgentGalleryQuery query,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<OperatorOutboxPage>> GetQuarantinePageAsync(
        string kind,
        string? storageAlias,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentGalleryCapture>> GetGalleryCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentSystemStatus>> GetSystemStatusAsync(CancellationToken cancellationToken);

    Task<OperatorUiResult<OperatorCommandReceipt>> SetCapturePausedAsync(
        bool paused,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<OperatorCommandReceipt>> ResolveOutboxAsync(
        string kind,
        OutboxOperationAction action,
        string actionToken,
        string reasonCode,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class CameraAgentOperatorUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    CameraAgentOperationsSummaryProvider operationsProvider,
    ICameraAgentGallery gallery,
    CaptureAdmissionCoordinator captureControl,
    ICameraAgentStorageResolver storageResolver,
    IArtifactOutbox artifactOutbox,
    IEnvironmentalObservationOutbox environmentalOutbox,
    EnvironmentalObservationDeliveryWakeup environmentalWakeup,
    ICameraAgentConfigurationAccessor configurationAccessor,
    IEnumerable<CaptureProcessingStepRegistration> processingRegistrations,
    IEnumerable<CameraModuleRegistration> moduleRegistrations,
    IOptions<CameraAgentHostOptions> hostOptions,
    OutboxOperationsTokenService tokens,
    TimeProvider timeProvider,
    ILogger<CameraAgentOperatorUiService> logger) : ICameraAgentOperatorUiService
{
    private const int MaximumQuarantineItems = 8;
    private const int QuarantineReadSize = 50;
    private readonly CameraAgentHostOptions _hostOptions = hostOptions.Value;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    public async ValueTask<OperatorUiResult<CameraAgentOperationsView>> GetOperationsAsync(
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CameraAgentOperationsView>();
        }

        try
        {
            var summary = await operationsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            var centralDisabled = _hostOptions.CentralIntegration.Mode == CentralIntegrationMode.Disabled;
            var artifacts = centralDisabled
                ? []
                : await ReadArtifactQuarantineAsync(cancellationToken).ConfigureAwait(false);
            var environmental = centralDisabled
                ? []
                : await ReadEnvironmentalQuarantineAsync(cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentOperationsView>.Success(new(summary, artifacts, environmental));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent operations UI read failed.");
            return Unavailable<CameraAgentOperationsView>("Current operations data is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    public async ValueTask<OperatorUiResult<CameraAgentGalleryPage>> GetGalleryPageAsync(
        CameraAgentGalleryQuery query,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CameraAgentGalleryPage>();
        }

        try
        {
            return OperatorUiResult<CameraAgentGalleryPage>.Success(
                await gallery.GetPageAsync(query, cancellationToken).ConfigureAwait(false));
        }
        catch (CameraAgentGalleryQueryException)
        {
            return OperatorUiResult<CameraAgentGalleryPage>.Failure(
                OperatorUiResultKind.Invalid,
                "The gallery filters or cursor are invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent gallery UI read failed.");
            return Unavailable<CameraAgentGalleryPage>("The gallery is temporarily unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    public async ValueTask<OperatorUiResult<OperatorOutboxPage>> GetQuarantinePageAsync(
        string kind,
        string? storageAlias,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<OperatorOutboxPage>();
        }
        if (pageSize is < 1 or > 50 ||
            !string.Equals(kind, "Artifact", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(kind, "Environmental", StringComparison.OrdinalIgnoreCase))
        {
            return OperatorUiResult<OperatorOutboxPage>.Failure(
                OperatorUiResultKind.Invalid,
                "The quarantine source or cursor is invalid.");
        }

        try
        {
            if (_hostOptions.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
            {
                return OperatorUiResult<OperatorOutboxPage>.Success(new(
                    string.Equals(kind, "Artifact", StringComparison.OrdinalIgnoreCase) ? "Artifact" : "Environmental",
                    [], null, [], null));
            }

            if (string.Equals(kind, "Artifact", StringComparison.OrdinalIgnoreCase))
            {
                var locations = await storageResolver.GetUploadLocationsAsync(cancellationToken).ConfigureAwait(false);
                var aliases = locations.Select(static location => location.Alias).ToArray();
                var location = string.IsNullOrWhiteSpace(storageAlias)
                    ? locations.Count == 0 ? null : locations[0]
                    : locations.SingleOrDefault(item => string.Equals(item.Alias, storageAlias, StringComparison.Ordinal));
                if (location is null)
                {
                    return string.IsNullOrWhiteSpace(storageAlias) && locations.Count == 0
                        ? OperatorUiResult<OperatorOutboxPage>.Success(new("Artifact", aliases, null, [], null))
                        : OperatorUiResult<OperatorOutboxPage>.Failure(
                            OperatorUiResultKind.Invalid,
                            "The selected storage alias is invalid.");
                }
                ArtifactOutboxOperationsCursor? artifactCursor = null;
                if (!string.IsNullOrWhiteSpace(cursor) &&
                    !tokens.TryReadArtifactCursor(cursor, location.Alias, out artifactCursor))
                {
                    return OperatorUiResult<OperatorOutboxPage>.Failure(
                        OperatorUiResultKind.Invalid,
                        "The quarantine source or cursor is invalid.");
                }

                var page = await artifactOutbox.ReadOperationsPageAsync(
                    location.Root,
                    pageSize,
                    artifactCursor,
                    cancellationToken).ConfigureAwait(false);
                var items = new List<OperatorOutboxItem>(page.Items.Count);
                foreach (var item in page.Items)
                {
                    var audit = await artifactOutbox.ReadOperationsAuditAsync(
                        location.Root, item.RecordKey, 1, null, cancellationToken).ConfigureAwait(false);
                    items.Add(new OperatorOutboxItem(
                        "Artifact",
                        item.Status.ToString(),
                        location.Alias,
                        item.Role?.ToString(),
                        item.AttemptCount,
                        item.PayloadBytes,
                        item.UpdatedUtc,
                        OutboxOperationsReasonCodes.Sanitize(item.ReasonCode),
                        item.CanReplay
                            ? tokens.ProtectArtifactAction(OutboxOperationAction.Replay, location.Alias, item.RecordKey)
                            : null,
                        item.CanAbandon
                            ? tokens.ProtectArtifactAction(OutboxOperationAction.Abandon, location.Alias, item.RecordKey)
                            : null,
                        item.CreatedUtc,
                        item.NextAttemptUtc,
                        item.MediaType,
                        FormatAuditSummary(audit.Items.Count == 0 ? null : audit.Items[0])));
                }
                return OperatorUiResult<OperatorOutboxPage>.Success(new(
                    "Artifact",
                    aliases,
                    location.Alias,
                    items,
                    page.NextCursor is null ? null : tokens.ProtectArtifactCursor(location.Alias, page.NextCursor)));
            }

            if (!string.IsNullOrWhiteSpace(storageAlias))
            {
                return OperatorUiResult<OperatorOutboxPage>.Failure(
                    OperatorUiResultKind.Invalid,
                    "Environmental quarantine is not storage-alias scoped.");
            }
            EnvironmentalOutboxOperationsCursor? environmentalCursor = null;
            if (!string.IsNullOrWhiteSpace(cursor) &&
                !tokens.TryReadEnvironmentalCursor(cursor, out environmentalCursor))
            {
                return OperatorUiResult<OperatorOutboxPage>.Failure(
                    OperatorUiResultKind.Invalid,
                    "The quarantine source or cursor is invalid.");
            }
            var environmentalPage = await environmentalOutbox.ReadOperationsPageAsync(
                _hostOptions.RawIngressRoot,
                pageSize,
                environmentalCursor,
                cancellationToken).ConfigureAwait(false);
            var environmentalItems = new List<OperatorOutboxItem>(environmentalPage.Items.Count);
            foreach (var item in environmentalPage.Items)
            {
                var audit = await environmentalOutbox.ReadOperationsAuditAsync(
                    _hostOptions.RawIngressRoot, item.RecordId, 1, null, cancellationToken).ConfigureAwait(false);
                environmentalItems.Add(new OperatorOutboxItem(
                    "Environmental",
                    item.Status,
                    null,
                    null,
                    item.AttemptCount,
                    item.PayloadBytes,
                    item.UpdatedUtc,
                    OutboxOperationsReasonCodes.Sanitize(item.ReasonCode),
                    item.CanReplay
                        ? tokens.ProtectEnvironmentalAction(OutboxOperationAction.Replay, item.RecordId)
                        : null,
                    item.CanAbandon
                        ? tokens.ProtectEnvironmentalAction(OutboxOperationAction.Abandon, item.RecordId)
                        : null,
                    item.CreatedUtc,
                    null,
                    null,
                    FormatAuditSummary(audit.Items.Count == 0 ? null : audit.Items[0])));
            }
            return OperatorUiResult<OperatorOutboxPage>.Success(new(
                "Environmental",
                [],
                null,
                environmentalItems,
                environmentalPage.NextCursor is null
                    ? null
                    : tokens.ProtectEnvironmentalCursor(environmentalPage.NextCursor)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent quarantine UI read failed.");
            return Unavailable<OperatorOutboxPage>("The quarantine page is temporarily unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    public async ValueTask<OperatorUiResult<CameraAgentGalleryCapture>> GetGalleryCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CameraAgentGalleryCapture>();
        }

        try
        {
            var capture = await gallery.GetCaptureAsync(captureId, cancellationToken).ConfigureAwait(false);
            return capture is null
                ? OperatorUiResult<CameraAgentGalleryCapture>.Failure(
                    OperatorUiResultKind.NotFound,
                    "The requested capture was not found.")
                : OperatorUiResult<CameraAgentGalleryCapture>.Success(capture);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent gallery detail UI read failed.");
            return Unavailable<CameraAgentGalleryCapture>("The capture detail is temporarily unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    public async ValueTask<OperatorUiResult<CameraAgentSystemStatus>> GetSystemStatusAsync(
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<CameraAgentSystemStatus>();
        }

        try
        {
            var config = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
            var pipeline = config.ResolveProcessingSteps()
                .Select((step, index) => new CameraAgentPipelineNodeStatus(
                    string.IsNullOrWhiteSpace(step.Id) ? $"node-{index + 1}" : step.Id,
                    ResolveProcessingAlias(step.Type, processingRegistrations),
                    step.Required,
                    step.DependsOn?.Order(StringComparer.Ordinal).ToArray() ?? []))
                .OrderBy(static node => node.Id, StringComparer.Ordinal)
                .ToArray();
            var sensor = config.Rig.Sensor;
            var optics = config.Rig.Optics;
            var capture = config.Rig.Pipeline;
            var envelope = capture.Envelope;
            var distribution = _hostOptions.CaptureDistribution;
            var environmental = _hostOptions.EnvironmentalDelivery;
            var transient = _hostOptions.TransientDetection;
            var centralEnabled = _hostOptions.CentralIntegration.Mode == CentralIntegrationMode.Enabled;

            var status = new CameraAgentSystemStatus(
                "Unversioned startup snapshot",
                string.Empty,
                "Validated at startup",
                config.AgentId ?? "Unavailable",
                ResolveModuleAlias(config.ModuleType, moduleRegistrations),
                _hostOptions.CentralIntegration.Mode.ToString(),
                new CameraAgentSensorStatus(
                    sensor.Name,
                    sensor.WidthPixels,
                    sensor.HeightPixels,
                    sensor.PixelSizeMicrons,
                    sensor.ColorMode.ToString(),
                    sensor.PixelFormat.ToString(),
                    sensor.ResponseMode.ToString(),
                    config.Rig.ProfileVersion,
                    sensor.SensorRecipeVersion),
                new CameraAgentOpticsStatus(
                    optics.LensKind.ToString(),
                    optics.ProjectionModel,
                    optics.FocalLengthMillimeters,
                    optics.FieldOfViewDegrees,
                    optics.VerticalFieldOfViewDegrees,
                    optics.CalibrationVersion,
                    optics.HorizontalFlip,
                    optics.Crop is null ? "Full sensor" : $"{optics.Crop.Width} x {optics.Crop.Height} sensor crop"),
                new CameraAgentCapturePolicyStatus(
                    capture.CadenceMode.ToString(),
                    capture.CaptureInterval.TotalSeconds,
                    capture.DayExposure.TotalMilliseconds,
                    capture.NightExposure.TotalMilliseconds,
                    capture.DayGain,
                    capture.NightGain,
                    envelope?.MinExposure.TotalMilliseconds,
                    envelope?.MaxExposure.TotalMilliseconds,
                    envelope?.MinGain,
                    envelope?.MaxGain,
                    envelope?.Preference.ToString() ?? "Fixed day/night defaults"),
                pipeline,
                new CameraAgentRetentionStatus(
                    _hostOptions.RetentionSweepIntervalMinutes,
                    _hostOptions.DiskPressureThresholdPercent,
                    _hostOptions.DiskPressureRecoveryPercent,
                    _hostOptions.DiskPressureRetentionDays,
                    _hostOptions.RawIngressReserveBytes),
                new CameraAgentUploadStatus(
                    centralEnabled && distribution.UploadEnabled,
                    _hostOptions.UploadBatchSize,
                    _hostOptions.UploadPollIntervalSeconds,
                    _hostOptions.UploadRetryInitialDelaySeconds,
                    _hostOptions.UploadRetryMaximumDelaySeconds,
                    _hostOptions.UploadBandwidthLimitBytesPerSecond,
                    distribution.RequiredMaximumPendingCount,
                    distribution.RequiredMaximumPendingBytes,
                    distribution.OptionalMaximumPendingCount,
                    distribution.OptionalMaximumPendingBytes),
                new CameraAgentEnvironmentalPolicyStatus(
                    centralEnabled && environmental.Enabled,
                    environmental.BatchSize,
                    environmental.PollIntervalSeconds,
                    environmental.MaximumAttempts,
                    environmental.MaximumPendingCount,
                    environmental.MaximumPendingBytes),
                new CameraAgentTransientPolicyStatus(
                    transient.Mode.ToString(),
                    transient.Required,
                    transient.CandidateTimeoutMinutes,
                    transient.WorkerPollIntervalMilliseconds,
                    transient.MaximumAttempts,
                    transient.MaximumAdjacentStartIntervalSeconds,
                    transient.StarMaximumResults));
            return OperatorUiResult<CameraAgentSystemStatus>.Success(status with
            {
                SnapshotIdentity = ComputeSnapshotIdentity(status)
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent system status UI read failed.");
            return Unavailable<CameraAgentSystemStatus>("The startup configuration snapshot is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    public async Task<OperatorUiResult<OperatorCommandReceipt>> SetCapturePausedAsync(
        bool paused,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var actor = await GetAuthorizedActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return Denied<OperatorCommandReceipt>();
        }

        try
        {
            var result = paused
                ? await captureControl.PauseAsync(
                    idempotencyKey, expectedVersion, actor, "operator-maintenance", cancellationToken).ConfigureAwait(false)
                : await captureControl.ResumeAsync(
                    idempotencyKey, expectedVersion, actor, "operator-resume", cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<OperatorCommandReceipt>.Success(new(
                paused ? "Pause capture" : "Resume capture",
                result.Replayed ? "Duplicate receipt" : result.Changed ? "Applied" : "Already current",
                result.State.ToString(),
                result.Version,
                result.CompletedUtc));
        }
        catch (CaptureControlValidationException)
        {
            return OperatorUiResult<OperatorCommandReceipt>.Failure(
                OperatorUiResultKind.Invalid,
                "The capture command was invalid.");
        }
        catch (CaptureControlConflictException)
        {
            return OperatorUiResult<OperatorCommandReceipt>.Failure(
                OperatorUiResultKind.Conflict,
                "Capture state changed. Refresh and review the command again.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent capture UI command failed.");
            return Unavailable<OperatorCommandReceipt>("The capture command could not be completed.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    public async ValueTask<OperatorUiResult<OperatorCommandReceipt>> ResolveOutboxAsync(
        string kind,
        OutboxOperationAction action,
        string actionToken,
        string reasonCode,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var actor = await GetAuthorizedActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return Denied<OperatorCommandReceipt>();
        }
        if (!OutboxOperationsReasonCodes.IsAllowed(action, reasonCode) ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            return OperatorUiResult<OperatorCommandReceipt>.Failure(
                OperatorUiResultKind.Invalid,
                "The outbox command was invalid.");
        }

        try
        {
            OutboxOperationDisposition disposition;
            if (string.Equals(kind, "Artifact", StringComparison.Ordinal) &&
                tokens.TryReadArtifactAction(action, actionToken, out var alias, out var recordKey))
            {
                var location = await storageResolver.ResolveAliasAsync(alias, cancellationToken).ConfigureAwait(false);
                if (location is null)
                {
                    return NotFound<OperatorCommandReceipt>("The outbox item is no longer available.");
                }
                disposition = await artifactOutbox.ResolveOperationsAsync(
                    location.Root, recordKey, action, idempotencyKey, "owner", reasonCode, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (string.Equals(kind, "Environmental", StringComparison.Ordinal) &&
                tokens.TryReadEnvironmentalAction(action, actionToken, out var recordId))
            {
                disposition = await environmentalOutbox.ResolveOperationsAsync(
                    _hostOptions.RawIngressRoot,
                    recordId,
                    action,
                    idempotencyKey,
                    "owner",
                    reasonCode,
                    cancellationToken).ConfigureAwait(false);
                if (action == OutboxOperationAction.Replay)
                {
                    environmentalWakeup.Signal();
                }
            }
            else
            {
                return NotFound<OperatorCommandReceipt>("The outbox action expired or is no longer available.");
            }

            return OperatorUiResult<OperatorCommandReceipt>.Success(new(
                string.Equals(kind, "Artifact", StringComparison.Ordinal)
                    ? $"{action} artifact item"
                    : $"{action} environmental item",
                disposition == OutboxOperationDisposition.Duplicate ? "Duplicate receipt" : "Applied",
                action == OutboxOperationAction.Replay ? "Pending" : "Abandoned",
                null,
                timeProvider.GetUtcNow()));
        }
        catch (OutboxOperationCollisionException)
        {
            return OperatorUiResult<OperatorCommandReceipt>.Failure(
                OperatorUiResultKind.Conflict,
                "The outbox item changed. Refresh and review the command again.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            logger.LogInformation(exception, "CameraAgent outbox UI command conflicted with current state.");
            return OperatorUiResult<OperatorCommandReceipt>.Failure(
                OperatorUiResultKind.Conflict,
                "The outbox item changed. Refresh and review the command again.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent outbox UI command failed.");
            return Unavailable<OperatorCommandReceipt>("The outbox command could not be completed.");
        }
    }

    private async ValueTask<IReadOnlyList<OperatorOutboxItem>> ReadArtifactQuarantineAsync(
        CancellationToken cancellationToken)
    {
        var items = new List<OperatorOutboxItem>();
        foreach (var location in await storageResolver.GetUploadLocationsAsync(cancellationToken).ConfigureAwait(false))
        {
            var page = await artifactOutbox.ReadOperationsPageAsync(
                location.Root, QuarantineReadSize, null, cancellationToken).ConfigureAwait(false);
            items.AddRange(page.Items
                .Where(static item => item.Status == ArtifactOutboxStatus.Quarantined)
                .Select(item => new OperatorOutboxItem(
                    "Artifact",
                    item.Status.ToString(),
                    location.Alias,
                    item.Role?.ToString(),
                    item.AttemptCount,
                    item.PayloadBytes,
                    item.UpdatedUtc,
                    OutboxOperationsReasonCodes.Sanitize(item.ReasonCode),
                    item.CanReplay
                        ? tokens.ProtectArtifactAction(OutboxOperationAction.Replay, location.Alias, item.RecordKey)
                        : null,
                    item.CanAbandon
                        ? tokens.ProtectArtifactAction(OutboxOperationAction.Abandon, location.Alias, item.RecordKey)
                        : null)));
        }
        return items.OrderByDescending(static item => item.UpdatedUtc).Take(MaximumQuarantineItems).ToArray();
    }

    private async ValueTask<IReadOnlyList<OperatorOutboxItem>> ReadEnvironmentalQuarantineAsync(
        CancellationToken cancellationToken)
    {
        var page = await environmentalOutbox.ReadOperationsPageAsync(
            _hostOptions.RawIngressRoot, QuarantineReadSize, null, cancellationToken).ConfigureAwait(false);
        return page.Items
            .Where(static item => item.Status is "quarantined" or "terminal")
            .Take(MaximumQuarantineItems)
            .Select(item => new OperatorOutboxItem(
                "Environmental",
                item.Status,
                null,
                null,
                item.AttemptCount,
                item.PayloadBytes,
                item.UpdatedUtc,
                OutboxOperationsReasonCodes.Sanitize(item.ReasonCode),
                item.CanReplay
                    ? tokens.ProtectEnvironmentalAction(OutboxOperationAction.Replay, item.RecordId)
                    : null,
                item.CanAbandon
                    ? tokens.ProtectEnvironmentalAction(OutboxOperationAction.Abandon, item.RecordId)
                    : null))
            .ToArray();
    }

    private async ValueTask<bool> IsAuthorizedAsync(string policyName)
    {
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return (await authorizationService.AuthorizeAsync(authenticationState.User, policyName).ConfigureAwait(false)).Succeeded;
    }

    private async ValueTask<string?> GetAuthorizedActorAsync()
    {
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        if (!(await authorizationService.AuthorizeAsync(
                authenticationState.User,
                CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)).Succeeded)
        {
            return null;
        }
        return authenticationState.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 and <= 128 } actor
            ? actor
            : null;
    }

    private static OperatorUiResult<T> Denied<T>() => OperatorUiResult<T>.Failure(
        OperatorUiResultKind.Unauthorized,
        "You are not authorized for this operation.");

    private static OperatorUiResult<T> NotFound<T>(string message) =>
        OperatorUiResult<T>.Failure(OperatorUiResultKind.NotFound, message);

    private static OperatorUiResult<T> Unavailable<T>(string message) =>
        OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, message);

    private static string? FormatAuditSummary(OutboxOperationsAuditRecord? audit) => audit is null
        ? null
        : $"{OperationsLabel(audit.Action)} by {audit.ActorKind}, {OutboxOperationsReasonCodes.Sanitize(audit.ReasonCode)}, {audit.OccurredUtc:O}";

    private static string OperationsLabel(string value) => string.Equals(value, "replay", StringComparison.OrdinalIgnoreCase)
        ? "Replay"
        : string.Equals(value, "abandon", StringComparison.OrdinalIgnoreCase)
            ? "Abandon"
            : "System update";

    internal static string ResolveProcessingAlias(
        string configuredType,
        IEnumerable<CaptureProcessingStepRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            if (MatchesRegisteredType(configuredType, registration.Alias, registration.ImplementationType))
            {
                return registration.Alias;
            }
        }
        return "Unavailable";
    }

    internal static string ResolveModuleAlias(
        string configuredType,
        IEnumerable<CameraModuleRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            if (MatchesRegisteredType(configuredType, registration.ModuleType, registration.ImplementationType))
            {
                return registration.ModuleType;
            }
        }
        return "Unavailable";
    }

    internal static string ComputeSnapshotIdentity(CameraAgentSystemStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var allowlisted = new
        {
            status.SnapshotLabel,
            status.ValidationStatus,
            status.AgentId,
            status.ModuleType,
            status.CentralIntegration,
            status.Sensor,
            status.Optics,
            status.Capture,
            Pipeline = status.Pipeline
                .OrderBy(static node => node.Id, StringComparer.Ordinal)
                .Select(static node => node with
                {
                    Dependencies = node.Dependencies.Order(StringComparer.Ordinal).ToArray()
                })
                .ToArray(),
            status.Retention,
            status.Upload,
            status.Environmental,
            status.Transient
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(allowlisted)));
    }

    private static bool MatchesRegisteredType(string configuredType, string alias, Type implementationType) =>
        string.Equals(configuredType, alias, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(configuredType, implementationType.Name, StringComparison.Ordinal) ||
        string.Equals(configuredType, implementationType.FullName, StringComparison.Ordinal) ||
        string.Equals(configuredType, implementationType.AssemblyQualifiedName, StringComparison.Ordinal);
}
