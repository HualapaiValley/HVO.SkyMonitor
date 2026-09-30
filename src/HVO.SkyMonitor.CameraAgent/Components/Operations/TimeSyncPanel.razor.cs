using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// System control's Time panel. It shows the latest clock measurement, runs an on-demand check, and edits the time
/// server list in the operator settings file. Setting the host clock and the GPS receiver are shown disabled with the
/// reason: the host's time service owns the clock, and no GPS reader exists yet.
/// </summary>
public sealed partial class TimeSyncPanel : ComponentBase, IDisposable
{
    internal const string SetClockUnavailableReason = "The host's time service sets the clock; CameraAgent measures it.";
    internal const string GpsUnavailableReason =
        "GPS time source is not supported yet. These are the settings a GPS reader will use; nothing is read from a device.";
    private const int ServersInputMaxLength = (TimeSyncSettings.MaximumServerLength + 2) * TimeSyncSettings.MaximumServers;
    private readonly CancellationTokenSource _lifetime = new();
    private TimeSyncView? _view;
    private PanelMessage? _message;
    private string _serversInput = string.Empty;
    private string? _error;
    private int _loadedGeneration = -1;
    private bool _checking;
    private bool _saving;
    private bool _disposed;

    [Inject] internal ICameraAgentTimeSyncUiService TimeSyncService { get; set; } = default!;

    /// <summary>Bumped by the page's Refresh, so the panel reads the clock state again with the rest of the page.</summary>
    [Parameter] public int Generation { get; set; }

    private bool Busy => _checking || _saving;

    private static string CheckIntervalText => ClockFormat.Unsigned(ClockSyncMonitor.OnDemandInterval);

    private string? CheckUnavailableReason => _view switch
    {
        null => "The clock state could not be read.",
        { Status: ClockSyncStatus.Disabled } => "Clock checking is turned off in this agent's settings.",
        { CanChange: false } => "Operations change rights are required to check the clock.",
        _ => null,
    };

    private string? ServersUnavailableReason => _view?.Setting switch
    {
        null => "The clock state could not be read.",
        _ when _view is { CanChange: false } => "Operations change rights are required to change the time servers.",
        { Version: null } => "This host loads no operator settings file, so the time servers are set in its own configuration instead.",
        { Unreadable: true } => "The operator settings file is not valid settings JSON. Correct it, then refresh.",
        { Overridden: true } => "Another configuration source sets the time servers, so a list saved here would have no effect.",
        _ => null,
    };

    protected override async Task OnParametersSetAsync()
    {
        if (Generation != _loadedGeneration)
        {
            _loadedGeneration = Generation;
            await LoadAsync();
        }
    }

    private async Task LoadAsync(bool resetInput = false)
    {
        var result = await TimeSyncService.GetAsync(_lifetime.Token);
        if (_disposed)
        {
            return;
        }
        if (result is { IsSuccess: true, Value: { } view })
        {
            // The input keeps what the operator typed until the stored list changes or a save completes.
            var settingChanged = resetInput || _view is null || !SameSetting(_view.Setting, view.Setting);
            _view = view;
            _error = null;
            if (settingChanged)
            {
                _serversInput = view.Setting.UsingDefault ? string.Empty : string.Join(", ", view.Setting.Servers);
            }
        }
        else
        {
            _view = null;
            _error = result.Message ?? "The clock state could not be read.";
        }
    }

    private async Task CheckNowAsync()
    {
        if (Busy || CheckUnavailableReason is not null)
        {
            return;
        }
        _checking = true;
        _message = null;
        try
        {
            var result = await TimeSyncService.CheckNowAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            _message = result is { IsSuccess: true, Value: { } check }
                ? PanelMessage.Info(CheckText(check))
                : PanelMessage.Failure(result.Message ?? "The clock check could not be run.");
            await LoadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task SaveServersAsync(bool useDefault)
    {
        if (Busy || _view is not { Setting.Version: { } version } || ServersUnavailableReason is not null)
        {
            return;
        }
        IReadOnlyList<string> servers = useDefault ? [] : TimeSyncSettings.SplitServers(_serversInput);
        if (!useDefault && servers.Count == 0)
        {
            _message = PanelMessage.Failure("Enter at least one time server, or choose Use default.");
            return;
        }
        if (CameraAgentTimeSyncUiService.ValidateServers(servers) is { } invalid)
        {
            _message = PanelMessage.Failure(invalid);
            return;
        }
        _saving = true;
        _message = null;
        try
        {
            var result = await TimeSyncService.SaveServersAsync(servers, version, _lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (result.IsSuccess)
            {
                _message = PanelMessage.Info(useDefault
                    ? "The default time server is in use again from the next check."
                    : "Time servers saved. The next check uses them.");
                await LoadAsync(resetInput: true);
            }
            else
            {
                _message = PanelMessage.Failure(result.Message ?? "The time server change could not be completed.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _saving = false;
        }
    }

    private static bool SameSetting(TimeSyncServersSetting left, TimeSyncServersSetting right)
        => string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
           left.UsingDefault == right.UsingDefault &&
           left.Servers.SequenceEqual(right.Servers, StringComparer.Ordinal);

    private static string CheckText(TimeSyncCheckView check) => check.Outcome switch
    {
        ClockCheckOutcome.Joined => "A check was already running; its result is shown.",
        ClockCheckOutcome.RateLimited => check.RetryAfterUtc is { } retry
            ? string.Create(CultureInfo.InvariantCulture,
                $"The clock was checked less than {CheckIntervalText} ago, so that result is shown. Check again after {retry:HH:mm:ss} UTC.")
            : $"The clock was checked less than {CheckIntervalText} ago, so that result is shown.",
        _ => "Clock checked.",
    };

    private static string StatusTone(ClockSyncStatus status) => status switch
    {
        ClockSyncStatus.InTolerance or ClockSyncStatus.HostSynchronized => "success",
        ClockSyncStatus.Drifting or ClockSyncStatus.Unsynchronized or ClockSyncStatus.Unverified => "warning",
        ClockSyncStatus.NotMeasured => "pending",
        _ => "neutral",
    };

    private static string OffsetText(TimeSyncView view) => view switch
    {
        { Selected.Offset: { } offset } => ClockFormat.Relative(offset.Negate()),
        { MeasuredUtc: null } => "Not measured",
        _ => "No server answered",
    };

    private static string AnsweredByText(TimeSyncView view) => view switch
    {
        { Selected: { Stratum: { } stratum } selected } => string.Create(
            CultureInfo.InvariantCulture, $"{selected.Server}, stratum {stratum}"),
        { Selected: { } selected } => selected.Server,
        { MeasuredUtc: null } => "—",
        _ => "None",
    };

    private static string MeasuredText(TimeSyncView view)
        => view.MeasuredUtc is { } measured ? string.Concat(ClockFormat.Unsigned(view.AgentUtc - measured), " ago") : "Not yet";

    private static string? MeasuredTitle(TimeSyncView view)
        => view.MeasuredUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Every(TimeSpan interval) => interval.TotalHours >= 1 && interval.Minutes == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)interval.TotalHours} h")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(interval.TotalMinutes)} min");

    private static string KernelText(KernelClockState kernel) => kernel switch
    {
        // The kernel reports its error in microseconds; below a millisecond the rounded figure would read "±0 ms".
        { Status: KernelClockStatus.Synchronized, EstimatedError: { } error } => error < TimeSpan.FromMilliseconds(1)
            ? "Synchronized, estimated error under 1 ms"
            : string.Concat("Synchronized, ±", ClockFormat.Unsigned(error), " estimated"),
        { Status: KernelClockStatus.Synchronized } => "Synchronized",
        { Status: KernelClockStatus.Unsynchronized } => "Not synchronized",
        _ => "Not reported by this host",
    };

    private static string FailureText(SntpFailure? failure) => failure switch
    {
        null => "Answered",
        SntpFailure.InvalidServer => "Not a valid server",
        SntpFailure.Unresolved => "Name not found",
        SntpFailure.Timeout => "No reply in time",
        SntpFailure.Unreachable => "Unreachable",
        SntpFailure.InvalidReply => "Invalid reply",
        SntpFailure.Refused => "Declined to serve",
        SntpFailure.Unsynchronized => "Server unsynchronized",
        _ => "Failed",
    };

    private static string ServersSummary(TimeSyncServersSetting setting) => setting switch
    {
        { UsingDefault: true } => $"None is configured, so the default {string.Join(", ", TimeSyncSettings.DefaultServers)} is used.",
        { Servers.Count: 0 } => "No configured entry is a valid server, so no server is queried.",
        _ => string.Empty,
    };

    private static string IgnoredText(int count) => count == 1
        ? "1 configured entry is ignored because it is not a valid server name or exceeds the limit."
        : string.Create(CultureInfo.InvariantCulture,
            $"{count} configured entries are ignored because they are not valid server names or exceed the limit.");

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed record PanelMessage(string Text, string Tone, bool IsError)
    {
        public static PanelMessage Info(string text) => new(text, "success", false);

        public static PanelMessage Failure(string text) => new(text, "failure", true);
    }
}
