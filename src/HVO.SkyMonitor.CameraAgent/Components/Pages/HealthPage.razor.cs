using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class HealthPage : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private SystemHealthView? _health;
    private CameraAgentOperationsView? _view;
    private string? _error;
    private string? _summaryError;
    private bool _loading = true;
    private bool _disposed;

    [Inject] internal ICameraAgentSystemUiService SystemService { get; set; } = default!;
    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;

    private string ImageFreshness => _view?.Summary.CaptureTelemetry.Value.LatestCaptureStartedUtc is { } started
        ? FormatAge(started)
        : _view is null ? "Unknown" : "None yet";

    private string ImageFreshnessDetail
    {
        get
        {
            if (_view is null)
            {
                return "Capture telemetry is unavailable";
            }
            var summary = _view.Summary;
            if (string.Equals(summary.CaptureControl.Value.State, "Paused", StringComparison.Ordinal))
            {
                return "Capture is paused";
            }
            var telemetry = summary.CaptureTelemetry.Value;
            return telemetry.LatestCaptureStartedUtc is null
                ? "No capture since the agent started"
                : telemetry.SampleCount > 1 && double.IsFinite(telemetry.AverageIntervalMilliseconds)
                    ? $"Since the last capture; interval {FormatDuration(telemetry.AverageIntervalMilliseconds)}"
                    : "Since the last capture started";
        }
    }

    private string LanesDetail => _view is null
        ? "Lane state is unavailable"
        : _view.Summary.CaptureLanes.Value is { PendingCount: > 0 } lanes
            ? $"{Number(lanes.PendingCount)} pending"
            : "No backlog";

    private string CentralState
    {
        get
        {
            if (_health is { CentralIntegrationEnabled: false })
            {
                return "Not used";
            }
            return _view is null ? "Unknown" : OperationsPage.SplitWords(_view.Summary.Heartbeat.Value.Availability);
        }
    }

    private string CentralDetail
    {
        get
        {
            if (_health is { CentralIntegrationEnabled: false })
            {
                return "Standalone agent";
            }
            return _view?.Summary.Heartbeat.Value.LastAcknowledgedUtc is { } acknowledged
                ? $"Heartbeat acknowledged {FormatAge(acknowledged)} ago"
                : "No heartbeat acknowledged yet";
        }
    }

    private string ProcessingLatency
    {
        get
        {
            var timing = _view?.Summary.CaptureRuntime.Value.Timings
                .FirstOrDefault(static item => string.Equals(item.Segment, "Processing", StringComparison.Ordinal));
            return timing is { SampleCount: > 0 } ? FormatDuration(timing.P95Milliseconds) : "No samples";
        }
    }

    private string StorageUsed
    {
        get
        {
            if (_view is null)
            {
                return "Unknown";
            }
            var percents = _view.Summary.Storage.Value.Select(StoragePage.UsedPercent).ToArray();
            return percents.Length == 0 || percents.Any(static value => value is null)
                ? "Unknown"
                : $"{percents.Max()}% used";
        }
    }

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var health = await SystemService.GetHealthAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (health.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            if (health.IsSuccess && health.Value is not null)
            {
                _health = health.Value;
                _error = null;
            }
            else
            {
                // Keep the last completed checks visible and say why they are not current.
                _error = health.Message ?? "The health checks could not be run.";
            }

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
                _summaryError = null;
            }
            else
            {
                _view = null;
                _summaryError = operations.Message ?? "The operations summary is unavailable.";
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

    /// <summary>Drops the checks and facts read so far before leaving the page.</summary>
    private void DenyAccess()
    {
        _health = null;
        _view = null;
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    /// <summary>The worst state among the enabled checks of the given scopes, as chip text and class.</summary>
    private Chip ScopeChip(params string[] scopes)
    {
        var states = _health?.Checks
            .Where(check => scopes.Contains(check.Scope, StringComparer.Ordinal) && check.State != SystemCheckState.Disabled)
            .Select(static check => check.State)
            .ToArray() ?? [];
        return states.Length == 0
            ? new Chip("No checks", "neutral")
            : new Chip(StateText(states.Max()), StateChip(states.Max()));
    }

    private string ClockDriftTitle(SystemClockFact? clock) => clock?.MeasuredUtc is { } measured
        ? $"This agent's clock minus network time, measured {FormatAge(measured)} ago"
        : "This agent's clock minus network time";

    internal static string StateText(SystemCheckState state) => state switch
    {
        SystemCheckState.Healthy => "Healthy",
        SystemCheckState.Degraded => "Degraded",
        SystemCheckState.Unhealthy => "Unhealthy",
        _ => "Not used",
    };

    internal static string StateChip(SystemCheckState state) => state switch
    {
        SystemCheckState.Healthy => "success",
        SystemCheckState.Degraded => "warning",
        SystemCheckState.Unhealthy => "failure",
        _ => "neutral",
    };

    private static string OverallDetail(SystemHealthView health)
    {
        var enabled = health.Checks.Count(static check => check.State != SystemCheckState.Disabled);
        var failing = health.Checks.Count(static check => check.State is SystemCheckState.Degraded or SystemCheckState.Unhealthy);
        return failing == 0
            ? $"{enabled} of {health.Checks.Count} checks passing"
            : $"{failing} of {enabled} checks need attention";
    }

    private string FormatAge(DateTimeOffset value) => FormatAge(TimeProvider.GetUtcNow() - value);

    internal static string FormatAge(TimeSpan age)
    {
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

    internal static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }
        return value.TotalDays >= 1
            ? FormattableString.Invariant($"{(int)value.TotalDays}d {value.Hours}h")
            : value.TotalHours >= 1
                ? FormattableString.Invariant($"{(int)value.TotalHours}h {value.Minutes}m")
                : FormattableString.Invariant($"{value.Minutes}m {value.Seconds}s");
    }

    internal static string FormatDuration(double milliseconds) => !double.IsFinite(milliseconds)
        ? "Unavailable"
        : milliseconds < 1000
            ? FormattableString.Invariant($"{milliseconds:F0} ms")
            : milliseconds < 60_000
                ? FormattableString.Invariant($"{milliseconds / 1000:F1} s")
                : FormattableString.Invariant($"{milliseconds / 60_000:F1} min");

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:0.#}%");

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("MMM d, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string FormatClock(DateTimeOffset value)
        => value.UtcDateTime.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed record Chip(string Text, string Css);
}
