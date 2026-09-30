using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// the mount carries the boresight and roll adjustment. Everything else is copied from the active rig unchanged.
/// </summary>
internal sealed record ActiveRigEditRequest(
    string ActiveRevisionId, long SelectionVersion, bool HorizontalFlip, double FieldOfViewDegrees,
    double FocalLengthMillimeters, double BoresightAltitudeDegrees, double BoresightAzimuthDegrees,
    double RollAdjustmentDegrees);

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
    internal const string SupervisedRestartKey = "CameraAgent:Restart:Supervised";
    private static readonly JsonSerializerOptions EquipmentJson = new(JsonSerializerDefaults.Web);
    // Scoped per circuit, so the one-shot restart latch is keyed by the process-wide host lifetime.
    private static readonly ConditionalWeakTable<IHostApplicationLifetime, string> RestartRequests = new();

    /// <summary>Delay between answering the restart request and stopping, so the reply reaches the browser.</summary>
    internal TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);

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
        return MutateAsync(actor => ApplyActiveRigEditCoreAsync(request, actor, token));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A partially applied edit must still report what it durably recorded.")]
    private async Task<ActiveRigEditOutcome> ApplyActiveRigEditCoreAsync(ActiveRigEditRequest request, string actor,
        CancellationToken token)
    {
        if (runtime.Snapshot is null) throw new InvalidOperationException("Runtime not ready");
        var catalog = await store.GetAsync(token).ConfigureAwait(false);
        var active = catalog.Revisions.FirstOrDefault(revision => revision.RevisionId == catalog.Selection.ActiveRevisionId);
        if (active is null || !string.Equals(active.RevisionId, request.ActiveRevisionId, StringComparison.Ordinal) ||
            catalog.Selection.Version != request.SelectionVersion)
            return Stopped("The active rig changed since this page loaded. Refresh and retry.", [], []);
        if (catalog.Selection.PendingRevisionId is not null)
            return Stopped("A rig change is already awaiting restart. Restart or cancel it before editing again.", [], []);
        ValidateEdit(request, active.Rig.Optics);
        var optics = active.Rig.Optics with
        {
            HorizontalFlip = request.HorizontalFlip,
            FieldOfViewDegrees = request.FieldOfViewDegrees,
            FocalLengthMillimeters = request.FocalLengthMillimeters
        };
        var mount = active.Rig.Orientation with
        {
            BoresightAltitudeDegrees = request.BoresightAltitudeDegrees,
            BoresightAzimuthDegrees = request.BoresightAzimuthDegrees,
            RollAdjustmentDegrees = request.RollAdjustmentDegrees
        };
        var opticsChanged = optics != active.Rig.Optics;
        var mountChanged = mount != active.Rig.Orientation;
        if (!opticsChanged && !mountChanged)
            return new ActiveRigEditOutcome(ActiveRigEditStatus.NoChanges, [], []);

        // Preview of the active revision reads the same schedule and selection state that staging requires, so
        // a pending draft is reported before anything is recorded.
        var gate = await store.PreviewAsync(active.RevisionId, token).ConfigureAwait(false);
        if (gate.Failure is "pending-named-rig")
            return Stopped("A rig change is already awaiting restart. Restart or cancel it before editing again.", [], []);
        if (gate.Failure is "pending-schedule-draft")
            return Stopped("A capture schedule draft is pending. Apply or discard it on Schedule before editing the rig.", [], []);
        if (!string.Equals(runtime.Snapshot?.Revision.RevisionId, gate.ScheduleRevisionId, StringComparison.Ordinal))
            return Stopped("The capture runtime has not loaded the active schedule. Refresh and retry.", [], []);

        var notDone = new List<string>();
        if (opticsChanged) notDone.Add("Optics were not saved.");
        if (mountChanged) notDone.Add("Mount was not saved.");
        notDone.Add("No rig revision was composed.");
        notDone.Add("The new rig revision was not previewed.");
        notDone.Add("Nothing was staged; the active rig and schedule are unchanged.");
        var recorded = new List<string>();
        var completed = 0;
        string? composedId = null;
        try
        {
            var opticsRevisionId = active.OpticsRevisionId;
            if (opticsChanged)
            {
                opticsRevisionId = await SaveEditedEquipmentAsync("optics", active.OpticsRevisionId, optics, recorded, token)
                    .ConfigureAwait(false);
                completed++;
            }
            var mountRevisionId = active.MountRevisionId;
            if (mountChanged)
            {
                mountRevisionId = await SaveEditedEquipmentAsync("mount", active.MountRevisionId, mount, recorded, token)
                    .ConfigureAwait(false);
                completed++;
            }
            var composed = await store.ComposeAsync(active.ProfileId, active.CameraRevisionId, opticsRevisionId,
                mountRevisionId, token).ConfigureAwait(false);
            composedId = composed.RevisionId;
            recorded.Add($"Composed rig revision r{composed.RevisionNumber.ToString(CultureInfo.InvariantCulture)}.");
            completed++;
            var preview = await store.PreviewAsync(composed.RevisionId, token).ConfigureAwait(false);
            completed++;
            if (!preview.Valid)
                return Stopped($"Preview failed: {preview.Failure}", recorded, notDone.Skip(completed), composedId);
            var receipt = await store.StageAsync(composed.RevisionId, request.SelectionVersion,
                Guid.NewGuid().ToString("N"), actor, acknowledgeUnvalidated: true, preview.ScheduleRevisionId,
                preview.ScheduleProfileSha256, token).ConfigureAwait(false);
            recorded.Add($"Staged rig revision r{composed.RevisionNumber.ToString(CultureInfo.InvariantCulture)} for restart.");
            LogRigEditStaged(logger, composed.RevisionId, actor);
            return new ActiveRigEditOutcome(ActiveRigEditStatus.Staged, recorded, [], null, composedId, receipt);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Stopped(DescribeEditFailure(exception), recorded, notDone.Skip(completed), composedId);
        }
    }

    private static ActiveRigEditOutcome Stopped(string failure, IEnumerable<string> recorded, IEnumerable<string> notDone,
        string? composedId = null)
        => new(ActiveRigEditStatus.Failed, recorded.ToArray(), notDone.ToArray(), failure, composedId);

    private string DescribeEditFailure(Exception exception)
    {
        switch (exception)
        {
            case CaptureScheduleStoreConflictException:
                return "Rig state changed while the edit was applied.";
            case SqliteException { SqliteErrorCode: 19 }:
                return "An equipment name already exists.";
            case CaptureProfileCompatibilityException:
                return "The rig is incompatible with this CameraAgent.";
            case KeyNotFoundException:
                return "A referenced revision was not found.";
            case ArgumentException:
                return "The rig change is invalid.";
            default:
                logger.LogWarning(exception, "Active rig edit failed.");
                return "Named rig data is unavailable.";
        }
    }

    private static void ValidateEdit(ActiveRigEditRequest request, OpticsProfile basis)
    {
        static bool Within(double value, double minimum, double maximum)
            => double.IsFinite(value) && value >= minimum && value <= maximum;
        var focalMinimum = basis.FocalLengthMillimeters == 0 ? 0 : double.Epsilon;
        if (!Within(request.FieldOfViewDegrees, double.Epsilon, 360) ||
            !Within(request.FocalLengthMillimeters, focalMinimum, double.MaxValue) ||
            !Within(request.BoresightAltitudeDegrees, -90, 90) ||
            !Within(request.BoresightAzimuthDegrees, 0, 360) ||
            !Within(request.RollAdjustmentDegrees, -360, 360))
            throw new ArgumentException("The active rig edit is out of range.", nameof(request));
    }

    /// <summary>
    /// Revises the equipment definition in place when the store allows it; installed (read-only) or superseded
    /// revisions are duplicated under a new name instead, so no installed definition is ever changed.
    /// </summary>
    private async Task<string> SaveEditedEquipmentAsync(string kind, string revisionId, object definition,
        List<string> recorded, CancellationToken token)
    {
        var detail = await store.GetEquipmentAsync(revisionId, token).ConfigureAwait(false);
        var json = JsonSerializer.SerializeToElement(definition, definition.GetType(), EquipmentJson);
        if (detail.CanRevise)
        {
            var revised = await store.SaveEquipmentAsync(detail.DefinitionId, kind, detail.DisplayName, json, token,
                detail.RevisionId, detail.RevisionId).ConfigureAwait(false);
            recorded.Add($"Saved {kind} \"{revised.DisplayName}\" revision {revised.RevisionNumber.ToString(CultureInfo.InvariantCulture)}.");
            return revised.RevisionId;
        }
        var taken = (await store.GetInventoryAsync(token).ConfigureAwait(false)).Equipment
            .Where(item => item.Kind == kind).Select(item => item.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = CopySuffix().Replace(detail.DisplayName.Trim(), string.Empty);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = ProposeCopyName(baseName, taken);
            try
            {
                var created = await store.SaveEquipmentAsync(null, kind, name, json, token).ConfigureAwait(false);
                recorded.Add($"Created {kind} \"{created.DisplayName}\" from \"{detail.DisplayName}\", which " +
                    (detail.IsInstalled ? "is installed and read-only." : "has a newer revision."));
                return created.RevisionId;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                taken.Add(name);
            }
        }
        throw new CaptureScheduleStoreConflictException("No free equipment name was found.");
    }

    private static string ProposeCopyName(string name, HashSet<string> taken)
    {
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? " copy" : $" copy {number.ToString(CultureInfo.InvariantCulture)}";
            var candidate = string.Concat(name[..Math.Min(name.Length, 128 - suffix.Length)].TrimEnd(), suffix);
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    [LoggerMessage(LogLevel.Information, "Active rig edit staged revision {RevisionId} for restart by {Actor}.")]
    private static partial void LogRigEditStaged(ILogger logger, string revisionId, string actor);

    [LoggerMessage(LogLevel.Information, "CameraAgent restart requested by {Actor} to apply staged changes.")]
    private static partial void LogRestartRequested(ILogger logger, string actor);

    [GeneratedRegex(@" copy( [0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CopySuffix();

    public ValueTask<OperatorUiResult<CameraAgentRestartStatus>> GetRestartStatusAsync(CancellationToken token)
        => ReadAsync(async () =>
        {
            var principal = await PrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
            var canRequest = principal is not null &&
                !string.IsNullOrWhiteSpace(CameraAgentCredentialAccess.GetOwnerId(principal));
            return new CameraAgentRestartStatus(IsSupervised(configuration), canRequest, RestartRequested);
        });

    public ValueTask<OperatorUiResult<CameraAgentRestartDisposition>> RequestRestartAsync(CancellationToken token)
        => MutateAsync(actor =>
        {
            if (!IsSupervised(configuration))
                return Task.FromResult(CameraAgentRestartDisposition.Unsupervised);
            if (lifetime.ApplicationStopping.IsCancellationRequested || !RestartRequests.TryAdd(lifetime, actor))
                return Task.FromResult(CameraAgentRestartDisposition.AlreadyRequested);
            LogRestartRequested(logger, actor);
            _ = StopAfterDelayAsync();
            return Task.FromResult(CameraAgentRestartDisposition.Scheduled);
        });

    private bool RestartRequested
        => lifetime.ApplicationStopping.IsCancellationRequested || RestartRequests.TryGetValue(lifetime, out _);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed background stop is logged and the one-shot latch is released for a retry.")]
    private async Task StopAfterDelayAsync()
    {
        try
        {
            if (RestartDelay > TimeSpan.Zero)
                await Task.Delay(RestartDelay, timeProvider).ConfigureAwait(false);
            lifetime.StopApplication();
        }
        catch (Exception exception)
        {
            RestartRequests.Remove(lifetime);
            logger.LogError(exception, "CameraAgent restart request could not stop the host.");
        }
    }

    /// <summary>
    /// Restart is offered only when something will start the process again: an explicit
    /// <c>CameraAgent:Restart:Supervised</c> setting wins; otherwise a .NET container image
    /// (<c>DOTNET_RUNNING_IN_CONTAINER=true</c>) is treated as supervised by its restart policy.
    /// </summary>
    internal static bool IsSupervised(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[SupervisedRestartKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return bool.TryParse(configured, out var supervised) && supervised;
        return string.Equals(configuration["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase);
    }
}
