using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Settings;

/// <summary>The disposition of one settings write.</summary>
internal enum CameraAgentSettingsWriteOutcome
{
    Applied,
    Unchanged,
    PendingRestart,

    /// <summary>A value or the request failed validation; nothing changed.</summary>
    Invalid,

    /// <summary>The group changed since <c>expectedVersion</c> was read, or its state refused the change.</summary>
    Conflict,

    /// <summary>A setting cannot be changed through the settings API now; nothing changed.</summary>
    ReadOnly,

    /// <summary>The group does not exist.</summary>
    NotFound,

    /// <summary>The group's store could not be read or written.</summary>
    Unavailable
}

/// <param name="Group">The group as it is after the write, when it could be read.</param>
/// <param name="Problems">The rule each rejected setting breaks, keyed by setting key.</param>
/// <param name="Recorded">Durable records a multi-step write made, in order.</param>
/// <param name="NotDone">Steps a multi-step write did not reach.</param>
/// <param name="Replayed">The durable store confirmed an existing command; this write changed nothing.</param>
internal sealed record CameraAgentSettingsWriteResult(
    CameraAgentSettingsWriteOutcome Outcome,
    CameraAgentSettingsGroupState? Group,
    string? Message = null,
    IReadOnlyDictionary<string, string>? Problems = null,
    IReadOnlyList<string>? Recorded = null,
    IReadOnlyList<string>? NotDone = null,
    bool Replayed = false);

/// <summary>
/// Reads every catalogue setting with where its value comes from, and writes one group at a time to the store that
/// keeps it, provided the group is still at the version the caller read. Callers authorize the actor first; this
/// service validates every value against the catalogue before any store sees it.
/// </summary>
internal sealed partial class CameraAgentSettingsService(
    CameraAgentSettingSources sources,
    ISceneObjectBound sceneObjectBound,
    CurrentSkyRefreshSetting currentSkyRefresh,
    ISiteProfileStore siteProfileStore,
    IDeploymentLocationStore deploymentLocationStore,
    SqliteNamedRigProfileStore rigStore,
    CaptureScheduleRuntimeCoordinator scheduleRuntime,
    ICameraAgentConfigurationAccessor configurationAccessor,
    CameraAgentOperatorTelemetry telemetry,
    ILogger<CameraAgentSettingsService> logger,
    OperatorSettingsFile? settingsFile = null)
{
    private const string VirtualSkyModule = "VirtualSky";
    private const string NoSettingsFileReason =
        "This host loads no operator settings file, so settings are changed in its own configuration instead.";
    private const string UnavailableVersion = "unavailable";

    /// <summary>Test seam after advisory checks, before the store decides a concurrent write atomically.</summary>
    internal Func<string, CancellationToken, Task>? BeforeStoreWriteAsync { get; init; }

    public async Task<CameraAgentSettingsResponse> GetAsync(CancellationToken cancellationToken)
    {
        var groups = new List<CameraAgentSettingsGroupState>(CameraAgentSettingsCatalogue.Groups.Count);
        foreach (var group in CameraAgentSettingsCatalogue.Groups)
        {
            groups.Add(await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false));
        }

        return new(CameraAgentSettingsContract.SchemaVersion, groups);
    }

    /// <summary>One group as it is now, or null when no group has that name.</summary>
    public async Task<CameraAgentSettingsGroupState?> GetGroupAsync(string group, CancellationToken cancellationToken)
        => CameraAgentSettingsCatalogue.FindGroup(group) is { } found
            ? await ReadGroupAsync(found, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Validates the request against the catalogue and, when every value passes, writes the group to its store.
    /// </summary>
    /// <param name="idempotencyKey">
    /// The client's command key for a store that records one; a fresh key is used when the client sends none.
    /// </param>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A settings write logs internal failures and returns a fixed unavailable outcome.")]
    public async Task<CameraAgentSettingsWriteResult> WriteAsync(
        string group,
        CameraAgentSettingsUpdateRequest request,
        string actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (CameraAgentSettingsCatalogue.FindGroup(group) is not { } found)
        {
            return new(CameraAgentSettingsWriteOutcome.NotFound, null, $"No settings group is named '{group}'.");
        }

        var current = await ReadGroupAsync(found, cancellationToken).ConfigureAwait(false);
        if (current.Settings.Any(setting => setting.Source == CameraAgentSettingSource.Unavailable))
        {
            return new(CameraAgentSettingsWriteOutcome.Unavailable, current, current.ReadOnlyReason);
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(request.ExpectedVersion) || request.Values is null || request.Values.Count == 0 ||
            reason is { Length: > CameraAgentSettingsContract.MaximumReasonLength } ||
            reason?.Any(char.IsControl) == true ||
            idempotencyKey is { Length: 0 or > ManualDeploymentLocationContract.MaximumIdempotencyKeyLength })
        {
            return new(
                CameraAgentSettingsWriteOutcome.Invalid,
                current,
                "A settings change needs the group's expectedVersion and at least one value; a reason is at most " +
                $"{CameraAgentSettingsContract.MaximumReasonLength} characters without control characters.");
        }

        if (found.Name == CameraAgentSettingsCatalogue.ScheduleGroup && idempotencyKey is not null &&
            await TryReplayScheduleAsync(found, current, request, actor, idempotencyKey, reason, cancellationToken)
                .ConfigureAwait(false) is { } replay)
        {
            if (replay.Replayed) LogReplayed(logger, found.Name, actor);
            return replay;
        }

        var (values, problems) = Parse(found, current, request.Values);
        if (problems.Count > 0)
        {
            return new(CameraAgentSettingsWriteOutcome.Invalid, current, "One or more values are invalid.", problems);
        }

        var readOnly = values.Keys
            .Select(key => current.Settings.First(setting => setting.Key == key))
            .Where(setting => !setting.CanWrite)
            .ToDictionary(setting => setting.Key, setting => setting.ReadOnlyReason ?? "It cannot be changed here.");
        if (!current.CanWrite || readOnly.Count > 0)
        {
            return new(
                CameraAgentSettingsWriteOutcome.ReadOnly,
                current,
                current.ReadOnlyReason ?? "One or more settings cannot be changed through the settings API now.",
                readOnly);
        }

        if (!string.Equals(request.ExpectedVersion, current.Version, StringComparison.Ordinal) &&
            !(idempotencyKey is not null && found.Name is
                CameraAgentSettingsCatalogue.LocationGroup or CameraAgentSettingsCatalogue.ScheduleGroup))
        {
            return Conflict(current);
        }

        var command = idempotencyKey ?? Guid.NewGuid().ToString("N");
        CameraAgentSettingsWriteResult result;
        try
        {
            result = found.Name switch
            {
                CameraAgentSettingsCatalogue.SiteGroup => await WriteSiteAsync(found, values, current.Version, actor,
                    cancellationToken).ConfigureAwait(false),
                CameraAgentSettingsCatalogue.SkyMapGroup => await WriteFileIntegerAsync(found,
                    CameraAgentSettingsCatalogue.MaximumSceneObjects, values, current.Version, actor, cancellationToken)
                    .ConfigureAwait(false),
                CameraAgentSettingsCatalogue.CurrentSkyGroup => await WriteFileIntegerAsync(found,
                    CameraAgentSettingsCatalogue.MinimumRefreshInterval, values, current.Version, actor, cancellationToken)
                    .ConfigureAwait(false),
                CameraAgentSettingsCatalogue.LocationGroup => await WriteLocationAsync(found, values, request.ExpectedVersion!,
                    actor, command, reason, cancellationToken).ConfigureAwait(false),
                CameraAgentSettingsCatalogue.CameraGroup => await WriteCameraAsync(found, values, current.Version, actor,
                    cancellationToken).ConfigureAwait(false),
                CameraAgentSettingsCatalogue.ScheduleGroup => await WriteScheduleAsync(found, values, request.ExpectedVersion!,
                    actor, command, reason, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"The settings group {found.Name} has no writer.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogWriteFailed(logger, exception, found.Name);
            return new(
                CameraAgentSettingsWriteOutcome.Unavailable,
                await TryReadGroupAsync(found, cancellationToken).ConfigureAwait(false),
                "The settings change could not be completed.");
        }

        if (result.Replayed)
        {
            LogReplayed(logger, found.Name, actor);
        }
        else if (result.Outcome is CameraAgentSettingsWriteOutcome.Applied or CameraAgentSettingsWriteOutcome.PendingRestart)
        {
            LogWritten(logger, found.Name, string.Join(", ", values.Keys), result.Outcome, actor);
        }

        return result;
    }

    private static (Dictionary<string, object?> Values, Dictionary<string, string> Problems) Parse(
        CameraAgentSettingsGroup group,
        CameraAgentSettingsGroupState current,
        IReadOnlyDictionary<string, JsonElement> sent)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, element) in sent)
        {
            var definition = group.Settings.FirstOrDefault(setting => string.Equals(setting.Key, key, StringComparison.Ordinal));
            if (definition is null)
            {
                problems[key] = $"The {group.Name} group has no setting with this key.";
                continue;
            }

            var allowed = current.Settings.First(setting => setting.Key == key).AllowedValues;
            if (definition.TryParse(element, allowed, out var value, out var problem))
            {
                values[key] = value;
            }
            else
            {
                problems[key] = problem!;
            }
        }

        return (values, problems);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A group whose store fails is reported as unavailable rather than failing every other group.")]
    private async Task<CameraAgentSettingsGroupState> ReadGroupAsync(
        CameraAgentSettingsGroup group,
        CancellationToken cancellationToken)
    {
        try
        {
            return group.Name switch
            {
                CameraAgentSettingsCatalogue.SiteGroup => await ReadSiteAsync(group, cancellationToken).ConfigureAwait(false),
                CameraAgentSettingsCatalogue.SkyMapGroup => await ReadFileIntegerAsync(group,
                    CameraAgentSettingsCatalogue.MaximumSceneObjects, sceneObjectBound.Current, cancellationToken)
                    .ConfigureAwait(false),
                CameraAgentSettingsCatalogue.CurrentSkyGroup => await ReadFileIntegerAsync(group,
                    CameraAgentSettingsCatalogue.MinimumRefreshInterval, currentSkyRefresh.Seconds, cancellationToken)
                    .ConfigureAwait(false),
                CameraAgentSettingsCatalogue.LocationGroup => ReadLocation(group),
                CameraAgentSettingsCatalogue.CameraGroup => await ReadCameraAsync(group, cancellationToken).ConfigureAwait(false),
                CameraAgentSettingsCatalogue.ScheduleGroup => await ReadScheduleAsync(group, cancellationToken).ConfigureAwait(false),
                _ => Unavailable(group, "This settings group has no reader.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogReadFailed(logger, exception, group.Name);
            return Unavailable(group, "This settings group could not be read.");
        }
    }

    private async Task<CameraAgentSettingsGroupState?> TryReadGroupAsync(
        CameraAgentSettingsGroup group,
        CancellationToken cancellationToken)
        => await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);

    private static CameraAgentSettingsGroupState Unavailable(CameraAgentSettingsGroup group, string reason)
        => new(
            group.Name,
            UnavailableVersion,
            false,
            reason,
            [.. group.Settings.Select(setting => new CameraAgentSettingState(
                setting.Key, null, false, CameraAgentSettingSource.Unavailable, null, false, reason, null, null, null))]);

    private static CameraAgentSettingsWriteResult Conflict(CameraAgentSettingsGroupState current)
        => new(
            CameraAgentSettingsWriteOutcome.Conflict,
            current,
            "The settings changed since they were read. Read them again before retrying.");

    // Site profile: the settings file, through the site profile store, which keeps the section's own rules.

    private async Task<CameraAgentSettingsGroupState> ReadSiteAsync(
        CameraAgentSettingsGroup group,
        CancellationToken cancellationToken)
    {
        var state = await siteProfileStore.GetAsync(cancellationToken).ConfigureAwait(false);
        var canWrite = state.Problem is null && state.SettingsFilePath is not null;
        var reason = canWrite ? null : state.Problem ?? NoSettingsFileReason;
        var profile = state.Profile;
        return new(group.Name, state.Version, canWrite, reason,
        [
            FileValue(CameraAgentSettingsCatalogue.ObservatoryName, profile.ObservatoryName),
            profile.CameraName is null && state.EffectiveCameraName is { } installed
                ? new(CameraAgentSettingsCatalogue.CameraName.Key, CameraAgentSettingDefinition.ToJson(installed), true,
                    CameraAgentSettingSource.Installation, "The name the installation configured.", canWrite, reason,
                    null, null, null)
                : FileValue(CameraAgentSettingsCatalogue.CameraName, profile.CameraName),
            FileValue(CameraAgentSettingsCatalogue.OwnerName, profile.OwnerName),
            FileValue(CameraAgentSettingsCatalogue.OwnerContact, profile.OwnerContact)
        ]);

        CameraAgentSettingState FileValue(CameraAgentSettingDefinition definition, string? value)
            => new(
                definition.Key,
                CameraAgentSettingDefinition.ToJson(value),
                value is not null,
                value is null ? CameraAgentSettingSource.Default : CameraAgentSettingSource.SettingsFile,
                null,
                canWrite,
                reason,
                null,
                null,
                null);
    }

    private async Task<CameraAgentSettingsWriteResult> WriteSiteAsync(
        CameraAgentSettingsGroup group,
        Dictionary<string, object?> values,
        string expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var current = (await siteProfileStore.GetAsync(cancellationToken).ConfigureAwait(false)).Profile;
        var profile = new SiteProfileValues(
            Text(CameraAgentSettingsCatalogue.ObservatoryName, current.ObservatoryName),
            Text(CameraAgentSettingsCatalogue.CameraName, current.CameraName),
            Text(CameraAgentSettingsCatalogue.OwnerName, current.OwnerName),
            Text(CameraAgentSettingsCatalogue.OwnerContact, current.OwnerContact));
        var result = await siteProfileStore.ApplyAsync(new SiteProfileRequest(profile, expectedVersion, actor),
            cancellationToken).ConfigureAwait(false);
        var after = await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            SiteProfileStatus.Applied => new(CameraAgentSettingsWriteOutcome.Applied, after),
            SiteProfileStatus.Unchanged => new(CameraAgentSettingsWriteOutcome.Unchanged, after),
            SiteProfileStatus.Conflict => Conflict(after),
            _ when result.ReasonCode is SiteProfileLimits.UnreadableReasonCode or SiteProfileLimits.UnavailableReasonCode
                => new(CameraAgentSettingsWriteOutcome.ReadOnly, after, after.ReadOnlyReason ?? NoSettingsFileReason),
            _ => new(CameraAgentSettingsWriteOutcome.Invalid, after, "The site profile change was rejected.")
        };

        string? Text(CameraAgentSettingDefinition definition, string? existing)
            => values.TryGetValue(definition.Key, out var value) ? (string?)value : existing;
    }

    // Sky map and Current Sky: one whole number in the settings file, read back through the live configuration so an
    // override is visible. The value in effect is what its consumer reads now.

    private async Task<CameraAgentSettingsGroupState> ReadFileIntegerAsync(
        CameraAgentSettingsGroup group,
        CameraAgentSettingDefinition definition,
        int effective,
        CancellationToken cancellationToken)
    {
        var snapshot = settingsFile is null ? null : await settingsFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        var source = sources.Find(definition.ConfigurationKey!);
        var canWrite = snapshot is { Problem: null } && !source.Overridden;
        var reason = snapshot is null ? NoSettingsFileReason
            : snapshot.Problem ?? (source.Overridden ? OverriddenReason(source.Source) : null);
        var configured = snapshot?.GetValue(definition.ConfigurationKey!);
        var unusable = source.Source switch
        {
            CameraAgentSettingSource.SettingsFile => configured is null ||
                !int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var saved) ||
                definition.Check(saved) is not null,
            // An object or array at the key gives the configuration no value there, so the default applies; it is
            // still a stored value the host cannot use.
            CameraAgentSettingSource.Default => configured is null && snapshot?.HasValue(definition.ConfigurationKey!) == true,
            _ => false
        };
        var problem = unusable
            ? string.Create(CultureInfo.InvariantCulture,
                $"The settings file value is not a whole number from {definition.Minimum:N0} to {definition.Maximum:N0}, ") +
              "so the default applies until it is corrected."
            : null;

        return new(group.Name, snapshot?.Version ?? UnavailableVersion, canWrite, reason,
        [
            new(definition.Key, CameraAgentSettingDefinition.ToJson(effective), source.Source != CameraAgentSettingSource.Default,
                source.Source, null, canWrite, reason, null, problem, null)
        ]);
    }

    private async Task<CameraAgentSettingsWriteResult> WriteFileIntegerAsync(
        CameraAgentSettingsGroup group,
        CameraAgentSettingDefinition definition,
        Dictionary<string, object?> values,
        string expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var number = (int?)values[definition.Key];
        var result = await settingsFile!.WriteAsync(
            expectedVersion,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                [definition.ConfigurationKey!] = number is { } value ? JsonValue.Create(value) : null
            },
            actor,
            cancellationToken).ConfigureAwait(false);
        var after = await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            OperatorSettingsWriteStatus.Applied => new(CameraAgentSettingsWriteOutcome.Applied, after),
            OperatorSettingsWriteStatus.Unchanged => new(CameraAgentSettingsWriteOutcome.Unchanged, after),
            OperatorSettingsWriteStatus.Conflict => Conflict(after),
            _ => new(CameraAgentSettingsWriteOutcome.ReadOnly, after, after.ReadOnlyReason ?? result.Snapshot.Problem)
        };
    }

    // Deployment location: the protected location history; a change is a new manual version for the next start.

    private CameraAgentSettingsGroupState ReadLocation(CameraAgentSettingsGroup group)
    {
        var manual = deploymentLocationStore.Manual;
        var active = deploymentLocationStore.Active;
        if (active is null)
        {
            return Unavailable(group, "The deployment location is not available yet.");
        }

        var canWrite = manual.Supported;
        var reason = canWrite ? null : "This CameraAgent's location owner does not accept local changes.";
        var pending = PendingCoordinates(manual);
        var detail = string.Create(CultureInfo.InvariantCulture, $"Version {active.Version} ({active.Source})");
        return new(group.Name, LocationVersion(manual), canWrite, reason,
        [
            Coordinate(CameraAgentSettingsCatalogue.Latitude, active.LatitudeDegrees, pending?.Latitude),
            Coordinate(CameraAgentSettingsCatalogue.Longitude, active.LongitudeDegrees, pending?.Longitude),
            Coordinate(CameraAgentSettingsCatalogue.Elevation, active.ElevationMeters, pending?.Elevation),
            new(CameraAgentSettingsCatalogue.TimeZone.Key, CameraAgentSettingDefinition.ToJson(active.TimeZoneId), true,
                CameraAgentSettingSource.DeploymentLocation, detail, canWrite, reason,
                CameraAgentSettingDefinition.ToJson(pending?.TimeZoneId), null, null)
        ]);

        CameraAgentSettingState Coordinate(CameraAgentSettingDefinition definition, double value, double? next)
            => new(definition.Key, CameraAgentSettingDefinition.ToJson(value), true,
                CameraAgentSettingSource.DeploymentLocation, detail, canWrite, reason,
                CameraAgentSettingDefinition.ToJson(next), null, null);
    }

    /// <summary>The manual coordinates recorded for the next start, when an entry is waiting for a restart.</summary>
    private static (double Latitude, double Longitude, double Elevation, string TimeZoneId)? PendingCoordinates(
        ManualDeploymentLocationState manual)
        => manual.Override is { PendingRestart: true } staged
            ? (staged.LatitudeDegrees, staged.LongitudeDegrees, staged.ElevationMeters, staged.TimeZoneId)
            : null;

    private static string LocationVersion(ManualDeploymentLocationState manual)
        => string.Create(CultureInfo.InvariantCulture, $"{manual.KnownVersion}.{manual.ManualSequence}");

    private async Task<CameraAgentSettingsWriteResult> WriteLocationAsync(
        CameraAgentSettingsGroup group,
        Dictionary<string, object?> values,
        string expectedVersion,
        string actor,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        var parts = expectedVersion.Split('.');
        if (parts.Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var knownVersion) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var manualSequence))
        {
            return Conflict(ReadLocation(group));
        }

        var manual = deploymentLocationStore.Manual;
        var active = deploymentLocationStore.Active!;
        // Values left out keep the coordinates that will govern the next start: the newest manual entry when there is
        // one, else the active version. A replay keeps its original resolved fields even after later writes.
        // The store checks replay content first and refuses fresh commands unless the caller's version is current.
        var replay = await deploymentLocationStore.FindManualCommandAsync(idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        var basis = manual.Override;
        if (BeforeStoreWriteAsync is { } beforeWrite)
        {
            await beforeWrite(group.Name, cancellationToken).ConfigureAwait(false);
        }
        var result = await deploymentLocationStore.ApplyManualAsync(
            new ManualDeploymentLocationRequest(
                Number(CameraAgentSettingsCatalogue.Latitude, replay?.LatitudeDegrees ?? basis?.LatitudeDegrees ?? active.LatitudeDegrees),
                Number(CameraAgentSettingsCatalogue.Longitude, replay?.LongitudeDegrees ?? basis?.LongitudeDegrees ?? active.LongitudeDegrees),
                Number(CameraAgentSettingsCatalogue.Elevation, replay?.ElevationMeters ?? basis?.ElevationMeters ?? active.ElevationMeters),
                values.TryGetValue(CameraAgentSettingsCatalogue.TimeZone.Key, out var zone)
                    ? (string)zone!
                    : replay?.TimeZoneId ?? basis?.TimeZoneId ?? active.TimeZoneId,
                knownVersion,
                manualSequence,
                idempotencyKey,
                actor,
                string.IsNullOrWhiteSpace(reason) ? null : reason),
            cancellationToken).ConfigureAwait(false);
        var after = ReadLocation(group);
        return result.Status switch
        {
            ManualDeploymentLocationStatus.Applied or ManualDeploymentLocationStatus.Replayed =>
                new(CameraAgentSettingsWriteOutcome.PendingRestart, after,
                    Replayed: result.Status == ManualDeploymentLocationStatus.Replayed),
            ManualDeploymentLocationStatus.Unchanged => new(CameraAgentSettingsWriteOutcome.Unchanged, after),
            ManualDeploymentLocationStatus.Conflict => Conflict(after),
            _ when result.FieldPath is { } field &&
                   CameraAgentSettingsCatalogue.Find(field) is { Group: CameraAgentSettingsCatalogue.LocationGroup } setting
                => new(CameraAgentSettingsWriteOutcome.Invalid, after, "The location change was rejected.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [setting.Key] = $"{setting.Title} was rejected by the deployment location store."
                    }),
            _ => new(CameraAgentSettingsWriteOutcome.Invalid, after, "The location change was rejected.")
        };

        double Number(CameraAgentSettingDefinition definition, double existing)
            => values.TryGetValue(definition.Key, out var value) ? (double)value! : existing;
    }

    // Camera: the active rig; a change saves a camera revision and stages a rig revision for the next start.

    private async Task<CameraAgentSettingsGroupState> ReadCameraAsync(
        CameraAgentSettingsGroup group,
        CancellationToken cancellationToken)
    {
        var catalog = await rigStore.GetAsync(cancellationToken).ConfigureAwait(false);
        var active = catalog.Revisions.FirstOrDefault(revision => revision.RevisionId == catalog.Selection.ActiveRevisionId);
        if (active is null)
        {
            return Unavailable(group, "No rig is active yet.");
        }

        var pending = catalog.Revisions.FirstOrDefault(revision => revision.RevisionId == catalog.Selection.PendingRevisionId);
        var editor = Editor();
        var lighting = await editor.ReadSkyLightingAsync(active, cancellationToken).ConfigureAwait(false);
        var pendingLighting = pending is null
            ? null
            : await editor.ReadSkyLightingAsync(pending, cancellationToken).ConfigureAwait(false);
        var reason = active.Module.Type != VirtualSkyModule
            ? "Sky lighting applies only to a virtual sky camera."
            : pending is not null
                ? "A rig change is awaiting restart. Restart or cancel it on Camera & rig before changing this."
                : null;
        string? choice = null;
        string? problem = null;
        if (lighting is not null)
        {
            choice = lighting.Choice;
            problem = choice is null
                ? "The sky lighting of the active camera revision cannot be read."
                : lighting.FixedSkyBackground
                    ? "This camera sets a fixed sky background (backgroundElectronsPerSecond), which overrides the " +
                      "sky lighting choice in the rendered image."
                    : null;
        }

        var version = string.Create(CultureInfo.InvariantCulture,
            $"{catalog.Selection.Version}.{catalog.Selection.ActiveRevisionId}");
        return new(group.Name, version, reason is null, reason,
        [
            new(CameraAgentSettingsCatalogue.SkyLighting.Key, CameraAgentSettingDefinition.ToJson(choice),
                choice is not null, CameraAgentSettingSource.RigRevision,
                string.Create(CultureInfo.InvariantCulture, $"Rig revision r{active.RevisionNumber}"),
                reason is null, reason, CameraAgentSettingDefinition.ToJson(pendingLighting?.Choice), problem, null)
        ]);
    }

    private async Task<CameraAgentSettingsWriteResult> WriteCameraAsync(
        CameraAgentSettingsGroup group,
        Dictionary<string, object?> values,
        string expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var parts = expectedVersion.Split('.', 2);
        if (parts.Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var selectionVersion))
        {
            return Conflict(await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false));
        }

        if (!await TryStartScheduleRuntimeAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(CameraAgentSettingsWriteOutcome.Unavailable,
                await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false),
                "The capture runtime has not started yet.");
        }

        var catalog = await rigStore.GetAsync(cancellationToken).ConfigureAwait(false);
        var active = catalog.Revisions.FirstOrDefault(revision => revision.RevisionId == parts[1]);
        if (active is null)
        {
            return Conflict(await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false));
        }

        // The rig the caller read keeps its optics and mount; the editor refuses the edit unless that rig and the
        // caller's selection version are still the active ones.
        var optics = active.Rig.Optics;
        var mount = active.Rig.Orientation;
        var outcome = await Editor().ApplyAsync(
            new ActiveRigEditRequest(
                active.RevisionId,
                selectionVersion,
                optics.HorizontalFlip,
                optics.FieldOfViewDegrees,
                optics.FocalLengthMillimeters,
                mount.BoresightAltitudeDegrees,
                mount.BoresightAzimuthDegrees,
                mount.RollAdjustmentDegrees,
                (string)values[CameraAgentSettingsCatalogue.SkyLighting.Key]!),
            actor,
            cancellationToken).ConfigureAwait(false);
        var after = await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            ActiveRigEditStatus.Staged => new(CameraAgentSettingsWriteOutcome.PendingRestart, after,
                Recorded: outcome.Recorded),
            ActiveRigEditStatus.NoChanges => new(CameraAgentSettingsWriteOutcome.Unchanged, after),
            _ => new(CameraAgentSettingsWriteOutcome.Conflict, after, outcome.Failure, Recorded: outcome.Recorded,
                NotDone: outcome.NotDone)
        };
    }

    private CameraAgentActiveRigEditor Editor() => new(rigStore, scheduleRuntime, logger);

    // Capture schedule: the schedule revision history; a change applies at the next capture boundary.

    private async Task<CameraAgentSettingsGroupState> ReadScheduleAsync(
        CameraAgentSettingsGroup group,
        CancellationToken cancellationToken)
    {
        if (!await TryStartScheduleRuntimeAsync(cancellationToken).ConfigureAwait(false))
        {
            return Unavailable(group, "The capture schedule has not started yet.");
        }

        var state = await scheduleRuntime.GetOperatorStateAsync(cancellationToken).ConfigureAwait(false);
        var active = state.ActiveRevision;
        var revisions = new[] { active }
            .Concat(state.PendingRevision is { } draft ? [draft] : [])
            .Concat(state.History)
            .DistinctBy(revision => revision.RevisionId)
            .OrderByDescending(revision => revision.RevisionNumber)
            .Select(revision => new CameraAgentSettingChoice(
                revision.RevisionId,
                RevisionLabel(revision, active, state.PendingRevision),
                string.Create(CultureInfo.InvariantCulture,
                    $"Saved {revision.CreatedUtc:yyyy-MM-dd HH:mm} UTC from {revision.Source}.")))
            .ToArray();
        return new(group.Name, state.StateVersion.ToString(CultureInfo.InvariantCulture), true, null,
        [
            new(CameraAgentSettingsCatalogue.ActiveScheduleRevision.Key, CameraAgentSettingDefinition.ToJson(active.RevisionId),
                true, CameraAgentSettingSource.ScheduleRevision,
                string.Create(CultureInfo.InvariantCulture, $"Revision {active.RevisionNumber}"), true, null,
                CameraAgentSettingDefinition.ToJson(state.PendingRevision?.RevisionId), null, revisions)
        ]);
    }

    private static string RevisionLabel(
        CaptureScheduleRevisionSnapshot revision,
        CaptureScheduleRevisionSnapshot active,
        CaptureScheduleRevisionSnapshot? pending)
    {
        var number = revision.RevisionNumber.ToString(CultureInfo.InvariantCulture);
        return revision.RevisionId == active.RevisionId ? $"Revision {number} (active)"
            : revision.RevisionId == pending?.RevisionId ? $"Revision {number} (saved draft)"
            : $"Revision {number}";
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A replay logs internal failures and returns a fixed unavailable outcome, as a fresh write does.")]
    private async Task<CameraAgentSettingsWriteResult?> TryReplayScheduleAsync(
        CameraAgentSettingsGroup group,
        CameraAgentSettingsGroupState current,
        CameraAgentSettingsUpdateRequest request,
        string actor,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        // A retained command can outlive the current enum choices. Only an exact durable match may skip that list.
        if (request.Values!.Count != 1 ||
            !request.Values.TryGetValue(CameraAgentSettingsCatalogue.ActiveScheduleRevision.Key, out var element) ||
            element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: > 0 and <= 128 } target ||
            string.IsNullOrWhiteSpace(target) ||
            !long.TryParse(request.ExpectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return null;
        }
        try
        {
            return await scheduleRuntime.TryReplayRevisionAsync(target, idempotencyKey, version, actor, reason,
                    cancellationToken).ConfigureAwait(false) is null
                ? null
                : new(CameraAgentSettingsWriteOutcome.Applied,
                    await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false), Replayed: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CaptureScheduleStoreConflictException)
        {
            return Conflict(current);
        }
        catch (Exception exception)
        {
            LogWriteFailed(logger, exception, group.Name);
            return new(CameraAgentSettingsWriteOutcome.Unavailable, current,
                "The settings change could not be completed.");
        }
    }

    private async Task<CameraAgentSettingsWriteResult> WriteScheduleAsync(
        CameraAgentSettingsGroup group,
        Dictionary<string, object?> values,
        string expectedVersion,
        string actor,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        var target = (string)values[CameraAgentSettingsCatalogue.ActiveScheduleRevision.Key]!;
        if (!long.TryParse(expectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return Conflict(await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false));
        }
        var state = await scheduleRuntime.GetOperatorStateAsync(cancellationToken).ConfigureAwait(false);
        if (version != state.StateVersion)
        {
            return Conflict(await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false));
        }
        if (string.Equals(target, state.ActiveRevision.RevisionId, StringComparison.Ordinal))
        {
            return new(CameraAgentSettingsWriteOutcome.Unchanged, await ReadGroupAsync(group, cancellationToken)
                .ConfigureAwait(false));
        }

        var revision = state.History.FirstOrDefault(item => item.RevisionId == target)
            ?? (state.PendingRevision?.RevisionId == target ? state.PendingRevision : null);
        var rollback = revision is not null && revision.RevisionNumber < state.ActiveRevision.RevisionNumber;
        var action = rollback ? "rollback" : "activate";
        using var activity = telemetry.StartPlanMutation(action);
        CameraAgentSettingsWriteResult result;
        try
        {
            if (BeforeStoreWriteAsync is { } beforeWrite)
            {
                await beforeWrite(group.Name, cancellationToken).ConfigureAwait(false);
            }
            var mutation = rollback
                ? await scheduleRuntime.RollbackWithDispositionAsync(target, idempotencyKey, version, actor, reason, cancellationToken)
                    .ConfigureAwait(false)
                : await scheduleRuntime.ActivateWithDispositionAsync(target, idempotencyKey, version, actor, reason, cancellationToken)
                    .ConfigureAwait(false);
            result = new(CameraAgentSettingsWriteOutcome.Applied,
                await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false), Replayed: mutation.Replayed);
        }
        catch (CaptureScheduleStoreConflictException)
        {
            result = Conflict(await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or
                                              CaptureProfileCompatibilityException)
        {
            result = new(CameraAgentSettingsWriteOutcome.Invalid,
                await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false),
                "The schedule revision cannot be applied on this CameraAgent.");
        }

        var outcome = result.Outcome == CameraAgentSettingsWriteOutcome.Applied ? "applied" : "failed";
        activity?.SetTag("outcome", outcome);
        telemetry.RecordPlanMutation(action, outcome);
        return result;
    }

    /// <summary>
    /// Starts the schedule runtime from the loaded camera configuration when it has not started, as the schedule
    /// endpoints do; false when the configuration has not loaded yet, rather than waiting for it.
    /// </summary>
    private async Task<bool> TryStartScheduleRuntimeAsync(CancellationToken cancellationToken)
    {
        if (scheduleRuntime.Snapshot is not null)
        {
            return true;
        }

        if (!configurationAccessor.IsConfigured)
        {
            return false;
        }

        var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        _ = await scheduleRuntime.InitializeAsync(configuration, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string OverriddenReason(CameraAgentSettingSource source)
    {
        var where = source switch
        {
            CameraAgentSettingSource.Environment => "An environment variable",
            CameraAgentSettingSource.SecretsDirectory => "A file in the installer's secrets directory",
            CameraAgentSettingSource.CommandLine => "The command line",
            _ => "Another configuration source"
        };
        return $"{where} sets this, which takes precedence over the settings file; change or remove it there.";
    }

    [LoggerMessage(7620, LogLevel.Information, "Settings group {Group} changed by {Actor}: {Keys} ({Outcome})")]
    private static partial void LogWritten(ILogger logger, string group, string keys,
        CameraAgentSettingsWriteOutcome outcome, string actor);

    [LoggerMessage(7623, LogLevel.Information, "Settings group {Group} write replayed for {Actor}; no change")]
    private static partial void LogReplayed(ILogger logger, string group, string actor);

    [LoggerMessage(7621, LogLevel.Warning, "Settings group {Group} could not be read")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, string group);

    [LoggerMessage(7622, LogLevel.Warning, "Settings group {Group} could not be changed")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, string group);
}
