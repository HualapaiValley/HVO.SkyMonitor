using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// Software &amp; catalog: the prototype <c>renderSoftware()</c> composition bound to this process and the catalog
/// snapshot it validated at startup. Availability checks and on-demand checksum verification have no bounded
/// in-app command, so they stay disabled with the reason; install, select and rollback remain installer driven.
/// </summary>
public sealed partial class SoftwarePage : ComponentBase, IDisposable
{
    private const string AvailabilityUnavailableReason =
        "This page never contacts a package source. hvo-skymonitor status reports what is installed, and hvo-skymonitor catalog install adds a package.";
    private const string VerifyUnavailableReason =
        "The selected catalog's checksum is verified every time CameraAgent starts, and it refuses to start on a mismatch.";
    private readonly CancellationTokenSource _lifetime = new();
    private SystemSoftwareView? _software;
    private string? _error;
    private bool _loading = true;
    private bool _disposed;

    [Inject] internal ICameraAgentSystemUiService SystemService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var result = await SystemService.GetSoftwareAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            _software = result.IsSuccess ? result.Value : null;
            _error = result.IsSuccess ? null : result.Message ?? "The software inventory could not be read.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _loading = false;
        }
    }

    internal static string PackageKindText(string kind) => kind switch
    {
        "Production" => "official",
        "Fixture" => "test fixture",
        _ => kind,
    };

    /// <summary>The first and last eight hex digits; the full value is in the element title.</summary>
    internal static string ShortChecksum(string sha256) => sha256.Length > 20
        ? $"sha256:{sha256[..8]}…{sha256[^8..]}"
        : $"sha256:{sha256}";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
