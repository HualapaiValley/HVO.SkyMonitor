using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed record OperatorOutboxActionRequest(
    OperatorOutboxItem Item,
    OutboxOperationAction Action,
    string TriggerId);

public sealed partial class OperationsPage : ComponentBase, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumQuarantineDisplay = 8;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private PeriodicTimer? _timer;
    private Task? _pollTask;
    private Task? _commandTask;
    private CameraAgentOperationsView? _view;
    private PendingOperatorCommand? _pendingCommand;
    private string? _pendingReasonCode;
    private string? _errorMessage;
    private string? _commandError;
    private string? _statusMessage;
    private bool _isInitialLoading = true;
    private bool _isSubmitting;
    private bool _showConfirmation;
    private IJSObjectReference? _module;
    private ElementReference _confirmationDialog;
    private int _disposeStarted;
    private int _refreshRequested;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        _lifetime = new CancellationTokenSource();
        await RequestRefreshAsync(_lifetime.Token);
        _timer = new PeriodicTimer(RefreshInterval, TimeProvider);
        _pollTask = PollAsync(_lifetime.Token);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_showConfirmation)
        {
            return;
        }
        _showConfirmation = false;
        _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/OperationsPage.razor.js");
        await _module.InvokeVoidAsync("showModal", _confirmationDialog);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RequestRefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RequestRefreshAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _refreshRequested, 1);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (Interlocked.Exchange(ref _refreshRequested, 0) != 0)
            {
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RefreshTimeout);
        try
        {
            var result = await OperatorService.GetOperationsAsync(timeout.Token).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                await InvokeAsync(HandleUnauthorized).ConfigureAwait(false);
                return;
            }
            await InvokeAsync(() =>
            {
                if (result.IsSuccess && result.Value is not null)
                {
                    _view = result.Value;
                    _errorMessage = null;
                }
                else
                {
                    _errorMessage = result.Message ?? "Current operations data is unavailable.";
                }
                _isInitialLoading = false;
                StateHasChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await InvokeAsync(() =>
            {
                _errorMessage = "The latest refresh exceeded its five-second deadline.";
                _isInitialLoading = false;
                StateHasChanged();
            }).ConfigureAwait(false);
        }
    }

    private async Task RefreshNowAsync()
    {
        if (_lifetime is null)
        {
            return;
        }
        await RequestRefreshAsync(_lifetime.Token).ConfigureAwait(false);
    }

    private void BeginPause() => BeginCapture(paused: true);

    private void BeginResume() => BeginCapture(paused: false);

    private void BeginCapture(bool paused)
    {
        if (_view is null || _isSubmitting)
        {
            return;
        }
        _pendingCommand = new PendingOperatorCommand(
            paused ? "Pause capture?" : "Resume capture?",
            paused
                ? "The current exposure will finish through durable ingress before another exposure is blocked. Processing and delivery continue."
                : "Capture admission will reopen using the current startup-validated configuration.",
            paused ? "Pause capture" : "Resume capture",
            paused ? "Paused" : "Running",
            _view.Summary.CaptureControl.Value.Version,
            paused,
            false,
            false,
            null,
            null,
            "capture-action",
            null,
            CreateIdempotencyKey());
        OpenConfirmation();
    }

    private void BeginOutboxAction(OperatorOutboxActionRequest request)
    {
        if (_isSubmitting)
        {
            return;
        }
        var abandon = request.Action == OutboxOperationAction.Abandon;
        _pendingCommand = new PendingOperatorCommand(
            string.Equals(request.Item.Kind, "Artifact", StringComparison.Ordinal)
                ? $"{request.Action} artifact item?"
                : $"{request.Action} environmental item?",
            abandon
                ? "Abandonment releases the durable delivery hold. The evidence will not be delivered by this outbox."
                : "Replay returns the item to bounded delivery using its existing durable evidence.",
            string.Equals(request.Item.Kind, "Artifact", StringComparison.Ordinal)
                ? $"{request.Action} artifact item"
                : $"{request.Action} environmental item",
            abandon ? "Abandoned" : "Pending",
            null,
            false,
            true,
            abandon,
            request.Item.Kind,
            request.Action,
            request.TriggerId,
            request.Action == OutboxOperationAction.Replay ? request.Item.ReplayToken : request.Item.AbandonToken,
            CreateIdempotencyKey());
        _pendingReasonCode = PendingReasonCodes[0].Code;
        OpenConfirmation();
    }

    private void OpenConfirmation()
    {
        _commandError = null;
        _statusMessage = null;
        _showConfirmation = true;
    }

    private async Task CancelConfirmationAsync()
    {
        var triggerId = _pendingCommand?.TriggerId;
        await CloseDialogAsync(triggerId);
        _pendingCommand = null;
        _pendingReasonCode = null;
        _commandError = null;
    }

    private Task ConfirmCommandAsync()
    {
        if (_pendingCommand is null || _isSubmitting || _lifetime is null)
        {
            return Task.CompletedTask;
        }
        var command = _pendingCommand;
        _isSubmitting = true;
        _commandError = null;
        _statusMessage = null;
        _commandTask = ExecuteCommandAsync(command, _lifetime.Token);
        return _commandTask;
    }

    private async Task ExecuteCommandAsync(PendingOperatorCommand command, CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                OperatorUiResult<OperatorCommandReceipt> result;
                if (command.IsOutbox)
                {
                    result = await OperatorService.ResolveOutboxAsync(
                        command.OutboxKind!,
                        command.OutboxAction!.Value,
                        command.ActionToken!,
                        _pendingReasonCode!,
                        command.IdempotencyKey,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await OperatorService.SetCapturePausedAsync(
                        command.PauseCapture,
                        command.ExpectedVersion!.Value,
                        command.IdempotencyKey,
                        cancellationToken).ConfigureAwait(false);
                }
                if (result.IsSuccess && result.Value is not null)
                {
                    var statusMessage = $"{result.Value.Action}: {result.Value.Disposition}. Current state: {result.Value.State}.";
                    await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                    await CloseDialogAsync(command.TriggerId).ConfigureAwait(false);
                    await InvokeAsync(() =>
                    {
                        _statusMessage = statusMessage;
                        _pendingCommand = null;
                        _pendingReasonCode = null;
                        _commandError = null;
                    }).ConfigureAwait(false);
                }
                else if (result.Kind == OperatorUiResultKind.Unauthorized)
                {
                    await InvokeAsync(HandleUnauthorized).ConfigureAwait(false);
                }
                else
                {
                    await InvokeAsync(() =>
                    {
                        _commandError = result.Message ?? "The command could not be completed.";
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await InvokeAsync(() => _isSubmitting = false).ConfigureAwait(false);
        }
    }

    private async Task CloseDialogAsync(string? triggerId)
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync(
                    "close",
                    _confirmationDialog,
                    triggerId,
                    "mainContent").ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private void HandleUnauthorized()
    {
        _view = null;
        _pendingCommand = null;
        _pendingReasonCode = null;
        _errorMessage = null;
        _commandError = null;
        _statusMessage = null;
        _showConfirmation = false;
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    private bool CanPause => _view?.Summary.CaptureControl.Value.State == "Running";
    private bool CanResume => _view?.Summary.CaptureControl.Value.State == "Paused";
    private bool IsDisconnected => _view?.Summary.Heartbeat.Value.Availability is not "Available";
    private bool HasPressure => _view is not null && (
        _view.Summary.Storage.Value.Any(static item => item.IsUnderPressure) ||
        _view.Summary.CaptureLanes.Value.Lanes.Any(static lane => lane.PressureLevel > 0));
    private bool IsEmpty => _view is not null &&
        _view.Summary.CaptureTelemetry.Value.SampleCount == 0 &&
        _view.Summary.RawIngress.Value.PendingCount == 0 &&
        _view.Summary.CaptureProcessing.Value.PendingCount == 0 &&
        _view.Summary.ArtifactOutbox.Value.PendingCount == 0;
    private bool HasStaleSection => _view is not null && new[]
    {
        _view.Summary.CaptureControl.Freshness,
        _view.Summary.RawIngress.Freshness,
        _view.Summary.CaptureLanes.Freshness,
        _view.Summary.CaptureProcessing.Freshness,
        _view.Summary.ArtifactOutbox.Freshness,
        _view.Summary.Storage.Freshness,
        _view.Summary.CaptureRuntime.Freshness,
        _view.Summary.Heartbeat.Freshness,
        _view.Summary.EnvironmentalDelivery.Freshness,
        _view.Summary.TransientWorker.Freshness,
        _view.Summary.CaptureTelemetry.Freshness,
        _view.Summary.Configuration.Freshness
    }.Any(static freshness => string.Equals(freshness, "stale", StringComparison.OrdinalIgnoreCase));

    private string OverallStateText => _isInitialLoading
        ? "Loading"
        : _view is null
            ? "Error"
            : _errorMessage is not null || HasStaleSection
                ? "Stale"
                : IsDisconnected
                    ? "Disconnected"
                    : HasPressure
                        ? "Pressure"
                        : "Current";

    private string OverallStateClass => OverallStateText switch
    {
        "Loading" => "state-chip--loading",
        "Error" => "state-chip--error",
        "Stale" => "state-chip--stale",
        "Disconnected" => "state-chip--disconnected",
        "Pressure" => "state-chip--pressure",
        _ => "state-chip--current"
    };

    private static IReadOnlyList<(string Code, string Label)> ReplayReasons { get; } =
    [
        ("configuration-corrected", "Configuration corrected"),
        ("evidence-restored", "Evidence restored"),
        ("upstream-recovered", "Upstream recovered")
    ];

    private static IReadOnlyList<(string Code, string Label)> AbandonReasons { get; } =
    [
        ("invalid-source", "Invalid source"),
        ("irrecoverable-evidence", "Irrecoverable evidence"),
        ("operator-approved-loss", "Operator-approved loss")
    ];

    private IReadOnlyList<(string Code, string Label)> PendingReasonCodes =>
        _pendingCommand?.IsAbandon == true ? AbandonReasons : ReplayReasons;

    internal static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "Unavailable";
        }
        var value = (double)bytes.Value;
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return FormattableString.Invariant($"{value:F1} {units[unit]}");
    }

    internal static string FormatTime(DateTimeOffset? value) => value is null
        ? "Never"
        : value.Value.ToLocalTime().ToString("MMM d, HH:mm:ss", CultureInfo.InvariantCulture);

    private string FormatAge(DateTimeOffset value)
    {
        var age = TimeProvider.GetUtcNow() - value;
        return age < TimeSpan.FromSeconds(2)
            ? "just now"
            : FormattableString.Invariant($"{Math.Max(0, age.TotalSeconds):F0}s ago");
    }

    private static string FormatMilliseconds(double? value) => value is null || !double.IsFinite(value.Value)
        ? "Unavailable"
        : FormattableString.Invariant($"{value.Value:F1} ms");

    private static string FormatNumber(double? value) => value is null || !double.IsFinite(value.Value)
        ? "Unavailable"
        : value.Value.ToString("F1", CultureInfo.InvariantCulture);

    internal static string SplitWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unavailable";
        }
        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index] is '_' or '-' ? ' ' : value[index];
            if (index > 0 && char.IsUpper(character) && char.IsLower(value[index - 1]))
            {
                builder.Append(' ');
            }
            builder.Append(character);
        }
        return char.ToUpperInvariant(builder[0]) + builder.ToString(1, builder.Length - 1);
    }

    private static string StateClass(string value) => value switch
    {
        "Running" or "Accepting" or "Available" or "Healthy" => "state-chip--current",
        "Paused" or "Initializing" => "state-chip--stale",
        "Unavailable" or "Unhealthy" => "state-chip--error",
        _ => "state-chip--pressure"
    };

    private static string PressureClass(int level) => level switch
    {
        >= 2 => "lane--critical",
        1 => "lane--warning",
        _ => string.Empty
    };

    private static string PressureText(int level) => level switch
    {
        >= 2 => "Critical pressure",
        1 => "Pressure warning",
        _ => "Normal pressure"
    };

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string CreateIdempotencyKey() =>
        $"ui-{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is null || Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _timer?.Dispose();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (_commandTask is not null)
        {
            try
            {
                await _commandTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _operationGate.WaitAsync().ConfigureAwait(false);
        _operationGate.Release();
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
        _operationGate.Dispose();
    }

    private sealed record PendingOperatorCommand(
        string Heading,
        string Description,
        string ActionLabel,
        string ExpectedState,
        long? ExpectedVersion,
        bool PauseCapture,
        bool IsOutbox,
        bool IsAbandon,
        string? OutboxKind,
        OutboxOperationAction? OutboxAction,
        string TriggerId,
        string? ActionToken,
        string IdempotencyKey);
}
