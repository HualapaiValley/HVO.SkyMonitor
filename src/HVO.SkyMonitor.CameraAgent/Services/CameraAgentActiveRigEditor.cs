using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Settings;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Stages an edit of the active rig as one restart-bound change: it saves the edited equipment revisions, composes a
/// rig revision from them, previews it and stages it. The caller authorizes the actor; the Camera &amp; rig page and the
/// settings API both stage through here, so a change is recorded the same way whichever one makes it.
/// </summary>
internal sealed partial class CameraAgentActiveRigEditor(
    SqliteNamedRigProfileStore store,
    CaptureScheduleRuntimeCoordinator runtime,
    ILogger logger)
{
    private const string VirtualSkyModule = "VirtualSky";
    private static readonly JsonSerializerOptions EquipmentJson = new(JsonSerializerDefaults.Web);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A partially applied edit must still report what it durably recorded.")]
    public async Task<ActiveRigEditOutcome> ApplyAsync(ActiveRigEditRequest request, string actor,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        var camera = await EditedCameraAsync(active, request.SkyLighting, token).ConfigureAwait(false);
        var cameraChanged = camera is not null;
        if (!opticsChanged && !mountChanged && !cameraChanged)
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
        if (cameraChanged) notDone.Add("Camera was not saved.");
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
            var cameraRevisionId = active.CameraRevisionId;
            if (camera is not null)
            {
                cameraRevisionId = await SaveEditedEquipmentAsync("camera", active.CameraRevisionId, camera, recorded, token)
                    .ConfigureAwait(false);
                completed++;
            }
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
            var composed = await store.ComposeAsync(active.ProfileId, cameraRevisionId, opticsRevisionId,
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

    /// <summary>
    /// The sky lighting of a rig's camera revision, or null when the camera is not a virtual sky camera.
    /// </summary>
    public async Task<CameraAgentSkyLighting?> ReadSkyLightingAsync(NamedRigRevision revision, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.Module.Type != VirtualSkyModule) return null;
        var detail = await store.GetEquipmentAsync(revision.CameraRevisionId, token).ConfigureAwait(false);
        var projected = JsonSerializer.SerializeToElement(detail.Definition, EquipmentJson);
        return new CameraAgentSkyLighting(
            ProjectedSkyLighting(projected),
            projected.TryGetProperty("fixedSkyBackground", out var fixedBackground) &&
            fixedBackground.ValueKind == JsonValueKind.True);
    }

    private static ActiveRigEditOutcome Stopped(string failure, IEnumerable<string> recorded, IEnumerable<string> notDone,
        string? composedId = null)
        => new(ActiveRigEditStatus.Failed, recorded.ToArray(), notDone.ToArray(), failure, composedId);

    /// <summary>
    /// The camera definition to save when the edit changes the sky lighting, or null when it leaves the camera as it
    /// is. Only the module type, sensor and readout are sent; the store restores every module option from the basis and
    /// changes only the sky lighting.
    /// </summary>
    private async Task<JsonElement?> EditedCameraAsync(NamedRigRevision active, string? skyLighting, CancellationToken token)
    {
        if (skyLighting is null) return null;
        if (active.Module.Type != VirtualSkyModule)
            throw new ArgumentException("Sky lighting applies only to a virtual sky camera.", nameof(skyLighting));
        if (CameraAgentSettingsCatalogue.SkyLighting.Check(skyLighting) is not null)
            throw new ArgumentException("The sky lighting choice is not supported.", nameof(skyLighting));
        var detail = await store.GetEquipmentAsync(active.CameraRevisionId, token).ConfigureAwait(false);
        var projected = JsonSerializer.SerializeToElement(detail.Definition, EquipmentJson);
        if (string.Equals(ProjectedSkyLighting(projected), skyLighting, StringComparison.Ordinal)) return null;
        var camera = projected.Deserialize<NamedCameraEquipment>(EquipmentJson)
            ?? throw new InvalidDataException("The active camera revision is invalid.");
        return JsonSerializer.SerializeToElement(
            new
            {
                module = new { type = active.Module.Type },
                sensor = camera.Sensor,
                readout = camera.Readout,
                illuminationMode = skyLighting
            },
            EquipmentJson);
    }

    private static string? ProjectedSkyLighting(JsonElement definition)
        => definition.TryGetProperty("illuminationMode", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

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
    /// revisions are duplicated under a new name instead, so no installed definition is ever changed. A camera copy
    /// names its basis, so the store restores the basis module options into it.
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
        var basis = kind == "camera" ? detail.RevisionId : null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = ProposeCopyName(baseName, taken);
            try
            {
                var created = await store.SaveEquipmentAsync(null, kind, name, json, token, basis).ConfigureAwait(false);
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

    [GeneratedRegex(@" copy( [0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CopySuffix();
}

/// <summary>A virtual sky camera revision's sky lighting.</summary>
/// <param name="Choice">The chosen sky lighting, or null when the revision's choice cannot be read.</param>
/// <param name="FixedSkyBackground">
/// True when the camera sets a fixed sky background, which overrides the choice in the rendered image.
/// </param>
internal sealed record CameraAgentSkyLighting(string? Choice, bool FixedSkyBackground);
