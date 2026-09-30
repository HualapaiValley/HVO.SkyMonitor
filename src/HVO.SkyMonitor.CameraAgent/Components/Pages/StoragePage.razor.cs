using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class StoragePage : ComponentBase, IAsyncDisposable
{
    private const string PolicyTriggerId = "storage-policy";
    private const string FocusFallbackId = "storage-refresh";

    /// <summary>
    /// No on-demand reconciliation command exists. Raw ingress reconciles once as the agent starts and derived products
    /// every five minutes, so the action stays visible but disabled with this explanation.
    /// </summary>
    internal const string ReconcileUnavailableReason =
        "Reconciliation runs automatically: raw ingress when the agent starts, derived products every 5 minutes. There is no on-demand command.";

    private readonly CancellationTokenSource _lifetime = new();
    private CameraAgentOperationsView? _view;
    private CameraAgentSystemStatus? _system;
    private CameraAgentStorageReconciliation? _reconciliation;
    private string? _error;
    private string? _policyError;
    private string? _reconciliationError;
    private bool _loading = true;
    private bool _disposed;
    private bool _dialogOpen;
    private bool _showDialog;
    private string? _focusTargetId;
    private IJSObjectReference? _module;
    private ElementReference _dialogElement;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    private string? PolicyUnavailableReason => _system is null
        ? _policyError ?? "The retention policy is not loaded."
        : null;

    private bool HasStoragePressure => _view is not null
        && _view.Summary.Storage.Value.Any(static location => location.IsUnderPressure);

    private bool HasPressure => HasStoragePressure
        || (_view is not null && _view.Summary.CaptureLanes.Value.Lanes.Any(static lane => lane.PressureLevel > 0));

    /// <summary>Everything that pins evidence against retention, in the order work flows through the agent.</summary>
    private IReadOnlyList<Hold> Holds
    {
        get
        {
            if (_view is null)
            {
                return [];
            }
            var summary = _view.Summary;
            var ingress = summary.RawIngress.Value;
            var lanes = summary.CaptureLanes.Value;
            var outbox = summary.ArtifactOutbox.Value;
            // Lane and outbox totals include their quarantined items, which are listed once under Quarantine instead.
            // The processing queue is the standard capture lane, so it is not listed a second time. The raw figure
            // counts captures whose hold is set for any reason, including lane work and quarantined lane items, so
            // it overlaps the rows below it; the page says so rather than presenting the rows as a breakdown.
            return
            [
                new Hold("Raw captures held", ingress.PendingCount, ingress.OldestPendingUtc),
                new Hold("Capture lanes", Waiting(lanes.PendingCount, lanes.QuarantineCount), lanes.OldestPendingUtc),
                new Hold("Delivery", Waiting(outbox.PendingCount, outbox.QuarantineCount), outbox.OldestPendingUtc),
                new Hold("Quarantine", ingress.QuarantineCount + lanes.QuarantineCount + outbox.QuarantineCount, null),
            ];
        }
    }

    private string OldestHeld
    {
        get
        {
            var oldest = Holds.Where(static hold => hold.OldestUtc is not null).MinBy(static hold => hold.OldestUtc);
            return oldest?.OldestUtc is { } value
                ? $"{FormatUtc(value)}, {oldest.Label}"
                : "Nothing is waiting";
        }
    }

    private DateTimeOffset? LastReconciledUtc
    {
        get
        {
            var raw = _reconciliation?.RawIngress?.CompletedUtc;
            var derived = _reconciliation?.DerivedProducts?.CompletedUtc;
            return raw is null ? derived : derived is null ? raw : raw > derived ? raw : derived;
        }
    }

    private IEnumerable<ReconciliationLine> ReconciliationLines
    {
        get
        {
            var raw = _reconciliation?.RawIngress;
            if (raw is null)
            {
                yield return new ReconciliationLine("neutral", "Raw ingress: no reconciliation has completed since the agent started.");
            }
            else
            {
                var problems = raw.Quarantined + raw.MissingEvidence + raw.IndexProjectionFailures;
                yield return new ReconciliationLine(problems > 0 ? "failed" : string.Empty, string.Create(
                    CultureInfo.InvariantCulture,
                    $"Raw ingress at startup, {FormatUtc(raw.CompletedUtc)}: {Count(raw.Inspected, "frame", "frames")} inspected, {raw.Recovered} recovered, {raw.Cleaned} cleaned up."));
                if (raw.Quarantined > 0 || raw.MissingEvidence > 0)
                {
                    yield return new ReconciliationLine("failed", string.Create(
                        CultureInfo.InvariantCulture,
                        $"Raw ingress: {raw.Quarantined} quarantined and {raw.MissingEvidence} missing their evidence file. Review them under Quarantine & recovery."));
                }
                if (raw.IndexProjectionFailures > 0)
                {
                    yield return new ReconciliationLine("failed", string.Create(
                        CultureInfo.InvariantCulture,
                        $"Raw ingress: {Count(raw.IndexProjectionFailures, "frame", "frames")} could not be added to the local index."));
                }
            }

            var derived = _reconciliation?.DerivedProducts;
            if (derived is null)
            {
                yield return new ReconciliationLine("neutral", "Derived products: the first check runs within 5 minutes of startup.");
            }
            else if (!derived.Succeeded)
            {
                yield return new ReconciliationLine("failed", $"Derived products: the check at {FormatUtc(derived.CompletedUtc)} failed. It runs again within 5 minutes.");
            }
            else
            {
                var problems = derived.Missing + derived.Quarantined;
                yield return new ReconciliationLine(problems > 0 ? "failed" : string.Empty, string.Create(
                    CultureInfo.InvariantCulture,
                    $"Derived products, {FormatUtc(derived.CompletedUtc)}: {Count(derived.Inspected, "product", "products")} inspected, {derived.Recoverable} recoverable, {derived.Cleaned} cleaned up."));
                if (problems > 0)
                {
                    yield return new ReconciliationLine("failed", string.Create(
                        CultureInfo.InvariantCulture,
                        $"Derived products: {derived.Missing} missing and {derived.Quarantined} quarantined."));
                }
            }
        }
    }

    protected override Task OnInitializedAsync() => LoadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/StoragePage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _dialogElement).ConfigureAwait(false);
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/StoragePage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", target, FocusFallbackId).ConfigureAwait(false);
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var operations = await OperatorService.GetOperationsAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (operations.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            if (operations.IsSuccess && operations.Value is not null)
            {
                _view = operations.Value;
                _error = null;
            }
            else
            {
                // Keep the last valid snapshot visible and say why it is stale.
                _error = operations.Message ?? "Current storage and retention state is unavailable.";
            }

            var system = await OperatorService.GetSystemStatusAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (system.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            if (system.IsSuccess && system.Value is not null)
            {
                _system = system.Value;
                _policyError = null;
            }
            else
            {
                _system = null;
                _policyError = system.Message ?? "The retention policy is unavailable.";
                CloseDialog();
            }

            var reconciliation = await OperatorService.GetStorageReconciliationAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (reconciliation.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            _reconciliation = reconciliation.IsSuccess ? reconciliation.Value : null;
            _reconciliationError = reconciliation.IsSuccess
                ? null
                : reconciliation.Message ?? "Reconciliation results are unavailable.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _loading = false;
        }
    }

    private void DenyAccess() => NavigationManager.NavigateTo("/Account/AccessDenied");

    private void OpenPolicyDialog()
    {
        if (_system is null)
        {
            return;
        }
        _focusTargetId = null;
        _dialogOpen = true;
        _showDialog = true;
    }

    private void CloseDialog()
    {
        if (_dialogOpen)
        {
            _focusTargetId = PolicyTriggerId;
        }
        _dialogOpen = false;
        _showDialog = false;
    }

    /// <summary>The used share of a location, or null when its capacity is unknown.</summary>
    internal static int? UsedPercent(OperationsStorageState location)
    {
        if (!location.ProbeSucceeded || location.TotalBytes <= 0)
        {
            return null;
        }
        var used = Math.Clamp(location.TotalBytes - location.AvailableBytes, 0, location.TotalBytes);
        return (int)Math.Round(used * 100d / location.TotalBytes, MidpointRounding.AwayFromZero);
    }

    private static string DonutStyle(int percent)
        => string.Create(CultureInfo.InvariantCulture, $"--used: {percent}%");

    private static (string Text, string Chip) CapacityState(IReadOnlyList<OperationsStorageState> storage)
    {
        if (storage.Count == 0 || storage.Any(static location => UsedPercent(location) is null))
        {
            return storage.Any(static location => location.IsUnderPressure) ? ("Under pressure", "warning") : ("Unknown", "pending");
        }
        return storage.Any(static location => location.IsUnderPressure) ? ("Under pressure", "warning") : ("Healthy", "success");
    }

    internal static long Waiting(long held, long quarantined) => Math.Max(0, held - quarantined);

    private static (string Text, string Chip) Pressure(int level) => level switch
    {
        >= 2 => ("Critical", "failure"),
        1 => ("Warning", "warning"),
        _ => ("Normal", "success"),
    };

    private static string AvailabilityChip(string availability) => availability switch
    {
        "Accepting" or "Healthy" => "success",
        "Degraded" => "warning",
        "Unhealthy" => "failure",
        _ => "pending",
    };

    private static string Percent(double value)
        => string.Create(CultureInfo.InvariantCulture, $"{value:0.#}%");

    private static string Days(int days) => days == 1 ? "1 day" : string.Create(CultureInfo.InvariantCulture, $"{days} days");

    private static string Minutes(int minutes) => minutes == 1 ? "minute" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes");

    private static string Count(long value, string singular, string plural)
        => string.Create(CultureInfo.InvariantCulture, $"{value:N0} {(value == 1 ? singular : plural)}");

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("MMM d, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private sealed record Hold(string Label, long Count, DateTimeOffset? OldestUtc);

    private sealed record ReconciliationLine(string Kind, string Text)
    {
        public string Glyph => Kind switch { "failed" => "!", "neutral" => "~", _ => "+" };

        public string Status => Kind switch { "failed" => "Needs attention:", "neutral" => "Not run yet:", _ => "Passed:" };
    }
}
