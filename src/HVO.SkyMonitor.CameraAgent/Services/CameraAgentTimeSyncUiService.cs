using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The Time panel on System control: the latest clock measurement, an on-demand check, and the time server list in the
/// operator settings file. Configured server names are shown to readers; the addresses they resolve to never are.
/// </summary>
internal interface ICameraAgentTimeSyncUiService
{
    ValueTask<OperatorUiResult<GpsReceiverSettings>> SaveGpsAsync(
        GpsReceiverSettings receiver, string expectedVersion, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<TimeSyncView>> GetAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<TimeSyncCheckView>> CheckNowAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<TimeSyncServersSetting>> SaveServersAsync(
        IReadOnlyList<string> servers,
        string expectedVersion,
        CancellationToken cancellationToken);
}

/// <param name="AgentUtc">This agent's clock when the view was read.</param>
/// <param name="SiteTimeZoneId">The active deployment location's time zone, or null when none is active.</param>
/// <param name="SiteUtcOffset">That zone's offset from UTC at <paramref name="AgentUtc"/>.</param>
/// <param name="StatusDetail">Why the clock has that status, in the words the health check uses.</param>
/// <param name="IgnoredEntries">Configured entries not queried because they are invalid or beyond the limit.</param>
internal sealed record TimeSyncView(
    DateTimeOffset AgentUtc,
    string? SiteTimeZoneId,
    TimeSpan? SiteUtcOffset,
    ClockSyncStatus Status,
    string StatusDetail,
    TimeSpan Tolerance,
    TimeSpan Interval,
    DateTimeOffset? MeasuredUtc,
    TimeSyncServerView? Selected,
    IReadOnlyList<TimeSyncServerView> Servers,
    int IgnoredEntries,
    KernelClockState Kernel,
    TimeSyncServersSetting Setting,
    bool CanChange)
{
    internal GpsReceiverSettings? Receiver { get; init; }
    internal GpsTimeStatus? Gps { get; init; }
    internal bool GpsOverridden { get; init; }
}

internal sealed record TimeSyncServerView(
    string Server,
    SntpFailure? Failure,
    TimeSpan? Offset,
    TimeSpan? RoundTrip,
    int? Stratum,
    bool Selected);

/// <param name="Servers">
/// The valid servers the agent queries, the defaults when none is configured. Empty when every configured entry is
/// invalid: the agent then queries nothing rather than falling back to a server the operator did not choose.
/// </param>
/// <param name="UsingDefault">Whether no server is configured, so the defaults are in use.</param>
/// <param name="Version">The settings file version a save must name, or null when no settings file is loaded.</param>
/// <param name="Overridden">Whether another configuration source supplies the list, so a saved one has no effect.</param>
/// <param name="Unreadable">Whether the settings file is not valid settings JSON, so nothing can be saved to it.</param>
internal sealed record TimeSyncServersSetting(
    IReadOnlyList<string> Servers,
    bool UsingDefault,
    string? Version,
    bool Overridden,
    bool Unreadable = false);

internal sealed record TimeSyncCheckView(ClockCheckOutcome Outcome, DateTimeOffset? RetryAfterUtc);

internal sealed class CameraAgentTimeSyncUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    IClockSyncMonitor monitor,
    TimeProvider timeProvider,
    ILogger<CameraAgentTimeSyncUiService> logger,
    IDeploymentLocationStore? deploymentLocationStore = null,
    OperatorSettingsFile? settingsFile = null) : ICameraAgentTimeSyncUiService
{
    private const string SettingsConflictMessage =
        "The settings file changed since this page was read. Refresh before retrying.";
    private const string SettingsUnreadableMessage =
        "The settings file is not valid settings JSON. Correct it, then refresh before retrying.";
    private const string NoSettingsFileMessage =
        "This host loads no operator settings file, so this setting cannot be saved here.";

    private static readonly string[] ServerKeys = [TimeSyncSettings.ServersKey, TimeSyncSettings.ServersKey + ":0"];

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<TimeSyncView>> GetAsync(CancellationToken cancellationToken)
    {
        if (await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
                .ConfigureAwait(false) is null)
        {
            return OperatorUiResult<TimeSyncView>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            var canChange = await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
                .ConfigureAwait(false) is not null;
            var file = settingsFile is null
                ? null
                : await settingsFile.ReadAsync(cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<TimeSyncView>.Success(Project(file, canChange));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Time sync projection failed with {ExceptionType}.", exception.GetType().Name);
            return OperatorUiResult<TimeSyncView>.Failure(
                OperatorUiResultKind.Unavailable, "The clock state could not be read.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<TimeSyncCheckView>> CheckNowAsync(CancellationToken cancellationToken)
    {
        if (await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
                .ConfigureAwait(false) is null)
        {
            return OperatorUiResult<TimeSyncCheckView>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        if (!monitor.Settings.Enabled)
        {
            return OperatorUiResult<TimeSyncCheckView>.Failure(
                OperatorUiResultKind.Invalid, "Clock checking is turned off in this agent's settings.");
        }
        try
        {
            var result = await monitor.CheckNowAsync(cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<TimeSyncCheckView>.Success(new TimeSyncCheckView(result.Outcome, result.RetryAfterUtc));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("On-demand clock check failed with {ExceptionType}.", exception.GetType().Name);
            return OperatorUiResult<TimeSyncCheckView>.Failure(
                OperatorUiResultKind.Unavailable, "The clock check could not be run.");
        }
    }

    private const string GpsPrefix = GpsReceiverSettings.SectionKey + ":";
    private static readonly string[] GpsKeys = [GpsPrefix + "Enabled", GpsPrefix + "Device", GpsPrefix + "BaudRate", GpsPrefix + "UsePps"];

    public ValueTask<OperatorUiResult<GpsReceiverSettings>> SaveGpsAsync(
        GpsReceiverSettings receiver, string expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        return SaveSettingAsync(expectedVersion, () =>
            !monitor.GpsReaderRegistered ? "GPS reader support is unavailable on this host." :
            settingsFile?.FindOverriddenKeys(GpsKeys).Count > 0 ? "Another configuration source sets GPS receiver settings." :
            receiver.Validate(),
            () => new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                [GpsPrefix + "Enabled"] = receiver.Enabled,
                [GpsPrefix + "Device"] = receiver.Device,
                [GpsPrefix + "BaudRate"] = receiver.BaudRate,
                [GpsPrefix + "UsePps"] = receiver.UsePps,
            },
            _ => receiver, cancellationToken);
    }

    public ValueTask<OperatorUiResult<TimeSyncServersSetting>> SaveServersAsync(
        IReadOnlyList<string> servers, string expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return SaveSettingAsync(expectedVersion, () => ValidateServers(servers),
            () => new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                [TimeSyncSettings.ServersKey] = servers.Count == 0 ? null : JsonValue.Create(string.Join(", ", servers)),
            },
            version => ReadSetting(version, servers.Count == 0 ? TimeSyncSettings.DefaultServers : servers, servers.Count == 0),
            cancellationToken);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<T>> SaveSettingAsync<T>(
        string expectedVersion, Func<string?> validate, Func<IReadOnlyDictionary<string, JsonNode?>> changes,
        Func<string, T> project, CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");
        if (settingsFile is null)
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, NoSettingsFileMessage);
        if (string.IsNullOrWhiteSpace(expectedVersion))
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Conflict, SettingsConflictMessage);
        try
        {
            if (validate() is { } invalid)
                return OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, invalid);
            var result = await settingsFile.WriteAsync(expectedVersion, changes(), actor, cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                OperatorSettingsWriteStatus.Conflict => OperatorUiResult<T>.Failure(OperatorUiResultKind.Conflict, SettingsConflictMessage),
                OperatorSettingsWriteStatus.Unreadable => OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, SettingsUnreadableMessage),
                _ => OperatorUiResult<T>.Success(project(result.Snapshot.Version)),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent time settings change failed.");
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, "The time settings change could not be completed.");
        }
    }

    /// <summary>The operator-facing reason a server list cannot be saved, or null when it can.</summary>
    internal static string? ValidateServers(IReadOnlyList<string> servers)
    {
        if (servers.Count > TimeSyncSettings.MaximumServers)
        {
            return $"List at most {TimeSyncSettings.MaximumServers} time servers.";
        }
        foreach (var server in servers)
        {
            if (!TimeSyncSettings.TryParseServer(server, out _, out _))
            {
                return "Each time server must be a host name or IP address, optionally with :port, such as "
                    + "pool.ntp.org or 192.168.1.10:123. Addresses with a scheme, path or credentials are not accepted.";
            }
        }
        return servers.Distinct(StringComparer.OrdinalIgnoreCase).Count() == servers.Count
            ? null
            : "Each time server can be listed only once.";
    }

    private TimeSyncView Project(OperatorSettingsSnapshot? file, bool canChange)
    {
        var settings = monitor.Settings;
        var snapshot = monitor.Latest;
        var assessment = ClockAssessment.Evaluate(settings, snapshot);
        var now = timeProvider.GetUtcNow();
        var (zoneId, zoneOffset) = SiteZone(now);
        var measured = settings.Enabled && snapshot is { Enabled: true } ? snapshot : null;
        var servers = measured?.Servers
            .Select(result => new TimeSyncServerView(
                result.Server,
                result.Failure,
                result.Offset,
                result.RoundTrip,
                result.Stratum,
                ReferenceEquals(result, measured.Selected)))
            .ToArray() ?? [];
        var configured = settings.EffectiveServers;
        var valid = configured
            .Where(static entry => TimeSyncSettings.TryParseServer(entry, out _, out _))
            .Take(TimeSyncSettings.MaximumServers)
            .ToArray();
        return new TimeSyncView(
            now,
            zoneId,
            zoneOffset,
            assessment.Status,
            assessment.Description,
            settings.Tolerance,
            settings.Interval,
            measured is { Selected: null, Gps: { CanVerifyClock: true } gps } ? gps.PpsMeasuredUtc : measured?.MeasuredUtc,
            servers.FirstOrDefault(static server => server.Selected),
            servers,
            configured.Count - valid.Length,
            snapshot?.Kernel ?? KernelClockState.Unknown,
            ReadSetting(file?.Version, valid, settings.Servers.Count == 0, file?.Problem is not null),
            canChange)
        {
            Receiver = monitor.GpsReaderRegistered ? settings.Gps : null,
            GpsOverridden = settingsFile?.FindOverriddenKeys(GpsKeys).Count > 0,
            Gps = measured?.Gps,
        };
    }

    private TimeSyncServersSetting ReadSetting(
        string? version,
        IReadOnlyList<string> servers,
        bool usingDefault,
        bool unreadable = false)
        => new(servers, usingDefault, version, settingsFile?.FindOverriddenKeys(ServerKeys).Count > 0, unreadable);

    private (string? ZoneId, TimeSpan? Offset) SiteZone(DateTimeOffset now)
    {
        if (deploymentLocationStore?.Active?.TimeZoneId is not { Length: > 0 } zoneId)
        {
            return (null, null);
        }
        return TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone)
            ? (zoneId, zone.GetUtcOffset(now))
            : (zoneId, null);
    }

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy)
            .ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }
}
