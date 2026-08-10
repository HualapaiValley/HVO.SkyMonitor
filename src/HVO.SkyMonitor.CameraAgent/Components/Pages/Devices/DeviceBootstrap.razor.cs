using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages.Devices;

public sealed partial class DeviceBootstrap : ComponentBase
{
    private DeviceIdentity? Identity { get; set; }
    private DeviceSecrets? Secrets { get; set; }
    private string EnvelopeInput { get; set; } = string.Empty;
    private string? SubmitError { get; set; }
    private string? SubmitSuccess { get; set; }
    private string? CopyStatus { get; set; }
    private string? CaptureAgentId { get; set; }
    private bool IsBusy { get; set; }
    private bool IsCentralIntegrationDisabled =>
        HostOptions.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled;

    [Inject]
    internal IDeviceIdentityStore IdentityStore { get; set; } = default!;

    [Inject]
    internal IDeviceSecretStore SecretStore { get; set; } = default!;

    [Inject]
    internal IDeviceBootstrapWorkflow BootstrapWorkflow { get; set; } = default!;

    [Inject]
    internal IOptions<CameraAgentHostOptions> HostOptions { get; set; } = default!;

    [Inject]
    internal ILogger<DeviceBootstrap> Logger { get; set; } = default!;

    [Inject]
    internal ICameraAgentConfigurationAccessor ConfigurationAccessor { get; set; } = default!;

    [Inject]
    internal FleetHeartbeatState HeartbeatState { get; set; } = default!;

    [Inject]
    internal ArtifactOutboxState OutboxState { get; set; } = default!;

    [Inject]
    internal IOptions<SkyMonitorClientOptions> SkyMonitorOptions { get; set; } = default!;

    [Inject]
    internal IOptions<CentralIdentityOptions> CentralIdentityOptions { get; set; } = default!;

    [Inject]
    internal IJSRuntime JsRuntime { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await LoadStateAsync().ConfigureAwait(false);
    }

    private async Task LoadStateAsync()
    {
        if (!IsCentralIntegrationDisabled)
        {
            Identity = await IdentityStore.GetOrCreateAsync().ConfigureAwait(false);
        }
        Secrets = await SecretStore.GetAsync().ConfigureAwait(false);
        CaptureAgentId = (await ConfigurationAccessor.WaitForConfigurationAsync(default).ConfigureAwait(false)).AgentId;
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsBusy = true;
            SubmitError = null;
            SubmitSuccess = null;
            CopyStatus = null;
            await LoadStateAsync().ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SubmitAsync()
    {
        if (string.IsNullOrWhiteSpace(EnvelopeInput))
        {
            SubmitError = "Envelope is required.";
            return;
        }

        try
        {
            IsBusy = true;
            SubmitError = null;
            SubmitSuccess = null;

            Secrets = await BootstrapWorkflow.BootstrapAsync(EnvelopeInput).ConfigureAwait(false);
            EnvelopeInput = string.Empty;
            SubmitSuccess = "Bootstrap completed. Device secrets are stored securely; refresh to confirm heartbeat and upload readiness.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is >= System.Net.HttpStatusCode.BadRequest and < System.Net.HttpStatusCode.InternalServerError)
        {
            Logger.LogWarning(ex, "LogicHost rejected the device bootstrap envelope.");
            SubmitError = "LogicHost rejected the envelope. It may be expired, already used, or issued for another device. Return to LogicHost and issue a new envelope.";
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "Device bootstrap request failed.");
            SubmitError = "Unable to reach LogicHost. Verify network connectivity and try again.";
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogError(ex, "Device bootstrap response was invalid.");
            SubmitError = ex.Message;
        }
        catch (CryptographicException ex)
        {
            Logger.LogError(ex, "Device bootstrap payload could not be decrypted.");
            SubmitError = "Received device secrets could not be decrypted. Confirm the envelope and try again.";
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "Device bootstrap response could not be parsed.");
            SubmitError = "Bootstrap response was malformed. Check the service logs for more details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearSecretsAsync()
    {
        try
        {
            IsBusy = true;
            await SecretStore.ClearAsync().ConfigureAwait(false);
            Secrets = null;
            SubmitError = null;
            SubmitSuccess = "Stored secrets were cleared.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Uri LogicHostRegistrationUri => new(
        CentralIdentityOptions.Value.InteractiveClient?.PublicAuthority ?? SkyMonitorOptions.Value.ResolveBaseUri(),
        "/devices/register");

    private bool IsCaptureIdentityAligned =>
        Identity is not null && string.Equals(CaptureAgentId, Identity.DeviceId, StringComparison.Ordinal);

    private FleetHeartbeatStateSnapshot Heartbeat => HeartbeatState.Snapshot;

    private ArtifactOutboxStateSnapshot Outbox => OutboxState.Snapshot;

    private bool IsHeartbeatReady =>
        Secrets is not null &&
        Heartbeat.Availability == FleetAvailability.Available &&
        Heartbeat.LastAcknowledgedUtc is { } acknowledgedUtc &&
        acknowledgedUtc >= Secrets.IssuedAtUtc &&
        DateTimeOffset.UtcNow - acknowledgedUtc <= TimeSpan.FromSeconds(Math.Max(30, Secrets.HeartbeatIntervalSeconds * 3));

    private bool IsUploadReady =>
        Secrets is not null &&
        HostOptions.Value.CaptureDistribution.UploadEnabled &&
        Outbox.Availability == ArtifactOutboxAvailability.Healthy;

    private async Task CopyAsync(string value, string label)
    {
        try
        {
            await JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", value).ConfigureAwait(false);
            CopyStatus = $"{label} copied.";
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Clipboard copy failed for {PairingValueLabel}", label);
            CopyStatus = "Clipboard access was blocked. Select and copy the value manually.";
        }
    }

    private Task CopyDeviceIdAsync()
        => Identity is null ? Task.CompletedTask : CopyAsync(Identity.DeviceId, "Device ID");

    private Task CopyVerificationCodeAsync()
        => Identity is null ? Task.CompletedTask : CopyAsync(Identity.VerificationCode, "Verification code");

    private static string FormatTimestamp(DateTimeOffset value)
        => value == default
            ? "—"
            : value.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
}
