using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class DeliveryPage : SiteTimeComponent, IAsyncDisposable
{
    private const string PolicyTriggerId = "delivery-policy";
    private const string FocusFallbackId = "delivery-refresh";
    internal const string UnknownCountText = "Unknown";
    internal const string NotReadText = "Not read yet";

    /// <summary>
    /// No audited bulk-retry contract exists. Waiting items already retry on their own schedule, and quarantined items
    /// are replayed one at a time with a reason, so the action stays visible but disabled with this explanation.
    /// </summary>
    internal const string RetryUnavailableReason =
        "Waiting items retry automatically after their backoff. Quarantined items are replayed one at a time under Quarantine & recovery, so there is no bulk retry.";

    private readonly CancellationTokenSource _lifetime = new();
    private CameraAgentOperationsView? _view;
    private IReadOnlyList<CameraAgentDeliveryRecord>? _records;
    private CameraAgentSystemStatus? _system;
    private string? _error;
    private string? _recordsError;
    private string? _policyError;
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

    private bool CentralDisabled => string.Equals(
        _view?.Summary.Configuration.Value.CentralIntegration, "Disabled", StringComparison.OrdinalIgnoreCase);

    private string? PolicyUnavailableReason => _system is null
        ? _policyError ?? "The export policy is not loaded."
        : null;

    private string ExportSummary => _system is null
        ? "Export policy unavailable"
        : (_system.Upload.Enabled, _system.Environmental.Enabled) switch
        {
            (true, true) => "Captures and environment",
            (true, false) => "Captures only",
            (false, true) => "Environment only",
            _ => "No exports enabled",
        };

    /// <summary>The newest acknowledged record in the bounded window; older acknowledgements are not claimed.</summary>
    private CameraAgentDeliveryRecord? LastAcknowledged => _records?
        .Where(static item => item.Record is { Status: ArtifactOutboxStatus.Acknowledged, AcknowledgedUtc: not null })
        .MaxBy(static item => item.Record.AcknowledgedUtc);

    /// <summary>
    /// One row per outbound lane. A section whose freshness is <c>unknown</c> has never been read,
    /// so its counts are shown as unknown rather than as the summary's zero defaults.
    /// </summary>
    private IEnumerable<LaneView> Lanes
    {
        get
        {
            if (_view is null)
            {
                yield break;
            }
            var summary = _view.Summary;
            var artifacts = summary.ArtifactOutbox.Value;
            yield return Lane("Capture artifacts", "Policy-selected capture exports", summary.ArtifactOutbox, artifacts.Availability,
                Waiting(artifacts), artifacts.RetryCount, artifacts.QuarantineCount, artifacts.OldestPendingUtc);
            var environmental = summary.EnvironmentalDelivery.Value;
            yield return Lane("Environment", "Independent observation history", summary.EnvironmentalDelivery, environmental.Availability,
                environmental.PendingCount, environmental.RetryCount, environmental.QuarantineCount, environmental.OldestPendingUtc);
            var heartbeat = summary.Heartbeat.Value;
            yield return Lane("Heartbeat", "Agent health and backlog summary", summary.Heartbeat, heartbeat.Availability,
                heartbeat.PendingCount, heartbeat.RetryCount, heartbeat.QuarantineCount, heartbeat.OldestPendingUtc);
            var evidence = summary.ExecutionEvidenceExport.Value;
            yield return Lane("Execution evidence", "Processing run records", summary.ExecutionEvidenceExport, evidence.Availability,
                evidence.PendingCount, evidence.RetryCount, evidence.QuarantineCount, evidence.OldestPendingUtc);
        }
    }

    private static LaneView Lane<T>(
        string name, string detail, OperationsSection<T> section, string availability,
        long waiting, long retrying, long quarantined, DateTimeOffset? oldestWaitingUtc)
        => IsRead(section)
            ? new LaneView(name, detail, availability, waiting, retrying, quarantined, oldestWaitingUtc)
            : new LaneView(name, detail, availability, null, null, null, null);

    /// <summary>False while a queue has never been read; its counts are then defaults, not observations.</summary>
    private static bool IsRead<T>(OperationsSection<T> section)
        => !string.Equals(section.Freshness, OperationsFreshness.Unknown, StringComparison.Ordinal);

    protected override Task OnInitializedAsync() => LoadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/DeliveryPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _dialogElement).ConfigureAwait(false);
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/DeliveryPage.razor.js").ConfigureAwait(false);
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
                _error = operations.Message ?? "Current delivery state is unavailable.";
            }

            var records = await OperatorService.GetDeliveryRecordsAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (records.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            _records = records.IsSuccess ? records.Value : null;
            _recordsError = records.IsSuccess ? null : records.Message ?? "The delivery outbox could not be read.";

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
                _policyError = system.Message ?? "The export policy is unavailable.";
                CloseDialog();
            }
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

    /// <summary>Items still to send: held minus quarantined, since quarantine waits for an operator, not a retry.</summary>
    private static long Waiting(OperationsQueueState queue)
        => Math.Max(0, queue.PendingCount - queue.QuarantineCount);

    private string Ago(DateTimeOffset value)
    {
        var age = (_view?.Summary.CapturedUtc ?? value) - value;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }
        return age.TotalSeconds < 90
            ? FormattableString.Invariant($"{age.TotalSeconds:F0}s ago")
            : age.TotalMinutes < 90
                ? FormattableString.Invariant($"{age.TotalMinutes:F0} min ago")
                : age.TotalHours < 48
                    ? FormattableString.Invariant($"{age.TotalHours:F0} h ago")
                    : FormattableString.Invariant($"{age.TotalDays:F0} d ago");
    }

    private static string CaptureLabel(ArtifactOutboxDeliveryRecord record) => record.CaptureSequence is { } sequence
        ? string.Create(CultureInfo.InvariantCulture, $"Capture #{sequence}")
        : "Capture not recorded";

    private static string Payload(ArtifactOutboxDeliveryRecord record)
    {
        var role = record.Role is { } value ? OperationsPage.SplitWords(value.ToString()) : "Artifact";
        return record.PayloadBytes is { } bytes ? $"{role} / {OperationsPage.FormatBytes(bytes)}" : role;
    }

    private string NextAction(ArtifactOutboxDeliveryRecord record) => record.Status switch
    {
        ArtifactOutboxStatus.Pending => $"Queued since {FormatSiteTime(record.CreatedUtc)}",
        ArtifactOutboxStatus.Leased => "Sending now",
        ArtifactOutboxStatus.Retry => $"Retry at {FormatSiteTime(record.NextAttemptUtc)}",
        ArtifactOutboxStatus.Acknowledged => "Complete",
        ArtifactOutboxStatus.Abandoned => "Abandoned by an operator",
        _ => "Held",
    };

    private static (string Text, string Chip) State(ArtifactOutboxStatus status) => status switch
    {
        ArtifactOutboxStatus.Pending => ("Waiting", "pending"),
        ArtifactOutboxStatus.Leased => ("Sending", "running"),
        ArtifactOutboxStatus.Retry => ("Retrying", "warning"),
        ArtifactOutboxStatus.Quarantined => ("Quarantined", "failure"),
        ArtifactOutboxStatus.Acknowledged => ("Acknowledged", "success"),
        ArtifactOutboxStatus.Abandoned => ("Abandoned", "skipped"),
        _ => (OperationsPage.SplitWords(status.ToString()), "pending"),
    };

    private static string AvailabilityChip(string availability) => availability switch
    {
        "Healthy" or "Available" or "Connected" => "success",
        "Degraded" or "Retrying" or "Backoff" => "warning",
        "Unhealthy" or "Unavailable" or "Failed" or "Rejected" => "failure",
        "Disabled" => "skipped",
        _ => "pending",
    };

    private static string Count(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? UnknownCountText;

    private static string EnabledText(bool enabled) => enabled ? "Enabled" : "Disabled";

    private static string Bandwidth(int bytesPerSecond) => bytesPerSecond > 0
        ? $"{OperationsPage.FormatBytes(bytesPerSecond)}/s"
        : "Unlimited";

    private static string Seconds(int seconds) => seconds < 120
        ? string.Create(CultureInfo.InvariantCulture, $"{seconds}s")
        : seconds % 60 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60.0:F1} min");

    private string FormatSiteTime(DateTimeOffset value) => SiteTime.Format(value);

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

    private sealed record LaneView(
        string Name,
        string Detail,
        string Availability,
        long? Waiting,
        long? Retrying,
        long? Quarantined,
        DateTimeOffset? OldestWaitingUtc)
    {
        public bool Read => Waiting is not null;
    }
}
