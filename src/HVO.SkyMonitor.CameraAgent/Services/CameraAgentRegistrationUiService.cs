using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The Registration page's read projection and envelope import. The projection carries the pairing identity and
/// fixed labels for the central relationship; it never carries the registration token, device key, client
/// identifiers, token or heartbeat endpoints, the Observatory identifier, the state directory, or the envelope.
/// </summary>
internal interface ICameraAgentRegistrationUiService
{
    ValueTask<OperatorUiResult<RegistrationView>> GetRegistrationAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<RegistrationImportReceipt>> ImportEnvelopeAsync(
        string envelope,
        CancellationToken cancellationToken);
}

internal enum RegistrationState
{
    /// <summary>Central integration is disabled, so this camera never contacts LogicHost.</summary>
    Standalone,

    /// <summary>Central integration is enabled and no envelope has been imported.</summary>
    NotRegistered,

    /// <summary>An envelope was imported and LogicHost has not yet acknowledged a heartbeat under it.</summary>
    Waiting,

    /// <summary>LogicHost acknowledges this camera's heartbeat.</summary>
    Active,

    /// <summary>LogicHost refuses this camera's credentials, as it does once the device is revoked.</summary>
    Rejected,

    /// <summary>The protected registration record could not be read.</summary>
    Unavailable
}

/// <summary>The local pairing identity LogicHost asks for. Both values are shown so the owner can copy them.</summary>
internal sealed record RegistrationIdentity(string DeviceId, string VerificationCode, DateTimeOffset CreatedUtc);

/// <summary>The non-secret facts of an imported registration.</summary>
internal sealed record RegistrationRecord(
    string? FriendlyName,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    int HeartbeatIntervalSeconds,
    DeploymentLocationResolutionStatus? LocationReview);

internal sealed record RegistrationHeartbeat(
    FleetAvailability Availability,
    bool CredentialsRejected,
    DateTimeOffset? LastAcknowledgedUtc,
    bool Ready);

internal sealed record RegistrationDelivery(
    bool UploadEnabled,
    ArtifactOutboxAvailability Availability,
    long PendingCount,
    long RetryCount,
    long QuarantineCount,
    bool Ready);

/// <param name="LogicHost">The LogicHost host name only; never a path, endpoint or credential.</param>
/// <param name="RegistrationUri">LogicHost's device registration page, or null when no public address is set.</param>
/// <param name="CaptureIdentityAligned">Captures are recorded under the provisioned device identity.</param>
/// <param name="CanImport">The signed-in account holds operations change rights.</param>
internal sealed record RegistrationView(
    RegistrationState State,
    RegistrationIdentity? Identity,
    RegistrationRecord? Record,
    RegistrationHeartbeat Heartbeat,
    RegistrationDelivery Delivery,
    string? LogicHost,
    Uri? RegistrationUri,
    bool CaptureIdentityAligned,
    bool CanImport);

internal sealed record RegistrationImportReceipt(string? FriendlyName, DeploymentLocationResolutionStatus LocationReview);

internal sealed class CameraAgentRegistrationUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IDeviceBootstrapWorkflow bootstrapWorkflow,
    ICameraAgentConfigurationAccessor configurationAccessor,
    FleetHeartbeatState heartbeatState,
    ArtifactOutboxState outboxState,
    IOptions<CameraAgentHostOptions> hostOptions,
    IOptions<SkyMonitorClientOptions> clientOptions,
    TimeProvider timeProvider,
    ILogger<CameraAgentRegistrationUiService> logger) : ICameraAgentRegistrationUiService
{
    /// <summary>Longer than any envelope LogicHost issues; a paste beyond it is not an envelope.</summary>
    internal const int EnvelopeMaxLength = 16_384;

    internal const string CredentialsRejectedReason = "credentials-blocked";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<RegistrationView>> GetRegistrationAsync(CancellationToken cancellationToken)
    {
        var user = (await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (!(await authorizationService.AuthorizeAsync(
                user, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false)).Succeeded)
        {
            return Denied<RegistrationView>();
        }
        var canImport = (await authorizationService.AuthorizeAsync(
            user, CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)).Succeeded;

        var options = hostOptions.Value;
        var central = options.CentralIntegration.Mode != CentralIntegrationMode.Disabled;
        var logicHost = clientOptions.Value.TryResolvePublicBaseUri(out var baseUri) ? baseUri.Host : null;
        var registrationUri = baseUri is null ? null : new Uri(baseUri, "/devices/register");
        var outbox = outboxState.Snapshot;
        var heartbeat = heartbeatState.Snapshot;
        if (!central)
        {
            return OperatorUiResult<RegistrationView>.Success(new RegistrationView(
                RegistrationState.Standalone,
                null,
                null,
                MapHeartbeat(heartbeat, null),
                MapDelivery(options, outbox, null),
                null,
                null,
                false,
                canImport));
        }

        try
        {
            var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            var agentId = (await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false)).AgentId;
            DeviceSecrets? secrets;
            var unreadable = false;
            try
            {
                secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or CryptographicException or JsonException)
            {
                logger.LogWarning("CameraAgent registration record read failed with {ExceptionType}.", exception.GetType().Name);
                secrets = null;
                unreadable = true;
            }

            var mappedHeartbeat = MapHeartbeat(heartbeat, secrets);
            var state = unreadable
                ? RegistrationState.Unavailable
                : secrets is null
                    ? RegistrationState.NotRegistered
                    : mappedHeartbeat.CredentialsRejected
                        ? RegistrationState.Rejected
                        : mappedHeartbeat.Ready ? RegistrationState.Active : RegistrationState.Waiting;
            return OperatorUiResult<RegistrationView>.Success(new RegistrationView(
                state,
                new RegistrationIdentity(identity.DeviceId, identity.VerificationCode, identity.CreatedUtc),
                secrets is null
                    ? null
                    : new RegistrationRecord(
                        string.IsNullOrWhiteSpace(secrets.FriendlyName) ? null : secrets.FriendlyName.Trim(),
                        secrets.IssuedAtUtc,
                        secrets.ExpiresAtUtc,
                        secrets.HeartbeatIntervalSeconds,
                        secrets.DeploymentLocationAcknowledgment?.Status),
                mappedHeartbeat,
                MapDelivery(options, outbox, secrets),
                logicHost,
                registrationUri,
                string.Equals(agentId, identity.DeviceId, StringComparison.Ordinal),
                canImport));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("CameraAgent registration projection failed with {ExceptionType}.", exception.GetType().Name);
            return Unavailable<RegistrationView>("The registration state could not be read.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<RegistrationImportReceipt>> ImportEnvelopeAsync(
        string envelope,
        CancellationToken cancellationToken)
    {
        var user = (await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (!(await authorizationService.AuthorizeAsync(
                user, CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)).Succeeded)
        {
            return Denied<RegistrationImportReceipt>();
        }
        if (hostOptions.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            return Invalid<RegistrationImportReceipt>(
                "Central integration is off, so this CameraAgent cannot register with LogicHost.");
        }
        var trimmed = envelope?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Invalid<RegistrationImportReceipt>("Paste the envelope LogicHost issued for this device.");
        }
        if (trimmed.Length > EnvelopeMaxLength)
        {
            return Invalid<RegistrationImportReceipt>(
                "That is longer than any envelope LogicHost issues. Copy the envelope again and paste only it.");
        }

        try
        {
            var secrets = await bootstrapWorkflow.BootstrapAsync(trimmed, cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<RegistrationImportReceipt>.Success(new RegistrationImportReceipt(
                string.IsNullOrWhiteSpace(secrets.FriendlyName) ? null : secrets.FriendlyName.Trim(),
                secrets.DeploymentLocationAcknowledgment?.Status ?? DeploymentLocationResolutionStatus.Pending));
        }
        catch (HttpRequestException exception) when (exception.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            logger.LogWarning("LogicHost rejected the device bootstrap envelope with status {StatusCode}.", (int)exception.StatusCode.Value);
            return Invalid<RegistrationImportReceipt>(
                "LogicHost rejected the envelope. It may have expired, already been used, or been issued for another device. Issue a new envelope in LogicHost and paste it here.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Device bootstrap request failed with {ExceptionType}.", exception.GetType().Name);
            return Unavailable<RegistrationImportReceipt>(
                "LogicHost could not be reached. Check the network connection and try again; the same envelope can be used until it expires.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Device bootstrap request timed out.");
            return Unavailable<RegistrationImportReceipt>(
                "LogicHost did not answer in time. Try again; the same envelope can be used until it expires.");
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning("Device bootstrap payload could not be decrypted: {ExceptionType}.", exception.GetType().Name);
            return Invalid<RegistrationImportReceipt>(
                "The registration LogicHost returned could not be decrypted. Check that the whole envelope was pasted, or issue a new one.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or InvalidDataException)
        {
            logger.LogWarning(exception, "Device bootstrap response was rejected.");
            return Invalid<RegistrationImportReceipt>(
                "LogicHost's response did not pass this CameraAgent's checks, so nothing was stored. The host log has the detail.");
        }
        catch (Exception exception)
        {
            logger.LogWarning("Device bootstrap failed with {ExceptionType}.", exception.GetType().Name);
            return Unavailable<RegistrationImportReceipt>("The envelope could not be imported. Nothing was stored.");
        }
    }

    private RegistrationHeartbeat MapHeartbeat(FleetHeartbeatStateSnapshot snapshot, DeviceSecrets? secrets)
    {
        var rejected = secrets is not null
            && string.Equals(snapshot.Reason, CredentialsRejectedReason, StringComparison.Ordinal);
        var ready = secrets is not null
            && !rejected
            && snapshot.Availability == FleetAvailability.Available
            && snapshot.LastAcknowledgedUtc is { } acknowledged
            && acknowledged >= secrets.IssuedAtUtc
            && timeProvider.GetUtcNow() - acknowledged
                <= TimeSpan.FromSeconds(Math.Max(30, secrets.HeartbeatIntervalSeconds * 3));
        return new RegistrationHeartbeat(snapshot.Availability, rejected, snapshot.LastAcknowledgedUtc, ready);
    }

    private static RegistrationDelivery MapDelivery(
        CameraAgentHostOptions options,
        ArtifactOutboxStateSnapshot snapshot,
        DeviceSecrets? secrets)
    {
        var enabled = options.CaptureDistribution.UploadEnabled;
        return new RegistrationDelivery(
            enabled,
            snapshot.Availability,
            snapshot.PendingCount,
            snapshot.RetryCount,
            snapshot.QuarantineCount,
            secrets is not null && enabled && snapshot.Availability == ArtifactOutboxAvailability.Healthy);
    }

    private static OperatorUiResult<T> Denied<T>() => OperatorUiResult<T>.Failure(
        OperatorUiResultKind.Unauthorized,
        "You are not authorized for this operation.");

    private static OperatorUiResult<T> Invalid<T>(string message) =>
        OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, message);

    private static OperatorUiResult<T> Unavailable<T>(string message) =>
        OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, message);
}
