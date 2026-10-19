using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentNamedRigUiService
{
    ValueTask<OperatorUiResult<NamedRigUiCatalog>> GetAsync(CancellationToken token);
    ValueTask<OperatorUiResult<NamedRigInventory>> GetInventoryAsync(CancellationToken token);
    ValueTask<OperatorUiResult<NamedRigHistoryPage>> GetHistoryAsync(string profileId, int limit,
        long? beforeRevisionNumber, long? version, CancellationToken token);
    ValueTask<OperatorUiResult<NamedEquipmentDetail>> GetEquipmentAsync(string revisionId, CancellationToken token);
    ValueTask<OperatorUiResult<NamedEquipmentDefinition>> SaveEquipmentAsync(string? definitionId, string kind,
        string name, JsonElement definition, string? basisRevisionId, string? expectedRevisionId, CancellationToken token);
    ValueTask<OperatorUiResult<NamedEquipmentDefinition>> CreateZwoStarterAsync(string templateId, string name, CancellationToken token);
    ValueTask<OperatorUiResult<NamedRigProfile>> SaveProfileAsync(string? profileId, string name, CancellationToken token);
    ValueTask<OperatorUiResult<NamedRigRevision>> ComposeAsync(string profileId, string cameraId, string opticsId,
        string mountId, CancellationToken token);
    ValueTask<OperatorUiResult<NamedRigPreview>> PreviewAsync(string revisionId, CancellationToken token);
    ValueTask<OperatorUiResult<NamedRigStageReceipt>> StageAsync(string revisionId, long version, string key,
        bool acknowledgeUnvalidated, string expectedScheduleRevisionId, string expectedScheduleProfileSha256,
        CancellationToken token);
    ValueTask<OperatorUiResult<NamedRigStageReceipt>> CancelAsync(string revisionId, long version, string key,
        CancellationToken token);

    /// <summary>
    /// Saves the changed optics/mount equipment, composes a new revision of the active named rig, previews it
    /// against the active schedule and stages it for restart as one orchestrated operator command.
    /// </summary>
    ValueTask<OperatorUiResult<ActiveRigEditOutcome>> ApplyActiveRigEditAsync(ActiveRigEditRequest request,
        CancellationToken token);

    ValueTask<OperatorUiResult<CameraAgentRestartStatus>> GetRestartStatusAsync(CancellationToken token);

    /// <summary>Stops the host so its supervisor restarts it and applies staged restart-bound changes.</summary>
    ValueTask<OperatorUiResult<CameraAgentRestartDisposition>> RequestRestartAsync(CancellationToken token);
}

internal sealed record NamedRigUiCatalog(NamedRigSelection Selection, IReadOnlyList<NamedRigRevision> Revisions,
    string? PendingRuntimeFailure, string? ActiveRuntimeFailure);

/// <summary>
/// The operator-editable subset of the active rig. Optics carry the horizontal flip, field of view and focal length;
/// the mount carries the boresight and roll adjustment; a virtual sky camera carries its sky lighting. Everything else
/// is copied from the active rig unchanged.
/// </summary>
/// <param name="SkyLighting">
/// A virtual sky camera's new sky lighting choice, or null to keep the camera as it is.
/// </param>
internal sealed record ActiveRigEditRequest(
    string ActiveRevisionId, long SelectionVersion, bool HorizontalFlip, double FieldOfViewDegrees,
    double FocalLengthMillimeters, double BoresightAltitudeDegrees, double BoresightAzimuthDegrees,
    double RollAdjustmentDegrees, string? SkyLighting = null);

internal enum ActiveRigEditStatus
{
    Staged,
    NoChanges,
    Failed
}

/// <summary>What an active-rig edit durably recorded and, when it stopped early, which steps did not happen.</summary>
internal sealed record ActiveRigEditOutcome(
    ActiveRigEditStatus Status, IReadOnlyList<string> Recorded, IReadOnlyList<string> NotDone,
    string? Failure = null, string? ComposedRevisionId = null, NamedRigStageReceipt? Receipt = null);

internal sealed record CameraAgentRestartStatus(bool Supervised, bool CanRequest, bool Restarting);

internal enum CameraAgentRestartDisposition
{
    Scheduled,
    AlreadyRequested,
    Unsupervised
}

internal sealed partial class CameraAgentNamedRigUiService(
    AuthenticationStateProvider authentication, IAuthorizationService authorization,
    SqliteNamedRigProfileStore store, CaptureScheduleRuntimeCoordinator runtime,
    IHostApplicationLifetime lifetime, IConfiguration configuration, TimeProvider timeProvider,
    ILogger<CameraAgentNamedRigUiService> logger) : ICameraAgentNamedRigUiService
{
    internal const string SupervisedRestartKey = CameraAgentRestart.SupervisedKey;

    /// <summary>Delay between answering the restart request and stopping, so the reply reaches the browser.</summary>
    internal TimeSpan RestartDelay { get; init; } = CameraAgentRestart.DefaultDelay;

    private async Task<ClaimsPrincipal?> PrincipalAsync(string policy)
    {
        var principal = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        return (await authorization.AuthorizeAsync(principal, null, policy).ConfigureAwait(false)).Succeeded ? principal : null;
    }

    private ValueTask<OperatorUiResult<T>> ReadAsync<T>(Func<Task<T>> operation)
        => ExecuteAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1, false, operation);

    private ValueTask<OperatorUiResult<T>> MutateAsync<T>(Func<string, Task<T>> operation)
        => ExecuteAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1, true, operation);

    private ValueTask<OperatorUiResult<T>> ExecuteAsync<T>(string policy, bool requireOwner, Func<Task<T>> operation)
        => ExecuteAsync(policy, requireOwner, _ => operation());

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI boundary logs internal failures and returns fixed messages.")]
    private async ValueTask<OperatorUiResult<T>> ExecuteAsync<T>(string policy, bool requireOwner, Func<string, Task<T>> operation)
    {
        try
        {
            var principal = await PrincipalAsync(policy).ConfigureAwait(false);
            var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
            if (principal is null || requireOwner && string.IsNullOrWhiteSpace(actor))
                return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");
            return OperatorUiResult<T>.Success(await operation(actor ?? string.Empty).ConfigureAwait(false));
        }
        catch (ArgumentException) { return OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, "The rig command is invalid."); }
        catch (KeyNotFoundException) { return OperatorUiResult<T>.Failure(OperatorUiResultKind.NotFound, "The revision was not found."); }
        catch (CaptureScheduleStoreConflictException) { return OperatorUiResult<T>.Failure(OperatorUiResultKind.Conflict, "Rig state changed. Refresh before retrying."); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Conflict, "A rig or equipment name already exists. Choose a different name and retry.");
        }
        catch (CaptureProfileCompatibilityException) { return OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, "The rig is incompatible with this CameraAgent."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Named rig UI operation failed.");
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, "Named rig data is unavailable.");
        }
    }

    public ValueTask<OperatorUiResult<NamedRigUiCatalog>> GetAsync(CancellationToken token)
        => ReadAsync(async () =>
        {
            var catalog = await store.GetAsync(token).ConfigureAwait(false);
            return new NamedRigUiCatalog(
                new NamedRigSelection(catalog.Selection.ActiveRevisionId, catalog.Selection.PendingRevisionId,
                    catalog.Selection.Version, PendingScheduleRevisionId: catalog.Selection.PendingScheduleRevisionId),
                catalog.Revisions.Select(Sanitize).ToArray(),
                store.PendingRuntimeFailure is not null && catalog.Selection.PendingRevisionId is not null
                    ? "Pending rig failed to initialize." : null,
                store.ActiveRuntimeFailure is not null ? "Active camera is unavailable for capture." : null);
        });

    private static NamedRigRevision Sanitize(NamedRigRevision revision)
        => revision with { Module = revision.Module with { Options = null } };

    public ValueTask<OperatorUiResult<NamedRigInventory>> GetInventoryAsync(CancellationToken token)
        => ReadAsync(() => store.GetInventoryAsync(token));

    public ValueTask<OperatorUiResult<NamedRigHistoryPage>> GetHistoryAsync(string profileId, int limit,
        long? beforeRevisionNumber, long? version, CancellationToken token)
        => ReadAsync(() => store.GetHistoryAsync(profileId, limit, beforeRevisionNumber, version, token));

    public ValueTask<OperatorUiResult<NamedEquipmentDetail>> GetEquipmentAsync(string revisionId, CancellationToken token)
        => ReadAsync(() => store.GetEquipmentAsync(revisionId, token));

    public ValueTask<OperatorUiResult<NamedEquipmentDefinition>> SaveEquipmentAsync(string? definitionId, string kind,
        string name, JsonElement definition, string? basisRevisionId, string? expectedRevisionId, CancellationToken token)
        => MutateAsync(_ => store.SaveEquipmentAsync(definitionId, kind, name, definition, token,
            basisRevisionId, expectedRevisionId));

    public ValueTask<OperatorUiResult<NamedEquipmentDefinition>> CreateZwoStarterAsync(string templateId, string name,
        CancellationToken token)
        => MutateAsync(async _ =>
        {
            var camera = ZwoCameraStarter.Create(templateId);
            return await store.SaveEquipmentAsync(null, "camera", name,
                JsonSerializer.SerializeToElement(camera), token).ConfigureAwait(false);
        });

    public ValueTask<OperatorUiResult<NamedRigProfile>> SaveProfileAsync(string? profileId, string name, CancellationToken token)
        => MutateAsync(_ => store.SaveProfileNameAsync(profileId, name, token));

    public ValueTask<OperatorUiResult<NamedRigRevision>> ComposeAsync(string profileId, string cameraId, string opticsId,
        string mountId, CancellationToken token)
        => MutateAsync(async _ => Sanitize(await store.ComposeAsync(profileId, cameraId, opticsId, mountId, token).ConfigureAwait(false)));

    public ValueTask<OperatorUiResult<NamedRigPreview>> PreviewAsync(string revisionId, CancellationToken token)
        => ReadAsync(() => store.PreviewAsync(revisionId, token));

    public ValueTask<OperatorUiResult<NamedRigStageReceipt>> StageAsync(string revisionId, long version, string key,
        bool acknowledgeUnvalidated, string expectedScheduleRevisionId, string expectedScheduleProfileSha256,
        CancellationToken token)
        => MutateAsync(actor => runtime.Snapshot is null
            ? throw new InvalidOperationException("Runtime not ready")
            : store.StageAsync(revisionId, version, key, actor, acknowledgeUnvalidated,
                expectedScheduleRevisionId, expectedScheduleProfileSha256, token));

    public ValueTask<OperatorUiResult<NamedRigStageReceipt>> CancelAsync(string revisionId, long version, string key,
        CancellationToken token)
        => MutateAsync(actor => store.CancelPendingAsync(revisionId, version, key, actor, token));

    public ValueTask<OperatorUiResult<ActiveRigEditOutcome>> ApplyActiveRigEditAsync(ActiveRigEditRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MutateAsync(actor => new CameraAgentActiveRigEditor(store, runtime, logger).ApplyAsync(request, actor, token));
    }

    [LoggerMessage(LogLevel.Information, "CameraAgent restart requested by {Actor} to apply staged changes.")]
    private static partial void LogRestartRequested(ILogger logger, string actor);

    public ValueTask<OperatorUiResult<CameraAgentRestartStatus>> GetRestartStatusAsync(CancellationToken token)
        => ReadAsync(async () =>
        {
            var principal = await PrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
            var canRequest = principal is not null &&
                !string.IsNullOrWhiteSpace(CameraAgentCredentialAccess.GetOwnerId(principal));
            return new CameraAgentRestartStatus(IsSupervised(configuration), canRequest, RestartRequested);
        });

    public ValueTask<OperatorUiResult<CameraAgentRestartDisposition>> RequestRestartAsync(CancellationToken token)
        => MutateAsync(actor => Task.FromResult(CameraAgentRestart.Request(
            lifetime, configuration, actor, RestartDelay, timeProvider, logger, LogRestartRequested)));

    private bool RestartRequested => CameraAgentRestart.IsRequested(lifetime);

    /// <inheritdoc cref="CameraAgentRestart.IsSupervised"/>
    internal static bool IsSupervised(IConfiguration configuration) => CameraAgentRestart.IsSupervised(configuration);
}
