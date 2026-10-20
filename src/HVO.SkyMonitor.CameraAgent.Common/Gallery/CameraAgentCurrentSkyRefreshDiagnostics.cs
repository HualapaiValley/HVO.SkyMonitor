using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>Request-local timings for the Current Sky page and its nested presentation reads.</summary>
public sealed class CameraAgentCurrentSkyRefreshDiagnostics : IDisposable
{
    private const string TimingKey = "CameraAgent.CurrentSky.RefreshTiming";
    private readonly Lock _gate = new();
    private readonly Activity _activity = new("CameraAgent.CurrentSky.Refresh");
    private readonly Dictionary<string, double> _durations = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly long _started;
    private bool _deadlineLogged;
    private bool _disposed;

    public CameraAgentCurrentSkyRefreshDiagnostics(TimeProvider clock, ILogger logger)
    {
        _clock = clock;
        _logger = logger;
        _started = clock.GetTimestamp();
        _activity.SetCustomProperty(TimingKey, this);
        _activity.Start();
    }

    /// <summary>Measures a phase only when the calling flow belongs to a page refresh.</summary>
    public static IDisposable? Measure(TimeProvider clock, string phase)
        => Activity.Current?.GetCustomProperty(TimingKey) is CameraAgentCurrentSkyRefreshDiagnostics timing
            ? new Phase(timing, clock, phase) : null;

    public void LogDeadline(TimeSpan bound, bool firstRefresh)
    {
        Dictionary<string, double> durations;
        lock (_gate)
        {
            if (_deadlineLogged) return;
            _deadlineLogged = true;
            durations = new Dictionary<string, double>(_durations, StringComparer.Ordinal);
        }
        // The view read includes preview validation and layer availability. Compare its remaining time with the
        // nested phases, so the outer read cannot hide the operation that actually consumed the bound.
        var measuredView = durations.GetValueOrDefault("view-read");
        var measuredLayers = durations.GetValueOrDefault("layer-read");
        if (durations.TryGetValue("view-read", out var view))
            durations["view-read"] = Math.Max(0, view - durations.GetValueOrDefault("preview-validation") - durations.GetValueOrDefault("layer-availability"));
        durations["dispatcher"] = Math.Max(0, _clock.GetElapsedTime(_started).TotalMilliseconds -
            measuredView - measuredLayers);
        var slowest = durations.MaxBy(static pair => pair.Value).Key;
        _logger.LogInformation("Current Sky page refresh missed {BoundSeconds} s bound. FirstRefresh={FirstRefresh}; ElapsedMs={ElapsedMs}; SlowestPhase={SlowestPhase}.",
            bound.TotalSeconds, firstRefresh, _clock.GetElapsedTime(_started).TotalMilliseconds, slowest);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _activity.Dispose();
    }

    private sealed class Phase(CameraAgentCurrentSkyRefreshDiagnostics owner, TimeProvider clock, string phase) : IDisposable
    {
        private readonly long _started = clock.GetTimestamp();

        public void Dispose()
        {
            var elapsed = clock.GetElapsedTime(_started).TotalMilliseconds;
            lock (owner._gate)
            {
                if (owner._disposed) return;
                owner._durations[phase] = owner._durations.GetValueOrDefault(phase) + elapsed;
            }
            owner._logger.LogDebug("Current Sky page refresh phase {Phase} took {ElapsedMs} ms.", phase, elapsed);
        }
    }
}
