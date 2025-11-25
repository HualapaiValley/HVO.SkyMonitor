using System;
using System.Globalization;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages.Devices;

public sealed partial class DeviceBootstrap : ComponentBase
{
    private DeviceIdentity? Identity { get; set; }
    private DeviceSecrets? Secrets { get; set; }
    private string EnvelopeInput { get; set; } = string.Empty;
    private string? SubmitError { get; set; }
    private string? SubmitSuccess { get; set; }
    private bool IsBusy { get; set; }

    [Inject]
    internal IDeviceIdentityStore IdentityStore { get; set; } = default!;

    [Inject]
    internal IDeviceSecretStore SecretStore { get; set; } = default!;

    [Inject]
    internal DeviceBootstrapWorkflow BootstrapWorkflow { get; set; } = default!;

    [Inject]
    internal ILogger<DeviceBootstrap> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await LoadStateAsync().ConfigureAwait(false);
    }

    private async Task LoadStateAsync()
    {
        Identity = await IdentityStore.GetOrCreateAsync().ConfigureAwait(false);
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
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to bootstrap device");
            SubmitError = ex.Message;
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
