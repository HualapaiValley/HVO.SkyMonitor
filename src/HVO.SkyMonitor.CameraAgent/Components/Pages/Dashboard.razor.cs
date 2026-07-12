using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.AgentCore;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class Dashboard : ComponentBase, IDisposable
{
    private readonly List<CaptureTelemetrySample> _recentSamples = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CaptureTelemetrySnapshot? _snapshot;
    private PeriodicTimer? _refreshTimer;
    private CancellationTokenSource? _cts;
    private Task? _refreshLoop;
    private CameraModuleConfig? _configuration;
    private string? _rawPreviewImageSource;
    private string? _processedPreviewImageSource;
    private LatestFrameSnapshot? _rawFrame;
    private LatestFrameSnapshot? _processedFrame;
    private bool _isDisposed;
    private bool _isRefreshing;

    [Inject]
    public ICaptureTelemetryProvider TelemetryProvider { get; set; } = default!;

    [Inject]
    public ILogger<Dashboard> Logger { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public IOptions<CapturePreviewOptions> PreviewOptions { get; set; } = default!;

    [Inject]
    public ICameraAgentConfigurationAccessor ConfigurationAccessor { get; set; } = default!;

    [Inject]
    public ILatestFrameAccessor LatestFrameAccessor { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        _cts = new CancellationTokenSource();
        _configuration = await ConfigurationAccessor.WaitForConfigurationAsync(_cts.Token).ConfigureAwait(false);
        await RefreshInternalAsync(_cts.Token).ConfigureAwait(false);
        _refreshTimer = new PeriodicTimer(TimeSpan.FromSeconds(PreviewOptions.Value.PollingIntervalSeconds));
        _refreshLoop = RunRefreshLoopAsync(_cts.Token);
    }

    private async Task RunRefreshLoopAsync(CancellationToken token)
    {
        if (_refreshTimer is null)
        {
            return;
        }

        try
        {
            while (await _refreshTimer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await RefreshInternalAsync(token).ConfigureAwait(false);
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during disposal
        }
        finally
        {
            _refreshTimer.Dispose();
        }
    }

    private async Task RefreshInternalAsync(CancellationToken token)
    {
        await _refreshLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _isRefreshing = true;
            _snapshot = GetTelemetrySnapshot();
            _recentSamples.Clear();
            if (_snapshot.Samples.Count > 0)
            {
                for (var i = _snapshot.Samples.Count - 1; i >= 0 && _recentSamples.Count < 10; i--)
                {
                    _recentSamples.Add(_snapshot.Samples[i]);
                }
            }

            var timestamp = LatestSample?.StartedUtc.ToUnixTimeMilliseconds();
            LatestFrameAccessor.TryGetSnapshot(FrameArtifactRole.Raw, out _rawFrame);
            LatestFrameAccessor.TryGetSnapshot(FrameArtifactRole.Combined, out _processedFrame);
            _rawPreviewImageSource = timestamp is null ? null : FormattableString.Invariant($"/api/v1.0/frames/raw?ts={timestamp}");
            _processedPreviewImageSource = timestamp is null ? null : FormattableString.Invariant($"/api/v1.0/frames/processed?ts={timestamp}");
        }
        finally
        {
            _isRefreshing = false;
            _refreshLock.Release();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Telemetry provider callbacks may throw; logging and rethrowing keeps the refresh loop observable while preserving original behavior.")]
    private CaptureTelemetrySnapshot GetTelemetrySnapshot()
    {
        try
        {
            return TelemetryProvider.GetSnapshot();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to retrieve capture telemetry snapshot.");
            throw;
        }
    }

    public async Task RefreshAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await RefreshInternalAsync(_cts.Token).ConfigureAwait(false);
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    public CaptureTelemetryAggregate? Aggregate => _snapshot?.Aggregate;

    public CaptureTelemetrySample? LatestSample
    {
        get
        {
            if (_snapshot is null || _snapshot.Samples.Count == 0)
            {
                return null;
            }

            return _snapshot.Samples[^1];
        }
    }

    public IReadOnlyList<CaptureTelemetrySample> RecentSamples => _recentSamples;

    public bool IsRefreshing => _isRefreshing;

    public CameraRigConfig? Rig => _configuration?.Rig;

    public string? RawPreviewImageSource => _rawPreviewImageSource;

    public string? ProcessedPreviewImageSource => _processedPreviewImageSource;

    public LatestFrameSnapshot? RawFrame => _rawFrame;

    public LatestFrameSnapshot? ProcessedFrame => _processedFrame;

    public string StatusText
    {
        get
        {
            if (LatestSample is null)
            {
                return "Waiting for frames";
            }

            var delta = TimeProvider.GetUtcNow() - LatestSample.StartedUtc;
            return delta < TimeSpan.FromSeconds(5)
                ? "Live"
                : $"Updated {delta.TotalSeconds:F0}s ago";
        }
    }

    public string StatusCssClass
    {
        get
        {
            if (LatestSample is null)
            {
                return "status-pill--idle";
            }

            var delta = TimeProvider.GetUtcNow() - LatestSample.StartedUtc;
            if (delta < TimeSpan.FromSeconds(5))
            {
                return "status-pill--live";
            }

            if (delta < TimeSpan.FromSeconds(30))
            {
                return "status-pill--stale";
            }

            return "status-pill--idle";
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _refreshTimer?.Dispose();
        _refreshLock.Dispose();
    }

    private static string FormatMs(double? value)
        => value is null or double.NaN or double.PositiveInfinity or double.NegativeInfinity
            ? "—"
            : FormattableString.Invariant($"{value.Value:F0} ms");

    private static string FormatTimeSpan(TimeSpan? value)
        => value is null
            ? "—"
            : FormattableString.Invariant($"{value.Value.TotalMilliseconds:F0} ms");

    private static string FormatGain(double? gain)
        => gain is null ? "—" : gain.Value.ToString("F1", CultureInfo.InvariantCulture);

    private static string FormatPercent(double? value)
        => value is null
            ? "—"
            : FormattableString.Invariant($"{Math.Clamp(value.Value, 0, 1) * 100:F1}%");

    private static string FormatNumber(double? value)
        => value is null ? "—" : value.Value.ToString("F1", CultureInfo.InvariantCulture);

    private string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToLocalTime().ToString("MMM d HH:mm:ss", CultureInfo.InvariantCulture);

    private string FormatFrameTimestamp(LatestFrameSnapshot? frame)
        => frame is null ? "—" : FormatTimestamp(frame.TimestampUtc);

    private static string FormatFrameExposure(LatestFrameSnapshot? frame)
        => frame?.Metadata is null ? "—" : FormatMs(frame.Metadata.Exposure.TotalMilliseconds);

    private static string FormatFrameGain(LatestFrameSnapshot? frame)
        => frame?.Metadata is null ? "—" : FormatGain(frame.Metadata.Gain);

    private static string FormatFrameOffset(LatestFrameSnapshot? frame)
        => frame?.Metadata?.Offset is { } offset ? offset.ToString("F1", CultureInfo.InvariantCulture) : "—";

    private static string FormatStackCount(LatestFrameSnapshot? frame)
        => frame?.SourceArtifactCount?.ToString(CultureInfo.InvariantCulture)
            ?? GetFrameMetadataValue(frame, "stackCount")
            ?? "—";

    private static string FormatStackIntegration(LatestFrameSnapshot? frame)
    {
        var milliseconds = GetFrameMetadataValue(frame, "totalIntegrationMilliseconds");
        return double.TryParse(milliseconds, CultureInfo.InvariantCulture, out var parsed)
            ? FormatMs(parsed)
            : "—";
    }

    private static string? GetFrameMetadataValue(LatestFrameSnapshot? frame, string key)
        => frame?.Metadata?.Extra is { } values && values.TryGetValue(key, out var value) ? value : null;
}
