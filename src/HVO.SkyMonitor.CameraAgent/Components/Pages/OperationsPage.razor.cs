using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed record OperatorOutboxActionRequest(
    OperatorOutboxItem Item,
    OutboxOperationAction Action,
    string TriggerId);

/// <summary>
/// The Operations overview: the prototype <c>renderOverview()</c> composition bound to the durable
/// and runtime sources this CameraAgent reports. Facts no source reports (next-capture countdown,
/// sensor temperature, cloud estimate) are stated as not reported rather than filled in.
/// </summary>
public sealed partial class OperationsPage : SiteTimeComponent, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConfigurationRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(4);
    private const int MaximumRecentChanges = 4;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _toastLifetime;
    private PeriodicTimer? _timer;
    private Task? _pollTask;
    private Task? _commandTask;
    private Task? _toastTask;
    private CameraAgentOperationsView? _view;
    private CaptureScheduleOperatorState? _schedule;
    private long? _scheduleReadControlVersion;
    private CameraAgentPipelineOperatorState? _pipeline;
    private CalibrationUiStatus? _calibration;
    private NamedRigUiCatalog? _rigCatalog;
    private NamedRigInventory? _rigInventory;
    private DateTimeOffset? _configurationReadUtc;
    private CameraAgentProcessingExecutionSummary? _latestRun;
    private PendingOperatorCommand? _pendingCommand;
    private ToastMessage? _toast;
    private string? _errorMessage;
    private string? _commandError;
    private bool _isInitialLoading = true;
    private bool _isSubmitting;
    private bool _showConfirmation;
    private bool _unauthorized;
    private IJSObjectReference? _module;
    private ElementReference _confirmationDialog;
    private int _disposeStarted;
    private int _refreshRequested;
    private int _configurationRequested = 1;

    [CascadingParameter] internal OperationsLayout? Layout { get; set; }

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal ICameraAgentScheduleUiService ScheduleService { get; set; } = default!;
    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;
    [Inject] internal ICameraAgentCalibrationUiService CalibrationService { get; set; } = default!;
    [Inject] internal ICameraAgentNamedRigUiService RigService { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        _lifetime = new CancellationTokenSource();
        await RequestRefreshAsync(_lifetime.Token);
        if (_unauthorized)
        {
            return;
        }
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
            while (!_unauthorized && Interlocked.Exchange(ref _refreshRequested, 0) != 0)
            {
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Reads the operations summary and the latest live run on every tick. The configuration
    /// sources change only through their own pages, so they are read when the page opens, on a
    /// retry and then once a minute; the schedule decision is also re-read when capture control
    /// changes or its next transition has passed, so the admission fact follows pause, resume and
    /// the timetable.
    /// </summary>
    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RefreshTimeout);
        var readConfiguration = Interlocked.Exchange(ref _configurationRequested, 0) != 0 ||
            _configurationReadUtc is not { } configurationRead ||
            TimeProvider.GetUtcNow() - configurationRead >= ConfigurationRefreshInterval;
        try
        {
            var result = await OperatorService.GetOperationsAsync(timeout.Token).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                await InvokeAsync(HandleUnauthorized).ConfigureAwait(false);
                return;
            }
            var view = result.IsSuccess ? result.Value : null;
            var controlVersion = (view ?? _view)?.Summary.CaptureControl.Value.Version;
            var readSchedule = readConfiguration || _schedule is null ||
                _scheduleReadControlVersion != controlVersion ||
                _schedule.Decision.NextTransitionUtc <= TimeProvider.GetUtcNow();

            var schedule = readSchedule ? await ScheduleService.GetAsync(timeout.Token).ConfigureAwait(false) : null;
            var pipeline = readConfiguration ? await ScheduleService.GetPipelineAsync(timeout.Token).ConfigureAwait(false) : null;
            var calibration = readConfiguration ? await CalibrationService.GetStatusAsync(timeout.Token).ConfigureAwait(false) : null;
            var rigCatalog = readConfiguration ? await RigService.GetAsync(timeout.Token).ConfigureAwait(false) : null;
            var rigInventory = readConfiguration ? await RigService.GetInventoryAsync(timeout.Token).ConfigureAwait(false) : null;
            var executions = await GraphService.GetExecutionsAsync(1, timeout.Token).ConfigureAwait(false);
            if (schedule?.Kind == OperatorUiResultKind.Unauthorized ||
                pipeline?.Kind == OperatorUiResultKind.Unauthorized ||
                calibration?.Kind == OperatorUiResultKind.Unauthorized ||
                rigCatalog?.Kind == OperatorUiResultKind.Unauthorized ||
                rigInventory?.Kind == OperatorUiResultKind.Unauthorized ||
                executions.Kind == OperatorUiResultKind.Unauthorized)
            {
                await InvokeAsync(HandleUnauthorized).ConfigureAwait(false);
                return;
            }

            await InvokeAsync(() =>
            {
                if (view is not null)
                {
                    _view = view;
                    _errorMessage = null;
                }
                else
                {
                    _errorMessage = result.Message ?? "Current operations data is unavailable.";
                }
                if (schedule is not null)
                {
                    _schedule = schedule.IsSuccess ? schedule.Value : null;
                    _scheduleReadControlVersion = controlVersion;
                }
                if (readConfiguration)
                {
                    _pipeline = pipeline!.IsSuccess ? pipeline.Value : null;
                    _calibration = calibration!.IsSuccess ? calibration.Value : null;
                    _rigCatalog = rigCatalog!.IsSuccess ? rigCatalog.Value : null;
                    _rigInventory = rigInventory!.IsSuccess ? rigInventory.Value : null;
                    _configurationReadUtc = TimeProvider.GetUtcNow();
                }
                _latestRun = executions.IsSuccess && executions.Value?.Live is { Count: > 0 } live ? live[0] : null;
                _isInitialLoading = false;
                if (_view is not null)
                {
                    Layout?.Publish(_view, _errorMessage is not null);
                }
                StateHasChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (readConfiguration)
            {
                Interlocked.Exchange(ref _configurationRequested, 1);
            }
            await InvokeAsync(() =>
            {
                _errorMessage = "The latest refresh exceeded its five-second deadline.";
                _isInitialLoading = false;
                if (_view is not null)
                {
                    Layout?.Publish(_view, refreshFailed: true);
                }
                StateHasChanged();
            }).ConfigureAwait(false);
        }
    }

    internal async Task RefreshNowAsync()
    {
        if (_lifetime is null)
        {
            return;
        }
        Interlocked.Exchange(ref _configurationRequested, 1);
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
            paused,
            _view.Summary.CaptureControl.Value.Version,
            "capture-action",
            CreateIdempotencyKey());
        _commandError = null;
        _showConfirmation = true;
    }

    private async Task CancelConfirmationAsync()
    {
        if (_isSubmitting)
        {
            return;
        }
        await CloseDialogAsync(_pendingCommand?.TriggerId);
        _pendingCommand = null;
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
        _commandTask = ExecuteCommandAsync(command, _lifetime.Token);
        return _commandTask;
    }

    /// <summary>
    /// Submits the pending pause or resume. A failed attempt keeps the pending command, so
    /// confirming again retries with the same idempotency key and cannot apply the change twice.
    /// A version conflict is the exception: the same request can never succeed, so the page
    /// reads the current state and the operator reviews a new command against it.
    /// </summary>
    private async Task ExecuteCommandAsync(PendingOperatorCommand command, CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await OperatorService.SetCapturePausedAsync(
                    command.PauseCapture,
                    command.ExpectedVersion,
                    command.IdempotencyKey,
                    reason: null,
                    cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess && result.Value is not null)
                {
                    var receipt = result.Value;
                    await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                    if (_unauthorized)
                    {
                        return;
                    }
                    await CloseDialogAsync(command.TriggerId).ConfigureAwait(false);
                    await InvokeAsync(() =>
                    {
                        _pendingCommand = null;
                        _commandError = null;
                        ShowToast(new ToastMessage($"{receipt.Action}: {receipt.Disposition}", $"Current state: {receipt.State}."));
                    }).ConfigureAwait(false);
                }
                else if (result.Kind == OperatorUiResultKind.Unauthorized)
                {
                    await InvokeAsync(HandleUnauthorized).ConfigureAwait(false);
                }
                else if (result.Kind == OperatorUiResultKind.Conflict)
                {
                    await ReviewAfterConflictAsync(command, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await InvokeAsync(() =>
                    {
                        _commandError = $"{result.Message ?? "The command could not be completed."} Confirming again retries the same request.";
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

    /// <summary>
    /// Replaces a command whose expected state version went stale. When the fresh read shows the
    /// command still applies, the dialog stays open with a new expected version and idempotency
    /// key for the operator to confirm; otherwise it closes without sending anything again.
    /// </summary>
    private async Task ReviewAfterConflictAsync(PendingOperatorCommand command, CancellationToken cancellationToken)
    {
        await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        if (_unauthorized)
        {
            return;
        }
        OperationsCaptureControlState? current = null;
        var stillApplies = false;
        await InvokeAsync(() =>
        {
            current = _errorMessage is null ? _view?.Summary.CaptureControl.Value : null;
            stillApplies = current is not null && current.State == (command.PauseCapture ? "Running" : "Paused");
            if (stillApplies)
            {
                _pendingCommand = command with
                {
                    ExpectedVersion = current!.Version,
                    IdempotencyKey = CreateIdempotencyKey()
                };
                _commandError = $"Capture state changed while this was open. Capture is still {current.State}; review the new expected state version and confirm again.";
            }
        }).ConfigureAwait(false);
        if (stillApplies)
        {
            return;
        }
        await CloseDialogAsync(command.TriggerId).ConfigureAwait(false);
        await InvokeAsync(() =>
        {
            _pendingCommand = null;
            _commandError = null;
            ShowToast(new ToastMessage(
                "Capture state changed",
                current is null
                    ? "The current capture state could not be read, so the command was not sent again."
                    : $"Current state: {current.State}. The command was not sent again.",
                "warning"));
        }).ConfigureAwait(false);
    }

    private void ShowToast(ToastMessage toast)
    {
        _toastLifetime?.Cancel();
        _toastLifetime?.Dispose();
        _toastLifetime = _lifetime is null ? null : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _toast = toast;
        if (_toastLifetime is not null)
        {
            _toastTask = HideToastAsync(toast, _toastLifetime.Token);
        }
    }

    private async Task HideToastAsync(ToastMessage toast, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ToastDuration, TimeProvider, cancellationToken).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                if (ReferenceEquals(_toast, toast))
                {
                    _toast = null;
                    StateHasChanged();
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
        _unauthorized = true;
        _isInitialLoading = false;
        _view = null;
        _pendingCommand = null;
        _errorMessage = null;
        _commandError = null;
        _toast = null;
        _showConfirmation = false;
        StateHasChanged();
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    private string CaptureState => _view?.Summary.CaptureControl.Value.State ?? string.Empty;
    private bool CanPause => CaptureState == "Running";
    private bool CanResume => CaptureState == "Paused";
    private bool IsCentralIntegrationDisabled =>
        _view?.Summary.Configuration.Value.CentralIntegration == "Disabled";
    private bool HasStaleSection => HasFreshness("stale");
    private bool HasUnknownSection => HasFreshness("unknown");

    private bool HasFreshness(string freshness)
    {
        if (_view is null)
        {
            return false;
        }
        var summary = _view.Summary;
        return new[]
        {
            summary.CaptureControl.Freshness,
            summary.RawIngress.Freshness,
            summary.CaptureLanes.Freshness,
            summary.CaptureProcessing.Freshness,
            summary.ArtifactOutbox.Freshness,
            summary.Storage.Freshness,
            summary.CaptureRuntime.Freshness,
            summary.Heartbeat.Freshness,
            summary.EnvironmentalDelivery.Freshness,
            summary.TransientWorker.Freshness,
            summary.CaptureTelemetry.Freshness,
            summary.Configuration.Freshness
        }.Any(value => string.Equals(value, freshness, StringComparison.OrdinalIgnoreCase));
    }

    private string HeadingEyebrow =>
        $"{(string.IsNullOrWhiteSpace(_view?.DisplayName) ? "This camera" : _view.DisplayName)} / local authority";

    private string DeckClass => _errorMessage is not null || HasStaleSection || HasUnknownSection
        ? "pending"
        : CanPause ? string.Empty : "warning";

    private string CaptureHeadline => CaptureState switch
    {
        "Running" => "Capture loop running",
        "Paused" => "Capture paused",
        "PauseRequested" => "Capture pausing",
        "Initializing" => "Capture initializing",
        "Unavailable" => "Capture unavailable",
        _ => $"Capture state: {SplitWords(CaptureState)}"
    };

    private string CaptureDetail
    {
        get
        {
            var telemetry = _view!.Summary.CaptureTelemetry.Value;
            var detail = _errorMessage is not null || HasStaleSection
                ? "Last observed state; some facts need a fresh reading"
                : telemetry.SampleCount == 0
                    ? "No capture activity yet"
                    : string.Join(" / ", new[]
                    {
                        telemetry.LatestMode is null ? null : $"{SplitWords(telemetry.LatestMode)} mode",
                        _schedule?.Decision.SetpointProfileId is { } setpoint ? $"setpoint {setpoint}" : null
                    }.OfType<string>().DefaultIfEmpty("Capture recorded"));
            return CanPause || CanResume ? detail : $"{detail}; pause and resume are unavailable in this state";
        }
    }

    private string AdmissionText => _schedule?.Decision switch
    {
        null => "Unavailable",
        { Admitted: true, Reason: CaptureScheduleAdmissionReason.ForceOpenOverride } => "Open by override",
        { Admitted: true } => "Schedule open",
        { Reason: CaptureScheduleAdmissionReason.ManualPause } => "Paused by operator",
        { Reason: CaptureScheduleAdmissionReason.SafetyUnavailable } => "Safety hold",
        { Reason: CaptureScheduleAdmissionReason.Blackout } => "Blackout",
        { Reason: CaptureScheduleAdmissionReason.ForceClosedOverride } => "Closed by override",
        _ => "Schedule closed"
    };

    private NamedRigRevision? ActiveRig => _rigCatalog?.Revisions.FirstOrDefault(
        revision => revision.RevisionId == _rigCatalog.Selection.ActiveRevisionId);

    private NamedRigRevision? PendingRig => _rigCatalog?.Revisions.FirstOrDefault(
        revision => revision.RevisionId == _rigCatalog.Selection.PendingRevisionId);

    private string? RigName(NamedRigRevision revision) =>
        _rigInventory?.Profiles.FirstOrDefault(profile => profile.ProfileId == revision.ProfileId)?.DisplayName;

    private string ActiveProfileText => ActiveRig is { } rig && RigName(rig) is { } name
        ? name
        : _schedule is not null
            ? $"Capture profile r{_schedule.ActiveRevision.RevisionNumber}"
            : "Unavailable";

    private string RigTitle => _rigCatalog is null
        ? "Rig catalog unavailable"
        : ActiveRig is { } rig
            ? $"{RigName(rig) ?? "Named rig"} / r{rig.RevisionNumber}"
            : "No named rig active";

    private string RigDetail => _rigCatalog is null
        ? "Open Camera & rig to load the inventory"
        : PendingRig is { } pending
            ? $"Revision r{pending.RevisionNumber} pending restart"
            : _view?.Summary.Configuration.Value.ModuleType is { } module
                ? $"Module {SplitWords(module)}"
                : "No restart pending";

    private string ScheduleDetail => _schedule is null
        ? "Open Schedule to load the revision"
        : _schedule.PendingRevision is { } pending
            ? $"Revision r{pending.RevisionNumber} pending"
            : _schedule.Decision.NextTransitionUtc is { } next
                ? $"Next transition {SiteTime.Format(next)}"
                : "No transition scheduled";

    private string PipelineDetail => _pipeline is null
        ? "Open Pipeline to load the plan"
        : _pipeline.Pending is { } pending
            ? $"Revision r{pending.RevisionNumber} pending"
            : _pipeline.Active.Plan.EffectiveNodes.Count == 1
                ? "1 configured step"
                : $"{_pipeline.Active.Plan.EffectiveNodes.Count.ToString(CultureInfo.InvariantCulture)} configured steps";

    private string CalibrationTitle => _calibration is null
        ? "Calibration unavailable"
        : _calibration.ActiveBundle?.BundleId ?? "No active bundle";

    private string CalibrationDetail => _calibration is null
        ? "Open Calibration to load the library"
        : _calibration.ActiveBundle is { } bundle
            ? Count(bundle.MasterCount, "master frame", "master frames")
            : Count(_calibration.PublishedBundleCount, "published bundle", "published bundles");

    private string ProcessingLaneText
    {
        get
        {
            var queue = _view!.Summary.CaptureProcessing.Value;
            return queue.LeasedCount > 0 ? CountText(queue.LeasedCount, "active") : CountText(queue.PendingCount, "pending");
        }
    }

    private string DeliveryLaneText
    {
        get
        {
            var queue = _view!.Summary.ArtifactOutbox.Value;
            return IsCentralIntegrationDisabled
                ? "Disabled"
                : queue.RetryCount > 0 ? CountText(queue.RetryCount, "retry") : CountText(queue.PendingCount, "pending");
        }
    }

    /// <summary>The storage root nearest to full; only a successful probe with a capacity counts.</summary>
    private OperationsStorageState? FullestStorage => _view?.Summary.Storage.Value
        .Where(static storage => storage.ProbeSucceeded && storage.TotalBytes > 0)
        .OrderByDescending(static storage => UsedPercent(storage))
        .FirstOrDefault();

    private static int UsedPercent(OperationsStorageState storage) =>
        (int)Math.Clamp(Math.Round((storage.TotalBytes - storage.AvailableBytes) * 100d / storage.TotalBytes), 0, 100);

    private string BacklogText(OperationsQueueState queue) =>
        !string.Equals(queue.Availability, "Available", StringComparison.Ordinal)
            ? SplitWords(queue.Availability)
            : queue.OldestPendingUtc is { } oldest ? $"Oldest pending {FormatAge(oldest)}" : "No backlog";

    private string LastProcessingValue => _latestRun is { } run
        ? run.Duration is { } duration ? FormatSeconds(duration.TotalMilliseconds) : SplitWords(run.Status.ToString())
        : _view!.Summary.CaptureTelemetry.Value is { SampleCount: > 0 } telemetry
            ? FormatSeconds(telemetry.AverageProcessingMilliseconds)
            : "None";

    private string LastProcessingDetail => _latestRun is { } run
        ? run.Duration is null ? "Latest run has not finished" : $"{SplitWords(run.Status.ToString())} run"
        : _view!.Summary.CaptureTelemetry.Value.SampleCount > 0
            ? "Average of recent captures"
            : "No processing recorded";

    /// <summary>
    /// Recorded configuration history: capture-profile revisions (which carry the schedule and
    /// pipeline) and the last calibration activation, newest first.
    /// </summary>
    private IReadOnlyList<RecentChange> RecentChanges
    {
        get
        {
            var changes = new List<RecentChange>();
            if (_schedule is not null)
            {
                foreach (var revision in _schedule.History)
                {
                    var status = revision.RevisionId == _schedule.ActiveRevision.RevisionId
                        ? "; active"
                        : revision.RevisionId == _schedule.PendingRevision?.RevisionId ? "; pending" : string.Empty;
                    changes.Add(new RecentChange(
                        revision.CreatedUtc,
                        "Profile",
                        $"Revision r{revision.RevisionNumber} saved from {SourceText(revision.Source)}{status}."));
                }
            }
            if (_calibration?.LastActivation is { } activation)
            {
                changes.Add(new RecentChange(
                    activation.ActivatedUtc,
                    "Calibration",
                    $"{SplitWords(activation.CommandKind)}: bundle {activation.ToBundleId} active."));
            }
            return changes
                .OrderByDescending(static change => change.Utc)
                .Take(MaximumRecentChanges)
                .ToArray();
        }
    }

    private static string SourceText(string source) => source switch
    {
        "operator-draft" => "an operator draft",
        "file-draft" => "a configuration file draft",
        "file-bootstrap" => "the startup configuration file",
        "legacy-bootstrap" => "the legacy configuration",
        _ => $"the {source} source"
    };

    private string FormatChangeTime(DateTimeOffset value) => SiteTime.Format(value);

    private string FormatClock(DateTimeOffset value) => SiteTime.Format(value, "HH:mm:ss");

    private static string FormatIso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

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

    /// <summary>The compact elapsed time since <paramref name="value"/>: 4s, 12 min, 3 h, 2 d.</summary>
    private string FormatAge(DateTimeOffset value)
    {
        var age = TimeProvider.GetUtcNow() - value;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }
        return age.TotalSeconds < 90
            ? FormattableString.Invariant($"{age.TotalSeconds:F0}s")
            : age.TotalMinutes < 90
                ? FormattableString.Invariant($"{age.TotalMinutes:F0} min")
                : age.TotalHours < 48
                    ? FormattableString.Invariant($"{age.TotalHours:F0} h")
                    : FormattableString.Invariant($"{age.TotalDays:F0} d");
    }

    private static string FormatSeconds(double milliseconds) => !double.IsFinite(milliseconds)
        ? "Unavailable"
        : milliseconds < 1000
            ? FormattableString.Invariant($"{milliseconds:F0}ms")
            : milliseconds < 60_000
                ? FormattableString.Invariant($"{milliseconds / 1000:F2}s")
                : FormattableString.Invariant($"{milliseconds / 60_000:F1} min");

    private static string CountText(long count, string label) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {label}";

    private static string Count(long count, string singular, string plural) =>
        count == 1 ? $"1 {singular}" : CountText(count, plural);

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
        foreach (var task in new[] { _pollTask, _commandTask, _toastTask })
        {
            if (task is null)
            {
                continue;
            }
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _operationGate.WaitAsync().ConfigureAwait(false);
        _operationGate.Release();
        _toastLifetime?.Dispose();
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
        bool PauseCapture,
        long ExpectedVersion,
        string TriggerId,
        string IdempotencyKey);

    private sealed record ToastMessage(string Title, string Detail, string Tone = "success");

    private sealed record RecentChange(DateTimeOffset Utc, string Area, string Text);
}
