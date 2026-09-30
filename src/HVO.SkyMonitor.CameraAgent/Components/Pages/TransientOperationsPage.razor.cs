using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class TransientOperationsPage : ComponentBase, IAsyncDisposable
{
    private const string ProfileTriggerId = "transients-profile";
    private const string FocusFallbackId = "transients-refresh";
    private const string ContextPendingReason = "causal-context-pending";
    private const string CandidatePersistedReason = "causal-candidate-persisted";

    private readonly CancellationTokenSource _lifetime = new();
    private TransientOperationsView? _view;
    private string? _error;
    private bool _loading = true;
    private bool _disposed;
    private bool _dialogOpen;
    private bool _showDialog;
    private string? _focusTargetId;
    private IJSObjectReference? _module;
    private ElementReference _dialogElement;

    [Inject] internal ICameraAgentTransientOperationsUiService TransientService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    private string EventRunUnavailableReason => _view switch
    {
        null => "Detector status is not loaded.",
        { LatestEventRun: null } => "No recent capture produced a causal candidate.",
        _ => "The pipeline run for the latest candidate capture was not recorded.",
    };

    protected override Task OnInitializedAsync() => LoadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/TransientOperationsPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _dialogElement).ConfigureAwait(false);
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/TransientOperationsPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", target, FocusFallbackId).ConfigureAwait(false);
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            var result = await TransientService.GetOverviewAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _view = null;
                _dialogOpen = false;
                _showDialog = false;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (!result.IsSuccess || result.Value is null)
            {
                _view = null;
                _dialogOpen = false;
                _error = result.Message ?? "The detector lane is unavailable.";
            }
            else
            {
                _view = result.Value;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposed)
            {
                _loading = false;
            }
        }
    }

    private void OpenProfileDialog()
    {
        if (_view is null)
        {
            return;
        }
        _dialogOpen = true;
        _showDialog = true;
    }

    private void CloseDialog()
    {
        if (_dialogOpen)
        {
            _focusTargetId = ProfileTriggerId;
        }
        _dialogOpen = false;
        _showDialog = false;
    }

    /// <summary>
    /// Off and Central say plainly that this agent extracts nothing; only Edge and Hybrid report the worker, so a
    /// disabled worker is never shown as a fault.
    /// </summary>
    internal static (string Icon, string Title, string Detail) Banner(TransientOperationsView view)
    {
        switch (view.Mode)
        {
            case TransientOperatingMode.Off:
                return ("warning", "Transient detection is off",
                    "This agent runs no detector. Retained outcomes and candidates stay readable below.");
            case TransientOperatingMode.Central:
                return ("pending", "Central detection",
                    "This agent does not extract candidates. LogicHost runs detection on the captures this agent delivers.");
        }
        var lane = view.Mode == TransientOperatingMode.Hybrid
            ? "Candidates are handed to LogicHost for centered assessment."
            : "Candidates are assessed on this agent only.";
        var worker = view.Worker;
        return worker.Availability switch
        {
            TransientWorkerAvailability.Healthy => ("success", $"{view.Mode} detector healthy",
                $"Causal extraction runs independently of display processing. {lane}"),
            TransientWorkerAvailability.Starting => ("running", $"{view.Mode} detector starting",
                "The worker is starting and will pick up queued frames."),
            TransientWorkerAvailability.Degraded => ("warning", $"{view.Mode} detector degraded",
                worker.Reason == "quarantined-work"
                    ? "Some detector work is quarantined. Other captures continue."
                    : $"Reason: {worker.Reason}."),
            TransientWorkerAvailability.Unhealthy => ("failure", $"{view.Mode} detector unhealthy",
                worker.Reason == "worker-failure"
                    ? "The detector worker failed and is retrying. Captures continue and wait for the detector."
                    : $"Reason: {worker.Reason}."),
            _ => ("pending", $"{view.Mode} detector not running", $"Reason: {worker.Reason}."),
        };
    }

    private static string NoOutcomesText(TransientOperationsView view) => view.Mode switch
    {
        TransientOperatingMode.Off => "No capture has entered the detector lane. Detection is off.",
        TransientOperatingMode.Central => "No capture has entered the local detector lane. LogicHost detects centrally.",
        _ => "No capture has entered the detector lane yet.",
    };

    /// <summary>
    /// What the causal pass concluded for one capture. A capture without both prior frames is not a failure: it was
    /// not assessed, and says why.
    /// </summary>
    internal static (string Text, string? Detail, bool Failed) EdgeOutcome(TransientCaptureOutcome outcome)
    {
        switch (outcome.FrameState)
        {
            case null or "queued":
                return ("Waiting for detector", null, false);
            case "retry_wait":
                return ("Retrying", $"After attempt {Count(outcome.FrameAttempts)}", false);
            case "quarantined":
                return ("Detector failed", outcome.Reason, true);
            case "abandoned":
                return ("Abandoned", outcome.Reason, false);
        }
        if (outcome.CandidateCount > 0)
        {
            var text = outcome.CandidateCount == 1
                ? "1 causal candidate"
                : $"{Count(outcome.CandidateCount)} causal candidates";
            var detail = outcome.QuarantinedCandidates > 0
                ? $"{Count(outcome.QuarantinedCandidates)} quarantined"
                : outcome.CompletedCandidates == outcome.CandidateCount
                    ? "All complete"
                    : $"{Count(outcome.CompletedCandidates)} of {Count(outcome.CandidateCount)} complete";
            return (text, detail, outcome.QuarantinedCandidates > 0);
        }
        if (outcome.CausalSucceeded == true)
        {
            return ("No candidate", null, false);
        }
        return outcome.Reason == ContextPendingReason
            ? ("No prior context", "Frames N-2 and N-1 were not both available.", false)
            : ("Not assessed", outcome.Reason == CandidatePersistedReason ? null : outcome.Reason, false);
    }

    internal static (string Chip, string Text) LaneState(TransientCaptureOutcome outcome) => outcome.WorkState switch
    {
        "completed" => ("success", "Complete"),
        "candidate_persisted" => ("running", "Candidate in progress"),
        "quarantined" => ("failure", "Quarantined"),
        "abandoned" => ("neutral", "Abandoned"),
        _ when outcome.FrameState == "retry_wait" => ("warning", "Retrying"),
        _ => ("pending", "Pending"),
    };

    /// <summary>
    /// Candidates reach LogicHost only in Hybrid mode. Every other mode says so rather than showing an empty queue,
    /// and an unavailable delivery lane is never shown as "nothing to deliver".
    /// </summary>
    internal static (string Chip, string Icon, string Text, string? Detail) Handoff(TransientOperationsView view)
    {
        if (view.Mode != TransientOperatingMode.Hybrid)
        {
            return ("neutral", "pending", "Not exported", view.Mode switch
            {
                TransientOperatingMode.Edge => "Edge mode keeps candidates on this agent and assesses them locally.",
                TransientOperatingMode.Central => "This agent delivers captures, not candidates. LogicHost detects and assesses centrally.",
                _ => "Detection is off, so there are no candidates to hand off.",
            });
        }
        var delivery = view.Delivery;
        return delivery.Availability switch
        {
            TransientCandidateDeliveryAvailability.Healthy => ("success", "success", "Delivering", null),
            TransientCandidateDeliveryAvailability.Starting => ("running", "running", "Starting",
                "The delivery lane is starting. Queued candidates wait durably."),
            TransientCandidateDeliveryAvailability.Degraded => ("warning", "warning", "Degraded",
                delivery.AuthenticationBlockedCount > 0
                    ? "LogicHost refused this agent's sign-in. Candidates wait durably until it is accepted again."
                    : $"Delivery is degraded ({delivery.Reason}). Candidates wait durably and retry."),
            TransientCandidateDeliveryAvailability.Unhealthy => ("failure", "failure", "Unavailable",
                $"Delivery is unavailable ({delivery.Reason}). Candidates wait durably; none are lost."),
            _ => ("neutral", "pending", "Disabled",
                view.CentralIntegrationEnabled
                    ? $"Candidate delivery is not running ({delivery.Reason})."
                    : "Central integration is disabled, so candidates are not delivered."),
        };
    }

    private static string EventChip(string state) => state switch
    {
        "Validated" => "success",
        "NeedsReview" => "warning",
        "Rejected" => "neutral",
        _ => "pending",
    };

    private static string StageClass(string state) => state switch
    {
        "Available" => "available",
        "Pending" => "pending",
        _ => "absent",
    };

    private static string Words(string value) => OperationsPage.SplitWords(value.Replace('_', ' '));

    private static string Sequence(long sequence) => string.Create(CultureInfo.InvariantCulture, $"#{sequence}");

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Seconds(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:0.##} s");

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatAge(TimeSpan age)
    {
        var span = age < TimeSpan.Zero ? TimeSpan.Zero : age;
        return span.TotalSeconds < 90
            ? FormattableString.Invariant($"{Math.Round(span.TotalSeconds):0}s ago")
            : span.TotalMinutes < 90
                ? FormattableString.Invariant($"{Math.Round(span.TotalMinutes):0}m ago")
                : span.TotalHours < 48
                    ? FormattableString.Invariant($"{Math.Round(span.TotalHours):0}h ago")
                    : FormattableString.Invariant($"{Math.Round(span.TotalDays):0}d ago");
    }

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
}
