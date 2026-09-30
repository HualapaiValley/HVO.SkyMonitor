using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentSkyMapUiService
{
    ValueTask<OperatorUiResult<CameraAgentSkyMapProjectionResult>> GetSkyMapAsync(
        DateTimeOffset? atUtc,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualDeploymentLocationState>> GetManualLocationAsync(
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualDeploymentLocationResult>> ApplyManualLocationAsync(
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters,
        string timeZoneId,
        long expectedVersion,
        long expectedManualSequence,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>The site profile, the LogicHost assignment as this camera last heard it, and the map settings.</summary>
    ValueTask<OperatorUiResult<CameraAgentSiteView>> GetSiteAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<SiteProfileResult>> SaveSiteProfileAsync(
        SiteProfileValues profile,
        string expectedVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the sky map object bound to the operator settings file, or removes it so the default applies when
    /// <paramref name="maximumObjects"/> is null. The next projection uses it.
    /// </summary>
    ValueTask<OperatorUiResult<CameraAgentObjectLimitSetting>> SaveObjectLimitAsync(
        int? maximumObjects,
        string expectedVersion,
        CancellationToken cancellationToken);
}

/// <summary>Everything the Observatory &amp; location page shows besides geometry and the projected scene.</summary>
/// <param name="OwnerLoginEmail">The local owner account's email, offered as the default owner contact.</param>
/// <param name="ObjectLimit">The sky map object bound in the settings file, or null when this host loads none.</param>
/// <param name="ActorNames">
/// The sign-in name of every account that recorded a manual location change, keyed by the account identifier the
/// audit stores, so history shows who acted without exposing the identifier.
/// </param>
internal sealed record CameraAgentSiteView(
    SiteProfileState Profile,
    string? OwnerLoginEmail,
    CameraAgentSiteAssignment Assignment,
    CameraAgentSiteMapSettings Map,
    CameraAgentObjectLimitSetting? ObjectLimit,
    IReadOnlyDictionary<string, string> ActorNames);

/// <summary>The sky map object bound as the operator settings file holds it.</summary>
/// <param name="Version">The settings file version a save must name.</param>
/// <param name="SavedValue">The value the file sets, as written, or null when it sets none and the default applies.</param>
/// <param name="Overridden">
/// A source above the settings file, such as an environment variable, supplies the bound, so a saved value has no
/// effect until that source is removed.
/// </param>
internal sealed record CameraAgentObjectLimitSetting(string Version, string? SavedValue, bool Overridden);

internal enum CameraAgentSiteAssignmentState
{
    /// <summary>Central integration is disabled, so this camera never contacts LogicHost.</summary>
    Standalone,

    /// <summary>Central integration is enabled but this camera has not completed device registration.</summary>
    NotRegistered,

    /// <summary>The registration record could not be read.</summary>
    Unavailable,

    /// <summary>This camera holds a LogicHost registration.</summary>
    Registered
}

/// <summary>
/// The LogicHost Observatory assignment as this camera last received it. LogicHost owns every fact here; the
/// camera only reports what it was told and when, and never lets it replace local geometry.
/// </summary>
/// <param name="LocationReview">
/// LogicHost's disposition of the deployment location this camera reported. It is not Observatory membership,
/// which LogicHost never sends to the camera.
/// </param>
internal sealed record CameraAgentSiteAssignment(
    CameraAgentSiteAssignmentState State,
    string? RegistrationName,
    DeploymentLocationResolutionStatus? LocationReview,
    long? AcknowledgedVersion,
    long? ProposedVersion,
    string ReconciliationOutcome,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc);

/// <summary>The browser-side map backdrop. The browser fetches tiles itself; CameraAgent never proxies them.</summary>
internal sealed record CameraAgentSiteMapSettings(
    bool Enabled,
    string TileTemplate,
    string Attribution,
    Uri AttributionLink,
    int Zoom);

internal sealed class CameraAgentSkyMapUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    ICameraAgentSkyMapProjection projection,
    IDeploymentLocationStore deploymentLocationStore,
    TimeProvider timeProvider,
    ILogger<CameraAgentSkyMapUiService> logger,
    ISiteProfileStore? siteProfileStore = null,
    IOptions<CameraAgentHostOptions>? hostOptions = null,
    IOptions<LocalIdentityOptions>? localIdentityOptions = null,
    IDeviceSecretStore? deviceSecretStore = null,
    DeploymentLocationReconciliationState? reconciliationState = null,
    UserManager<ApplicationUser>? userManager = null,
    OperatorSettingsFile? settingsFile = null) : ICameraAgentSkyMapUiService
{
    private const string SettingsConflictMessage =
        "The settings file changed since this page was read. Refresh before retrying.";
    private const string SettingsUnreadableMessage =
        "The settings file is not valid settings JSON. Correct it, then refresh before retrying.";
    private const string NoSettingsFileMessage =
        "This host loads no operator settings file, so this setting cannot be saved here.";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<CameraAgentSkyMapProjectionResult>> GetSkyMapAsync(
        DateTimeOffset? atUtc,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        if (atUtc is { } requested &&
            !CameraAgentSkyMapInstantBounds.IsWithinBounds(requested, timeProvider.GetUtcNow()))
        {
            return OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Invalid, CameraAgentSkyMapInstantBounds.RejectionMessage);
        }
        try
        {
            return OperatorUiResult<CameraAgentSkyMapProjectionResult>.Success(
                await projection.ProjectAsync(atUtc, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent sky map UI read failed.");
            return OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Unavailable, "The sky map projection is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<ManualDeploymentLocationState>> GetManualLocationAsync(
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return OperatorUiResult<ManualDeploymentLocationState>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            return OperatorUiResult<ManualDeploymentLocationState>.Success(deploymentLocationStore.Manual);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent manual deployment-location read failed.");
            return OperatorUiResult<ManualDeploymentLocationState>.Failure(
                OperatorUiResultKind.Unavailable, "Manual deployment-location state is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<ManualDeploymentLocationResult>> ApplyManualLocationAsync(
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters,
        string timeZoneId,
        long expectedVersion,
        long expectedManualSequence,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            var result = await deploymentLocationStore.ApplyManualAsync(
                new ManualDeploymentLocationRequest(
                    latitudeDegrees,
                    longitudeDegrees,
                    elevationMeters,
                    timeZoneId,
                    expectedVersion,
                    expectedManualSequence,
                    idempotencyKey,
                    actor,
                    reason),
                cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                ManualDeploymentLocationStatus.Conflict => OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Conflict, DescribeFailure(result)),
                ManualDeploymentLocationStatus.Invalid => OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Invalid, DescribeFailure(result)),
                _ => OperatorUiResult<ManualDeploymentLocationResult>.Success(result)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent manual deployment-location command failed.");
            return OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                OperatorUiResultKind.Unavailable, "The coordinate change could not be completed.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<CameraAgentSiteView>> GetSiteAsync(CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return OperatorUiResult<CameraAgentSiteView>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        if (siteProfileStore is null)
        {
            return OperatorUiResult<CameraAgentSiteView>.Failure(
                OperatorUiResultKind.Unavailable, "The site profile is unavailable.");
        }
        try
        {
            var profile = await siteProfileStore.GetAsync(cancellationToken).ConfigureAwait(false);
            var options = hostOptions?.Value ?? new CameraAgentHostOptions();
            var map = options.SiteMap;
            var ownerEmail = localIdentityOptions?.Value.AdminEmail;
            // Read after the profile: were the file to change between the two reads, a save naming the profile's
            // older version is refused as a conflict rather than overwriting the newer value shown here.
            var objectLimit = settingsFile is null
                ? null
                : ReadObjectLimit(await settingsFile.ReadAsync(cancellationToken).ConfigureAwait(false));
            return OperatorUiResult<CameraAgentSiteView>.Success(new CameraAgentSiteView(
                profile,
                string.IsNullOrWhiteSpace(ownerEmail) ? null : ownerEmail.Trim(),
                await ReadAssignmentAsync(options, cancellationToken).ConfigureAwait(false),
                new CameraAgentSiteMapSettings(
                    map.Enabled, map.TileTemplate, map.Attribution, map.AttributionLink, map.Zoom),
                objectLimit is null ? null : objectLimit with { Version = profile.Version },
                await ResolveActorNamesAsync(deploymentLocationStore.Manual.History.Select(entry => entry.Actor))
                    .ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent site profile read failed.");
            return OperatorUiResult<CameraAgentSiteView>.Failure(
                OperatorUiResultKind.Unavailable, "The site profile is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<SiteProfileResult>> SaveSiteProfileAsync(
        SiteProfileValues profile,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return OperatorUiResult<SiteProfileResult>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        if (siteProfileStore is null)
        {
            return OperatorUiResult<SiteProfileResult>.Failure(
                OperatorUiResultKind.Unavailable, "The site profile is unavailable.");
        }
        try
        {
            var result = await siteProfileStore.ApplyAsync(
                new SiteProfileRequest(profile, expectedVersion, actor),
                cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                SiteProfileStatus.Conflict => OperatorUiResult<SiteProfileResult>.Failure(
                    OperatorUiResultKind.Conflict, DescribeProfileFailure(result)),
                SiteProfileStatus.Invalid => OperatorUiResult<SiteProfileResult>.Failure(
                    OperatorUiResultKind.Invalid, DescribeProfileFailure(result)),
                _ => OperatorUiResult<SiteProfileResult>.Success(result)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent site profile command failed.");
            return OperatorUiResult<SiteProfileResult>.Failure(
                OperatorUiResultKind.Unavailable, "The site profile change could not be completed.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<CameraAgentObjectLimitSetting>> SaveObjectLimitAsync(
        int? maximumObjects,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return OperatorUiResult<CameraAgentObjectLimitSetting>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        if (settingsFile is null)
        {
            return OperatorUiResult<CameraAgentObjectLimitSetting>.Failure(
                OperatorUiResultKind.Unavailable, NoSettingsFileMessage);
        }
        if (maximumObjects is < 1 or > CameraAgentSkyMapProjection.MaximumConfigurableObjects ||
            string.IsNullOrWhiteSpace(expectedVersion))
        {
            return OperatorUiResult<CameraAgentObjectLimitSetting>.Failure(
                OperatorUiResultKind.Invalid,
                $"The object limit must be a whole number from 1 to {CameraAgentSkyMapProjection.MaximumConfigurableObjects:N0}.");
        }
        try
        {
            var result = await settingsFile.WriteAsync(
                expectedVersion,
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    [SkyMapOptions.MaximumObjectsKey] = maximumObjects is { } value ? JsonValue.Create(value) : null
                },
                actor,
                cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                OperatorSettingsWriteStatus.Conflict => OperatorUiResult<CameraAgentObjectLimitSetting>.Failure(
                    OperatorUiResultKind.Conflict, SettingsConflictMessage),
                OperatorSettingsWriteStatus.Unreadable => OperatorUiResult<CameraAgentObjectLimitSetting>.Failure(
                    OperatorUiResultKind.Invalid, SettingsUnreadableMessage),
                _ => OperatorUiResult<CameraAgentObjectLimitSetting>.Success(ReadObjectLimit(result.Snapshot))
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent object limit change failed.");
            return OperatorUiResult<CameraAgentObjectLimitSetting>.Failure(
                OperatorUiResultKind.Unavailable, "The object limit change could not be completed.");
        }
    }

    private CameraAgentObjectLimitSetting ReadObjectLimit(OperatorSettingsSnapshot snapshot)
        => new(
            snapshot.Version,
            snapshot.GetValue(SkyMapOptions.MaximumObjectsKey),
            settingsFile?.FindOverriddenKeys([SkyMapOptions.MaximumObjectsKey]).Count > 0);

    /// <summary>Maps a rejected profile command to fixed operator-facing guidance.</summary>
    internal static string DescribeProfileFailure(SiteProfileResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (result.ReasonCode, result.FieldPath) switch
        {
            (SiteProfileLimits.ExpectedVersionConflictReasonCode, _) => SettingsConflictMessage,
            (SiteProfileLimits.UnreadableReasonCode, _) => SettingsUnreadableMessage,
            (SiteProfileLimits.UnavailableReasonCode, _) => NoSettingsFileMessage,
            (_, "observatoryName") =>
                $"Observatory name must be at most {SiteProfileLimits.MaximumNameLength} characters without control characters.",
            (_, "cameraName") =>
                $"Camera name must be at most {SiteProfileLimits.MaximumNameLength} characters without control characters.",
            (_, "ownerName") =>
                $"Owner name must be at most {SiteProfileLimits.MaximumNameLength} characters without control characters.",
            (_, "ownerContact") =>
                $"Owner contact must be at most {SiteProfileLimits.MaximumContactLength} characters without control characters.",
            _ => "The profile change was rejected before anything durable changed."
        };
    }

    private async ValueTask<IReadOnlyDictionary<string, string>> ResolveActorNamesAsync(IEnumerable<string> actors)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userManager is null)
        {
            return names;
        }
        foreach (var actor in actors.Where(actor => !string.IsNullOrWhiteSpace(actor)).Distinct(StringComparer.Ordinal))
        {
            var user = await userManager.FindByIdAsync(actor).ConfigureAwait(false);
            if ((user?.Email ?? user?.UserName) is { Length: > 0 } name)
            {
                names[actor] = name;
            }
        }
        return names;
    }

    private async ValueTask<CameraAgentSiteAssignment> ReadAssignmentAsync(
        CameraAgentHostOptions options,
        CancellationToken cancellationToken)
    {
        var outcome = reconciliationState?.Outcome ?? "not-started";
        var lastAttempt = reconciliationState?.LastAttemptUtc;
        var lastSuccess = reconciliationState?.LastSuccessUtc;
        if (options.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            return new(CameraAgentSiteAssignmentState.Standalone, null, null, null, null, outcome, null, null);
        }
        if (deviceSecretStore is null)
        {
            return new(CameraAgentSiteAssignmentState.Unavailable, null, null, null, null, outcome, lastAttempt, lastSuccess);
        }

        DeviceSecrets? secrets;
        try
        {
            secrets = await deviceSecretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            logger.LogWarning(exception, "CameraAgent device registration read failed for the site page.");
            return new(CameraAgentSiteAssignmentState.Unavailable, null, null, null, null, outcome, lastAttempt, lastSuccess);
        }
        if (secrets is null)
        {
            return new(CameraAgentSiteAssignmentState.NotRegistered, null, null, null, null, outcome, lastAttempt, lastSuccess);
        }

        var acknowledgement = secrets.DeploymentLocationAcknowledgment;
        return new(
            CameraAgentSiteAssignmentState.Registered,
            string.IsNullOrWhiteSpace(secrets.FriendlyName) ? null : secrets.FriendlyName.Trim(),
            acknowledgement?.Status,
            acknowledgement is { Status: DeploymentLocationResolutionStatus.Acknowledged }
                ? acknowledgement.Deployment.Version
                : null,
            deploymentLocationStore.Manual.PendingVersion,
            outcome,
            lastAttempt,
            lastSuccess);
    }

    /// <summary>Maps a rejected manual command to fixed operator-facing guidance.</summary>
    internal static string DescribeFailure(ManualDeploymentLocationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.Equals(
                result.ReasonCode,
                ManualDeploymentLocationContract.ExpectedVersionConflictReasonCode,
                StringComparison.Ordinal))
        {
            return "The deployment location changed since this page was read. Refresh before retrying.";
        }
        if (string.Equals(
                result.ReasonCode,
                ManualDeploymentLocationContract.ExpectedManualSequenceConflictReasonCode,
                StringComparison.Ordinal))
        {
            return "Another coordinate entry was recorded since this page was read. Refresh before retrying.";
        }
        if (string.Equals(
                result.ReasonCode,
                ManualDeploymentLocationContract.IdempotencyKeyConflictReasonCode,
                StringComparison.Ordinal))
        {
            return "This command identifier was already recorded with different coordinates. Refresh before retrying.";
        }
        if (string.Equals(
                result.ReasonCode,
                ManualDeploymentLocationContract.SupersededEntryReasonCode,
                StringComparison.Ordinal))
        {
            return "This command identifier was recorded against coordinates a configuration change has since "
                + "superseded. Refresh and submit the entry again.";
        }
        return result.FieldPath switch
        {
            "location.latitudeDegrees" => "Latitude must be a number between -90 and 90 degrees.",
            "location.longitudeDegrees" => "Longitude must be a number between -180 and 180 degrees, east positive.",
            "location.elevationMeters" =>
                $"Elevation must be a number between {ManualDeploymentLocationContract.MinimumElevationMeters:F0} "
                + $"and {ManualDeploymentLocationContract.MaximumElevationMeters:F0} metres.",
            "location.timeZoneId" =>
                "Time zone must be an IANA identifier this host can resolve, such as America/Phoenix or UTC.",
            _ => "The coordinate entry was rejected before anything durable changed."
        };
    }

    private async Task<bool> IsAuthorizedAsync()
        => await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false) is not null;

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy)
            .ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }
}

/// <summary>The bounded instant window every sky map read accepts.</summary>
internal static class CameraAgentSkyMapInstantBounds
{
    internal static readonly TimeSpan MaximumFuture = TimeSpan.FromHours(24);
    internal static readonly TimeSpan MaximumPast = TimeSpan.FromDays(62);

    internal const string RejectionMessage =
        "The requested instant must be no more than 24 hours ahead of, or 62 days behind, the current time.";

    internal static bool IsWithinBounds(DateTimeOffset atUtc, DateTimeOffset nowUtc)
        => atUtc <= nowUtc + MaximumFuture && atUtc >= nowUtc - MaximumPast;
}
