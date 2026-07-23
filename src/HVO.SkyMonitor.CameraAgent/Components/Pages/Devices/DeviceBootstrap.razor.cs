using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages.Devices;

public sealed partial class DeviceBootstrap : ComponentBase
{
    private DeviceIdentity? Identity { get; set; }
    private DeviceSecrets? Secrets { get; set; }
    private string EnvelopeInput { get; set; } = string.Empty;
    private string? SubmitError { get; set; }
    private string? SubmitSuccess { get; set; }
    private bool IsBusy { get; set; }
    private bool IsCentralIntegrationDisabled =>
        HostOptions.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled;

    [Inject]
    internal IDeviceIdentityStore IdentityStore { get; set; } = default!;

    [Inject]
    internal IDeviceSecretStore SecretStore { get; set; } = default!;

    [Inject]
    internal DeviceBootstrapWorkflow BootstrapWorkflow { get; set; } = default!;

    [Inject]
    internal IOptions<CameraAgentHostOptions> HostOptions { get; set; } = default!;

    [Inject]
    internal ILogger<DeviceBootstrap> Logger { get; set; } = default!;

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
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsBusy = true;
            SubmitError = null;
            SubmitSuccess = null;
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
            SubmitSuccess = "Bootstrap completed successfully. Device secrets stored securely.";
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

    private static string FormatTimestamp(DateTimeOffset value)
        => value == default
            ? "—"
            : value.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
}
