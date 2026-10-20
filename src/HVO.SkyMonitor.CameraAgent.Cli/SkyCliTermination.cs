using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>
/// The termination signals of <c>hvo-sky</c>: SIGINT (Ctrl+C), SIGTERM, and SIGHUP (the terminal closing, or an SSH
/// session dropping). The first cancels <see cref="Token"/> instead of ending the process, so the command reports what
/// it had done and exits 130 (<c>status --watch</c> ends normally). The process still ends: <see cref="Grace"/> after
/// the first signal it exits 130, and a second signal ends it at once. Nothing <c>hvo-sky</c> sends needs a follow-up
/// that a signal could cut short, unlike <c>hvo-roof</c>'s Stop.
/// </summary>
internal sealed class SkyCliTermination : IDisposable
{
    /// <summary>How long a command has to end after the first signal.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static readonly PosixSignal[] Signals = [PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP];

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Action<int> _exit;
    private readonly TimeProvider _time;
    private readonly List<PosixSignalRegistration> _registrations = [];
    private ITimer? _watchdog;
    private bool _disposed;

    /// <param name="exit">Ends the process with an exit code; tests give their own.</param>
    /// <param name="time">The clock of the grace period.</param>
    public SkyCliTermination(Action<int> exit, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(exit);
        _exit = exit;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Cancelled by the first signal.</summary>
    public CancellationToken Token => _cancel.Token;

    /// <summary>The signals of this process, handled here until <see cref="Dispose"/>.</summary>
    public static SkyCliTermination Register()
    {
        var termination = new SkyCliTermination(Environment.Exit);
        foreach (var signal in Signals)
        {
            try
            {
                termination._registrations.Add(PosixSignalRegistration.Create(signal, termination.OnSignal));
            }
            catch (PlatformNotSupportedException)
            {
                // A platform without this signal: the runtime's default applies to it.
            }
        }

        return termination;
    }

    /// <summary>Handles one signal: the first cancels <see cref="Token"/>, a later one ends the process.</summary>
    internal void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        bool first;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            first = _watchdog is null;
            if (first)
            {
                _watchdog = _time.CreateTimer(_ => OnWatchdog(), null, Grace, Timeout.InfiniteTimeSpan);
            }
        }

        if (!first)
        {
            _exit((int)SkyExitCode.Interrupted);
            return;
        }

        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The command had already ended.
        }
    }

    private void OnWatchdog()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _exit((int)SkyExitCode.Interrupted);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watchdog?.Dispose();
        }

        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _cancel.Dispose();
    }
}
