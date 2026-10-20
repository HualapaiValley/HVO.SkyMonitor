using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// Software &amp; catalog: the prototype <c>renderSoftware()</c> composition bound to this process and the catalog
/// snapshot it validated at startup. Availability checks and on-demand checksum verification have no bounded
/// in-app command, so they stay disabled with the reason; install, select and rollback remain installer driven. The
/// orbital snapshot panel owns its own status read and its bounded Refresh and Resume commands; the summary rail shows
/// the same view it reports.
/// </summary>
public sealed partial class SoftwarePage : SiteTimeComponent, IDisposable
{
    private const string AvailabilityUnavailableReason =
        "This page never contacts a package source. hvo-sky-install status reports what is installed, and hvo-sky-install catalog install adds a package.";
    private const string VerifyUnavailableReason =
        "The selected catalog's checksum is verified every time CameraAgent starts, and it refuses to start on a mismatch.";
    private readonly CancellationTokenSource _lifetime = new();
    private SystemSoftwareView? _software;
    private OrbitalDataStatusView? _orbitalView;
    private string? _error;
    private bool _loading = true;
    private bool _orbitalRead;
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

    private void OnOrbitalViewChanged(OrbitalDataStatusView? view)
    {
        _orbitalView = view;
        _orbitalRead = true;
    }

    /// <summary>
    /// The summary rail's orbital tile: the panel's state word and one short line. Element age comes from the newest
    /// epoch and retrieval age from the last successful retrieval; an unreadable status or store makes no data claim.
    /// </summary>
    internal static (string State, string Detail) OrbitalRail(OrbitalDataStatusView? view, bool read)
    {
        if (view is null)
        {
            return read ? ("Unavailable", "Status could not be read") : ("Reading", "Reading the orbital status");
        }
        var state = view.State;
        var label = OrbitalOperatorText.StateLabel(state);
        var elementAge = view.NewestElementAge is { } age ? string.Concat(HealthPage.FormatDuration(age), " old") : null;
        return (label, state switch
        {
            OrbitalDataState.Unavailable => "Orbital store unavailable",
            OrbitalDataState.Halted => string.Concat("Provider halted: ", view.Status.ProviderHalt is { } halt
                ? OrbitalOperatorText.HaltStatus(halt.HttpStatusCode)
                : OrbitalOperatorText.NoHttpStatusText),
            OrbitalDataState.Quarantined => "Last download quarantined",
            OrbitalDataState.NeedsRepair => "Orbital storage needs attention",
            OrbitalDataState.NoData => "No orbital data installed",
            OrbitalDataState.Disabled => elementAge is null
                ? "Refresh off; no data installed"
                : string.Concat("Refresh off; newest elements ", elementAge),
            OrbitalDataState.Stale when elementAge is null => "Element age unknown; treated as stale",
            OrbitalDataState.RefreshOverdue => view.RetrievalAge is { } retrieval
                ? string.Concat("Last retrieval ", HealthPage.FormatDuration(retrieval), " ago")
                : "No successful retrieval recorded",
            _ => elementAge is null ? "Element epochs not shown" : string.Concat("Newest elements ", elementAge),
        });
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

    private string FormatSiteTime(DateTimeOffset value) => SiteTime.Format(value);

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
